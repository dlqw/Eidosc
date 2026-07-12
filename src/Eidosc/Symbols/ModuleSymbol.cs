namespace Eidosc.Symbols;

/// <summary>
/// 模块符号
/// </summary>
public sealed record ModuleSymbol : Symbol
{
    public override SymbolKind Kind => SymbolKind.Module;

    /// <summary>
    /// 所属 package alias。为空表示当前 package。
    /// </summary>
    public string? PackageAlias { get; init; }

    /// <summary>
    /// Concrete package instance key. This separates package identity from the user-facing alias.
    /// </summary>
    public string? PackageInstanceKey { get; init; }

    public ModuleIdentity Identity => ModuleIdentity.Create(PackageAlias, PackageInstanceKey, Path);

    /// <summary>
    /// 模块路径（如 ["std", "collection"]）
    /// </summary>
    public List<string> Path { get; init; } = [];

    /// <summary>
    /// 模块内符号
    /// </summary>
    public List<SymbolId> Members { get; init; } = [];

    /// <summary>
    /// 模块导出作用域。
    /// 仅在显式 export 模式下生效，可包含 re-export alias。
    /// </summary>
    public List<ModuleBindingEntry> ExportedBindings { get; init; } = [];

    /// <summary>
    /// 当前模块是否启用显式 export 模式。
    /// 若为 false，则保持历史行为：所有 public 直接成员默认可见。
    /// </summary>
    public bool UsesExplicitExports { get; init; }

    /// <summary>
    /// 导入的模块
    /// </summary>
    public List<SymbolId> Imports { get; init; } = [];

    /// <summary>
    /// 父模块
    /// </summary>
    public SymbolId? ParentModule { get; init; }

    /// <summary>
    /// 比较模块路径是否相同
    /// </summary>
    public bool Equals(ModuleSymbol? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;

        if (PackageAlias != other.PackageAlias) return false;
        if (PackageInstanceKey != other.PackageInstanceKey) return false;

        // 比较路径
        if (Path.Count != other.Path.Count) return false;
        for (int i = 0; i < Path.Count; i++)
        {
            if (Path[i] != other.Path[i]) return false;
        }

        return true;
    }

    /// <summary>
    /// 基于路径计算哈希值
    /// </summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(PackageAlias);
        hash.Add(PackageInstanceKey);
        foreach (var segment in Path)
        {
            hash.Add(segment);
        }
        return hash.ToHashCode();
    }
}
