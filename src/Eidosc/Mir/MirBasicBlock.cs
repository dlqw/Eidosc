using Eidosc.Utils;

namespace Eidosc.Mir;

/// <summary>
/// MIR 基本块 - 包含一系列指令和一个终止符
/// </summary>
public sealed class MirBasicBlock
{
    /// <summary>
    /// 基本块 ID
    /// </summary>
    public BlockId Id { get; init; }

    /// <summary>
    /// 指令列表
    /// </summary>
    public List<MirInstruction> Instructions { get; init; } = [];

    /// <summary>
    /// 终止符
    /// </summary>
    public MirTerminator? Terminator { get; set; }

    /// <summary>
    /// 源码位置
    /// </summary>
    public SourceSpan Span { get; init; }

    /// <summary>
    /// 是否是入口块
    /// </summary>
    public bool IsEntry { get; init; }

    public override string ToString()
    {
        var instrs = string.Join("\n  ", Instructions.Select(i => i.ToString()));
        var term = Terminator?.ToString() ?? "no terminator";
        return $"bb{Id.Value}:\n  {instrs}\n  {term}";
    }
}
