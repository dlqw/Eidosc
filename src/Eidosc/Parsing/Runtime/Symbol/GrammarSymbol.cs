using MemoryPack;

namespace Eidosc;

[MemoryPackable]
[MemoryPackUnion(0, typeof(Terminal))]
[MemoryPackUnion(1, typeof(NonTerminal))]
public abstract partial class GrammarSymbol(int id, string debugName)
{
    public readonly int Id = id;
    public readonly string DebugName = debugName;
    
    public override string ToString() => DebugName;
}