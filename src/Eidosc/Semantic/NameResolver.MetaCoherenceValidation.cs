using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Eidosc.Types;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private sealed record ProspectiveInstanceHead(
        Declaration Declaration,
        SymbolId TraitId,
        TypePath ImplementingType,
        TypeId ImplementingTypeId,
        ImplHeadShape Shape);

    private bool TryValidateNominalMutationCoherence(MetaInvocationOccurrence invocation, out string reason)
    {
        var diagnosticCount = _diagnostics.Count;
        try
        {
            return TryValidateNominalMutationCoherenceCore(invocation, out reason);
        }
        finally
        {
            DiscardProspectiveCoherenceDiagnostics(diagnosticCount);
        }
    }

    private bool TryValidateNominalMutationCoherenceCore(MetaInvocationOccurrence invocation, out string reason)
    {
        var target = invocation.Target;
        if (target is not (AdtDef or CaseTypeDef or TraitDef))
        {
            reason = string.Empty;
            return true;
        }

        var removedSymbols = CollectOwnedDeclarationSymbolIds(target);
        var removedTypes = removedSymbols
            .Select(_symbolTable.GetSymbol)
            .Where(static symbol => symbol is { TypeId.IsValid: true })
            .Select(static symbol => symbol!.TypeId)
            .ToHashSet();
        foreach (var instance in _instanceDeclarations.Values
                     .Where(instance => !ReferenceEquals(instance, target))
                     .GroupBy(static instance => instance.SymbolId)
                     .Select(static group => group.First()))
        {
            var ownerModuleId = GetDeclarationOwnerModuleId(instance, _currentModule);
            using var moduleScope = PushResolutionModuleScope(ownerModuleId);
            using var currentModuleScope = PushCurrentModuleScope(ownerModuleId);
            if (!TryBuildProspectiveInstanceHead(instance, out var head, out _))
            {
                if (DeclarationReferencesTargetPath(instance, invocation.TargetPath))
                {
                    reason = $"replacing or removing '{GetGeneratedDeclarationName(target)}' would invalidate existing instance '{instance.Name}'";
                    return false;
                }
                continue;
            }
            if (removedSymbols.Contains(head.TraitId) ||
                removedTypes.Contains(head.ImplementingTypeId) ||
                ShapeReferencesRemovedNominal(head.Shape.ImplementingType, removedSymbols, removedTypes) ||
                head.Shape.TraitArgs.Any(shape =>
                    ShapeReferencesRemovedNominal(shape, removedSymbols, removedTypes)) ||
                DeclarationReferencesTargetPath(instance, invocation.TargetPath))
            {
                reason = $"replacing or removing '{GetGeneratedDeclarationName(target)}' would invalidate existing instance '{instance.Name}'";
                return false;
            }
        }

        foreach (var implementation in _symbolTable.Symbols.Values
                     .OfType<ImplSymbol>()
                     .Where(implementation => !removedSymbols.Contains(implementation.Id)))
        {
            if (removedSymbols.Contains(implementation.Trait) ||
                removedTypes.Contains(implementation.ImplementingType) ||
                ShapeReferencesRemovedNominal(implementation.ImplementingTypeShape, removedSymbols, removedTypes) ||
                implementation.TraitTypeArgShapes.Any(shape =>
                    ShapeReferencesRemovedNominal(shape, removedSymbols, removedTypes)) ||
                ImplKeyReferencesRemovedNominal(implementation.ImplementingTypeKey, removedSymbols, removedTypes) ||
                implementation.GetMatchingTraitTypeArgKeys().Any(key =>
                    ImplKeyReferencesRemovedNominal(key, removedSymbols, removedTypes)) ||
                implementation.ImplementingTypeRequirements.Any(requirement =>
                    removedSymbols.Contains(requirement.Trait)))
            {
                reason = $"replacing or removing '{GetGeneratedDeclarationName(target)}' would invalidate existing impl '{implementation.Name}'";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool DeclarationReferencesTargetPath(
        Declaration declaration,
        IReadOnlyList<string> targetPath)
    {
        if (targetPath.Count == 0)
        {
            return false;
        }

        foreach (var entry in AstStableNodeTraversal.Enumerate(CreateTraversalModule(declaration)))
        {
            if (entry.Node is not TypePath path)
            {
                continue;
            }

            var segments = path.ModulePath.Concat([path.TypeName]).ToArray();
            if (segments.SequenceEqual(targetPath, StringComparer.Ordinal) ||
                segments.Length >= targetPath.Count &&
                segments[^targetPath.Count..].SequenceEqual(targetPath, StringComparer.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ImplKeyReferencesRemovedNominal(
        ImplTypeRefKey key,
        IReadOnlySet<SymbolId> removedSymbols,
        IReadOnlySet<TypeId> removedTypes) =>
        !key.IsEmpty &&
        (removedSymbols.Contains(key.SymbolId) ||
         removedTypes.Contains(key.TypeId) ||
         key.TypeArguments.Any(argument =>
             ImplKeyReferencesRemovedNominal(argument, removedSymbols, removedTypes)));

    private static bool ShapeReferencesRemovedNominal(
        ImplTypeShapeNode? shape,
        IReadOnlySet<SymbolId> removedSymbols,
        IReadOnlySet<TypeId> removedTypes)
    {
        return shape switch
        {
            null => false,
            ImplConstructorShapeNode constructor =>
                removedSymbols.Contains(constructor.SymbolId) ||
                removedTypes.Contains(constructor.TypeId) ||
                constructor.Args.Any(argument =>
                    ShapeReferencesRemovedNominal(argument, removedSymbols, removedTypes)),
            ImplTupleShapeNode tuple => tuple.Elements.Any(element =>
                ShapeReferencesRemovedNominal(element, removedSymbols, removedTypes)),
            ImplArrowShapeNode arrow =>
                ShapeReferencesRemovedNominal(arrow.ParamType, removedSymbols, removedTypes) ||
                ShapeReferencesRemovedNominal(arrow.ReturnType, removedSymbols, removedTypes),
            ImplEffectfulShapeNode effectful =>
                ShapeReferencesRemovedNominal(effectful.InputType, removedSymbols, removedTypes) ||
                ShapeReferencesRemovedNominal(effectful.OutputType, removedSymbols, removedTypes),
            _ => false
        };
    }

    private bool TryValidateGeneratedInstanceCoherence(
        MetaInvocationOccurrence invocation,
        IReadOnlyList<MaterializedMetaNode> materializedNodes,
        SymbolId generatedModuleId,
        out string reason)
    {
        var diagnosticCount = _diagnostics.Count;
        try
        {
            return TryValidateGeneratedInstanceCoherenceCore(
                invocation,
                materializedNodes,
                generatedModuleId,
                out reason);
        }
        finally
        {
            DiscardProspectiveCoherenceDiagnostics(diagnosticCount);
        }
    }

    private bool TryValidateGeneratedInstanceCoherenceCore(
        MetaInvocationOccurrence invocation,
        IReadOnlyList<MaterializedMetaNode> materializedNodes,
        SymbolId generatedModuleId,
        out string reason)
    {
        var declarations = materializedNodes
            .SelectMany(static materialized => EnumerateGeneratedDeclarations(materialized.Node))
            .ToArray();
        var candidates = declarations
            .OfType<InstanceDecl>()
            .ToArray();
        var generatedMembers = materializedNodes
            .Where(static materialized => materialized.Placement == MetaDeclarationPlacement.Member)
            .Select(static materialized => materialized.Node)
            .ToArray();
        if (invocation.Target is InstanceDecl instanceTarget && generatedMembers.Length > 0)
        {
            var methods = instanceTarget.Methods.Concat(generatedMembers.OfType<FuncDef>()).ToArray();
            var associatedTypes = instanceTarget.AssociatedTypes
                .Concat(generatedMembers.OfType<AssociatedTypeDecl>())
                .ToArray();
            var associatedConsts = instanceTarget.AssociatedConsts
                .Concat(generatedMembers.OfType<AssociatedConstDecl>())
                .ToArray();
            using var moduleScope = PushResolutionModuleScope(invocation.ModuleId);
            using var currentModuleScope = PushCurrentModuleScope(invocation.ModuleId);
            if (!TryValidateProspectiveInstanceMutation(
                    instanceTarget,
                    methods,
                    associatedTypes,
                    associatedConsts,
                    out reason))
            {
                reason = $"generated members make instance '{instanceTarget.Name}' incoherent: {reason}";
                return false;
            }
        }
        var replacedSymbols = materializedNodes.Any(static node =>
                node.Placement == MetaDeclarationPlacement.ReplaceTarget)
            ? CollectOwnedDeclarationSymbolIds(invocation.Target)
            : [];
        var candidateHeads = new List<ProspectiveInstanceHead>(candidates.Length);
        using (PushResolutionModuleScope(generatedModuleId))
        using (PushCurrentModuleScope(generatedModuleId))
        {
            foreach (var candidate in candidates)
            {
                if (!TryBuildProspectiveInstanceHead(candidate, out var head, out reason))
                {
                    reason = $"generated instance '{candidate.Name}' failed detached coherence validation: {reason}";
                    return false;
                }
                candidateHeads.Add(head);
            }

        }

        if (candidateHeads.Count == 0)
        {
            reason = string.Empty;
            return true;
        }

        var candidateTraitIds = candidateHeads.Select(static head => head.TraitId).ToHashSet();
        var existingHeads = new List<ProspectiveInstanceHead>();
        foreach (var existing in _instanceDeclarations.Values
                     .Where(instance => !replacedSymbols.Contains(instance.SymbolId))
                     .GroupBy(static instance => instance.SymbolId)
                     .Select(static group => group.First()))
        {
            var ownerModuleId = GetDeclarationOwnerModuleId(existing, invocation.ModuleId);
            using var moduleScope = PushResolutionModuleScope(ownerModuleId);
            using var currentModuleScope = PushCurrentModuleScope(ownerModuleId);
            if (existing.Trait == null ||
                !TryResolveTraitFromInstance(existing.Trait, out var existingTraitId, out _) ||
                !candidateTraitIds.Contains(existingTraitId))
            {
                continue;
            }
            if (!TryBuildProspectiveInstanceHead(existing, out var head, out reason))
            {
                reason = $"existing instance '{existing.Name}' could not be included in detached coherence validation: {reason}";
                return false;
            }
            existingHeads.Add(head);
        }

        foreach (var candidate in candidateHeads)
        {
            foreach (var existing in existingHeads)
            {
                if (TryFindProspectiveInstanceConflict(candidate, existing, out reason))
                {
                    return false;
                }
            }

            foreach (var existing in _symbolTable.GetImplsForTrait(candidate.TraitId))
            {
                if (replacedSymbols.Contains(existing.Id))
                {
                    continue;
                }

                var existingShape = BuildImplHeadShape(existing);
                if (ImplSpecializationComparer.MayOverlap(candidate.Shape, existingShape) &&
                    ImplSpecializationComparer.CompareHeads(candidate.Shape, existingShape) is not
                        (ImplSpecializationRelation.MoreSpecific or ImplSpecializationRelation.LessSpecific))
                {
                    reason = $"generated implementation '{GetGeneratedDeclarationName(candidate.Declaration)}' overlaps existing impl '{existing.Name}'";
                    return false;
                }
            }
        }

        for (var leftIndex = 0; leftIndex < candidateHeads.Count; leftIndex++)
        {
            for (var rightIndex = leftIndex + 1; rightIndex < candidateHeads.Count; rightIndex++)
            {
                if (TryFindProspectiveInstanceConflict(
                        candidateHeads[leftIndex],
                        candidateHeads[rightIndex],
                        out reason))
                {
                    return false;
                }
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool TryValidateProspectiveInstanceMutation(
        InstanceDecl source,
        IEnumerable<FuncDef> methods,
        IEnumerable<AssociatedTypeDecl> associatedTypes,
        IEnumerable<AssociatedConstDecl> associatedConsts,
        out string reason)
    {
        var diagnosticCount = _diagnostics.Count;
        try
        {
            return TryValidateProspectiveInstanceMutationCore(
                source,
                methods,
                associatedTypes,
                associatedConsts,
                out reason);
        }
        finally
        {
            DiscardProspectiveCoherenceDiagnostics(diagnosticCount);
        }
    }

    private bool TryValidateProspectiveInstanceMutationCore(
        InstanceDecl source,
        IEnumerable<FuncDef> methods,
        IEnumerable<AssociatedTypeDecl> associatedTypes,
        IEnumerable<AssociatedConstDecl> associatedConsts,
        out string reason)
    {
        if (source.Trait == null)
        {
            reason = "the instance has no trait reference";
            return false;
        }

        var prospective = new InstanceDecl();
        prospective.SetName(source.Name);
        prospective.SetSpan(source.Span);
        prospective.SetTypeParams([.. source.TypeParams]);
        prospective.SetTrait(source.Trait);
        prospective.SetMethods([.. methods]);
        prospective.SetAssociatedTypes([.. associatedTypes]);
        prospective.SetAssociatedConsts([.. associatedConsts]);
        return TryBuildProspectiveInstanceHead(prospective, out _, out reason);
    }

    private void DiscardProspectiveCoherenceDiagnostics(int diagnosticCount)
    {
        if (_diagnostics.Count > diagnosticCount)
        {
            _diagnostics.RemoveRange(diagnosticCount, _diagnostics.Count - diagnosticCount);
        }
    }

    private bool TryBuildProspectiveInstanceHead(
        InstanceDecl instance,
        out ProspectiveInstanceHead head,
        out string reason)
    {
        head = null!;
        if (instance.Trait == null ||
            !TryResolveTraitFromInstance(instance.Trait, out var traitId, out var traitReference))
        {
            reason = "the referenced trait is unavailable";
            return false;
        }

        TypePath implementingType;
        TypeId implementingTypeId;
        var matchedTraitMethods = new HashSet<SymbolId>();
        if (instance.Methods.Count == 0)
        {
            if (!TryGetImplTargetTypeFromTraitRef(instance.Trait, out implementingType, out implementingTypeId))
            {
                reason = "an instance without methods requires a concrete first trait type argument";
                return false;
            }
        }
        else
        {
            implementingType = null!;
            implementingTypeId = TypeId.None;
            foreach (var method in instance.Methods)
            {
                if (!TryGetImplTargetType(
                        method,
                        out var methodType,
                        out var methodTypeId,
                        cloneReceiver: _symbolTable.GetSymbol(traitId)?.Name == "Clone"))
                {
                    reason = $"method '{method.Name}' has no concrete implementation target type";
                    return false;
                }
                if (!TryValidateTraitImplCompatibility(
                        traitId,
                        method,
                        methodType,
                        traitReference.TypeArgTexts,
                        out reason,
                        out var matchedTraitMethodId))
                {
                    return false;
                }

                if (matchedTraitMethodId.IsValid)
                {
                    matchedTraitMethods.Add(matchedTraitMethodId);
                }

                if (implementingType == null)
                {
                    implementingType = methodType;
                    implementingTypeId = methodTypeId;
                }
                else if (!IsSameNamedInstanceImplementingType(
                             implementingType,
                             implementingTypeId,
                             methodType,
                             methodTypeId))
                {
                    reason = "all instance methods must use the same concrete implementation target type";
                    return false;
                }
            }
        }

        if (!_traitDefinitions.TryGetValue(traitId, out var traitDefinition))
        {
            reason = "the trait declaration shape is unavailable";
            return false;
        }
        foreach (var requiredMethod in traitDefinition.Methods.Where(static method => method.Body.Count == 0))
        {
            if (!requiredMethod.SymbolId.IsValid || matchedTraitMethods.Contains(requiredMethod.SymbolId))
            {
                continue;
            }

            reason = $"required trait method '{requiredMethod.Name}' is not implemented";
            return false;
        }
        if (!TryValidateProspectiveAssociatedItems(instance, traitDefinition, out reason))
        {
            return false;
        }

        var shape = BuildDetachedImplHeadShape(
            traitId,
            traitReference,
            implementingType,
            instance.TypeParams);
        head = new ProspectiveInstanceHead(
            instance,
            traitId,
            implementingType,
            implementingTypeId,
            shape);
        reason = string.Empty;
        return true;
    }

    private ImplHeadShape BuildDetachedImplHeadShape(
        SymbolId traitId,
        ImplTraitReference traitReference,
        TypePath implementingType,
        IReadOnlyList<TypeParam> typeParameters)
    {
        var typeParameterNames = typeParameters
            .Where(static parameter => parameter.ParameterKind != GenericParameterKind.Value)
            .Select(static parameter => parameter.Name)
            .ToHashSet(StringComparer.Ordinal);
        var valueParameterNames = typeParameters
            .Where(static parameter => parameter.ParameterKind == GenericParameterKind.Value)
            .ToDictionary(
                static parameter => parameter.Name,
                static parameter => ResolveDetachedValueParameterTypeId(parameter),
                StringComparer.Ordinal);
        var traitArguments = BuildDetachedTraitArgumentShapes(
            traitId,
            traitReference,
            typeParameterNames,
            valueParameterNames);
        return new ImplHeadShape(
            traitId,
            traitArguments,
            BuildDetachedImplTypeShape(implementingType, typeParameterNames, valueParameterNames));
    }

    private IReadOnlyList<ImplTypeShapeNode> BuildDetachedTraitArgumentShapes(
        SymbolId traitId,
        ImplTraitReference traitReference,
        IReadOnlySet<string> typeParameterNames,
        IReadOnlyDictionary<string, TypeId> valueParameterNames)
    {
        if (traitReference.GenericArguments.Count > 0)
        {
            return traitReference.GenericArguments
                .Select((argument, index) => BuildDetachedGenericArgumentShape(
                    argument,
                    GetImplTraitGenericParameterKind(traitId, index),
                    typeParameterNames,
                    valueParameterNames,
                    index))
                .ToArray();
        }
        if (traitReference.TypeArgs.Count > 0)
        {
            return traitReference.TypeArgs
                .Select((argument, index) => GetImplTraitGenericParameterKind(traitId, index) == GenericParameterKind.Value &&
                                             argument is TypePath valuePath
                    ? BuildDetachedValueShape(
                        ConvertTypePathToValueExpression(valuePath),
                        valueParameterNames,
                        index)
                    : BuildDetachedImplTypeShape(argument, typeParameterNames, valueParameterNames))
                .ToArray();
        }

        return traitReference.TypeArgTexts
            .Select(ParseCanonicalShapeOrFallback)
            .ToArray();
    }

    private ImplTypeShapeNode BuildDetachedGenericArgumentShape(
        GenericArgumentNode argument,
        GenericParameterKind expectedKind,
        IReadOnlySet<string> typeParameterNames,
        IReadOnlyDictionary<string, TypeId> valueParameterNames,
        int parameterIndex)
    {
        if (expectedKind == GenericParameterKind.Value)
        {
            var expression = argument switch
            {
                ValueGenericArgumentNode value => value.Expression,
                UnresolvedGenericArgumentNode { ValueCandidate: { } value } => value,
                TypeGenericArgumentNode { Type: TypePath path } => ConvertTypePathToValueExpression(path),
                UnresolvedGenericArgumentNode { TypeCandidate: TypePath path } => ConvertTypePathToValueExpression(path),
                _ => null
            };
            return expression == null
                ? ImplWildcardShapeNode.Instance
                : BuildDetachedValueShape(expression, valueParameterNames, parameterIndex);
        }

        var detachedType = argument switch
        {
            TypeGenericArgumentNode typeArgument => typeArgument.Type,
            EffectGenericArgumentNode effect => effect.EffectRow,
            UnresolvedGenericArgumentNode { TypeCandidate: { } typeCandidate } => typeCandidate,
            _ => null
        };
        return detachedType == null
            ? ImplWildcardShapeNode.Instance
            : BuildDetachedImplTypeShape(detachedType, typeParameterNames, valueParameterNames);
    }

    private static ImplTypeShapeNode BuildDetachedValueShape(
        EidosAstNode expression,
        IReadOnlyDictionary<string, TypeId> valueParameterNames,
        int parameterIndex)
    {
        var name = expression switch
        {
            IdentifierExpr identifier => identifier.Name,
            PathExpr path when path.ModulePath.Count == 0 => path.Name,
            _ => string.Empty
        };
        if (!string.IsNullOrWhiteSpace(name) && valueParameterNames.TryGetValue(name, out var parameterTypeId))
        {
            return new ImplValueVariableShapeNode($"value:{name}", parameterTypeId);
        }
        if (ComptimeEvaluator.TryEvaluate(
                expression,
                new Dictionary<SymbolId, ComptimeValue>(),
                out var value,
                out _))
        {
            return new ImplConcreteValueShapeNode(
                ImplValueRefKey.NormalizeCanonicalPayload(value.CanonicalText),
                ResolveImplValueTypeId(expression, value));
        }

        return new ImplValueVariableShapeNode(
            $"expression:{parameterIndex}:{expression.Span.Position}:{expression.Span.Length}",
            TypeId.None);
    }

    private static TypeId ResolveDetachedValueParameterTypeId(TypeParam parameter) =>
        parameter.ComptimeTypeAnnotation is TypePath { TypeName: var name }
            ? BaseTypes.GetBuiltInTypeId(name)
            : TypeId.None;

    private ImplTypeShapeNode BuildDetachedImplTypeShape(
        TypeNode node,
        IReadOnlySet<string> typeParameterNames,
        IReadOnlyDictionary<string, TypeId> valueParameterNames)
    {
        return node switch
        {
            TypePath path when path.TypeArgs.Count == 0 &&
                               path.GenericArguments.Count == 0 &&
                               typeParameterNames.Contains(path.TypeName) =>
                new ImplVariableShapeNode(path.TypeName),
            TypePath path => BuildDetachedImplTypePathShape(path, typeParameterNames, valueParameterNames),
            TupleType tuple => new ImplTupleShapeNode(tuple.Elements
                .Select(element => BuildDetachedImplTypeShape(element, typeParameterNames, valueParameterNames))
                .ToArray()),
            ArrowType arrow => new ImplArrowShapeNode(
                BuildDetachedImplTypeShape(arrow.ParamType, typeParameterNames, valueParameterNames),
                BuildDetachedImplTypeShape(arrow.ReturnType, typeParameterNames, valueParameterNames)),
            EffectfulType effectful => new ImplEffectfulShapeNode(
                BuildDetachedImplTypeShape(effectful.InputType, typeParameterNames, valueParameterNames),
                effectful.EnumerateEffectPaths()
                    .Select(path => string.Join(WellKnownStrings.Separators.Path, path))
                    .OrderBy(static path => path, StringComparer.Ordinal)
                    .ToArray(),
                effectful.OutputType == null
                    ? null
                    : BuildDetachedImplTypeShape(effectful.OutputType, typeParameterNames, valueParameterNames)),
            WildcardType => ImplWildcardShapeNode.Instance,
            _ => new ImplConstructorShapeNode(node.GetType().Name, [])
        };
    }

    private ImplTypeShapeNode BuildDetachedImplTypePathShape(
        TypePath path,
        IReadOnlySet<string> typeParameterNames,
        IReadOnlyDictionary<string, TypeId> valueParameterNames)
    {
        var symbolId = ResolveTypePathSymbolIdForImplKey(path);
        var symbol = symbolId.IsValid ? _symbolTable.GetSymbol(symbolId) : null;
        var name = path.ModulePath.Count == 0
            ? path.TypeName
            : string.Join(WellKnownStrings.Separators.Path, [.. path.ModulePath, path.TypeName]);
        return new ImplConstructorShapeNode(
            name,
            BuildDetachedPathArgumentShapes(path, typeParameterNames, valueParameterNames))
        {
            SymbolId = symbolId,
            TypeId = symbol?.TypeId ?? TypeId.None
        };
    }

    private IReadOnlyList<ImplTypeShapeNode> BuildDetachedPathArgumentShapes(
        TypePath path,
        IReadOnlySet<string> typeParameterNames,
        IReadOnlyDictionary<string, TypeId> valueParameterNames)
    {
        if (path.GenericArguments.Count == 0)
        {
            return path.TypeArgs
                .Select(argument => BuildDetachedImplTypeShape(argument, typeParameterNames, valueParameterNames))
                .ToArray();
        }

        var targetSymbolId = ResolveTypePathSymbolIdForImplKey(path);
        return path.GenericArguments
            .Select((argument, index) =>
            {
                var expectedKind = argument.ResolvedKind ??
                                   (TryGetGenericParameterKind(targetSymbolId, index, out var declaredKind)
                                       ? declaredKind
                                       : GenericParameterKind.Type);
                return BuildDetachedGenericArgumentShape(
                    argument,
                    expectedKind,
                    typeParameterNames,
                    valueParameterNames,
                    index);
            })
            .ToArray();
    }

    private static bool TryValidateProspectiveAssociatedItems(
        InstanceDecl instance,
        TraitDef trait,
        out string reason)
    {
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var associatedType in instance.AssociatedTypes)
        {
            if (!typeNames.Add(associatedType.Name))
            {
                reason = $"associated type '{associatedType.Name}' is implemented more than once";
                return false;
            }
            if (associatedType.ValueType == null)
            {
                reason = $"associated type '{associatedType.Name}' requires a type value";
                return false;
            }
        }

        var constNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var associatedConst in instance.AssociatedConsts)
        {
            if (!constNames.Add(associatedConst.Name))
            {
                reason = $"associated const '{associatedConst.Name}' is implemented more than once";
                return false;
            }
            if (associatedConst.Value == null)
            {
                reason = $"associated const '{associatedConst.Name}' requires a value";
                return false;
            }
        }

        var requiredTypes = trait.AssociatedTypes.Select(static item => item.Name).ToHashSet(StringComparer.Ordinal);
        var requiredConsts = trait.AssociatedConsts.Select(static item => item.Name).ToHashSet(StringComparer.Ordinal);
        var missingType = requiredTypes.FirstOrDefault(name => !typeNames.Contains(name));
        if (missingType != null)
        {
            reason = $"required associated type '{missingType}' is not implemented";
            return false;
        }
        var missingConst = requiredConsts.FirstOrDefault(name => !constNames.Contains(name));
        if (missingConst != null)
        {
            reason = $"required associated const '{missingConst}' is not implemented";
            return false;
        }
        var unknownType = typeNames.FirstOrDefault(name => !requiredTypes.Contains(name));
        if (unknownType != null)
        {
            reason = $"unknown associated type '{unknownType}'";
            return false;
        }
        var unknownConst = constNames.FirstOrDefault(name => !requiredConsts.Contains(name));
        if (unknownConst != null)
        {
            reason = $"unknown associated const '{unknownConst}'";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryFindProspectiveInstanceConflict(
        ProspectiveInstanceHead candidate,
        ProspectiveInstanceHead existing,
        out string reason)
    {
        if (candidate.TraitId != existing.TraitId ||
            !ImplSpecializationComparer.MayOverlap(candidate.Shape, existing.Shape) ||
            ImplSpecializationComparer.CompareHeads(candidate.Shape, existing.Shape) is
                ImplSpecializationRelation.MoreSpecific or ImplSpecializationRelation.LessSpecific)
        {
            reason = string.Empty;
            return false;
        }

        var existingDescription = existing.Declaration is InstanceDecl
            ? $"instance '{GetGeneratedDeclarationName(existing.Declaration)}'"
            : $"implementation '{GetGeneratedDeclarationName(existing.Declaration)}'";
        reason = $"generated implementation '{GetGeneratedDeclarationName(candidate.Declaration)}' overlaps {existingDescription}";
        return true;
    }
}
