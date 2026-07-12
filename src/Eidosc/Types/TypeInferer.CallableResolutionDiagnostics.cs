using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Utils;

using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private string FormatCallableCandidateList(IEnumerable<SymbolId> candidateIds)
    {
        var displays = candidateIds
            .Select(FormatCallableCandidate)
            .Where(static display => !string.IsNullOrWhiteSpace(display))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static display => display, StringComparer.Ordinal)
            .ToArray();

        return displays.Length == 0
            ? "<unknown>"
            : string.Join(", ", displays);
    }

    private string FormatCallableCandidate(SymbolId candidateId)
    {
        if (!candidateId.IsValid ||
            _symbolTable.GetSymbol(candidateId) is not FuncSymbol function)
        {
            return string.Empty;
        }

        if (_symbolTable.Modules.TryGetOwningModule(candidateId, out var module))
        {
            var moduleName = _symbolTable.Modules.FormatModuleFullName(module.Id);
            return string.IsNullOrWhiteSpace(moduleName)
                ? function.Name
                : moduleName + WellKnownStrings.Separators.Path + function.Name;
        }

        return function.Name;
    }

    private void ReportCallableResolutionFailure(
        SourceSpan span,
        string callableName,
        string syntaxKind,
        TypeDirectedCandidateResolution resolution,
        IReadOnlyList<Type> argumentTypes,
        string noMatchMessage)
    {
        if (resolution.IsAmbiguous)
        {
            AddStructuredErrorDiagnostic(
                CreateAmbiguousCallableDiagnostic(
                    span,
                    callableName,
                    syntaxKind,
                    resolution,
                    argumentTypes),
                span);
            return;
        }

        AddError(span, noMatchMessage);
    }

    private EidoscDiagnostic CreateAmbiguousCallableDiagnostic(
        SourceSpan span,
        string callableName,
        string syntaxKind,
        TypeDirectedCandidateResolution resolution,
        IReadOnlyList<Type> argumentTypes)
    {
        var candidateList = FormatCallableCandidateList(resolution.AmbiguousCandidateIds);
        var qualifiedPaths = resolution.AmbiguousCandidateIds
            .Select(FormatCallableCandidate)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        var candidateDetails = resolution.AmbiguousCandidateIds
            .Select(FormatCallableCandidateDetail)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        var diag = EidoscDiagnostic.Error(
                DiagnosticMessages.AmbiguousCallableOverload(callableName, candidateList),
                TypeErrorCode)
            .WithLabel(
                span,
                DiagnosticMessages.AmbiguousCallableOverload(callableName, candidateList))
            .WithNote($"call syntax: {syntaxKind}")
            .WithNote($"argument types: {FormatCallableArgumentTypeList(argumentTypes)}")
            .WithMetadata("resolution.kind", "callable-ambiguity")
            .WithMetadata("callable.name", callableName)
            .WithMetadata("callable.syntax", syntaxKind)
            .WithMetadata("callable.argumentTypes", FormatCallableArgumentTypeList(argumentTypes))
            .WithMetadata("callable.candidateCount", resolution.CandidateCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithMetadata("callable.viableCandidateCount", resolution.ViableCandidateCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithMetadata("callable.bestScore", resolution.BestScore.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithMetadata("callable.candidates", string.Join(" | ", qualifiedPaths));

        if (qualifiedPaths.Length > 0)
        {
            diag.WithMetadata("callable.qualifiedPaths", string.Join(" | ", qualifiedPaths));
            diag.WithNote($"qualified paths: {string.Join(", ", qualifiedPaths)}");
        }

        if (candidateDetails.Length > 0)
        {
            diag.WithMetadata("callable.candidateDetails", string.Join(" | ", candidateDetails));
        }

        foreach (var detail in candidateDetails)
        {
            diag.WithNote($"candidate: {detail}");
        }

        var suggestedQualifiedPaths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidateId in resolution.AmbiguousCandidateIds)
        {
            var qualifiedPath = FormatCallableCandidate(candidateId);
            if (string.IsNullOrWhiteSpace(qualifiedPath) ||
                !suggestedQualifiedPaths.Add(qualifiedPath))
            {
                continue;
            }

            diag.WithSuggestion(
                $"Use qualified path '{qualifiedPath}' to disambiguate '{callableName}'.",
                SuggestionKind.QualifySymbol,
                span,
                replacement: null,
                helpUrl: null,
                confidence: "high",
                requiresCleanTypes: false,
                originalSymbolId: candidateId.Value);
        }

        diag.WithSuggestion(
            $"Add a more specific type annotation so overload resolution can choose '{callableName}' unambiguously.",
            SuggestionKind.ChangeType,
            span,
            replacement: null,
            helpUrl: null,
            confidence: "medium",
            requiresCleanTypes: false);

        return diag;
    }

    private string FormatCallableArgumentTypeList(IEnumerable<Type> argumentTypes)
    {
        var displays = argumentTypes
            .Select(type => _substitution.Apply(type).ToString())
            .Where(static display => !string.IsNullOrWhiteSpace(display))
            .ToArray();
        return displays.Length == 0
            ? "<none>"
            : string.Join(", ", displays);
    }

    private string FormatCallableCandidateDetail(SymbolId candidateId)
    {
        var display = FormatCallableCandidate(candidateId);
        if (!candidateId.IsValid ||
            _symbolTable.GetSymbol(candidateId) is not FuncSymbol function)
        {
            return display;
        }

        var signature = TryGetCallableCandidateSignature(candidateId, out var functionType)
            ? functionType.ToString()
            : null;
        var origin = TryFormatCallableCandidateOrigin(function);

        return (signature, origin) switch
        {
            ({ Length: > 0 }, { Length: > 0 }) => $"{display} :: {signature} [{origin}]",
            ({ Length: > 0 }, _) => $"{display} :: {signature}",
            (_, { Length: > 0 }) => $"{display} [{origin}]",
            _ => display
        };
    }

    private bool TryGetCallableCandidateSignature(SymbolId candidateId, out Type functionType)
    {
        var trial = _substitution.Clone();
        if (TryGetFunctionTypeForTrial(candidateId, trial, out functionType))
        {
            functionType = trial.Apply(functionType);
            return true;
        }

        functionType = CreateErrorRecoveryType();
        return false;
    }

    private string TryFormatCallableCandidateOrigin(FuncSymbol function)
    {
        var originParts = new List<string>();
        var moduleName = TryFormatQualifiedSymbolName(function.Id);
        if (!string.IsNullOrWhiteSpace(moduleName))
        {
            var lastSeparator = moduleName.LastIndexOf(WellKnownStrings.Separators.Path, StringComparison.Ordinal);
            if (lastSeparator > 0)
            {
                originParts.Add($"module={moduleName[..lastSeparator]}");
            }
        }

        if (function.OwnerTrait is { IsValid: true } ownerTraitId)
        {
            var traitName = TryFormatQualifiedSymbolName(ownerTraitId);
            if (!string.IsNullOrWhiteSpace(traitName))
            {
                originParts.Add($"trait={traitName}");
            }
        }

        return string.Join(", ", originParts);
    }

    private string TryFormatQualifiedSymbolName(SymbolId symbolId)
    {
        if (!symbolId.IsValid ||
            _symbolTable.GetSymbol(symbolId) is not { } symbol)
        {
            return string.Empty;
        }

        if (_symbolTable.Modules.TryGetOwningModule(symbolId, out var module))
        {
            var moduleName = _symbolTable.Modules.FormatModuleFullName(module.Id);
            return string.IsNullOrWhiteSpace(moduleName)
                ? symbol.Name
                : moduleName + WellKnownStrings.Separators.Path + symbol.Name;
        }

        return symbol.Name;
    }

    private readonly record struct TypeDirectedCandidateResolution(
        SymbolId SelectedSymbolId,
        IReadOnlyList<SymbolId> AmbiguousCandidateIds,
        int CandidateCount,
        int ViableCandidateCount,
        int BestScore,
        bool IsResolved,
        bool IsAmbiguous)
    {
        public static TypeDirectedCandidateResolution NoMatch(int candidateCount, int viableCandidateCount) =>
            new(
                SymbolId.None,
                [],
                candidateCount,
                viableCandidateCount,
                int.MinValue,
                IsResolved: false,
                IsAmbiguous: false);

        public static TypeDirectedCandidateResolution Resolved(
            SymbolId selectedSymbolId,
            int candidateCount,
            int viableCandidateCount,
            int bestScore) =>
            new(
                selectedSymbolId,
                [],
                candidateCount,
                viableCandidateCount,
                bestScore,
                IsResolved: true,
                IsAmbiguous: false);

        public static TypeDirectedCandidateResolution Ambiguous(
            IReadOnlyList<SymbolId> ambiguousCandidateIds,
            int candidateCount,
            int viableCandidateCount,
            int bestScore) =>
            new(
                SymbolId.None,
                ambiguousCandidateIds.ToArray(),
                candidateCount,
                viableCandidateCount,
                bestScore,
                IsResolved: false,
                IsAmbiguous: true);
    }
}
