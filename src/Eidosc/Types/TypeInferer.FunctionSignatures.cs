using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Semantic;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private Type InferFunctionType(FuncDef funcDef)
    {
        var functionType = InferFunctionSignatureType(
            funcDef.Signature,
            funcDef.TypeParams,
            requiredAbilities: funcDef.RequiredAbilities);
        return funcDef.RequiredAbilities.Count == 0 && funcDef.InferredEffects is { IsPure: false } inferredEffects
            ? ApplyRequiredAbilitiesToFunction(functionType, inferredEffects)
            : functionType;
    }

    private TypeScheme InferFunctionSignatureScheme(FuncDef funcDef)
    {
        if (funcDef.Signature.Count == 0)
        {
            return _env.Generalize(BaseTypes.Unit);
        }

        var typeVarEnv = new Dictionary<string, Type>(StringComparer.Ordinal);
        var kindEnvByName = CreateTypeParamKindMap(funcDef.TypeParams);
        var kindEnvByTypeVar = new Dictionary<int, Kind>();

        RegisterSignatureTypeParams(funcDef.TypeParams, kindEnvByName, typeVarEnv, kindEnvByTypeVar);
        var valueGenericParameterTypes = ResolveValueGenericParameterTypes(funcDef.TypeParams, typeVarEnv);

        _typeParamKindStack.Push(kindEnvByName);
        _typeParamVarKindStack.Push(kindEnvByTypeVar);
        try
        {
            var signatureConstraintGenerator = new ConstraintGenerator(_symbolTable, _substitution);
            foreach (var typeParam in funcDef.TypeParams)
            {
                if (!typeVarEnv.TryGetValue(typeParam.Name, out var typeVar))
                {
                    continue;
                }

                signatureConstraintGenerator.CollectTypeParamConstraints(
                    typeParam,
                    typeVar,
                    typeNode => ConvertType(typeNode, typeVarEnv, allowTypeConstructorReference: true));
            }

            var functionType = ConvertFunctionSignatureType(funcDef.Signature, typeVarEnv);
            if (funcDef.Body.Count == 0 && functionType is TyFun function)
            {
                functionType = StripLeadingUnitParams(function);
            }

            functionType = ApplyRequiredAbilitiesToFunction(
                functionType,
                ResolveRequiredAbilities(funcDef.RequiredAbilities ?? [], typeVarEnv));
            if (funcDef.RequiredAbilities is not { Count: > 0 } &&
                funcDef.InferredEffects is { IsPure: false } inferredEffects)
            {
                functionType = ApplyRequiredAbilitiesToFunction(functionType, inferredEffects);
            }

            var scheme = _env.Generalize(functionType, signatureConstraintGenerator.Constraints.Constraints.ToList());
            RegisterFunctionGenericParameterTypes(funcDef, typeVarEnv, valueGenericParameterTypes);
            return scheme;
        }
        finally
        {
            _typeParamVarKindStack.Pop();
            _typeParamKindStack.Pop();
        }
    }

    private Type InferFunctionSignatureType(
        IReadOnlyList<TypeNode> signature,
        IReadOnlyList<TypeParam> typeParams,
        IReadOnlyList<TypeParam>? outerTypeParams = null,
        IReadOnlyDictionary<string, Kind>? outerKindEnvByName = null,
        IReadOnlyList<EffectRequirementNode>? requiredAbilities = null,
        Type? selfType = null)
    {
        if (signature.Count == 0)
        {
            return BaseTypes.Unit;
        }

        var typeVarEnv = new Dictionary<string, Type>(StringComparer.Ordinal);
        var kindEnvByName = outerKindEnvByName == null
            ? []
            : new Dictionary<string, Kind>(outerKindEnvByName, StringComparer.Ordinal);
        foreach (var pair in CreateTypeParamKindMap(typeParams))
        {
            kindEnvByName[pair.Key] = pair.Value;
        }

        var kindEnvByTypeVar = new Dictionary<int, Kind>();

        RegisterSignatureTypeParams(outerTypeParams ?? [], kindEnvByName, typeVarEnv, kindEnvByTypeVar);
        RegisterSignatureTypeParams(typeParams, kindEnvByName, typeVarEnv, kindEnvByTypeVar);
        ResolveValueGenericParameterTypes(outerTypeParams ?? [], typeVarEnv);
        ResolveValueGenericParameterTypes(typeParams, typeVarEnv);
        if (selfType != null)
        {
            typeVarEnv[WellKnownStrings.Keywords.Self] = selfType;
        }

        _typeParamKindStack.Push(kindEnvByName);
        _typeParamVarKindStack.Push(kindEnvByTypeVar);
        try
        {
            var functionType = ConvertFunctionSignatureType(signature, typeVarEnv);
            return ApplyRequiredAbilitiesToFunction(
                functionType,
                ResolveRequiredAbilities(requiredAbilities ?? [], typeVarEnv));
        }
        finally
        {
            _typeParamVarKindStack.Pop();
            _typeParamKindStack.Pop();
        }
    }

    private Type ConvertFunctionSignatureType(
        IReadOnlyList<TypeNode> signature,
        Dictionary<string, Type> typeVarEnv)
    {
        if (signature.Count == 1)
        {
            return ConvertType(signature[0], typeVarEnv);
        }

        var paramTypes = signature.Take(signature.Count - 1)
            .Select(typeNode => ConvertType(typeNode, typeVarEnv))
            .ToList();
        var returnType = ConvertType(signature[^1], typeVarEnv);

        return new TyFun
        {
            Params = paramTypes,
            Result = returnType
        };
    }

    private void RegisterSignatureTypeParams(
        IReadOnlyList<TypeParam> typeParams,
        IReadOnlyDictionary<string, Kind> kindEnvByName,
        Dictionary<string, Type> typeVarEnv,
        Dictionary<int, Kind> kindEnvByTypeVar)
    {
        foreach (var typeParam in typeParams)
        {
            if (string.IsNullOrWhiteSpace(typeParam.Name) ||
                typeVarEnv.ContainsKey(typeParam.Name))
            {
                continue;
            }

            var typeVar = _substitution.FreshTypeVariable();
            typeVarEnv[typeParam.Name] = typeVar;
            if (typeVar is TyVar typeVariable &&
                kindEnvByName.TryGetValue(typeParam.Name, out var typeParamKind))
            {
                kindEnvByTypeVar[typeVariable.Index] = typeParamKind;
            }
        }
    }

    private static List<string> SplitQualifiedName(string name)
    {
        return name
            .Replace(WellKnownStrings.Separators.ModulePath, WellKnownStrings.Separators.Path, StringComparison.Ordinal)
            .Split(WellKnownStrings.Separators.Path, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
    }
}
