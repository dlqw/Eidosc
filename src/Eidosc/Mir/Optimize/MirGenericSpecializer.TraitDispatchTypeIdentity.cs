using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private ImplTypeShapeNode BuildImplementingTypeShape(TypeId typeId)
    {
        if (!typeId.IsValid)
        {
            return ImplWildcardShapeNode.Instance;
        }

        var builtinName = ImplLookupCanonicalizer.ResolveBuiltinCanonicalTypeName(typeId);
        if (!string.IsNullOrWhiteSpace(builtinName))
        {
            return new ImplConstructorShapeNode(builtinName, [])
            {
                TypeId = typeId
            };
        }

        if (!TryGetTypeDescriptor(typeId, out var descriptor))
        {
            return BuildNamedTypeFallbackShape(typeId);
        }

        return descriptor switch
        {
            TypeDescriptor.Builtin builtin => new ImplConstructorShapeNode(
                ImplLookupCanonicalizer.ResolveBuiltinCanonicalTypeName(new TypeId(builtin.TypeIdValue)),
                [])
            {
                TypeId = new TypeId(builtin.TypeIdValue)
            },
            TypeDescriptor.TypeVar typeVar => new ImplVariableShapeNode($"t{typeVar.Index}"),
            TypeDescriptor.Tuple tuple => new ImplTupleShapeNode(tuple.FieldTypes.Select(BuildImplementingTypeShape).ToList()),
            TypeDescriptor.Function function => BuildFunctionTypeShape(function),
            TypeDescriptor.TyCon tyCon => new ImplConstructorShapeNode(
                tyCon.ConstructorDescriptor,
                tyCon.TypeArgs.Select(BuildImplementingTypeShape).ToList())
            {
                SymbolId = TryResolveConstructorSymbolId(tyCon.Constructor, out var constructorSymbolId)
                    ? constructorSymbolId
                    : SymbolId.None,
                TypeId = TryResolveConstructorTypeId(tyCon.Constructor, out var constructorTypeId)
                    ? constructorTypeId
                    : TypeId.None
            },
            TypeDescriptor.Ref reference => new ImplConstructorShapeNode("Ref", [BuildImplementingTypeShape(reference.Inner)]),
            TypeDescriptor.MutRef reference => new ImplConstructorShapeNode("MRef", [BuildImplementingTypeShape(reference.Inner)]),
            _ => BuildNamedTypeFallbackShape(typeId)
        };
    }

    private ImplTypeShapeNode BuildNamedTypeFallbackShape(TypeId typeId)
    {
        if (TryResolveTypeConstructorSymbol(typeId, out var symbolId, out var declaredTypeId))
        {
            return new ImplConstructorShapeNode($"sym:{symbolId.Value}", [])
            {
                SymbolId = symbolId,
                TypeId = declaredTypeId
            };
        }

        return new ImplConstructorShapeNode($"type:{typeId.Value}", [])
        {
            TypeId = typeId
        };
    }

    private ImplTypeShapeNode BuildFunctionTypeShape(TypeDescriptor.Function function)
    {
        var resultShape = BuildImplementingTypeShape(function.ReturnType);
        for (var i = function.ParamTypes.Length - 1; i >= 0; i--)
        {
            resultShape = new ImplArrowShapeNode(BuildImplementingTypeShape(function.ParamTypes[i]), resultShape);
        }

        return resultShape;
    }

    private TypeId ResolveImplLookupTypeId(TypeId receiverTypeId)
    {
        if (!receiverTypeId.IsValid)
        {
            return TypeId.None;
        }

        if (BaseTypes.IsBuiltIn(receiverTypeId))
        {
            return receiverTypeId;
        }

        if (TryGetTypeDescriptor(receiverTypeId, out var descriptor) &&
            descriptor is TypeDescriptor.TyCon tyCon)
        {
            if (TryResolveConstructorTypeId(tyCon.Constructor, out var typeId))
            {
                return typeId;
            }
        }

        return receiverTypeId;
    }

    private bool TryResolveTypeConstructorSymbol(
        TypeId typeId,
        out SymbolId symbolId,
        out TypeId declaredTypeId)
    {
        symbolId = SymbolId.None;
        declaredTypeId = TypeId.None;

        if (_typeConstructorInfoByTypeId.TryGetValue(typeId.Value, out var typeConstructor) &&
            TryUseTypeConstructorInfo(typeConstructor, out symbolId, out declaredTypeId))
        {
            return true;
        }

        return false;
    }

    private static bool TryUseTypeConstructorInfo(
        MirTypeConstructorInfo typeConstructor,
        out SymbolId symbolId,
        out TypeId declaredTypeId)
    {
        symbolId = SymbolId.None;
        declaredTypeId = TypeId.None;

        if (!typeConstructor.SymbolId.IsValid ||
            !typeConstructor.TypeId.IsValid)
        {
            return false;
        }

        symbolId = typeConstructor.SymbolId;
        declaredTypeId = typeConstructor.TypeId;
        return true;
    }

    private bool TryResolveConstructorTypeId(TypeConstructorKey constructor, out TypeId typeId)
    {
        typeId = TypeId.None;
        if (constructor.Kind is TypeConstructorKeyKind.TypeId or TypeConstructorKeyKind.Builtin)
        {
            typeId = new TypeId(constructor.Id);
            return typeId.IsValid;
        }

        if (constructor.Kind == TypeConstructorKeyKind.Symbol &&
            TryResolveSymbolTypeConstructorId(new SymbolId(constructor.Id), out var symbolTypeId))
        {
            typeId = symbolTypeId;
            return true;
        }

        return false;
    }

    private static bool TryResolveConstructorSymbolId(TypeConstructorKey constructor, out SymbolId symbolId)
    {
        symbolId = SymbolId.None;
        if (constructor.Kind != TypeConstructorKeyKind.Symbol)
        {
            return false;
        }

        symbolId = new SymbolId(constructor.Id);
        return symbolId.IsValid;
    }

    private bool TryResolveSymbolTypeConstructorId(SymbolId symbolId, out TypeId typeId)
    {
        typeId = TypeId.None;
        if (!symbolId.IsValid)
        {
            return false;
        }

        var symbolTypeId = new TypeId(symbolId.Value);
        if (TryGetTypeDescriptor(symbolTypeId, out _))
        {
            typeId = symbolTypeId;
            return true;
        }

        if (TryResolveModuleAliasTypeId(symbolId, out typeId))
        {
            return true;
        }

        if (_typeConstructorInfoBySymbol.TryGetValue(symbolId, out var typeConstructor) &&
            typeConstructor.TypeId.IsValid)
        {
            typeId = typeConstructor.TypeId;
            return true;
        }

        if (_typeConstructorInfoByTypeId.TryGetValue(symbolId.Value, out typeConstructor) &&
            typeConstructor.TypeId.IsValid)
        {
            typeId = typeConstructor.TypeId;
            return true;
        }

        return false;
    }

    private bool TryResolveModuleAliasTypeId(SymbolId aliasId, out TypeId typeId)
    {
        foreach (var aliasInfo in _moduleTypeAliases)
        {
            if (aliasInfo.AliasId == aliasId && aliasInfo.TypeId.IsValid)
            {
                typeId = aliasInfo.TypeId;
                return true;
            }
        }

        typeId = TypeId.None;
        return false;
    }

    private IEnumerable<ImplTypeShapeNode> EnumerateImplementingTypeCandidateShapes(SymbolId ownerTrait, TypeId receiverTypeId)
    {
        var seen = new HashSet<ImplTypeShapeNode>();

        void AddCandidate(ImplTypeShapeNode candidate, List<ImplTypeShapeNode> sink)
        {
            if (seen.Add(candidate))
            {
                sink.Add(candidate);
            }
        }

        var candidates = new List<ImplTypeShapeNode>();
        AddCandidate(BuildImplementingTypeShape(receiverTypeId), candidates);

        foreach (var aliasCandidate in EnumerateAliasImplementingTypeShapes(receiverTypeId))
        {
            AddCandidate(aliasCandidate, candidates);
        }

        foreach (var candidate in candidates)
        {
            yield return candidate;

            if (SupportsHigherKindedDispatchProjection(ownerTrait) &&
                TryProjectHigherKindedImplementingType(candidate, out var projectedCandidate) &&
                seen.Add(projectedCandidate))
            {
                yield return projectedCandidate;
            }
        }

        if (SupportsHigherKindedDispatchProjection(ownerTrait) &&
            TryBuildClosedCaseRootConstructorShape(receiverTypeId, out var rootConstructorShape) &&
            seen.Add(rootConstructorShape))
        {
            yield return rootConstructorShape;
        }
    }

    private bool TryBuildClosedCaseRootConstructorShape(
        TypeId receiverTypeId,
        out ImplTypeShapeNode rootConstructorShape)
    {
        rootConstructorShape = ImplWildcardShapeNode.Instance;
        if (_symbolTable == null ||
            !TryGetTypeDescriptor(receiverTypeId, out var descriptor) ||
            descriptor is not TypeDescriptor.TyCon tyCon ||
            !TryResolveConstructorSymbolId(tyCon.Constructor, out var caseId) ||
            _symbolTable.GetSymbol<AdtSymbol>(caseId) is not { IsCaseType: true })
        {
            return false;
        }

        var rootId = _symbolTable.GetClosedCaseRoot(caseId);
        if (rootId == caseId ||
            _symbolTable.GetSymbol<AdtSymbol>(rootId) is not { } rootSymbol)
        {
            return false;
        }

        var rootTypeParameterCount = _symbolTable
            .GetClosedCaseEffectiveGenericParameterIds(rootId)
            .Count(parameterId =>
                _symbolTable.GetSymbol<TypeParamSymbol>(parameterId)?.ParameterKind == GenericParameterKind.Type);
        if (tyCon.TypeArgs.Length < rootTypeParameterCount)
        {
            return false;
        }

        var rootShape = new ImplConstructorShapeNode(
            TypeConstructorKey.FromSymbol(rootId).ToDescriptorString(),
            tyCon.TypeArgs
                .Take(rootTypeParameterCount)
                .Select(BuildImplementingTypeShape)
                .ToList())
        {
            SymbolId = rootId,
            TypeId = rootSymbol.TypeId
        };

        return TryProjectHigherKindedImplementingType(rootShape, out rootConstructorShape);
    }

}
