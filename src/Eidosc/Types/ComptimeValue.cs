using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Eidosc.Ast.Types;
using Eidosc.Utils;

namespace Eidosc.Types;

internal abstract record ComptimeValue
{
    public Type? StaticType { get; init; }

    protected abstract string UntypedCanonicalText { get; }

    public string CanonicalText => StaticType == null
        ? UntypedCanonicalText
        : $"typed:{EncodeText(StaticType.ToString())}:{UntypedCanonicalText}";

    public string CanonicalHash => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalText))).ToLowerInvariant();

    public virtual bool TryGetRuntimeLiteral(out object? value)
    {
        value = null;
        return false;
    }

    public bool StructuralEquals(ComptimeValue other) =>
        string.Equals(CanonicalText, other.CanonicalText, StringComparison.Ordinal);

    public static bool TryFromLiteral(object? value, out ComptimeValue comptimeValue)
    {
        switch (value)
        {
            case null:
                comptimeValue = ComptimeUnitValue.Instance;
                return true;
            case bool scalar:
                comptimeValue = new ComptimeBoolValue(scalar);
                return true;
            case byte scalar:
                comptimeValue = new ComptimeIntegerValue(scalar);
                return true;
            case short scalar:
                comptimeValue = new ComptimeIntegerValue(scalar);
                return true;
            case int scalar:
                comptimeValue = new ComptimeIntegerValue(scalar);
                return true;
            case long scalar:
                comptimeValue = new ComptimeIntegerValue(scalar);
                return true;
            case float scalar:
                comptimeValue = new ComptimeFloatValue(scalar);
                return true;
            case double scalar:
                comptimeValue = new ComptimeFloatValue(scalar);
                return true;
            case char scalar:
                comptimeValue = new ComptimeCharValue(scalar);
                return true;
            case string scalar when scalar == "()":
                comptimeValue = ComptimeUnitValue.Instance;
                return true;
            case string scalar:
                comptimeValue = new ComptimeStringValue(scalar);
                return true;
            default:
                comptimeValue = ComptimeUnitValue.Instance;
                return false;
        }
    }

    internal static string EncodeText(string value) =>
        Convert.ToHexString(Encoding.UTF8.GetBytes(value)).ToLowerInvariant();
}

internal sealed record ComptimeUnitValue : ComptimeValue
{
    public static ComptimeUnitValue Instance { get; } = new();

    private ComptimeUnitValue()
    {
    }

    protected override string UntypedCanonicalText => "unit";

    public override bool TryGetRuntimeLiteral(out object? value)
    {
        value = null;
        return true;
    }
}

internal sealed record ComptimeBoolValue(bool Value) : ComptimeValue
{
    protected override string UntypedCanonicalText => Value ? "bool:1" : "bool:0";

    public override bool TryGetRuntimeLiteral(out object? value)
    {
        value = Value;
        return true;
    }
}

internal sealed record ComptimeIntegerValue(long Value) : ComptimeValue
{
    protected override string UntypedCanonicalText => $"int:{Value.ToString(CultureInfo.InvariantCulture)}";

    public override bool TryGetRuntimeLiteral(out object? value)
    {
        value = Value;
        return true;
    }
}

internal sealed record ComptimeFloatValue(double Value) : ComptimeValue
{
    protected override string UntypedCanonicalText => $"float:{Value.ToString("R", CultureInfo.InvariantCulture)}";

    public override bool TryGetRuntimeLiteral(out object? value)
    {
        value = Value;
        return true;
    }
}

internal sealed record ComptimeCharValue(char Value) : ComptimeValue
{
    protected override string UntypedCanonicalText => $"char:{((int)Value).ToString(CultureInfo.InvariantCulture)}";

    public override bool TryGetRuntimeLiteral(out object? value)
    {
        value = Value;
        return true;
    }
}

internal sealed record ComptimeStringValue(string Value) : ComptimeValue
{
    protected override string UntypedCanonicalText => $"string:{EncodeText(Value)}";

    public override bool TryGetRuntimeLiteral(out object? value)
    {
        value = Value;
        return true;
    }
}

internal enum ComptimeSequenceKind
{
    Tuple,
    List
}

internal sealed record ComptimeSequenceValue(
    ComptimeSequenceKind Kind,
    IReadOnlyList<ComptimeValue> Elements) : ComptimeValue
{
    protected override string UntypedCanonicalText =>
        $"sequence:{Kind}:{Elements.Count}:[{string.Join(";", Elements.Select(static element => element.CanonicalText))}]";
}

internal sealed record ComptimeNamedValue(string Name, ComptimeValue Value)
{
    public string CanonicalText => $"{Convert.ToHexString(Encoding.UTF8.GetBytes(Name)).ToLowerInvariant()}={Value.CanonicalText}";
}

internal sealed record ComptimeAdtValue(
    SymbolId ConstructorId,
    string ConstructorName,
    IReadOnlyList<ComptimeValue> PositionalValues,
    IReadOnlyList<ComptimeNamedValue> NamedValues) : ComptimeValue
{
    protected override string UntypedCanonicalText =>
        $"adt:{EncodeText(ConstructorName)}:pos[{string.Join(";", PositionalValues.Select(static value => value.CanonicalText))}]" +
        $":named[{string.Join(";", NamedValues.Select(static value => value.CanonicalText))}]";

    public bool HasSameConstructor(SymbolId constructorId, string constructorName)
    {
        if (ConstructorId.IsValid && constructorId.IsValid)
        {
            return ConstructorId == constructorId;
        }

        return string.Equals(ConstructorName, constructorName, StringComparison.Ordinal);
    }
}

internal sealed record MetaTypeRef(
    string Kind,
    string Name,
    string StableIdentity,
    SymbolId SymbolId,
    TypeId TypeId,
    IReadOnlyList<MetaTypeRef> Arguments,
    TypeNode? InternalSyntax = null,
    IReadOnlyList<MetaGenericArgumentRef>? GenericArguments = null)
{
    public string CanonicalText =>
        $"{Kind}:{ComptimeValue.EncodeText(Name)}:{ComptimeValue.EncodeText(StableIdentity)}" +
        $"[{string.Join(";", Arguments.Select(static argument => argument.CanonicalText))}]" +
        $"<[{string.Join(";", (GenericArguments ?? []).Select(static argument => argument.CanonicalText))}]>";
}

internal sealed record MetaGenericArgumentRef(
    string Domain,
    string Display,
    string StableIdentity,
    SymbolId SymbolId,
    MetaTypeRef? Type)
{
    public string CanonicalText =>
        $"{Domain}:{ComptimeValue.EncodeText(Display)}:{ComptimeValue.EncodeText(StableIdentity)}:" +
        (Type == null ? "none" : Type.CanonicalText);
}

internal sealed record ComptimeTypeValue(MetaTypeRef TypeRef) : ComptimeValue
{
    protected override string UntypedCanonicalText => $"meta-type:{TypeRef.CanonicalText}";
}

internal sealed record ComptimeDeclValue(
    SymbolId SymbolId,
    string StableIdentity,
    string Name,
    string DeclarationKind,
    SourceSpan Span) : ComptimeValue
{
    protected override string UntypedCanonicalText =>
        $"meta-decl:{EncodeText(StableIdentity)}:{EncodeText(Name)}:{EncodeText(DeclarationKind)}";
}

internal sealed record ComptimeMetaObjectValue(
    string SchemaKind,
    IReadOnlyList<ComptimeNamedValue> Properties) : ComptimeValue
{
    protected override string UntypedCanonicalText =>
        SchemaKind.StartsWith("build.", StringComparison.Ordinal)
            ? $"build-object:{WellKnownStrings.Build.SchemaVersion}:{EncodeText(SchemaKind)}" +
              $"[{string.Join(";", Properties.Select(static property => property.CanonicalText))}]"
            : $"meta-object:{WellKnownStrings.Meta.SchemaVersion}:{EncodeText(SchemaKind)}" +
              $"[{string.Join(";", Properties.Select(static property => property.CanonicalText))}]";

    public bool TryGet(string name, out ComptimeValue value)
    {
        foreach (var property in Properties)
        {
            if (string.Equals(property.Name, name, StringComparison.Ordinal))
            {
                value = property.Value;
                return true;
            }
        }

        value = ComptimeUnitValue.Instance;
        return false;
    }
}
