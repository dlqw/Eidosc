using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Ide;

internal static class NamingStyleDiagnosticBuilder
{
    private const string LowerSnakeCaseCode = "S1101";
    private const string UpperCamelCaseCode = "S1102";
    private const string ScreamingSnakeCaseCode = "S1103";
    private const string FqnRedundancyCode = "S1104";
    private const string ModuleFileCode = "S1105";
    private const string WeakTypeNameCode = "S1106";

    private static readonly HashSet<string> WeakPublicTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Info", "Input", "Output", "Data", "Value", "Context", "Manager", "State", "Utils", "Misc", "Helper"
    };

    public static IReadOnlyList<Diagnostic.Diagnostic> Build(
        ModuleDecl module,
        string sourceText,
        string? sourceFilePath,
        SymbolTable? symbolTable)
    {
        if (string.IsNullOrEmpty(sourceText))
        {
            return [];
        }

        var diagnostics = new List<Diagnostic.Diagnostic>();
        var reported = new HashSet<string>(StringComparer.Ordinal);
        if (symbolTable != null)
        {
            AddSymbolDiagnostics(module, sourceText, sourceFilePath, symbolTable, diagnostics, reported);
        }

        AddAstOnlyDiagnostics(module, sourceText, sourceFilePath, symbolTable, diagnostics, reported);
        AddModuleFileDiagnostic(module, sourceFilePath, diagnostics, reported);
        return diagnostics;
    }

    private static void AddSymbolDiagnostics(
        ModuleDecl module,
        string sourceText,
        string? sourceFilePath,
        SymbolTable symbolTable,
        List<Diagnostic.Diagnostic> diagnostics,
        HashSet<string> reported)
    {
        foreach (var symbol in symbolTable.Symbols.Values.OrderBy(static symbol => symbol.Id.Value))
        {
            if (!IsRequestedSource(symbol.Span, sourceText, sourceFilePath) ||
                symbol.GeneratedOrigin != null ||
                !TryGetSymbolConvention(symbol, out var convention, out var category) ||
                !SourceIdentifierSpanFinder.TryFind(sourceText, symbol.Span, symbol.Name, out var nameSpan))
            {
                continue;
            }

            AddConventionDiagnostic(
                symbol.Name,
                convention,
                category,
                nameSpan,
                symbol.Id,
                diagnostics,
                reported);

            if (symbol.IsModuleLevel && symbol.IsPublic && IsFqnCheckedCategory(symbol))
            {
                AddFqnRedundancyDiagnostic(
                    module.Path,
                    symbol.Name,
                    convention,
                    category,
                    nameSpan,
                    symbol.Id,
                    diagnostics,
                    reported);
            }

            if (symbol.IsPublic &&
                symbol is AdtSymbol &&
                WeakPublicTypeNames.Contains(symbol.Name))
            {
                AddWeakTypeNameDiagnostic(symbol, nameSpan, diagnostics, reported);
            }
        }
    }

    private static bool TryGetSymbolConvention(
        Symbol symbol,
        out NamingConvention convention,
        out string category)
    {
        switch (symbol)
        {
            case TypeParamSymbol { ParameterKind: GenericParameterKind.Value }:
                convention = NamingConvention.ScreamingSnakeCase;
                category = "value const generic parameter";
                return true;
            case TypeParamSymbol { ParameterKind: GenericParameterKind.EffectRow }:
                convention = NamingConvention.UpperCamelCase;
                category = "effect-row parameter";
                return true;
            case TypeParamSymbol:
                convention = NamingConvention.UpperCamelCase;
                category = "type generic parameter";
                return true;
            case VarSymbol { IsModuleLevel: true, IsComptime: true }:
                convention = NamingConvention.ScreamingSnakeCase;
                category = "module-level comptime constant";
                return true;
            case VarSymbol { IsParameter: true }:
                convention = NamingConvention.LowerSnakeCase;
                category = "parameter";
                return true;
            case VarSymbol { IsPatternBound: true }:
                convention = NamingConvention.LowerSnakeCase;
                category = "pattern binding";
                return true;
            case VarSymbol:
                convention = NamingConvention.LowerSnakeCase;
                category = "value";
                return true;
            case FuncSymbol:
                convention = NamingConvention.LowerSnakeCase;
                category = "function or method";
                return true;
            case EffectSymbol:
                convention = NamingConvention.LowerSnakeCase;
                category = "effect or capability";
                return true;
            case TraitSymbol:
                convention = NamingConvention.UpperCamelCase;
                category = "trait";
                return true;
            case CtorSymbol:
                convention = NamingConvention.UpperCamelCase;
                category = "constructor";
                return true;
            case FieldSymbol:
                convention = NamingConvention.LowerSnakeCase;
                category = "field";
                return true;
            case AdtSymbol { IsTypeAlias: true }:
                convention = NamingConvention.UpperCamelCase;
                category = "type alias";
                return true;
            case AdtSymbol:
                convention = NamingConvention.UpperCamelCase;
                category = "type";
                return true;
            default:
                convention = default;
                category = string.Empty;
                return false;
        }
    }

    private static bool IsFqnCheckedCategory(Symbol symbol) =>
        symbol.Kind is SymbolKind.Adt or SymbolKind.TypeAlias or SymbolKind.Trait or
        SymbolKind.Function or SymbolKind.Constructor;

    private static void AddAstOnlyDiagnostics(
        ModuleDecl module,
        string sourceText,
        string? sourceFilePath,
        SymbolTable? symbolTable,
        List<Diagnostic.Diagnostic> diagnostics,
        HashSet<string> reported)
    {
        var visited = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
        Visit(module);

        void Visit(EidosAstNode node)
        {
            if (!visited.Add(node) || !IsRequestedSource(node.Span, sourceText, sourceFilePath))
            {
                return;
            }

            switch (node)
            {
                case ModuleDecl declaration when !ReferenceEquals(declaration, module):
                    AddModulePathDiagnostics(declaration);
                    break;
                case InstanceDecl instance:
                    AddNodeName(instance, instance.Name, NamingConvention.UpperCamelCase, "named instance");
                    break;
                case AssociatedTypeDecl associatedType:
                    AddNodeName(associatedType, associatedType.Name, NamingConvention.UpperCamelCase, "associated type");
                    break;
                case AssociatedConstDecl associatedConst:
                    AddNodeName(associatedConst, associatedConst.Name, NamingConvention.ScreamingSnakeCase, "associated constant");
                    break;
                case Field field when !node.SymbolId.IsValid:
                    AddNodeName(field, field.Name, NamingConvention.LowerSnakeCase, "field");
                    break;
                case TypeParam typeParam when !node.SymbolId.IsValid:
                    AddNodeName(
                        typeParam,
                        typeParam.Name,
                        typeParam.ParameterKind == GenericParameterKind.Value
                            ? NamingConvention.ScreamingSnakeCase
                            : NamingConvention.UpperCamelCase,
                        typeParam.ParameterKind switch
                        {
                            GenericParameterKind.Value => "value const generic parameter",
                            GenericParameterKind.EffectRow => "effect-row parameter",
                            _ => "type generic parameter"
                        });
                    break;
                case VarPattern pattern when !node.SymbolId.IsValid && !pattern.MayResolveToConstructor:
                    AddNodeName(pattern, pattern.Name, NamingConvention.LowerSnakeCase, "pattern binding");
                    break;
                case MetaInvocationSyntax invocation when invocation.GeneratorPath.Count > 0:
                    AddNodeName(
                        invocation,
                        invocation.GeneratorPath[^1],
                        NamingConvention.LowerSnakeCase,
                        "meta generator",
                        preferLast: true);
                    break;
                case ImportDecl import:
                    AddImportAliases(import);
                    break;
            }

            foreach (var child in EnumerateChildNodes(node))
            {
                Visit(child);
            }
        }

        void AddNodeName(
            EidosAstNode node,
            string name,
            NamingConvention convention,
            string category,
            bool preferLast = false)
        {
            if (!SourceIdentifierSpanFinder.TryFind(sourceText, node.Span, name, out var span, preferLast))
            {
                return;
            }

            AddConventionDiagnostic(name, convention, category, span, node.SymbolId, diagnostics, reported);
        }

        void AddModulePathDiagnostics(ModuleDecl declaration)
        {
            foreach (var segment in declaration.Path)
            {
                if (SourceIdentifierSpanFinder.TryFind(sourceText, declaration.Span, segment, out var span))
                {
                    AddConventionDiagnostic(
                        segment,
                        NamingConvention.UpperCamelCase,
                        "module path segment",
                        span,
                        declaration.SymbolId,
                        diagnostics,
                        reported);
                }
            }
        }

        void AddImportAliases(ImportDecl import)
        {
            if (!string.IsNullOrWhiteSpace(import.Alias) &&
                SourceIdentifierSpanFinder.TryFind(sourceText, import.Span, import.Alias, out var moduleAliasSpan, preferLast: true))
            {
                AddConventionDiagnostic(
                    import.Alias,
                    NamingConvention.UpperCamelCase,
                    "module alias",
                    moduleAliasSpan,
                    import.ResolvedModule,
                    diagnostics,
                    reported);
            }

            foreach (var imported in import.ResolvedSymbols.Where(static imported => imported.IsAliased))
            {
                var symbol = symbolTable?.GetSymbol(imported.SymbolId);
                if (!TryGetImportedAliasConvention(imported.Kind, symbol, out var convention, out var category) ||
                    !SourceIdentifierSpanFinder.TryFind(sourceText, import.Span, imported.Name, out var aliasSpan, preferLast: true))
                {
                    continue;
                }

                AddConventionDiagnostic(
                    imported.Name,
                    convention,
                    category,
                    aliasSpan,
                    imported.SymbolId,
                    diagnostics,
                    reported);
            }
        }
    }

    private static bool TryGetImportedAliasConvention(
        ResolutionKind resolutionKind,
        Symbol? symbol,
        out NamingConvention convention,
        out string category)
    {
        if (symbol != null && TryGetSymbolConvention(symbol, out convention, out _))
        {
            category = $"imported {resolutionKind.ToString().ToLowerInvariant()} alias";
            return true;
        }

        switch (resolutionKind)
        {
            case ResolutionKind.Type:
            case ResolutionKind.Constructor:
                convention = NamingConvention.UpperCamelCase;
                category = "imported type alias";
                return true;
            case ResolutionKind.Effect:
            case ResolutionKind.Value:
                convention = NamingConvention.LowerSnakeCase;
                category = $"imported {resolutionKind.ToString().ToLowerInvariant()} alias";
                return true;
            case ResolutionKind.Module:
                convention = NamingConvention.UpperCamelCase;
                category = "imported module alias";
                return true;
            default:
                convention = default;
                category = string.Empty;
                return false;
        }
    }

    private static void AddConventionDiagnostic(
        string name,
        NamingConvention convention,
        string category,
        SourceSpan span,
        SymbolId symbolId,
        List<Diagnostic.Diagnostic> diagnostics,
        HashSet<string> reported)
    {
        if (string.IsNullOrWhiteSpace(name) || name == "_" || name.StartsWith("__", StringComparison.Ordinal))
        {
            return;
        }

        var expected = Normalize(name, convention);
        if (string.Equals(name, expected, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(expected))
        {
            return;
        }

        var code = convention switch
        {
            NamingConvention.LowerSnakeCase => LowerSnakeCaseCode,
            NamingConvention.UpperCamelCase => UpperCamelCaseCode,
            NamingConvention.ScreamingSnakeCase => ScreamingSnakeCaseCode,
            _ => LowerSnakeCaseCode
        };
        var key = $"{code}:{span.Position}:{span.Length}:{expected}";
        if (!reported.Add(key))
        {
            return;
        }

        var diagnostic = Diagnostic.Diagnostic.Warning(
                DiagnosticMessages.NamingStyleMismatch(name, category, FormatConvention(convention)),
                code)
            .WithLabel(span, DiagnosticMessages.NamingStyleExpectedName(expected))
            .WithSuggestion(
                DiagnosticMessages.RenameSymbolSuggestion(expected),
                SuggestionKind.RenameSymbol,
                span,
                expected,
                requiresCleanTypes: true,
                originalSymbolId: symbolId.IsValid ? symbolId.Value : null)
            .WithMetadata("style", "naming")
            .WithMetadata("naming.category", category)
            .WithMetadata("naming.convention", FormatConvention(convention))
            .WithMetadata("naming.expected", expected);
        if (symbolId.IsValid)
        {
            diagnostic.WithMetadata("naming.symbolId", symbolId.Value.ToString(CultureInfo.InvariantCulture));
        }

        diagnostics.Add(diagnostic);
    }

    private static void AddFqnRedundancyDiagnostic(
        IReadOnlyList<string> modulePath,
        string name,
        NamingConvention convention,
        string category,
        SourceSpan span,
        SymbolId symbolId,
        List<Diagnostic.Diagnostic> diagnostics,
        HashSet<string> reported)
    {
        var nameWords = SplitWords(name);
        if (nameWords.Count < 2)
        {
            return;
        }

        foreach (var segment in modulePath)
        {
            var moduleWords = SplitWords(segment);
            if (moduleWords.Count == 0 || moduleWords.Count >= nameWords.Count ||
                !nameWords.Take(moduleWords.Count).SequenceEqual(moduleWords, StringComparer.Ordinal))
            {
                continue;
            }

            var replacement = BuildFromWords(nameWords.Skip(moduleWords.Count), convention, HasUnusedPrefix(name));
            if (string.IsNullOrWhiteSpace(replacement))
            {
                continue;
            }

            var key = $"{FqnRedundancyCode}:{span.Position}:{span.Length}:{replacement}";
            if (!reported.Add(key))
            {
                return;
            }

            diagnostics.Add(Diagnostic.Diagnostic.Warning(
                    DiagnosticMessages.NamingFqnRedundancy(name, segment),
                    FqnRedundancyCode)
                .WithLabel(span, DiagnosticMessages.NamingStyleExpectedName(replacement))
                .WithSuggestion(
                    DiagnosticMessages.RenameSymbolSuggestion(replacement),
                    SuggestionKind.RenameSymbol,
                    span,
                    replacement,
                    requiresCleanTypes: true,
                    originalSymbolId: symbolId.IsValid ? symbolId.Value : null)
                .WithMetadata("style", "naming")
                .WithMetadata("naming.category", category)
                .WithMetadata("naming.rule", "fqn-redundancy")
                .WithMetadata("naming.expected", replacement));
            return;
        }
    }

    private static void AddWeakTypeNameDiagnostic(
        Symbol symbol,
        SourceSpan span,
        List<Diagnostic.Diagnostic> diagnostics,
        HashSet<string> reported)
    {
        var key = $"{WeakTypeNameCode}:{span.Position}:{span.Length}";
        if (!reported.Add(key))
        {
            return;
        }

        diagnostics.Add(Diagnostic.Diagnostic.Warning(
                DiagnosticMessages.NamingWeakPublicTypeName(symbol.Name),
                WeakTypeNameCode)
            .WithLabel(span)
            .WithMetadata("style", "naming")
            .WithMetadata("naming.category", "public type")
            .WithMetadata("naming.rule", "weak-semantic-placeholder"));
    }

    private static void AddModuleFileDiagnostic(
        ModuleDecl module,
        string? sourceFilePath,
        List<Diagnostic.Diagnostic> diagnostics,
        HashSet<string> reported)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath) ||
            !string.Equals(Path.GetExtension(sourceFilePath), ".eidos", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var stem = Path.GetFileNameWithoutExtension(sourceFilePath);
        var expectedStem = Normalize(stem, NamingConvention.LowerSnakeCase);
        if (string.Equals(stem, expectedStem, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(expectedStem) ||
            !reported.Add($"{ModuleFileCode}:{sourceFilePath}"))
        {
            return;
        }

        diagnostics.Add(Diagnostic.Diagnostic.Warning(
                DiagnosticMessages.NamingModuleFileMismatch(Path.GetFileName(sourceFilePath), $"{expectedStem}.eidos"),
                ModuleFileCode)
            .WithLabel(module.Span)
            .WithMetadata("style", "naming")
            .WithMetadata("naming.category", "module file")
            .WithMetadata("naming.expected", $"{expectedStem}.eidos"));
    }

    private static bool IsRequestedSource(SourceSpan span, string sourceText, string? sourceFilePath)
    {
        if (span.Position < 0 || span.Length <= 0 || span.EndPosition > sourceText.Length)
        {
            return false;
        }

        if (string.IsNullOrWhiteSpace(sourceFilePath) || string.IsNullOrWhiteSpace(span.FilePath))
        {
            return true;
        }

        return string.Equals(NormalizePath(span.FilePath), NormalizePath(sourceFilePath), StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetFileName(span.FilePath), Path.GetFileName(sourceFilePath), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/').Trim();
        }
    }

    private static IEnumerable<EidosAstNode> EnumerateChildNodes(EidosAstNode node)
    {
        foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            switch (property.GetValue(node))
            {
                case EidosAstNode child:
                    yield return child;
                    break;
                case IEnumerable enumerable and not string:
                    foreach (var item in enumerable)
                    {
                        if (item is EidosAstNode childItem)
                        {
                            yield return childItem;
                        }
                    }
                    break;
            }
        }
    }

    internal static string Normalize(string name, NamingConvention convention)
    {
        var words = SplitWords(name);
        return BuildFromWords(words, convention, HasUnusedPrefix(name));
    }

    private static IReadOnlyList<string> SplitWords(string name)
    {
        var trimmed = name.TrimStart('_');
        var words = new List<string>();
        var current = new StringBuilder();
        for (var index = 0; index < trimmed.Length; index++)
        {
            var value = trimmed[index];
            if (!char.IsLetterOrDigit(value))
            {
                Flush();
                continue;
            }

            var previous = index > 0 ? trimmed[index - 1] : '\0';
            var next = index + 1 < trimmed.Length ? trimmed[index + 1] : '\0';
            var startsWord = current.Length > 0 &&
                             (char.IsUpper(value) &&
                              (char.IsLower(previous) || char.IsDigit(previous) ||
                               char.IsUpper(previous) && char.IsLower(next)) ||
                              char.IsLetter(value) && char.IsDigit(previous));
            if (startsWord)
            {
                Flush();
            }

            current.Append(char.ToLowerInvariant(value));
        }

        Flush();
        return words;

        void Flush()
        {
            if (current.Length == 0)
            {
                return;
            }

            words.Add(current.ToString());
            current.Clear();
        }
    }

    private static string BuildFromWords(
        IEnumerable<string> words,
        NamingConvention convention,
        bool unusedPrefix)
    {
        var values = words.Where(static word => word.Length > 0).ToList();
        if (values.Count == 0)
        {
            return string.Empty;
        }

        var prefix = unusedPrefix ? "_" : string.Empty;
        return convention switch
        {
            NamingConvention.LowerSnakeCase => prefix + string.Join("_", values),
            NamingConvention.ScreamingSnakeCase => prefix + string.Join("_", values).ToUpperInvariant(),
            NamingConvention.UpperCamelCase => prefix + string.Concat(values.Select(ToUpperCamelWord)),
            _ => prefix + string.Join("_", values)
        };
    }

    private static string ToUpperCamelWord(string word) =>
        word.Length == 0
            ? string.Empty
            : char.ToUpperInvariant(word[0]) + word[1..];

    private static bool HasUnusedPrefix(string name) =>
        name.Length > 1 && name[0] == '_' && name[1] != '_';

    private static string FormatConvention(NamingConvention convention) => convention switch
    {
        NamingConvention.LowerSnakeCase => "lower_snake_case",
        NamingConvention.UpperCamelCase => "UpperCamelCase",
        NamingConvention.ScreamingSnakeCase => "SCREAMING_SNAKE_CASE",
        _ => convention.ToString()
    };

    internal enum NamingConvention
    {
        LowerSnakeCase,
        UpperCamelCase,
        ScreamingSnakeCase
    }
}
