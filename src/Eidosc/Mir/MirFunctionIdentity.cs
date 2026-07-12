namespace Eidosc.Mir;

internal static class MirFunctionIdentity
{
    public static string GetStableKey(MirFunc function)
    {
        if (TryGetStableKey(function.FunctionId, out var functionIdKey))
        {
            return functionIdKey;
        }

        return GetStableKey(function.Name, function.SymbolId);
    }

    public static string GetStableKey(string? functionName, SymbolId symbolId)
    {
        if (symbolId.IsValid)
        {
            return $"sym:{symbolId.Value}";
        }

        if (!string.IsNullOrWhiteSpace(functionName))
        {
            return $"name:{functionName}";
        }

        return "anon:<unknown>";
    }

    public static string GetStableKey(MirFunctionRef functionRef)
    {
        if (TryGetStableKey(functionRef.FunctionId, out var functionIdKey))
        {
            return functionIdKey;
        }

        return GetStableKey(functionRef.Name, functionRef.SymbolId);
    }

    public static bool TryGetStableKey(FunctionId? functionId, out string key)
    {
        key = string.Empty;
        if (functionId == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(functionId.StableIdentityKey))
        {
            key = $"stable:{functionId.StableIdentityKey}";
            return true;
        }

        if (functionId.SymbolId.IsValid)
        {
            key = !string.IsNullOrWhiteSpace(functionId.ModuleIdentityKey)
                ? $"sym:{functionId.ModuleIdentityKey}::{functionId.SymbolId.Value}"
                : $"sym:{functionId.SymbolId.Value}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(functionId.QualifiedName))
        {
            key = !string.IsNullOrWhiteSpace(functionId.ModuleIdentityKey)
                ? $"qualified:{functionId.ModuleIdentityKey}::{functionId.QualifiedName}"
                : $"qualified:{functionId.QualifiedName}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(functionId.ModuleIdentityKey) &&
            !string.IsNullOrWhiteSpace(functionId.Name))
        {
            key = $"module-id:{functionId.ModuleIdentityKey}::{functionId.Name}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(functionId.Module) &&
            !string.IsNullOrWhiteSpace(functionId.Name))
        {
            key = $"module:{functionId.Module}::{functionId.Name}";
            return true;
        }

        return false;
    }

    public static bool TryGetStableKeyIgnoringSymbolId(FunctionId? functionId, out string key)
    {
        key = string.Empty;
        if (functionId == null)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(functionId.StableIdentityKey))
        {
            key = $"stable:{functionId.StableIdentityKey}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(functionId.QualifiedName))
        {
            key = !string.IsNullOrWhiteSpace(functionId.ModuleIdentityKey)
                ? $"qualified:{functionId.ModuleIdentityKey}::{functionId.QualifiedName}"
                : $"qualified:{functionId.QualifiedName}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(functionId.ModuleIdentityKey) &&
            !string.IsNullOrWhiteSpace(functionId.Name))
        {
            key = $"module-id:{functionId.ModuleIdentityKey}::{functionId.Name}";
            return true;
        }

        if (!string.IsNullOrWhiteSpace(functionId.Module) &&
            !string.IsNullOrWhiteSpace(functionId.Name))
        {
            key = $"module:{functionId.Module}::{functionId.Name}";
            return true;
        }

        return false;
    }
}
