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
        if (constructor.Args.Count == 0 && constructor.ValueArgs.Count == 0)
        {
            return head;
        }

        var valueArguments = constructor.ValueArgs.ToDictionary(static argument => argument.ParameterIndex);
        var typeArgumentIndex = 0;
        var argumentCount = constructor.Args.Count + constructor.ValueArgs.Count;
        var arguments = new List<string>(argumentCount);
        for (var parameterIndex = 0; parameterIndex < argumentCount; parameterIndex++)
        {
            if (valueArguments.TryGetValue(parameterIndex, out var valueArgument))
            {
                arguments.Add(valueArgument.ValueVariableIndex >= 0
                    ? $"value-var:{valueArgument.ValueVariableIndex}:{valueArgument.TypeId.Value}"
                    : valueArgument.ReferencedParameterIndex >= 0
                        ? $"value-param:{valueArgument.ReferencedParameterIndex}:{valueArgument.CanonicalHash}:{valueArgument.TypeId.Value}"
                        : $"value:{valueArgument.CanonicalText}");
            }
            else if (typeArgumentIndex < constructor.Args.Count)
            {
                arguments.Add($"type:{CreateCallableResolutionArgumentTypeKey(constructor.Args[typeArgumentIndex++])}");
            }
        }

        return $"{head}[{string.Join(",", arguments)}]";
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
                                 constructor.ValueArgs.Any(static argument => !argument.IsConcrete) ||
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
