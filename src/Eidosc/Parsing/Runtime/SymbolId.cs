namespace Eidosc;

/// <summary>
/// 符号唯一标识符
/// </summary>
public readonly record struct SymbolId(int Value)
{
    /// <summary>
    /// 无效/空符号 ID
    /// </summary>
    public static readonly SymbolId None = new(-1);

    /// <summary>
    /// 检查是否是有效的符号 ID
    /// </summary>
    public bool IsValid => Value >= 0;

    /// <summary>
    /// 隐式转换为 int
    /// </summary>
    public static implicit operator int(SymbolId id) => id.Value;

    public override string ToString() => IsValid ? $"${Value}" : "$<none>";
}
