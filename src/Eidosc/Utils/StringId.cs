namespace Eidosc.Utils;

public readonly struct StringId(int id, ushort length, char firstChar) : IEquatable<StringId>
{
    public readonly int Id = id;
    public readonly ushort Length = length;
    public readonly char FirstChar = firstChar;

    public static readonly StringId Empty = new(0, 0, '\0');

    public bool IsEmpty => Id == 0;

    public override bool Equals(object? obj) => obj is StringId other && Equals(other);
    public bool Equals(StringId other) => Id == other.Id;
    public override int GetHashCode() => Id;
    public override string ToString() => $"#{Id}";

    public static bool operator ==(StringId left, StringId right) => left.Id == right.Id;
    public static bool operator !=(StringId left, StringId right) => left.Id != right.Id;

    public static implicit operator int(StringId @string) => @string.Id;
}