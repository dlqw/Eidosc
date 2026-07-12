using Eidosc.Mir;
using Eidosc.Types;

namespace Eidosc.Borrow;

public sealed class BorrowModuleAnalysisContext
{
    private readonly MirModule _module;
    private readonly Dictionary<string, MirFunc> _functionByStableKey;
    private readonly Dictionary<string, int> _functionIndexByStableKey;
    private readonly Dictionary<MirFunc, string> _stableKeyByFunction;
    private readonly string[] _stableKeysByIndex;
    private readonly int[] _parameterCountsByIndex;
    private readonly Dictionary<int, bool> _managedTypeCache = [];

    public BorrowModuleAnalysisContext(MirModule module)
    {
        _module = module;
        _functionByStableKey = new Dictionary<string, MirFunc>(module.Functions.Count, StringComparer.Ordinal);
        _functionIndexByStableKey = new Dictionary<string, int>(module.Functions.Count, StringComparer.Ordinal);
        _stableKeyByFunction = new Dictionary<MirFunc, string>(module.Functions.Count);
        _stableKeysByIndex = new string[module.Functions.Count];
        _parameterCountsByIndex = new int[module.Functions.Count];

        for (int i = 0; i < module.Functions.Count; i++)
        {
            var function = module.Functions[i];
            var stableKey = MirFunctionIdentity.GetStableKey(function);
            _stableKeysByIndex[i] = stableKey;
            _stableKeyByFunction[function] = stableKey;
            _functionByStableKey[stableKey] = function;
            _functionIndexByStableKey[stableKey] = i;
            _parameterCountsByIndex[i] = function.Locals.Count(static local => local.IsParameter);
        }
    }

    public IReadOnlyList<MirFunc> Functions => _module.Functions;

    public IReadOnlyDictionary<string, MirFunc> FunctionByStableKey => _functionByStableKey;

    public string GetStableKey(MirFunc function)
    {
        return _stableKeyByFunction.TryGetValue(function, out var stableKey)
            ? stableKey
            : MirFunctionIdentity.GetStableKey(function);
    }

    public string GetStableKey(int functionIndex)
    {
        return _stableKeysByIndex[functionIndex];
    }

    public string GetStableKey(MirFunctionRef functionRef)
    {
        return MirFunctionIdentity.GetStableKey(functionRef);
    }

    public bool TryGetFunction(MirFunctionRef functionRef, out MirFunc function)
    {
        var stableKey = GetStableKey(functionRef);
        return _functionByStableKey.TryGetValue(stableKey, out function!);
    }

    public bool TryGetFunctionIndex(MirFunctionRef functionRef, out int functionIndex)
    {
        var stableKey = GetStableKey(functionRef);
        return _functionIndexByStableKey.TryGetValue(stableKey, out functionIndex);
    }

    public bool TryGetFunctionIndex(string stableKey, out int functionIndex)
    {
        return _functionIndexByStableKey.TryGetValue(stableKey, out functionIndex);
    }

    public int GetParameterCount(int functionIndex)
    {
        return _parameterCountsByIndex[functionIndex];
    }

    public bool TryGetParameterCount(MirFunctionRef functionRef, out int parameterCount)
    {
        if (TryGetFunctionIndex(functionRef, out var functionIndex))
        {
            parameterCount = _parameterCountsByIndex[functionIndex];
            return true;
        }

        parameterCount = 0;
        return false;
    }

    public bool IsRuntimeWordAbi(int functionIndex)
    {
        return _module.Functions[functionIndex].IsRuntimeWordAbi;
    }

    public bool IsManagedType(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return false;
        }

        var typeValue = typeId.Value;
        if (_managedTypeCache.TryGetValue(typeValue, out var isManaged))
        {
            return isManaged;
        }

        isManaged = TypeSemantics.IsManagedType(typeId);
        _managedTypeCache[typeValue] = isManaged;
        return isManaged;
    }
}

public sealed class ModuleFieldEscapeAnalysisStats
{
    public long Functions { get; set; }
    public long CallEdges { get; set; }
    public long SelfRecursiveFunctions { get; set; }
    public long RecursiveFunctions { get; set; }
    public long SccCount { get; set; }
    public long RecursiveSccCount { get; set; }
    public long Summaries { get; set; }
    public long ParamEscapeEntries { get; set; }
    public long AliasEdges { get; set; }
    public long FullyEscapedLocals { get; set; }
    public long FieldEscapedLocals { get; set; }

    public void Reset()
    {
        Functions = 0;
        CallEdges = 0;
        SelfRecursiveFunctions = 0;
        RecursiveFunctions = 0;
        SccCount = 0;
        RecursiveSccCount = 0;
        Summaries = 0;
        ParamEscapeEntries = 0;
        AliasEdges = 0;
        FullyEscapedLocals = 0;
        FieldEscapedLocals = 0;
    }
}

public sealed class UnifiedStackPromotionAnalysisStats
{
    public long InstructionsScanned { get; set; }
    public long ConstructorCandidates { get; set; }
    public long ClosureLookups { get; set; }
    public long ClosureLookupMisses { get; set; }
    public long ClosureCandidates { get; set; }
    public long AliasEdges { get; set; }
    public long EscapedLocals { get; set; }
    public long PromotedAllocations { get; set; }
    public long ManagedFieldChecks { get; set; }

    public void Reset()
    {
        InstructionsScanned = 0;
        ConstructorCandidates = 0;
        ClosureLookups = 0;
        ClosureLookupMisses = 0;
        ClosureCandidates = 0;
        AliasEdges = 0;
        EscapedLocals = 0;
        PromotedAllocations = 0;
        ManagedFieldChecks = 0;
    }
}

internal sealed class LocalUnionFind
{
    private readonly Dictionary<int, int> _parent = [];

    public int NodeCount => _parent.Count;

    public int EdgeCount { get; private set; }

    public void Union(int left, int right)
    {
        EdgeCount++;
        var leftRoot = Find(left);
        var rightRoot = Find(right);
        if (leftRoot != rightRoot)
        {
            _parent[rightRoot] = leftRoot;
        }
    }

    public int Find(int value)
    {
        if (!_parent.TryGetValue(value, out var parent))
        {
            _parent[value] = value;
            return value;
        }

        if (parent == value)
        {
            return value;
        }

        var root = Find(parent);
        _parent[value] = root;
        return root;
    }
}
