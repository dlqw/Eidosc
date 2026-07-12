namespace Eidosc;

/// <summary>
/// 类型唯一标识符
/// </summary>
public readonly record struct TypeId(int Value)
{
    /// <summary>
    /// 无效/空类型 ID
    /// </summary>
    public static readonly TypeId None = new(-1);

    /// <summary>
    /// 检查是否是有效的类型 ID
    /// </summary>
    public bool IsValid => Value >= 0;

    /// <summary>
    /// 隐式转换为 int
    /// </summary>
    public static implicit operator int(TypeId id) => id.Value;

    public override string ToString() => IsValid ? $"T{Value}" : "T<none>";
}
