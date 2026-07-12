namespace Eidosc.Mir;

public sealed class StaticBranchRegistry
{
    private readonly Dictionary<string, Dictionary<string, StaticBranchInfo>> _branchesByHandler = new(StringComparer.Ordinal);

    public void Register(string handlerName, string ability, string operation, StaticBranchInfo info)
    {
        if (!_branchesByHandler.TryGetValue(handlerName, out var operations))
        {
            operations = new Dictionary<string, StaticBranchInfo>(StringComparer.Ordinal);
            _branchesByHandler[handlerName] = operations;
        }
        operations[operation] = info;
    }

    public bool TryResolve(string handlerName, string operation, out StaticBranchInfo info)
    {
        info = default;
        if (!_branchesByHandler.TryGetValue(handlerName, out var operations))
        {
            return false;
        }
        return operations.TryGetValue(operation, out info);
    }

    public bool TryGetAllBranches(string handlerName, out IReadOnlyDictionary<string, StaticBranchInfo> branches)
    {
        if (_branchesByHandler.TryGetValue(handlerName, out var operations))
        {
            branches = operations;
            return true;
        }
        branches = null!;
        return false;
    }
}
