using MemoryPack;

namespace Eidosc;

[MemoryPackable]
public readonly partial struct ProductionId(int id) : IEquatable<ProductionId>
{
    private readonly int _id = id;

    public override string ToString() => _id.ToString();

    public bool Equals(ProductionId other)
    {
        return _id == other._id;
    }

    public override bool Equals(object? obj)
    {
        return obj is ProductionId other && Equals(other);
    }

    public override int GetHashCode()
    {
        return _id;
    }

    public static bool operator ==(ProductionId left, ProductionId right)
    {
        return left.Equals(right);
    }

    public static bool operator !=(ProductionId left, ProductionId right)
    {
        return !left.Equals(right);
    }

    public static implicit operator ProductionId(int id) => new(id);
    public static implicit operator int(ProductionId id) => id._id;
}

[MemoryPackable]
public partial class Production(int lvalue, int reduceCount, ProductionId id)
{
    public readonly ProductionId Id = id;
    public readonly int LValue = lvalue;
    public readonly int ReduceCount = reduceCount;
}