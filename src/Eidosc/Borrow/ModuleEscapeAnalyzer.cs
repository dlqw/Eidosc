using Eidosc.Mir;

namespace Eidosc.Borrow;

/// <summary>
/// 单个参数的字段级逃逸信息。
/// </summary>
public sealed class ParamEscapeInfo
{
    /// <summary>
    /// 参数整体逃逸到堆/未知函数。
    /// </summary>
    public bool FullyEscapes { get; set; }

    /// <summary>
    /// 如果不整体逃逸，哪些字段的值逃逸。
    /// Key: 字段索引, Value: true 表示该字段逃逸。
    /// </summary>
    public Dictionary<int, bool> FieldEscapes { get; init; } = [];
}

/// <summary>
/// 单个函数的字段级逃逸摘要。
/// 扩展 FunctionEscapeSummary，提供字段级精度。
/// </summary>
public sealed class FieldEscapeSummary
{
    public string FunctionName { get; init; } = "";
    public SymbolId FunctionSymbolId { get; init; } = SymbolId.None;
    public bool IsRecursive { get; init; }
    public Dictionary<int, ParamEscapeInfo> ParamEscapes { get; init; } = [];
}

/// <summary>
/// 单个函数的参数逃逸摘要。
/// </summary>
public sealed class FunctionEscapeSummary
{
    public string FunctionName { get; init; } = "";
    public SymbolId FunctionSymbolId { get; init; } = SymbolId.None;
    public HashSet<int> EscapingParams { get; init; } = [];
    public bool IsRecursive { get; init; }
}

/// <summary>
/// 模块级过程间逃逸分析器。
///
/// 通过构建模块内调用图，按拓扑序自底向上计算每个函数的参数逃逸摘要。
/// 这使得 StackPromotionAnalyzer 能精确判断 MirCall 参数是否逃逸，
/// 而非保守地将所有调用参数标记为逃逸。
///
/// 算法：
/// 1. BuildCallGraph — 遍历 module.Functions，提取模块内调用边
/// 2. FindRecursiveFunctions — 检测自递归 + 互递归（Tarjan SCC）
/// 3. ComputeTopologicalOrder — DFS 后序遍历调用图
/// 4. ComputeEscapeSummaries — 按拓扑序计算每个函数的参数逃逸信息
/// </summary>
public sealed class ModuleEscapeAnalyzer
{
    private readonly MirModule _module;
    private readonly Dictionary<string, HashSet<string>> _callGraph = [];
    private readonly Dictionary<string, FunctionEscapeSummary> _summaries = [];

    public IReadOnlyDictionary<string, FunctionEscapeSummary> Summaries => _summaries;

    public ModuleEscapeAnalyzer(MirModule module)
    {
        _module = module;
    }

    public void Analyze()
    {
        BuildCallGraph();
        var recursiveFunctions = FindRecursiveFunctions();
        ComputeEscapeSummaries(recursiveFunctions);
    }

    /// <summary>
    /// 构建模块内函数间的调用图。
    /// 遍历每个函数的所有 MirCall 指令，提取被调用函数名。
    /// 仅记录模块内函数间的调用（忽略外部/FFI调用）。
    /// </summary>
    private void BuildCallGraph()
    {
        var moduleFunctionNames = new HashSet<string>();
        foreach (var func in _module.Functions)
        {
            var functionKey = MirFunctionIdentity.GetStableKey(func);
            moduleFunctionNames.Add(functionKey);
            _callGraph.TryAdd(functionKey, []);
        }

        foreach (var func in _module.Functions)
        {
            var functionKey = MirFunctionIdentity.GetStableKey(func);
            var callees = _callGraph[functionKey];
            foreach (var block in func.BasicBlocks)
            {
                foreach (var instr in block.Instructions)
                {
                    if (instr is MirCall { Function: MirFunctionRef calleeRef })
                    {
                        var calleeKey = MirFunctionIdentity.GetStableKey(calleeRef);
                        if (moduleFunctionNames.Contains(calleeKey) &&
                            calleeKey != functionKey)
                        {
                            callees.Add(calleeKey);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// 检测递归函数（自递归 + 互递归）。
    /// 自递归：函数直接调用自身。
    /// 互递归：使用 Tarjan SCC 算法检测强连通分量（大小 > 1）。
    /// 递归函数所有参数保守标记为逃逸。
    /// </summary>
    private HashSet<string> FindRecursiveFunctions()
    {
        var recursive = new HashSet<string>();

        // 自递归
        foreach (var func in _module.Functions)
        {
            var functionKey = MirFunctionIdentity.GetStableKey(func);
            bool selfRecursive = false;
            foreach (var block in func.BasicBlocks)
            {
                foreach (var instr in block.Instructions)
                {
                    if (instr is MirCall { Function: MirFunctionRef calleeRef } &&
                        MirFunctionIdentity.GetStableKey(calleeRef) == functionKey)
                    {
                        selfRecursive = true;
                        break;
                    }
                }
                if (selfRecursive) break;
            }
            if (selfRecursive)
            {
                recursive.Add(functionKey);
            }
        }

        // 互递归：Tarjan SCC
        var indexCounter = 0;
        var stack = new Stack<string>();
        var onStack = new HashSet<string>();
        var indices = new Dictionary<string, int>();
        var lowLinks = new Dictionary<string, int>();

        foreach (var func in _module.Functions)
        {
            var functionKey = MirFunctionIdentity.GetStableKey(func);
            if (!indices.ContainsKey(functionKey))
            {
                TarjanDfs(functionKey, ref indexCounter, stack, onStack, indices, lowLinks, recursive);
            }
        }

        return recursive;
    }

    private void TarjanDfs(
        string node,
        ref int indexCounter,
        Stack<string> stack,
        HashSet<string> onStack,
        Dictionary<string, int> indices,
        Dictionary<string, int> lowLinks,
        HashSet<string> recursive)
    {
        indices[node] = indexCounter;
        lowLinks[node] = indexCounter;
        indexCounter++;
        stack.Push(node);
        onStack.Add(node);

        if (_callGraph.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                if (!indices.ContainsKey(neighbor))
                {
                    TarjanDfs(neighbor, ref indexCounter, stack, onStack, indices, lowLinks, recursive);
                    lowLinks[node] = Math.Min(lowLinks[node], lowLinks[neighbor]);
                }
                else if (onStack.Contains(neighbor))
                {
                    lowLinks[node] = Math.Min(lowLinks[node], indices[neighbor]);
                }
            }
        }

        // 如果 node 是 SCC 根，弹出 SCC 成员
        if (lowLinks[node] == indices[node])
        {
            var sccMembers = new List<string>();
            string member;
            do
            {
                member = stack.Pop();
                onStack.Remove(member);
                sccMembers.Add(member);
            } while (member != node);

            // SCC 大小 > 1 表示互递归
            if (sccMembers.Count > 1)
            {
                foreach (var m in sccMembers)
                {
                    recursive.Add(m);
                }
            }
        }
    }

    /// <summary>
    /// 按拓扑序计算每个函数的参数逃逸摘要。
    /// 使用 DFS 后序遍历实现拓扑排序。
    /// </summary>
    private void ComputeEscapeSummaries(HashSet<string> recursiveFunctions)
    {
        // DFS 后序天然保证被调用者先于调用者（无需反转）
        var visited = new HashSet<string>();
        var topoOrder = new List<string>();

        foreach (var func in _module.Functions)
        {
            TopologicalDfs(MirFunctionIdentity.GetStableKey(func), visited, topoOrder);
        }

        // 按拓扑序计算摘要（被调用者先于调用者处理）
        foreach (var funcName in topoOrder)
        {
            var func = _module.Functions.FirstOrDefault(f => MirFunctionIdentity.GetStableKey(f) == funcName);
            if (func == null) continue;

            if (recursiveFunctions.Contains(funcName))
            {
                // 递归函数：所有参数保守逃逸
                var paramCount = func.Locals.Count(l => l.IsParameter);
                _summaries[funcName] = new FunctionEscapeSummary
                {
                    FunctionName = func.Name,
                    FunctionSymbolId = func.SymbolId,
                    EscapingParams = Enumerable.Range(0, paramCount).ToHashSet(),
                    IsRecursive = true
                };
            }
            else
            {
                _summaries[funcName] = ComputeFunctionEscapeSummary(func);
            }
        }
    }

    private void TopologicalDfs(string node, HashSet<string> visited, List<string> order)
    {
        if (!visited.Add(node)) return;

        if (_callGraph.TryGetValue(node, out var neighbors))
        {
            foreach (var neighbor in neighbors)
            {
                TopologicalDfs(neighbor, visited, order);
            }
        }

        order.Add(node);
    }

    /// <summary>
    /// 计算单个非递归函数的参数逃逸摘要。
    /// 扫描所有指令，判断哪些参数 local 在逃逸上下文中被使用。
    /// </summary>
    private FunctionEscapeSummary ComputeFunctionEscapeSummary(MirFunc func)
    {
        var escapingParams = new HashSet<int>();

        // 构建 local ID → 参数索引的映射
        var paramIndices = new Dictionary<int, int>();
        for (int i = 0; i < func.Locals.Count; i++)
        {
            if (func.Locals[i].IsParameter)
            {
                paramIndices[func.Locals[i].Id.Value] = i;
            }
        }

        if (paramIndices.Count == 0)
        {
            return new FunctionEscapeSummary
            {
                FunctionName = func.Name,
                FunctionSymbolId = func.SymbolId,
                EscapingParams = escapingParams,
                IsRecursive = false
            };
        }

        foreach (var block in func.BasicBlocks)
        {
            foreach (var instr in block.Instructions)
            {
                AnalyzeInstructionForEscape(instr, paramIndices, escapingParams);
            }

            // 终止符
            switch (block.Terminator)
            {
                case MirReturn { Value: not null } ret:
                    MarkParamIfLocal(ret.Value, paramIndices, escapingParams);
                    break;
                case MirSwitch { Discriminant: var disc }:
                    MarkParamIfLocal(disc, paramIndices, escapingParams);
                    break;
            }
        }

        return new FunctionEscapeSummary
        {
            FunctionName = func.Name,
            FunctionSymbolId = func.SymbolId,
            EscapingParams = escapingParams,
            IsRecursive = false
        };
    }

    private void AnalyzeInstructionForEscape(
        MirInstruction instr,
        Dictionary<int, int> paramIndices,
        HashSet<int> escapingParams)
    {
        switch (instr)
        {
            case MirCall call:
                // 检查被调用者是否是模块内函数，如果是，使用其摘要
                if (call.Function is MirFunctionRef calleeRef &&
                    _summaries.TryGetValue(MirFunctionIdentity.GetStableKey(calleeRef), out var calleeSummary))
                {
                    for (int i = 0; i < call.Arguments.Count; i++)
                    {
                        if (calleeSummary.EscapingParams.Contains(i))
                        {
                            MarkParamIfLocal(call.Arguments[i], paramIndices, escapingParams);
                        }
                    }
                }
                else
                {
                    // 未知/外部函数：保守标记所有参数位置的参数为逃逸
                    foreach (var arg in call.Arguments)
                    {
                        MarkParamIfLocal(arg, paramIndices, escapingParams);
                    }
                }
                // 函数操作数本身可能是参数
                MarkParamIfLocal(call.Function, paramIndices, escapingParams);
                break;

            case MirStore store:
                MarkParamIfLocal(store.Value, paramIndices, escapingParams);
                break;

            case MirLoad load:
                MarkParamIfLocal(load.Source, paramIndices, escapingParams);
                break;

            // MirCopy/MirMove: 如果 target 是参数，source 也逃逸（通过别名）
            case MirCopy copy:
                if (IsParamLocal(copy.Target, paramIndices, out var tgtIdx))
                {
                    MarkParamIfLocal(copy.Source, paramIndices, escapingParams);
                }
                break;

            case MirMove move:
                if (IsParamLocal(move.Target, paramIndices, out var tgtIdx2))
                {
                    MarkParamIfLocal(move.Source, paramIndices, escapingParams);
                }
                break;
        }
    }

    private static void MarkParamIfLocal(
        MirOperand? operand,
        Dictionary<int, int> paramIndices,
        HashSet<int> escapingParams)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var localId }
            && paramIndices.TryGetValue(localId.Value, out var paramIdx))
        {
            escapingParams.Add(paramIdx);
        }
    }

    private static bool IsParamLocal(MirOperand? operand, Dictionary<int, int> paramIndices, out int paramIdx)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var localId }
            && paramIndices.TryGetValue(localId.Value, out paramIdx))
        {
            return true;
        }
        paramIdx = -1;
        return false;
    }
}
