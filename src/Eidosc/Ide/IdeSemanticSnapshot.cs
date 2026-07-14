using Eidosc.Symbols;
using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Eidosc.Diagnostic;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Borrow;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using IdeType = Eidosc.Types.Type;

namespace Eidosc.Ide;

public sealed class IdeSemanticSnapshot
{
    public bool Success { get; init; }
    public string InputFile { get; init; } = "";
    public string CompletedPhase { get; init; } = "";
    public string SnapshotConfidence { get; init; } = "Stale";
    public IdeSnapshotContract SnapshotContract { get; init; } = new();
    public bool TypeAnalysisIncomplete { get; init; }
    public string? TypeAnalysisIncompleteReason { get; init; }
    public int TypeErrorLimit { get; init; }
    public int SuppressedTypeDiagnosticCount { get; init; }
    public int SuppressedTypeConstraintCount { get; init; }
    public List<IdeDiagnosticEntry> Diagnostics { get; init; } = [];
    public List<IdeOutlineEntry> Outline { get; init; } = [];
    public List<IdeSymbolEntry> Symbols { get; init; } = [];
    public List<IdeOverloadGroupEntry> OverloadGroups { get; init; } = [];
    public List<IdeOccurrenceEntry> Occurrences { get; init; } = [];
    public List<IdeCompletionEntry> Completions { get; init; } = [];
    public List<IdeGeneratedDocumentEntry> GeneratedDocuments { get; init; } = [];
    public List<IdeBorrowCapabilityEntry> BorrowCapabilities { get; init; } = [];
    public List<IdeRecoveredNodeEntry> RecoveredNodes { get; init; } = [];
}

public sealed class IdeSnapshotContract
{
    public string Stage { get; init; } = "Stale";
    public List<string> GuaranteedFields { get; init; } = [];
    public bool HasDiagnostics { get; init; }
    public bool HasLexicalTokens { get; init; }
    public bool HasParsedAst { get; init; }
    public bool HasOutline { get; init; }
    public bool HasDeclarationSymbols { get; init; }
    public bool HasTermOccurrences { get; init; }
    public bool HasRecoveredNodes { get; init; }
    public bool HasCleanTypeInformation { get; init; }
    public bool AllowsTypeSensitiveRewrites { get; init; }
    public bool AllowsDefinitionFingerprints { get; init; }
}

public sealed class IdeRecoveredNodeEntry
{
    public string Kind { get; init; } = "";
    public string Reason { get; init; } = "";
    public IdeSpan? Span { get; init; }
}

public sealed class IdeOutlineEntry
{
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Detail { get; init; } = "";
    public int Depth { get; init; }
    public string? ContainerName { get; init; }
    public IdeSpan? Span { get; init; }
}

public sealed class IdeDiagnosticEntry
{
    public string Severity { get; init; } = "error";
    public string? Code { get; init; }
    public string Message { get; init; } = "";
    public IdeSpan? Span { get; init; }
    public Dictionary<string, string> Metadata { get; init; } = [];
    public List<IdeDiagnosticLabelEntry> Labels { get; init; } = [];
    public List<IdeDiagnosticRelatedEntry> Related { get; init; } = [];
    public List<IdeDiagnosticSuggestionEntry> Suggestions { get; init; } = [];
    public List<string> Notes { get; init; } = [];
    public List<string> Helps { get; init; } = [];
}

public sealed class IdeDiagnosticLabelEntry
{
    public string Message { get; init; } = "";
    public IdeSpan? Span { get; init; }
}

public sealed class IdeDiagnosticRelatedEntry
{
    public string Severity { get; init; } = "note";
    public string Message { get; init; } = "";
    public IdeSpan? Span { get; init; }
}

public sealed class IdeDiagnosticSuggestionEntry
{
    public string Kind { get; init; } = "";
    public string Message { get; init; } = "";
    public IdeSpan? Span { get; init; }
    public string? Replacement { get; init; }
    public string? HelpUrl { get; init; }
    public string Confidence { get; init; } = "high";
    public bool RequiresCleanTypes { get; init; }
    public int? OriginalSymbolId { get; init; }
    public string? OriginalFingerprint { get; init; }
    public string? OriginalFingerprintScope { get; init; }
}

public sealed class IdeSymbolEntry
{
    public int SymbolId { get; init; }
    public string Name { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Documentation { get; init; } = "";
    public string? GenericParameterText { get; init; }
    public string? TypeText { get; init; }
    public string? TypeConfidence { get; init; }
    public string? DefinitionFingerprint { get; init; }
    public string? DefinitionFingerprintScope { get; init; }
    public string? BindingMode { get; init; }
    public IdeSpan? VisibilitySpan { get; init; }
    public bool IsBuiltin { get; init; }
    public bool IsGenerated { get; init; }
    public IdeGeneratedOriginEntry? GeneratedOrigin { get; init; }
    public string? ExternalLibrary { get; init; }
    public IdeSpan? Span { get; init; }
}

public sealed class IdeGeneratedOriginEntry
{
    public string StableIdentity { get; init; } = "";
    public string GeneratorIdentity { get; init; } = "";
    public string TargetIdentity { get; init; } = "";
    public int GeneratorSymbolId { get; init; }
    public int TargetSymbolId { get; init; }
    public int AttributeOccurrenceIndex { get; init; }
    public int ExpansionOutputIndex { get; init; }
    public string CanonicalArgumentsHash { get; init; } = "";
    public int MetaSchemaVersion { get; init; }
    public IdeSpan? AttributeSpan { get; init; }
    public string VirtualDocumentPath { get; init; } = "";
}

public sealed class IdeGeneratedDocumentEntry
{
    public string Uri { get; init; } = "";
    public string LanguageId { get; init; } = "eidos";
    public string StableIdentity { get; init; } = "";
    public string GeneratorIdentity { get; init; } = "";
    public string TargetIdentity { get; init; } = "";
    public string Content { get; init; } = "";
}

public sealed class IdeOverloadGroupEntry
{
    public string GroupId { get; init; } = "";
    public string Name { get; init; } = "";
    public string ContainerName { get; init; } = "";
    public List<int> MemberSymbolIds { get; init; } = [];
    public List<IdeOverloadMemberEntry> Members { get; init; } = [];
    public IdeSpan? Span { get; init; }
}

public sealed class IdeOverloadMemberEntry
{
    public int SymbolId { get; init; }
    public string Name { get; init; } = "";
    public string? TypeText { get; init; }
    public string? TypeConfidence { get; init; }
    public string? DefinitionFingerprint { get; init; }
    public IdeSpan? Span { get; init; }
}

public sealed class IdeOccurrenceEntry
{
    public int SymbolId { get; init; }
    public string Role { get; init; } = "reference";
    public string Source { get; init; } = "";
    public IdeSpan Span { get; init; } = IdeSpan.Empty;
}

public sealed class IdeCompletionEntry
{
    public int? SymbolId { get; init; }
    public string? OverloadGroupId { get; init; }
    public int OverloadCount { get; init; }
    public List<int> OverloadMemberSymbolIds { get; init; } = [];
    public string Label { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Documentation { get; init; } = "";
    public string? GenericParameterText { get; init; }
    public string? TypeText { get; init; }
    public string? TypeConfidence { get; init; }
    public string? DefinitionFingerprint { get; init; }
    public string? DefinitionFingerprintScope { get; init; }
    public string? BindingMode { get; init; }
    public IdeSpan? Span { get; init; }
    public IdeSpan? VisibilitySpan { get; init; }
    public bool IsBuiltin { get; init; }
    public bool IsGenerated { get; init; }
    public IdeGeneratedOriginEntry? GeneratedOrigin { get; init; }
    public string SortText { get; init; } = "";
}

public sealed class IdeBorrowCapabilityEntry
{
    public string FunctionName { get; init; } = "";
    public bool HasSnapshot { get; init; }
    public bool IsEnforced { get; init; }
    public List<string> GlobalCapabilities { get; init; } = [];
    public List<IdeBorrowCapabilityProviderEntry> Providers { get; init; } = [];
}

public sealed class IdeBorrowCapabilityProviderEntry
{
    public string Provider { get; init; } = "";
    public List<string> Capabilities { get; init; } = [];
}

public sealed class IdeProofStateEntry
{
    public string ProofName { get; init; } = "";
    public string CheckStatus { get; init; } = "not-run";
    public string Goal { get; init; } = "";
    public string? Term { get; init; }
    public string? FailedGoal { get; init; }
    public string? FailedTerm { get; init; }
    public List<IdeProofLemmaCandidateEntry> LemmaCandidates { get; init; } = [];
    public string? SearchReport { get; init; }
    public List<IdeProofSearchCandidateEntry> SearchCandidates { get; init; } = [];
    public IdeSpan? Span { get; init; }
}

public sealed class IdeProofSearchCandidateEntry
{
    public string Kind { get; init; } = "";
    public string Status { get; init; } = "failure";
    public string? Replacement { get; init; }
}

public sealed class IdeProofLemmaCandidateEntry
{
    public string Name { get; init; } = "";
    public string Proposition { get; init; } = "";
    public List<string> Usages { get; init; } = [];
    public string? SourceFilePath { get; init; }
}

public sealed class IdeSpan
{
    public int StartLine { get; init; }
    public int StartCharacter { get; init; }
    public int EndLine { get; init; }
    public int EndCharacter { get; init; }
    public int Start { get; init; }
    public int Length { get; init; }
    public string? FilePath { get; init; }

    public static IdeSpan Empty { get; } = new();

    public bool Contains(int line, int character)
    {
        if (line < StartLine || line > EndLine)
        {
            return false;
        }

        if (line == StartLine && character < StartCharacter)
        {
            return false;
        }

        if (line == EndLine && character > EndCharacter)
        {
            return false;
        }

        return true;
    }

    public static bool TryFrom(SourceSpan span, out IdeSpan result)
    {
        if (!HasSpan(span))
        {
            result = Empty;
            return false;
        }

        var startLine = Math.Max(0, span.Location.Line);
        var startCharacter = Math.Max(0, span.Location.Column);
        var start = Math.Max(0, span.Location.Position);
        var length = Math.Max(0, span.Length);
        var end = start + length;

        result = new IdeSpan
        {
            StartLine = startLine,
            StartCharacter = startCharacter,
            EndLine = startLine,
            EndCharacter = startCharacter + length,
            Start = start,
            Length = length,
            FilePath = string.IsNullOrWhiteSpace(span.FilePath) ? null : span.FilePath
        };
        return true;
    }

    private static bool HasSpan(SourceSpan span)
    {
        return !string.IsNullOrWhiteSpace(span.FilePath) ||
               span.Length > 0 ||
               span.Location.Position > 0 ||
               span.Location.Line > 0 ||
               span.Location.Column > 0;
    }
}

public static partial class IdeSemanticSnapshotBuilder
{
    private const string DefinitionFingerprintVersion = "eidos-ide-fp-v2";
    private const string DefinitionFingerprintScope = "SessionOnly";

    private sealed class IdeSymbolMetadata
    {
        public IdeType? Type { get; init; }
        public string? TypeText { get; init; }
        public string? TypeConfidence { get; init; }
        public string? BindingMode { get; init; }
        public SourceSpan? VisibilitySpan { get; init; }
    }

    private static readonly string[] KeywordCompletions =
    [
        WellKnownStrings.Keywords.Func, WellKnownStrings.Keywords.Fn, WellKnownStrings.Keywords.Type, WellKnownStrings.Keywords.Trait, WellKnownStrings.Keywords.Forall, WellKnownStrings.Keywords.Refl, WellKnownStrings.Keywords.By, WellKnownStrings.Keywords.Cases, WellKnownStrings.Keywords.Effect, WellKnownStrings.Keywords.Handler, WellKnownStrings.Keywords.Module, WellKnownStrings.Keywords.Import, WellKnownStrings.Keywords.Export,
        WellKnownStrings.Keywords.Let, WellKnownStrings.Keywords.Mut, WellKnownStrings.Keywords.If, WellKnownStrings.Keywords.Then, WellKnownStrings.Keywords.Else, "while", WellKnownStrings.Keywords.Match, WellKnownStrings.Keywords.When, WellKnownStrings.Keywords.With, WellKnownStrings.Keywords.Need, "loop", WellKnownStrings.Keywords.Return, WellKnownStrings.Keywords.Unreachable,
        WellKnownStrings.AdditionalKeywords.Break, WellKnownStrings.AdditionalKeywords.Continue, WellKnownStrings.AdditionalKeywords.As, WellKnownStrings.Operators.Ref, WellKnownStrings.Operators.MRef, WellKnownStrings.Keywords.Resume, WellKnownStrings.Keywords.Requires, WellKnownStrings.Keywords.Self
    ];

    private static readonly string[] AttributeCompletions =
    [
        "@derive",
        "@impl",
        "@operator",
        "@ffi",
        "@borrow",
        "@transparent",
        "@cstruct"
    ];

    private static readonly string[] DeriveTraitCompletions =
    [
        "Eq",
        "Show",
        "Ord",
        "Hash",
        "Clone",
        "Copy"
    ];

    public static IdeSemanticSnapshot Build(CompilationResult result)
    {
        var symbolMetadata = result.Ast != null
            ? CollectSymbolMetadata(result.Ast)
            : new Dictionary<int, IdeSymbolMetadata>();
        var canBuildFingerprints = CanBuildDefinitionFingerprints(result);
        var symbols = BuildSymbols(result.SymbolTable, symbolMetadata, canBuildFingerprints);
        var overloadGroups = BuildOverloadGroups(result.SymbolTable, symbols);
        var modulePathSymbolIds = AddSyntheticModulePrefixSymbols(result.SymbolTable, symbols);
        var importedModuleAliases = result.Ast != null
            ? CollectImportedModuleAliases(result.Ast)
            : new Dictionary<string, SymbolId>();
        var symbolMap = symbols.ToDictionary(s => s.SymbolId, s => s);
        var occurrences = result.Ast != null
            ? CollectOccurrences(result.Ast, symbolMap, result.SymbolTable, result.SourceText, modulePathSymbolIds, importedModuleAliases)
            : [];
        var outline = result.Ast != null
            ? BuildOutline(result.Ast)
            : [];
        var recoveredNodes = result.Ast != null
            ? CollectRecoveredNodes(result.Ast)
            : [];
        var allowsTypeSensitiveRewrites = CanEmitTypeSensitiveSuggestions(result);
        var diagnostics = BuildDiagnostics(
            CollectDiagnostics(result, symbols),
            symbols,
            allowsTypeSensitiveRewrites);
        var completions = BuildCompletions(result.SymbolTable, symbols, overloadGroups);
        var generatedDocuments = symbols
            .Where(static symbol => symbol.IsGenerated && symbol.GeneratedOrigin != null)
            .OrderBy(static symbol => symbol.GeneratedOrigin!.StableIdentity, StringComparer.Ordinal)
            .Select(GeneratedDocumentRenderer.Create)
            .ToList();
        var borrowCapabilities = BuildBorrowCapabilities(result.BorrowCheckResult);

        var snapshotConfidence = DetermineSnapshotConfidence(result);
        var snapshotContract = BuildSnapshotContract(
            result,
            snapshotConfidence,
            symbols,
            occurrences,
            outline,
            recoveredNodes,
            completions,
            generatedDocuments);

        return new IdeSemanticSnapshot
        {
            Success = result.Success,
            InputFile = result.InputFile,
            CompletedPhase = result.CompletedPhase.ToString(),
            SnapshotConfidence = snapshotConfidence,
            SnapshotContract = snapshotContract,
            TypeAnalysisIncomplete = result.TypeAnalysisIncomplete,
            TypeAnalysisIncompleteReason = result.TypeAnalysisIncompleteReason,
            TypeErrorLimit = result.TypeErrorLimit,
            SuppressedTypeDiagnosticCount = result.SuppressedTypeDiagnosticCount,
            SuppressedTypeConstraintCount = result.SuppressedTypeConstraintCount,
            Diagnostics = diagnostics,
            Outline = outline,
            Symbols = symbols,
            OverloadGroups = overloadGroups,
            Occurrences = occurrences,
            Completions = completions,
            GeneratedDocuments = generatedDocuments,
            BorrowCapabilities = borrowCapabilities,
            RecoveredNodes = recoveredNodes
        };
    }

    private static IdeSnapshotContract BuildSnapshotContract(
        CompilationResult result,
        string snapshotConfidence,
        IReadOnlyList<IdeSymbolEntry> symbols,
        IReadOnlyList<IdeOccurrenceEntry> occurrences,
        IReadOnlyList<IdeOutlineEntry> outline,
        IReadOnlyList<IdeRecoveredNodeEntry> recoveredNodes,
        IReadOnlyList<IdeCompletionEntry> completions,
        IReadOnlyList<IdeGeneratedDocumentEntry> generatedDocuments)
    {
        var hasDiagnostics = result.Diagnostics.Count > 0;
        var hasLexicalTokens = result.Tokens?.Count > 0;
        var hasParsedAst = result.Ast != null;
        var hasOutline = outline.Count > 0;
        var hasRecoveredNodes = recoveredNodes.Count > 0;
        var hasDeclarationSymbols = symbols.Any(static symbol => !symbol.IsBuiltin && symbol.Span != null);
        var hasTermOccurrences = occurrences.Count > 0;
        var hasCleanTypeInformation = symbols.Any(static symbol =>
            string.Equals(symbol.TypeConfidence, "TypedClean", StringComparison.Ordinal));
        var allowsTypeSensitiveRewrites = CanEmitTypeSensitiveSuggestions(result);
        var allowsDefinitionFingerprints = CanBuildDefinitionFingerprints(result);
        var guaranteedFields = new List<string>
        {
            nameof(IdeSemanticSnapshot.Success),
            nameof(IdeSemanticSnapshot.InputFile),
            nameof(IdeSemanticSnapshot.CompletedPhase),
            nameof(IdeSemanticSnapshot.SnapshotConfidence),
            nameof(IdeSemanticSnapshot.SnapshotContract),
            nameof(IdeSemanticSnapshot.Diagnostics),
            nameof(IdeSemanticSnapshot.Completions)
        };

        if (hasLexicalTokens)
        {
            guaranteedFields.Add("LexicalTokens");
        }

        if (hasParsedAst)
        {
            guaranteedFields.Add("ParsedAst");
        }

        if (hasOutline)
        {
            guaranteedFields.Add(nameof(IdeSemanticSnapshot.Outline));
        }

        if (symbols.Count > 0)
        {
            guaranteedFields.Add(nameof(IdeSemanticSnapshot.Symbols));
        }

        if (generatedDocuments.Count > 0)
        {
            guaranteedFields.Add(nameof(IdeSemanticSnapshot.GeneratedDocuments));
        }

        if (hasTermOccurrences)
        {
            guaranteedFields.Add(nameof(IdeSemanticSnapshot.Occurrences));
        }

        if (hasRecoveredNodes)
        {
            guaranteedFields.Add(nameof(IdeSemanticSnapshot.RecoveredNodes));
        }

        if (hasCleanTypeInformation)
        {
            guaranteedFields.Add("CleanTypeInformation");
        }

        return new IdeSnapshotContract
        {
            Stage = snapshotConfidence,
            GuaranteedFields = guaranteedFields,
            HasDiagnostics = hasDiagnostics,
            HasLexicalTokens = hasLexicalTokens,
            HasParsedAst = hasParsedAst,
            HasOutline = hasOutline,
            HasDeclarationSymbols = hasDeclarationSymbols,
            HasTermOccurrences = hasTermOccurrences,
            HasRecoveredNodes = hasRecoveredNodes,
            HasCleanTypeInformation = hasCleanTypeInformation,
            AllowsTypeSensitiveRewrites = allowsTypeSensitiveRewrites,
            AllowsDefinitionFingerprints = allowsDefinitionFingerprints
        };
    }

    private static List<IdeOutlineEntry> BuildOutline(ModuleDecl root)
    {
        var entries = new List<IdeOutlineEntry>();
        AddModuleOutline(root, depth: 0, containerName: null, includeModuleEntry: root.Path.Count > 0, entries);
        return entries;
    }

    private static void AddModuleOutline(
        ModuleDecl module,
        int depth,
        string? containerName,
        bool includeModuleEntry,
        List<IdeOutlineEntry> entries)
    {
        var moduleName = FormatModulePath(module.Path);
        var childDepth = depth;
        if (includeModuleEntry && !string.IsNullOrWhiteSpace(moduleName))
        {
            entries.Add(CreateOutlineEntry(
                moduleName,
                WellKnownStrings.Keywords.Module,
                detail: "",
                depth,
                containerName,
                module.Span));
            childDepth++;
            containerName = moduleName;
        }

        foreach (var declaration in module.Declarations)
        {
            if (declaration is ModuleDecl childModule)
            {
                AddModuleOutline(
                    childModule,
                    childDepth,
                    containerName,
                    includeModuleEntry: true,
                    entries);
                continue;
            }

            if (TryCreateDeclarationOutlineEntry(declaration, childDepth, containerName, out var entry))
            {
                entries.Add(entry);
            }
        }
    }

    private static bool TryCreateDeclarationOutlineEntry(
        Declaration declaration,
        int depth,
        string? containerName,
        out IdeOutlineEntry entry)
    {
        entry = declaration switch
        {
            FuncDef func => CreateOutlineEntry(
                func.Name,
                DiagnosticMessages.IdeSymbolDetailFunction,
                "",
                depth,
                containerName,
                func.Span),
            FuncDecl func => CreateOutlineEntry(
                func.Name,
                DiagnosticMessages.IdeSymbolDetailFunction,
                DiagnosticMessages.IdeOutlineDetailDeclaration,
                depth,
                containerName,
                func.Span),
            LetDecl { Pattern: VarPattern { Name.Length: > 0 } varPattern } letDecl => CreateOutlineEntry(
                varPattern.Name,
                letDecl.IsMutable ? DiagnosticMessages.IdeSymbolDetailMutableVariable : DiagnosticMessages.IdeSymbolDetailValue,
                "",
                depth,
                containerName,
                letDecl.Span),
            AdtDef adt => CreateOutlineEntry(adt.Name, WellKnownStrings.Keywords.Type, "", depth, containerName, adt.Span),
            TraitDef trait => CreateOutlineEntry(trait.Name, WellKnownStrings.Keywords.Trait, "", depth, containerName, trait.Span),
            EffectDef ability => CreateOutlineEntry(ability.Name, WellKnownStrings.Keywords.Effect, "", depth, containerName, ability.Span),
            ImportDecl import => CreateOutlineEntry(
                GetImportOutlineName(import),
                DiagnosticMessages.IdeSymbolDetailImport,
                import.Kind.ToString(),
                depth,
                containerName,
                import.Span),
            OperatorDecl op => CreateOutlineEntry(
                op.OperatorSymbol,
                DiagnosticMessages.IdeSymbolDetailOperator,
                op.Fixity.ToString(),
                depth,
                containerName,
                op.Span),
            _ => new IdeOutlineEntry()
        };

        return !string.IsNullOrWhiteSpace(entry.Name);
    }

    private static IdeOutlineEntry CreateOutlineEntry(
        string name,
        string kind,
        string detail,
        int depth,
        string? containerName,
        SourceSpan span)
    {
        return new IdeOutlineEntry
        {
            Name = name,
            Kind = kind,
            Detail = detail,
            Depth = depth,
            ContainerName = containerName,
            Span = IdeSpan.TryFrom(span, out var ideSpan) ? ideSpan : null
        };
    }

    private static string GetImportOutlineName(ImportDecl import)
    {
        var modulePath = FormatImportModulePath(import);
        if (!string.IsNullOrWhiteSpace(import.Alias))
        {
            return $"{modulePath} as {import.Alias}";
        }

        return import.Kind switch
        {
            ImportKind.Wildcard => $"{modulePath}.*",
            ImportKind.Selective when import.SelectiveImports.Count > 0 =>
                $"{modulePath}.{{{string.Join(", ", import.SelectiveImports.Select(static item => item.Alias is { Length: > 0 } ? $"{item.Name} as {item.Alias}" : item.Name))}}}",
            _ => modulePath
        };
    }

    private static string FormatModulePath(IReadOnlyList<string> path) =>
        string.Join(WellKnownStrings.Separators.ModulePath, path.Where(static part => !string.IsNullOrWhiteSpace(part)));

    private static string FormatImportModulePath(ImportDecl import)
    {
        var modulePath = FormatModulePath(import.ModulePath);
        return string.IsNullOrWhiteSpace(import.PackageAlias)
            ? modulePath
            : $"{import.PackageAlias}{WellKnownStrings.Separators.Path}{modulePath}";
    }

    private static IReadOnlyList<Diagnostic.Diagnostic> CollectDiagnostics(
        CompilationResult result,
        IReadOnlyList<IdeSymbolEntry> symbols)
    {
        if (result.Ast is not ModuleDecl module)
        {
            return result.Diagnostics;
        }

        if (!CanEmitTypeSensitiveSuggestions(result))
        {
            return result.Diagnostics;
        }

        var styleDiagnostics = IdeStyleSuggestionBuilder.Build(
            module,
            result.SourceText,
            !string.IsNullOrWhiteSpace(result.InputFile) ? Path.GetFullPath(result.InputFile) : result.InputFile,
            result.SymbolTable,
            CreateRewritePreviewValidator(result, symbols));
        if (styleDiagnostics.Count == 0)
        {
            return result.Diagnostics;
        }

        return result.Diagnostics.Concat(styleDiagnostics).ToList();
    }

    private static List<IdeDiagnosticEntry> BuildDiagnostics(
        IReadOnlyList<Diagnostic.Diagnostic> diagnostics,
        IReadOnlyList<IdeSymbolEntry> symbols,
        bool allowsTypeSensitiveRewrites)
    {
        var result = new List<IdeDiagnosticEntry>(diagnostics.Count);
        var fingerprintsBySymbolId = symbols
            .Where(static symbol => !string.IsNullOrWhiteSpace(symbol.DefinitionFingerprint))
            .ToDictionary(static symbol => symbol.SymbolId, static symbol => symbol.DefinitionFingerprint);

        foreach (var diagnostic in diagnostics)
        {
            IdeSpan? span = null;
            var labels = new List<IdeDiagnosticLabelEntry>(diagnostic.Labels.Count);

            foreach (var label in diagnostic.Labels)
            {
                IdeSpan? labelSpan = null;
                if (IdeSpan.TryFrom(label.Span, out var convertedLabelSpan))
                {
                    labelSpan = convertedLabelSpan;
                    span ??= convertedLabelSpan;
                }

                labels.Add(new IdeDiagnosticLabelEntry
                {
                    Message = label.Message,
                    Span = labelSpan
                });
            }

            var related = new List<IdeDiagnosticRelatedEntry>(diagnostic.Related.Count);
            foreach (var relatedDiagnostic in diagnostic.Related)
            {
                IdeSpan? relatedSpan = null;
                foreach (var relatedLabel in relatedDiagnostic.Labels)
                {
                    if (IdeSpan.TryFrom(relatedLabel.Span, out var convertedRelatedSpan))
                    {
                        relatedSpan = convertedRelatedSpan;
                        break;
                    }
                }

                var relatedMessage = relatedDiagnostic.Message;
                var relatedLabelMessage = relatedDiagnostic.Labels.FirstOrDefault(static label => !string.IsNullOrWhiteSpace(label.Message))?.Message;
                if (!string.IsNullOrWhiteSpace(relatedLabelMessage))
                {
                    relatedMessage = DiagnosticMessages.RelatedDiagnosticMessageWithLabel(
                        relatedMessage,
                        relatedLabelMessage);
                }

                related.Add(new IdeDiagnosticRelatedEntry
                {
                    Severity = MapSeverity(relatedDiagnostic.Level),
                    Message = relatedMessage,
                    Span = relatedSpan
                });
            }

            var suggestions = new List<IdeDiagnosticSuggestionEntry>(diagnostic.Suggestions.Count);
            foreach (var suggestion in diagnostic.Suggestions)
            {
                if (suggestion.RequiresCleanTypes && !allowsTypeSensitiveRewrites)
                {
                    continue;
                }

                var originalFingerprint = suggestion.OriginalSymbolId is { } originalSymbolId &&
                                          fingerprintsBySymbolId.TryGetValue(originalSymbolId, out var fingerprint)
                    ? fingerprint
                    : null;
                suggestions.Add(new IdeDiagnosticSuggestionEntry
                {
                    Kind = suggestion.Kind.ToString(),
                    Message = suggestion.Message,
                    Span = suggestion.Span is { } suggestionSpan ? TryConvertIdeSpan(suggestionSpan) : null,
                    Replacement = suggestion.Replacement,
                    HelpUrl = suggestion.HelpUrl,
                    Confidence = suggestion.Confidence,
                    RequiresCleanTypes = suggestion.RequiresCleanTypes,
                    OriginalSymbolId = suggestion.OriginalSymbolId,
                    OriginalFingerprint = originalFingerprint,
                    OriginalFingerprintScope = string.IsNullOrWhiteSpace(originalFingerprint)
                        ? null
                        : DefinitionFingerprintScope
                });
            }

            result.Add(new IdeDiagnosticEntry
            {
                Severity = MapSeverity(diagnostic.Level),
                Code = diagnostic.Code,
                Message = diagnostic.Message,
                Span = span,
                Metadata = diagnostic.Metadata.Count > 0
                    ? new Dictionary<string, string>(diagnostic.Metadata, StringComparer.Ordinal)
                    : [],
                Labels = labels,
                Related = related,
                Suggestions = suggestions,
                Notes = [.. diagnostic.Notes],
                Helps = [.. diagnostic.Helps]
            });
        }

        return result;
    }

    private static bool CanEmitTypeSensitiveSuggestions(CompilationResult result)
    {
        return result.CompletedPhase >= CompilationPhase.Types &&
               !result.TypeAnalysisIncomplete &&
               result.Diagnostics.All(static diagnostic => diagnostic.Level != Diagnostic.DiagnosticLevel.Error);
    }

    private static bool CanBuildDefinitionFingerprints(CompilationResult result)
    {
        return result.CompletedPhase >= CompilationPhase.Types &&
               !result.TypeAnalysisIncomplete &&
               result.Diagnostics.All(static diagnostic => diagnostic.Level != Diagnostic.DiagnosticLevel.Error);
    }

    private static string DetermineSnapshotConfidence(CompilationResult result)
    {
        if (result.CompletedPhase >= CompilationPhase.Types)
        {
            return result.TypeAnalysisIncomplete ||
                   result.Diagnostics.Any(static diagnostic => diagnostic.Level == DiagnosticLevel.Error)
                ? "TypedRecovered"
                : "TypedClean";
        }

        if (result.CompletedPhase >= CompilationPhase.Namer)
        {
            return "ResolvedTerms";
        }

        if (result.CompletedPhase >= CompilationPhase.Parser)
        {
            return "Parsed";
        }

        if (result.CompletedPhase >= CompilationPhase.Lexer)
        {
            return "Lexed";
        }

        return "Stale";
    }

    private static Func<SourceSpan, string, int?, bool> CreateRewritePreviewValidator(
        CompilationResult result,
        IReadOnlyList<IdeSymbolEntry> symbols)
    {
        const int maxRewritePreviewsPerSnapshot = 8;
        var cache = new Dictionary<RewritePreviewKey, bool>();
        var previewCount = 0;
        var rewriteTargetsBySymbolId = symbols
            .Select(static symbol => (symbol.SymbolId, Identity: TryCreateRewriteTargetIdentity(symbol)))
            .Where(static item => item.Identity.HasValue)
            .ToDictionary(static item => item.SymbolId, static item => item.Identity.GetValueOrDefault());
        return (span, replacement, originalSymbolId) =>
        {
            var key = new RewritePreviewKey(span.Location.Position, span.Length, replacement);
            if (cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (previewCount >= maxRewritePreviewsPerSnapshot)
            {
                cache[key] = false;
                return false;
            }

            previewCount++;
            var originalTargetIdentity = originalSymbolId is { } id &&
                                         rewriteTargetsBySymbolId.TryGetValue(id, out var identity)
                ? identity
                : (RewriteTargetIdentity?)null;
            var resolution = TryApplyRewrite(result.SourceText, span, replacement, out var rewrittenSource)
                ? ResolveRewritePreviewCandidate(
                    result,
                    rewrittenSource,
                    span.Location.Position,
                    replacement.Length,
                    originalSymbolId.HasValue,
                    originalTargetIdentity)
                : CandidateResolution.NoMatch(
                    candidateCount: 1,
                    viableCandidateCount: 0,
                    CandidateResolutionChecks.AstPreview);
            var accepted = resolution.IsResolved;
            cache[key] = accepted;
            return accepted;
        };
    }

    private static bool TryApplyRewrite(
        string sourceText,
        SourceSpan span,
        string replacement,
        out string rewrittenSource)
    {
        rewrittenSource = "";
        var start = span.Location.Position;
        var length = span.Length;
        if (start < 0 || length <= 0 || start + length > sourceText.Length)
        {
            return false;
        }

        rewrittenSource = sourceText.Remove(start, length).Insert(start, replacement);
        return true;
    }

    private static CandidateResolution ResolveRewritePreviewCandidate(
        CompilationResult originalResult,
        string rewrittenSource,
        int replacementStart,
        int replacementLength,
        bool requiresTargetIdentity,
        RewriteTargetIdentity? originalTargetIdentity)
    {
        var checks = CandidateResolutionChecks.AstPreview |
                     CandidateResolutionChecks.NameCheck |
                     CandidateResolutionChecks.TypeCheck;
        if (requiresTargetIdentity)
        {
            checks |= CandidateResolutionChecks.Fingerprint;
        }

        var preview = new CompilationPipeline(rewrittenSource, new CompilationOptions
        {
            InputFile = originalResult.InputFile,
            ImportSearchRoots = [.. originalResult.ImportSearchRoots],
            NoImplicitPrelude = originalResult.NoImplicitPrelude,
            StopAtPhase = CompilationPhase.Types,
            UseColors = false,
            Verbose = false
        }).Run();

        if (!preview.Success ||
            preview.CompletedPhase < CompilationPhase.Types ||
            preview.Diagnostics.Any(static diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error))
        {
            return CandidateResolution.NoMatch(candidateCount: 1, viableCandidateCount: 0, checks);
        }

        if (!requiresTargetIdentity)
        {
            return CandidateResolution.ResolvedWithoutSymbol(candidateCount: 1, viableCandidateCount: 1, checks: checks);
        }

        if (!originalTargetIdentity.HasValue)
        {
            return CandidateResolution.NoMatch(candidateCount: 1, viableCandidateCount: 0, checks);
        }

        if (!TryFindRewriteTargetSymbolId(
            preview.Ast,
            replacementStart,
            replacementLength,
            out var replacementSymbolId))
        {
            return CandidateResolution.NoMatch(candidateCount: 1, viableCandidateCount: 0, checks);
        }

        var previewMetadata = preview.Ast != null
            ? CollectSymbolMetadata(preview.Ast)
            : new Dictionary<int, IdeSymbolMetadata>();
        var previewSymbols = BuildSymbols(preview.SymbolTable, previewMetadata, canBuildDefinitionFingerprints: true);
        var replacementSymbol = previewSymbols.FirstOrDefault(symbol => symbol.SymbolId == replacementSymbolId);
        if (replacementSymbol == null)
        {
            return CandidateResolution.NoMatch(candidateCount: 1, viableCandidateCount: 0, checks);
        }

        var previewTargetIdentity = TryCreateRewriteTargetIdentity(replacementSymbol);
        return previewTargetIdentity.HasValue &&
               previewTargetIdentity.Value == originalTargetIdentity.Value
            ? CandidateResolution.Resolved(
                new SymbolId(replacementSymbolId),
                candidateCount: 1,
                viableCandidateCount: 1,
                bestScore: 0,
                checks)
            : CandidateResolution.NoMatch(candidateCount: 1, viableCandidateCount: 0, checks);
    }

    private static bool TryFindRewriteTargetSymbolId(
        EidosAstNode? root,
        int replacementStart,
        int replacementLength,
        out int symbolId)
    {
        symbolId = -1;
        if (root == null)
        {
            return false;
        }

        EidosAstNode? best = null;
        Visit(root);

        return TryGetCallTargetSymbolId(best, out symbolId);

        void Visit(object? value)
        {
            if (value == null || value is string)
            {
                return;
            }

            if (value is EidosAstNode node)
            {
                if (ContainsRewriteSpan(node.Span, replacementStart, replacementLength) &&
                    node is CallExpr or MethodCallExpr)
                {
                    if (best == null || node.Span.Length < best.Span.Length)
                    {
                        best = node;
                    }
                }

                foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!property.CanRead ||
                        property.GetIndexParameters().Length > 0 ||
                        string.Equals(property.Name, nameof(EidosAstNode.InferredType), StringComparison.Ordinal))
                    {
                        continue;
                    }

                    Visit(property.GetValue(node));
                }

                return;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    Visit(item);
                }
            }
        }
    }

    private static bool ContainsRewriteSpan(SourceSpan span, int start, int length)
    {
        var spanStart = span.Location.Position;
        var spanEnd = spanStart + span.Length;
        var replacementEnd = start + length;
        return spanStart <= start && spanEnd >= replacementEnd;
    }

    private static bool TryGetCallTargetSymbolId(EidosAstNode? node, out int symbolId)
    {
        symbolId = -1;
        switch (node)
        {
            case CallExpr call:
                return TryGetCallableSymbolId(call.Function, out symbolId);
            case MethodCallExpr method when method.SymbolId.IsValid:
                symbolId = method.SymbolId.Value;
                return true;
            default:
                return false;
        }
    }

    private static bool TryGetCallableSymbolId(EidosAstNode? node, out int symbolId)
    {
        symbolId = -1;
        switch (node)
        {
            case IdentifierExpr identifier when identifier.SymbolId.IsValid:
                symbolId = identifier.SymbolId.Value;
                return true;
            case PathExpr path when path.SymbolId.IsValid:
                symbolId = path.SymbolId.Value;
                return true;
            case MethodCallExpr method when method.SymbolId.IsValid:
                symbolId = method.SymbolId.Value;
                return true;
            case CallExpr call:
                return TryGetCallableSymbolId(call.Function, out symbolId);
            default:
                return false;
        }
    }

    private readonly record struct RewritePreviewKey(int Start, int Length, string Replacement);

    private readonly record struct RewriteTargetIdentity(string DefinitionFingerprint);

    private static RewriteTargetIdentity? TryCreateRewriteTargetIdentity(IdeSymbolEntry symbol)
    {
        return string.IsNullOrWhiteSpace(symbol.DefinitionFingerprint)
            ? null
            : new RewriteTargetIdentity(symbol.DefinitionFingerprint);
    }

    private static List<IdeSymbolEntry> BuildSymbols(
        SymbolTable? symbolTable,
        IReadOnlyDictionary<int, IdeSymbolMetadata> symbolMetadata,
        bool canBuildDefinitionFingerprints)
    {
        if (symbolTable == null)
        {
            return [];
        }

        var symbols = new List<IdeSymbolEntry>(symbolTable.Symbols.Count);
        var modulePathsBySymbolId = BuildModulePathsBySymbolId(symbolTable);
        foreach (var symbol in symbolTable.Symbols.Values)
        {
            var isBuiltin = symbol.Kind == SymbolKind.Adt && IsBuiltinTypeName(symbol.Name);
            symbolMetadata.TryGetValue(symbol.Id.Value, out var metadata);
            IdeSpan? span = null;
            if (symbol.GeneratedOrigin is { } generatedOrigin)
            {
                span = CreateGeneratedVirtualSpan(generatedOrigin.VirtualDocumentPath);
            }
            else if (IdeSpan.TryFrom(symbol.Span, out var symbolSpan))
            {
                span = symbolSpan;
            }

            var definitionFingerprint = canBuildDefinitionFingerprints
                ? BuildDefinitionFingerprint(symbol, metadata, modulePathsBySymbolId)
                : null;
            symbols.Add(new IdeSymbolEntry
            {
                SymbolId = symbol.Id.Value,
                Name = symbol.Name,
                Kind = MapSymbolKind(symbol.Kind),
                Detail = BuildSymbolDetail(symbol),
                Documentation = BuildSymbolDocumentation(symbol, isBuiltin, metadata),
                GenericParameterText = BuildGenericParameterText(symbolTable, symbol),
                TypeText = symbol is CtorSymbol { SignatureText.Length: > 0 } ctorSymbol
                    ? ctorSymbol.SignatureText
                    : metadata?.TypeText,
                TypeConfidence = symbol is CtorSymbol { SignatureText: { Length: > 0 } }
                    ? "TypedClean"
                    : metadata?.TypeConfidence,
                DefinitionFingerprint = definitionFingerprint,
                DefinitionFingerprintScope = definitionFingerprint != null
                    ? DefinitionFingerprintScope
                    : null,
                BindingMode = metadata?.BindingMode,
                VisibilitySpan = symbol.IsModuleLevel
                    ? null
                    : TryConvertIdeSpan(metadata?.VisibilitySpan),
                IsBuiltin = isBuiltin,
                IsGenerated = symbol.GeneratedOrigin != null,
                GeneratedOrigin = CreateGeneratedOriginEntry(symbol.GeneratedOrigin),
                ExternalLibrary = symbol is FuncSymbol { IsExternal: true, ExternalLibrary: not null } func
                    ? func.ExternalLibrary
                    : null,
                Span = span
            });
        }

        symbols.Sort(static (left, right) =>
        {
            var nameCompare = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            if (nameCompare != 0)
            {
                return nameCompare;
            }

            return string.Compare(left.Kind, right.Kind, StringComparison.Ordinal);
        });

        return symbols;
    }

    private static IdeGeneratedOriginEntry? CreateGeneratedOriginEntry(GeneratedDeclarationOrigin? origin)
    {
        if (origin == null)
        {
            return null;
        }

        return new IdeGeneratedOriginEntry
        {
            StableIdentity = origin.StableIdentity,
            GeneratorIdentity = origin.GeneratorIdentity,
            TargetIdentity = origin.TargetIdentity,
            GeneratorSymbolId = origin.GeneratorSymbolId.Value,
            TargetSymbolId = origin.TargetSymbolId.Value,
            AttributeOccurrenceIndex = origin.AttributeOccurrenceIndex,
            ExpansionOutputIndex = origin.ExpansionOutputIndex,
            CanonicalArgumentsHash = origin.CanonicalArgumentsHash,
            MetaSchemaVersion = origin.MetaSchemaVersion,
            AttributeSpan = IdeSpan.TryFrom(origin.AttributeSpan, out var attributeSpan) ? attributeSpan : null,
            VirtualDocumentPath = origin.VirtualDocumentPath
        };
    }

    private static IdeSpan CreateGeneratedVirtualSpan(string virtualDocumentPath) => new()
    {
        StartLine = 0,
        StartCharacter = 0,
        EndLine = 0,
        EndCharacter = 1,
        Start = 0,
        Length = 1,
        FilePath = virtualDocumentPath
    };

    private static List<IdeOverloadGroupEntry> BuildOverloadGroups(
        SymbolTable? symbolTable,
        IReadOnlyList<IdeSymbolEntry> symbols)
    {
        if (symbolTable == null || symbols.Count == 0)
        {
            return [];
        }

        var modulePathsBySymbolId = BuildModulePathsBySymbolId(symbolTable);
        var symbolEntriesById = symbols.ToDictionary(static symbol => symbol.SymbolId);
        var groups = symbolTable.Symbols.Values
            .OfType<FuncSymbol>()
            .Where(IsOrdinaryOverloadCandidate)
            .Where(static function => !string.IsNullOrWhiteSpace(function.Name) && function.Id.IsValid)
            .GroupBy(
                function =>
                {
                    modulePathsBySymbolId.TryGetValue(function.Id.Value, out var modulePath);
                    return (Container: modulePath ?? "", function.Name);
                })
            .Where(static group => group.Select(function => function.Id).Distinct().Count() > 1)
            .OrderBy(static group => group.Key.Container, StringComparer.Ordinal)
            .ThenBy(static group => group.Key.Name, StringComparer.Ordinal)
            .ToList();

        var result = new List<IdeOverloadGroupEntry>(groups.Count);
        foreach (var group in groups)
        {
            var members = group
                .Select(function => symbolEntriesById.TryGetValue(function.Id.Value, out var entry)
                    ? new IdeOverloadMemberEntry
                    {
                        SymbolId = entry.SymbolId,
                        Name = entry.Name,
                        TypeText = entry.TypeText,
                        TypeConfidence = entry.TypeConfidence,
                        DefinitionFingerprint = entry.DefinitionFingerprint,
                        Span = entry.Span
                    }
                    : null)
                .Where(static member => member != null)
                .Select(static member => member!)
                .OrderBy(static member => member.TypeText ?? "", StringComparer.Ordinal)
                .ThenBy(static member => member.SymbolId)
                .ToList();
            if (members.Count <= 1)
            {
                continue;
            }

            result.Add(new IdeOverloadGroupEntry
            {
                GroupId = BuildOverloadGroupId(group.Key.Container, group.Key.Name),
                Name = group.Key.Name,
                ContainerName = group.Key.Container,
                MemberSymbolIds = members.Select(static member => member.SymbolId).ToList(),
                Members = members,
                Span = members.FirstOrDefault(static member => member.Span != null)?.Span
            });
        }

        return result;
    }

    private static bool IsOrdinaryOverloadCandidate(FuncSymbol function) =>
        !function.IsTraitImplementation &&
        function.OwnerTrait is not { IsValid: true };

    private static string BuildOverloadGroupId(string containerName, string name) =>
        string.IsNullOrWhiteSpace(containerName)
            ? $"overload::{name}"
            : $"overload::{containerName}::{name}";

    private static Dictionary<int, string> BuildModulePathsBySymbolId(SymbolTable symbolTable)
    {
        var result = new Dictionary<int, string>();
        foreach (var moduleId in symbolTable.Modules.ModulePaths.Values)
        {
            var module = symbolTable.Modules.GetModule(moduleId);
            if (module == null)
            {
                continue;
            }

            var modulePath = module.Path.Count == 0
                ? ""
                : string.Join(WellKnownStrings.Separators.ModulePath, module.Path);
            foreach (var memberId in module.Members)
            {
                result[memberId.Value] = modulePath;
            }
        }

        return result;
    }

    private static string? BuildDefinitionFingerprint(
        Symbol symbol,
        IdeSymbolMetadata? metadata,
        IReadOnlyDictionary<int, string> modulePathsBySymbolId)
    {
        if (!IsFingerprintableDefinition(symbol) ||
            metadata?.Type is not { } type)
        {
            return null;
        }

        modulePathsBySymbolId.TryGetValue(symbol.Id.Value, out var modulePath);
        var material = new StringBuilder();
        AppendFingerprintToken(material, DefinitionFingerprintVersion);
        AppendFingerprintToken(material, DefinitionFingerprintScope);
        AppendFingerprintToken(material, MapSymbolKind(symbol.Kind));
        AppendFingerprintToken(material, modulePath ?? "");
        AppendFingerprintToken(material, symbol.Name);
        AppendTypeFingerprintMaterial(material, type, []);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(material.ToString()));
        return $"{DefinitionFingerprintVersion}:{Convert.ToHexString(hash).ToLowerInvariant()}";
    }

    private static void AppendFingerprintToken(StringBuilder builder, string value)
    {
        builder.Append(value.Length);
        builder.Append(':');
        builder.Append(value);
        builder.Append('\0');
    }

    private static void AppendTypeFingerprintMaterial(
        StringBuilder builder,
        IdeType type,
        HashSet<int> activeTypeVariables)
    {
        switch (type)
        {
            case TyVar { Instance: not null } tyVar:
                if (!activeTypeVariables.Add(tyVar.Index))
                {
                    AppendFingerprintToken(builder, "tyvar-cycle");
                    AppendFingerprintToken(builder, tyVar.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    return;
                }

                AppendFingerprintToken(builder, "tyvar-instance");
                AppendTypeFingerprintMaterial(builder, tyVar.Instance, activeTypeVariables);
                activeTypeVariables.Remove(tyVar.Index);
                return;

            case TyVar tyVar:
                AppendFingerprintToken(builder, "tyvar");
                AppendFingerprintToken(builder, tyVar.Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
                return;

            case TyCon con:
                AppendFingerprintToken(builder, "tycon");
                AppendFingerprintToken(builder, con.ConstructorVarIndex?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "");
                AppendFingerprintToken(builder, con.Name);
                AppendFingerprintToken(builder, con.Args.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var arg in con.Args)
                {
                    AppendTypeFingerprintMaterial(builder, arg, activeTypeVariables);
                }

                return;

            case TyFun fun:
                AppendFingerprintToken(builder, "tyfun");
                AppendFingerprintToken(builder, fun.Params.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var param in fun.Params)
                {
                    AppendTypeFingerprintMaterial(builder, param, activeTypeVariables);
                }

                AppendTypeFingerprintMaterial(builder, fun.Result, activeTypeVariables);
                AppendTypeFingerprintMaterial(builder, fun.Effects, activeTypeVariables);
                return;

            case TyTuple tuple:
                AppendFingerprintToken(builder, "tytuple");
                AppendFingerprintToken(builder, tuple.Elements.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var element in tuple.Elements)
                {
                    AppendTypeFingerprintMaterial(builder, element, activeTypeVariables);
                }

                return;

            case TyRef reference:
                AppendFingerprintToken(builder, "tyref");
                AppendTypeFingerprintMaterial(builder, reference.Inner, activeTypeVariables);
                return;

            case TyMutRef reference:
                AppendFingerprintToken(builder, "tymutref");
                AppendTypeFingerprintMaterial(builder, reference.Inner, activeTypeVariables);
                return;

            case TyShared shared:
                AppendFingerprintToken(builder, "tyshared");
                AppendTypeFingerprintMaterial(builder, shared.Inner, activeTypeVariables);
                return;

            case EffectRow abilitySet:
                AppendFingerprintToken(builder, "abilityset");
                AppendFingerprintToken(builder, abilitySet.Effects.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var ability in abilitySet.Effects
                             .OrderBy(static ability => ability.Name, StringComparer.Ordinal)
                             .ThenBy(static ability => ability.TypeArgs.Count))
                {
                    AppendTypeFingerprintMaterial(builder, ability, activeTypeVariables);
                }

                foreach (var variable in abilitySet.Variables.OrderBy(static variable => variable.Id))
                {
                    AppendFingerprintToken(builder, variable.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
                }

                return;

            case EffectTag effect:
                AppendFingerprintToken(builder, "effect");
                AppendFingerprintToken(builder, effect.Name);
                AppendFingerprintToken(builder, effect.TypeArgs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
                foreach (var typeArg in effect.TypeArgs)
                {
                    AppendTypeFingerprintMaterial(builder, typeArg, activeTypeVariables);
                }

                return;

            default:
                AppendFingerprintToken(builder, type.GetType().Name);
                return;
        }
    }

    private static bool IsFingerprintableDefinition(Symbol symbol)
    {
        return symbol switch
        {
            FuncSymbol or
            VarSymbol { IsModuleLevel: true } or
            AdtSymbol or
            EffectSymbol or
            TraitSymbol or
            ImplSymbol => true,
            _ => false
        };
    }

    private static Dictionary<string, SymbolId> AddSyntheticModulePrefixSymbols(
        SymbolTable? symbolTable,
        List<IdeSymbolEntry> symbols)
    {
        var modulePathSymbolIds = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
        if (symbolTable == null)
        {
            return modulePathSymbolIds;
        }

        var modulePaths = symbolTable.Modules.ModulePaths.Values
            .Select(symbolTable.Modules.GetModule)
            .OfType<ModuleSymbol>()
            .Where(static module => module.Path.Count > 0)
            .Select(static module => module.Path)
            .DistinctBy(static path => string.Join(WellKnownStrings.Separators.ModulePath, path))
            .ToList();

        foreach (var path in modulePaths)
        {
            var moduleId = symbolTable.Modules.LookupModuleByPath(path);
            if (moduleId.HasValue)
            {
                modulePathSymbolIds[FormatModulePath(path)] = moduleId.Value;
            }
        }

        var nextSyntheticId = symbols.Count > 0 ? symbols.Max(static symbol => symbol.SymbolId) + 1 : 1;
        foreach (var path in modulePaths)
        {
            for (var length = 1; length < path.Count; length++)
            {
                var prefix = path.Take(length).ToList();
                var key = FormatModulePath(prefix);
                if (modulePathSymbolIds.ContainsKey(key))
                {
                    continue;
                }

                var symbolId = new SymbolId(nextSyntheticId++);
                modulePathSymbolIds[key] = symbolId;
                var displayName = prefix[^1];
                symbols.Add(new IdeSymbolEntry
                {
                    SymbolId = symbolId.Value,
                    Name = displayName,
                    Kind = WellKnownStrings.Keywords.Module,
                    Detail = DiagnosticMessages.IdeModulePathDetail,
                    Documentation = DiagnosticMessages.IdeModulePathDocumentation(
                        string.Join(WellKnownStrings.Separators.ModulePath, prefix))
                });
            }
        }

        symbols.Sort(static (left, right) =>
        {
            var nameCompare = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            return nameCompare != 0
                ? nameCompare
                : string.Compare(left.Kind, right.Kind, StringComparison.Ordinal);
        });

        return modulePathSymbolIds;
    }

    private static Dictionary<int, IdeSymbolMetadata> CollectSymbolMetadata(EidosAstNode root)
    {
        var metadata = new Dictionary<int, IdeSymbolMetadata>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        Visit(root, null);
        return metadata;

        void Visit(object? value, SourceSpan? visibilitySpan)
        {
            if (value == null || value is string)
            {
                return;
            }

            if (value is EidosAstNode node)
            {
                if (!visited.Add(node))
                {
                    return;
                }

                switch (node)
                {
                    case IfLetExpr ifLetExpr:
                        Visit(ifLetExpr.MatchedExpression, visibilitySpan);
                        Visit(ifLetExpr.Pattern, GetNodeSpanOrFallback(ifLetExpr.ThenBranch, ifLetExpr.Span));
                        Visit(ifLetExpr.ThenBranch, visibilitySpan);
                        Visit(ifLetExpr.ElseBranch, visibilitySpan);
                        return;
                    case WhileLetExpr whileLetExpr:
                        Visit(whileLetExpr.MatchedExpression, visibilitySpan);
                        Visit(whileLetExpr.Pattern, GetNodeSpanOrFallback(whileLetExpr.Body, whileLetExpr.Span));
                        Visit(whileLetExpr.Body, visibilitySpan);
                        return;
                    case ListComprehension listComprehension:
                        VisitListComprehension(listComprehension, visibilitySpan);
                        return;
                }

                var childVisibility = ResolveChildVisibility(node, visibilitySpan);
                AddNodeMetadata(node, visibilitySpan);
                foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    Visit(property.GetValue(node), childVisibility);
                }

                return;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    Visit(item, visibilitySpan);
                }
            }
        }

        void VisitListComprehension(ListComprehension listComprehension, SourceSpan? inheritedVisibility)
        {
            AddNodeMetadata(listComprehension, inheritedVisibility);

            Visit(listComprehension.Output, inheritedVisibility);
            var listVisibility = GetNodeSpanOrFallback(listComprehension, inheritedVisibility);
            foreach (var qualifier in listComprehension.Qualifiers)
            {
                if (qualifier.Kind == QualifierKind.Generator)
                {
                    Visit(qualifier.GeneratorExpression, inheritedVisibility);
                    Visit(qualifier.GeneratorPattern, listVisibility);
                }
                else
                {
                    Visit(qualifier.GuardExpression, inheritedVisibility);
                }

                Visit(qualifier, inheritedVisibility);
            }
        }

        void AddNodeMetadata(EidosAstNode node, SourceSpan? visibilitySpan)
        {
            if (!node.SymbolId.IsValid || !IsDefinitionNode(node))
            {
                return;
            }

            var isRecovered = node.IsRecovered;
            var typeText = !isRecovered &&
                           node.InferredType is IdeType inferredType &&
                           !ContainsErrorRecoveryType(inferredType)
                ? FormatTypeForIde(inferredType, node)
                : null;
            var bindingMode = node switch
            {
                VarPattern varPattern => varPattern.BindingMode.ToDisplayText(),
                AsPattern asPattern => asPattern.BindingMode.ToDisplayText(),
                _ => null
            };

            var nextMetadata = new IdeSymbolMetadata
            {
                Type = !isRecovered &&
                       node.InferredType is IdeType metadataType &&
                       !ContainsErrorRecoveryType(metadataType)
                    ? metadataType
                    : null,
                TypeText = string.IsNullOrWhiteSpace(typeText) ? null : typeText,
                TypeConfidence = string.IsNullOrWhiteSpace(typeText) ? null : "TypedClean",
                BindingMode = string.IsNullOrWhiteSpace(bindingMode) ||
                              string.Equals(bindingMode, "value", StringComparison.Ordinal)
                    ? null
                    : bindingMode,
                VisibilitySpan = visibilitySpan
            };

            if (!metadata.TryGetValue(node.SymbolId.Value, out var existingMetadata))
            {
                metadata[node.SymbolId.Value] = nextMetadata;
                return;
            }

            metadata[node.SymbolId.Value] = new IdeSymbolMetadata
            {
                Type = existingMetadata.Type ?? nextMetadata.Type,
                TypeText = existingMetadata.TypeText ?? nextMetadata.TypeText,
                TypeConfidence = existingMetadata.TypeConfidence ?? nextMetadata.TypeConfidence,
                BindingMode = existingMetadata.BindingMode ?? nextMetadata.BindingMode,
                VisibilitySpan = SelectBroaderVisibility(existingMetadata.VisibilitySpan, nextMetadata.VisibilitySpan)
            };
        }
    }

    private static SourceSpan? SelectBroaderVisibility(SourceSpan? left, SourceSpan? right)
    {
        if (left == null || right == null)
        {
            return null;
        }

        var leftValue = left.Value;
        var rightValue = right.Value;
        if (leftValue.Position <= rightValue.Position && leftValue.EndPosition >= rightValue.EndPosition)
        {
            return leftValue;
        }

        if (rightValue.Position <= leftValue.Position && rightValue.EndPosition >= leftValue.EndPosition)
        {
            return rightValue;
        }

        return leftValue;
    }

    private static List<IdeRecoveredNodeEntry> CollectRecoveredNodes(EidosAstNode root)
    {
        var entries = new List<IdeRecoveredNodeEntry>();
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        Visit(root);
        return entries;

        void Visit(object? value)
        {
            if (value == null || value is string)
            {
                return;
            }

            if (value is EidosAstNode node)
            {
                if (!visited.Add(node))
                {
                    return;
                }

                if (node.IsRecovered)
                {
                    entries.Add(new IdeRecoveredNodeEntry
                    {
                        Kind = node.GetType().Name,
                        Reason = node.RecoveryReason ?? AstRecoveryReasons.ParserRecoveredLiteral,
                        Span = TryConvertIdeSpan(node.Span)
                    });
                }

                foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    Visit(property.GetValue(node));
                }

                return;
            }

            if (value is IEnumerable enumerable)
            {
                foreach (var item in enumerable)
                {
                    Visit(item);
                }
            }
        }
    }

    private sealed class IdeTypeDisplayContext(EidosAstNode ownerNode, IdeType rootType)
    {
        private readonly Dictionary<int, string> _namesByVar = BuildTypeVariableNames(ownerNode, rootType);
        private readonly Dictionary<int, string> _fallbackNamesByVar = new();

        public string GetName(int index)
        {
            if (_namesByVar.TryGetValue(index, out var declaredName))
            {
                return declaredName;
            }

            if (!_fallbackNamesByVar.TryGetValue(index, out var generatedName))
            {
                generatedName = GenerateTypeVariableName(_fallbackNamesByVar.Count);
                _fallbackNamesByVar[index] = generatedName;
            }

            return generatedName;
        }

        private static Dictionary<int, string> BuildTypeVariableNames(EidosAstNode ownerNode, IdeType rootType)
        {
            var declaredTypeParams = GetDeclaredTypeParamNames(ownerNode);
            if (declaredTypeParams.Count == 0)
            {
                return [];
            }

            var freeVars = CollectFreeTypeVariablesInDisplayOrder(rootType);
            var result = new Dictionary<int, string>();
            var count = Math.Min(declaredTypeParams.Count, freeVars.Count);
            for (var i = 0; i < count; i++)
            {
                result[freeVars[i]] = declaredTypeParams[i];
            }

            return result;
        }

        private static List<string> GetDeclaredTypeParamNames(EidosAstNode ownerNode)
        {
            var typeParams = ownerNode switch
            {
                FuncDef funcDef => funcDef.TypeParams,
                FuncDecl funcDecl => funcDecl.TypeParams,
                ProofDecl proofDecl => proofDecl.TypeParams,
                AdtDef adtDef => adtDef.TypeParams,
                TraitDef traitDef => traitDef.TypeParams,
                EffectDef => [],
                _ => []
            };

            return typeParams
                .Where(static typeParam => typeParam.ParameterKind == GenericParameterKind.Type)
                .Select(static typeParam => typeParam.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        private static List<int> CollectFreeTypeVariablesInDisplayOrder(IdeType type)
        {
            var result = new List<int>();
            var seen = new HashSet<int>();

            Visit(type);
            return result;

            void Add(int index)
            {
                if (seen.Add(index))
                {
                    result.Add(index);
                }
            }

            void Visit(IdeType current)
            {
                switch (current)
                {
                    case TyVar { Instance: not null } var:
                        Visit(var.Instance);
                        break;
                    case TyVar var:
                        Add(var.Index);
                        break;
                    case TyCon con:
                        if (con.ConstructorVarIndex.HasValue)
                        {
                            Add(con.ConstructorVarIndex.Value);
                        }

                        foreach (var arg in con.Args)
                        {
                            Visit(arg);
                        }

                        break;
                    case TyFun fun:
                        foreach (var param in fun.Params)
                        {
                            Visit(param);
                        }

                        Visit(fun.Result);
                        break;
                    case TyTuple tuple:
                        foreach (var element in tuple.Elements)
                        {
                            Visit(element);
                        }

                        break;
                    case TyRef reference:
                        Visit(reference.Inner);
                        break;
                    case TyMutRef reference:
                        Visit(reference.Inner);
                        break;
                    case TyShared shared:
                        Visit(shared.Inner);
                        break;
                }
            }
        }

        private static string GenerateTypeVariableName(int ordinal)
        {
            const string names = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
            return ordinal < names.Length
                ? names[ordinal].ToString()
                : $"T{ordinal + 1}";
        }
    }

    private static string FormatTypeForIde(IdeType type, EidosAstNode ownerNode)
    {
        var context = new IdeTypeDisplayContext(ownerNode, type);
        return FormatTypeForIde(type, context);
    }

    private static bool ContainsErrorRecoveryType(IdeType type)
    {
        return type switch
        {
            TyVar { IsErrorRecovery: true } => true,
            TyVar { Instance: not null } var => ContainsErrorRecoveryType(var.Instance),
            TyVar => false,
            TyCon con => con.Args.Any(ContainsErrorRecoveryType),
            TyFun fun => fun.Params.Any(ContainsErrorRecoveryType) ||
                         ContainsErrorRecoveryType(fun.Result) ||
                         ContainsErrorRecoveryType(fun.Effects),
            TyTuple tuple => tuple.Elements.Any(ContainsErrorRecoveryType),
            TyRef reference => ContainsErrorRecoveryType(reference.Inner),
            TyMutRef reference => ContainsErrorRecoveryType(reference.Inner),
            TyShared shared => ContainsErrorRecoveryType(shared.Inner),
            EffectRow abilitySet => abilitySet.Effects.Any(ContainsErrorRecoveryType),
            EffectTag abilityType => abilityType.TypeArgs.Any(ContainsErrorRecoveryType),
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private static string FormatTypeForIde(IdeType type, IdeTypeDisplayContext context)
    {
        return type switch
        {
            TyVar { Instance: not null } var => FormatTypeForIde(var.Instance, context),
            TyVar var => context.GetName(var.Index),
            TyCon con => FormatTypeConstructorForIde(con, context),
            TyFun fun => FormatFunctionTypeForIde(fun, context),
            TyTuple tuple => $"({string.Join(", ", tuple.Elements.Select(element => FormatTypeForIde(element, context)))})",
            TyRef reference => $"Ref[{FormatTypeForIde(reference.Inner, context)}]",
            TyMutRef reference => $"MRef[{FormatTypeForIde(reference.Inner, context)}]",
            TyShared shared => $"Shared[{FormatTypeForIde(shared.Inner, context)}]",
            EffectRow abilitySet => $"{{{string.Join(", ", abilitySet.Effects)}}}",
            EffectTag abilityType => abilityType.ToString() ?? string.Empty,
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private static string FormatTypeConstructorForIde(TyCon con, IdeTypeDisplayContext context)
    {
        var constructorName = !string.IsNullOrWhiteSpace(con.Name)
            ? con.Name
            : con.ConstructorVarIndex.HasValue
                ? context.GetName(con.ConstructorVarIndex.Value)
                : "<type>";

        if (con.Args.Count == 0 && con.ValueArgs.Count == 0)
        {
            return constructorName;
        }

        var valueArguments = con.ValueArgs.ToDictionary(static argument => argument.ParameterIndex);
        var argumentCount = con.Args.Count + con.ValueArgs.Count;
        var typeArgumentIndex = 0;
        var arguments = new List<string>(argumentCount);
        for (var parameterIndex = 0; parameterIndex < argumentCount; parameterIndex++)
        {
            if (valueArguments.TryGetValue(parameterIndex, out var valueArgument))
            {
                arguments.Add(valueArgument.DisplayText);
            }
            else if (typeArgumentIndex < con.Args.Count)
            {
                arguments.Add(FormatTypeForIde(con.Args[typeArgumentIndex++], context));
            }
        }

        while (typeArgumentIndex < con.Args.Count)
        {
            arguments.Add(FormatTypeForIde(con.Args[typeArgumentIndex++], context));
        }

        var args = string.Join(", ", arguments);
        return $"{constructorName}<{args}>";
    }

    private static string FormatFunctionTypeForIde(TyFun fun, IdeTypeDisplayContext context)
    {
        var parameters = fun.Params.Count switch
        {
            0 => "()",
            1 => FormatFunctionParameterTypeForIde(fun.Params[0], context),
            _ => $"({string.Join(", ", fun.Params.Select(param => FormatTypeForIde(param, context)))})"
        };

        var resultText = FormatTypeForIde(fun.Result, context);
        return fun.Effects switch
        {
            null or { IsPure: true } => $"{parameters} -> {resultText}",
            var currentAbilities => $"{parameters} -> {resultText} need {FormatEffectRowForIde(currentAbilities, context)}"
        };
    }

    private static string FormatFunctionParameterTypeForIde(IdeType type, IdeTypeDisplayContext context)
    {
        var formatted = FormatTypeForIde(type, context);
        return type is TyFun ? $"({formatted})" : formatted;
    }

    private static string FormatEffectRowForIde(EffectRow abilities, IdeTypeDisplayContext context)
    {
        _ = context;
        return abilities.ToString();
    }
}
