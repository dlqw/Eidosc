namespace Eidosc.Pipeline;

/// <summary>
/// 模块依赖图：跟踪模块间的导入关系。
/// 正向边：模块 → 它导入的模块。
/// 反向边：模块 → 导入它的模块（用于失效传播）。
/// </summary>
public sealed class ModuleDependencyGraph
{
    private readonly Dictionary<string, HashSet<string>> _forwardEdges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _reverseEdges = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _moduleKeysBySourcePath = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _sourcePathsByModuleKey = new(StringComparer.Ordinal);

    public void RegisterModuleIdentity(string sourcePath, string moduleKey)
    {
        if (string.IsNullOrWhiteSpace(sourcePath) || string.IsNullOrWhiteSpace(moduleKey))
        {
            return;
        }

        sourcePath = SourcePathNormalizer.Normalize(sourcePath);
        if (_moduleKeysBySourcePath.TryGetValue(sourcePath, out var previousKey) &&
            !string.Equals(previousKey, moduleKey, StringComparison.Ordinal))
        {
            if (_sourcePathsByModuleKey.TryGetValue(previousKey, out var previousSources))
            {
                previousSources.Remove(sourcePath);
                if (previousSources.Count == 0)
                {
                    _sourcePathsByModuleKey.Remove(previousKey);
                }
            }
        }

        _moduleKeysBySourcePath[sourcePath] = moduleKey;
        if (!_sourcePathsByModuleKey.TryGetValue(moduleKey, out var sourcePaths))
        {
            sourcePaths = new HashSet<string>(StringComparer.Ordinal);
            _sourcePathsByModuleKey[moduleKey] = sourcePaths;
        }

        sourcePaths.Add(sourcePath);
    }

    public bool TryGetModuleKeyForSourcePath(string sourcePath, out string moduleKey) =>
        _moduleKeysBySourcePath.TryGetValue(SourcePathNormalizer.Normalize(sourcePath), out moduleKey!);

    public IReadOnlySet<string> GetSourcePathsForModuleKey(string moduleKey)
    {
        return _sourcePathsByModuleKey.TryGetValue(moduleKey, out var sourcePaths)
            ? sourcePaths
            : EmptySet;
    }

    /// <summary>
    /// 添加一条依赖边：importer 依赖于 imported
    /// </summary>
    public void AddDependency(string importer, string imported)
    {
        if (!_forwardEdges.TryGetValue(importer, out var deps))
        {
            deps = new HashSet<string>(StringComparer.Ordinal);
            _forwardEdges[importer] = deps;
        }
        deps.Add(imported);

        if (!_reverseEdges.TryGetValue(imported, out var dependents))
        {
            dependents = new HashSet<string>(StringComparer.Ordinal);
            _reverseEdges[imported] = dependents;
        }
        dependents.Add(importer);
    }

    public void SetDependencies(string importer, IEnumerable<string> importedModules)
    {
        if (_forwardEdges.Remove(importer, out var previousDeps))
        {
            foreach (var previousDep in previousDeps)
            {
                if (!_reverseEdges.TryGetValue(previousDep, out var dependents))
                {
                    continue;
                }

                dependents.Remove(importer);
                if (dependents.Count == 0)
                {
                    _reverseEdges.Remove(previousDep);
                }
            }
        }

        foreach (var imported in importedModules.Where(static imported => !string.IsNullOrWhiteSpace(imported)))
        {
            AddDependency(importer, imported);
        }
    }

    public void Clear()
    {
        _forwardEdges.Clear();
        _reverseEdges.Clear();
        _moduleKeysBySourcePath.Clear();
        _sourcePathsByModuleKey.Clear();
    }

    /// <summary>
    /// 获取模块的直接依赖
    /// </summary>
    public IReadOnlySet<string> GetDependencies(string module)
    {
        return _forwardEdges.TryGetValue(module, out var deps) ? deps : EmptySet;
    }

    /// <summary>
    /// 获取直接依赖于该模块的模块
    /// </summary>
    public IReadOnlySet<string> GetDependents(string module)
    {
        return _reverseEdges.TryGetValue(module, out var deps) ? deps : EmptySet;
    }

    /// <summary>
    /// 计算传递闭包：从给定模块集合出发，所有受影响的模块（包括自身）
    /// </summary>
    public HashSet<string> GetTransitiveDependents(IEnumerable<string> changedModules)
    {
        var affected = new HashSet<string>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var module in changedModules)
        {
            if (affected.Add(module))
                queue.Enqueue(module);
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var dependent in GetDependents(current))
            {
                if (affected.Add(dependent))
                    queue.Enqueue(dependent);
            }
        }

        return affected;
    }

    /// <summary>
    /// 所有已注册的模块
    /// </summary>
    public IEnumerable<string> AllModules => _forwardEdges.Keys
        .Concat(_reverseEdges.Keys)
        .Concat(_sourcePathsByModuleKey.Keys)
        .Distinct(StringComparer.Ordinal);

    /// <summary>
    /// 拓扑排序（依赖在前）
    /// </summary>
    public List<string> TopologicalSort()
    {
        var result = new List<string>();
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var inStack = new HashSet<string>(StringComparer.Ordinal);

        foreach (var module in AllModules)
        {
            Visit(module, result, visited, inStack);
        }

        return result;
    }

    private void Visit(string module, List<string> result, HashSet<string> visited, HashSet<string> inStack)
    {
        if (visited.Contains(module))
            return;
        if (inStack.Contains(module))
            return; // Cycle detected, skip

        inStack.Add(module);

        foreach (var dep in GetDependencies(module))
        {
            Visit(dep, result, visited, inStack);
        }

        inStack.Remove(module);
        visited.Add(module);
        result.Add(module);
    }

    private static readonly IReadOnlySet<string> EmptySet = new HashSet<string>(StringComparer.Ordinal);
}
