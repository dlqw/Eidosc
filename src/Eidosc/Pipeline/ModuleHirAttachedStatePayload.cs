using Eidosc.Mir;
using Eidosc.Types;

namespace Eidosc.Pipeline;

public sealed record ModuleHirAttachedStatePayload(
    string SchemaVersion,
    ParameterEffectMapPayload ParameterEffects,
    IReadOnlyList<int> CopyLikeTypeIds,
    IReadOnlyList<DynamicTypeKeyPayload> DynamicTypeKeys,
    IReadOnlyList<TypeDescriptorEntryPayload> TypeDescriptors,
    IReadOnlyList<ConstructorLayoutGroupPayload> ConstructorLayouts,
    string Hash)
{
    public const string CurrentSchemaVersion = "module-hir-attached-state-payload-v1";

    public static ModuleHirAttachedStatePayload Create(
        ParameterEffectMap? parameterEffects,
        IReadOnlySet<TypeId>? copyLikeTypeIds,
        IReadOnlyDictionary<TypeId, string>? dynamicTypeKeys,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors,
        IReadOnlyDictionary<int, List<ConstructorTypeLayout>>? constructorLayouts)
    {
        var payload = new ModuleHirAttachedStatePayload(
            CurrentSchemaVersion,
            ParameterEffectMapPayload.Create(parameterEffects),
            (copyLikeTypeIds ?? new HashSet<TypeId>())
                .Select(static id => id.Value)
                .Order()
                .ToArray(),
            (dynamicTypeKeys ?? new Dictionary<TypeId, string>())
                .OrderBy(static entry => entry.Key.Value)
                .Select(static entry => new DynamicTypeKeyPayload(entry.Key.Value, entry.Value))
                .ToArray(),
            (typeDescriptors ?? new Dictionary<int, TypeDescriptor>())
                .OrderBy(static entry => entry.Key)
                .Select(static entry => new TypeDescriptorEntryPayload(entry.Key, TypeDescriptorPayload.Create(entry.Value)))
                .ToArray(),
            (constructorLayouts ?? new Dictionary<int, List<ConstructorTypeLayout>>())
                .OrderBy(static entry => entry.Key)
                .Select(static entry => new ConstructorLayoutGroupPayload(
                    entry.Key,
                    entry.Value
                        .OrderBy(static layout => layout.ConstructorName, StringComparer.Ordinal)
                        .ThenBy(static layout => layout.TagValue)
                        .Select(ConstructorTypeLayoutPayload.Create)
                        .ToArray()))
                .ToArray(),
            "");

        return payload with { Hash = ComputeHash(payload) };
    }

    public bool HasValidHash() =>
        !string.IsNullOrWhiteSpace(Hash) &&
        string.Equals(Hash, ComputeHash(this), StringComparison.Ordinal);

    public bool TryRestore(out ModuleHirAttachedState restored)
    {
        restored = ModuleHirAttachedState.Empty;
        if (SchemaVersion != CurrentSchemaVersion ||
            !HasValidHash() ||
            !ParameterEffects.TryRestore(out var parameterEffects))
        {
            return false;
        }

        var typeDescriptors = new Dictionary<int, TypeDescriptor>();
        foreach (var entry in TypeDescriptors)
        {
            if (!entry.Descriptor.TryRestore(out var descriptor))
            {
                return false;
            }

            typeDescriptors[entry.TypeId] = descriptor;
        }

        var constructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>();
        foreach (var group in ConstructorLayouts)
        {
            constructorLayouts[group.TypeId] = group.Layouts
                .Select(static layout => layout.Restore())
                .ToList();
        }

        restored = new ModuleHirAttachedState(
            parameterEffects,
            CopyLikeTypeIds.Select(static id => new TypeId(id)).ToHashSet(),
            DynamicTypeKeys.ToDictionary(static entry => new TypeId(entry.TypeId), static entry => entry.TypeKey),
            typeDescriptors,
            constructorLayouts);
        return true;
    }

    private static string ComputeHash(ModuleHirAttachedStatePayload payload) =>
        ModuleArtifactHash.ComputeJsonHash(payload with { Hash = "" });
}

public sealed record ModuleHirAttachedState(
    ParameterEffectMap ParameterEffects,
    IReadOnlySet<TypeId> CopyLikeTypeIds,
    IReadOnlyDictionary<TypeId, string> DynamicTypeKeys,
    IReadOnlyDictionary<int, TypeDescriptor> TypeDescriptors,
    IReadOnlyDictionary<int, List<ConstructorTypeLayout>> ConstructorLayouts)
{
    public static ModuleHirAttachedState Empty { get; } =
        new(
            new ParameterEffectMap(),
            new HashSet<TypeId>(),
            new Dictionary<TypeId, string>(),
            new Dictionary<int, TypeDescriptor>(),
            new Dictionary<int, List<ConstructorTypeLayout>>());
}

public sealed record ParameterEffectMapPayload(
    IReadOnlyList<ParameterEffectNameEntryPayload> ByName,
    IReadOnlyList<ParameterEffectSymbolEntryPayload> BySymbolId)
{
    public static ParameterEffectMapPayload Create(ParameterEffectMap? map) =>
        new(
            (map?.EffectsByName ?? new Dictionary<string, IReadOnlyList<ParameterEffect>>())
                .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                .Select(static entry => new ParameterEffectNameEntryPayload(
                    entry.Key,
                    entry.Value.Select(static effect => effect.ToString()).ToArray()))
                .ToArray(),
            (map?.EffectsBySymbolId ?? new Dictionary<int, IReadOnlyList<ParameterEffect>>())
                .OrderBy(static entry => entry.Key)
                .Select(static entry => new ParameterEffectSymbolEntryPayload(
                    entry.Key,
                    entry.Value.Select(static effect => effect.ToString()).ToArray()))
                .ToArray());

    public bool TryRestore(out ParameterEffectMap map)
    {
        map = new ParameterEffectMap();

        foreach (var entry in ByName)
        {
            if (!TryRestoreEffects(entry.Effects, out var effects))
            {
                return false;
            }

            map.Add(entry.Name, 0, effects);
        }

        foreach (var entry in BySymbolId)
        {
            if (!TryRestoreEffects(entry.Effects, out var effects))
            {
                return false;
            }

            map.Add("", entry.SymbolId, effects);
        }

        return true;
    }

    private static bool TryRestoreEffects(
        IReadOnlyList<string> payloads,
        out List<ParameterEffect> effects)
    {
        effects = new List<ParameterEffect>(payloads.Count);
        foreach (var payload in payloads)
        {
            if (!Enum.TryParse<ParameterEffect>(payload, out var effect))
            {
                return false;
            }

            effects.Add(effect);
        }

        return true;
    }
}

public sealed record ParameterEffectNameEntryPayload(string Name, IReadOnlyList<string> Effects);

public sealed record ParameterEffectSymbolEntryPayload(int SymbolId, IReadOnlyList<string> Effects);

public sealed record DynamicTypeKeyPayload(int TypeId, string TypeKey);

public sealed record TypeDescriptorEntryPayload(int TypeId, TypeDescriptorPayload Descriptor);

public sealed record TypeDescriptorPayload(
    string Kind,
    int TypeIdValue = -1,
    IReadOnlyList<int>? ParamTypes = null,
    int ReturnType = -1,
    string? Effects = null,
    IReadOnlyList<int>? FieldTypes = null,
    string? ConstructorKind = null,
    int ConstructorId = -1,
    IReadOnlyList<int>? TypeArgs = null,
    int Inner = -1,
    string? AbilitiesKey = null,
    int ResultType = -1,
    int Index = -1)
{
    public static TypeDescriptorPayload Create(TypeDescriptor descriptor) =>
        descriptor switch
        {
            TypeDescriptor.Builtin builtin => new TypeDescriptorPayload(
                nameof(TypeDescriptor.Builtin),
                TypeIdValue: builtin.TypeIdValue),
            TypeDescriptor.Function function => new TypeDescriptorPayload(
                nameof(TypeDescriptor.Function),
                ParamTypes: function.ParamTypes.Select(static id => id.Value).ToArray(),
                ReturnType: function.ReturnType.Value,
                Effects: function.Effects),
            TypeDescriptor.Tuple tuple => new TypeDescriptorPayload(
                nameof(TypeDescriptor.Tuple),
                FieldTypes: tuple.FieldTypes.Select(static id => id.Value).ToArray()),
            TypeDescriptor.TyCon tyCon => new TypeDescriptorPayload(
                nameof(TypeDescriptor.TyCon),
                ConstructorKind: tyCon.Constructor.Kind.ToString(),
                ConstructorId: tyCon.Constructor.Id,
                TypeArgs: tyCon.TypeArgs.Select(static id => id.Value).ToArray()),
            TypeDescriptor.Ref reference => new TypeDescriptorPayload(
                nameof(TypeDescriptor.Ref),
                Inner: reference.Inner.Value),
            TypeDescriptor.MutRef reference => new TypeDescriptorPayload(
                nameof(TypeDescriptor.MutRef),
                Inner: reference.Inner.Value),
            TypeDescriptor.Shared shared => new TypeDescriptorPayload(
                nameof(TypeDescriptor.Shared),
                Inner: shared.Inner.Value),
            TypeDescriptor.TypeVar typeVar => new TypeDescriptorPayload(
                nameof(TypeDescriptor.TypeVar),
                Index: typeVar.Index),
            _ => throw new InvalidOperationException($"Unsupported type descriptor '{descriptor.GetType().Name}'.")
        };

    public bool TryRestore(out TypeDescriptor descriptor)
    {
        descriptor = new TypeDescriptor.Builtin(TypeId.None.Value);
        switch (Kind)
        {
            case nameof(TypeDescriptor.Builtin):
                descriptor = new TypeDescriptor.Builtin(TypeIdValue);
                return true;
            case nameof(TypeDescriptor.Function):
                descriptor = new TypeDescriptor.Function(
                    (ParamTypes ?? []).Select(static id => new TypeId(id)).ToArray(),
                    new TypeId(ReturnType),
                    Effects);
                return true;
            case nameof(TypeDescriptor.Tuple):
                descriptor = new TypeDescriptor.Tuple(
                    (FieldTypes ?? []).Select(static id => new TypeId(id)).ToArray());
                return true;
            case nameof(TypeDescriptor.TyCon):
                if (!Enum.TryParse<TypeConstructorKeyKind>(ConstructorKind, out var constructorKind))
                {
                    return false;
                }

                descriptor = new TypeDescriptor.TyCon(
                    new TypeConstructorKey(constructorKind, ConstructorId),
                    (TypeArgs ?? []).Select(static id => new TypeId(id)).ToArray());
                return true;
            case nameof(TypeDescriptor.Ref):
                descriptor = new TypeDescriptor.Ref(new TypeId(Inner));
                return true;
            case nameof(TypeDescriptor.MutRef):
                descriptor = new TypeDescriptor.MutRef(new TypeId(Inner));
                return true;
            case nameof(TypeDescriptor.Shared):
                descriptor = new TypeDescriptor.Shared(new TypeId(Inner));
                return true;
            case nameof(TypeDescriptor.TypeVar):
                descriptor = new TypeDescriptor.TypeVar(Index);
                return true;
            default:
                return false;
        }
    }
}

public sealed record ConstructorLayoutGroupPayload(
    int TypeId,
    IReadOnlyList<ConstructorTypeLayoutPayload> Layouts);

public sealed record ConstructorTypeLayoutPayload(
    string TypeName,
    string ConstructorName,
    uint TagValue,
    int RuntimeTypeId,
    IReadOnlyList<int> FieldTypeIds)
{
    public static ConstructorTypeLayoutPayload Create(ConstructorTypeLayout layout) =>
        new(
            layout.TypeName,
            layout.ConstructorName,
            layout.TagValue,
            layout.RuntimeTypeId,
            layout.FieldTypeIds.Select(static id => id.Value).ToArray());

    public ConstructorTypeLayout Restore() =>
        new()
        {
            TypeName = TypeName,
            ConstructorName = ConstructorName,
            TagValue = TagValue,
            RuntimeTypeId = RuntimeTypeId,
            FieldTypeIds = FieldTypeIds.Select(static id => new TypeId(id)).ToList()
        };
}
