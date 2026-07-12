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

        if (IsVariableLikeName(key.Text))
        {
            return new ImplVariableShapeNode(key.Text);
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

    public static bool IsVariableLikeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.Length == 1 && char.IsLetter(name[0]))
        {
            return true;
        }

        return char.IsLower(name[0]);
    }
}
