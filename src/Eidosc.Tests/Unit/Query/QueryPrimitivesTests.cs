using Eidosc.Symbols;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Query;

namespace Eidosc.Tests.Unit.Query;

public sealed class DepNodeTests
{
    [Fact]
    public void DepNodeIndex_Invalid_Is_Not_Valid()
    {
        Assert.False(DepNodeIndex.Invalid.IsValid);
        Assert.True(new DepNodeIndex(0).IsValid);
        Assert.True(new DepNodeIndex(42).IsValid);
    }

    [Fact]
    public void Fingerprint_Combine_Is_Deterministic()
    {
        var a = Fingerprint.From(1);
        var b = Fingerprint.From(2);
        var c1 = Fingerprint.Combine(a, b);
        var c2 = Fingerprint.Combine(a, b);
        Assert.Equal(c1, c2);
    }

    [Fact]
    public void DepNode_Create_Produces_Consistent_Fingerprint()
    {
        var n1 = DepNode.Create(DepKind.ParseModule, "test.eidos");
        var n2 = DepNode.Create(DepKind.ParseModule, "test.eidos");
        Assert.Equal(n1, n2);
    }

    [Fact]
    public void DepNode_Different_Kind_Or_Key_Are_Unequal()
    {
        var a = DepNode.Create(DepKind.ParseModule, "a.eidos");
        var b = DepNode.Create(DepKind.ParseModule, "b.eidos");
        var c = DepNode.Create(DepKind.ResolveNames, "a.eidos");
        Assert.NotEqual(a, b);
        Assert.NotEqual(a, c);
    }
}
public sealed class DependencyGraphTests
{
    [Fact]
    public void Record_Returns_Valid_Index()
    {
        var graph = new DependencyGraph();
        var node = DepNode.Create(DepKind.ParseModule, "test.eidos");
        var index = graph.Record(node);
        Assert.True(index.IsValid);
    }

    [Fact]
    public void Record_Same_Node_Returns_Same_Index()
    {
        var graph = new DependencyGraph();
        var node = DepNode.Create(DepKind.ParseModule, "test.eidos");
        var i1 = graph.Record(node);
        var i2 = graph.Record(node);
        Assert.Equal(i1, i2);
    }

    [Fact]
    public void GetIndex_Returns_Recorded_Index()
    {
        var graph = new DependencyGraph();
        var node = DepNode.Create(DepKind.InferTypes, "module");
        var recorded = graph.Record(node);
        var fetched = graph.GetIndex(node);
        Assert.Equal(recorded, fetched);
    }

    [Fact]
    public void GetIndex_Unrecorded_Node_Returns_Invalid()
    {
        var graph = new DependencyGraph();
        var node = DepNode.Create(DepKind.InferTypes, "unknown");
        Assert.Equal(DepNodeIndex.Invalid, graph.GetIndex(node));
    }

    [Fact]
    public void GetNode_Roundtrips()
    {
        var graph = new DependencyGraph();
        var node = DepNode.Create(DepKind.BuildHir, "mod");
        var index = graph.Record(node);
        Assert.Equal(node, graph.GetNode(index));
    }

    [Fact]
    public void GetNode_Invalid_Returns_Null()
    {
        var graph = new DependencyGraph();
        Assert.Null(graph.GetNode(DepNodeIndex.Invalid));
    }

    [Fact]
    public void AddEdge_Creates_Dependency()
    {
        var graph = new DependencyGraph();
        var parent = graph.Record(DepNode.Create(DepKind.ParseModule, "a"));
        var child = graph.Record(DepNode.Create(DepKind.ResolveNames, "a"));
        graph.AddEdge(parent, child);

        var deps = graph.GetDependencies(parent);
        Assert.Single(deps);
        Assert.Equal(child, deps[0]);
    }

    [Fact]
    public void AddEdge_Deduplicates_Dependency()
    {
        var graph = new DependencyGraph();
        var parent = graph.Record(DepNode.Create(DepKind.ParseModule, "a"));
        var child = graph.Record(DepNode.Create(DepKind.ResolveNames, "a"));

        graph.AddEdge(parent, child);
        graph.AddEdge(parent, child);

        Assert.Single(graph.GetDependencies(parent));
        Assert.Single(graph.GetDependents(child));
    }

    [Fact]
    public void Record_Concurrent_Roundtrips_All_Nodes()
    {
        var graph = new DependencyGraph();
        var nodes = Enumerable.Range(0, 512)
            .Select(i => DepNode.Create(DepKind.InferTypes, $"module-{i}"))
            .ToArray();

        Parallel.ForEach(nodes, node =>
        {
            var index = graph.Record(node);
            Assert.True(index.IsValid);
        });

        foreach (var node in nodes)
        {
            var index = graph.GetIndex(node);
            Assert.True(index.IsValid);
            Assert.Equal(node, graph.GetNode(index));
        }
    }

    [Fact]
    public void AddEdge_Invalid_Indices_Are_NoOps()
    {
        var graph = new DependencyGraph();
        var valid = graph.Record(DepNode.Create(DepKind.CodeGen, "x"));
        graph.AddEdge(DepNodeIndex.Invalid, valid);
        graph.AddEdge(valid, DepNodeIndex.Invalid);
        Assert.Empty(graph.GetDependencies(valid));
    }

    [Fact]
    public void GetDependencies_No_Edges_Returns_Empty()
    {
        var graph = new DependencyGraph();
        var index = graph.Record(DepNode.Create(DepKind.Optimize, "x"));
        Assert.Empty(graph.GetDependencies(index));
    }

    [Fact]
    public void GetDependencies_Invalid_Index_Returns_Empty()
    {
        var graph = new DependencyGraph();
        Assert.Empty(graph.GetDependencies(DepNodeIndex.Invalid));
    }

    [Fact]
    public void Clear_Resets_Graph()
    {
        var graph = new DependencyGraph();
        var node = DepNode.Create(DepKind.ParseModule, "a");
        graph.Record(node);
        graph.Clear();
        Assert.Equal(DepNodeIndex.Invalid, graph.GetIndex(node));
    }

    [Fact]
    public void Multiple_Edges_On_Same_Node()
    {
        var graph = new DependencyGraph();
        var parent = graph.Record(DepNode.Create(DepKind.InferTypes, "p"));
        var c1 = graph.Record(DepNode.Create(DepKind.ParseModule, "c1"));
        var c2 = graph.Record(DepNode.Create(DepKind.ResolveNames, "c2"));
        var c3 = graph.Record(DepNode.Create(DepKind.BuildHir, "c3"));
        graph.AddEdge(parent, c1);
        graph.AddEdge(parent, c2);
        graph.AddEdge(parent, c3);

        var deps = graph.GetDependencies(parent);
        Assert.Equal(3, deps.Count);
        Assert.Contains(c1, deps);
        Assert.Contains(c2, deps);
        Assert.Contains(c3, deps);
    }
}

public sealed class QueryCacheTests
{
    [Fact]
    public void DefaultCache_Lookup_Empty_Returns_Null()
    {
        var cache = new DefaultQueryCache<string, int>();
        Assert.Null(cache.Lookup("key"));
    }

    [Fact]
    public void DefaultCache_Insert_Then_Lookup()
    {
        var cache = new DefaultQueryCache<string, int>();
        var depIndex = new DepNodeIndex(0);
        cache.Insert("key", 42, depIndex);
        var result = cache.Lookup("key");
        Assert.NotNull(result);
        Assert.Equal(42, result!.Value.Result);
        Assert.Equal(depIndex, result.Value.DepIndex);
    }

    [Fact]
    public void DefaultCache_Overwrite()
    {
        var cache = new DefaultQueryCache<string, int>();
        cache.Insert("key", 1, new DepNodeIndex(0));
        cache.Insert("key", 2, new DepNodeIndex(1));
        Assert.Equal(2, cache.Lookup("key")!.Value.Result);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void DefaultCache_Clear()
    {
        var cache = new DefaultQueryCache<string, int>();
        cache.Insert("a", 1, new DepNodeIndex(0));
        cache.Insert("b", 2, new DepNodeIndex(1));
        cache.Clear();
        Assert.Null(cache.Lookup("a"));
        Assert.Null(cache.Lookup("b"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void SingleCache_Lookup_Empty_Returns_Null()
    {
        var cache = new SingleQueryCache<int>();
        Assert.Null(cache.Lookup(default));
    }

    [Fact]
    public void SingleCache_Insert_Then_Lookup()
    {
        var cache = new SingleQueryCache<int>();
        var depIndex = new DepNodeIndex(5);
        cache.Insert(default, 99, depIndex);
        var result = cache.Lookup(default);
        Assert.NotNull(result);
        Assert.Equal(99, result!.Value.Result);
        Assert.Equal(depIndex, result.Value.DepIndex);
    }

    [Fact]
    public void SingleCache_Clear()
    {
        var cache = new SingleQueryCache<int>();
        cache.Insert(default, 1, new DepNodeIndex(0));
        cache.Clear();
        Assert.Null(cache.Lookup(default));
        Assert.Equal(0, cache.Count);
    }
}
