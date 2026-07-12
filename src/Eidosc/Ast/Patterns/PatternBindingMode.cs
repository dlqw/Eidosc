namespace Eidosc;

/// <summary>
/// 模式绑定方式。
/// </summary>
public enum PatternBindingMode
{
    /// <summary>
    /// 按值绑定（默认）。
    /// </summary>
    ByValue,

    /// <summary>
    /// 共享借用绑定（ref）。
    /// </summary>
    SharedBorrow,

    /// <summary>
    /// 可变借用绑定（mref；兼容 mut / ref mut）。
    /// </summary>
    MutableBorrow
}

public static class PatternBindingModeExtensions
{
    public static string ToDisplayText(this PatternBindingMode mode)
    {
        return mode switch
        {
            PatternBindingMode.ByValue => "value",
            PatternBindingMode.SharedBorrow => "ref",
            PatternBindingMode.MutableBorrow => "mref",
            _ => mode.ToString()
        };
    }
}
