using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Borrow;

// Loan diagnostics, alias tracking, constraint result types
public sealed partial class LoanConstraintVerifier
{


    private LoanConstraintResult ValidateMoveOfOwnedTarget(
        BorrowTarget borrowTarget,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        var localId = borrowTarget.BaseLocal;
        if (state.MovedVars.Contains(localId))
        {
            AddDiagnostic(new BorrowDiagnostic
            {
                Kind = BorrowErrorKind.UseAfterMove,
                Message = DiagnosticMessages.BorrowUseAfterMove,
                Span = span,
                Location = (blockId, instructionIndex),
                Hint = DiagnosticMessages.BorrowMoveOrCopyHint
            });

            return LoanConstraintResult.Failure(
                LoanConstraintViolation.UseAfterMove,
                DiagnosticMessages.BorrowUseAfterMoveCannotUse,
                span,
                hint: DiagnosticMessages.BorrowMoveOrCopyHint);
        }

        var activeBorrows = GetActiveBorrows(borrowTarget, state);
        if (activeBorrows.Count == 0)
        {
            return LoanConstraintResult.Success();
        }

        var conflict = activeBorrows[0];
        var hint = BorrowAliasTrace.BuildConflictHint(
            conflict.AliasTrace,
            conflict.TraceId,
            DiagnosticMessages.BorrowWaitEndsBeforeMove);

        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.MutateWhileBorrowed,
            Message = DiagnosticMessages.BorrowCannotMoveBorrowedValue,
            Span = span,
            RelatedSpan = conflict.Span,
            Location = (blockId, instructionIndex),
            RelatedLocation = conflict.Location,
            RelatedAliasTrace = [.. conflict.AliasTrace],
            RelatedAliasTraceId = conflict.TraceId,
            Hint = hint
        });

        return LoanConstraintResult.Failure(
            LoanConstraintViolation.MutateWhileBorrowed,
            DiagnosticMessages.BorrowCannotMoveBorrowedValue,
            span,
            conflict.Span,
            hint);
    }

    private LoanConstraintResult ValidateReadLocal(
        LocalId localId,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult>? results)
    {
        if (!state.MovedVars.Contains(localId))
        {
            return LoanConstraintResult.Success();
        }

        var result = LoanConstraintResult.Failure(
            LoanConstraintViolation.UseAfterMove,
            DiagnosticMessages.BorrowUseAfterMoveCannotRead,
            span,
            hint: DiagnosticMessages.BorrowReadBeforeMoveHint);

        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.UseAfterMove,
            Message = DiagnosticMessages.BorrowUseAfterMove,
            Span = span,
            Location = (blockId, instructionIndex),
            Hint = result.Hint
        });

        results?.Add(result);
        return result;
    }

    private void ValidateReadOperand(
        MirOperand operand,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var localId })
        {
            ValidateReadLocal(localId, span, blockId, instructionIndex, state, results);
        }
    }

    private void AddBorrow(
        BorrowTarget borrowTarget,
        LocalId borrowee,
        LocalId borrower,
        bool isMutable,
        LifetimeId lifetime,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        ActiveBorrowInfo? sourceBorrow = null,
        string? traceStep = null,
        string? originSummary = null)
    {
        CheckBorrowConflict(borrowTarget, isMutable, span, blockId, instructionIndex, state);

        var effectiveOriginLocation = sourceBorrow?.OriginLocation ?? (blockId, instructionIndex);
        var effectiveOriginSummary = sourceBorrow?.OriginSummary ?? originSummary ?? "";
        var effectiveTrace = sourceBorrow != null
            ? new List<string>(sourceBorrow.AliasTrace)
            : [];

        if (!string.IsNullOrEmpty(traceStep))
        {
            effectiveTrace.Add(traceStep);
        }
        else if (effectiveTrace.Count == 0 && !string.IsNullOrEmpty(effectiveOriginSummary))
        {
            effectiveTrace.Add(effectiveOriginSummary);
        }

        var canReuseSourceTraceId =
            sourceBorrow != null &&
            !string.IsNullOrEmpty(sourceBorrow.TraceId) &&
            string.IsNullOrEmpty(traceStep);
        var traceId = canReuseSourceTraceId
            ? sourceBorrow!.TraceId
            : BorrowAliasTrace.BuildTraceId(
                effectiveOriginLocation,
                effectiveTrace,
                borrower,
                borrowee,
                borrowTarget,
                isMutable);

        var borrow = new ActiveBorrowInfo
        {
            Borrower = borrower,
            Borrowee = borrowee,
            BorrowTarget = borrowTarget,
            IsMutable = isMutable,
            Lifetime = lifetime,
            Span = span,
            Location = (blockId, instructionIndex),
            OriginLocation = effectiveOriginLocation,
            OriginSummary = effectiveOriginSummary,
            AliasTrace = effectiveTrace,
            TraceId = traceId
        };

        if (!state.TryAddBorrow(borrow))
        {
            return;
        }

        state.MovedVars.Remove(borrower);
    }

    private void CheckBorrowConflict(
        BorrowTarget borrowTarget,
        bool isNewMutable,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        var existingBorrows = state.GetBorrowsByBorrowTarget(borrowTarget);

        foreach (var existing in existingBorrows)
        {
            if (isNewMutable)
            {
                var conflictKey = $"{borrowTarget.StableKey}:{existing.Location.Block.Value}:{existing.Location.Index}:mut";
                if (!_reportedConflicts.Add(conflictKey))
                {
                    continue;
                }

                AddDiagnostic(new BorrowDiagnostic
                {
                    Kind = existing.IsMutable
                        ? BorrowErrorKind.MultipleMutableBorrows
                        : BorrowErrorKind.MutableWhileImmutableBorrowed,
                    Message = DiagnosticMessages.BorrowCreateMutableConflict,
                    Span = span,
                    RelatedSpan = existing.Span,
                    Location = (blockId, instructionIndex),
                    RelatedLocation = existing.Location,
                    RelatedAliasTrace = [.. existing.AliasTrace],
                    RelatedAliasTraceId = existing.TraceId,
                    Hint = BorrowAliasTrace.BuildConflictHint(
                        existing.AliasTrace,
                        existing.TraceId,
                        null)
                });
            }
            else if (existing.IsMutable)
            {
                var conflictKey = $"{borrowTarget.StableKey}:{existing.Location.Block.Value}:{existing.Location.Index}:shared";
                if (!_reportedConflicts.Add(conflictKey))
                {
                    continue;
                }

                AddDiagnostic(new BorrowDiagnostic
                {
                    Kind = BorrowErrorKind.ImmutableWhileMutableBorrowed,
                    Message = DiagnosticMessages.BorrowCreateSharedWhileMutable,
                    Span = span,
                    RelatedSpan = existing.Span,
                    Location = (blockId, instructionIndex),
                    RelatedLocation = existing.Location,
                    RelatedAliasTrace = [.. existing.AliasTrace],
                    RelatedAliasTraceId = existing.TraceId,
                    Hint = BorrowAliasTrace.BuildConflictHint(
                        existing.AliasTrace,
                        existing.TraceId,
                        null)
                });
            }
        }
    }

    private void EndBorrowsForTarget(
        MirPlace place,
        BorrowTarget target,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        if (place.Kind == PlaceKind.Local && IsBorrower(place.Local, state))
        {
            EndBorrowsByBorrower(place.Local, blockId, instructionIndex, state);
        }
        else
        {
            EndBorrowsByBorrowTarget(target, blockId, instructionIndex, state);
        }
    }

    private void EndBorrowsByBorrower(
        LocalId localId,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        state.EndBorrowsByBorrower(localId, (blockId, instructionIndex));
    }

    private void EndBorrowsByBorrowee(
        LocalId localId,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        state.EndBorrowsByBorrowee(localId, (blockId, instructionIndex));
    }

    private void EndBorrowsByBorrowTarget(
        BorrowTarget target,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        state.EndBorrowsByBorrowTarget(target, (blockId, instructionIndex));
    }

    private IReadOnlyList<BorrowTarget> ResolveBorrowTargets(LocalId localId, LoanVerifierState state)
    {
        return state.ResolveBorrowTargetPaths(localId);
    }

    private IReadOnlyList<BorrowTarget> ResolveBorrowTargetPaths(LocalId localId, LoanVerifierState state)
    {
        return state.ResolveBorrowTargetPaths(localId);
    }

    private bool IsBorrower(LocalId localId, LoanVerifierState state)
    {
        return state.IsBorrower(localId);
    }

    private List<ActiveBorrowInfo> GetBorrowsByBorrower(LocalId localId, LoanVerifierState state)
    {
        return state.GetBorrowsByBorrower(localId);
    }

    private List<ActiveBorrowInfo> GetActiveBorrows(BorrowTarget target, LoanVerifierState state)
    {
        return state.GetBorrowsByBorrowTarget(target);
    }

    private LoanVerifierState GetIncomingState(
        MirFunc function,
        BlockId blockId,
        ControlFlowGraph cfg,
        Dictionary<BlockId, LoanVerifierState> blockOutStates,
        IReadOnlyDictionary<(BlockId Predecessor, BlockId Successor), HashSet<LocalId>> oneShotBackedgeSuppressions)
    {
        if (blockId.Equals(function.EntryBlockId))
        {
            return LoanVerifierState.Empty(_localIndexMap, _localsByIndex);
        }

        var predecessors = cfg.GetPredecessors(blockId);
        if (predecessors.Count == 0)
        {
            return LoanVerifierState.Empty(_localIndexMap, _localsByIndex);
        }

        LoanVerifierState? mergedState = null;

        foreach (var predecessor in predecessors)
        {
            if (!blockOutStates.TryGetValue(predecessor, out var predecessorState))
            {
                continue;
            }

            var incomingState = ApplyOneShotBackedgeSuppressions(
                predecessor,
                blockId,
                predecessorState,
                oneShotBackedgeSuppressions);

            if (mergedState == null)
            {
                mergedState = incomingState;
                continue;
            }

            mergedState.UnionBorrowsFrom(incomingState);
            mergedState.MovedVars.UnionWith(incomingState.MovedVars);
        }

        return mergedState ?? LoanVerifierState.Empty(_localIndexMap, _localsByIndex);
    }

    private static LoanVerifierState ApplyOneShotBackedgeSuppressions(
        BlockId predecessor,
        BlockId successor,
        LoanVerifierState predecessorState,
        IReadOnlyDictionary<(BlockId Predecessor, BlockId Successor), HashSet<LocalId>> oneShotBackedgeSuppressions)
    {
        var incomingState = predecessorState.Clone();
        if (!oneShotBackedgeSuppressions.TryGetValue((predecessor, successor), out var locals))
        {
            return incomingState;
        }

        foreach (var local in locals)
        {
            incomingState.MovedVars.Remove(local);
        }

        return incomingState;
    }

    private void AddDiagnostic(BorrowDiagnostic diagnostic)
    {
        var key = BorrowAliasTrace.BuildDiagnosticDedupKey(diagnostic);
        if (!_reportedDiagnostics.Add(key))
        {
            return;
        }

        Diagnostics.Add(diagnostic);
    }

    private bool ShouldDetachBorrowAliasOnCopy(MirFunc function, LocalId targetLocal)
    {
        _ = function;
        return _localsById.TryGetValue(targetLocal, out var local) &&
               CopyTypeSemantics.IsCopyType(local.TypeId, _hasCopyImplResolver, _dynamicTypeKeys);
    }

    private void InitializeFunctionLocals(MirFunc function)
    {
        if (ReferenceEquals(_currentFunction, function))
        {
            return;
        }

        _currentFunction = function;
        _localsById = function.Locals.ToDictionary(local => local.Id);
        var localIndexMap = new Dictionary<LocalId, int>(function.Locals.Count);
        var localsByIndex = new LocalId[function.Locals.Count];
        for (int i = 0; i < function.Locals.Count; i++)
        {
            var localId = function.Locals[i].Id;
            localIndexMap[localId] = i;
            localsByIndex[i] = localId;
        }

        _localIndexMap = localIndexMap;
        _localsByIndex = localsByIndex;
    }

}
