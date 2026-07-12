namespace Eidosc.Pipeline;

/// <summary>
/// 增量编译驱动：检测变更 → 失效 → 重编译受影响模块。
/// 管理模块指纹、依赖图和制品缓存。
/// </summary>
public sealed class IncrementalCompilationDriver
{
    private readonly ModuleArtifactCache _cache;
    private readonly ModuleDependencyGraph _dependencyGraph = new();

    public IncrementalCompilationDriver(string cacheDirectory)
    {
        _cache = new ModuleArtifactCache(cacheDirectory);
    }

    /// <summary>
    /// 依赖图（只读）
    /// </summary>
    public ModuleDependencyGraph Dependencies => _dependencyGraph;

    /// <summary>
    /// 制品缓存（只读）
    /// </summary>
    public ModuleArtifactCache Cache => _cache;

    /// <summary>
    /// 注册模块依赖关系（编译完成后调用）
    /// </summary>
    public void RegisterDependencies(string modulePath, List<string> importedModules)
    {
        foreach (var imported in importedModules)
        {
            _dependencyGraph.AddDependency(modulePath, imported);
        }
    }

    /// <summary>
    /// 更新模块缓存（编译完成后调用）
    /// </summary>
    public void UpdateCache(string modulePath, string sourceText, List<string>? dependencies = null)
    {
        _cache.Update(modulePath, sourceText, dependencies);
    }

    /// <summary>
    /// 检查模块是否需要重新编译
    /// </summary>
    public bool NeedsRecompilation(string modulePath, string currentSourceText)
    {
        return !_cache.IsUpToDate(modulePath, currentSourceText);
    }

    /// <summary>
    /// 计算需要重新编译的模块集合（传递闭包）
    /// </summary>
    public HashSet<string> GetAffectedModules(IEnumerable<string> changedModules)
    {
        return _dependencyGraph.GetTransitiveDependents(changedModules);
    }

    /// <summary>
    /// 获取编译顺序（拓扑排序）
    /// </summary>
    public List<string> GetCompilationOrder()
    {
        return _dependencyGraph.TopologicalSort();
    }

    /// <summary>
    /// 使指定模块的缓存失效
    /// </summary>
    public void Invalidate(params string[] modulePaths)
    {
        foreach (var path in modulePaths)
            _cache.Invalidate(path);
    }

    /// <summary>
    /// 使所有缓存失效
    /// </summary>
    public void InvalidateAll()
    {
        _cache.InvalidateAll();
    }
}
