using Eidosc.Symbols;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Conservative, MIR-local facts consumed by sequence planning.
/// Unknown calls and opaque stores are treated as escaping; a failed proof
/// leaves the original pipeline lowering intact.
/// </summary>
public sealed class SequenceOptimizationFacts
{
    private static readonly CompilerSemanticRole[] NonEscapingConsumerRoles =
    [
        CompilerSemanticRole.SequenceHead,
        CompilerSemanticRole.SequenceTake,
        CompilerSemanticRole.SequenceMap,
        CompilerSemanticRole.SequenceFilter,
        CompilerSemanticRole.SequenceFlatMap,
        CompilerSemanticRole.SequenceFoldLeft,
        CompilerSemanticRole.SequenceFoldRight,
        CompilerSemanticRole.SequenceFind,
        CompilerSemanticRole.SequenceAny,
        CompilerSemanticRole.SequenceAll,
        CompilerSemanticRole.SequenceCount,
        CompilerSemanticRole.SequenceDrop,
        CompilerSemanticRole.SequenceZip,
        CompilerSemanticRole.SequenceZipWith,
        CompilerSemanticRole.SequencePartition,
        CompilerSemanticRole.SequenceReverse,
        CompilerSemanticRole.SequenceForEach,
        CompilerSemanticRole.SequenceBuilderFreeze,
        CompilerSemanticRole.FunctorMap,
        CompilerSemanticRole.ApplicativeApply,
        CompilerSemanticRole.MonadBind
    ];

    private SequenceOptimizationFacts(
        IReadOnlyDictionary<LocalId, int> readCounts,
        IReadOnlySet<LocalId> escapedLocals,
        IReadOnlySet<LocalId> aliasedLocals,
        IReadOnlySet<LocalId> borrowedLocals)
    {
        ReadCounts = readCounts;
        EscapedLocals = escapedLocals;
        AliasedLocals = aliasedLocals;
        BorrowedLocals = borrowedLocals;
    }

    public IReadOnlyDictionary<LocalId, int> ReadCounts { get; }

    public IReadOnlySet<LocalId> EscapedLocals { get; }

    /// <summary>
    /// Locals participating in an ownership-sharing operation such as
    /// <c>MirCopy</c>. A shared root is never a destructive-reuse proof.
    /// </summary>
    public IReadOnlySet<LocalId> AliasedLocals { get; }

    /// <summary>
    /// Locals observed through an active MIR borrow alias. The facts are
    /// intentionally conservative because regions are finalized later by the
    /// borrow phase; sequence planning must not assume a borrow has ended.
    /// </summary>
    public IReadOnlySet<LocalId> BorrowedLocals { get; }

    public bool IsSingleRead(LocalId local) => ReadCounts.GetValueOrDefault(local) == 1;

    public bool IsSingleUseNonEscaping(LocalId local) =>
        IsSingleRead(local) &&
        !EscapedLocals.Contains(local) &&
        !AliasedLocals.Contains(local) &&
        !BorrowedLocals.Contains(local);

    public static SequenceOptimizationFacts Analyze(MirFunc function)
    {
        var reads = function.Locals.ToDictionary(static local => local.Id, static _ => 0);
        var escaped = new HashSet<LocalId>();
        var aliased = new HashSet<LocalId>();
        var borrowed = new HashSet<LocalId>();

        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                AddInstructionReads(instruction, reads, aliased, borrowed);
                AddInstructionEscapes(instruction, escaped);
            }

            AddTerminatorReads(block.Terminator, reads);
            AddTerminatorEscapes(block.Terminator, escaped);
        }

        return new SequenceOptimizationFacts(reads, escaped, aliased, borrowed);
    }

    private static void AddInstructionReads(
        MirInstruction instruction,
        IDictionary<LocalId, int> reads,
        ISet<LocalId> aliased,
        ISet<LocalId> borrowed)
    {
        switch (instruction)
        {
            case MirAssign assign:
                AddOperandReads(assign.Source, reads);
                break;
            case MirCaseInject injection:
                AddOperandReads(injection.Operand, reads);
                break;
            case MirCall call:
                AddOperandReads(call.Function, reads);
                for (var index = 0; index < call.Arguments.Count; index++)
                {
                    var argument = call.Arguments[index];
                    AddOperandReads(argument, reads);
                    if (call.BorrowedArgumentIndices.Contains(index))
                        AddOperandLocals(argument, borrowed);
                }
                break;
            case MirBinOp binary:
                AddOperandReads(binary.Left, reads);
                AddOperandReads(binary.Right, reads);
                break;
            case MirUnaryOp unary:
                AddOperandReads(unary.Operand, reads);
                break;
            case MirSelect select:
                AddOperandReads(select.Condition, reads);
                AddOperandReads(select.TrueValue, reads);
                AddOperandReads(select.FalseValue, reads);
                break;
            case MirLoad load:
                AddOperandReads(load.Source, reads);
                if (load.CreatesBorrowAlias)
                {
                    AddOperandLocals(load.Source, borrowed);
                    AddOperandLocals(load.Target, borrowed);
                }
                break;
            case MirStore store:
                // A local store defines the target; it does not read the
                // previous value. Projection stores do read their address
                // dependencies (base and index), matching MIR place-use
                // semantics used by the ownership proofs.
                AddProjectionAddressReads(store.Target, reads);
                AddOperandReads(store.Value, reads);
                break;
            case MirCopy copy:
                AddOperandReads(copy.Source, reads);
                AddOperandLocals(copy.Source, aliased);
                AddOperandLocals(copy.Target, aliased);
                break;
            case MirMove move:
                AddOperandReads(move.Source, reads);
                break;
            case MirDrop drop:
                AddOperandReads(drop.Value, reads);
                break;
        }
    }

    private static void AddTerminatorReads(
        MirTerminator? terminator,
        IDictionary<LocalId, int> reads)
    {
        switch (terminator)
        {
            case MirReturn { Value: { } value }:
                AddOperandReads(value, reads);
                break;
            case MirSwitch switched:
                AddOperandReads(switched.Discriminant, reads);
                break;
        }
    }

    private static void AddInstructionEscapes(
        MirInstruction instruction,
        ISet<LocalId> escaped)
    {
        switch (instruction)
        {
            case MirStore store when store.Target.Kind != PlaceKind.Local:
                AddOperandLocals(store.Value, escaped);
                break;
            case MirCall call when !IsKnownNonEscapingConsumer(call):
                for (var index = 0; index < call.Arguments.Count; index++)
                {
                    // An explicit borrow is a non-retaining boundary. All
                    // other arguments to an unknown callee remain escaping.
                    if (call.BorrowedArgumentIndices.Contains(index))
                        continue;

                    AddOperandLocals(call.Arguments[index], escaped);
                }
                break;
        }
    }

    private static void AddTerminatorEscapes(
        MirTerminator? terminator,
        ISet<LocalId> escaped)
    {
        if (terminator is MirReturn { Value: { } value })
            AddOperandLocals(value, escaped);
    }

    private static bool IsKnownNonEscapingConsumer(MirCall call) =>
        call.Function is MirFunctionRef functionRef &&
        NonEscapingConsumerRoles.Contains(functionRef.CompilerSemanticRole);

    private static void AddOperandReads(
        MirOperand operand,
        IDictionary<LocalId, int> reads)
    {
        if (operand is not MirPlace place)
            return;

        foreach (var local in EnumerateLocals(place))
        {
            reads.TryGetValue(local, out var count);
            reads[local] = count + 1;
        }
    }

    private static void AddProjectionAddressReads(
        MirPlace place,
        IDictionary<LocalId, int> reads)
    {
        if (place.Kind == PlaceKind.Local)
            return;

        if (place.Base is not null)
            AddOperandReads(place.Base, reads);

        if (place.Index is not null)
            AddOperandReads(place.Index, reads);
    }

    private static void AddOperandLocals(MirOperand? operand, ISet<LocalId> escaped)
    {
        if (operand is not MirPlace place)
            return;

        foreach (var local in EnumerateLocals(place))
            escaped.Add(local);
    }

    private static IEnumerable<LocalId> EnumerateLocals(MirPlace place)
    {
        if (place.Kind == PlaceKind.Local)
            yield return place.Local;

        if (place.Base is MirPlace basePlace)
        {
            foreach (var local in EnumerateLocals(basePlace))
                yield return local;
        }

        if (place.Index is MirPlace indexPlace)
        {
            foreach (var local in EnumerateLocals(indexPlace))
                yield return local;
        }
    }
}
