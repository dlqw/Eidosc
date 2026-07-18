using Eidosc.Ast;
using Eidosc.Ast.Types;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private Type InstantiateSchemeWithConstraints(TypeScheme scheme, SourceSpan usageSpan)
    {
        var instantiated = _substitution.InstantiateScheme(scheme);
        if (instantiated.Constraints.Count > 0)
        {
            _constraintGenerator.Constraints.AddRange(instantiated.Constraints.Select(constraint => WithConstraintSpan(constraint, usageSpan)));
        }

        return instantiated.Type;
    }

    private Type InstantiateSchemeWithGenericArgumentsAndConstraints(
        TypeScheme scheme,
        IReadOnlyList<GenericArgumentNode> genericArguments,
        SymbolId targetSymbolId,
        EidosAstNode application,
        SourceSpan usageSpan)
    {
        var parameterIds = _symbolTable.GetSymbol<FuncSymbol>(targetSymbolId)?.TypeParams ?? [];
        if (genericArguments.Count > 0 && genericArguments.Count != parameterIds.Count)
        {
            AddError(
                usageSpan,
                $"Function expects {parameterIds.Count} generic arguments but received {genericArguments.Count}.");
        }

        var explicitTypeArgs = genericArguments
            .Select(argument => argument switch
            {
                TypeGenericArgumentNode typeArgument =>
                    ConvertTypeInCurrentTypeParamContext(typeArgument.Type),
                EffectGenericArgumentNode effectArgument =>
                    ConvertTypeInCurrentTypeParamContext(effectArgument.EffectRow),
                _ => null
            })
            .OfType<Type>()
            .ToList();
        InstantiatedTypeScheme instantiated;
        try
        {
            instantiated = genericArguments.Count > 0
                ? _substitution.InstantiateSchemeWithTypeArgs(scheme, explicitTypeArgs)
                : _substitution.InstantiateScheme(scheme);
        }
        catch (TypeInferenceException ex)
        {
            AddError(usageSpan, ex.Message);
            instantiated = _substitution.InstantiateScheme(scheme);
        }

        var valueArguments = new List<GenericValueArgument>();
        for (var parameterIndex = 0; parameterIndex < parameterIds.Count; parameterIndex++)
        {
            if (_symbolTable.GetSymbol<TypeParamSymbol>(parameterIds[parameterIndex]) is not
                {
                    ParameterKind: GenericParameterKind.Value
                } parameter)
            {
                continue;
            }

            var inferredArgument = FindReferencedValueArgument(instantiated.Type, parameterIndex) ??
                                   _substitution.FreshValueVariable(
                                       CreateValueGenericArgumentTemplate(
                                           parameter.Name,
                                           new TyCon { Id = parameter.TypeId },
                                           parameterIndex) with
                                       {
                                           ReferencedParameterIndex = -1
                                       });
            if (parameterIndex < genericArguments.Count &&
                genericArguments[parameterIndex] is ValueGenericArgumentNode explicitValueArgument)
            {
                if (!TryResolveValueParameterDeclaredType(parameter, out var declaredType))
                {
                    AddError(explicitValueArgument.Span, $"Cannot resolve value parameter type for '{parameter.Name}'.");
                }
                else
                {
                    var sourceTypeVarEnv = _typeParamEnvStack.TryPeek(out var currentTypeVarEnv)
                        ? currentTypeVarEnv
                        : new Dictionary<string, Type>(StringComparer.Ordinal);
                    if (TryResolveExplicitValueArgument(
                            explicitValueArgument,
                            parameterIndex,
                            declaredType,
                            sourceTypeVarEnv,
                            out var resolvedExplicitArgument))
                    {
                        try
                        {
                            _substitution.UnifyValueArguments(inferredArgument, resolvedExplicitArgument);
                        }
                        catch (TypeInferenceException ex)
                        {
                            AddError(explicitValueArgument.Span, ex.Message);
                        }
                    }
                }
            }

            valueArguments.Add(inferredArgument with { ParameterIndex = parameterIndex });
        }

        if (valueArguments.Count > 0)
        {
            _valueGenericArgumentsByApplication[application] = valueArguments;
        }

        if (instantiated.Constraints.Count > 0)
        {
            _constraintGenerator.Constraints.AddRange(
                instantiated.Constraints.Select(constraint => WithConstraintSpan(constraint, usageSpan)));
        }

        return instantiated.Type;
    }

    private bool TryResolveValueParameterDeclaredType(TypeParamSymbol parameter, out Type type)
    {
        if (_valueGenericParameterTypesBySymbol.TryGetValue(parameter.Id, out var registeredType))
        {
            type = registeredType;
            return true;
        }

        if (TryCreateTypeFromSymbolMetadataTypeId(parameter.TypeId, out var metadataType))
        {
            type = metadataType;
            return true;
        }

        type = null!;
        return false;
    }

    private GenericValueArgument? FindReferencedValueArgument(Type type, int referencedParameterIndex)
    {
        type = _substitution.Apply(type);
        return type switch
        {
            TyCon constructor => constructor.ValueArgs.FirstOrDefault(argument =>
                                     argument.ReferencedParameterIndex == referencedParameterIndex) ??
                                 FindReferencedValueArgument(constructor.Args, referencedParameterIndex),
            TyFun function => FindReferencedValueArgument(function.Params, referencedParameterIndex) ??
                              FindReferencedValueArgument(function.Result, referencedParameterIndex),
            TyTuple tuple => FindReferencedValueArgument(tuple.Elements, referencedParameterIndex),
            TyRef reference => FindReferencedValueArgument(reference.Inner, referencedParameterIndex),
            TyMutRef reference => FindReferencedValueArgument(reference.Inner, referencedParameterIndex),
            TyShared shared => FindReferencedValueArgument(shared.Inner, referencedParameterIndex),
            TyReflProof { WitnessType: { } witness } => FindReferencedValueArgument(witness, referencedParameterIndex),
            EffectTag effect => FindReferencedValueArgument(effect.TypeArgs, referencedParameterIndex),
            EffectRow effects => FindReferencedValueArgument(effects.Effects.Cast<Type>(), referencedParameterIndex),
            _ => null
        };
    }

    private GenericValueArgument? FindReferencedValueArgument(
        IEnumerable<Type> types,
        int referencedParameterIndex)
    {
        foreach (var type in types)
        {
            if (FindReferencedValueArgument(type, referencedParameterIndex) is { } argument)
            {
                return argument;
            }
        }

        return null;
    }

    private bool HasValueGenericParameters(SymbolId symbolId) =>
        _symbolTable.GetSymbol<FuncSymbol>(symbolId)?.TypeParams.Any(parameterId =>
            _symbolTable.GetSymbol<TypeParamSymbol>(parameterId)?.ParameterKind == GenericParameterKind.Value) == true;

    private void ValidateResolvedValueGenericArguments(EidosAstNode? application, SourceSpan usageSpan)
    {
        if (application == null ||
            !_valueGenericArgumentsByApplication.TryGetValue(application, out var arguments))
        {
            return;
        }

        foreach (var argument in arguments)
        {
            var resolved = _substitution.Apply(argument);
            if (!resolved.IsConcrete && resolved.ReferencedParameterIndex < 0)
            {
                AddError(
                    usageSpan,
                    $"Cannot infer compile-time value argument '{argument.DisplayText}'; provide an explicit value argument or constrain it through parameter types.");
            }
        }
    }

    private Type InstantiateSchemeWithExplicitTypeArgsAndConstraints(
        TypeScheme scheme,
        IReadOnlyList<TypeNode> explicitTypeArgNodes,
        SourceSpan usageSpan)
    {
        var explicitTypeArgs = explicitTypeArgNodes
            .Select(ConvertTypeInCurrentTypeParamContext)
            .ToList();

        return InstantiateSchemeWithExplicitTypeArgsAndConstraints(scheme, explicitTypeArgs, usageSpan);
    }

    private Type InstantiateSchemeWithExplicitTypeArgsAndConstraints(
        TypeScheme scheme,
        IReadOnlyList<Type> explicitTypeArgs,
        SourceSpan usageSpan)
    {
        InstantiatedTypeScheme instantiated;
        try
        {
            instantiated = _substitution.InstantiateSchemeWithTypeArgs(scheme, explicitTypeArgs);
        }
        catch (TypeInferenceException ex)
        {
            AddError(usageSpan, ex.Message);
            instantiated = _substitution.InstantiateScheme(scheme);
        }

        if (instantiated.Constraints.Count > 0)
        {
            _constraintGenerator.Constraints.AddRange(
                instantiated.Constraints.Select(constraint => WithConstraintSpan(constraint, usageSpan)));
        }

        return instantiated.Type;
    }

    private static TypeConstraint WithConstraintSpan(TypeConstraint constraint, SourceSpan span)
    {
        return constraint switch
        {
            TraitConstraint traitConstraint => traitConstraint with { Span = span },
            EqualityConstraint equalityConstraint => equalityConstraint with { Span = span },
            KindConstraint kindConstraint => kindConstraint with { Span = span },
            _ => constraint
        };
    }

    private void ApplyAdtTypeParamConstraints(
        SymbolId adtSymbolId,
        IReadOnlyDictionary<string, Type> typeVarEnv,
        SourceSpan usageSpan)
    {
        if (!_adtTypeParamConstraintBindings.TryGetValue(adtSymbolId, out var adtBinding))
        {
            return;
        }

        ApplyTypeParamConstraintBinding(
            new TypeParamConstraintBinding(
                adtBinding.AdtId,
                adtBinding.TypeParamNames,
                adtBinding.TraitRequirementsByIndex),
            CreateTypeParamKindMapForOwner(adtSymbolId, adtBinding.TypeParamNames),
            typeVarEnv,
            usageSpan);
    }

    private void ApplyConstructorTypeParamConstraints(
        CtorTypeBinding binding,
        IReadOnlyDictionary<string, Type> typeVarEnv,
        SourceSpan usageSpan)
    {
        if (!_ctorTypeParamConstraintBindings.TryGetValue(binding.CtorId, out var constraintBinding))
        {
            return;
        }

        ApplyTypeParamConstraintBinding(
            constraintBinding,
            CreateTypeParamKindMapForCtorBinding(
                binding.AdtId,
                binding.AdtTypeParamNames,
                binding.CtorId,
                binding.CtorTypeParamNames),
            typeVarEnv,
            usageSpan);
    }

    private void ApplyTypeParamConstraintBinding(
        TypeParamConstraintBinding binding,
        IReadOnlyDictionary<string, Kind> kindEnvByName,
        IReadOnlyDictionary<string, Type> typeVarEnv,
        SourceSpan usageSpan)
    {
        var matchCount = Math.Min(binding.TypeParamNames.Count, binding.TraitRequirementsByIndex.Count);
        var mutableTypeVarEnv = typeVarEnv as Dictionary<string, Type> ?? typeVarEnv.ToDictionary(pair => pair.Key, pair => pair.Value);
        for (var i = 0; i < matchCount; i++)
        {
            var typeParamName = binding.TypeParamNames[i];
            if (!typeVarEnv.TryGetValue(typeParamName, out var typeArg))
            {
                continue;
            }

            foreach (var requirement in binding.TraitRequirementsByIndex[i])
            {
                var traitArgs = requirement.TraitArgNodes
                    .Select(typeNode => ConvertTypeWithAdditionalKindContext(
                        typeNode,
                        mutableTypeVarEnv,
                        kindEnvByName,
                        allowTypeConstructorReference: true))
                    .ToList();
                _constraintGenerator.Constraints.AddTrait(
                    typeArg,
                    requirement.TraitId,
                    requirement.TraitName,
                    usageSpan,
                    traitArgs);
            }
        }
    }

    private void UnifyCtorArgumentTypes(
        CtorTypeBinding binding,
        Dictionary<string, Type> typeVarEnv,
        IReadOnlyList<Type> positionalArgTypes,
        IReadOnlyDictionary<string, Type> namedArgTypes)
    {
        var kindEnvByName = CreateTypeParamKindMapForCtorBinding(
            binding.AdtId,
            binding.AdtTypeParamNames,
            binding.CtorId,
            binding.CtorTypeParamNames);
        var positionalCount = Math.Min(positionalArgTypes.Count, binding.PositionalArgTypes.Count);
        for (var i = 0; i < positionalCount; i++)
        {
            var expectedType = ConvertTypeWithAdditionalKindContext(
                binding.PositionalArgTypes[i],
                typeVarEnv,
                kindEnvByName);
            UnifyExpectedType(
                expectedType,
                NormalizeInferredConstructorArgument(expectedType, positionalArgTypes[i]));
        }

        foreach (var (fieldName, actualType) in namedArgTypes)
        {
            if (!binding.NamedArgTypes.TryGetValue(fieldName, out var fieldType))
            {
                continue;
            }

            var expectedFieldType = ConvertTypeWithAdditionalKindContext(
                fieldType,
                typeVarEnv,
                kindEnvByName);
            UnifyExpectedType(
                expectedFieldType,
                NormalizeInferredConstructorArgument(expectedFieldType, actualType));
        }
    }

    private Type NormalizeInferredConstructorArgument(Type expected, Type actual)
    {
        if (_substitution.Apply(expected) is TyVar &&
            _substitution.Apply(actual) is TyCon actualConstructor &&
            TryPromoteClosedCaseToRoot(actualConstructor, out var promotedActual))
        {
            return promotedActual;
        }

        return actual;
    }
}
