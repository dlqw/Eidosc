namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private string CreateCallableResolutionArgumentTypeKey(Type type)
    {
        return type switch
        {
            TyCon constructor => CreateCallableResolutionConstructorKey(constructor),
            TyVar variable => $"var:{variable.Index}",
            TyRef reference => $"ref({CreateCallableResolutionArgumentTypeKey(reference.Inner)})",
            TyMutRef reference => $"mref({CreateCallableResolutionArgumentTypeKey(reference.Inner)})",
            TyShared shared => $"shared({CreateCallableResolutionArgumentTypeKey(shared.Inner)})",
            TyTuple tuple => CreateCallableResolutionTupleKey(tuple),
            TyFun function => CreateCallableResolutionFunctionKey(function),
            TyReflProof proof => proof.WitnessType == null
                ? "refl"
                : $"refl({CreateCallableResolutionArgumentTypeKey(proof.WitnessType)})",
            EffectTag ability => CreateCallableResolutionEffectKey(ability),
            EffectRow abilities => CreateCallableResolutionEffectRowKey(abilities),
            RequestType request => CreateCallableResolutionRequestKey(request),
            _ => type.ToString() ?? type.GetType().Name
        };
    }

    private string CreateCallableResolutionConstructorKey(TyCon constructor)
    {
        var qualifiedName = TryFormatQualifiedSymbolName(constructor.Symbol);
        var head = constructor.ConstructorVarIndex.HasValue
            ? $"ctorvar:{constructor.ConstructorVarIndex.Value}"
            : string.IsNullOrWhiteSpace(qualifiedName)
                ? $"name:{constructor.Name}"
                : $"symbol:{qualifiedName}";
        return constructor.Args.Count == 0
            ? head
            : $"{head}[{JoinCallableResolutionTypeKeys(constructor.Args)}]";
    }

    private string CreateCallableResolutionTupleKey(TyTuple tuple) =>
        $"tuple({JoinCallableResolutionTypeKeys(tuple.Elements)})";

    private string CreateCallableResolutionFunctionKey(TyFun function) =>
        $"fun({JoinCallableResolutionTypeKeys(function.Params)})->" +
        $"{CreateCallableResolutionArgumentTypeKey(function.Result)}!" +
        CreateCallableResolutionEffectRowKey(function.Effects);

    private string CreateCallableResolutionEffectKey(EffectTag ability)
    {
        var qualifiedName = TryFormatQualifiedSymbolName(ability.Symbol);
        var head = string.IsNullOrWhiteSpace(qualifiedName)
            ? ability.Name
            : qualifiedName;
        return ability.TypeArgs.Count == 0
            ? head
            : $"{head}[{JoinCallableResolutionTypeKeys(ability.TypeArgs)}]";
    }

    private string CreateCallableResolutionEffectRowKey(EffectRow? abilities)
    {
        return abilities == null
            ? "abilities()"
            : $"abilities({string.Join(",", abilities.Effects
                .Select(CreateCallableResolutionEffectKey)
                .Order(StringComparer.Ordinal))})";
    }

    private string CreateCallableResolutionRequestKey(RequestType request)
    {
        var payload = request.Payload == null
            ? ""
            : CreateCallableResolutionArgumentTypeKey(request.Payload);
        var resumeArg = request.ResumeArg == null
            ? ""
            : CreateCallableResolutionArgumentTypeKey(request.ResumeArg);
        return $"request({CreateCallableResolutionArgumentTypeKey(request.Effect)}," +
               $"{CreateCallableResolutionArgumentTypeKey(request.Result)},{payload},{resumeArg})";
    }

    private string JoinCallableResolutionTypeKeys(IEnumerable<Type> types) =>
        string.Join(",", types.Select(CreateCallableResolutionArgumentTypeKey));

    private static bool ContainsTypeVariable(Type type)
    {
        return type switch
        {
            TyVar => true,
            TyCon constructor => constructor.ConstructorVarIndex.HasValue ||
                                 constructor.Args.Any(ContainsTypeVariable),
            TyFun function => function.Params.Any(ContainsTypeVariable) ||
                              ContainsTypeVariable(function.Result) ||
                              ContainsTypeVariable(function.Effects),
            TyTuple tuple => tuple.Elements.Any(ContainsTypeVariable),
            TyReflProof proof => proof.WitnessType != null && ContainsTypeVariable(proof.WitnessType),
            EffectTag ability => ability.TypeArgs.Any(ContainsTypeVariable),
            EffectRow abilities => abilities.Variables.Count > 0 ||
                                    abilities.Effects.Any(ContainsTypeVariable),
            RequestType request => ContainsTypeVariable(request.Effect) ||
                                   ContainsTypeVariable(request.Result) ||
                                   (request.Payload != null && ContainsTypeVariable(request.Payload)) ||
                                   (request.ResumeArg != null && ContainsTypeVariable(request.ResumeArg)),
            _ => false
        };
    }
}
