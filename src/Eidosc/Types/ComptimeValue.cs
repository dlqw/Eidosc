using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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

    protected static string EncodeText(string value) =>
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
