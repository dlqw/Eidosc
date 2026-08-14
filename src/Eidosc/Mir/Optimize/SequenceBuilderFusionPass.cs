using Eidosc.Borrow;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Removes the representation-only SeqBuilder.freeze wrapper when the builder
/// is consumed exactly once. The public builder contract remains unchanged;
/// the MIR result is a move of the wrapped RuntimeArray storage.
/// </summary>
public sealed class SequenceBuilderFusionPass :
    IMirOptimizationPass,
    IMirOptimizationMetricsProvider,
    IOwnershipAnalysisSnapshotConsumer
{
    public string Name => "SequenceBuilderFusion";

    public long FreezesElided { get; private set; }

    private IReadOnlyDictionary<string, OwnershipAnalysisSnapshot> _ownershipSnapshots =
        new Dictionary<string, OwnershipAnalysisSnapshot>(StringComparer.Ordinal);

    IReadOnlyDictionary<string, OwnershipAnalysisSnapshot> IOwnershipAnalysisSnapshotConsumer.OwnershipSnapshots
    {
        set => _ownershipSnapshots = value ??
            new Dictionary<string, OwnershipAnalysisSnapshot>(StringComparer.Ordinal);
    }

    public IReadOnlyDictionary<string, long> GetMetricsSnapshot() =>
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["sequence.builder.freezes_elided"] = FreezesElided
        };

    public MirModule Run(MirModule module)
    {
        FreezesElided = 0;
        var changed = false;
        foreach (var function in module.Functions.Where(static function => !function.IsExternal))
        {
            if (!_ownershipSnapshots.TryGetValue(
                    MirFunctionIdentity.GetStableKey(function),
                    out var snapshot))
            {
                continue;
            }

            foreach (var block in function.BasicBlocks)
            {
                for (var index = 0; index < block.Instructions.Count; index++)
                {
                    if (block.Instructions[index] is not MirCall
                        {
                            Target: MirPlace { Kind: PlaceKind.Local } target,
                        Function: MirFunctionRef functionRef,
                        Arguments: [MirPlace { Kind: PlaceKind.Local } builder]
                        } call ||
                        functionRef.CompilerSemanticRole != CompilerSemanticRole.SequenceBuilderFreeze ||
                        CountUses(function, builder.Local) != 1 ||
                        !HasSingleFieldWrapperLayout(module, builder.TypeId, target.TypeId) ||
                        !snapshot.CanDestructivelyUpdate(builder.Local, block.Id, index))
                    {
                        continue;
                    }

                    block.Instructions[index] = new MirLoad
                    {
                        Target = target,
                        Source = new MirPlace
                        {
                            Kind = PlaceKind.Index,
                            Base = builder,
                            Index = new MirConstant
                            {
                                Value = new MirConstantValue.IntValue(0),
                                TypeId = new TypeId(BaseTypes.IntId),
                                Span = call.Span
                            },
                            IndexAccessKind = MirIndexAccessKind.Aggregate,
                            TypeId = target.TypeId,
                            Span = call.Span
                        },
                        CreatesBorrowAlias = false,
                        MovesOutOfSource = true,
                        Span = call.Span
                    };
                    FreezesElided++;
                    changed = true;
                }
            }
        }

        return changed ? module.WithFunctions(module.Functions.ToList()) : module;
    }

    private static bool HasSingleFieldWrapperLayout(
        MirModule module,
        TypeId builderType,
        TypeId sequenceType) =>
        module.ConstructorLayouts.TryGetValue(builderType.Value, out var layouts) &&
        layouts is [{ FieldTypeIds: [var fieldType] }] &&
        fieldType == sequenceType;

    private static int CountUses(MirFunc function, LocalId local) =>
        function.BasicBlocks.Sum(block =>
            block.Instructions.Sum(instruction => CountUses(instruction, local)) +
            CountUses(block.Terminator, local));

    private static int CountUses(MirInstruction instruction, LocalId local) => instruction switch
    {
        MirCall call => call.Arguments.Sum(argument => CountUses(argument, local)),
        MirLoad load => CountUses(load.Source, local),
        MirMove move => CountUses(move.Source, local),
        MirCopy copy => CountUses(copy.Source, local),
        MirDrop drop => CountUses(drop.Value, local),
        MirAssign assign => CountUses(assign.Source, local),
        MirStore store => CountUses(store.Target, local) + CountUses(store.Value, local),
        _ => 0
    };

    private static int CountUses(MirTerminator? terminator, LocalId local) => terminator switch
    {
        MirReturn ret => CountUses(ret.Value, local),
        MirSwitch branch => CountUses(branch.Discriminant, local),
        _ => 0
    };

    private static int CountUses(MirOperand? operand, LocalId local) => operand is not MirPlace place
        ? 0
        : (place.Kind == PlaceKind.Local && place.Local == local ? 1 : 0) +
          CountUses(place.Base, local) + CountUses(place.Index, local);
}
