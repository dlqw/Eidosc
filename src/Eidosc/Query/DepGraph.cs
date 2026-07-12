namespace Eidosc.Query;

public enum DepKind
{
    ParseModule,
    ResolveNames,
    InferTypes,
    InferAbilities,
    BuildHir,
    BuildMir,
    CheckBorrow,
    Optimize,
    CheckSend,
    CodeGen,
}

public readonly record struct DepNodeIndex(int Value)
{
    public static readonly DepNodeIndex Invalid = new(-1);
    public bool IsValid => Value >= 0;
}

public readonly record struct Fingerprint(ulong Value)
{
    public static Fingerprint From<T>(T value) where T : notnull
    {
        return new Fingerprint((ulong)(uint)HashCode.Combine(value));
    }

    public static Fingerprint Combine(Fingerprint a, Fingerprint b)
    {
        return new Fingerprint((ulong)HashCode.Combine(a.Value, b.Value));
    }
}

public readonly record struct DepNode(DepKind Kind, Fingerprint Key)
{
    public static DepNode Create<T>(DepKind kind, T key) where T : notnull
        => new(kind, Fingerprint.From(key));
}
