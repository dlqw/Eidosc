using System.Collections.Concurrent;

namespace Eidosc.Query;

public sealed class DependencyGraph
{
    private readonly object _sync = new();
    private readonly Dictionary<DepNode, DepNodeIndex> _nodeToIndex = new();
    private readonly List<DepNode> _indexToNode = [];
    private readonly List<HashSet<DepNodeIndex>> _edges = [];
    private readonly List<HashSet<DepNodeIndex>> _reverseEdges = [];

    public DepNodeIndex Record(DepNode node)
    {
        lock (_sync)
        {
            if (_nodeToIndex.TryGetValue(node, out var existing))
            {
                return existing;
            }

            var index = new DepNodeIndex(_indexToNode.Count);
            _nodeToIndex[node] = index;
            _indexToNode.Add(node);
            _edges.Add([]);
            _reverseEdges.Add([]);
            return index;
        }
    }

    public void AddEdge(DepNodeIndex from, DepNodeIndex to)
    {
        if (!from.IsValid || !to.IsValid) return;
        lock (_sync)
        {
            if (from.Value >= _edges.Count || to.Value >= _reverseEdges.Count)
            {
                return;
            }

            _edges[from.Value].Add(to);
            _reverseEdges[to.Value].Add(from);
        }
    }

    public DepNodeIndex GetIndex(DepNode node)
    {
        lock (_sync)
        {
            return _nodeToIndex.TryGetValue(node, out var index) ? index : DepNodeIndex.Invalid;
        }
    }

    public DepNode? GetNode(DepNodeIndex index)
    {
        if (!index.IsValid) return null;
        lock (_sync)
        {
            return index.Value < _indexToNode.Count ? _indexToNode[index.Value] : null;
        }
    }

    public IReadOnlyList<DepNodeIndex> GetDependencies(DepNodeIndex index)
    {
        if (!index.IsValid || index.Value < 0) return [];
        lock (_sync)
        {
            return index.Value < _edges.Count ? _edges[index.Value].ToList() : [];
        }
    }

    public IReadOnlyList<DepNodeIndex> GetDependents(DepNodeIndex index)
    {
        if (!index.IsValid || index.Value < 0) return [];
        lock (_sync)
        {
            return index.Value < _reverseEdges.Count ? _reverseEdges[index.Value].ToList() : [];
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _nodeToIndex.Clear();
            _indexToNode.Clear();
            _edges.Clear();
            _reverseEdges.Clear();
        }
    }
}
