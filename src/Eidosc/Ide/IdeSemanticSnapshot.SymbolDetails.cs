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

// Symbol detail building, documentation, borrow capabilities
public static partial class IdeSemanticSnapshotBuilder
{


    private static List<IdeOccurrenceEntry> CollectOccurrences(
        EidosAstNode root,
        IReadOnlyDictionary<int, IdeSymbolEntry> symbolMap,
        SymbolTable? symbolTable,
        string sourceText,
        IReadOnlyDictionary<string, SymbolId> modulePathSymbolIds,
        IReadOnlyDictionary<string, SymbolId> importedModuleAliases)
    {
        var occurrences = new List<IdeOccurrenceEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        Visit(root);
        return occurrences;

        void Visit(object? value)
        {
            if (value == null)
            {
                return;
            }

            if (value is string)
            {
                return;
            }

            if (value is EidosAstNode node)
            {
                if (!visited.Add(node))
                {
                    return;
                }

                AddOccurrenceFromNode(node);

                foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length > 0)
                    {
                        continue;
                    }

                    if (string.Equals(property.Name, nameof(EidosAstNode.InferredType), StringComparison.Ordinal))
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

        void AddOccurrenceFromNode(EidosAstNode node)
        {
            if (node is ImportDecl import)
            {
                var importedModuleSymbolId = import.ResolvedSymbols
                    .FirstOrDefault(static symbol => symbol.Kind == ResolutionKind.Module && symbol.SymbolId.IsValid)
                    ?.SymbolId;
                var moduleSymbolId = importedModuleSymbolId is { IsValid: true }
                    ? importedModuleSymbolId.Value
                    : import.ResolvedModule;
                if (!moduleSymbolId.IsValid)
                {
                    return;
                }

                var qualifiedModulePath = import.ToQualifiedModulePath();
                if (qualifiedModulePath.Count > 0)
                {
                    AddQualifiedPathOccurrences(
                        qualifiedModulePath.Take(qualifiedModulePath.Count - 1).ToList(),
                        qualifiedModulePath[^1],
                        moduleSymbolId,
                        nameof(ImportDecl),
                        import.Span);
                }

                return;
            }

            if (node is PathExpr pathExpr &&
                pathExpr.SymbolId.IsValid)
            {
                AddQualifiedPathOccurrences(
                    pathExpr.ModulePath,
                    pathExpr.Name,
                    pathExpr.SymbolId,
                    nameof(PathExpr),
                    pathExpr.Span);
                return;
            }

            if (node is TypePath typePath &&
                typePath.SymbolId.IsValid)
            {
                AddQualifiedPathOccurrences(
                    typePath.ModulePath,
                    typePath.TypeName,
                    typePath.SymbolId,
                    nameof(TypePath),
                    typePath.Span);
                return;
            }

            if (node is EffectfulType effectfulType &&
                effectfulType.EffectSymbolIds.Count > 0 &&
                effectfulType.EffectPathSpans.Count > 0)
            {
                var count = Math.Min(effectfulType.EffectSymbolIds.Count, effectfulType.EffectPathSpans.Count);
                for (var i = 0; i < count; i++)
                {
                    if (!effectfulType.EffectSymbolIds[i].IsValid ||
                        !IdeSpan.TryFrom(effectfulType.EffectPathSpans[i], out var effectSpan))
                    {
                        continue;
                    }

                    AddOccurrence(effectfulType.EffectSymbolIds[i].Value, "reference", nameof(EffectfulType), effectSpan);
                }

                return;
            }

            if (node is EffectRequirementNode requirement &&
                requirement.SymbolId.IsValid &&
                requirement.Path.Count > 0)
            {
                AddQualifiedPathOccurrences(
                    requirement.Path.Take(requirement.Path.Count - 1).ToList(),
                    requirement.Path[^1],
                    requirement.SymbolId,
                    nameof(EffectRequirementNode),
                    requirement.Span);
                return;
            }

            if (node.SymbolId.IsValid && IdeSpan.TryFrom(node.Span, out var span))
            {
                var role = IsDefinitionNode(node) || MatchesDefinitionSpan(node.SymbolId.Value, span)
                    ? "definition"
                    : "reference";
                AddOccurrence(node.SymbolId.Value, role, node.GetType().Name, span);
            }

            if (node is Assignment assignment &&
                assignment.TargetSymbolId.IsValid &&
                IdeSpan.TryFrom(assignment.Span, out var assignSpan))
            {
                AddOccurrence(assignment.TargetSymbolId.Value, "reference", "AssignmentTarget", assignSpan);
            }
        }

        bool MatchesDefinitionSpan(int symbolId, IdeSpan span)
        {
            if (!symbolMap.TryGetValue(symbolId, out var symbol) || symbol.Span == null)
            {
                return false;
            }

            return symbol.Span.Start == span.Start &&
                   symbol.Span.Length == span.Length;
        }

        void AddOccurrence(int symbolId, string role, string source, IdeSpan span)
        {
            var key = $"{symbolId}:{role}:{span.Start}:{span.Length}:{source}";
            if (!seen.Add(key))
            {
                return;
            }

            occurrences.Add(new IdeOccurrenceEntry
            {
                SymbolId = symbolId,
                Role = role,
                Source = source,
                Span = span
            });
        }

        void AddQualifiedPathOccurrences(
            IReadOnlyList<string> modulePath,
            string leafName,
            SymbolId leafSymbolId,
            string source,
            SourceSpan sourceSpan)
        {
            if (!TryFindQualifiedPathSegmentSpans(
                    sourceText,
                    sourceSpan,
                    modulePath,
                    leafName,
                    out var segmentSpans))
            {
                if (IdeSpan.TryFrom(sourceSpan, out var fallbackSpan))
                {
                    AddOccurrence(leafSymbolId.Value, "reference", source, fallbackSpan);
                }

                return;
            }

            var segments = modulePath.Concat([leafName]).ToList();
            for (var i = 0; i < segments.Count - 1; i++)
            {
                var prefix = segments.Take(i + 1).ToList();
                var prefixSymbolId = ResolveQualifiedPathPrefixSymbol(symbolTable, modulePathSymbolIds, importedModuleAliases, prefix);
                if (!prefixSymbolId.HasValue ||
                    !prefixSymbolId.Value.IsValid ||
                    prefixSymbolId.Value == leafSymbolId ||
                    !IdeSpan.TryFrom(segmentSpans[i], out var prefixSpan))
                {
                    continue;
                }

                AddOccurrence(prefixSymbolId.Value.Value, "reference", $"{source}Prefix", prefixSpan);
            }

            if (IdeSpan.TryFrom(segmentSpans[^1], out var leafSpan))
            {
                AddOccurrence(leafSymbolId.Value, "reference", source, leafSpan);
            }
        }
    }

    private static SymbolId? ResolveQualifiedPathPrefixSymbol(
        SymbolTable? symbolTable,
        IReadOnlyDictionary<string, SymbolId> modulePathSymbolIds,
        IReadOnlyDictionary<string, SymbolId> importedModuleAliases,
        IReadOnlyList<string> prefix)
    {
        if (prefix.Count == 0)
        {
            return null;
        }

        if (modulePathSymbolIds.TryGetValue(FormatModulePath(prefix), out var modulePrefixId))
        {
            return modulePrefixId;
        }

        if (prefix.Count == 1 &&
            importedModuleAliases.TryGetValue(prefix[0], out var importedModuleId) &&
            importedModuleId.IsValid)
        {
            return importedModuleId;
        }

        if (symbolTable == null)
        {
            return null;
        }

        var moduleId = symbolTable.Modules.LookupModuleByPath(prefix);
        if (moduleId.HasValue && moduleId.Value.IsValid)
        {
            return moduleId.Value;
        }

        var result = symbolTable.ResolvePathWithResult(prefix);
        return result.IsSuccess ? result.SymbolId : null;
    }

    private static Dictionary<string, SymbolId> CollectImportedModuleAliases(EidosAstNode root)
    {
        var aliases = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
        var ambiguousAliases = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);

        Visit(root);

        foreach (var alias in ambiguousAliases)
        {
            aliases.Remove(alias);
        }

        return aliases;

        void Visit(object? value)
        {
            if (value == null || value is string)
            {
                return;
            }

            if (value is ImportDecl import)
            {
                foreach (var symbol in import.ResolvedSymbols)
                {
                    if (symbol.Kind != ResolutionKind.Module ||
                        !symbol.SymbolId.IsValid ||
                        string.IsNullOrWhiteSpace(symbol.Name))
                    {
                        continue;
                    }

                    if (aliases.TryGetValue(symbol.Name, out var existing) &&
                        existing != symbol.SymbolId)
                    {
                        ambiguousAliases.Add(symbol.Name);
                        continue;
                    }

                    aliases[symbol.Name] = symbol.SymbolId;
                }
            }

            if (value is EidosAstNode node)
            {
                if (!visited.Add(node))
                {
                    return;
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

    private static string FormatModulePath(IEnumerable<string> path)
    {
        return string.Join(WellKnownStrings.Separators.ModulePath, path);
    }

    private static bool TryFindQualifiedPathSegmentSpans(
        string sourceText,
        SourceSpan pathSpan,
        IReadOnlyList<string> modulePath,
        string leafName,
        out List<SourceSpan> segmentSpans)
    {
        segmentSpans = [];
        if (string.IsNullOrEmpty(sourceText) ||
            pathSpan.Position < 0 ||
            pathSpan.Length <= 0 ||
            pathSpan.Position + pathSpan.Length > sourceText.Length)
        {
            return false;
        }

        var pathText = sourceText.Substring(pathSpan.Position, pathSpan.Length);
        var searchStart = 0;
        foreach (var segment in modulePath.Concat([leafName]))
        {
            var relativeStart = pathText.IndexOf(segment, searchStart, StringComparison.Ordinal);
            if (relativeStart < 0)
            {
                segmentSpans = [];
                return false;
            }

            segmentSpans.Add(new SourceSpan(pathSpan.Location + relativeStart, segment.Length));
            searchStart = relativeStart + segment.Length;
        }

        return segmentSpans.Count == modulePath.Count + 1;
    }

    private static bool IsDefinitionNode(EidosAstNode node)
    {
        return node switch
        {
            FuncDef or
            FuncDecl or
            LetDecl or
            AdtDef or
            EffectDef or
            TraitDef or
            ProofDecl or
            ModuleDecl or
            Constructor or
            Field or
            Eidosc.Ast.Types.TypeParam or
            Eidosc.Ast.Patterns.VarPattern or
            Eidosc.Ast.Patterns.AsPattern => true,
            _ => false
        };
    }

    private static List<IdeCompletionEntry> BuildCompletions(
        SymbolTable? symbolTable,
        IReadOnlyList<IdeSymbolEntry> symbols,
        IReadOnlyList<IdeOverloadGroupEntry> overloadGroups)
    {
        var result = new List<IdeCompletionEntry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var qualifiedLabelsBySymbolId = symbolTable != null
            ? BuildQualifiedCompletionLabels(symbolTable)
            : new Dictionary<int, List<string>>();
        var overloadGroupBySymbolId = BuildOverloadGroupMap(overloadGroups);

        foreach (var symbol in symbols)
        {
            if (string.IsNullOrWhiteSpace(symbol.Name))
            {
                continue;
            }

            var key = $"symbol:{symbol.SymbolId}";
            if (!seen.Add(key))
            {
                continue;
            }

            overloadGroupBySymbolId.TryGetValue(symbol.SymbolId, out var overloadGroup);
            result.Add(new IdeCompletionEntry
            {
                SymbolId = symbol.SymbolId,
                OverloadGroupId = overloadGroup?.GroupId,
                OverloadCount = overloadGroup?.MemberSymbolIds.Count ?? 0,
                OverloadMemberSymbolIds = overloadGroup?.MemberSymbolIds.ToList() ?? [],
                Label = symbol.Name,
                Kind = symbol.Kind,
                Detail = symbol.Detail,
                Documentation = symbol.Documentation,
                TypeText = symbol.TypeText,
                TypeConfidence = symbol.TypeConfidence,
                DefinitionFingerprint = symbol.DefinitionFingerprint,
                DefinitionFingerprintScope = symbol.DefinitionFingerprintScope,
                BindingMode = symbol.BindingMode,
                Span = symbol.Span,
                VisibilitySpan = symbol.VisibilitySpan,
                IsBuiltin = symbol.IsBuiltin,
                SortText = symbol.IsBuiltin ? $"0_{symbol.Name}" : $"1_{symbol.Name}"
            });

            if (!qualifiedLabelsBySymbolId.TryGetValue(symbol.SymbolId, out var qualifiedLabels))
            {
                continue;
            }

            foreach (var qualifiedLabel in qualifiedLabels)
            {
                if (string.IsNullOrWhiteSpace(qualifiedLabel) ||
                    string.Equals(qualifiedLabel, symbol.Name, StringComparison.Ordinal))
                {
                    continue;
                }

                var qualifiedKey = $"symbol:{symbol.SymbolId}:{qualifiedLabel}";
                if (!seen.Add(qualifiedKey))
                {
                    continue;
                }

                result.Add(new IdeCompletionEntry
                {
                    SymbolId = symbol.SymbolId,
                    OverloadGroupId = overloadGroup?.GroupId,
                    OverloadCount = overloadGroup?.MemberSymbolIds.Count ?? 0,
                    OverloadMemberSymbolIds = overloadGroup?.MemberSymbolIds.ToList() ?? [],
                    Label = qualifiedLabel,
                    Kind = symbol.Kind,
                    Detail = string.IsNullOrWhiteSpace(symbol.Detail)
                        ? DiagnosticMessages.IdeQualifiedPathDetail
                        : DiagnosticMessages.IdeQualifiedSymbolDetail(symbol.Detail),
                    Documentation = symbol.Documentation,
                    TypeText = symbol.TypeText,
                    TypeConfidence = symbol.TypeConfidence,
                    DefinitionFingerprint = symbol.DefinitionFingerprint,
                    DefinitionFingerprintScope = symbol.DefinitionFingerprintScope,
                    BindingMode = symbol.BindingMode,
                    Span = symbol.Span,
                    VisibilitySpan = symbol.VisibilitySpan,
                    IsBuiltin = symbol.IsBuiltin,
                    SortText = symbol.IsBuiltin ? $"0q_{qualifiedLabel}" : $"1q_{qualifiedLabel}"
                });
            }
        }

        foreach (var keyword in KeywordCompletions)
        {
            var key = $"keyword:{keyword}";
            if (!seen.Add(key))
            {
                continue;
            }

            var documentation = string.Equals(keyword, WellKnownStrings.Keywords.Self, StringComparison.Ordinal)
                ? DiagnosticMessages.IdeTraitSelfDocumentation
                : DiagnosticMessages.IdeKeywordDocumentation;

            result.Add(new IdeCompletionEntry
            {
                Label = keyword,
                Kind = "keyword",
                Detail = DiagnosticMessages.IdeKeywordDetail,
                Documentation = documentation,
                TypeText = null,
                BindingMode = null,
                Span = null,
                VisibilitySpan = null,
                IsBuiltin = false,
                SortText = $"2_{keyword}"
            });
        }

        foreach (var attribute in AttributeCompletions)
        {
            var key = $"attribute:{attribute}";
            if (!seen.Add(key))
            {
                continue;
            }

            result.Add(new IdeCompletionEntry
            {
                Label = attribute,
                Kind = "keyword",
                Detail = DiagnosticMessages.IdeAttributeDetail,
                Documentation = DiagnosticMessages.IdeAttributeDocumentation,
                TypeText = null,
                BindingMode = null,
                Span = null,
                VisibilitySpan = null,
                IsBuiltin = false,
                SortText = $"2a_{attribute}"
            });
        }

        foreach (var traitName in DeriveTraitCompletions)
        {
            var key = $"derive-trait:{traitName}";
            if (!seen.Add(key))
            {
                continue;
            }

            result.Add(new IdeCompletionEntry
            {
                Label = traitName,
                Kind = "trait",
                Detail = DiagnosticMessages.IdeDeriveTraitDetail,
                Documentation = DiagnosticMessages.IdeDeriveTraitDocumentation,
                TypeText = null,
                BindingMode = null,
                Span = null,
                VisibilitySpan = null,
                IsBuiltin = false,
                SortText = $"2d_{traitName}"
            });
        }

        result.Sort(static (left, right) => string.Compare(left.SortText, right.SortText, StringComparison.Ordinal));
        return result;
    }

    private static Dictionary<int, IdeOverloadGroupEntry> BuildOverloadGroupMap(
        IReadOnlyList<IdeOverloadGroupEntry> overloadGroups)
    {
        var result = new Dictionary<int, IdeOverloadGroupEntry>();
        foreach (var group in overloadGroups)
        {
            foreach (var memberSymbolId in group.MemberSymbolIds)
            {
                result[memberSymbolId] = group;
            }
        }

        return result;
    }

    private static Dictionary<int, List<string>> BuildQualifiedCompletionLabels(SymbolTable symbolTable)
    {
        var result = new Dictionary<int, List<string>>();

        foreach (var module in symbolTable.Modules.ModulePaths.Values
                     .Distinct()
                     .Select(symbolTable.Modules.GetModule)
                     .Where(static module => module?.Path is { Count: > 0 })
                     .OrderBy(static module => string.Join(WellKnownStrings.Operators.Divide, module!.Path), StringComparer.Ordinal))
        {
            var shortPrefix = module!.Path[^1];
            AddQualifiedCompletionLabels(
                symbolTable,
                module.Id,
                shortPrefix,
                result,
                new HashSet<SymbolId>(),
                useModulePathSeparator: module.Path.Count == 1);

            var fullPrefix = string.Join(".", module.Path);
            if (!string.Equals(fullPrefix, shortPrefix, StringComparison.Ordinal))
            {
                AddQualifiedCompletionLabels(
                    symbolTable,
                    module.Id,
                    fullPrefix,
                    result,
                    new HashSet<SymbolId>(),
                    useModulePathSeparator: true);
            }

            if (!string.IsNullOrWhiteSpace(module.PackageAlias))
            {
                var packagePrefix = $"{module.PackageAlias}{WellKnownStrings.Separators.Path}{fullPrefix}";
                AddQualifiedCompletionLabels(
                    symbolTable,
                    module.Id,
                    packagePrefix,
                    result,
                    new HashSet<SymbolId>(),
                    useModulePathSeparator: true);
            }
        }

        return result;
    }

    private static void AddQualifiedCompletionLabels(
        SymbolTable symbolTable,
        SymbolId moduleId,
        string prefix,
        Dictionary<int, List<string>> result,
        HashSet<SymbolId> visitedModules,
        bool useModulePathSeparator)
    {
        if (!moduleId.IsValid ||
            string.IsNullOrWhiteSpace(prefix) ||
            !visitedModules.Add(moduleId))
        {
            return;
        }

        try
        {
            foreach (var binding in symbolTable.Modules.GetAccessibleBindings(moduleId, requesterModuleId: null))
            {
                if (string.IsNullOrWhiteSpace(binding.Name) || !binding.SymbolId.IsValid)
                {
                    continue;
                }

                var symbol = symbolTable.GetSymbol(binding.SymbolId);
                switch (symbol)
                {
                    case ModuleSymbol nestedModule:
                        AddQualifiedCompletionLabels(
                            symbolTable,
                            nestedModule.Id,
                            AppendModuleSegment(prefix, binding.Name, useModulePathSeparator),
                            result,
                            visitedModules,
                            useModulePathSeparator);
                        break;

                    case TraitSymbol trait:
                        AddQualifiedCompletionLabel(result, trait.Id, $"{prefix}::{binding.Name}");
                        AddQualifiedOwnerMemberCompletionLabels(
                            symbolTable,
                            result,
                            prefix,
                            binding.Name,
                            trait.Methods);
                        break;

                    case EffectSymbol ability:
                        AddQualifiedCompletionLabel(result, ability.Id, $"{prefix}::{binding.Name}");
                        break;
                }
            }
        }
        finally
        {
            visitedModules.Remove(moduleId);
        }
    }

    private static void AddQualifiedOwnerMemberCompletionLabels(
        SymbolTable symbolTable,
        Dictionary<int, List<string>> result,
        string prefix,
        string ownerName,
        IReadOnlyList<SymbolId> memberIds)
    {
        if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(ownerName))
        {
            return;
        }

        var leafName = GetQualifiedPathLeaf(prefix);
        var collapseOwnerSegment = string.Equals(leafName, ownerName, StringComparison.Ordinal);

        foreach (var memberId in memberIds)
        {
            if (symbolTable.GetSymbol(memberId) is not FuncSymbol member ||
                string.IsNullOrWhiteSpace(member.Name))
            {
                continue;
            }

            var label = collapseOwnerSegment
                ? $"{prefix}::{member.Name}"
                : $"{prefix}::{ownerName}::{member.Name}";
            AddQualifiedCompletionLabel(result, member.Id, label);
        }
    }

    private static string GetQualifiedPathLeaf(string prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return string.Empty;
        }

        var separatorIndex = Math.Max(
            prefix.LastIndexOf(WellKnownStrings.Separators.Path, StringComparison.Ordinal),
            Math.Max(
                prefix.LastIndexOf(WellKnownStrings.Separators.ModulePath, StringComparison.Ordinal),
                prefix.LastIndexOf(".", StringComparison.Ordinal)));
        return separatorIndex >= 0
            ? prefix[(separatorIndex + SeparatorLengthAt(prefix, separatorIndex))..]
            : prefix;
    }

    private static string AppendModuleSegment(string prefix, string segment, bool useModulePathSeparator)
    {
        var separator = useModulePathSeparator
            ? "."
            : WellKnownStrings.Separators.Path;
        return $"{prefix}{separator}{segment}";
    }

    private static int SeparatorLengthAt(string value, int index)
    {
        return value.AsSpan(index).StartsWith(WellKnownStrings.Separators.Path, StringComparison.Ordinal)
            ? WellKnownStrings.Separators.Path.Length
            : WellKnownStrings.Separators.ModulePath.Length;
    }

    private static void AddQualifiedCompletionLabel(
        Dictionary<int, List<string>> result,
        SymbolId symbolId,
        string label)
    {
        if (!symbolId.IsValid || string.IsNullOrWhiteSpace(label))
        {
            return;
        }

        if (!result.TryGetValue(symbolId.Value, out var labels))
        {
            labels = [];
            result[symbolId.Value] = labels;
        }

        if (!labels.Contains(label, StringComparer.Ordinal))
        {
            labels.Add(label);
        }
    }

    private static List<IdeBorrowCapabilityEntry> BuildBorrowCapabilities(ModuleBorrowCheckResult? borrowCheckResult)
    {
        if (borrowCheckResult == null || borrowCheckResult.FunctionResults.Count == 0)
        {
            return [];
        }

        var entries = new List<IdeBorrowCapabilityEntry>(borrowCheckResult.FunctionResults.Count);
        foreach (var functionResult in borrowCheckResult.FunctionResults.Values
                     .OrderBy(static result => result.FunctionName, StringComparer.Ordinal))
        {
            var snapshot = functionResult.BorrowChecker?.CapabilitySnapshot ??
                           functionResult.LoanConstraintVerifier?.CapabilitySnapshot;
            if (snapshot == null)
            {
                entries.Add(new IdeBorrowCapabilityEntry
                {
                    FunctionName = functionResult.FunctionName,
                    HasSnapshot = false,
                    IsEnforced = false,
                    GlobalCapabilities = [],
                    Providers = []
                });
                continue;
            }

            var globals = snapshot.GlobalCapabilities
                .Select(capability => capability.ToString().ToLowerInvariant())
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            var providers = snapshot.EnumerateCapabilityProviders()
                .Select(provider => new IdeBorrowCapabilityProviderEntry
                {
                    Provider = provider.Provider,
                    Capabilities = provider.Capabilities
                        .Select(capability => capability.ToString().ToLowerInvariant())
                        .OrderBy(name => name, StringComparer.Ordinal)
                        .ToList()
                })
                .OrderBy(entry => entry.Provider, StringComparer.Ordinal)
                .ToList();

            entries.Add(new IdeBorrowCapabilityEntry
            {
                FunctionName = functionResult.FunctionName,
                HasSnapshot = true,
                IsEnforced = snapshot.IsEnforced,
                GlobalCapabilities = globals,
                Providers = providers
            });
        }

        return entries;
    }

    private static string BuildSymbolDetail(Symbol symbol)
    {
        return symbol switch
        {
            FuncSymbol => DiagnosticMessages.IdeSymbolDetailFunction,
            VarSymbol { IsParameter: true } => DiagnosticMessages.IdeSymbolDetailParameter,
            VarSymbol { IsPatternBound: true } => DiagnosticMessages.IdeSymbolDetailPatternBinding,
            VarSymbol { IsMutable: true } => DiagnosticMessages.IdeSymbolDetailMutableVariable,
            VarSymbol => DiagnosticMessages.IdeSymbolDetailValue,
            CtorSymbol => DiagnosticMessages.IdeSymbolDetailConstructor,
            TraitSymbol => WellKnownStrings.Keywords.Trait,
            EffectSymbol => WellKnownStrings.Keywords.Effect,
            AdtSymbol => WellKnownStrings.Keywords.Type,
            TypeParamSymbol => DiagnosticMessages.IdeSymbolDetailTypeParameter,
            ModuleSymbol => WellKnownStrings.Keywords.Module,
            FieldSymbol => DiagnosticMessages.IdeSymbolDetailField,
            ImplSymbol => DiagnosticMessages.IdeSymbolDetailTraitImpl,
            _ => symbol.Kind.ToString()
        };
    }

    private static string BuildSymbolDocumentation(
        Symbol symbol,
        bool isBuiltin,
        IdeSymbolMetadata? metadata)
    {
        if (isBuiltin && TryGetBuiltinTypeDocumentation(symbol.Name, out var doc))
        {
            return doc;
        }

        return symbol.Kind switch
        {
            SymbolKind.Function => DiagnosticMessages.IdeFunctionDocumentation(symbol.Name),
            SymbolKind.Variable when symbol is VarSymbol { IsPatternBound: true }
                => "",
            SymbolKind.Variable => "",
            SymbolKind.Adt => DiagnosticMessages.IdeTypeDocumentation(symbol.Name),
            SymbolKind.Constructor => DiagnosticMessages.IdeConstructorDocumentation(symbol.Name),
            SymbolKind.Trait => DiagnosticMessages.IdeTraitDocumentation(symbol.Name),
            SymbolKind.Effect => DiagnosticMessages.IdeEffectDocumentation(symbol.Name),
            SymbolKind.TypeParameter => DiagnosticMessages.IdeTypeParameterDocumentation(symbol.Name),
            SymbolKind.Module => DiagnosticMessages.IdeModuleDocumentation(symbol.Name),
            SymbolKind.Field => DiagnosticMessages.IdeFieldDocumentation(symbol.Name),
            SymbolKind.Proof => DiagnosticMessages.IdeProofDocumentation(symbol.Name),
            SymbolKind.Impl => DiagnosticMessages.IdeTraitImplementationDocumentation(symbol.Name),
            _ => DiagnosticMessages.IdeSymbolDocumentation(symbol.Kind.ToString(), symbol.Name)
        };
    }

    private static bool TryGetBuiltinTypeDocumentation(string typeName, out string documentation)
    {
        documentation = typeName switch
        {
            WellKnownStrings.BuiltinTypes.Int => DiagnosticMessages.IdeBuiltinIntDocumentation,
            WellKnownStrings.BuiltinTypes.Float => DiagnosticMessages.IdeBuiltinFloatDocumentation,
            WellKnownStrings.BuiltinTypes.Bool => DiagnosticMessages.IdeBuiltinBoolDocumentation,
            WellKnownStrings.BuiltinTypes.String => DiagnosticMessages.IdeBuiltinStringDocumentation,
            WellKnownStrings.BuiltinTypes.Char => DiagnosticMessages.IdeBuiltinCharDocumentation,
            WellKnownStrings.BuiltinTypes.Unit => DiagnosticMessages.IdeBuiltinUnitDocumentation,
            WellKnownStrings.BuiltinTypes.Never => DiagnosticMessages.IdeBuiltinNeverDocumentation,
            _ => string.Empty
        };
        return documentation.Length > 0;
    }

    private static bool IsBuiltinTypeName(string typeName) =>
        typeName is WellKnownStrings.BuiltinTypes.Int or
            WellKnownStrings.BuiltinTypes.Float or
            WellKnownStrings.BuiltinTypes.Bool or
            WellKnownStrings.BuiltinTypes.String or
            WellKnownStrings.BuiltinTypes.Char or
            WellKnownStrings.BuiltinTypes.Unit or
            WellKnownStrings.BuiltinTypes.Never;

    private static SourceSpan? ResolveChildVisibility(EidosAstNode node, SourceSpan? inheritedVisibility)
    {
        return node switch
        {
            ModuleDecl or FuncDef or FuncDecl or BlockExpr or PatternBranch or LambdaExpr
                => node.Span,
            _ => inheritedVisibility
        };
    }

    private static SourceSpan GetNodeSpanOrFallback(EidosAstNode? node, SourceSpan fallback)
    {
        if (node != null && HasSpan(node.Span))
        {
            return node.Span;
        }

        return fallback;
    }

    private static SourceSpan? GetNodeSpanOrFallback(EidosAstNode? node, SourceSpan? fallback)
    {
        if (node != null && HasSpan(node.Span))
        {
            return node.Span;
        }

        return fallback;
    }

    private static IdeSpan? TryConvertIdeSpan(SourceSpan? span)
    {
        if (span is { } value && IdeSpan.TryFrom(value, out var ideSpan))
        {
            return ideSpan;
        }

        return null;
    }

    private static bool HasSpan(SourceSpan span)
    {
        return span.Length > 0 ||
               span.Location.Position > 0 ||
               span.Location.Line > 0 ||
               span.Location.Column > 0;
    }

    private static string MapSeverity(DiagnosticLevel level)
    {
        return level switch
        {
            DiagnosticLevel.Error => "error",
            DiagnosticLevel.Warning => "warning",
            DiagnosticLevel.Info => "info",
            DiagnosticLevel.Note => "note",
            DiagnosticLevel.Help => "help",
            _ => "error"
        };
    }

    private static string MapSymbolKind(SymbolKind kind)
    {
        return kind switch
        {
            SymbolKind.Function => "function",
            SymbolKind.Variable => "variable",
            SymbolKind.TypeParameter => "typeParameter",
            SymbolKind.Adt => WellKnownStrings.Keywords.Type,
            SymbolKind.TypeAlias => "typeAlias",
            SymbolKind.Constructor => "constructor",
            SymbolKind.Effect => WellKnownStrings.Keywords.Effect,
            SymbolKind.Trait => WellKnownStrings.Keywords.Trait,
            SymbolKind.Module => WellKnownStrings.Keywords.Module,
            SymbolKind.Field => "field",
            SymbolKind.Proof => WellKnownStrings.Keywords.Proof,
            SymbolKind.Impl => "impl",
            _ => "symbol"
        };
    }
}
