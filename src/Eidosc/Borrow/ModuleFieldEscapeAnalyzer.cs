using Eidosc.Mir;

namespace Eidosc.Borrow;

/// <summary>
/// 模块级字段精度逃逸分析器。
/// </summary>
public sealed class ModuleFieldEscapeAnalyzer
{
    private readonly MirModule _module;
    private readonly BorrowModuleAnalysisContext _context;
    private readonly Dictionary<string, FieldEscapeSummary> _summaries = [];
    private List<int>[] _callGraph = [];
    private readonly List<int> _activeFunctionIndices = [];

    public IReadOnlyDictionary<string, FieldEscapeSummary> Summaries => _summaries;

    public ModuleFieldEscapeAnalysisStats Stats { get; } = new();

    public ModuleFieldEscapeAnalyzer(MirModule module)
        : this(module, new BorrowModuleAnalysisContext(module))
    {
    }

    public ModuleFieldEscapeAnalyzer(MirModule module, BorrowModuleAnalysisContext context)
    {
        _module = module;
        _context = context;
    }

    public void Analyze()
    {
        Stats.Reset();
        _summaries.Clear();
        BuildCallGraph();
        var recursiveFunctions = FindRecursiveFunctions();
        ComputeFieldEscapeSummaries(recursiveFunctions);
    }

    private void BuildCallGraph()
    {
        var functionCount = _module.Functions.Count;
        _callGraph = new List<int>[functionCount];
        _activeFunctionIndices.Clear();

        for (int i = 0; i < functionCount; i++)
        {
            if (_module.Functions[i].IsExternal)
            {
                continue;
            }

            _callGraph[i] = [];
            _activeFunctionIndices.Add(i);
            Stats.Functions++;
        }

        foreach (var callerIndex in _activeFunctionIndices)
        {
            var caller = _module.Functions[callerIndex];
            var callees = _callGraph[callerIndex];

            foreach (var block in caller.BasicBlocks)
            {
                foreach (var instr in block.Instructions)
                {
                    if (instr is not MirCall { Function: MirFunctionRef calleeRef })
                    {
                        continue;
                    }

                    if (!_context.TryGetFunctionIndex(calleeRef, out var calleeIndex) ||
                        _module.Functions[calleeIndex].IsExternal ||
                        calleeIndex == callerIndex)
                    {
                        continue;
                    }

                    if (!callees.Contains(calleeIndex))
                    {
                        callees.Add(calleeIndex);
                        Stats.CallEdges++;
                    }
                }
            }
        }
    }

    private bool[] FindRecursiveFunctions()
    {
        var recursive = new bool[_module.Functions.Count];

        foreach (var functionIndex in _activeFunctionIndices)
        {
            var function = _module.Functions[functionIndex];
            var selfRecursive = false;
            foreach (var block in function.BasicBlocks)
            {
                foreach (var instr in block.Instructions)
                {
                    if (instr is MirCall { Function: MirFunctionRef calleeRef } &&
                        _context.TryGetFunctionIndex(calleeRef, out var calleeIndex) &&
                        calleeIndex == functionIndex)
                    {
                        selfRecursive = true;
                        break;
                    }
                }

                if (selfRecursive)
                {
                    break;
                }
            }

            if (selfRecursive)
            {
                recursive[functionIndex] = true;
                Stats.SelfRecursiveFunctions++;
            }
        }

        var nextIndex = 0;
        var stack = new Stack<int>();
        var onStack = new bool[_module.Functions.Count];
        var indices = new int[_module.Functions.Count];
        var lowLinks = new int[_module.Functions.Count];
        Array.Fill(indices, -1);
        Array.Fill(lowLinks, -1);

        foreach (var functionIndex in _activeFunctionIndices)
        {
            if (indices[functionIndex] < 0)
            {
                TarjanDfs(functionIndex, ref nextIndex, stack, onStack, indices, lowLinks, recursive);
            }
        }

        Stats.RecursiveFunctions = recursive.LongCount(static value => value);
        return recursive;
    }

    private void TarjanDfs(
        int node,
        ref int nextIndex,
        Stack<int> stack,
        bool[] onStack,
        int[] indices,
        int[] lowLinks,
        bool[] recursive)
    {
        indices[node] = nextIndex;
        lowLinks[node] = nextIndex;
        nextIndex++;
        stack.Push(node);
        onStack[node] = true;

        foreach (var neighbor in _callGraph[node])
        {
            if (indices[neighbor] < 0)
            {
                TarjanDfs(neighbor, ref nextIndex, stack, onStack, indices, lowLinks, recursive);
                lowLinks[node] = Math.Min(lowLinks[node], lowLinks[neighbor]);
            }
            else if (onStack[neighbor])
            {
                lowLinks[node] = Math.Min(lowLinks[node], indices[neighbor]);
            }
        }

        if (lowLinks[node] != indices[node])
        {
            return;
        }

        Stats.SccCount++;
        var sccMembers = new List<int>();
        int member;
        do
        {
            member = stack.Pop();
            onStack[member] = false;
            sccMembers.Add(member);
        } while (member != node);

        if (sccMembers.Count <= 1)
        {
            return;
        }

        Stats.RecursiveSccCount++;
        foreach (var sccMember in sccMembers)
        {
            recursive[sccMember] = true;
        }
    }

    private void ComputeFieldEscapeSummaries(bool[] recursiveFunctions)
    {
        var visited = new bool[_module.Functions.Count];
        var topoOrder = new List<int>(_activeFunctionIndices.Count);

        foreach (var functionIndex in _activeFunctionIndices)
        {
            TopologicalDfs(functionIndex, visited, topoOrder);
        }

        foreach (var functionIndex in topoOrder)
        {
            var function = _module.Functions[functionIndex];
            var stableKey = _context.GetStableKey(functionIndex);

            if (recursiveFunctions[functionIndex])
            {
                var paramCount = _context.GetParameterCount(functionIndex);
                var paramEscapes = new Dictionary<int, ParamEscapeInfo>(paramCount);
                for (int i = 0; i < paramCount; i++)
                {
                    paramEscapes[i] = new ParamEscapeInfo { FullyEscapes = true };
                }

                _summaries[stableKey] = new FieldEscapeSummary
                {
                    FunctionName = function.Name,
                    FunctionSymbolId = function.SymbolId,
                    IsRecursive = true,
                    ParamEscapes = paramEscapes
                };
                Stats.Summaries++;
                Stats.ParamEscapeEntries += paramEscapes.Count;
                continue;
            }

            var summary = ComputeFieldSummary(function);
            _summaries[stableKey] = summary;
            Stats.Summaries++;
            Stats.ParamEscapeEntries += summary.ParamEscapes.Count;
        }
    }

    private void TopologicalDfs(int node, bool[] visited, List<int> order)
    {
        if (visited[node])
        {
            return;
        }

        visited[node] = true;
        foreach (var neighbor in _callGraph[node])
        {
            TopologicalDfs(neighbor, visited, order);
        }

        order.Add(node);
    }

    private FieldEscapeSummary ComputeFieldSummary(MirFunc function)
    {
        if (function.Locals.Count == 0)
        {
            return CreateSummary(function, []);
        }

        var paramIndices = new Dictionary<int, int>();
        for (int i = 0; i < function.Locals.Count; i++)
        {
            if (function.Locals[i].IsParameter)
            {
                paramIndices[function.Locals[i].Id.Value] = i;
            }
        }

        if (paramIndices.Count == 0)
        {
            return CreateSummary(function, []);
        }

        var aliases = new LocalUnionFind();
        var fullyEscapedLocals = new HashSet<int>();
        Dictionary<int, HashSet<int>>? fieldEscapesByLocal = null;

        foreach (var block in function.BasicBlocks)
        {
            foreach (var instr in block.Instructions)
            {
                AnalyzeInstructionForFieldEscape(instr, fullyEscapedLocals, ref fieldEscapesByLocal, aliases);
            }

            if (block.Terminator is MirReturn { Value: not null } ret)
            {
                MarkLocalFullyEscapes(ret.Value, fullyEscapedLocals);
            }

            if (block.Terminator is MirSwitch { Discriminant: var disc })
            {
                MarkLocalFullyEscapes(disc, fullyEscapedLocals);
            }
        }

        Stats.AliasEdges += aliases.EdgeCount;
        Stats.FullyEscapedLocals += fullyEscapedLocals.Count;
        Stats.FieldEscapedLocals += fieldEscapesByLocal?.Count ?? 0;

        var fullyEscapedRoots = new HashSet<int>();
        foreach (var localValue in fullyEscapedLocals)
        {
            fullyEscapedRoots.Add(aliases.Find(localValue));
        }

        Dictionary<int, HashSet<int>>? fieldEscapesByRoot = null;
        if (fieldEscapesByLocal != null)
        {
            fieldEscapesByRoot = new Dictionary<int, HashSet<int>>();
            foreach (var (localValue, fields) in fieldEscapesByLocal)
            {
                var root = aliases.Find(localValue);
                if (!fieldEscapesByRoot.TryGetValue(root, out var rootFields))
                {
                    rootFields = [];
                    fieldEscapesByRoot[root] = rootFields;
                }

                foreach (var field in fields)
                {
                    rootFields.Add(field);
                }
            }
        }

        var paramEscapes = new Dictionary<int, ParamEscapeInfo>();
        foreach (var (localValue, paramIndex) in paramIndices)
        {
            var root = aliases.Find(localValue);
            if (fullyEscapedRoots.Contains(root))
            {
                EnsureParamInfo(paramIndex, paramEscapes).FullyEscapes = true;
                continue;
            }

            if (fieldEscapesByRoot == null ||
                !fieldEscapesByRoot.TryGetValue(root, out var fieldEscapes))
            {
                continue;
            }

            var info = EnsureParamInfo(paramIndex, paramEscapes);
            foreach (var fieldEscape in fieldEscapes)
            {
                info.FieldEscapes[fieldEscape] = true;
            }
        }

        return CreateSummary(function, paramEscapes);
    }

    private static FieldEscapeSummary CreateSummary(
        MirFunc function,
        Dictionary<int, ParamEscapeInfo> paramEscapes)
    {
        return new FieldEscapeSummary
        {
            FunctionName = function.Name,
            FunctionSymbolId = function.SymbolId,
            IsRecursive = false,
            ParamEscapes = paramEscapes
        };
    }

    private void AnalyzeInstructionForFieldEscape(
        MirInstruction instr,
        HashSet<int> fullyEscapedLocals,
        ref Dictionary<int, HashSet<int>>? fieldEscapesByLocal,
        LocalUnionFind aliases)
    {
        switch (instr)
        {
            case MirCall call:
                AnalyzeCallForFieldEscape(call, fullyEscapedLocals, ref fieldEscapesByLocal);
                break;

            case MirStore store:
                MarkLocalFullyEscapes(store.Value, fullyEscapedLocals);
                break;

            case MirLoad load:
                MarkLocalFullyEscapes(load.Source, fullyEscapedLocals);
                break;

            case MirCopy copy:
                AddAlias(copy.Target, copy.Source, aliases);
                break;

            case MirMove move:
                AddAlias(move.Target, move.Source, aliases);
                break;

            case MirCaseInject
                {
                    Target: MirPlace target,
                    Operand: MirPlace source
                }:
                AddAlias(target, source, aliases);
                break;

        }
    }

    private void AnalyzeCallForFieldEscape(
        MirCall call,
        HashSet<int> fullyEscapedLocals,
        ref Dictionary<int, HashSet<int>>? fieldEscapesByLocal)
    {
        if (call.Function is MirFunctionRef calleeRef &&
            _summaries.TryGetValue(_context.GetStableKey(calleeRef), out var calleeSummary))
        {
            for (int i = 0; i < call.Arguments.Count; i++)
            {
                if (!calleeSummary.ParamEscapes.TryGetValue(i, out var calleeParamInfo))
                {
                    continue;
                }

                if (calleeParamInfo.FullyEscapes)
                {
                    MarkLocalFullyEscapes(call.Arguments[i], fullyEscapedLocals);
                }
                else if (calleeParamInfo.FieldEscapes.Count > 0)
                {
                    MarkLocalFieldEscapes(call.Arguments[i], calleeParamInfo.FieldEscapes.Keys, ref fieldEscapesByLocal);
                }
            }
        }
        else
        {
            foreach (var arg in call.Arguments)
            {
                MarkLocalFullyEscapes(arg, fullyEscapedLocals);
            }
        }

        MarkLocalFullyEscapes(call.Function, fullyEscapedLocals);
    }

    private static void MarkLocalFullyEscapes(MirOperand? operand, HashSet<int> fullyEscapedLocals)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var localId })
        {
            fullyEscapedLocals.Add(localId.Value);
        }
    }

    private static void MarkLocalFieldEscapes(
        MirOperand? operand,
        IEnumerable<int> fieldIndices,
        ref Dictionary<int, HashSet<int>>? fieldEscapesByLocal)
    {
        if (operand is not MirPlace { Kind: PlaceKind.Local, Local: var localId })
        {
            return;
        }

        fieldEscapesByLocal ??= [];
        if (!fieldEscapesByLocal.TryGetValue(localId.Value, out var fields))
        {
            fields = [];
            fieldEscapesByLocal[localId.Value] = fields;
        }

        foreach (var fieldIndex in fieldIndices)
        {
            fields.Add(fieldIndex);
        }
    }

    private static void AddAlias(MirPlace target, MirPlace source, LocalUnionFind aliases)
    {
        if (target.Kind != PlaceKind.Local || source.Kind != PlaceKind.Local)
        {
            return;
        }

        aliases.Union(target.Local.Value, source.Local.Value);
    }

    private static ParamEscapeInfo EnsureParamInfo(
        int paramIdx,
        Dictionary<int, ParamEscapeInfo> paramEscapes)
    {
        if (!paramEscapes.TryGetValue(paramIdx, out var info))
        {
            info = new ParamEscapeInfo();
            paramEscapes[paramIdx] = info;
        }

        return info;
    }
}
