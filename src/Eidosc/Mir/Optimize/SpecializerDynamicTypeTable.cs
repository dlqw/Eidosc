using Eidosc.Types;
namespace Eidosc.Mir.Optimize;

/// <summary>
/// Dynamic type table extracted from MirGenericSpecializer (D2/D3).
/// Owns 5 fields for runtime type key/descriptor/id bidirectional mapping.
/// Provides get-or-create semantics for specialization type substitution.
/// </summary>
internal sealed class SpecializerDynamicTypeTable
{
    private readonly Dictionary<int, string> _keyById = [];
    private readonly Dictionary<string, TypeId> _idByKey = new(StringComparer.Ordinal);
    private readonly Dictionary<TypeDescriptor, TypeId> _idByDescriptor = new(TypeDescriptorStructuralComparer.Instance);
    private readonly Dictionary<int, TypeDescriptor> _descriptorById = [];
    private int _nextId = 1000;

    // ── Properties (exposed for backward compatibility with partials) ──

    internal Dictionary<int, string> KeyByIdDict => _keyById;
    internal Dictionary<string, TypeId> IdByKeyDict => _idByKey;
    internal Dictionary<TypeDescriptor, TypeId> IdByDescriptorDict => _idByDescriptor;
    internal Dictionary<int, TypeDescriptor> DescriptorByIdDict => _descriptorById;
    internal int NextId
    {
        get => _nextId;
        set => _nextId = value;
    }
    public int Count => _keyById.Count;

    // ── Get-or-create by descriptor ──

    public TypeId GetOrCreateByDescriptor(TypeDescriptor descriptor, string typeKey)
    {
        if (_idByDescriptor.TryGetValue(descriptor, out var existingTypeId))
        {
            // Ensure key mapping is consistent
            _idByKey.TryAdd(typeKey, existingTypeId);
            return existingTypeId;
        }

        var newTypeId = new TypeId(_nextId++);
        _idByDescriptor[descriptor] = newTypeId;
        _idByKey[typeKey] = newTypeId;
        _keyById[newTypeId.Value] = typeKey;
        _descriptorById[newTypeId.Value] = descriptor;
        return newTypeId;
    }

    // ── Get-or-create by key ──

    public TypeId GetOrCreateByKey(string typeKey, TypeDescriptor? descriptor = null)
    {
        if (_idByKey.TryGetValue(typeKey, out var typeId))
            return typeId;

        var newTypeId = new TypeId(_nextId++);
        _idByKey[typeKey] = newTypeId;
        _keyById[newTypeId.Value] = typeKey;
        if (descriptor != null)
        {
            _descriptorById[newTypeId.Value] = descriptor;
            _idByDescriptor.TryAdd(descriptor, newTypeId);
        }
        return newTypeId;
    }

    /// <summary>
    /// Try-add a pre-allocated type id with key + descriptor.
    /// Used for type parameter registration.
    /// </summary>
    public void TryRegisterTypeParameter(int typeIdValue, string key, TypeDescriptor descriptor)
    {
        if (_descriptorById.TryAdd(typeIdValue, descriptor))
        {
            _idByDescriptor.TryAdd(descriptor, new TypeId(typeIdValue));
        }
        _keyById.TryAdd(typeIdValue, key);
        _idByKey.TryAdd(key, new TypeId(typeIdValue));
    }

    // ── Lookup ──

    public bool TryGetKey(int typeIdValue, out string key)
        => _keyById.TryGetValue(typeIdValue, out key!);

    public bool TryGetIdByKey(string key, out TypeId typeId)
        => _idByKey.TryGetValue(key, out typeId);

    public bool TryGetDescriptor(int typeIdValue, out TypeDescriptor descriptor)
        => _descriptorById.TryGetValue(typeIdValue, out descriptor!);

    // ── Lifecycle ──

    public void ResetNextId()
    {
        _nextId = _keyById.Count == 0
            ? 1000
            : _keyById.Keys.Max() + 1;
    }

    public void Clear()
    {
        _keyById.Clear();
        _idByKey.Clear();
        _idByDescriptor.Clear();
        _descriptorById.Clear();
        _nextId = 1000;
    }
}
