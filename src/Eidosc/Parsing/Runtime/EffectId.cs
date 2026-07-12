namespace Eidosc;

/// <summary>
/// 效应唯一标识符
/// </summary>
public readonly record struct EffectId(int Value)
{
    /// <summary>
    /// 无效/空效应 ID
    /// </summary>
    public static readonly EffectId None = new(-1);

    /// <summary>
    /// 纯净效应（无副作用）
    /// </summary>
    public static readonly EffectId Pure = new(0);

    /// <summary>
    /// 检查是否是有效的效应 ID
    /// </summary>
    public bool IsValid => Value >= 0;

    /// <summary>
    /// 隐式转换为 int
    /// </summary>
    public static implicit operator int(EffectId id) => id.Value;

    public override string ToString() => IsValid ? $"E{Value}" : "E<none>";
}
