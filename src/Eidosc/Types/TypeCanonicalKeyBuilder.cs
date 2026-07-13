using Eidosc.Symbols;

namespace Eidosc.Types;

public static class TypeCanonicalKeyBuilder
{
    public static string Build(
        Type type,
        Func<TyCon, TypeId> tyConTypeIdResolver)
    {
        return type switch
        {
            TyCon con => BuildTyConKey(con, tyConTypeIdResolver),
            TyVar variable => $"var:{variable.Index}",
            TyRef reference => $"ref({Build(reference.Inner, tyConTypeIdResolver)})",
            TyMutRef reference => $"mref({Build(reference.Inner, tyConTypeIdResolver)})",
            TyShared shared => $"shared({Build(shared.Inner, tyConTypeIdResolver)})",
            TyTuple tuple => $"tuple({string.Join(",", tuple.Elements.Select(element => Build(element, tyConTypeIdResolver)))})",
            TyFun function => $"fun({string.Join(",", function.Params.Select(parameter => Build(parameter, tyConTypeIdResolver)))})->{Build(function.Result, tyConTypeIdResolver)}",
            _ => type.ToString() ?? type.GetType().Name
        };
    }

    private static string BuildTyConKey(
        TyCon con,
        Func<TyCon, TypeId> tyConTypeIdResolver)
    {
        var typeId = tyConTypeIdResolver(con);
        var head = con.ConstructorVarIndex.HasValue
            ? $"ctorvar:{con.ConstructorVarIndex.Value}"
            : typeId.IsValid
                ? $"type:{typeId.Value}"
                : con.Symbol.IsValid
                    ? $"symbol:{con.Symbol.Value}"
                    : $"name:{con.Name}";
        if (con.Args.Count == 0 && con.ValueArgs.Count == 0)
        {
            return head;
        }

        var valueArguments = con.ValueArgs.ToDictionary(static argument => argument.ParameterIndex);
        var argumentCount = con.Args.Count + con.ValueArgs.Count;
        var typeArgumentIndex = 0;
        var arguments = new List<string>(argumentCount);
        for (var parameterIndex = 0; parameterIndex < argumentCount; parameterIndex++)
        {
            if (valueArguments.TryGetValue(parameterIndex, out var valueArgument))
            {
                arguments.Add(BuildValueArgumentKey(valueArgument));
            }
            else if (typeArgumentIndex < con.Args.Count)
            {
                arguments.Add(Build(con.Args[typeArgumentIndex++], tyConTypeIdResolver));
            }
        }

        return arguments.Count == 0
            ? head
            : $"{head}[{string.Join(",", arguments)}]";
    }

    private static string BuildValueArgumentKey(GenericValueArgument argument)
    {
        if (argument.ValueVariableIndex >= 0)
        {
            return $"value-var:{argument.ValueVariableIndex}:{argument.TypeId.Value}";
        }

        if (argument.ReferencedParameterIndex >= 0)
        {
            return $"value-param:{argument.ReferencedParameterIndex}:{argument.CanonicalHash}:{argument.TypeId.Value}";
        }

        return $"value:{argument.CanonicalText}";
    }
}
