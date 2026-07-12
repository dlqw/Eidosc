using System.Text;

namespace Eidosc.Mir.Optimize;

public sealed record RecursiveCallEdge
{
    public required string CallerKey { get; init; }
    public required string CallerName { get; init; }
    public required string CalleeKey { get; init; }
    public required string CalleeName { get; init; }
    public required BlockId BlockId { get; init; }
    public required int InstructionIndex { get; init; }
    public required bool IsTailCall { get; init; }
}

public sealed record RecursiveCallComponent
{
    public required List<string> FunctionKeys { get; init; }
    public required List<string> FunctionNames { get; init; }
    public required List<RecursiveCallEdge> Edges { get; init; }

    public bool IsSelfRecursiveOnly => FunctionKeys.Count == 1;
    public bool HasNonTailEdges => Edges.Any(static edge => !edge.IsTailCall);
    public int TailEdgeCount => Edges.Count(static edge => edge.IsTailCall);
    public int NonTailEdgeCount => Edges.Count(static edge => !edge.IsTailCall);
}

public sealed record RecursiveCallAnalysisResult
{
    public required List<RecursiveCallComponent> Components { get; init; }

    public int ComponentCount => Components.Count;
    public int RecursiveFunctionCount => Components.Sum(static component => component.FunctionKeys.Count);
    public int TailEdgeCount => Components.Sum(static component => component.TailEdgeCount);
    public int NonTailEdgeCount => Components.Sum(static component => component.NonTailEdgeCount);
}

public static class RecursiveCallAnalysis
{
    public static RecursiveCallAnalysisResult Analyze(MirModule module)
    {
        var functionByKey = module.Functions
            .Where(static function => !function.IsExternal)
            .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First(),
                StringComparer.Ordinal);
        var callEdges = CollectDirectCallEdges(functionByKey);
        var adjacency = BuildAdjacency(functionByKey.Keys, callEdges);
        var components = FindStronglyConnectedComponents(adjacency)
            .Where(component => IsRecursiveComponent(component, adjacency))
            .Select(component => CreateComponent(component, functionByKey, callEdges))
            .OrderBy(static component => component.FunctionNames[0], StringComparer.Ordinal)
            .ToList();

        return new RecursiveCallAnalysisResult
        {
            Components = components
        };
    }

    public static string Format(RecursiveCallAnalysisResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine("recursive_call_analysis:");
        sb.AppendLine($"  components: {result.ComponentCount}");
        sb.AppendLine($"  functions: {result.RecursiveFunctionCount}");
        sb.AppendLine($"  tail_edges: {result.TailEdgeCount}");
        sb.AppendLine($"  non_tail_edges: {result.NonTailEdgeCount}");

        foreach (var component in result.Components)
        {
            var kind = component.IsSelfRecursiveOnly ? "self" : "mutual";
            var status = component.HasNonTailEdges ? "non-tail-present" : "tail-only";
            sb.AppendLine($"  - {kind} {status}: {string.Join(", ", component.FunctionNames)}");
            foreach (var edge in component.Edges.OrderBy(static edge => edge.CallerName, StringComparer.Ordinal)
                         .ThenBy(static edge => edge.BlockId.Value)
                         .ThenBy(static edge => edge.InstructionIndex))
            {
                var edgeKind = edge.IsTailCall ? "tail" : "non-tail";
                sb.AppendLine(
                    $"      {edgeKind}: {edge.CallerName} -> {edge.CalleeName} at bb{edge.BlockId.Value}:{edge.InstructionIndex}");
            }
        }

        return sb.ToString();
    }

    private static List<RecursiveCallEdge> CollectDirectCallEdges(Dictionary<string, MirFunc> functionByKey)
    {
        var edges = new List<RecursiveCallEdge>();
        foreach (var caller in functionByKey.Values)
        {
            var callerKey = MirFunctionIdentity.GetStableKey(caller);
            var callerName = GetDisplayName(caller);
            foreach (var block in caller.BasicBlocks)
            {
                for (var i = 0; i < block.Instructions.Count; i++)
                {
                    if (block.Instructions[i] is not MirCall { Function: MirFunctionRef calleeRef } call)
                    {
                        continue;
                    }

                    var calleeKey = MirFunctionIdentity.GetStableKey(calleeRef);
                    if (!functionByKey.TryGetValue(calleeKey, out var callee))
                    {
                        continue;
                    }

                    edges.Add(new RecursiveCallEdge
                    {
                        CallerKey = callerKey,
                        CallerName = callerName,
                        CalleeKey = calleeKey,
                        CalleeName = GetDisplayName(callee),
                        BlockId = block.Id,
                        InstructionIndex = i,
                        IsTailCall = call.IsTailCall
                    });
                }
            }
        }

        return edges;
    }

    private static Dictionary<string, List<string>> BuildAdjacency(
        IEnumerable<string> functionKeys,
        IReadOnlyList<RecursiveCallEdge> callEdges)
    {
        var adjacency = functionKeys.ToDictionary(
            static key => key,
            static _ => new List<string>(),
            StringComparer.Ordinal);
        foreach (var edge in callEdges)
        {
            if (adjacency.TryGetValue(edge.CallerKey, out var callees))
            {
                callees.Add(edge.CalleeKey);
            }
        }

        return adjacency;
    }

    private static List<List<string>> FindStronglyConnectedComponents(Dictionary<string, List<string>> adjacency)
    {
        var index = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>(StringComparer.Ordinal);
        var indices = new Dictionary<string, int>(StringComparer.Ordinal);
        var lowLinks = new Dictionary<string, int>(StringComparer.Ordinal);
        var components = new List<List<string>>();

        foreach (var node in adjacency.Keys.OrderBy(static key => key, StringComparer.Ordinal))
        {
            if (!indices.ContainsKey(node))
            {
                Visit(node);
            }
        }

        return components;

        void Visit(string node)
        {
            indices[node] = index;
            lowLinks[node] = index;
            index++;
            stack.Push(node);
            onStack.Add(node);

            foreach (var successor in adjacency[node])
            {
                if (!adjacency.ContainsKey(successor))
                {
                    continue;
                }

                if (!indices.ContainsKey(successor))
                {
                    Visit(successor);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[successor]);
                }
                else if (onStack.Contains(successor))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indices[successor]);
                }
            }

            if (lowLinks[node] != indices[node])
            {
                return;
            }

            var component = new List<string>();
            string current;
            do
            {
                current = stack.Pop();
                onStack.Remove(current);
                component.Add(current);
            } while (!string.Equals(current, node, StringComparison.Ordinal));

            component.Sort(StringComparer.Ordinal);
            components.Add(component);
        }
    }

    private static bool IsRecursiveComponent(
        IReadOnlyList<string> component,
        Dictionary<string, List<string>> adjacency)
    {
        return component.Count > 1 ||
               adjacency.TryGetValue(component[0], out var successors) &&
               successors.Contains(component[0], StringComparer.Ordinal);
    }

    private static RecursiveCallComponent CreateComponent(
        List<string> componentKeys,
        Dictionary<string, MirFunc> functionByKey,
        IReadOnlyList<RecursiveCallEdge> callEdges)
    {
        var keySet = componentKeys.ToHashSet(StringComparer.Ordinal);
        var edges = callEdges
            .Where(edge => keySet.Contains(edge.CallerKey) && keySet.Contains(edge.CalleeKey))
            .ToList();
        var names = componentKeys
            .Select(key => GetDisplayName(functionByKey[key]))
            .OrderBy(static name => name, StringComparer.Ordinal)
            .ToList();

        return new RecursiveCallComponent
        {
            FunctionKeys = componentKeys,
            FunctionNames = names,
            Edges = edges
        };
    }

    private static string GetDisplayName(MirFunc function)
    {
        if (!string.IsNullOrWhiteSpace(function.SourceName))
        {
            return function.SourceName;
        }

        return string.IsNullOrWhiteSpace(function.Name) ? "<anonymous>" : function.Name;
    }
}
