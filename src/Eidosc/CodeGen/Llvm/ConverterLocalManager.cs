using Eidosc.Symbols;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.CodeGen.Llvm;

/// <summary>
/// Local slot management extracted from MirToLlvmConverter (D2/D3).
/// Owns 6 fields tracking LLVM locals: identity map, type cache,
/// storage sizing, slot-backing decisions, and alloca slots.
/// </summary>
internal sealed class ConverterLocalManager
{
    private readonly Dictionary<LocalId, LlvmLocal> _localMap = new();
    private readonly Dictionary<LocalId, TypeId> _localTypeById = new();
    private readonly Dictionary<LocalId, int> _aggregateStorageWordCountByLocal = new();
    private readonly HashSet<LocalId> _slotBackedLocals = [];
    private readonly HashSet<LocalId> _runtimeWordLocals = [];
    private readonly Dictionary<LocalId, LlvmAlloca> _localSlots = new();

    // ── Direct dictionary access (backward compat) ──

    internal Dictionary<LocalId, LlvmLocal> LocalMap => _localMap;
    internal Dictionary<LocalId, TypeId> LocalTypeById => _localTypeById;
    internal Dictionary<LocalId, int> AggregateStorageWordCountByLocal => _aggregateStorageWordCountByLocal;
    internal HashSet<LocalId> SlotBackedLocals => _slotBackedLocals;
    internal HashSet<LocalId> RuntimeWordLocals => _runtimeWordLocals;
    internal Dictionary<LocalId, LlvmAlloca> LocalSlots => _localSlots;

    // ── Lifecycle ──

    public void Clear()
    {
        _localMap.Clear();
        _localTypeById.Clear();
        _aggregateStorageWordCountByLocal.Clear();
        _slotBackedLocals.Clear();
        _runtimeWordLocals.Clear();
        _localSlots.Clear();
    }
}
