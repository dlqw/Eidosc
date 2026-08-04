using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Promotes constant-capacity, non-escaping RuntimeArray values into function-owned storage.
/// </summary>
public sealed class RuntimeSequenceStoragePromotionPass :
    IMirOptimizationPass,
    IMirOptimizationMetricsProvider
{
    private const long RuntimeArrayStorageOverheadBytes = 64;
    private const long MaxInlineArrayStorageBytes = 4096;

    public string Name => "RuntimeSequenceStoragePromotion";

    public long StoragesPromoted { get; private set; }

    public IReadOnlyDictionary<string, long> GetMetricsSnapshot() =>
        new Dictionary<string, long>(StringComparer.Ordinal)
        {
            ["sequence.collectors_stack_promoted"] = StoragesPromoted
        };

    public MirModule Run(MirModule module)
    {
        StoragesPromoted = 0;
        var changed = false;

        foreach (var function in module.Functions)
        {
            if (function.IsExternal || function.BasicBlocks.Count == 0)
            {
                continue;
            }

            var storages = FindLocalArrayStorages(function);
            if (HaveSameStorages(function.CallerOwnedAggregateAbi.LocalArrayStorages, storages))
            {
                continue;
            }

            function.CallerOwnedAggregateAbi = function.CallerOwnedAggregateAbi with
            {
                LocalArrayStorages = storages
            };
            StoragesPromoted += storages.Count;
            changed = true;
        }

        return changed ? module.WithFunctions(module.Functions.ToList()) : module;
    }

    private static IReadOnlyList<MirCallerOwnedArrayStorage> FindLocalArrayStorages(MirFunc function)
    {
        var allocations = function.BasicBlocks
            .SelectMany(static block => block.Instructions)
            .OfType<MirCall>()
            .Where(static call =>
                call.Target is MirPlace { Kind: PlaceKind.Local } &&
                call.Function is MirFunctionRef functionRef &&
                MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayNew))
            .ToArray();
        var storages = new List<MirCallerOwnedArrayStorage>();

        foreach (var allocation in allocations)
        {
            var target = (MirPlace)allocation.Target!;
            if (allocation.Arguments.Count < 2 ||
                !TryGetNonNegativeConstant(allocation.Arguments[0], out var capacity) ||
                !TryGetPositiveConstant(allocation.Arguments[1], out var elementSize))
            {
                continue;
            }

            var aliases = BuildDirectLocalAliasComponent(function, target.Local);
            if (!IsSafeLocalArrayCandidate(function, allocation, aliases))
            {
                continue;
            }

            long storageBytes;
            try
            {
                storageBytes = checked(RuntimeArrayStorageOverheadBytes + checked(capacity * elementSize));
            }
            catch (OverflowException)
            {
                continue;
            }

            if (storageBytes > MaxInlineArrayStorageBytes)
            {
                continue;
            }

            storages.Add(new MirCallerOwnedArrayStorage
            {
                Key = $"{MirFunctionIdentity.GetStableKey(function)}|local-array:{target.Local.Value}",
                ArrayLocal = target.Local,
                ArrayTypeId = target.TypeId,
                Capacity = capacity,
                ElementSize = elementSize,
                StorageBytes = storageBytes,
                PromoteInline = true
            });
        }

        return storages
            .GroupBy(static storage => storage.Key, StringComparer.Ordinal)
            .Select(static group => group.First())
            .OrderBy(static storage => storage.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static HashSet<LocalId> BuildDirectLocalAliasComponent(MirFunc function, LocalId seed)
    {
        var aliases = new HashSet<LocalId> { seed };
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var instruction in function.BasicBlocks.SelectMany(static block => block.Instructions))
            {
                (LocalId Source, LocalId Target)? edge = instruction switch
                {
                    MirMove
                    {
                        Source: { Kind: PlaceKind.Local, Local: var source },
                        Target: { Kind: PlaceKind.Local, Local: var target }
                    } => (source, target),
                    MirLoad
                    {
                        Source: MirPlace { Kind: PlaceKind.Local, Local: var source },
                        Target: { Kind: PlaceKind.Local, Local: var target },
                        IsMutableBorrow: false,
                        CreatesBorrowAlias: false,
                        MovesOutOfSource: true
                    } => (source, target),
                    _ => null
                };
                if (edge is not { } localEdge)
                {
                    continue;
                }

                if (aliases.Contains(localEdge.Source))
                {
                    changed |= aliases.Add(localEdge.Target);
                }
                if (aliases.Contains(localEdge.Target))
                {
                    changed |= aliases.Add(localEdge.Source);
                }
            }
        }

        return aliases;
    }

    private static bool IsSafeLocalArrayCandidate(
        MirFunc function,
        MirCall allocation,
        IReadOnlySet<LocalId> aliases)
    {
        if (function.Locals.Any(local => local.IsParameter && aliases.Contains(local.Id)))
        {
            return false;
        }

        var allocationCount = function.BasicBlocks
            .SelectMany(static block => block.Instructions)
            .OfType<MirCall>()
            .Count(call =>
                call.Target is MirPlace { Kind: PlaceKind.Local, Local: var target } &&
                aliases.Contains(target) &&
                call.Function is MirFunctionRef functionRef &&
                IsArrayIntrinsic(functionRef, WellKnownStrings.InternalNames.ArrayNew));
        if (allocationCount != 1)
        {
            return false;
        }

        foreach (var block in function.BasicBlocks)
        {
            if (block.Terminator is MirReturn { Value: MirPlace returned } &&
                TryGetRootLocal(returned, out var returnedRoot) &&
                aliases.Contains(returnedRoot))
            {
                return false;
            }

            foreach (var instruction in block.Instructions)
            {
                if (ReferenceEquals(instruction, allocation))
                {
                    continue;
                }

                if (OverwritesAliasLocal(instruction, aliases))
                {
                    return false;
                }

                if (instruction is MirCopy { Source: MirPlace copied } &&
                    TryGetRootLocal(copied, out var copiedRoot) &&
                    aliases.Contains(copiedRoot))
                {
                    return false;
                }

                if (instruction is MirAssign { Source: MirPlace assigned } &&
                    TryGetRootLocal(assigned, out var assignedRoot) &&
                    aliases.Contains(assignedRoot))
                {
                    return false;
                }

                if (instruction is MirLoad
                    {
                        Source: MirPlace { Kind: PlaceKind.Local, Local: var loadedLocal }
                    } load &&
                    aliases.Contains(loadedLocal) &&
                    (load.IsMutableBorrow || load.CreatesBorrowAlias || !load.MovesOutOfSource))
                {
                    return false;
                }

                if (instruction is MirStore { Value: MirPlace stored } store &&
                    TryGetRootLocal(stored, out var storedRoot) &&
                    aliases.Contains(storedRoot) &&
                    (store.Target.Kind != PlaceKind.Local || !aliases.Contains(store.Target.Local)))
                {
                    return false;
                }

                if (instruction is not MirCall call ||
                    !CallUsesAliases(call, aliases))
                {
                    continue;
                }

                if (call.Function is not MirFunctionRef functionRef ||
                    !IsSafeRuntimeArrayCall(call, functionRef, aliases))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static bool OverwritesAliasLocal(
        MirInstruction instruction,
        IReadOnlySet<LocalId> aliases) => instruction switch
    {
        MirAssign { Target: { Kind: PlaceKind.Local, Local: var target } } => aliases.Contains(target),
        MirCaseInject { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => aliases.Contains(target),
        MirBinOp { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => aliases.Contains(target),
        MirUnaryOp { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => aliases.Contains(target),
        MirLoad
        {
            Target: { Kind: PlaceKind.Local, Local: var target },
            Source: MirPlace { Kind: PlaceKind.Local, Local: var source },
            IsMutableBorrow: false,
            CreatesBorrowAlias: false,
            MovesOutOfSource: true
        } => aliases.Contains(target) && !aliases.Contains(source),
        MirLoad { Target: { Kind: PlaceKind.Local, Local: var target } } => aliases.Contains(target),
        MirStore { Target: { Kind: PlaceKind.Local, Local: var target } } => aliases.Contains(target),
        MirCopy { Target: { Kind: PlaceKind.Local, Local: var target } } => aliases.Contains(target),
        MirMove
        {
            Target: { Kind: PlaceKind.Local, Local: var target },
            Source: { Kind: PlaceKind.Local, Local: var source }
        } => aliases.Contains(target) && !aliases.Contains(source),
        MirMove { Target: { Kind: PlaceKind.Local, Local: var target } } => aliases.Contains(target),
        MirAlloc { Target: { Kind: PlaceKind.Local, Local: var target } } => aliases.Contains(target),
        _ => false
    };

    private static bool IsSafeRuntimeArrayCall(
        MirCall call,
        MirFunctionRef functionRef,
        IReadOnlySet<LocalId> aliases)
    {
        if (IsArrayIntrinsic(functionRef, WellKnownStrings.InternalNames.ArrayLength))
        {
            return call.Arguments.Count == 1 && IsAlias(call.Arguments[0], aliases);
        }

        if (IsArrayIntrinsic(functionRef, WellKnownStrings.InternalNames.ArrayPush))
        {
            return call.Target is MirPlace { Kind: PlaceKind.Local, Local: var target } &&
                   aliases.Contains(target) &&
                   call.Arguments.Count >= 1 &&
                   IsAlias(call.Arguments[0], aliases);
        }

        if (IsArrayIntrinsic(functionRef, WellKnownStrings.InternalNames.ArraySet))
        {
            return call.Arguments.Count >= 1 && IsAlias(call.Arguments[0], aliases);
        }

        return false;
    }

    private static bool IsArrayIntrinsic(MirFunctionRef functionRef, string intrinsicName) =>
        MirRuntimeFunctions.HasIdentity(functionRef, intrinsicName) ||
        (MirBuiltinFunctions.TryGetIntrinsicName(functionRef, out var name) &&
         string.Equals(name, intrinsicName, StringComparison.Ordinal));

    private static bool CallUsesAliases(MirCall call, IReadOnlySet<LocalId> aliases) =>
        (call.Target is MirPlace target &&
         TryGetRootLocal(target, out var targetRoot) &&
         aliases.Contains(targetRoot)) ||
        call.Arguments.OfType<MirPlace>().Any(argument =>
            TryGetRootLocal(argument, out var argumentRoot) && aliases.Contains(argumentRoot));

    private static bool IsAlias(MirOperand operand, IReadOnlySet<LocalId> aliases) =>
        operand is MirPlace place &&
        TryGetRootLocal(place, out var root) &&
        aliases.Contains(root);

    private static bool TryGetRootLocal(MirPlace place, out LocalId root)
    {
        var current = place;
        while (current.Kind != PlaceKind.Local)
        {
            if (current.Base is not MirPlace parent)
            {
                root = LocalId.None;
                return false;
            }
            current = parent;
        }

        root = current.Local;
        return root.IsValid;
    }

    private static bool TryGetNonNegativeConstant(MirOperand operand, out long value)
    {
        value = operand is MirConstant { Value: MirConstantValue.IntValue(var constant) } ? constant : -1;
        return value >= 0;
    }

    private static bool TryGetPositiveConstant(MirOperand operand, out long value)
    {
        value = operand is MirConstant { Value: MirConstantValue.IntValue(var constant) } ? constant : 0;
        return value > 0;
    }

    private static bool HaveSameStorages(
        IReadOnlyList<MirCallerOwnedArrayStorage> left,
        IReadOnlyList<MirCallerOwnedArrayStorage> right) =>
        left.Count == right.Count && left.SequenceEqual(right);
}
