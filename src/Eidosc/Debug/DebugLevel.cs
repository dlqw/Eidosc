namespace Eidosc.Debug;

/// <summary>
/// 调试输出级别
/// </summary>
public enum DebugLevel
{
    /// <summary>
    /// 最小级别 - 仅关键信息
    /// </summary>
    Minimal = 0,

    /// <summary>
    /// 正常级别 - 常规信息
    /// </summary>
    Normal = 1,

    /// <summary>
    /// 详细级别 - 详细信息
    /// </summary>
    Verbose = 2,

    /// <summary>
    /// 诊断级别 - 所有中间结果
    /// </summary>
    Diagnostic = 3
}
