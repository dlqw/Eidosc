using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private List<ImplSymbol> ResolveApplicableImplsForReceiverType(SymbolId ownerTrait, TypeId receiverTypeId)
    {
        var lookupTypeId = ResolveImplLookupTypeId(receiverTypeId);
        var requestedCanonicalShapes = EnumerateImplementingTypeCandidateShapes(ownerTrait, receiverTypeId).ToList();

        return !lookupTypeId.IsValid || requestedCanonicalShapes.Count == 0
            ? []
            : ResolveApplicableImplsForReceiverCandidate(
                ownerTrait,
                receiverTypeId,
                lookupTypeId,
                requestedCanonicalShapes);
    }

    private List<ImplSymbol> ResolveApplicableImplsForReceiverCandidate(
        SymbolId ownerTrait,
        TypeId receiverTypeId,
        TypeId lookupTypeId,
        IReadOnlyList<ImplTypeShapeNode> requestedCanonicalShapes)
    {
        var applicable = new Dictionary<SymbolId, ImplSymbol>();
        foreach (var impl in EnumerateTraitImpls(ownerTrait))
        {
            if (impl.Trait != ownerTrait)
            {
                continue;
            }

            if (!HasStructuredImplementingHead(impl) &&
                (impl.ImplementingType == receiverTypeId || impl.ImplementingType == lookupTypeId))
            {
                applicable[impl.Id] = impl;
                continue;
            }

            var candidateShapes = GetImplImplementingShapes(impl);

            if (requestedCanonicalShapes.Any(requestedShape =>
                    candidateShapes.Any(implShape =>
                        ImplSpecializationComparer.IsApplicableTo(requestedShape, implShape))))
            {
                applicable[impl.Id] = impl;
                continue;
            }

            if (!SupportsHigherKindedDispatchProjection(ownerTrait))
            {
                continue;
            }

            var traitArgumentShapes = GetImplTraitArgumentShapes(impl);
            if (traitArgumentShapes.Count == 0)
            {
                continue;
            }

            if (requestedCanonicalShapes.Any(requestedShape =>
                    traitArgumentShapes.Any(traitArgShape =>
                        ImplSpecializationComparer.IsApplicableTo(requestedShape, traitArgShape))))
            {
                applicable[impl.Id] = impl;
                continue;
            }

            if (TryResolveHigherKindedReceiverConstructorShape(receiverTypeId, out var receiverConstructorShape) &&
                ImplMentionsHigherKindedConstructor(impl, receiverConstructorShape))
            {
                applicable[impl.Id] = impl;
            }
        }

        if (TryResolveHigherKindedReceiverConstructorShape(receiverTypeId, out var finalReceiverConstructorShape))
        {
            var constructorSpecific = applicable.Values
                .Where(impl => ImplMentionsHigherKindedConstructor(impl, finalReceiverConstructorShape))
                .ToList();
            if (constructorSpecific.Count > 0)
            {
                return constructorSpecific;
            }
        }

        return [.. applicable.Values];
    }

    /// <summary>
    /// Falls back to finding impls for child traits that extend the requested parent trait
    /// via supertrait chains. E.g., if requesting Eq and no @impl(Eq) exists, finds @impl(Ord)
    /// where Ord: Eq.
    /// </summary>
    private List<ImplSymbol> ResolveApplicableImplsViaSupertraitChain(
        SymbolId parentTraitId,
        TypeId receiverTypeId)
    {
        var result = new List<ImplSymbol>();

        // Iterate all known impls and check if their trait is a child of parentTraitId
        foreach (var impl in EnumerateTraitImpls())
        {
            if (impl.Trait == parentTraitId)
            {
                // Already checked by direct lookup — skip
                continue;
            }

            if (!impl.Trait.IsValid)
            {
                continue;
            }

            // Check if impl.Trait has parentTraitId as an ancestor
            if (IsSupertraitOf(impl.Trait, parentTraitId))
            {
                // Verify the impl applies to this receiver type
                var childImpls = ResolveApplicableImplsForReceiverType(impl.Trait, receiverTypeId);
                if (childImpls.Count > 0)
                {
                    result.AddRange(childImpls);
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Checks whether ancestorTraitId is an ancestor (direct or transitive) of childTraitId
    /// through the supertrait chain.
    /// </summary>
    private bool IsSupertraitOf(SymbolId childTraitId, SymbolId ancestorTraitId)
    {
        if (childTraitId == ancestorTraitId)
        {
            return false;
        }

        if (_traitInfoById.TryGetValue(childTraitId, out var info) &&
            info.ParentTraits.Count > 0)
        {
            var visited = new HashSet<SymbolId>();
            var stack = new Stack<SymbolId>(info.ParentTraits);

            while (stack.Count > 0)
            {
                var current = stack.Pop();
                if (current == ancestorTraitId)
                {
                    return true;
                }

                if (!visited.Add(current))
                {
                    continue;
                }

                if (_traitInfoById.TryGetValue(current, out var parentInfo))
                {
                    foreach (var grandparent in parentInfo.ParentTraits)
                    {
                        stack.Push(grandparent);
                    }
                }
            }
        }

        return false;
    }

    private static bool HasStructuredImplementingHead(ImplSymbol impl)
    {
        return impl.ImplementingTypeShape != null || !impl.ImplementingTypeKey.IsEmpty;
    }

    private List<ImplTypeShapeNode> GetImplImplementingShapes(ImplSymbol impl)
    {
        if (impl.ImplementingTypeShape != null)
        {
            return [impl.ImplementingTypeShape];
        }

        if (!impl.ImplementingTypeKey.IsEmpty)
        {
            return [BuildImplTypeShapeNode(impl.ImplementingTypeKey)];
        }

        if (impl.ImplementingType.IsValid)
        {
            return [BuildImplementingTypeShape(impl.ImplementingType)];
        }

        return [];
    }

    private ImplTypeShapeNode GetImplImplementingShape(ImplSymbol impl)
    {
        if (impl.ImplementingTypeShape != null)
        {
            return impl.ImplementingTypeShape;
        }

        if (!impl.ImplementingTypeKey.IsEmpty)
        {
            return BuildImplTypeShapeNode(impl.ImplementingTypeKey);
        }

        if (impl.ImplementingType.IsValid)
        {
            return BuildImplementingTypeShape(impl.ImplementingType);
        }

        return ImplWildcardShapeNode.Instance;
    }

    private ImplHeadShape BuildImplHeadShape(ImplSymbol impl)
    {
        return new ImplHeadShape(
            impl.Trait,
            GetImplTraitArgumentShapes(impl),
            GetImplImplementingShape(impl));
    }

    private List<ImplTypeShapeNode> GetImplTraitArgumentShapes(ImplSymbol impl)
    {
        if (impl.TraitTypeArgShapes.Count > 0)
        {
            return impl.TraitTypeArgShapes
                .Where(IsStructurallyIdentifiedDispatchShape)
                .ToList();
        }

        var traitTypeArgKeys = impl.GetMatchingTraitTypeArgKeys();
        if (traitTypeArgKeys.Count > 0)
        {
            return traitTypeArgKeys
                .Where(IsStructurallyIdentifiedDispatchKey)
                .Select(BuildImplTypeShapeNode)
                .ToList();
        }

        return [];
    }

    private static bool IsStructurallyIdentifiedDispatchKey(ImplTypeRefKey key)
    {
        if (key.IsEmpty || !key.HasStructuredIdentity())
        {
            return false;
        }

        if (key.TypeArguments.IsDefaultOrEmpty)
        {
            return true;
        }

        return key.TypeArguments.All(IsStructurallyIdentifiedDispatchKey);
    }

    private static bool IsStructurallyIdentifiedDispatchShape(ImplTypeShapeNode shape)
    {
        return MentionsStructuredConstructor(shape) &&
               ShapeUsesOnlyStructuredConstructors(shape);
    }

    private static bool ShapeUsesOnlyStructuredConstructors(ImplTypeShapeNode shape)
    {
        return shape switch
        {
            ImplConstructorShapeNode constructor =>
                (constructor.SymbolId.IsValid || constructor.TypeId.IsValid) &&
                constructor.Args.All(ShapeUsesOnlyStructuredConstructors),
            ImplTupleShapeNode tuple => tuple.Elements.All(ShapeUsesOnlyStructuredConstructors),
            ImplArrowShapeNode arrow =>
                ShapeUsesOnlyStructuredConstructors(arrow.ParamType) &&
                ShapeUsesOnlyStructuredConstructors(arrow.ReturnType),
            ImplEffectfulShapeNode effectful =>
                ShapeUsesOnlyStructuredConstructors(effectful.InputType) &&
                (effectful.OutputType == null || ShapeUsesOnlyStructuredConstructors(effectful.OutputType)),
            ImplVariableShapeNode or
            ImplValueVariableShapeNode or
            ImplConcreteValueShapeNode or
            ImplWildcardShapeNode => true,
            _ => false
        };
    }

    private static bool MentionsStructuredConstructor(ImplTypeShapeNode shape)
    {
        return shape switch
        {
            ImplConstructorShapeNode constructor =>
                constructor.SymbolId.IsValid ||
                constructor.TypeId.IsValid ||
                constructor.Args.Any(MentionsStructuredConstructor),
            ImplTupleShapeNode tuple => tuple.Elements.Any(MentionsStructuredConstructor),
            ImplArrowShapeNode arrow =>
                MentionsStructuredConstructor(arrow.ParamType) ||
                MentionsStructuredConstructor(arrow.ReturnType),
            ImplEffectfulShapeNode effectful =>
                MentionsStructuredConstructor(effectful.InputType) ||
                (effectful.OutputType != null && MentionsStructuredConstructor(effectful.OutputType)),
            _ => false
        };
    }

    private ImplTypeShapeNode BuildImplTypeShapeNode(ImplTypeRefKey key)
    {
        if (TryBuildImplTypeVariableShape(key, out var variableShape))
        {
            return variableShape;
        }

        return ImplTypeShapeFactory.BuildFromKey(
            key,
            typeIdResolver: ResolveImplTypeRefKeyTypeId);
    }

    private bool TryBuildImplTypeVariableShape(ImplTypeRefKey key, out ImplTypeShapeNode shape)
    {
        shape = ImplWildcardShapeNode.Instance;
        if (!key.TypeArguments.IsDefaultOrEmpty)
        {
            return false;
        }

        if (TryResolveImplTypeRefKeyTypeDescriptor(key, out var descriptor) &&
            descriptor is TypeDescriptor.TypeVar typeVariable)
        {
            shape = new ImplVariableShapeNode($"t{typeVariable.Index}");
            return true;
        }

        if (key.TypeId.IsValid && IsMirGenericTypeParameter(key.TypeId))
        {
            shape = new ImplVariableShapeNode($"t{key.TypeId.Value}");
            return true;
        }

        if (key.SymbolId.IsValid && IsMirGenericTypeParameter(new TypeId(key.SymbolId.Value)))
        {
            shape = new ImplVariableShapeNode($"t{key.SymbolId.Value}");
            return true;
        }

        return false;
    }

    private bool TryResolveImplTypeRefKeyTypeDescriptor(ImplTypeRefKey key, out TypeDescriptor descriptor)
    {
        if (key.TypeId.IsValid &&
            TryGetTypeDescriptor(key.TypeId, out descriptor!))
        {
            return true;
        }

        if (key.SymbolId.IsValid &&
            TryGetTypeDescriptor(new TypeId(key.SymbolId.Value), out descriptor!))
        {
            return true;
        }

        descriptor = null!;
        return false;
    }

    private TypeId ResolveImplTypeRefKeyTypeId(ImplTypeRefKey key)
    {
        if (key.TypeId.IsValid)
        {
            return key.TypeId;
        }

        if (key.SymbolId.IsValid &&
            TryGetTypeDescriptor(new TypeId(key.SymbolId.Value), out _))
        {
            return new TypeId(key.SymbolId.Value);
        }

        if (key.SymbolId.IsValid &&
            TryResolveModuleAliasTypeId(key.SymbolId, out var aliasTypeId))
        {
            return aliasTypeId;
        }

        if (key.SymbolId.IsValid &&
            _typeConstructorInfoBySymbol.TryGetValue(key.SymbolId, out var typeConstructor) &&
            typeConstructor.TypeId.IsValid)
        {
            return typeConstructor.TypeId;
        }

        return TypeId.None;
    }

    private bool TryResolveHigherKindedReceiverConstructorShape(
        TypeId receiverTypeId,
        out ImplTypeShapeNode receiverConstructorShape)
    {
        receiverConstructorShape = ImplWildcardShapeNode.Instance;
        var implementingShape = BuildImplementingTypeShape(receiverTypeId);
        if (!TryProjectHigherKindedImplementingType(implementingShape, out var projectedShape) ||
            projectedShape is not ImplConstructorShapeNode)
        {
            return false;
        }

        receiverConstructorShape = projectedShape;
        return true;
    }

    private bool ImplMentionsHigherKindedConstructor(
        ImplSymbol impl,
        ImplTypeShapeNode receiverConstructorShape)
    {
        return GetImplTraitArgumentShapes(impl)
                   .Any(shape => ShapeMentionsConstructor(shape, receiverConstructorShape)) ||
               GetImplImplementingShapes(impl)
                   .Any(shape => ShapeMentionsConstructor(shape, receiverConstructorShape));
    }

    private static bool ShapeMentionsConstructor(
        ImplTypeShapeNode shape,
        ImplTypeShapeNode receiverConstructorShape)
    {
        switch (shape)
        {
            case ImplConstructorShapeNode constructor:
                var constructorHead = new ImplConstructorShapeNode(constructor.Name, [])
                {
                    SymbolId = constructor.SymbolId,
                    TypeId = constructor.TypeId
                };
                return ImplSpecializationComparer.IsApplicableTo(receiverConstructorShape, constructorHead) ||
                        constructor.Args.Any(child => ShapeMentionsConstructor(child, receiverConstructorShape));
            case ImplTupleShapeNode tuple:
                return tuple.Elements.Any(child => ShapeMentionsConstructor(child, receiverConstructorShape));
            case ImplArrowShapeNode arrow:
                return ShapeMentionsConstructor(arrow.ParamType, receiverConstructorShape) ||
                       ShapeMentionsConstructor(arrow.ReturnType, receiverConstructorShape);
            case ImplEffectfulShapeNode effectful:
                return ShapeMentionsConstructor(effectful.InputType, receiverConstructorShape) ||
                       (effectful.OutputType != null &&
                        ShapeMentionsConstructor(effectful.OutputType, receiverConstructorShape));
            default:
                return false;
        }
    }

    private ImplSymbol? TryChooseMostSpecificImpl(IReadOnlyList<ImplSymbol> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var maximal = new List<ImplSymbol>();
        foreach (var candidate in candidates)
        {
            var candidateShape = BuildImplHeadShape(candidate);
            var isDominated = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (ReferenceEquals(candidates[i], candidate) || candidates[i].Id == candidate.Id)
                {
                    continue;
                }

                var otherShape = BuildImplHeadShape(candidates[i]);
                if (ImplSpecializationComparer.CompareHeadsForSelection(otherShape, candidateShape) ==
                    ImplSpecializationRelation.MoreSpecific)
                {
                    isDominated = true;
                    break;
                }
            }

            if (!isDominated)
            {
                maximal.Add(candidate);
            }
        }

        return maximal.Count == 1 ? maximal[0] : null;
    }

    private readonly record struct AliasInstantiationArgument(TypeId TypeId, SymbolId OpenTypeParameterId)
    {
        public static AliasInstantiationArgument Concrete(TypeId typeId) => new(typeId, SymbolId.None);

        public static AliasInstantiationArgument OpenTypeParameter(SymbolId typeParameterId) =>
            new(TypeId.None, typeParameterId);
    }

    private IEnumerable<ImplTypeShapeNode> EnumerateAliasImplementingTypeShapes(TypeId receiverTypeId)
    {
        if (!receiverTypeId.IsValid)
        {
            yield break;
        }

        foreach (var aliasInfo in EnumerateTypeAliasInfos())
        {
            if (!TryResolveAliasInstantiation(receiverTypeId, aliasInfo, out var aliasArguments))
            {
                continue;
            }

            yield return BuildAliasInstantiationShape(aliasInfo, aliasArguments);
        }
    }

    private IEnumerable<MirTypeAliasInfo> EnumerateTypeAliasInfos()
    {
        return _moduleTypeAliases;
    }

    private bool TryResolveAliasInstantiation(
        TypeId concreteTypeId,
        MirTypeAliasInfo aliasInfo,
        out List<AliasInstantiationArgument> aliasArguments)
    {
        aliasArguments = [];
        if (!aliasInfo.AliasTarget.IsValid)
        {
            return false;
        }

        var bindings = new SpecializationBindings(
            new Dictionary<int, TypeId>(),
            new Dictionary<int, ConstructorBinding>());
        if (!TryCollectTypeBindings(aliasInfo.AliasTarget, concreteTypeId, bindings))
        {
            return false;
        }

        foreach (var typeParamId in aliasInfo.TypeParameterIds)
        {
            if (bindings.TypeBindings.TryGetValue(typeParamId.Value, out var boundTypeId))
            {
                aliasArguments.Add(AliasInstantiationArgument.Concrete(boundTypeId));
                continue;
            }

            aliasArguments.Add(AliasInstantiationArgument.OpenTypeParameter(typeParamId));
        }

        return true;
    }

    private ImplTypeShapeNode BuildAliasInstantiationShape(
        MirTypeAliasInfo aliasInfo,
        IReadOnlyList<AliasInstantiationArgument> aliasArguments)
    {
        var argumentShapes = aliasArguments
            .Select(BuildAliasArgumentShape)
            .ToList();
        return new ImplConstructorShapeNode(aliasInfo.Name, argumentShapes)
        {
            SymbolId = aliasInfo.AliasId,
            TypeId = aliasInfo.TypeId
        };
    }

    private ImplTypeShapeNode BuildAliasArgumentShape(AliasInstantiationArgument argument)
    {
        if (argument.OpenTypeParameterId.IsValid &&
            TryGetTypeDescriptor(new TypeId(argument.OpenTypeParameterId.Value), out var descriptor) &&
            descriptor is TypeDescriptor.TypeVar typeVariable)
        {
            return new ImplVariableShapeNode($"t{typeVariable.Index}");
        }

        if (argument.OpenTypeParameterId.IsValid &&
            IsMirGenericTypeParameter(new TypeId(argument.OpenTypeParameterId.Value)))
        {
            return new ImplVariableShapeNode($"t{argument.OpenTypeParameterId.Value}");
        }

        if (argument.OpenTypeParameterId.IsValid)
        {
            return new ImplVariableShapeNode($"t{argument.OpenTypeParameterId.Value}");
        }

        return BuildImplementingTypeShape(argument.TypeId);
    }

    private bool SupportsHigherKindedDispatchProjection(SymbolId ownerTrait)
    {
        if (_traitInfoById.TryGetValue(ownerTrait, out var traitInfo))
        {
            return traitInfo.TypeParameterCount > 0 &&
                   (traitInfo.SelfPosition != SelfPosition.Unknown ||
                    traitInfo.HasMethodDispatchMetadata);
        }

        return false;
    }

    private static bool TryProjectHigherKindedImplementingType(
        ImplTypeShapeNode shape,
        out ImplTypeShapeNode projectedShape)
    {
        projectedShape = ImplWildcardShapeNode.Instance;
        if (shape is not ImplConstructorShapeNode constructor ||
            constructor.Args.Count == 0)
        {
            return false;
        }

        projectedShape = new ImplConstructorShapeNode(
            constructor.Name,
            constructor.Args.Take(constructor.Args.Count - 1).ToList())
        {
            SymbolId = constructor.SymbolId,
            TypeId = constructor.TypeId
        };
        return true;
    }

    /// <summary>
    /// Attempts to resolve a trait dispatch target via default implementation.
    /// If the trait method has a default body (HasDefaultImplementation), we dispatch
    /// directly to the trait method's own FuncSymbol — the monomorphizer will handle
    /// Self substitution when specializing.
    /// </summary>
    private bool TryResolveDefaultImplMethod(
        SymbolId ownerTrait,
        SymbolId traitMethodId,
        out SymbolId implMethodId,
        out string implMethodName)
    {
        implMethodId = SymbolId.None;
        implMethodName = string.Empty;

        if (!traitMethodId.IsValid)
        {
            return false;
        }

        // Look up the trait's method info to check for default implementation
        if (!_traitInfoById.TryGetValue(ownerTrait, out var traitInfo))
        {
            return false;
        }

        foreach (var methodInfo in traitInfo.Methods)
        {
            if (methodInfo.MethodId == traitMethodId && methodInfo.HasDefaultImplementation)
            {
                implMethodId = traitMethodId;
                implMethodName = ResolveLoweredFunctionName(traitMethodId, methodInfo.Name);
                return true;
            }
        }

        return false;
    }
}
