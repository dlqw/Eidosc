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
        return con.Args.Count == 0
            ? head
            : $"{head}[{string.Join(",", con.Args.Select(arg => Build(arg, tyConTypeIdResolver)))}]";
    }
}
