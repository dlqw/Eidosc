using System.Text;
using Eidosc.Symbols;

namespace Eidosc.Types;

internal enum OwnershipPassingKind
{
    ByValue,
    SharedBorrow,
    MutableBorrow
}

internal readonly record struct OwnershipProjection(
    OwnershipPassingKind Kind,
    bool IsDeferred = false)
{
    public static OwnershipProjection FromType(
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors)
    {
        if (typeId.IsValid &&
            typeDescriptors != null &&
            typeDescriptors.TryGetValue(typeId.Value, out var descriptor))
        {
            return descriptor switch
            {
                TypeDescriptor.Ref => new OwnershipProjection(OwnershipPassingKind.SharedBorrow),
                TypeDescriptor.MutRef => new OwnershipProjection(OwnershipPassingKind.MutableBorrow),
                TypeDescriptor.TypeVar => new OwnershipProjection(OwnershipPassingKind.ByValue, IsDeferred: true),
                _ => new OwnershipProjection(OwnershipPassingKind.ByValue)
            };
        }

        return new OwnershipProjection(OwnershipPassingKind.ByValue);
    }
}

internal sealed record OwnershipSlot(
    int Ordinal,
    string Name,
    TypeId TypeId,
    OwnershipProjection Projection,
    string TypeIdentity);

internal sealed record OwnershipContract
{
    public const string CurrentSchemaVersion = "ownership-contract-v1";

    public static OwnershipContract Empty { get; } = new();

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;

    public SymbolId CallableSymbol { get; init; } = SymbolId.None;

    public string CallableName { get; init; } = string.Empty;

    public IReadOnlyList<OwnershipSlot> Parameters { get; init; } = [];

    public OwnershipSlot Result { get; init; } = new(
        -1,
        "result",
        TypeId.None,
        new OwnershipProjection(OwnershipPassingKind.ByValue),
        "invalid");

    public string CanonicalIdentity { get; init; } = string.Empty;

    public OwnershipSlot GetParameter(int ordinal)
    {
        if ((uint)ordinal >= (uint)Parameters.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(ordinal));
        }

        return Parameters[ordinal];
    }

    public static OwnershipContract Create(
        SymbolId callableSymbol,
        string callableName,
        IReadOnlyList<(string Name, TypeId TypeId)> parameters,
        TypeId resultType,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors,
        SymbolTable? symbolTable = null)
    {
        var slots = parameters
            .Select((parameter, ordinal) => CreateSlot(
                ordinal,
                parameter.Name,
                parameter.TypeId,
                typeDescriptors,
                symbolTable))
            .ToArray();
        var result = CreateSlot(-1, "result", resultType, typeDescriptors, symbolTable);

        return new OwnershipContract
        {
            CallableSymbol = callableSymbol,
            CallableName = callableName,
            Parameters = slots,
            Result = result,
            CanonicalIdentity = BuildCanonicalIdentity(
                FormatSymbolIdentity(callableSymbol, callableName, symbolTable),
                slots,
                result)
        };
    }

    private static OwnershipSlot CreateSlot(
        int ordinal,
        string name,
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors,
        SymbolTable? symbolTable)
    {
        var projection = OwnershipProjection.FromType(typeId, typeDescriptors);
        var typeIdentity = BuildTypeIdentity(typeId, typeDescriptors, symbolTable, []);
        return new OwnershipSlot(ordinal, name, typeId, projection, typeIdentity);
    }

    private static string BuildCanonicalIdentity(
        string callableIdentity,
        IReadOnlyList<OwnershipSlot> parameters,
        OwnershipSlot result)
    {
        var builder = new StringBuilder(CurrentSchemaVersion);
        builder.Append("|callable:");
        builder.Append(callableIdentity);
        foreach (var parameter in parameters)
        {
            AppendSlot(builder, "parameter", parameter);
        }

        AppendSlot(builder, "result", result);
        return builder.ToString();
    }

    private static void AppendSlot(StringBuilder builder, string category, OwnershipSlot slot)
    {
        builder.Append('|');
        builder.Append(category);
        builder.Append(':');
        builder.Append(slot.Ordinal);
        builder.Append(':');
        builder.Append(slot.TypeIdentity);
        builder.Append(':');
        builder.Append(slot.Projection.Kind);
        if (slot.Projection.IsDeferred)
        {
            builder.Append(":deferred");
        }
    }

    private static string BuildTypeIdentity(
        TypeId typeId,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors,
        SymbolTable? symbolTable,
        HashSet<int> active)
    {
        if (!typeId.IsValid)
        {
            return "invalid";
        }

        if (!active.Add(typeId.Value))
        {
            return $"recursive:{ResolveTypeIdIdentity(typeId, symbolTable)}";
        }

        try
        {
            if (typeDescriptors == null || !typeDescriptors.TryGetValue(typeId.Value, out var descriptor))
            {
                return ResolveTypeIdIdentity(typeId, symbolTable);
            }

            return descriptor switch
            {
                TypeDescriptor.Builtin builtin => ResolveBuiltinIdentity(new TypeId(builtin.TypeIdValue)),
                TypeDescriptor.Ref reference =>
                    $"ref<{BuildTypeIdentity(reference.Inner, typeDescriptors, symbolTable, active)}>",
                TypeDescriptor.MutRef reference =>
                    $"mref<{BuildTypeIdentity(reference.Inner, typeDescriptors, symbolTable, active)}>",
                TypeDescriptor.Shared shared =>
                    $"shared<{BuildTypeIdentity(shared.Inner, typeDescriptors, symbolTable, active)}>",
                TypeDescriptor.TypeVar variable => $"type-parameter:{variable.Index}",
                TypeDescriptor.Tuple tuple =>
                    $"tuple<{string.Join(",", tuple.FieldTypes.Select(field => BuildTypeIdentity(field, typeDescriptors, symbolTable, active)))}>",
                TypeDescriptor.Function function =>
                    $"function<({string.Join(",", function.ParamTypes.Select(parameter => BuildTypeIdentity(parameter, typeDescriptors, symbolTable, active)))})" +
                    $"->{BuildTypeIdentity(function.ReturnType, typeDescriptors, symbolTable, active)};effects={function.Effects ?? string.Empty}>",
                TypeDescriptor.TyCon constructor => BuildConstructorIdentity(
                    constructor,
                    typeDescriptors,
                    symbolTable,
                    active),
                _ => ResolveTypeIdIdentity(typeId, symbolTable)
            };
        }
        finally
        {
            active.Remove(typeId.Value);
        }
    }

    private static string BuildConstructorIdentity(
        TypeDescriptor.TyCon constructor,
        IReadOnlyDictionary<int, TypeDescriptor>? typeDescriptors,
        SymbolTable? symbolTable,
        HashSet<int> active)
    {
        var head = constructor.Constructor.Kind switch
        {
            TypeConstructorKeyKind.Symbol => FormatSymbolIdentity(
                constructor.Constructor.SymbolId,
                constructor.Constructor.ToDescriptorString(),
                symbolTable),
            TypeConstructorKeyKind.Builtin => ResolveBuiltinIdentity(constructor.Constructor.TypeId),
            TypeConstructorKeyKind.TypeId => ResolveTypeIdIdentity(constructor.Constructor.TypeId, symbolTable),
            TypeConstructorKeyKind.Variable => $"constructor-parameter:{constructor.Constructor.Id}",
            _ => constructor.Constructor.ToDescriptorString()
        };
        var typeArguments = string.Join(",", constructor.TypeArgs.Select(argument =>
            BuildTypeIdentity(argument, typeDescriptors, symbolTable, active)));
        var valueArguments = string.Join(",", constructor.ValueArgs.Select(static argument => argument.CanonicalHash));
        var effectArguments = string.Join(",", constructor.EffectArgs.Select(static argument => argument.CanonicalText));
        return $"nominal:{head}<{typeArguments};values={valueArguments};effects={effectArguments}>";
    }

    private static string ResolveTypeIdIdentity(TypeId typeId, SymbolTable? symbolTable)
    {
        var builtin = ResolveBuiltinIdentity(typeId);
        if (!builtin.StartsWith("builtin-id:", StringComparison.Ordinal))
        {
            return builtin;
        }

        if (symbolTable != null)
        {
            var matches = symbolTable.Symbols.Values
                .Where(symbol => symbol.TypeId == typeId)
                .Take(2)
                .ToArray();
            if (matches.Length == 1)
            {
                return FormatSymbolIdentity(matches[0].Id, matches[0].Name, symbolTable);
            }
        }

        return builtin;
    }

    private static string ResolveBuiltinIdentity(TypeId typeId)
    {
        var name = ImplLookupCanonicalizer.ResolveBuiltinCanonicalTypeName(typeId);
        return string.IsNullOrWhiteSpace(name) ? $"builtin-id:{typeId.Value}" : $"builtin:{name}";
    }

    private static string FormatSymbolIdentity(
        SymbolId symbolId,
        string fallbackName,
        SymbolTable? symbolTable)
    {
        if (symbolTable?.GetSymbol(symbolId) is not { } symbol)
        {
            return symbolId.IsValid ? $"symbol:{symbolId.Value}" : $"name:{fallbackName}";
        }

        if (symbolTable.Modules.TryGetOwningModule(symbolId, out var module))
        {
            return $"{ModuleRegistry.FormatModuleFullName(module)}.{symbol.Name}";
        }

        return $"{symbol.Kind}:{symbol.Name}";
    }
}
