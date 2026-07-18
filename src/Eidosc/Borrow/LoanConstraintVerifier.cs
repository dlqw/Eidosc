using Eidosc.Symbols;
using Eidosc.Diagnostic;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Borrow;

public enum LoanConstraintViolation
{
    NeedOwnershipButBorrowed,
    NeedMutableButShared,
    LifetimeTooShort,
    ReturnBorrowOutlivesBorrowee,
    UseAfterMove,
    MutateWhileBorrowed,
    MultipleMutableBorrows,
    SharedBorrowWhileMutable,
    ReadCapabilityDenied,
    WriteCapabilityDenied,
    MoveCapabilityDenied
}

public sealed class LoanConstraintResult
{
    public bool IsValid { get; init; }
    public LoanConstraintViolation? Violation { get; init; }
    public string Message { get; init; } = "";
    public SourceSpan Span { get; init; }
    public SourceSpan? RelatedSpan { get; init; }
    public string? Hint { get; init; }

    public static LoanConstraintResult Success() => new() { IsValid = true };

    public static LoanConstraintResult Failure(
        LoanConstraintViolation violation,
        string message,
        SourceSpan span,
        SourceSpan? relatedSpan = null,
        string? hint = null) => new()
        {
            IsValid = false,
            Violation = violation,
            Message = message,
            Span = span,
            RelatedSpan = relatedSpan,
            Hint = hint
        };

    public override string ToString() => IsValid ? "OK" : $"{Violation}: {Message}";
}

public sealed partial class LoanConstraintVerifier
{
    private readonly LoanSignatureCache _signatureCache;
    private readonly SymbolTable _symbolTable;
    private readonly Func<TypeId, bool> _hasCopyImplResolver;
    private readonly IReadOnlyDictionary<int, string>? _dynamicTypeKeys;
    private readonly BorrowCapabilitySnapshot? _capabilitySnapshot;
    private MirFunc? _currentFunction;
    private Dictionary<LocalId, MirLocal> _localsById = [];
    private IReadOnlyDictionary<LocalId, int> _localIndexMap = new Dictionary<LocalId, int>();
    private LocalId[] _localsByIndex = [];
    private readonly HashSet<string> _reportedDiagnostics = [];
    private readonly HashSet<string> _reportedConflicts = [];
    private readonly Dictionary<(BlockId Block, int Index), LoanVerifierState> _statesAtPoint = [];
    private readonly bool _capturePointStates;

    public List<BorrowDiagnostic> Diagnostics { get; } = [];
    public BorrowCapabilitySnapshot? CapabilitySnapshot => _capabilitySnapshot;

    public LoanConstraintVerifier(
        LoanSignatureCache signatureCache,
        SymbolTable symbolTable,
        BorrowCapabilitySnapshot? capabilitySnapshot = null,
        bool capturePointStates = true,
        IReadOnlyDictionary<int, string>? dynamicTypeKeys = null)
    {
        _signatureCache = signatureCache;
        _symbolTable = symbolTable;
        _hasCopyImplResolver = CopyTypeSemantics.CreateSymbolTableCopyResolver(symbolTable);
        _dynamicTypeKeys = dynamicTypeKeys;
        _capabilitySnapshot = capabilitySnapshot;
        _capturePointStates = capturePointStates;
    }

    public LoanConstraintResult VerifyCall(
        MirCall call,
        MirFunc caller,
        BlockId blockId,
        int instructionIndex)
    {
        Reset();
        InitializeFunctionLocals(caller);
        var state = LoanVerifierState.Empty(_localIndexMap, _localsByIndex);
        var results = new List<LoanConstraintResult>();
        var result = VerifyCall(call, caller, blockId, instructionIndex, state, results);
        return !result.IsValid
            ? result
            : results.FirstOrDefault() ?? LoanConstraintResult.Success();
    }

    public List<LoanConstraintResult> VerifyFunction(MirFunc function, ControlFlowGraph? cfg = null)
    {
        Reset();
        InitializeFunctionLocals(function);

        var results = new List<LoanConstraintResult>();
        var controlFlow = cfg ?? new ControlFlowGraph(function);
        var oneShotBackedgeSuppressions = OneShotLoopMoveAnalysis.CollectBackedgeSuppressions(function, controlFlow);
        var blockById = function.BasicBlocks.ToDictionary(block => block.Id);
        var blockOutStates = new Dictionary<BlockId, LoanVerifierState>();
        var pendingBlocks = new Queue<BlockId>(function.BasicBlocks.Select(block => block.Id));
        var queuedBlocks = function.BasicBlocks.Select(block => block.Id).ToHashSet();

        while (pendingBlocks.Count > 0)
        {
            var blockId = pendingBlocks.Dequeue();
            queuedBlocks.Remove(blockId);
            var block = blockById[blockId];
            var currentState = GetIncomingState(function, blockId, controlFlow, blockOutStates, oneShotBackedgeSuppressions);

            for (int i = 0; i < block.Instructions.Count; i++)
            {
                CheckInstruction(function, block.Instructions[i], blockId, i, currentState, results);
                if (_capturePointStates)
                {
                    _statesAtPoint[(blockId, i)] = currentState.Clone();
                }
            }

            if (block.Terminator != null)
            {
                CheckTerminator(block.Terminator, blockId, block.Instructions.Count, currentState, results);
                if (_capturePointStates)
                {
                    _statesAtPoint[(blockId, block.Instructions.Count)] = currentState.Clone();
                }
            }

            if (!blockOutStates.TryGetValue(block.Id, out var existingState) ||
                !existingState.SemanticallyEquals(currentState))
            {
                blockOutStates[block.Id] = currentState;

                foreach (var successor in controlFlow.GetSuccessors(block.Id))
                {
                    if (queuedBlocks.Add(successor))
                    {
                        pendingBlocks.Enqueue(successor);
                    }
                }
            }
        }

        return results;
    }

    internal IEnumerable<((BlockId Block, int Index) Point, IReadOnlyList<ActiveBorrowInfo> Borrows, IReadOnlyCollection<LocalId> MovedVars)> EnumerateStates()
    {
        return _statesAtPoint
            .OrderBy(entry => entry.Key.Block.Value)
            .ThenBy(entry => entry.Key.Index)
            .Select(entry => (
                entry.Key,
                (IReadOnlyList<ActiveBorrowInfo>)entry.Value.Borrows,
                (IReadOnlyCollection<LocalId>)entry.Value.MovedVars));
    }

    internal IReadOnlyList<ActiveBorrowInfo> GetBorrowsAtPoint(BlockId blockId, int index)
    {
        return _statesAtPoint.TryGetValue((blockId, index), out var state)
            ? state.Borrows
            : [];
    }

    private void Reset()
    {
        Diagnostics.Clear();
        _reportedDiagnostics.Clear();
        _reportedConflicts.Clear();
        _statesAtPoint.Clear();
    }

    private void CheckInstruction(
        MirFunc function,
        MirInstruction instruction,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        switch (instruction)
        {
            case MirCall call:
                {
                    var result = VerifyCall(call, function, blockId, instructionIndex, state, results);
                    if (!result.IsValid)
                    {
                        results.Add(result);
                    }
                    break;
                }

            case MirMove move:
                VerifyMove(move, blockId, instructionIndex, state, results);
                break;

            case MirLoad load:
                VerifyLoad(load, blockId, instructionIndex, state, results);
                break;

            case MirStore store:
                VerifyStore(store, blockId, instructionIndex, state, results);
                break;

            case MirCopy copy:
                VerifyCopy(function, copy, blockId, instructionIndex, state, results);
                break;

            case MirDrop drop:
                VerifyDrop(drop, blockId, instructionIndex, state);
                break;

            case MirAssign assign when assign.Target.Kind == PlaceKind.Local:
                PrepareWrite(assign.Target.Local, assign.Span, blockId, instructionIndex, state, results);
                ValidateReadOperand(assign.Source, assign.Span, blockId, instructionIndex, state, results);
                break;

            case MirCaseInject { Target: MirPlace { Kind: PlaceKind.Local } target } injection:
                PrepareWrite(target.Local, injection.Span, blockId, instructionIndex, state, results);
                ValidateReadOperand(injection.Operand, injection.Span, blockId, instructionIndex, state, results);
                break;

            case MirBinOp binOp when binOp.Target is MirPlace { Kind: PlaceKind.Local, Local: var targetLocal }:
                PrepareWrite(targetLocal, binOp.Span, blockId, instructionIndex, state, results);
                ValidateReadOperand(binOp.Left, binOp.Span, blockId, instructionIndex, state, results);
                ValidateReadOperand(binOp.Right, binOp.Span, blockId, instructionIndex, state, results);
                break;

            case MirUnaryOp unaryOp when unaryOp.Target is MirPlace { Kind: PlaceKind.Local, Local: var unaryTarget }:
                PrepareWrite(unaryTarget, unaryOp.Span, blockId, instructionIndex, state, results);
                ValidateReadOperand(unaryOp.Operand, unaryOp.Span, blockId, instructionIndex, state, results);
                break;

            case MirAlloc alloc when alloc.Target.Kind == PlaceKind.Local:
                PrepareWrite(alloc.Target.Local, alloc.Span, blockId, instructionIndex, state, results);
                break;
        }
    }

    private void CheckTerminator(
        MirTerminator terminator,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        switch (terminator)
        {
            case MirReturn ret when ret.Value is MirPlace returnPlace &&
                                     BorrowTarget.TryResolve(returnPlace, out var returnTarget):
                ValidateReadLocal(returnTarget.BaseLocal, ret.Span, blockId, instructionIndex, state, results);
                EndBorrowsForTarget(returnPlace, returnTarget, blockId, instructionIndex, state);
                break;
        }
    }

    private LoanConstraintResult VerifyCall(
        MirCall call,
        MirFunc caller,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        _ = caller;

        if (call.Target is MirPlace { Kind: PlaceKind.Local, Local: var targetLocal })
        {
            PrepareWrite(targetLocal, call.Span, blockId, instructionIndex, state, results);
        }

        var calleeSignature = LoanCallAnalysis.TryResolveCalleeSignature(call, _signatureCache, _symbolTable);
        if (calleeSignature == null)
        {
            foreach (var argument in call.Arguments)
            {
                ValidateReadOperand(argument, call.Span, blockId, instructionIndex, state, results);
            }

            return LoanConstraintResult.Success();
        }

        var returnedBorrowSources = LoanCallAnalysis.CollectReturnedBorrowSources(
            call,
            calleeSignature,
            localId => ResolveBorrowTargetPaths(localId, state),
            localId => GetBorrowsByBorrower(localId, state),
            borrow => borrow.BorrowTarget);

        for (int i = 0; i < call.Arguments.Count && i < calleeSignature.ParamRequirements.Count; i++)
        {
            var requirement = calleeSignature.ParamRequirements[i];
            var result = VerifyArgConstraint(
                call.Arguments[i],
                requirement,
                call.Span,
                blockId,
                instructionIndex,
                state);
            if (!result.IsValid)
            {
                return result;
            }
        }

        var returnResult = VerifyReturnConstraints(
            call,
            calleeSignature,
            blockId,
            instructionIndex,
            state);
        if (!returnResult.IsValid)
        {
            return returnResult;
        }

        var returnedBorrowCapabilityResult = VerifyReturnedBorrowCapabilities(
            returnedBorrowSources,
            call.Span,
            blockId,
            instructionIndex);
        if (!returnedBorrowCapabilityResult.IsValid)
        {
            return returnedBorrowCapabilityResult;
        }

        ApplyCallState(
            call,
            calleeSignature,
            blockId,
            instructionIndex,
            state,
            returnedBorrowSources);

        return LoanConstraintResult.Success();
    }

    private LoanConstraintResult VerifyArgConstraint(
        MirOperand argument,
        ParamBorrowRequirement requirement,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        if (argument is not MirPlace place)
        {
            return LoanConstraintResult.Success();
        }

        var capabilityResult = VerifyArgumentCapability(
            place,
            requirement.Mode,
            span,
            blockId,
            instructionIndex,
            state);
        if (!capabilityResult.IsValid)
        {
            return capabilityResult;
        }

        if (place.Kind != PlaceKind.Local)
        {
            return VerifyNonLocalArgConstraint(
                place,
                requirement.Mode,
                span,
                blockId,
                instructionIndex,
                state);
        }

        return requirement.Mode switch
        {
            ParamBorrowMode.Own => VerifyOwnership(place.Local, span, blockId, instructionIndex, state),
            ParamBorrowMode.BorrowShared => VerifySharedBorrow(place.Local, span, blockId, instructionIndex, state),
            ParamBorrowMode.BorrowMutable => VerifyMutableBorrow(place.Local, span, blockId, instructionIndex, state),
            ParamBorrowMode.Copy => VerifyCopyArgument(place.Local, span, blockId, instructionIndex, state),
            _ => LoanConstraintResult.Success()
        };
    }

    private LoanConstraintResult VerifyArgumentCapability(
        MirPlace place,
        ParamBorrowMode mode,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        return mode switch
        {
            ParamBorrowMode.Own => RequireCallArgumentMoveCapability(place, span, blockId, instructionIndex),
            ParamBorrowMode.BorrowMutable => RequireCallArgumentLoadCapability(
                place,
                isMutableBorrow: true,
                span,
                blockId,
                instructionIndex,
                state,
                readAction: DiagnosticMessages.BorrowActionCreateSharedBorrowThroughCallArgument,
                mutableAction: DiagnosticMessages.BorrowActionCreateMutableBorrowThroughCallArgument),
            ParamBorrowMode.BorrowShared => RequireCallArgumentLoadCapability(
                place,
                isMutableBorrow: false,
                span,
                blockId,
                instructionIndex,
                state,
                readAction: DiagnosticMessages.BorrowActionCreateSharedBorrowThroughCallArgument,
                mutableAction: DiagnosticMessages.BorrowActionCreateMutableBorrowThroughCallArgument),
            ParamBorrowMode.Copy => RequireCallArgumentLoadCapability(
                place,
                isMutableBorrow: false,
                span,
                blockId,
                instructionIndex,
                state,
                readAction: DiagnosticMessages.BorrowActionReadThroughCallArgument,
                mutableAction: DiagnosticMessages.BorrowActionCreateMutableBorrowThroughCallArgument),
            _ => LoanConstraintResult.Success()
        };
    }

    private LoanConstraintResult RequireCallArgumentLoadCapability(
        MirPlace place,
        bool isMutableBorrow,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        string readAction,
        string mutableAction)
    {
        foreach (var target in ResolveCallArgumentBorrowTargets(place, state).Distinct())
        {
            var capabilityResult = RequireLoadCapability(
                target,
                isMutableBorrow,
                span,
                blockId,
                instructionIndex,
                readAction,
                mutableAction);
            if (!capabilityResult.IsValid)
            {
                return capabilityResult;
            }
        }

        return LoanConstraintResult.Success();
    }

    private LoanConstraintResult RequireCallArgumentMoveCapability(
        MirPlace place,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex)
    {
        if (place.Kind == PlaceKind.Local)
        {
            return RequireMoveCapability(
                place.Local,
                span,
                blockId,
                instructionIndex,
                DiagnosticMessages.BorrowActionMoveValueThroughCallArgument);
        }

        if (BorrowTarget.TryResolve(place, out var target))
        {
            return RequireMoveCapability(
                target,
                span,
                blockId,
                instructionIndex,
                DiagnosticMessages.BorrowActionMoveValueThroughCallArgument);
        }

        return LoanConstraintResult.Success();
    }

    private IReadOnlyList<BorrowTarget> ResolveCallArgumentBorrowTargets(MirPlace place, LoanVerifierState state)
    {
        if (place.Kind == PlaceKind.Local)
        {
            return ResolveBorrowTargetPaths(place.Local, state);
        }

        if (BorrowTarget.TryResolve(place, out var target))
        {
            return [target];
        }

        return [];
    }

    private LoanConstraintResult VerifyNonLocalArgConstraint(
        MirPlace place,
        ParamBorrowMode mode,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        if (!BorrowTarget.TryResolve(place, out var target))
        {
            return LoanConstraintResult.Success();
        }

        return mode switch
        {
            ParamBorrowMode.Own => ValidateMoveOfOwnedTarget(target, span, blockId, instructionIndex, state),
            ParamBorrowMode.BorrowShared => VerifySharedBorrowTarget(target, span, blockId, instructionIndex, state),
            ParamBorrowMode.BorrowMutable => VerifyMutableBorrowTarget(target, span, blockId, instructionIndex, state),
            ParamBorrowMode.Copy => ValidateReadLocal(target.BaseLocal, span, blockId, instructionIndex, state, null),
            _ => LoanConstraintResult.Success()
        };
    }

    private LoanConstraintResult VerifySharedBorrowTarget(
        BorrowTarget borrowTarget,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        var movedResult = ValidateReadLocal(borrowTarget.BaseLocal, span, blockId, instructionIndex, state, null);
        if (!movedResult.IsValid)
        {
            return movedResult;
        }

        var mutableBorrows = GetActiveBorrows(borrowTarget, state)
            .Where(borrow => borrow.IsMutable)
            .ToList();
        if (mutableBorrows.Count == 0)
        {
            return LoanConstraintResult.Success();
        }

        var conflict = mutableBorrows[0];
        var hint = BorrowAliasTrace.BuildConflictHint(
            conflict.AliasTrace,
            conflict.TraceId,
            DiagnosticMessages.BorrowWaitMutableEndsBeforeShared);

        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.ImmutableWhileMutableBorrowed,
            Message = DiagnosticMessages.BorrowSharedWhileMutable,
            Span = span,
            RelatedSpan = conflict.Span,
            Location = (blockId, instructionIndex),
            RelatedLocation = conflict.Location,
            RelatedAliasTrace = [.. conflict.AliasTrace],
            RelatedAliasTraceId = conflict.TraceId,
            Hint = hint
        });

        return LoanConstraintResult.Failure(
            LoanConstraintViolation.SharedBorrowWhileMutable,
            DiagnosticMessages.BorrowSharedWhileMutableResult,
            span,
            conflict.Span,
            hint);
    }

    private LoanConstraintResult VerifyMutableBorrowTarget(
        BorrowTarget borrowTarget,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        var movedResult = ValidateReadLocal(borrowTarget.BaseLocal, span, blockId, instructionIndex, state, null);
        if (!movedResult.IsValid)
        {
            return movedResult;
        }

        var activeBorrows = GetActiveBorrows(borrowTarget, state);
        if (activeBorrows.Count == 0)
        {
            return LoanConstraintResult.Success();
        }

        var conflict = activeBorrows[0];
        var hasMutable = activeBorrows.Any(borrow => borrow.IsMutable);
        var violation = hasMutable
            ? LoanConstraintViolation.MultipleMutableBorrows
            : LoanConstraintViolation.NeedMutableButShared;
        var kind = hasMutable
            ? BorrowErrorKind.MultipleMutableBorrows
            : BorrowErrorKind.MutableWhileImmutableBorrowed;
        var hint = BorrowAliasTrace.BuildConflictHint(
            conflict.AliasTrace,
            conflict.TraceId,
            DiagnosticMessages.BorrowWaitExistingEndsBeforeMutable);

        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = kind,
            Message = hasMutable
                ? DiagnosticMessages.BorrowMultipleMutable
                : DiagnosticMessages.BorrowSharedExistsCannotCreateMutable,
            Span = span,
            RelatedSpan = conflict.Span,
            Location = (blockId, instructionIndex),
            RelatedLocation = conflict.Location,
            RelatedAliasTrace = [.. conflict.AliasTrace],
            RelatedAliasTraceId = conflict.TraceId,
            Hint = hint
        });

        return LoanConstraintResult.Failure(
            violation,
            hasMutable
                ? DiagnosticMessages.BorrowMutableBorrowedCannotBorrowAgain
                : DiagnosticMessages.BorrowSharedBorrowedCannotCreateMutable,
            span,
            conflict.Span,
            hint);
    }

    private LoanConstraintResult VerifyOwnership(
        LocalId localId,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        var aliasSources = GetBorrowsByBorrower(localId, state);
        if (aliasSources.Count > 0)
        {
            var firstAlias = aliasSources[0];
            var hint = BorrowAliasTrace.BuildConflictHint(
                firstAlias.AliasTrace,
                firstAlias.TraceId,
                DiagnosticMessages.BorrowOwnershipRequiredHint);

            AddDiagnostic(new BorrowDiagnostic
            {
                Kind = BorrowErrorKind.MutateWhileBorrowed,
                Message = DiagnosticMessages.BorrowArgumentRequiresOwnershipButAlias,
                Span = span,
                RelatedSpan = firstAlias.Span,
                Location = (blockId, instructionIndex),
                RelatedLocation = firstAlias.Location,
                RelatedAliasTrace = [.. firstAlias.AliasTrace],
                RelatedAliasTraceId = firstAlias.TraceId,
                Hint = hint
            });

            return LoanConstraintResult.Failure(
                LoanConstraintViolation.NeedOwnershipButBorrowed,
                DiagnosticMessages.BorrowArgumentRequiresOwnershipButBorrowed,
                span,
                firstAlias.Span,
                hint);
        }

        return ValidateMoveOfOwnedValue(localId, span, blockId, instructionIndex, state);
    }

    private LoanConstraintResult VerifySharedBorrow(
        LocalId localId,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        var movedResult = ValidateReadLocal(localId, span, blockId, instructionIndex, state, null);
        if (!movedResult.IsValid)
        {
            return movedResult;
        }

        var borrowTargets = ResolveBorrowTargets(localId, state);
        foreach (var borrowTarget in borrowTargets)
        {
            var mutableBorrows = GetActiveBorrows(borrowTarget, state)
                .Where(borrow => borrow.IsMutable)
                .ToList();
            if (mutableBorrows.Count == 0)
            {
                continue;
            }

            var conflict = mutableBorrows[0];
            var hint = BorrowAliasTrace.BuildConflictHint(
                conflict.AliasTrace,
                conflict.TraceId,
                DiagnosticMessages.BorrowWaitMutableEndsBeforeShared);

            AddDiagnostic(new BorrowDiagnostic
            {
                Kind = BorrowErrorKind.ImmutableWhileMutableBorrowed,
                Message = DiagnosticMessages.BorrowSharedWhileMutable,
                Span = span,
                RelatedSpan = conflict.Span,
                Location = (blockId, instructionIndex),
                RelatedLocation = conflict.Location,
                RelatedAliasTrace = [.. conflict.AliasTrace],
                RelatedAliasTraceId = conflict.TraceId,
                Hint = hint
            });

            return LoanConstraintResult.Failure(
                LoanConstraintViolation.SharedBorrowWhileMutable,
                DiagnosticMessages.BorrowSharedWhileMutableResult,
                span,
                conflict.Span,
                hint);
        }

        return LoanConstraintResult.Success();
    }

    private LoanConstraintResult VerifyMutableBorrow(
        LocalId localId,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        var movedResult = ValidateReadLocal(localId, span, blockId, instructionIndex, state, null);
        if (!movedResult.IsValid)
        {
            return movedResult;
        }

        var borrowTargets = ResolveBorrowTargets(localId, state);
        foreach (var borrowTarget in borrowTargets)
        {
            var activeBorrows = GetActiveBorrows(borrowTarget, state);
            if (activeBorrows.Count == 0)
            {
                continue;
            }

            var conflict = activeBorrows[0];
            var hasMutable = activeBorrows.Any(borrow => borrow.IsMutable);
            var violation = hasMutable
                ? LoanConstraintViolation.MultipleMutableBorrows
                : LoanConstraintViolation.NeedMutableButShared;
            var kind = hasMutable
                ? BorrowErrorKind.MultipleMutableBorrows
                : BorrowErrorKind.MutableWhileImmutableBorrowed;
            var hint = BorrowAliasTrace.BuildConflictHint(
                conflict.AliasTrace,
                conflict.TraceId,
                DiagnosticMessages.BorrowWaitExistingEndsBeforeMutable);

            AddDiagnostic(new BorrowDiagnostic
            {
                Kind = kind,
                Message = hasMutable
                    ? DiagnosticMessages.BorrowMultipleMutable
                    : DiagnosticMessages.BorrowSharedExistsCannotCreateMutable,
                Span = span,
                RelatedSpan = conflict.Span,
                Location = (blockId, instructionIndex),
                RelatedLocation = conflict.Location,
                RelatedAliasTrace = [.. conflict.AliasTrace],
                RelatedAliasTraceId = conflict.TraceId,
                Hint = hint
            });

            return LoanConstraintResult.Failure(
                violation,
                hasMutable
                    ? DiagnosticMessages.BorrowMutableBorrowedCannotBorrowAgain
                    : DiagnosticMessages.BorrowSharedBorrowedCannotCreateMutable,
                span,
                conflict.Span,
                hint);
        }

        return LoanConstraintResult.Success();
    }

    private LoanConstraintResult VerifyCopyArgument(
        LocalId localId,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        return ValidateReadLocal(localId, span, blockId, instructionIndex, state, null);
    }

    private LoanConstraintResult VerifyReturnConstraints(
        MirCall call,
        LoanSignature signature,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        if (!signature.ReturnsBorrow())
        {
            return LoanConstraintResult.Success();
        }

        foreach (var paramIndex in signature.ReturnConstraint.BoundToParams)
        {
            if (paramIndex < 0 || paramIndex >= call.Arguments.Count)
            {
                continue;
            }

            if (call.Arguments[paramIndex] is not MirPlace { Kind: PlaceKind.Local, Local: var argumentLocal })
            {
                continue;
            }

            var borrowTargets = ResolveBorrowTargets(argumentLocal, state);
            foreach (var borrowTarget in borrowTargets)
            {
                if (!state.MovedVars.Contains(borrowTarget.BaseLocal))
                {
                    continue;
                }

                AddDiagnostic(new BorrowDiagnostic
                {
                    Kind = BorrowErrorKind.LifetimeTooShort,
                    Message = DiagnosticMessages.BorrowReturnBoundToInvalidValue,
                    Span = call.Span,
                    Location = (blockId, instructionIndex),
                    Hint = DiagnosticMessages.BorrowKeepBorroweeAliveHint
                });

                return LoanConstraintResult.Failure(
                    LoanConstraintViolation.ReturnBorrowOutlivesBorrowee,
                    DiagnosticMessages.BorrowReturnedValueLifetimeExceedsArgument,
                    call.Span,
                    hint: DiagnosticMessages.BorrowKeepBorroweeAliveHint);
            }
        }

        return LoanConstraintResult.Success();
    }

    private LoanConstraintResult VerifyReturnedBorrowCapabilities(
        IEnumerable<(ReturnedCallBorrowBinding Binding, List<ActiveBorrowInfo> Sources)> returnedBorrowSources,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex)
    {
        foreach (var (binding, _) in returnedBorrowSources)
        {
            var capabilityResult = RequireLoadCapability(
                binding.BorrowTarget,
                binding.IsMutable,
                span,
                blockId,
                instructionIndex,
                DiagnosticMessages.BorrowActionBindReturnedSharedBorrow,
                DiagnosticMessages.BorrowActionBindReturnedMutableBorrow);
            if (!capabilityResult.IsValid)
            {
                return capabilityResult;
            }
        }

        return LoanConstraintResult.Success();
    }

    private void ApplyCallState(
        MirCall call,
        LoanSignature signature,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<(ReturnedCallBorrowBinding Binding, List<ActiveBorrowInfo> Sources)> returnedBorrowSources)
    {
        LoanCallAnalysis.ForEachOwnedLocalArgument(
            call,
            signature,
            (_, argumentLocal) =>
            {
                if (IsBorrower(argumentLocal, state))
                {
                    EndBorrowsByBorrower(argumentLocal, blockId, instructionIndex, state);
                    state.MovedVars.Add(argumentLocal);
                    return;
                }

                state.MovedVars.Add(argumentLocal);
                EndBorrowsByBorrowee(argumentLocal, blockId, instructionIndex, state);
            });

        if (!signature.ReturnsBorrow())
        {
            return;
        }

        LoanCallAnalysis.ApplyReturnedBorrowSources(
            returnedBorrowSources,
            binding =>
            {
                AddBorrow(
                    binding.BorrowTarget,
                    binding.Borrowee,
                    binding.TargetLocal,
                    binding.IsMutable,
                    binding.Lifetime,
                    call.Span,
                    blockId,
                    instructionIndex,
                    state,
                    originSummary: BorrowDiagnosticFormatter.BuildCallTrace(call, binding.ArgumentIndex, binding.TargetLocal, binding.Borrowee, blockId, instructionIndex));
            },
            (binding, sourceBorrow) =>
            {
                AddBorrow(
                    binding.BorrowTarget,
                    binding.Borrowee,
                    binding.TargetLocal,
                    binding.IsMutable,
                    binding.Lifetime,
                    call.Span,
                    blockId,
                    instructionIndex,
                    state,
                    sourceBorrow,
                    BorrowDiagnosticFormatter.BuildCallTrace(call, binding.ArgumentIndex, binding.TargetLocal, binding.Borrowee, blockId, instructionIndex));
            });
    }

    private void VerifyMove(
        MirMove move,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        if (!MirLocalTransferAnalysis.TryGetBinding(move, out var moveBinding))
        {
            return;
        }

        var moveCapabilityResult = RequireMoveCapability(moveBinding.Source, move.Span, blockId, instructionIndex);
        if (!moveCapabilityResult.IsValid)
        {
            results.Add(moveCapabilityResult);
            return;
        }

        PrepareWrite(moveBinding.Target, move.Span, blockId, instructionIndex, state, results);

        var transferBindings = BorrowPropagationAnalysis.CollectTransferBindings(
            moveBinding.Source,
            localId => GetBorrowsByBorrower(localId, state),
            borrow => borrow.BorrowTarget,
            borrow => borrow.IsMutable);
        if (transferBindings.Count > 0)
        {
            EndBorrowsByBorrower(moveBinding.Source, blockId, instructionIndex, state);
            state.MovedVars.Add(moveBinding.Source);

            foreach (var binding in transferBindings)
            {
                AddBorrow(
                    binding.BorrowTarget,
                    binding.Borrowee,
                    moveBinding.Target,
                    binding.IsMutable,
                    binding.SourceBorrow?.Lifetime ?? LifetimeId.None,
                    move.Span,
                    blockId,
                    instructionIndex,
                    state,
                    binding.SourceBorrow,
                    BorrowDiagnosticFormatter.BuildMoveTrace(moveBinding.Source, moveBinding.Target, blockId, instructionIndex));
            }

            return;
        }

        var result = ValidateMoveOfOwnedValue(moveBinding.Source, move.Span, blockId, instructionIndex, state);
        if (!result.IsValid)
        {
            results.Add(result);
            return;
        }

        state.MovedVars.Add(moveBinding.Source);
        EndBorrowsByBorrowee(moveBinding.Source, blockId, instructionIndex, state);
    }

    private void VerifyLoad(
        MirLoad load,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        if (load.Target.Kind != PlaceKind.Local)
        {
            return;
        }

        var targetLocal = load.Target.Local;
        var isMutableLoad = load.IsMutableBorrow;
        var createsBorrowAlias = load.IsMutableBorrow || load.CreatesBorrowAlias;
        PrepareWrite(targetLocal, load.Span, blockId, instructionIndex, state, results);

        if (!MirLocalTransferAnalysis.TryGetBinding(load, out var loadBinding))
        {
            if (BorrowTarget.TryResolve(load.Source, out var fallbackTarget))
            {
                var capabilityResult = RequireLoadCapability(
                    fallbackTarget,
                    isMutableLoad,
                    load.Span,
                    blockId,
                    instructionIndex);
                if (!capabilityResult.IsValid)
                {
                    results.Add(capabilityResult);
                    return;
                }

                if (!createsBorrowAlias)
                {
                    return;
                }

                var fallbackSource = fallbackTarget.BaseLocal;
                var readFallback = ValidateReadLocal(fallbackSource, load.Span, blockId, instructionIndex, state, results);
                if (!readFallback.IsValid)
                {
                    return;
                }

                AddBorrow(
                    fallbackTarget,
                    fallbackSource,
                    targetLocal,
                    isMutableLoad,
                    LifetimeId.None,
                    load.Span,
                    blockId,
                    instructionIndex,
                    state,
                    originSummary: BorrowDiagnosticFormatter.BuildLoadTrace(fallbackSource, targetLocal, blockId, instructionIndex));
            }
            return;
        }

        var sourceLocal = loadBinding.Source;
        targetLocal = loadBinding.Target;
        var readResult = ValidateReadLocal(sourceLocal, load.Span, blockId, instructionIndex, state, results);
        if (!readResult.IsValid)
        {
            return;
        }

        var loadBindings = createsBorrowAlias
            ? BorrowPropagationAnalysis.CollectLoadBindings(
                sourceLocal,
                localId => GetBorrowsByBorrower(localId, state),
                borrow => borrow.BorrowTarget)
            : BorrowPropagationAnalysis.CollectTransferBindings(
                sourceLocal,
                localId => GetBorrowsByBorrower(localId, state),
                borrow => borrow.BorrowTarget,
                borrow => borrow.IsMutable);
        foreach (var binding in loadBindings)
        {
            var effectiveIsMutable = createsBorrowAlias
                ? binding.SourceBorrow?.IsMutable == true || isMutableLoad
                : binding.IsMutable || isMutableLoad;
            var capabilityResult = RequireLoadCapability(
                binding.BorrowTarget,
                effectiveIsMutable,
                load.Span,
                blockId,
                instructionIndex);
            if (!capabilityResult.IsValid)
            {
                results.Add(capabilityResult);
                continue;
            }

            if (binding.SourceBorrow == null)
            {
                if (!createsBorrowAlias)
                {
                    continue;
                }

                AddBorrow(
                    binding.BorrowTarget,
                    binding.Borrowee,
                    targetLocal,
                    isMutableLoad,
                    LifetimeId.None,
                    load.Span,
                    blockId,
                    instructionIndex,
                    state,
                    originSummary: BorrowDiagnosticFormatter.BuildLoadTrace(sourceLocal, targetLocal, blockId, instructionIndex));
                continue;
            }

            AddBorrow(
                binding.BorrowTarget,
                binding.Borrowee,
                targetLocal,
                effectiveIsMutable,
                binding.SourceBorrow.Lifetime,
                load.Span,
                blockId,
                instructionIndex,
                state,
                binding.SourceBorrow,
                BorrowDiagnosticFormatter.BuildLoadTrace(sourceLocal, targetLocal, blockId, instructionIndex));
        }
    }

    private void VerifyStore(
        MirStore store,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        if (BorrowTarget.TryResolve(store.Target, out var storeTarget))
        {
            var capabilityResult = RequireWriteCapability(
                storeTarget,
                store.Span,
                blockId,
                instructionIndex,
                DiagnosticMessages.BorrowActionWrite);
            if (!capabilityResult.IsValid)
            {
                results.Add(capabilityResult);
                return;
            }
        }

        PrepareWrite(store.Target, store.Span, blockId, instructionIndex, state, results);

        ValidateReadOperand(store.Value, store.Span, blockId, instructionIndex, state, results);
    }

    private void VerifyCopy(
        MirFunc function,
        MirCopy copy,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        if (!MirLocalTransferAnalysis.TryGetBinding(copy, out var copyBinding))
        {
            return;
        }

        PrepareWrite(copyBinding.Target, copy.Span, blockId, instructionIndex, state, results);

        var readResult = ValidateReadLocal(copyBinding.Source, copy.Span, blockId, instructionIndex, state, results);
        if (!readResult.IsValid)
        {
            return;
        }

        var transferBindings = BorrowPropagationAnalysis.CollectTransferBindings(
            copyBinding.Source,
            localId => GetBorrowsByBorrower(localId, state),
            borrow => borrow.BorrowTarget,
            borrow => borrow.IsMutable);
        if (transferBindings.Count > 0 &&
            ShouldDetachBorrowAliasOnCopy(function, copyBinding.Target))
        {
            return;
        }

        foreach (var binding in transferBindings)
        {
            AddBorrow(
                binding.BorrowTarget,
                binding.Borrowee,
                copyBinding.Target,
                binding.IsMutable,
                binding.SourceBorrow?.Lifetime ?? LifetimeId.None,
                copy.Span,
                blockId,
                instructionIndex,
                state,
                binding.SourceBorrow,
                BorrowDiagnosticFormatter.BuildCopyTrace(copyBinding.Source, copyBinding.Target, blockId, instructionIndex));
        }
    }

    private void VerifyDrop(
        MirDrop drop,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
        if (drop.Value is not MirPlace dropPlace ||
            !BorrowTarget.TryResolve(dropPlace, out var dropTarget))
        {
            return;
        }

        if (dropPlace.Kind == PlaceKind.Local &&
            IsBorrower(dropPlace.Local, state))
        {
            EndBorrowsByBorrower(dropPlace.Local, blockId, instructionIndex, state);
            state.MovedVars.Add(dropPlace.Local);
            return;
        }

        EndBorrowsByBorrowTarget(dropTarget, blockId, instructionIndex, state);
        if (dropPlace.Kind == PlaceKind.Local)
        {
            state.MovedVars.Add(dropPlace.Local);
        }
    }

    private LoanConstraintResult RequireLoadCapability(
        BorrowTarget target,
        bool isMutableBorrow,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        string? readAction = null,
        string? mutableAction = null)
    {
        if (_capabilitySnapshot == null || !_capabilitySnapshot.IsEnforced)
        {
            return LoanConstraintResult.Success();
        }

        if (isMutableBorrow)
        {
            return RequireWriteCapability(
                target,
                span,
                blockId,
                instructionIndex,
                mutableAction ?? DiagnosticMessages.BorrowActionCreateMutableBorrow);
        }

        if (_capabilitySnapshot.CanRead(target))
        {
            return LoanConstraintResult.Success();
        }

        var targetDisplay = BorrowDiagnosticFormatter.FormatBorrowTarget(target);
        var resolution = _capabilitySnapshot.ExplainCapabilityResolution(target, BorrowCapabilityKind.Read);
        var effectiveReadAction = readAction ?? DiagnosticMessages.BorrowActionCreateSharedBorrow;
        var hint = DiagnosticMessages.BorrowReadCapabilityAdjustmentHint(resolution);
        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.ReadCapabilityDenied,
            Message = DiagnosticMessages.BorrowReadCapabilityDenied(effectiveReadAction, targetDisplay),
            Span = span,
            Location = (blockId, instructionIndex),
            Hint = hint
        });

        return LoanConstraintResult.Failure(
            LoanConstraintViolation.ReadCapabilityDenied,
            DiagnosticMessages.BorrowReadCapabilityDenied(effectiveReadAction, targetDisplay),
            span,
            hint: hint);
    }

    private LoanConstraintResult RequireWriteCapability(
        BorrowTarget target,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        string? action)
    {
        if (_capabilitySnapshot == null || !_capabilitySnapshot.IsEnforced)
        {
            return LoanConstraintResult.Success();
        }

        if (_capabilitySnapshot.CanWrite(target))
        {
            return LoanConstraintResult.Success();
        }

        var targetDisplay = BorrowDiagnosticFormatter.FormatBorrowTarget(target);
        var resolution = _capabilitySnapshot.ExplainCapabilityResolution(target, BorrowCapabilityKind.Write);
        var effectiveAction = action ?? DiagnosticMessages.BorrowActionWrite;
        var hint = DiagnosticMessages.BorrowWriteCapabilityAdjustmentHint(resolution);
        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.WriteCapabilityDenied,
            Message = DiagnosticMessages.BorrowWriteCapabilityDenied(effectiveAction, targetDisplay),
            Span = span,
            Location = (blockId, instructionIndex),
            Hint = hint
        });

        return LoanConstraintResult.Failure(
            LoanConstraintViolation.WriteCapabilityDenied,
            DiagnosticMessages.BorrowWriteCapabilityDenied(effectiveAction, targetDisplay),
            span,
            hint: hint);
    }

    private LoanConstraintResult RequireMoveCapability(
        LocalId localId,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        string? action = null)
    {
        if (_capabilitySnapshot == null || !_capabilitySnapshot.IsEnforced)
        {
            return LoanConstraintResult.Success();
        }

        if (_capabilitySnapshot.CanMove(localId))
        {
            return LoanConstraintResult.Success();
        }

        var localDisplay = BorrowDiagnosticFormatter.FormatLocal(localId);
        var resolution = _capabilitySnapshot.ExplainCapabilityResolution(localId, BorrowCapabilityKind.Move);
        var effectiveAction = action ?? DiagnosticMessages.BorrowActionMoveValue;
        var hint = DiagnosticMessages.BorrowMoveCapabilityAdjustmentHint(resolution);
        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.MoveCapabilityDenied,
            Message = DiagnosticMessages.BorrowMoveCapabilityDenied(effectiveAction, localDisplay),
            Span = span,
            Location = (blockId, instructionIndex),
            Hint = hint
        });

        return LoanConstraintResult.Failure(
            LoanConstraintViolation.MoveCapabilityDenied,
            DiagnosticMessages.BorrowMoveCapabilityDenied(effectiveAction, localDisplay),
            span,
            hint: hint);
    }

    private LoanConstraintResult RequireMoveCapability(
        BorrowTarget target,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        string? action = null)
    {
        if (_capabilitySnapshot == null || !_capabilitySnapshot.IsEnforced)
        {
            return LoanConstraintResult.Success();
        }

        if (_capabilitySnapshot.CanMove(target))
        {
            return LoanConstraintResult.Success();
        }

        var targetDisplay = BorrowDiagnosticFormatter.FormatBorrowTarget(target);
        var resolution = _capabilitySnapshot.ExplainCapabilityResolution(target, BorrowCapabilityKind.Move);
        var effectiveAction = action ?? DiagnosticMessages.BorrowActionMoveValue;
        var hint = DiagnosticMessages.BorrowMoveCapabilityAdjustmentHint(resolution);
        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.MoveCapabilityDenied,
            Message = DiagnosticMessages.BorrowMoveCapabilityDenied(effectiveAction, targetDisplay),
            Span = span,
            Location = (blockId, instructionIndex),
            Hint = hint
        });

        return LoanConstraintResult.Failure(
            LoanConstraintViolation.MoveCapabilityDenied,
            DiagnosticMessages.BorrowMoveCapabilityDenied(effectiveAction, targetDisplay),
            span,
            hint: hint);
    }

    private void PrepareWrite(
        MirPlace target,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        if (!BorrowTarget.TryResolve(target, out var borrowTarget))
        {
            return;
        }

        if (target.Kind == PlaceKind.Local)
        {
            PrepareWrite(target.Local, borrowTarget, span, blockId, instructionIndex, state, results);
            return;
        }

        var readResult = ValidateReadLocal(borrowTarget.BaseLocal, span, blockId, instructionIndex, state, results);
        if (!readResult.IsValid)
        {
            return;
        }

        ReportMutateWhileBorrowedConflict(borrowTarget, span, blockId, instructionIndex, state, results);
    }

    private void PrepareWrite(
        LocalId localId,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        PrepareWrite(localId, BorrowTarget.ForLocal(localId), span, blockId, instructionIndex, state, results);
    }

    private void PrepareWrite(
        LocalId localId,
        BorrowTarget borrowTarget,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        if (!localId.IsValid)
        {
            return;
        }

        if (IsBorrower(localId, state))
        {
            EndBorrowsByBorrower(localId, blockId, instructionIndex, state);
            state.MovedVars.Remove(localId);
            return;
        }

        ReportMutateWhileBorrowedConflict(borrowTarget, span, blockId, instructionIndex, state, results);
        state.MovedVars.Remove(localId);
    }

    private void ReportMutateWhileBorrowedConflict(
        BorrowTarget borrowTarget,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state,
        List<LoanConstraintResult> results)
    {
        var activeBorrows = GetActiveBorrows(borrowTarget, state);
        if (activeBorrows.Count == 0)
        {
            return;
        }

        var conflict = activeBorrows[0];
        var hint = BorrowAliasTrace.BuildConflictHint(
            conflict.AliasTrace,
            conflict.TraceId,
            DiagnosticMessages.BorrowWaitEndsBeforeWrite);

        AddDiagnostic(new BorrowDiagnostic
        {
            Kind = BorrowErrorKind.MutateWhileBorrowed,
            Message = DiagnosticMessages.BorrowValueBorrowedCannotModify,
            Span = span,
            RelatedSpan = conflict.Span,
            Location = (blockId, instructionIndex),
            RelatedLocation = conflict.Location,
            RelatedAliasTrace = [.. conflict.AliasTrace],
            RelatedAliasTraceId = conflict.TraceId,
            Hint = hint
        });

        results.Add(LoanConstraintResult.Failure(
            LoanConstraintViolation.MutateWhileBorrowed,
            DiagnosticMessages.BorrowValueBorrowedCannotModify,
            span,
            conflict.Span,
            hint));
    }

    private LoanConstraintResult ValidateMoveOfOwnedValue(
        LocalId localId,
        SourceSpan span,
        BlockId blockId,
        int instructionIndex,
        LoanVerifierState state)
    {
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

        var activeBorrows = GetActiveBorrows(BorrowTarget.ForLocal(localId), state);
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
}

internal sealed class LoanVerifierState
{
    private readonly IndexedBorrowState<ActiveBorrowInfo, BorrowAliasTrace.BorrowStateKey> _state;

    public List<ActiveBorrowInfo> Borrows => _state.Borrows;
    public LocalIdBitSet MovedVars { get; }

    public LoanVerifierState(List<ActiveBorrowInfo> borrows, LocalIdBitSet movedVars)
        : this(borrows, movedVars, cloneInputs: true)
    {
    }

    private LoanVerifierState(List<ActiveBorrowInfo> borrows, LocalIdBitSet movedVars, bool cloneInputs)
    {
        _state = new IndexedBorrowState<ActiveBorrowInfo, BorrowAliasTrace.BorrowStateKey>(
            borrows,
            GetBorrowKey,
            borrow => borrow.Borrower,
            borrow => borrow.Borrowee,
            CloneBorrow,
            cloneInputs,
            borrowTargetSelector: borrow => borrow.BorrowTarget);
        MovedVars = movedVars;
    }

    public static LoanVerifierState Empty(
        IReadOnlyDictionary<LocalId, int> localIndexMap,
        LocalId[] localsByIndex) => new([], new LocalIdBitSet(localIndexMap, localsByIndex));

    public LoanVerifierState Clone()
    {
        return new LoanVerifierState(
            Borrows.Select(CloneBorrow).ToList(),
            MovedVars.Clone(),
            cloneInputs: false);
    }

    public bool SemanticallyEquals(LoanVerifierState other)
    {
        return _state.SemanticallyEquals(other._state) &&
               MovedVars.SetEquals(other.MovedVars);
    }

    public bool TryAddBorrow(ActiveBorrowInfo borrow)
    {
        return _state.TryAddBorrow(borrow);
    }

    public void UnionBorrowsFrom(LoanVerifierState other)
    {
        foreach (var borrow in other.Borrows)
        {
            TryAddBorrow(CloneBorrow(borrow));
        }
    }

    public void EndBorrowsByBorrower(LocalId borrower, (BlockId Block, int Index) endLocation)
    {
        _state.EndBorrowsByBorrower(
            borrower,
            borrow => borrow.EndLocation = endLocation);
    }

    public void EndBorrowsByBorrowee(LocalId borrowee, (BlockId Block, int Index) endLocation)
    {
        _state.EndBorrowsByBorrowee(
            borrowee,
            borrow => borrow.EndLocation = endLocation);
    }

    public void EndBorrowsByBorrowTarget(BorrowTarget target, (BlockId Block, int Index) endLocation)
    {
        _state.EndBorrowsByBorrowTarget(
            target,
            borrow => borrow.EndLocation = endLocation);
    }

    public bool IsBorrower(LocalId localId)
    {
        return _state.IsBorrower(localId);
    }

    public List<ActiveBorrowInfo> GetBorrowsByBorrower(LocalId localId)
    {
        return _state.GetBorrowsByBorrower(localId);
    }

    public List<ActiveBorrowInfo> GetBorrowsByBorrowee(LocalId localId)
    {
        return _state.GetBorrowsByBorrowee(localId);
    }

    public List<ActiveBorrowInfo> GetBorrowsByBorrowTarget(BorrowTarget target)
    {
        return _state.GetBorrowsByBorrowTarget(target);
    }

    public List<LocalId> ResolveBorrowTargets(LocalId localId)
    {
        return _state.ResolveBorrowTargets(localId);
    }

    public List<BorrowTarget> ResolveBorrowTargetPaths(LocalId localId)
    {
        return _state.ResolveBorrowTargetPaths(localId);
    }

    public static ActiveBorrowInfo CloneBorrow(ActiveBorrowInfo borrow)
    {
        return new ActiveBorrowInfo
        {
            Borrower = borrow.Borrower,
            Borrowee = borrow.Borrowee,
            BorrowTarget = borrow.BorrowTarget,
            IsMutable = borrow.IsMutable,
            Lifetime = borrow.Lifetime,
            Span = borrow.Span,
            Location = borrow.Location,
            OriginLocation = borrow.OriginLocation,
            OriginSummary = borrow.OriginSummary,
            AliasTrace = borrow.AliasTrace,
            TraceId = borrow.TraceId,
            EndLocation = borrow.EndLocation
        };
    }

    public static BorrowAliasTrace.BorrowStateKey GetBorrowKey(ActiveBorrowInfo borrow)
    {
        return BorrowAliasTrace.BuildBorrowStateKey(
            borrow.Borrower,
            borrow.Borrowee,
            borrow.BorrowTarget,
            borrow.IsMutable,
            borrow.Location,
            borrow.OriginLocation,
            borrow.OriginSummary,
            borrow.TraceId,
            borrow.AliasTrace);
    }
}

internal sealed class LocalIdBitSet : IReadOnlyCollection<LocalId>
{
    private readonly IReadOnlyDictionary<LocalId, int> _localIndexMap;
    private readonly LocalId[] _localsByIndex;
    private readonly ulong[] _words;
    private int _count;

    public int Count => _count;

    public LocalIdBitSet(IReadOnlyDictionary<LocalId, int> localIndexMap, LocalId[] localsByIndex)
        : this(localIndexMap, localsByIndex, new ulong[(localsByIndex.Length + 63) / 64], 0)
    {
    }

    private LocalIdBitSet(
        IReadOnlyDictionary<LocalId, int> localIndexMap,
        LocalId[] localsByIndex,
        ulong[] words,
        int count)
    {
        _localIndexMap = localIndexMap;
        _localsByIndex = localsByIndex;
        _words = words;
        _count = count;
    }

    public bool Add(LocalId localId)
    {
        if (!TryGetIndex(localId, out var index))
        {
            return false;
        }

        var wordIndex = index >> 6;
        var mask = 1UL << (index & 63);
        if ((_words[wordIndex] & mask) != 0)
        {
            return false;
        }

        _words[wordIndex] |= mask;
        _count++;
        return true;
    }

    public bool Remove(LocalId localId)
    {
        if (!TryGetIndex(localId, out var index))
        {
            return false;
        }

        var wordIndex = index >> 6;
        var mask = 1UL << (index & 63);
        if ((_words[wordIndex] & mask) == 0)
        {
            return false;
        }

        _words[wordIndex] &= ~mask;
        _count--;
        return true;
    }

    public bool Contains(LocalId localId)
    {
        if (!TryGetIndex(localId, out var index))
        {
            return false;
        }

        var wordIndex = index >> 6;
        var mask = 1UL << (index & 63);
        return (_words[wordIndex] & mask) != 0;
    }

    public void UnionWith(LocalIdBitSet other)
    {
        for (int i = 0; i < _words.Length; i++)
        {
            _words[i] |= other._words[i];
        }

        Recount();
    }

    public bool SetEquals(LocalIdBitSet other)
    {
        if (_count != other._count)
        {
            return false;
        }

        for (int i = 0; i < _words.Length; i++)
        {
            if (_words[i] != other._words[i])
            {
                return false;
            }
        }

        return true;
    }

    public LocalIdBitSet Clone()
    {
        return new LocalIdBitSet(
            _localIndexMap,
            _localsByIndex,
            (ulong[])_words.Clone(),
            _count);
    }

    public IEnumerator<LocalId> GetEnumerator()
    {
        for (int i = 0; i < _localsByIndex.Length; i++)
        {
            var wordIndex = i >> 6;
            var mask = 1UL << (i & 63);
            if ((_words[wordIndex] & mask) != 0)
            {
                yield return _localsByIndex[i];
            }
        }
    }

    System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator()
    {
        return GetEnumerator();
    }

    private bool TryGetIndex(LocalId localId, out int index)
    {
        return _localIndexMap.TryGetValue(localId, out index);
    }

    private void Recount()
    {
        var total = 0;
        foreach (var word in _words)
        {
            total += (int)ulong.PopCount(word);
        }

        _count = total;
    }
}

internal sealed class ActiveBorrowInfo
{
    public LocalId Borrower { get; init; }
    public LocalId Borrowee { get; init; }
    public BorrowTarget BorrowTarget { get; init; }
    public bool IsMutable { get; init; }
    public LifetimeId Lifetime { get; init; }
    public SourceSpan Span { get; init; }
    public (BlockId Block, int Index) Location { get; init; }
    public (BlockId Block, int Index) OriginLocation { get; init; }
    public string OriginSummary { get; init; } = "";
    public List<string> AliasTrace { get; init; } = [];
    public string TraceId { get; init; } = "";
    public (BlockId Block, int Index)? EndLocation { get; set; }
}
