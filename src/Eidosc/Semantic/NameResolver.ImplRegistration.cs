using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Utils;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void TryRegisterTraitImplFromClauses(FuncDef func)
    {
        // Trait 内部的方法声明不应参与 impl 注册——它们是 trait 签名的一部分。
        if (_traitSignatureDepth > 0)
        {
            return;
        }

        var hasImplClause = false;
        foreach (var clause in func.Clauses)
        {
            if (clause.ClauseKind != DeclarationClauseKind.Impl)
            {
                continue;
            }

            hasImplClause = true;
            if (!_processedImplClauses.Add(clause))
            {
                continue;
            }

            if (!TryResolveTraitFromImplClause(clause, out var traitId, out var traitName, out var traitRef))
            {
                var name = string.IsNullOrWhiteSpace(traitName) ? "<missing>" : traitName;
                AddUndefinedImplTraitError(clause.Span, traitRef, DiagnosticMessages.UndefinedTraitInImpl(name));
                continue;
            }

            if (!TryGetImplTargetType(func, out var implementingTypePath, out var targetTypeId))
            {
                AddError(clause.Span, DiagnosticMessages.ImplRequiresConcreteFirstParameter);
                continue;
            }

            if (!TryBuildImplTypeRequirements(
                    func,
                    implementingTypePath,
                    out var implementingTypeRequirements,
                    out var requirementError))
            {
                AddError(clause.Span, requirementError ?? DiagnosticMessages.UnsupportedConstrainedImplHead);
                continue;
            }

            var isProofOnlyMarkerImpl = _traitDefinitions.TryGetValue(traitId, out var traitDefinition) &&
                                        traitDefinition.Methods.Count == 0;
            var matchedTraitMethodId = SymbolId.None;
            if (!isProofOnlyMarkerImpl &&
                !TryValidateTraitImplCompatibility(
                    traitId,
                    func,
                    implementingTypePath,
                    traitRef.TypeArgTexts,
                    out var reason,
                    out matchedTraitMethodId))
            {
                AddError(clause.Span, reason);
                continue;
            }

            var canonicalTraitTypeArgs = CanonicalizeImplTraitTypeArgs(traitRef);
            var traitTypeArgKeys = BuildImplTraitTypeArgKeys(traitId, traitRef);
            var canonicalTraitTypeArgKeys = BuildCanonicalImplTraitTypeArgKeys(
                canonicalTraitTypeArgs,
                traitTypeArgKeys);
            var implementingTypeKey = BuildImplTypeRefKey(implementingTypePath);
            var implementingTypeDisplay = NormalizeTypePath(implementingTypePath, selfType: null, traitTypeArgBindings: null);
            var canonicalImplementingType = CanonicalizeTypePathForImplHead(implementingTypePath);
            var requestedHeadShape = BuildCanonicalImplHeadShape(
                traitId,
                traitRef.GenericArguments.Count > 0 ? [] : traitRef.TypeArgs,
                implementingTypePath,
                canonicalTraitTypeArgs,
                canonicalTraitTypeArgKeys,
                canonicalImplementingType);
            if (TryGetConflictingImplRegistration(
                    traitId,
                    targetTypeId,
                    canonicalImplementingType,
                    canonicalTraitTypeArgs,
                    traitRef.TypeArgTexts,
                    requestedHeadShape,
                    out var conflictingImpl))
            {
                var traitDisplay = FormatTraitReferenceDisplay(traitRef);
                var requestedHead = FormatImplHeadDisplay(traitDisplay, implementingTypeDisplay);
                var conflictingHead = FormatImplHeadDisplay(
                    BuildTraitDisplay(GetTraitName(conflictingImpl!.Trait), conflictingImpl.TraitTypeArgs),
                    string.IsNullOrWhiteSpace(conflictingImpl.ImplementingTypeDisplay)
                        ? implementingTypeDisplay
                        : conflictingImpl.ImplementingTypeDisplay);
                var requestedCanonical = FormatImplHeadDisplay(
                    BuildTraitDisplay(GetTraitName(traitId), canonicalTraitTypeArgs),
                    canonicalImplementingType);
                var conflictingCanonical = FormatImplHeadDisplay(
                    BuildTraitDisplay(GetTraitName(conflictingImpl.Trait), conflictingImpl.CanonicalTraitTypeArgs),
                    string.IsNullOrWhiteSpace(conflictingImpl.CanonicalImplementingType)
                        ? canonicalImplementingType
                        : conflictingImpl.CanonicalImplementingType);
                var conflictingHeadShape = BuildImplHeadShape(conflictingImpl);
                var specializationRelation = ImplSpecializationComparer.CompareHeads(requestedHeadShape, conflictingHeadShape);
                _diagnostics.Add(
                    BuildOverlappingImplRegistrationDiagnostic(
                        clause.Span,
                        requestedHead,
                        conflictingImpl,
                        conflictingHead,
                        requestedCanonical,
                        conflictingCanonical,
                        specializationRelation));
                continue;
            }

            var implId = _symbolTable.DeclareImpl(
                traitId,
                targetTypeId,
                clause.Span,
                traitRef.TypeArgTexts,
                implementingTypeDisplay,
                canonicalImplementingType,
                canonicalTraitTypeArgs,
                traitTypeArgKeys,
                canonicalTraitTypeArgKeys,
                implementingTypeRequirements,
                requestedHeadShape,
                implementingTypeKey);
            if (implId.IsValid && func.SymbolId.IsValid && matchedTraitMethodId.IsValid)
            {
                _symbolTable.AddMethodToImpl(implId, func.SymbolId, matchedTraitMethodId);
            }
        }

        if (!hasImplClause &&
            (!func.SymbolId.IsValid || _processedConventionImplFunctions.Add(func.SymbolId)))
        {
            TryRegisterTraitImplByConvention(func);
        }
    }

    private bool TryGetConflictingImplRegistration(
        SymbolId traitId,
        TypeId implementingTypeId,
        string canonicalImplementingType,
        IReadOnlyList<string> canonicalTraitTypeArgs,
        IReadOnlyList<string> requestedTraitTypeArgs,
        ImplHeadShape requestedHeadShape,
        out ImplSymbol? conflictingImpl)
    {
        conflictingImpl = null;
        if (!traitId.IsValid || !implementingTypeId.IsValid || string.IsNullOrWhiteSpace(canonicalImplementingType))
        {
            return false;
        }

        var normalizedRequestedArgs = NormalizeTraitTypeArgsForImplRegistration(canonicalTraitTypeArgs);
        var normalizedRequestedTextArgs = NormalizeTraitTypeArgsForImplRegistration(requestedTraitTypeArgs);
        var queryKey = CreateImplOverlapQueryKey(
            traitId,
            canonicalImplementingType,
            normalizedRequestedArgs,
            normalizedRequestedTextArgs);
        var traitImpls = _symbolTable.GetImplsForTrait(traitId);
        AddCounter("Namer.implOverlap.traitBuckets");
        AddCounter("Namer.implOverlap.candidateImpls", traitImpls.Count);
        var candidateSetFingerprint = CreateImplOverlapCandidateSetFingerprint(traitImpls);
        if (TryRestoreCleanImplOverlapResult(
                queryKey,
                traitId,
                canonicalImplementingType,
                normalizedRequestedArgs,
                normalizedRequestedTextArgs,
                traitImpls.Count,
                candidateSetFingerprint))
        {
            return false;
        }

        if (TryRestoreConflictingImplOverlapResult(
                queryKey,
                traitId,
                canonicalImplementingType,
                normalizedRequestedArgs,
                normalizedRequestedTextArgs,
                requestedHeadShape,
                traitImpls,
                candidateSetFingerprint,
                out conflictingImpl))
        {
            return true;
        }

        var nonOverlappingCandidates = 0;
        var specializationAllowedCandidates = 0;
        foreach (var impl in traitImpls)
        {
            var normalizedExistingArgs = impl.CanonicalTraitTypeArgs.Count > 0
                ? NormalizeTraitTypeArgsForImplRegistration(impl.CanonicalTraitTypeArgs)
                : NormalizeTraitTypeArgsForImplRegistration(impl.TraitTypeArgs);
            if (normalizedExistingArgs.SequenceEqual(normalizedRequestedArgs))
            {
                var normalizedExistingTextArgs = NormalizeTraitTypeArgsForImplRegistration(impl.TraitTypeArgs);
                if (normalizedExistingTextArgs.SequenceEqual(normalizedRequestedTextArgs) &&
                    impl.ImplementingType == implementingTypeId)
                {
                    continue;
                }
            }

            var existingHeadShape = BuildImplHeadShape(impl);
            if (!ImplSpecializationComparer.MayOverlap(requestedHeadShape, existingHeadShape))
            {
                nonOverlappingCandidates++;
                AddCounter("Namer.implOverlap.nonOverlappingCandidates");
                continue;
            }

            var relation = ImplSpecializationComparer.CompareHeads(requestedHeadShape, existingHeadShape);
            if (relation is ImplSpecializationRelation.MoreSpecific or ImplSpecializationRelation.LessSpecific)
            {
                specializationAllowedCandidates++;
                AddCounter("Namer.implOverlap.specializationAllowedCandidates");
                continue;
            }

            conflictingImpl = impl;
            AddCounter("Namer.implOverlap.conflicts");
            RecordImplOverlapCheckSnapshotEntry(
                queryKey,
                traitId,
                canonicalImplementingType,
                normalizedRequestedArgs,
                normalizedRequestedTextArgs,
                traitImpls.Count,
                candidateSetFingerprint,
                nonOverlappingCandidates,
                specializationAllowedCandidates,
                hasConflict: true,
                conflictingImpl,
                relation);
            return true;
        }

        RecordImplOverlapCheckSnapshotEntry(
            queryKey,
            traitId,
            canonicalImplementingType,
            normalizedRequestedArgs,
            normalizedRequestedTextArgs,
            traitImpls.Count,
            candidateSetFingerprint,
            nonOverlappingCandidates,
            specializationAllowedCandidates,
            hasConflict: false,
            conflictingImpl: null,
            specializationRelation: null);
        return false;
    }

    private bool TryRestoreConflictingImplOverlapResult(
        string queryKey,
        SymbolId traitId,
        string canonicalImplementingType,
        IReadOnlyList<string> normalizedRequestedArgs,
        IReadOnlyList<string> normalizedRequestedTextArgs,
        ImplHeadShape requestedHeadShape,
        IReadOnlyList<ImplSymbol> traitImpls,
        string candidateSetFingerprint,
        out ImplSymbol? conflictingImpl)
    {
        conflictingImpl = null;
        if (!_previousImplOverlapSnapshotEntries.TryGetValue(queryKey, out var previous) ||
            !previous.HasConflict ||
            previous.CandidateCount != traitImpls.Count ||
            !string.Equals(previous.CandidateSetFingerprint, candidateSetFingerprint, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(previous.ConflictingImplKey))
        {
            return false;
        }

        var candidate = traitImpls.FirstOrDefault(impl =>
            string.Equals(CreateImplOverlapImplKey(impl), previous.ConflictingImplKey, StringComparison.Ordinal));
        if (candidate == null)
        {
            AddCounter("Namer.implOverlapPreviousSnapshot.conflictRestoreMisses");
            return false;
        }

        var existingHeadShape = BuildImplHeadShape(candidate);
        if (!ImplSpecializationComparer.MayOverlap(requestedHeadShape, existingHeadShape))
        {
            AddCounter("Namer.implOverlapPreviousSnapshot.conflictRestoreStaleHits");
            return false;
        }

        var relation = ImplSpecializationComparer.CompareHeads(requestedHeadShape, existingHeadShape);
        if (relation is ImplSpecializationRelation.MoreSpecific or ImplSpecializationRelation.LessSpecific)
        {
            AddCounter("Namer.implOverlapPreviousSnapshot.conflictRestoreStaleHits");
            return false;
        }

        conflictingImpl = candidate;
        AddCounter("Namer.implOverlapPreviousSnapshot.conflictRestoreHits");
        AddCounter("Namer.implOverlap.conflicts");
        RecordImplOverlapCheckSnapshotEntry(
            queryKey,
            traitId,
            canonicalImplementingType,
            normalizedRequestedArgs,
            normalizedRequestedTextArgs,
            traitImpls.Count,
            candidateSetFingerprint,
            previous.NonOverlappingCandidateCount,
            previous.SpecializationAllowedCandidateCount,
            hasConflict: true,
            conflictingImpl,
            relation);
        return true;
    }

    private bool TryRestoreCleanImplOverlapResult(
        string queryKey,
        SymbolId traitId,
        string canonicalImplementingType,
        IReadOnlyList<string> normalizedRequestedArgs,
        IReadOnlyList<string> normalizedRequestedTextArgs,
        int candidateCount,
        string candidateSetFingerprint)
    {
        if (!_previousImplOverlapSnapshotEntries.TryGetValue(queryKey, out var previous) ||
            previous.HasConflict ||
            previous.CandidateCount != candidateCount ||
            !string.Equals(previous.CandidateSetFingerprint, candidateSetFingerprint, StringComparison.Ordinal))
        {
            return false;
        }

        AddCounter("Namer.implOverlapPreviousSnapshot.restoreHits");
        RecordImplOverlapCheckSnapshotEntry(
            queryKey,
            traitId,
            canonicalImplementingType,
            normalizedRequestedArgs,
            normalizedRequestedTextArgs,
            candidateCount,
            candidateSetFingerprint,
            previous.NonOverlappingCandidateCount,
            previous.SpecializationAllowedCandidateCount,
            hasConflict: false,
            conflictingImpl: null,
            specializationRelation: null);
        return true;
    }

    private void RecordImplOverlapCheckSnapshotEntry(
        string queryKey,
        SymbolId traitId,
        string canonicalImplementingType,
        IReadOnlyList<string> normalizedRequestedArgs,
        IReadOnlyList<string> normalizedRequestedTextArgs,
        int candidateCount,
        string candidateSetFingerprint,
        int nonOverlappingCandidates,
        int specializationAllowedCandidates,
        bool hasConflict,
        ImplSymbol? conflictingImpl,
        ImplSpecializationRelation? specializationRelation)
    {
        var entry = new ImplOverlapCheckSnapshotEntry(
            queryKey,
            CreateImplOverlapTraitKey(traitId),
            canonicalImplementingType,
            string.Join(",", normalizedRequestedArgs),
            string.Join(",", normalizedRequestedTextArgs),
            candidateCount,
            candidateSetFingerprint,
            nonOverlappingCandidates,
            specializationAllowedCandidates,
            hasConflict,
            conflictingImpl == null ? null : CreateImplOverlapImplKey(conflictingImpl),
            specializationRelation?.ToString());
        _implOverlapSnapshotEntries[queryKey] = entry;

        if (!_previousImplOverlapSnapshotEntries.TryGetValue(queryKey, out var previous))
        {
            AddCounter("Namer.implOverlapPreviousSnapshot.misses");
            return;
        }

        AddCounter("Namer.implOverlapPreviousSnapshot.hits");
        var validated =
            previous.CandidateCount == entry.CandidateCount &&
            string.Equals(previous.CandidateSetFingerprint, entry.CandidateSetFingerprint, StringComparison.Ordinal) &&
            previous.NonOverlappingCandidateCount == entry.NonOverlappingCandidateCount &&
            previous.SpecializationAllowedCandidateCount == entry.SpecializationAllowedCandidateCount &&
            previous.HasConflict == entry.HasConflict &&
            string.Equals(previous.ConflictingImplKey, entry.ConflictingImplKey, StringComparison.Ordinal) &&
            string.Equals(previous.SpecializationRelation, entry.SpecializationRelation, StringComparison.Ordinal);
        AddCounter(validated
            ? "Namer.implOverlapPreviousSnapshot.validatedHits"
            : "Namer.implOverlapPreviousSnapshot.staleHits");
    }

    private string CreateImplOverlapQueryKey(
        SymbolId traitId,
        string canonicalImplementingType,
        IReadOnlyList<string> normalizedRequestedArgs,
        IReadOnlyList<string> normalizedRequestedTextArgs)
    {
        return string.Join(
            "|",
            CreateImplOverlapTraitKey(traitId),
            canonicalImplementingType,
            string.Join(",", normalizedRequestedArgs),
            string.Join(",", normalizedRequestedTextArgs));
    }

    private string CreateImplOverlapImplKey(ImplSymbol impl)
    {
        return string.Join(
            "|",
            CreateImplOverlapTraitKey(impl.Trait),
            impl.CanonicalImplementingType,
            string.Join(",", impl.CanonicalTraitTypeArgs),
            FormatStableImplTypeRefKey(impl.ImplementingTypeKey),
            string.Join(",", impl.CanonicalTraitTypeArgKeys.Select(FormatStableImplTypeRefKey)));
    }

    private string CreateImplOverlapCandidateSetFingerprint(IReadOnlyList<ImplSymbol> candidates)
    {
        return string.Join(
            ";",
            candidates
                .Select(CreateImplOverlapImplKey)
                .OrderBy(static key => key, StringComparer.Ordinal));
    }

    private string CreateImplOverlapTraitKey(SymbolId traitId)
    {
        if (!traitId.IsValid)
        {
            return string.Empty;
        }

        var traitName = GetStableQualifiedSymbolKey(traitId);
        return string.IsNullOrWhiteSpace(traitName)
            ? $"trait#{traitId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : traitName;
    }

    private string GetStableQualifiedSymbolKey(SymbolId symbolId)
    {
        if (!symbolId.IsValid ||
            _symbolTable.GetSymbol(symbolId) is not { } symbol)
        {
            return string.Empty;
        }

        if (_symbolTable.Modules.TryGetOwningModule(symbolId, out var module))
        {
            return $"{ModuleRegistry.FormatModuleFullName(module)}{WellKnownStrings.Separators.Path}{symbol.Name}";
        }

        return symbol.Name;
    }

    private string FormatStableImplTypeRefKey(ImplTypeRefKey key)
    {
        if (key.IsEmpty)
        {
            return string.Empty;
        }

        if (key.ValueArgument is { } valueArgument)
        {
            var identity = valueArgument.IsConcrete
                ? valueArgument.CanonicalPayload
                : string.IsNullOrWhiteSpace(valueArgument.DisplayText)
                    ? valueArgument.VariableIdentity
                    : $"param:{valueArgument.DisplayText}";
            return $"value:{valueArgument.ParameterIndex}:{valueArgument.TypeId.Value}:{identity}";
        }

        var head = FormatStableImplTypeRefKeyHead(key);
        if (key.TypeArguments.IsDefaultOrEmpty)
        {
            return head;
        }

        return $"{head}[{string.Join(",", key.TypeArguments.Select(FormatStableImplTypeRefKey))}]";
    }

    private string FormatStableImplTypeRefKeyHead(ImplTypeRefKey key)
    {
        if (!string.IsNullOrWhiteSpace(key.Text))
        {
            return key.Text;
        }

        if (key.TypeId.IsValid &&
            _symbolTable.GetSymbolByTypeId(key.TypeId) is { } typeSymbol)
        {
            return GetStableQualifiedSymbolKey(typeSymbol.Id);
        }

        if (key.SymbolId.IsValid)
        {
            return GetStableQualifiedSymbolKey(key.SymbolId);
        }

        if (key.TypeId.IsValid)
        {
            return $"type:{key.TypeId.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
        }

        return string.Empty;
    }

    private static List<string> NormalizeTraitTypeArgsForImplRegistration(IReadOnlyList<string>? traitTypeArgs)
    {
        if (traitTypeArgs == null || traitTypeArgs.Count == 0)
        {
            return [];
        }

        var normalized = new List<string>(traitTypeArgs.Count);
        foreach (var traitTypeArg in traitTypeArgs)
        {
            var text = traitTypeArg?.Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                normalized.Add(text);
            }
        }

        return normalized;
    }

    private string GetTraitName(SymbolId traitId)
    {
        return _symbolTable.GetSymbol(traitId)?.Name ?? $"trait#{traitId.Value}";
    }

    private static string BuildTraitDisplay(string traitName, IReadOnlyList<string> traitTypeArgs)
    {
        var normalizedName = string.IsNullOrWhiteSpace(traitName) ? "<trait>" : traitName;
        return traitTypeArgs.Count == 0
            ? normalizedName
            : $"{normalizedName}[{string.Join(", ", traitTypeArgs)}]";
    }

    private static string FormatImplHeadDisplay(string traitDisplay, string implementingTypeDisplay)
    {
        var typeDisplay = string.IsNullOrWhiteSpace(implementingTypeDisplay) ? "<type>" : implementingTypeDisplay;
        return $"@impl({traitDisplay}) on {typeDisplay}";
    }

    private EidoscDiagnostic BuildOverlappingImplRegistrationDiagnostic(
        SourceSpan requestedSpan,
        string requestedHead,
        ImplSymbol conflictingImpl,
        string conflictingHead,
        string requestedCanonical,
        string conflictingCanonical,
        ImplSpecializationRelation specializationRelation)
    {
        var diagnostic = new EidoscDiagnostic(
            EidoscDiagnosticLevel.Error,
            DiagnosticMessages.AmbiguousOverlappingImplRegistration,
            OverlappingImplRegistrationCode);
        diagnostic.WithLabel(requestedSpan, DiagnosticMessages.OverlappingImplRequestedHere(requestedHead));
        diagnostic.WithNote(DiagnosticMessages.OverlappingImplRequestedHead(requestedHead));
        diagnostic.WithNote(DiagnosticMessages.OverlappingImplExistingHead(conflictingHead));
        diagnostic.WithNote(DiagnosticMessages.OverlappingImplRequestedCanonicalHead(requestedCanonical));
        diagnostic.WithNote(DiagnosticMessages.OverlappingImplExistingCanonicalHead(conflictingCanonical));
        diagnostic.WithNote(DiagnosticMessages.OverlappingImplSpecializationRelation(FormatImplSpecializationRelation(specializationRelation)));
        diagnostic.WithHelp(DiagnosticMessages.OverlappingImplHelp);

        if (conflictingImpl.Span.Length > 0 || conflictingImpl.Span.Position > 0 || !string.IsNullOrWhiteSpace(conflictingImpl.Span.FilePath))
        {
            diagnostic.WithRelated(
                EidoscDiagnostic.Note(DiagnosticMessages.ExistingOverlappingImplRegisteredHere)
                    .WithLabel(conflictingImpl.Span, conflictingHead));
        }

        return diagnostic;
    }

    private static string FormatImplSpecializationRelation(ImplSpecializationRelation relation)
    {
        return relation switch
        {
            ImplSpecializationRelation.Equivalent => DiagnosticMessages.ImplSpecializationEquivalent,
            ImplSpecializationRelation.MoreSpecific => DiagnosticMessages.ImplSpecializationRequestedMoreSpecific,
            ImplSpecializationRelation.LessSpecific => DiagnosticMessages.ImplSpecializationRequestedLessSpecific,
            _ => DiagnosticMessages.ImplSpecializationIncomparable
        };
    }

}
