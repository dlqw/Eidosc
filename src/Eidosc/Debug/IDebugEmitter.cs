using Eidosc.Utils;

namespace Eidosc.Debug;

/// <summary>
/// 调试输出器接口
/// </summary>
public interface IDebugEmitter
{
    /// <summary>
    /// 当前调试级别
    /// </summary>
    DebugLevel Level { get; set; }

    /// <summary>
    /// 输出文本内容
    /// </summary>
    /// <param name="phase">阶段名称</param>
    /// <param name="fileName">文件名（不含扩展名）</param>
    /// <param name="content">内容</param>
    void Emit(string phase, string fileName, string content);

    /// <summary>
    /// 输出对象（序列化为 JSON）
    /// </summary>
    /// <param name="phase">阶段名称</param>
    /// <param name="fileName">文件名（不含扩展名）</param>
    /// <param name="obj">要序列化的对象</param>
    void EmitObject(string phase, string fileName, object obj);

    /// <summary>
    /// 输出带源码位置的信息
    /// </summary>
    /// <param name="phase">阶段名称</param>
    /// <param name="fileName">文件名</param>
    /// <param name="message">消息</param>
    /// <param name="span">源码位置</param>
    void EmitWithSpan(string phase, string fileName, string message, SourceSpan span);

    /// <summary>
    /// 开始阶段
    /// </summary>
    /// <param name="phase">阶段名称</param>
    void BeginPhase(string phase);

    /// <summary>
    /// 结束阶段
    /// </summary>
    /// <param name="phase">阶段名称</param>
    void EndPhase(string phase);

    /// <summary>
    /// 刷新所有输出
    /// </summary>
    void Flush();
}
