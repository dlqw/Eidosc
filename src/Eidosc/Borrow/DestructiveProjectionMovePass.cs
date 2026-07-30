using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;

namespace Eidosc.Borrow;

/// <summary>
/// Converts an owned projected read into a destructive move when the enclosing
/// owner is dropped on every continuation path. Clearing the moved slot lets the
/// ordinary aggregate/destructor path release every remaining field exactly
/// once without an otherwise redundant retain/release pair.
/// </summary>
public sealed class DestructiveProjectionMovePass : IMirOptimizationPass
{
    public string Name => "DestructiveProjectionMove";

    public MirModule Run(MirModule module)
    {
        foreach (var function in module.Functions)
        {
            var aliases = BuildAliasRoots(function);
            var cfg = new ControlFlowGraph(function);
            var blocksById = function.BasicBlocks.ToDictionary(static block => block.Id);
            foreach (var block in function.BasicBlocks)
            {
                MarkDestructiveLoads(module, block, aliases, cfg, blocksById);
            }
        }

        return module;
    }

    private static void MarkDestructiveLoads(
        MirModule module,
        MirBasicBlock block,
        IReadOnlyDictionary<LocalId, LocalId> aliases,
        ControlFlowGraph cfg,
        IReadOnlyDictionary<BlockId, MirBasicBlock> blocksById)
    {
        for (var loadIndex = 0; loadIndex < block.Instructions.Count; loadIndex++)
        {
            if (block.Instructions[loadIndex] is not MirLoad
                {
                    CreatesBorrowAlias: false,
                    IsMutableBorrow: false,
                    MovesOutOfSource: false,
                    Target: { Kind: PlaceKind.Local, TypeId: var targetTypeId },
                    Source: MirPlace { Kind: PlaceKind.Field or PlaceKind.Index } source
                } load ||
                !IsManagedOwnedType(module, targetTypeId) ||
                !TryNormalizeProjection(source, aliases, out var projection))
            {
                continue;
            }

            if (!OwnerIsDroppedOnEveryPath(
                    block,
                    loadIndex + 1,
                    projection,
                    aliases,
                    cfg,
                    blocksById,
                    []))
            {
                continue;
            }

            block.Instructions[loadIndex] = load with
            {
                Source = RewriteProjectionRoot(source, projection.Root),
                MovesOutOfSource = true
            };
        }
    }

    private static MirPlace RewriteProjectionRoot(MirPlace place, LocalId root)
    {
        if (place.Kind == PlaceKind.Local)
        {
            return place with { Local = root };
        }

        return place.Base is null
            ? place
            : place with { Base = RewriteProjectionRoot(place.Base, root) };
    }

    private static Dictionary<LocalId, LocalId> BuildAliasRoots(MirFunc function)
    {
        var roots = new Dictionary<LocalId, LocalId>();
        var definitions = function.BasicBlocks
            .SelectMany(static block => block.Instructions)
            .SelectMany(instruction => function.Locals
                .Where(local => DefinesLocal(instruction, local.Id))
                .Select(static local => local.Id))
            .GroupBy(static local => local)
            .ToDictionary(static group => group.Key, static group => group.Count());
        foreach (var instruction in function.BasicBlocks.SelectMany(static block => block.Instructions))
        {
            switch (instruction)
            {
                case MirLoad
                {
                    Target: { Kind: PlaceKind.Local, Local: var target },
                    Source: MirPlace { Kind: PlaceKind.Local, Local: var source }
                } when definitions.GetValueOrDefault(target) == 1:
                    roots[target] = ResolveRoot(source, roots);
                    break;
            }
        }

        return roots;
    }

    private static bool OwnerIsDroppedOnEveryPath(
        MirBasicBlock block,
        int startIndex,
        ProjectionKey projection,
        IReadOnlyDictionary<LocalId, LocalId> aliases,
        ControlFlowGraph cfg,
        IReadOnlyDictionary<BlockId, MirBasicBlock> blocksById,
        HashSet<BlockId> visiting)
    {
        for (var index = startIndex; index < block.Instructions.Count; index++)
        {
            var instruction = block.Instructions[index];
            if (DefinesLocal(instruction, projection.Root) ||
                DefinesAliasOfRoot(instruction, projection.Root, aliases))
            {
                return false;
            }

            if (instruction is MirDrop
                {
                    Value: MirPlace { Kind: PlaceKind.Local, Local: var droppedLocal }
                } &&
                ResolveRoot(droppedLocal, aliases) == projection.Root)
            {
                return true;
            }

            foreach (var operand in EnumerateUsedOperands(instruction))
            {
                if (operand is MirPlace place &&
                    UsesOverlappingStorage(place, projection, aliases))
                {
                    return false;
                }
            }
        }

        if (!visiting.Add(block.Id))
        {
            return false;
        }

        try
        {
            var successors = cfg.GetSuccessors(block.Id);
            return successors.Count > 0 && successors.All(successor =>
                blocksById.TryGetValue(successor, out var successorBlock) &&
                OwnerIsDroppedOnEveryPath(
                    successorBlock,
                    0,
                    projection,
                    aliases,
                    cfg,
                    blocksById,
                    visiting));
        }
        finally
        {
            visiting.Remove(block.Id);
        }
    }

    private static bool DefinesAliasOfRoot(
        MirInstruction instruction,
        LocalId root,
        IReadOnlyDictionary<LocalId, LocalId> aliases)
    {
        foreach (var alias in aliases)
        {
            if (alias.Key != root &&
                ResolveRoot(alias.Key, aliases) == root &&
                DefinesLocal(instruction, alias.Key))
            {
                return true;
            }
        }

        return false;
    }

    private static bool UsesOverlappingStorage(
        MirPlace place,
        ProjectionKey projection,
        IReadOnlyDictionary<LocalId, LocalId> aliases)
    {
        if (place.Kind == PlaceKind.Local)
        {
            return ResolveRoot(place.Local, aliases) == projection.Root;
        }

        if (!TryNormalizeProjection(place, aliases, out var candidate) ||
            candidate.Root != projection.Root)
        {
            return false;
        }

        return ProjectionPathsOverlap(projection.Path, candidate.Path);
    }

    private static bool ProjectionPathsOverlap(string left, string right) =>
        left == right ||
        left.StartsWith(right + '/', StringComparison.Ordinal) ||
        right.StartsWith(left + '/', StringComparison.Ordinal);

    private static bool DefinesLocal(MirInstruction instruction, LocalId local) => instruction switch
    {
        MirAssign { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirCaseInject { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirCall { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirBinOp { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirUnaryOp { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirLoad { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirStore { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirCopy { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirMove { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        MirAlloc { Target: MirPlace { Kind: PlaceKind.Local, Local: var target } } => target == local,
        _ => false
    };

    private static IEnumerable<MirOperand> EnumerateUsedOperands(MirInstruction instruction)
    {
        switch (instruction)
        {
            case MirAssign assign:
                yield return assign.Source;
                break;
            case MirCaseInject injection:
                yield return injection.Operand;
                break;
            case MirCall call:
                yield return call.Function;
                foreach (var argument in call.Arguments)
                {
                    yield return argument;
                }
                break;
            case MirBinOp binary:
                yield return binary.Left;
                yield return binary.Right;
                break;
            case MirUnaryOp unary:
                yield return unary.Operand;
                break;
            case MirLoad load:
                yield return load.Source;
                break;
            case MirStore store:
                yield return store.Target;
                yield return store.Value;
                break;
            case MirCopy copy:
                yield return copy.Source;
                break;
            case MirMove move:
                yield return move.Source;
                break;
        }
    }

    private static bool TryNormalizeProjection(
        MirPlace place,
        IReadOnlyDictionary<LocalId, LocalId> aliases,
        out ProjectionKey projection)
    {
        var path = new List<string>();
        var current = place;
        while (current.Kind != PlaceKind.Local)
        {
            switch (current.Kind)
            {
                case PlaceKind.Field when current.Base is MirPlace fieldBase:
                    path.Add($"f:{current.FieldName}");
                    current = fieldBase;
                    break;
                case PlaceKind.Index when current.IndexAccessKind == MirIndexAccessKind.Aggregate &&
                                          current.Base is MirPlace indexBase &&
                                          current.Index is MirConstant index:
                    path.Add($"i:{index.Value}");
                    current = indexBase;
                    break;
                default:
                    projection = default;
                    return false;
            }
        }

        if (!current.Local.IsValid || path.Count == 0)
        {
            projection = default;
            return false;
        }

        path.Reverse();
        projection = new ProjectionKey(ResolveRoot(current.Local, aliases), string.Join('/', path));
        return true;
    }

    private static bool IsManagedOwnedType(MirModule module, TypeId typeId)
    {
        if (!TypeSemantics.IsManagedType(typeId) ||
            typeId.Value is BaseTypes.RawPtrId or BaseTypes.CfnId)
        {
            return false;
        }

        return !module.TypeDescriptors.TryGetValue(typeId.Value, out var descriptor) ||
               descriptor is not TypeDescriptor.Ref and not TypeDescriptor.MutRef and not TypeDescriptor.TypeVar;
    }

    private static LocalId ResolveRoot(
        LocalId local,
        IReadOnlyDictionary<LocalId, LocalId> aliases)
    {
        var current = local;
        var visited = new HashSet<LocalId>();
        while (visited.Add(current) && aliases.TryGetValue(current, out var next) && next != current)
        {
            current = next;
        }

        return current;
    }

    private readonly record struct ProjectionKey(LocalId Root, string Path);
}
