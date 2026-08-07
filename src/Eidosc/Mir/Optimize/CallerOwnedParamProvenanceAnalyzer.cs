using Eidosc.Borrow;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

internal enum CallerOwnedParamProvenance
{
    Untracked,
    DirectParamDerived,
    ContainsParamDerived,
    Escaped,
    Unknown
}

internal sealed record CallerOwnedParamEscapeInfo(
    bool RequiresHeapFallback,
    CallerOwnedParamProvenance ReturnProvenance)
{
    public static CallerOwnedParamEscapeInfo Unknown { get; } = new(
        RequiresHeapFallback: true,
        ReturnProvenance: CallerOwnedParamProvenance.Unknown);

    public bool EscapesToMemory => RequiresHeapFallback;

    public bool ReturnsParamDerived => ReturnProvenance.IsParamDerived();
}

internal sealed class CallerOwnedParamProvenanceAnalyzer
{
    private readonly Func<MirFunctionRef, MirFunc?> _resolveFunction;
    private readonly Func<MirFunc, int, CallerOwnedParamEscapeInfo> _analyzeParameter;
    private readonly Func<MirFunc, bool> _isKnownSafeIntrinsicTemplate;
    private readonly Func<MirFunctionRef, bool> _isReadOnlyIntrinsic;
    private readonly Func<MirFunctionRef, bool> _isArrayReturningIntrinsic;
    private bool _requiresHeapFallback;
    private CallerOwnedParamProvenance _returnProvenance;

    public CallerOwnedParamProvenanceAnalyzer(
        Func<MirFunctionRef, MirFunc?> resolveFunction,
        Func<MirFunc, int, CallerOwnedParamEscapeInfo> analyzeParameter,
        Func<MirFunc, bool> isKnownSafeIntrinsicTemplate,
        Func<MirFunctionRef, bool> isReadOnlyIntrinsic,
        Func<MirFunctionRef, bool> isArrayReturningIntrinsic)
    {
        _resolveFunction = resolveFunction;
        _analyzeParameter = analyzeParameter;
        _isKnownSafeIntrinsicTemplate = isKnownSafeIntrinsicTemplate;
        _isReadOnlyIntrinsic = isReadOnlyIntrinsic;
        _isArrayReturningIntrinsic = isArrayReturningIntrinsic;
    }

    public CallerOwnedParamEscapeInfo Analyze(MirFunc function, int parameterIndex)
    {
        var parameters = function.Locals.Where(static local => local.IsParameter).ToArray();
        if (parameterIndex < 0 || parameterIndex >= parameters.Length)
        {
            return CallerOwnedParamEscapeInfo.Unknown;
        }

        if (function.BasicBlocks.Count == 0 && _isKnownSafeIntrinsicTemplate(function))
        {
            return new CallerOwnedParamEscapeInfo(
                RequiresHeapFallback: false,
                ReturnProvenance: CallerOwnedParamProvenance.Untracked);
        }

        if (!function.EntryBlockId.IsValid)
        {
            return CallerOwnedParamEscapeInfo.Unknown;
        }

        var blocks = function.BasicBlocks.ToDictionary(static block => block.Id);
        if (!blocks.ContainsKey(function.EntryBlockId))
        {
            return CallerOwnedParamEscapeInfo.Unknown;
        }

        var controlFlow = new ControlFlowGraph(function);
        var entryState = new ProvenanceState();
        entryState.SetLocal(
            parameters[parameterIndex].Id,
            CallerOwnedParamProvenance.DirectParamDerived);

        var inputStates = new Dictionary<BlockId, ProvenanceState>
        {
            [function.EntryBlockId] = entryState
        };
        var pending = new Queue<BlockId>();
        var queued = new HashSet<BlockId>();
        pending.Enqueue(function.EntryBlockId);
        queued.Add(function.EntryBlockId);

        while (pending.Count > 0)
        {
            var blockId = pending.Dequeue();
            queued.Remove(blockId);
            if (!blocks.TryGetValue(blockId, out var block))
            {
                MarkFallback();
                continue;
            }

            var state = inputStates[blockId].Clone();
            foreach (var instruction in block.Instructions)
            {
                TransferInstruction(instruction, state);
            }

            ObserveTerminator(block.Terminator, state);
            foreach (var successor in controlFlow.GetSuccessors(blockId))
            {
                if (!blocks.ContainsKey(successor))
                {
                    MarkFallback();
                    continue;
                }

                if (!inputStates.TryGetValue(successor, out var successorState))
                {
                    inputStates[successor] = state.Clone();
                    if (queued.Add(successor))
                    {
                        pending.Enqueue(successor);
                    }

                    continue;
                }

                if (successorState.JoinFrom(state) && queued.Add(successor))
                {
                    pending.Enqueue(successor);
                }
            }
        }

        return new CallerOwnedParamEscapeInfo(_requiresHeapFallback, _returnProvenance);
    }

    private void TransferInstruction(MirInstruction instruction, ProvenanceState state)
    {
        switch (instruction)
        {
            case MirAssign assign:
                TransferValue(assign.Target, assign.Source, state, movesSource: false);
                break;

            case MirCopy copy:
                TransferValue(copy.Target, copy.Source, state, movesSource: false);
                break;

            case MirMove move:
                TransferValue(move.Target, move.Source, state, movesSource: true);
                break;

            case MirLoad load:
                TransferValue(load.Target, load.Source, state, load.MovesOutOfSource);
                break;

            case MirStore store:
                var stored = ReadOperand(store.Value, state);
                if (!WritePlace(store.Target, stored, state) && stored.IsParamDerived())
                {
                    MarkEscaped();
                }
                break;

            case MirCaseInject injection:
                var injected = ReadOperand(injection.Operand, state);
                WriteContainedValue(injection.Target, injected, state);
                break;

            case MirCall call:
                TransferCall(call, state);
                break;

            case MirDrop:
                // A drop consumes the value inside the callee frame. Borrow
                // checking rejects passing a projected alias to an owning
                // parameter; caller-owned parameter variants handle their
                // own nested storage without leaking it outside the frame.
                break;

            case MirBinOp binary:
                TransferOpaqueOperation(binary.Target, [binary.Left, binary.Right], state);
                break;

            case MirUnaryOp unary:
                TransferOpaqueOperation(unary.Target, [unary.Operand], state);
                break;

            case MirSelect select:
                TransferOpaqueOperation(select.Target, [select.Condition, select.TrueValue, select.FalseValue], state);
                break;

            case MirAlloc alloc:
                state.SetLocal(alloc.Target.Local, CallerOwnedParamProvenance.Untracked);
                break;
        }
    }

    private void TransferValue(
        MirPlace target,
        MirOperand source,
        ProvenanceState state,
        bool movesSource)
    {
        if (target.Kind == PlaceKind.Local &&
            source is MirPlace { Kind: PlaceKind.Local } sourceLocal)
        {
            state.CopyLocal(sourceLocal.Local, target.Local);
            if (movesSource)
            {
                state.SetLocal(sourceLocal.Local, CallerOwnedParamProvenance.Untracked);
            }

            return;
        }

        var provenance = ReadOperand(source, state);
        if (!WritePlace(target, provenance, state) && provenance.IsParamDerived())
        {
            MarkEscaped();
        }

        if (movesSource && source is MirPlace sourcePlace)
        {
            WritePlace(sourcePlace, CallerOwnedParamProvenance.Untracked, state);
        }
    }

    private void TransferOpaqueOperation(
        MirOperand target,
        IReadOnlyList<MirOperand> operands,
        ProvenanceState state)
    {
        if (operands.Any(operand => ReadOperand(operand, state).IsParamDerived()))
        {
            MarkFallback();
            WriteOperand(target, CallerOwnedParamProvenance.Unknown, state);
            return;
        }

        WriteOperand(target, CallerOwnedParamProvenance.Untracked, state);
    }

    private void TransferCall(MirCall call, ProvenanceState state)
    {
        var argumentProvenances = call.Arguments
            .Select(argument => ReadOperand(argument, state))
            .ToArray();
        var recordUpdateProvenance = call.RecordUpdate == null
            ? CallerOwnedParamProvenance.Untracked
            : ReadOperand(call.RecordUpdate.Source, state);
        var hasDerivedInput = recordUpdateProvenance.IsParamDerived() ||
                              argumentProvenances.Any(static provenance => provenance.IsParamDerived());

        if (call.Function is not MirFunctionRef functionRef)
        {
            // Invoking a closure borrows its hidden environment for the duration of
            // the call. The callable value is not itself passed to or retained by
            // the invoked body, so callable provenance does not escape here.
            if (hasDerivedInput)
            {
                MarkEscaped();
            }

            WriteCallTarget(call, hasDerivedInput
                ? CallerOwnedParamProvenance.Unknown
                : CallerOwnedParamProvenance.Untracked, state);
            return;
        }

        if (TypeSemantics.IsAdtConstructorCall(functionRef))
        {
            WriteConstructorTarget(call, argumentProvenances, recordUpdateProvenance, state);
            return;
        }

        if (_isReadOnlyIntrinsic(functionRef))
        {
            WriteCallTarget(call, CallerOwnedParamProvenance.Untracked, state);
            return;
        }

        if (_isArrayReturningIntrinsic(functionRef))
        {
            var returned = JoinAll(argumentProvenances.Append(recordUpdateProvenance));
            if (returned == CallerOwnedParamProvenance.ContainsParamDerived)
            {
                returned = CallerOwnedParamProvenance.Unknown;
                MarkFallback();
            }

            WriteCallTarget(call, returned, state);
            return;
        }

        if (!hasDerivedInput)
        {
            WriteCallTarget(call, CallerOwnedParamProvenance.Untracked, state);
            return;
        }

        var callee = _resolveFunction(functionRef);
        if (callee == null || callee.IsExternal || callee.IsRuntimeWordAbi ||
            (callee.BasicBlocks.Count == 0 && !_isKnownSafeIntrinsicTemplate(callee)))
        {
            MarkEscaped();
            WriteCallTarget(call, CallerOwnedParamProvenance.Unknown, state);
            return;
        }

        var parameters = callee.Locals.Where(static local => local.IsParameter).ToArray();
        if (call.Arguments.Count < parameters.Length)
        {
            if (recordUpdateProvenance.IsParamDerived())
            {
                MarkFallback();
                WriteCallTarget(call, CallerOwnedParamProvenance.Unknown, state);
                return;
            }

            var capturedProvenance = JoinAll(argumentProvenances);
            WriteCallTarget(
                call,
                capturedProvenance.IsParamDerived()
                    ? CallerOwnedParamProvenance.ContainsParamDerived
                    : CallerOwnedParamProvenance.Untracked,
                state);
            return;
        }

        var returnedProvenance = CallerOwnedParamProvenance.Untracked;
        for (var index = 0; index < argumentProvenances.Length; index++)
        {
            var argumentProvenance = argumentProvenances[index];
            if (!argumentProvenance.IsParamDerived())
            {
                continue;
            }

            if (index >= parameters.Length)
            {
                MarkFallback();
                returnedProvenance = CallerOwnedParamProvenance.Unknown;
                break;
            }

            var calleeInfo = _analyzeParameter(callee, index);
            if (calleeInfo.RequiresHeapFallback)
            {
                MarkFallback();
            }

            returnedProvenance = returnedProvenance.Join(
                TransformReturnedProvenance(argumentProvenance, calleeInfo.ReturnProvenance));
        }

        if (recordUpdateProvenance.IsParamDerived())
        {
            MarkFallback();
            returnedProvenance = CallerOwnedParamProvenance.Unknown;
        }

        WriteCallTarget(call, returnedProvenance, state);
    }

    private static CallerOwnedParamProvenance TransformReturnedProvenance(
        CallerOwnedParamProvenance argument,
        CallerOwnedParamProvenance returned) => returned switch
        {
            CallerOwnedParamProvenance.Untracked => CallerOwnedParamProvenance.Untracked,
            CallerOwnedParamProvenance.DirectParamDerived => argument,
            CallerOwnedParamProvenance.ContainsParamDerived =>
                CallerOwnedParamProvenance.ContainsParamDerived,
            CallerOwnedParamProvenance.Escaped => CallerOwnedParamProvenance.Escaped,
            _ => CallerOwnedParamProvenance.Unknown
        };

    private void WriteConstructorTarget(
        MirCall call,
        IReadOnlyList<CallerOwnedParamProvenance> arguments,
        CallerOwnedParamProvenance recordUpdate,
        ProvenanceState state)
    {
        if (call.Target is not MirPlace { Kind: PlaceKind.Local } target)
        {
            if (arguments.Any(static argument => argument.IsParamDerived()) || recordUpdate.IsParamDerived())
            {
                MarkEscaped();
            }

            return;
        }

        state.ResetLocal(target.Local);
        for (var index = 0; index < arguments.Count; index++)
        {
            state.SetSyntheticAggregateField(target.Local, index, arguments[index]);
        }

        if (recordUpdate.IsParamDerived())
        {
            state.SetImpreciseContainer(target.Local, recordUpdate);
        }
    }

    private static CallerOwnedParamProvenance JoinAll(
        IEnumerable<CallerOwnedParamProvenance> provenances)
    {
        var result = CallerOwnedParamProvenance.Untracked;
        foreach (var provenance in provenances)
        {
            result = result.Join(provenance);
        }

        return result;
    }

    private void WriteContainedValue(
        MirOperand target,
        CallerOwnedParamProvenance value,
        ProvenanceState state)
    {
        if (target is not MirPlace targetPlace)
        {
            if (value.IsParamDerived())
            {
                MarkFallback();
            }

            return;
        }

        if (targetPlace.Kind == PlaceKind.Local)
        {
            if (value.IsParamDerived())
            {
                state.SetImpreciseContainer(
                    targetPlace.Local,
                    CallerOwnedParamProvenance.ContainsParamDerived);
            }
            else
            {
                state.SetLocal(targetPlace.Local, CallerOwnedParamProvenance.Untracked);
            }

            return;
        }

        if (!WritePlace(targetPlace, value, state) && value.IsParamDerived())
        {
            MarkEscaped();
        }
    }

    private void WriteCallTarget(
        MirCall call,
        CallerOwnedParamProvenance provenance,
        ProvenanceState state)
    {
        if (call.Target == null)
        {
            if (provenance.IsParamDerived())
            {
                MarkFallback();
            }

            return;
        }

        if (!WritePlace(call.Target, provenance, state) && provenance.IsParamDerived())
        {
            MarkEscaped();
        }
    }

    private static void WriteOperand(
        MirOperand operand,
        CallerOwnedParamProvenance provenance,
        ProvenanceState state)
    {
        if (operand is MirPlace place)
        {
            WritePlace(place, provenance, state);
        }
    }

    private static bool WritePlace(
        MirPlace place,
        CallerOwnedParamProvenance provenance,
        ProvenanceState state)
    {
        if (place.Kind == PlaceKind.Local)
        {
            state.SetLocal(place.Local, provenance);
            return true;
        }

        if (TryGetAggregateFieldKey(place, out var field))
        {
            state.SetAggregateField(field, provenance);
            return true;
        }

        return false;
    }

    private static CallerOwnedParamProvenance ReadOperand(
        MirOperand? operand,
        ProvenanceState state) => operand switch
        {
            MirPlace place => ReadPlace(place, state),
            _ => CallerOwnedParamProvenance.Untracked
        };

    private static CallerOwnedParamProvenance ReadPlace(
        MirPlace place,
        ProvenanceState state)
    {
        if (place.Kind == PlaceKind.Local)
        {
            return state.GetLocal(place.Local);
        }

        if (TryGetAggregateFieldKey(place, out var field))
        {
            if (state.TryGetAggregateField(field, out var fieldProvenance))
            {
                return fieldProvenance;
            }

            return state.IsImpreciseContainer(field.Root)
                ? CallerOwnedParamProvenance.Unknown
                : CallerOwnedParamProvenance.Untracked;
        }

        var root = ResolveRootLocal(place);
        return root.HasValue && state.GetLocal(root.Value).IsParamDerived()
            ? CallerOwnedParamProvenance.Unknown
            : CallerOwnedParamProvenance.Untracked;
    }

    private void ObserveTerminator(MirTerminator? terminator, ProvenanceState state)
    {
        if (terminator is not MirReturn { Value: { } returned })
        {
            return;
        }

        var provenance = ReadOperand(returned, state);
        _returnProvenance = _returnProvenance.Join(provenance);
        if (provenance is CallerOwnedParamProvenance.Unknown or CallerOwnedParamProvenance.Escaped)
        {
            MarkFallback();
        }
    }

    private void MarkEscaped()
    {
        _requiresHeapFallback = true;
    }

    private void MarkFallback()
    {
        _requiresHeapFallback = true;
    }

    private static bool TryGetAggregateFieldKey(
        MirPlace place,
        out AggregateFieldKey key)
    {
        var segments = new List<ProjectionSegment>();
        MirPlace? current = place;
        while (current != null && current.Kind != PlaceKind.Local)
        {
            switch (current.Kind)
            {
                case PlaceKind.Field when current.Base != null:
                    segments.Add(new ProjectionSegment(
                        PlaceKind.Field,
                        current.FieldName ?? "",
                        null));
                    current = current.Base;
                    continue;

                case PlaceKind.Index when current.IndexAccessKind == MirIndexAccessKind.Aggregate &&
                                          current.Base != null &&
                                          current.Index is MirConstant index:
                    segments.Add(new ProjectionSegment(
                        PlaceKind.Index,
                        "",
                        index.Value));
                    current = current.Base;
                    continue;

                default:
                    key = null!;
                    return false;
            }
        }

        if (current is not { Kind: PlaceKind.Local } || segments.Count == 0)
        {
            key = null!;
            return false;
        }

        segments.Reverse();
        key = new AggregateFieldKey(current.Local, segments);
        return true;
    }

    private static LocalId? ResolveRootLocal(MirPlace place)
    {
        MirPlace? current = place;
        while (current != null)
        {
            if (current.Kind == PlaceKind.Local)
            {
                return current.Local;
            }

            current = current.Base;
        }

        return null;
    }

    private readonly record struct ProjectionSegment(
        PlaceKind Kind,
        string FieldName,
        MirConstantValue? Index);

    private sealed class AggregateFieldKey : IEquatable<AggregateFieldKey>
    {
        private readonly ProjectionSegment[] _segments;

        public AggregateFieldKey(LocalId root, IEnumerable<ProjectionSegment> segments)
        {
            Root = root;
            _segments = segments.ToArray();
        }

        public LocalId Root { get; }

        public AggregateFieldKey Rebase(LocalId root) => new(root, _segments);

        public bool Equals(AggregateFieldKey? other) =>
            other != null &&
            Root == other.Root &&
            _segments.AsSpan().SequenceEqual(other._segments);

        public override bool Equals(object? obj) => obj is AggregateFieldKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(Root);
            foreach (var segment in _segments)
            {
                hash.Add(segment);
            }

            return hash.ToHashCode();
        }
    }

    private sealed class ProvenanceState
    {
        private readonly Dictionary<LocalId, CallerOwnedParamProvenance> _locals = [];
        private readonly Dictionary<AggregateFieldKey, CallerOwnedParamProvenance> _aggregateFields = [];
        private readonly HashSet<LocalId> _impreciseContainers = [];

        public ProvenanceState Clone()
        {
            var clone = new ProvenanceState();
            foreach (var (local, provenance) in _locals)
            {
                clone._locals[local] = provenance;
            }

            foreach (var (field, provenance) in _aggregateFields)
            {
                clone._aggregateFields[field] = provenance;
            }

            clone._impreciseContainers.UnionWith(_impreciseContainers);
            return clone;
        }

        public CallerOwnedParamProvenance GetLocal(LocalId local) =>
            _locals.GetValueOrDefault(local);

        public void ResetLocal(LocalId local)
        {
            _locals.Remove(local);
            _impreciseContainers.Remove(local);
            foreach (var field in _aggregateFields.Keys.Where(field => field.Root == local).ToArray())
            {
                _aggregateFields.Remove(field);
            }
        }

        public void SetLocal(LocalId local, CallerOwnedParamProvenance provenance)
        {
            ResetLocal(local);
            if (provenance != CallerOwnedParamProvenance.Untracked)
            {
                _locals[local] = provenance;
            }

            if (provenance == CallerOwnedParamProvenance.ContainsParamDerived)
            {
                _impreciseContainers.Add(local);
            }
        }

        public void CopyLocal(LocalId source, LocalId target)
        {
            var sourceProvenance = GetLocal(source);
            var sourceFields = _aggregateFields
                .Where(pair => pair.Key.Root == source)
                .Select(pair => (Field: pair.Key.Rebase(target), pair.Value))
                .ToArray();
            var isImprecise = _impreciseContainers.Contains(source);

            ResetLocal(target);
            if (sourceProvenance != CallerOwnedParamProvenance.Untracked)
            {
                _locals[target] = sourceProvenance;
            }

            foreach (var (field, provenance) in sourceFields)
            {
                _aggregateFields[field] = provenance;
            }

            if (isImprecise)
            {
                _impreciseContainers.Add(target);
            }
        }

        public void SetSyntheticAggregateField(
            LocalId root,
            int index,
            CallerOwnedParamProvenance provenance)
        {
            var key = new AggregateFieldKey(
                root,
                [new ProjectionSegment(
                    PlaceKind.Index,
                    "",
                    new MirConstantValue.IntValue(index))]);
            SetAggregateField(key, provenance);
        }

        public void SetAggregateField(
            AggregateFieldKey field,
            CallerOwnedParamProvenance provenance)
        {
            if (provenance == CallerOwnedParamProvenance.Untracked)
            {
                _aggregateFields.Remove(field);
            }
            else
            {
                _aggregateFields[field] = provenance;
            }

            RecomputeAggregateRoot(field.Root);
        }

        public void SetImpreciseContainer(
            LocalId local,
            CallerOwnedParamProvenance provenance)
        {
            ResetLocal(local);
            if (provenance == CallerOwnedParamProvenance.Untracked)
            {
                return;
            }

            _locals[local] = provenance == CallerOwnedParamProvenance.DirectParamDerived
                ? CallerOwnedParamProvenance.ContainsParamDerived
                : provenance;
            _impreciseContainers.Add(local);
        }

        public bool IsImpreciseContainer(LocalId local) => _impreciseContainers.Contains(local);

        public bool TryGetAggregateField(
            AggregateFieldKey field,
            out CallerOwnedParamProvenance provenance) =>
            _aggregateFields.TryGetValue(field, out provenance);

        public bool JoinFrom(ProvenanceState incoming)
        {
            var changed = false;
            foreach (var local in _locals.Keys.Concat(incoming._locals.Keys).Distinct().ToArray())
            {
                var joined = GetLocal(local).Join(incoming.GetLocal(local));
                if (joined == GetLocal(local))
                {
                    continue;
                }

                if (joined == CallerOwnedParamProvenance.Untracked)
                {
                    _locals.Remove(local);
                }
                else
                {
                    _locals[local] = joined;
                }

                changed = true;
            }

            foreach (var field in _aggregateFields.Keys
                         .Concat(incoming._aggregateFields.Keys)
                         .Distinct()
                         .ToArray())
            {
                var current = _aggregateFields.GetValueOrDefault(field);
                var joined = current.Join(incoming._aggregateFields.GetValueOrDefault(field));
                if (joined == current)
                {
                    continue;
                }

                if (joined == CallerOwnedParamProvenance.Untracked)
                {
                    _aggregateFields.Remove(field);
                }
                else
                {
                    _aggregateFields[field] = joined;
                }

                changed = true;
            }

            var previousImpreciseCount = _impreciseContainers.Count;
            _impreciseContainers.UnionWith(incoming._impreciseContainers);
            return changed || _impreciseContainers.Count != previousImpreciseCount;
        }

        private void RecomputeAggregateRoot(LocalId root)
        {
            var current = GetLocal(root);
            if (current is CallerOwnedParamProvenance.DirectParamDerived or
                CallerOwnedParamProvenance.Escaped or
                CallerOwnedParamProvenance.Unknown)
            {
                return;
            }

            var aggregate = _impreciseContainers.Contains(root)
                ? CallerOwnedParamProvenance.ContainsParamDerived
                : CallerOwnedParamProvenance.Untracked;
            foreach (var provenance in _aggregateFields
                         .Where(pair => pair.Key.Root == root)
                         .Select(static pair => pair.Value))
            {
                if (provenance.IsParamDerived())
                {
                    aggregate = aggregate.Join(CallerOwnedParamProvenance.ContainsParamDerived);
                }
            }

            if (aggregate == CallerOwnedParamProvenance.Untracked)
            {
                _locals.Remove(root);
            }
            else
            {
                _locals[root] = aggregate;
            }
        }
    }
}

internal static class CallerOwnedParamProvenanceExtensions
{
    public static bool IsParamDerived(this CallerOwnedParamProvenance provenance) =>
        provenance != CallerOwnedParamProvenance.Untracked;

    public static CallerOwnedParamProvenance Join(
        this CallerOwnedParamProvenance left,
        CallerOwnedParamProvenance right)
    {
        if (left == right)
        {
            return left;
        }

        if (left == CallerOwnedParamProvenance.Untracked)
        {
            return right;
        }

        if (right == CallerOwnedParamProvenance.Untracked)
        {
            return left;
        }

        if (left == CallerOwnedParamProvenance.Escaped ||
            right == CallerOwnedParamProvenance.Escaped)
        {
            return CallerOwnedParamProvenance.Escaped;
        }

        return CallerOwnedParamProvenance.Unknown;
    }
}
