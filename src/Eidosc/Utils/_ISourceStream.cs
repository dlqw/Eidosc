using System.Runtime.CompilerServices;

namespace Eidosc.Utils;

public interface ISourceStream
{
    /// <summary>
    /// 完整的源代码文本。
    /// </summary>
    string Text { get; }

    /// <summary>
    /// 获取从 PreviewPosition 开始直到文本末尾的切片。
    /// [性能关键] 用于 Grammar 中的 SIMD 批量扫描。
    /// </summary>
    ReadOnlySpan<char> RemainingSpan { get; }

    /// <summary>
    /// 当前已提交的物理位置 (Line/Column)。
    /// </summary>
    SourceLocation Location { get; }

    /// <summary>
    /// 当前已提交的绝对索引。
    /// 修改此属性会触发昂贵的行号重算。
    /// </summary>
    int Position { get; set; }

    /// <summary>
    /// 预览/向前看游标位置。
    /// 修改此属性开销极小。
    /// </summary>
    int PreviewPosition { get; set; }

    /// <summary>
    /// 获取 PreviewPosition 处的字符。
    /// </summary>
    char PreviewChar { get; }

    /// <summary>
    /// 获取 PreviewPosition + 1 处的字符。
    /// </summary>
    char NextPreviewChar { get; }

    /// <summary>
    /// 向前查看指定偏移量的字符。
    /// offset=0 等同于 PreviewChar。
    /// </summary>
    char PeekChar(int offset);

    /// <summary>
    /// 获取预览文本。
    /// </summary>
    string GetPreviewText();

    /// <summary>
    /// 检查当前位置是否匹配指定符号。
    /// </summary>
    bool MatchSymbol(string text);

    /// <summary>
    /// [关键] 重置流状态（用于回溯）。
    /// </summary>
    void Reset(SourceLocation location);

    /// <summary>
    /// 是否到达文件末尾 (基于 PreviewPosition)。
    /// </summary>
    bool Eof();

    // --- 默认实现方法 (Mixins) ---

    /// <summary>
    /// 将预览游标重置回当前提交位置。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void ResetPreviewPosition()
    {
        PreviewPosition = Location.Position;
    }

    /// <summary>
    /// 消耗并提交指定长度的字符。
    /// 这会移动 Position 并触发行号计算。
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    void Step(int step)
    {
        // 1. 先计算目标位置
        int target = Position + step;
        // 2. 更新 Position (这会触发 SetNewPosition 计算行号)
        Position = target;
        // 3. 确保 Preview 同步
        PreviewPosition = target;
    }
}