using Eidosc.Ast;
using MemoryPack;

namespace Eidosc;

[Flags]
public enum NonTerminalFlag
{
    None = 0,

    /// <summary>
    /// 减枝，如果没有子节点，则消除容器
    /// </summary>
    Pruning = 1 << 0,

    /// <summary>
    /// 降维，如果有且只有一个子节点，则消除容器
    /// </summary>
    Squeezing = 1 << 1,

    /// <summary>
    /// 解包，无论有没有子节点，都消除容器
    /// </summary>
    Unpacking = 1 << 2,
}

[MemoryPackable]
[method: MemoryPackConstructor]
public partial class NonTerminal(int id, string debugName, NonTerminalFlag flag) : GrammarSymbol(id, debugName)
{
    public readonly NonTerminalFlag Flag = flag;
    [MemoryPackIgnore] public Func<EidosAstNode>? AstNodeCreator { get; set; }

    public bool HasFlag(NonTerminalFlag flag) => Flag.HasFlag(flag);
}