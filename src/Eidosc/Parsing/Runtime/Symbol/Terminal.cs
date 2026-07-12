using MemoryPack;

namespace Eidosc;

[Flags]
public enum TerminalFlag
{
    None = 0,
    IsPunctuation = 1 << 0,
    IsKeyword = 1 << 1,
    IsReservedWord = 1 << 2,
}

[MemoryPackable]
[method: MemoryPackConstructor]
public partial class Terminal(int id, string debugName, TerminalFlag flags) : GrammarSymbol(id, debugName)
{
    public readonly TerminalFlag Flags = flags;

    public bool HasFlag(TerminalFlag flags) => Flags.HasFlag(flags);
    
    public override string ToString() => $"{DebugName}";
}