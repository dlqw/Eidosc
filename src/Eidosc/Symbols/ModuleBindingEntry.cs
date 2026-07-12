namespace Eidosc.Symbols;

/// <summary>
/// 模块绑定表中的可绑定条目。
/// 既用于真实成员，也用于 re-export alias。
/// </summary>
public sealed record ModuleBindingEntry
{
    public string Name { get; init; } = "";

    public SymbolId SymbolId { get; init; }

    public ResolutionKind Kind { get; init; }
}
