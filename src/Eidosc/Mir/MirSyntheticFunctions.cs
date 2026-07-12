namespace Eidosc.Mir;

internal static class MirSyntheticFunctions
{
    public const string LambdaRole = "lambda";
    public const string RecursiveClosureRole = "recursive_closure";

    private const string SyntheticPrefix = "synthetic";

    public static bool HasRole(MirFunctionRef functionRef, params ReadOnlySpan<string> roles)
    {
        return HasRole(functionRef.FunctionId, roles);
    }

    public static bool HasRole(FunctionId? functionId, params ReadOnlySpan<string> roles)
    {
        if (functionId is not { SymbolId.IsValid: false } ||
            string.IsNullOrWhiteSpace(functionId.QualifiedName) ||
            roles.Length == 0)
        {
            return false;
        }

        var segments = functionId.QualifiedName.Split(':');
        if (segments.Length < 5 ||
            !string.Equals(segments[0], SyntheticPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var role = segments[^2];
        foreach (var candidate in roles)
        {
            if (string.Equals(role, candidate, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
