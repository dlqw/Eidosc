namespace Eidosc.Symbols;

public static class ImplTypeShapeFactory
{
    public static ImplTypeShapeNode BuildFromKey(
        ImplTypeRefKey key,
        Func<SymbolId, string?>? typeParameterNameResolver = null,
        Func<ImplTypeRefKey, TypeId>? typeIdResolver = null)
    {
        if (key.IsEmpty)
        {
            return ImplWildcardShapeNode.Instance;
        }

        if (key.ValueArgument is { } valueArgument)
        {
            return valueArgument.IsConcrete
                ? new ImplConcreteValueShapeNode(valueArgument.CanonicalPayload, valueArgument.TypeId)
                : new ImplValueVariableShapeNode(
                    BuildValueVariableName(valueArgument),
                    valueArgument.TypeId);
        }

        if (key.SymbolId.IsValid &&
            key.TypeArguments.IsDefaultOrEmpty &&
            typeParameterNameResolver?.Invoke(key.SymbolId) is { Length: > 0 } typeParameterName)
        {
            return new ImplVariableShapeNode(typeParameterName);
        }

        var resolvedTypeId = typeIdResolver?.Invoke(key) ?? key.TypeId;
        if (!key.TypeArguments.IsDefaultOrEmpty)
        {
            return new ImplConstructorShapeNode(
                BuildShapeHead(key),
                key.TypeArguments
                    .Select(argument => BuildFromKey(argument, typeParameterNameResolver, typeIdResolver))
                    .ToList())
            {
                SymbolId = key.SymbolId,
                TypeId = resolvedTypeId
            };
        }

        if (key.SymbolId.IsValid || resolvedTypeId.IsValid)
        {
            return new ImplConstructorShapeNode(BuildShapeHead(key), [])
            {
                SymbolId = key.SymbolId,
                TypeId = resolvedTypeId
            };
        }

        if (TryGetExplicitVariableIdentity(key.Text, out var variableIdentity))
        {
            return new ImplVariableShapeNode(variableIdentity);
        }

        throw new InvalidOperationException(
            $"Impl type key '{key.Text}' has no structured SymbolId or TypeId.");
    }

    public static string BuildShapeHead(ImplTypeRefKey key)
    {
        if (key.TypeId.IsValid)
        {
            return $"type:{key.TypeId.Value}";
        }

        if (key.SymbolId.IsValid)
        {
            return $"sym:{key.SymbolId.Value}";
        }

        return key.Text;
    }

    public static bool TryGetExplicitVariableIdentity(string text, out string identity)
    {
        const string prefix = "var:";
        if (!string.IsNullOrWhiteSpace(text) &&
            text.StartsWith(prefix, StringComparison.Ordinal) &&
            text.Length > prefix.Length)
        {
            identity = text[prefix.Length..];
            return true;
        }

        identity = string.Empty;
        return false;
    }

    private static string BuildValueVariableName(ImplValueRefKey argument)
    {
        var display = string.IsNullOrWhiteSpace(argument.DisplayText)
            ? argument.VariableIdentity
            : argument.DisplayText;
        return $"value:{display}";
    }
}
