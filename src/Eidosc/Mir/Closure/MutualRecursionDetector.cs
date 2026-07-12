namespace Eidosc.Mir.Closure;

/// <summary>
/// 相互递归检测器 - 使用 Tarjan 算法检测强连通分量
/// </summary>
public sealed class MutualRecursionDetector
{
    private readonly Dictionary<string, HashSet<string>> _callGraph = [];

    /// <summary>
    /// 添加调用关系
    /// </summary>
    public void AddCall(string from, string to)
    {
        if (!_callGraph.ContainsKey(from))
            _callGraph[from] = [];
        _callGraph[from].Add(to);
    }

    /// <summary>
    /// 查找强连通分量
    /// </summary>
    public List<HashSet<string>> FindStronglyConnectedComponents()
    {
        var sccs = new List<HashSet<string>>();
        var visited = new HashSet<string>();
        var inStack = new HashSet<string>();
        var stack = new Stack<string>();
        var lowLink = new Dictionary<string, int>();
        var index = new Dictionary<string, int>();
        var currentIndex = 0;

        foreach (var node in _callGraph.Keys)
        {
            if (!visited.Contains(node))
            {
                FindSCC(node, visited, inStack, stack, lowLink, index, ref currentIndex, sccs);
            }
        }

        return sccs;
    }

    private void FindSCC(
        string node,
        HashSet<string> visited,
        HashSet<string> inStack,
        Stack<string> stack,
        Dictionary<string, int> lowLink,
        Dictionary<string, int> index,
        ref int currentIndex,
        List<HashSet<string>> sccs)
    {
        visited.Add(node);
        index[node] = currentIndex;
        lowLink[node] = currentIndex;
        currentIndex++;
        stack.Push(node);
        inStack.Add(node);

        if (_callGraph.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (!visited.Contains(neighbor))
                {
                    FindSCC(neighbor, visited, inStack, stack, lowLink, index, ref currentIndex, sccs);
                    lowLink[node] = Math.Min(lowLink[node], lowLink[neighbor]);
                }
                else if (inStack.Contains(neighbor))
                {
                    lowLink[node] = Math.Min(lowLink[node], index[neighbor]);
                }
            }
        }

        // 如果 node 是 SCC 的根节点
        if (lowLink[node] == index[node])
        {
            var scc = new HashSet<string>();
            string w;
            do
            {
                w = stack.Pop();
                inStack.Remove(w);
                scc.Add(w);
            } while (w != node);

            if (scc.Count > 1)
                sccs.Add(scc);
        }
    }
}
