using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    /// <summary>
    /// Simple type binding collection for inference: matches a declared (possibly polymorphic)
    /// type against a concrete type, binding any open type variables in the declared type.
    /// Unlike the specialization cloning version, this does NOT handle constructor variables.
    /// </summary>
    private bool TryCollectTypeBindingsForInference(TypeId declaredType, TypeId concreteType, SpecializationBindings bindings)
    {
        if (!declaredType.IsValid || !concreteType.IsValid || declaredType.Equals(concreteType))
        {
            return true;
        }

        if (!TryGetTypeDescriptor(declaredType, out var declaredDescriptor))
        {
            if (BaseTypes.IsBuiltIn(declaredType))
            {
                return declaredType.Equals(concreteType);
            }

            if (!BaseTypes.IsBuiltIn(declaredType) &&
                IsOpenInferenceTypeVariable(declaredType))
            {
                return TryBindTypeVariable(declaredType, concreteType, bindings);
            }

            return true;
        }

        // Open type variable → bind to concrete
        if (declaredDescriptor is TypeDescriptor.TypeVar)
        {
            return TryBindTypeVariable(declaredType, concreteType, bindings);
        }

        if (!TryGetTypeDescriptor(concreteType, out var concreteDescriptor))
        {
            return false;
        }

        // Function type: match parameters and result.
        if (declaredDescriptor is TypeDescriptor.Function declaredFunction &&
            concreteDescriptor is TypeDescriptor.Function concreteFunction)
        {
            if (TryCollectFlattenedFunctionTypeBindingsForInference(declaredFunction, concreteFunction, bindings))
            {
                return true;
            }

            if (declaredFunction.ParamTypes.Length == concreteFunction.ParamTypes.Length)
            {
                for (var i = 0; i < declaredFunction.ParamTypes.Length; i++)
                {
                    if (!TryCollectTypeBindingsForInference(declaredFunction.ParamTypes[i], concreteFunction.ParamTypes[i], bindings))
                    {
                        return false;
                    }
                }
                return TryCollectTypeBindingsForInference(declaredFunction.ReturnType, concreteFunction.ReturnType, bindings);
            }

            if (concreteFunction.ParamTypes.Length == declaredFunction.ParamTypes.Length + 1 &&
                TryResolveFlattenedFunctionType(concreteFunction.ParamTypes[^1], out var continuationParams, out _) &&
                continuationParams.Count > 0)
            {
                for (var i = 0; i < declaredFunction.ParamTypes.Length; i++)
                {
                    if (!TryCollectTypeBindingsForInference(declaredFunction.ParamTypes[i], concreteFunction.ParamTypes[i], bindings))
                    {
                        return false;
                    }
                }

                return TryCollectTypeBindingsForInference(declaredFunction.ReturnType, continuationParams[0], bindings);
            }
        }

        // Tuple: match elements
        if (declaredDescriptor is TypeDescriptor.Tuple declaredTuple &&
            concreteDescriptor is TypeDescriptor.Tuple concreteTuple &&
            declaredTuple.FieldTypes.Length == concreteTuple.FieldTypes.Length)
        {
            for (var i = 0; i < declaredTuple.FieldTypes.Length; i++)
            {
                if (!TryCollectTypeBindingsForInference(declaredTuple.FieldTypes[i], concreteTuple.FieldTypes[i], bindings))
                {
                    return false;
                }
            }
            return true;
        }

        // TyCon with constructor variable (e.g., T[G[A]] where T is var:N)
        // Bind the constructor variable to the concrete type constructor
        if (declaredDescriptor is TypeDescriptor.TyCon declaredTyCon &&
            TryParseConstructorVarIndex(declaredTyCon.Constructor, out var ctorVarIndex))
        {
            if (concreteDescriptor is TypeDescriptor.TyCon concreteTyCon)
            {
                if (!BindConstructorVariable(
                        ctorVarIndex,
                        declaredTyCon.TypeArgs,
                        concreteTyCon.Constructor,
                        concreteTyCon.TypeArgs,
                        bindings))
                {
                    return false;
                }

                if (declaredTyCon.TypeArgs.Length == concreteTyCon.TypeArgs.Length)
                {
                    for (var i = 0; i < declaredTyCon.TypeArgs.Length; i++)
                    {
                        if (!TryCollectTypeBindingsForInference(declaredTyCon.TypeArgs[i], concreteTyCon.TypeArgs[i], bindings))
                        {
                            return false;
                        }
                    }
                }
            }

            return true;
        }

        // TyCon: match type args (skip constructor vars — they're handled above)
        if (declaredDescriptor is TypeDescriptor.TyCon declaredConcreteTyCon &&
            concreteDescriptor is TypeDescriptor.TyCon concreteConcreteTyCon &&
            !TryParseConstructorVarIndex(declaredConcreteTyCon.Constructor, out _) &&
            AreConstructorKeysEquivalent(
                declaredConcreteTyCon.Constructor,
                concreteConcreteTyCon.Constructor) &&
            declaredConcreteTyCon.TypeArgs.Length == concreteConcreteTyCon.TypeArgs.Length)
        {
            for (var i = 0; i < declaredConcreteTyCon.TypeArgs.Length; i++)
            {
                if (!TryCollectTypeBindingsForInference(declaredConcreteTyCon.TypeArgs[i], concreteConcreteTyCon.TypeArgs[i], bindings))
                {
                    return false;
                }
            }
            return true;
        }

        return false;
    }

    private bool TryCollectFlattenedFunctionTypeBindingsForInference(
        TypeDescriptor.Function declaredFunction,
        TypeDescriptor.Function concreteFunction,
        SpecializationBindings bindings)
    {
        if (!TryResolveFlattenedFunctionDescriptor(declaredFunction, out var declaredParameters, out var declaredReturn) ||
            !TryResolveFlattenedFunctionDescriptor(concreteFunction, out var concreteParameters, out var concreteReturn) ||
            declaredParameters.Count == declaredFunction.ParamTypes.Length && concreteParameters.Count == concreteFunction.ParamTypes.Length ||
            declaredParameters.Count != concreteParameters.Count)
        {
            return false;
        }

        for (var i = 0; i < declaredParameters.Count; i++)
        {
            if (!TryCollectTypeBindingsForInference(declaredParameters[i], concreteParameters[i], bindings))
            {
                return false;
            }
        }

        return TryCollectTypeBindingsForInference(declaredReturn, concreteReturn, bindings);
    }
}
