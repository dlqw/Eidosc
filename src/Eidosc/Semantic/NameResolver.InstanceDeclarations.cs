using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void ResolveInstanceDeclReferences(InstanceDecl instance, bool resolveBodies = true)
    {
        var hasTypeParams = instance.TypeParams.Count > 0;
        using var scopeGuard = hasTypeParams ? _symbolTable.PushScopeGuard(ScopeKind.Module) : default;
        if (hasTypeParams)
        {
            foreach (var typeParam in instance.TypeParams)
            {
                DeclareTypeParameterIfValid(typeParam);
                ResolveTypeParamReferences(typeParam);
            }
        }

        if (instance.Trait == null)
        {
            AddError(instance.Span, DiagnosticMessages.UndefinedTraitInImpl("<missing>"));
            return;
        }

        ResolveInstanceTraitReference(instance.Trait);
        if (!TryResolveTraitFromInstance(instance.Trait, out var traitId, out var traitRef))
        {
            AddUndefinedImplTraitError(
                instance.Trait.Span,
                traitRef,
                DiagnosticMessages.UndefinedTraitInImpl(FormatTraitReferenceDisplay(traitRef)));
            return;
        }

        if (instance.UsesConstructorBridge)
        {
            GenerateConstructorBridgeInstanceMethods(instance, traitId);
        }

        if (instance.Members.Count > 0)
        {
            ResolveInstanceMemberRange(instance, 0, traitId, traitRef, resolveBodies);
            return;
        }

        foreach (var associatedType in instance.AssociatedTypes)
        {
            ResolveAssociatedTypeReferences(associatedType);
        }

        foreach (var associatedConst in instance.AssociatedConsts)
        {
            ResolveAssociatedConstReferences(associatedConst, resolveValue: resolveBodies);
        }

        foreach (var method in instance.Methods)
        {
            ResolveInstanceMethodReferences(method, resolveBodies);
        }

        if ((_isProvisionalSyntaxDiscovery || !resolveBodies) &&
            instance.MetaInvocations.Any(static invocation =>
                invocation.Stage <= ClauseStage.Semantic))
        {
            return;
        }

        ValidateAssociatedMemberImplementations(instance, traitId);
        RegisterNamedInstanceImpl(instance, traitId, traitRef);
    }

    private bool ResolveInstanceMemberRange(InstanceDecl instance, int startIndex, bool resolveBodies = true)
    {
        if (instance.Trait == null)
        {
            AddError(instance.Span, DiagnosticMessages.UndefinedTraitInImpl("<missing>"));
            return false;
        }

        ResolveInstanceTraitReference(instance.Trait);
        if (!TryResolveTraitFromInstance(instance.Trait, out var traitId, out var traitRef))
        {
            AddUndefinedImplTraitError(
                instance.Trait.Span,
                traitRef,
                DiagnosticMessages.UndefinedTraitInImpl(FormatTraitReferenceDisplay(traitRef)));
            return false;
        }

        return ResolveInstanceMemberRange(instance, startIndex, traitId, traitRef, resolveBodies);
    }

    private bool ResolveInstanceMemberRange(
        InstanceDecl instance,
        int startIndex,
        SymbolId traitId,
        ImplTraitReference traitRef,
        bool resolveBodies = true)
    {
        for (var index = startIndex; index < instance.Members.Count; index++)
        {
            switch (instance.Members[index])
            {
                case ExpandDeclaration expansion:
                    ResolveExpandMemberReferences(expansion, instance);
                    return false;
                case AssociatedTypeDecl associatedType:
                    ResolveAssociatedTypeReferences(associatedType);
                    break;
                case AssociatedConstDecl associatedConst:
                    ResolveAssociatedConstReferences(associatedConst, resolveValue: resolveBodies);
                    break;
                case FuncDef method:
                    ResolveInstanceMethodReferences(method, resolveBodies);
                    break;
            }
        }

        ValidateAssociatedMemberImplementations(instance, traitId);
        RegisterNamedInstanceImpl(instance, traitId, traitRef);
        return true;
    }

    private void GenerateConstructorBridgeInstanceMethods(InstanceDecl instance, SymbolId traitId)
    {
        if (instance.Methods.Count > 0)
        {
            return;
        }

        if (instance.TargetType == null)
        {
            AddError(instance.Span, "Constructor instance bridge requires an explicit target type.");
            return;
        }

        ResolveTypeReferences(instance.TargetType);
        if (ResolveInstanceBridgeTarget(instance.TargetType) is not { } adtEntry)
        {
            AddError(instance.TargetType.Span, "Constructor instance bridge target must be an ADT type.");
            return;
        }

        if (!_traitDefinitions.TryGetValue(traitId, out var traitDefinition))
        {
            AddError(instance.Span, DiagnosticMessages.TraitDefinitionUnavailableForImplSignature(
                _symbolTable.GetSymbol(traitId)?.Name ?? "<trait>"));
            return;
        }

        if (traitDefinition.TypeParams.Count > 0)
        {
            AddError(instance.Span, $"Trait '{traitDefinition.Name}' has type parameters; constructor instance bridge does not support trait type parameters yet.");
            return;
        }

        var generated = new List<FuncDef>();
        var selfType = CloneTypeNode(instance.TargetType);
        foreach (var method in traitDefinition.Methods)
        {
            if (!TryBuildConstructorBridgeMethodSignature(method, selfType, out var signature))
            {
                AddError(method.Span, $"Trait method '{method.Name}' cannot be bridged from constructors; expected first parameter Self.");
                continue;
            }

            var factsByConstructor = ValidateConstructorBridgeFacts(instance, adtEntry.Adt);
            var (methodParamTypes, _) = ExtractSignatureTypes(method.Signature);
            var extraParamCount = Math.Max(0, methodParamTypes.Count - 1);
            var branches = new List<PatternBranch>();
            var canGenerate = true;
            foreach (var ctor in adtEntry.Adt.Constructors)
            {
                factsByConstructor.TryGetValue(ctor.Name, out var ctorFacts);
                var constant = ctorFacts?.FirstOrDefault(c =>
                    string.Equals(c.Name, method.Name, StringComparison.Ordinal));
                if (constant?.Value == null)
                {
                    AddError(ctor.Span, $"Constructor '{ctor.Name}' must provide associated constant '{method.Name}' for instance '{instance.Name}'.");
                    canGenerate = false;
                    continue;
                }

                var receiverPattern = MakeCtorPattern(
                    ctor,
                    MakeWildcardPatterns(GetConstructorRuntimeFieldCount(ctor), instance.Span),
                    instance.Span);
                var branchPatterns = new List<Pattern> { receiverPattern };
                var extraArgPatterns = MakeVarPatterns(extraParamCount, "bridgeArg", instance.Span);
                branchPatterns.AddRange(extraArgPatterns);
                var branchPattern = branchPatterns.Count == 1
                    ? branchPatterns[0]
                    : MakeTuplePattern(branchPatterns, instance.Span);

                branches.Add(MakeBranch(
                    branchPattern,
                    ApplyConstructorBridgeConstant(constant.Value, extraArgPatterns, instance.Span),
                    instance.Span));
            }

            if (!canGenerate)
            {
                continue;
            }

            var funcDef = new FuncDef();
            SetPrivate(funcDef, "Name", method.Name);
            SetPrivate(funcDef, "Span", instance.Span);
            funcDef.SetTypeParams(instance.TypeParams.Select(tp => CreateDerivedTypeParamForInstance(tp, instance.Span)).ToList());
            funcDef.SetSignature(signature);
            funcDef.SetBody(branches);
            generated.Add(funcDef);
        }

        if (generated.Count == 0)
        {
            return;
        }

        instance.SetMethods(generated);
        _instanceMethodDeclarationDepth++;
        try
        {
            foreach (var method in generated)
            {
                CollectFuncDef(method);
            }
        }
        finally
        {
            _instanceMethodDeclarationDepth--;
        }
    }

    private Dictionary<string, List<ConstructorConstant>> ValidateConstructorBridgeFacts(InstanceDecl instance, AdtDef adt)
    {
        var result = new Dictionary<string, List<ConstructorConstant>>(StringComparer.Ordinal);
        var knownConstructors = adt.Constructors
            .Select(static ctor => ctor.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var fact in instance.ConstructorBridgeFacts)
        {
            if (!knownConstructors.Contains(fact.ConstructorName))
            {
                AddError(fact.Span, $"Constructor instance bridge references unknown constructor '{fact.ConstructorName}' for type '{adt.Name}'.");
                continue;
            }

            if (!result.TryAdd(fact.ConstructorName, fact.Constants))
            {
                AddError(fact.Span, $"Constructor instance bridge declares facts for constructor '{fact.ConstructorName}' more than once.");
                continue;
            }

            ValidateConstructorBridgeConstants(fact.ConstructorName, fact.Constants);
        }

        return result;
    }

    private void ValidateConstructorBridgeConstants(string constructorName, IReadOnlyList<ConstructorConstant> constants)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var constant in constants)
        {
            if (!seen.Add(constant.Name))
            {
                AddError(constant.Span, DiagnosticMessages.ConstructorConstantDuplicate(constructorName, constant.Name));
            }

            if (constant.Value != null && !IsSupportedConstructorConstantExpression(constant.Value))
            {
                AddError(
                    constant.Span,
                    DiagnosticMessages.ConstructorConstantExpressionUnsupported(constructorName, constant.Name));
            }

            if (constant.Value != null)
            {
                ResolveExpressionReferences(constant.Value);
            }
        }
    }

    private static EidosAstNode ApplyConstructorBridgeConstant(
        EidosAstNode constantValue,
        IReadOnlyList<VarPattern> extraArgPatterns,
        SourceSpan span)
    {
        var expression = CloneExpression(constantValue);
        foreach (var argPattern in extraArgPatterns)
        {
            var call = new CallExpr();
            call.SetSpan(span);
            call.SetFunction(expression);
            call.AddPositionalArg(MakeIdent(argPattern.Name, span));
            expression = call;
        }

        return expression;
    }

    private (SymbolId AdtId, AdtDef Adt)? ResolveInstanceBridgeTarget(TypeNode targetType)
    {
        if (targetType is not TypePath typePath)
        {
            return null;
        }

        var symbolId = !string.IsNullOrWhiteSpace(typePath.TypeName)
            ? _symbolTable.LookupType(typePath.TypeName) ?? SymbolId.None
            : typePath.SymbolId;

        return symbolId.IsValid &&
               _symbolTable.GetSymbol(symbolId) is AdtSymbol &&
               _adtDefinitions.TryGetValue(symbolId, out var adt)
            ? (symbolId, adt)
            : null;
    }

    private static bool TryBuildConstructorBridgeMethodSignature(
        FuncDef method,
        TypeNode selfType,
        out TypeNode signature)
    {
        signature = null!;
        if (method.TypeParams.Count > 0 || method.Signature.Any(ContainsEffectfulTypeNode))
        {
            return false;
        }

        var (paramTypes, returnType) = ExtractSignatureTypes(method.Signature);
        if (paramTypes.Count == 0 || returnType == null)
        {
            return false;
        }

        if (paramTypes[0] is not TypePath
            {
                ModulePath.Count: 0,
                TypeArgs.Count: 0,
                TypeName: WellKnownStrings.Keywords.Self
            })
        {
            return false;
        }

        if (returnType is EffectfulType)
        {
            return false;
        }

        var generatedParams = new List<TypeNode> { CloneTypeNode(selfType) };
        generatedParams.AddRange(paramTypes.Skip(1).Select(param => SubstituteSelfType(param, selfType)));
        signature = CreateCurriedArrowType(generatedParams, SubstituteSelfType(returnType, selfType), method.Span);
        return true;
    }

    private static TypeParam CreateDerivedTypeParamForInstance(TypeParam original, SourceSpan span)
    {
        var derived = new TypeParam();
        SetPrivate(derived, "Name", original.Name);
        SetPrivate(derived, "Span", original.Span);

        if (original.KindAnnotation != null)
        {
            SetPrivate(derived, "KindAnnotation", original.KindAnnotation);
        }

        foreach (var constraint in original.TraitConstraints)
        {
            derived.TraitConstraints.Add(constraint);
        }

        return derived;
    }

    private void ResolveInstanceTraitReference(TraitRef trait)
    {
        foreach (var typeArg in trait.TypeArgs)
        {
            ResolveTypeReferences(typeArg);
        }
    }

    private void ResolveInstanceMethodReferences(FuncDef method, bool resolveBody = true)
    {
        _traitSignatureDepth++;
        try
        {
            ResolveFuncDefReferences(method, resolveBody);
        }
        finally
        {
            _traitSignatureDepth--;
        }
    }

    private bool TryResolveTraitFromInstance(
        TraitRef trait,
        out SymbolId traitId,
        out ImplTraitReference traitRef)
    {
        var path = new List<string>(trait.ModulePath);
        if (!string.IsNullOrWhiteSpace(trait.TraitName))
        {
            path.Add(trait.TraitName);
        }

        var genericArguments = trait.GenericArguments.ToList();
        var typeArgTexts = genericArguments.Count > 0
            ? genericArguments
                .Select(RenderImplClauseGenericArgumentText)
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .ToList()
            : trait.TypeArgs
                .Select(RenderImplClauseTypeArgumentText)
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .ToList();
        traitRef = new(path, typeArgTexts, trait.TypeArgs.ToList(), genericArguments);
        traitId = SymbolId.None;

        if (path.Count == 0)
        {
            return false;
        }

        var result = ResolvePathWithImports(path);
        if (result.IsSuccess && _symbolTable.GetSymbol(result.SymbolId) is TraitSymbol)
        {
            traitId = result.SymbolId;
            trait.SymbolId = traitId;
            traitRef = ResolveImplTraitReferenceGenericArguments(traitId, traitRef, trait.Span);
            return true;
        }

        if (path.Count == 1)
        {
            var fallback = _symbolTable.LookupTrait(path[0]);
            if (fallback is { } fallbackId && _symbolTable.GetSymbol(fallbackId) is TraitSymbol)
            {
                traitId = fallbackId;
                trait.SymbolId = traitId;
                traitRef = ResolveImplTraitReferenceGenericArguments(traitId, traitRef, trait.Span);
                return true;
            }
        }

        return false;
    }

    private void RegisterNamedInstanceImpl(InstanceDecl instance, SymbolId traitId, ImplTraitReference traitRef)
    {
        if (!_processedNamedInstanceDeclarations.Add(instance))
        {
            return;
        }

        SymbolId implId = SymbolId.None;
        TypePath? registeredImplementingTypePath = null;
        TypeId registeredTargetTypeId = TypeId.None;
        SymbolId registeredTraitMethodId = SymbolId.None;

        if (instance.Methods.Count == 0)
        {
            TypePath implementingTypePath = null!;
            var targetTypeId = TypeId.None;
            var hasTarget = instance.TargetType != null &&
                            TryResolveImplTargetTypeNode(
                                instance.TargetType,
                                out implementingTypePath,
                                out targetTypeId);
            if (!hasTarget)
            {
                hasTarget = TryGetImplTargetTypeFromTraitRef(
                    instance.Trait,
                    out implementingTypePath,
                    out targetTypeId);
            }

            if (hasTarget)
            {
                implId = TryDeclareNamedInstanceImpl(
                    instance,
                    traitId,
                    traitRef,
                    implementingTypePath,
                    targetTypeId,
                    implementingTypeRequirements: []);
            }
            else
            {
                AddError(instance.Span, DiagnosticMessages.ImplRequiresConcreteFirstParameter);
            }

            if (implId.IsValid)
            {
                _symbolTable.AddMemberToModule(_currentModule, implId);
            }

            return;
        }

        foreach (var method in instance.Methods)
        {
            if (!TryGetImplTargetType(
                    method,
                    out var implementingTypePath,
                    out var targetTypeId,
                    cloneReceiver: _symbolTable.GetSymbol(traitId)?.Name == "Clone"))
            {
                AddError(method.Span, DiagnosticMessages.ImplRequiresConcreteFirstParameter);
                continue;
            }

            if (!TryValidateTraitImplCompatibility(
                    traitId,
                    method,
                    implementingTypePath,
                    traitRef.TypeArgTexts,
                    out var reason,
                    out var matchedTraitMethodId))
            {
                AddError(method.Span, reason);
                continue;
            }

            if (!implId.IsValid)
            {
                implId = TryDeclareNamedInstanceImpl(
                    instance,
                    traitId,
                    traitRef,
                    implementingTypePath,
                    targetTypeId,
                    implementingTypeRequirements: null);
                registeredImplementingTypePath = implementingTypePath;
                registeredTargetTypeId = targetTypeId;
                registeredTraitMethodId = matchedTraitMethodId;
            }
            else if (!IsSameNamedInstanceImplementingType(
                         registeredImplementingTypePath,
                         registeredTargetTypeId,
                         implementingTypePath,
                         targetTypeId))
            {
                AddError(method.Span, DiagnosticMessages.ImplRequiresConcreteFirstParameter);
                continue;
            }

            if (implId.IsValid && method.SymbolId.IsValid)
            {
                _symbolTable.AddMethodToImpl(
                    implId,
                    method.SymbolId,
                    matchedTraitMethodId.IsValid ? matchedTraitMethodId : registeredTraitMethodId);
            }
        }

        if (implId.IsValid)
        {
            _symbolTable.AddMemberToModule(_currentModule, implId);
            ValidateOverloadedTraitMethodCoverage(instance, traitId, implId);
        }
    }

    private void ValidateOverloadedTraitMethodCoverage(InstanceDecl instance, SymbolId traitId, SymbolId implId)
    {
        if (!_traitDefinitions.TryGetValue(traitId, out var traitDefinition) ||
            _symbolTable.GetSymbol<ImplSymbol>(implId) is not { } impl)
        {
            return;
        }

        var overloadedMethods = traitDefinition.Methods
            .GroupBy(static method => method.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .SelectMany(static group => group)
            .Where(static method => method.Body.Count == 0)
            .ToList();
        if (overloadedMethods.Count == 0)
        {
            return;
        }

        foreach (var method in overloadedMethods)
        {
            if (method.SymbolId.IsValid &&
                impl.TraitMethodImplementations.ContainsKey(method.SymbolId))
            {
                continue;
            }

            var traitName = _symbolTable.GetSymbol(traitId)?.Name ?? traitDefinition.Name;
            AddError(
                instance.Span,
                $"Instance '{instance.Name}' must implement overloaded trait method '{method.Name}' for trait '{traitName}'.");
        }
    }

    private void ValidateAssociatedMemberImplementations(InstanceDecl instance, SymbolId traitId)
    {
        if (!_traitDefinitions.TryGetValue(traitId, out var traitDefinition))
        {
            return;
        }

        ValidateAssociatedTypes(instance, traitDefinition);
        ValidateAssociatedConsts(instance, traitDefinition);
    }

    private void ValidateAssociatedTypes(InstanceDecl instance, TraitDef traitDefinition)
    {
        var implemented = new Dictionary<string, AssociatedTypeDecl>(StringComparer.Ordinal);
        foreach (var associatedType in instance.AssociatedTypes)
        {
            if (string.IsNullOrWhiteSpace(associatedType.Name))
            {
                continue;
            }

            if (!implemented.TryAdd(associatedType.Name, associatedType))
            {
                AddError(associatedType.Span, $"Instance '{instance.Name}' implements associated type '{associatedType.Name}' more than once.");
            }
        }

        var required = traitDefinition.AssociatedTypes
            .Select(static associatedType => associatedType.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var associatedType in traitDefinition.AssociatedTypes)
        {
            if (!string.IsNullOrWhiteSpace(associatedType.Name) &&
                !implemented.ContainsKey(associatedType.Name))
            {
                AddError(instance.Span, $"Instance '{instance.Name}' must implement associated type '{associatedType.Name}' for trait '{traitDefinition.Name}'.");
            }
        }

        foreach (var associatedType in instance.AssociatedTypes)
        {
            if (!string.IsNullOrWhiteSpace(associatedType.Name) &&
                !required.Contains(associatedType.Name))
            {
                AddError(associatedType.Span, $"Instance '{instance.Name}' provides unknown associated type '{associatedType.Name}' for trait '{traitDefinition.Name}'.");
            }

            if (associatedType.ValueType == null)
            {
                AddError(associatedType.Span, $"Instance '{instance.Name}' associated type '{associatedType.Name}' requires a type value.");
            }
        }
    }

    private void ValidateAssociatedConsts(InstanceDecl instance, TraitDef traitDefinition)
    {
        var implemented = new Dictionary<string, AssociatedConstDecl>(StringComparer.Ordinal);
        foreach (var associatedConst in instance.AssociatedConsts)
        {
            if (string.IsNullOrWhiteSpace(associatedConst.Name))
            {
                continue;
            }

            if (!implemented.TryAdd(associatedConst.Name, associatedConst))
            {
                AddError(associatedConst.Span, $"Instance '{instance.Name}' implements associated const '{associatedConst.Name}' more than once.");
            }
        }

        var required = traitDefinition.AssociatedConsts
            .Select(static associatedConst => associatedConst.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
        foreach (var associatedConst in traitDefinition.AssociatedConsts)
        {
            if (!string.IsNullOrWhiteSpace(associatedConst.Name) &&
                !implemented.ContainsKey(associatedConst.Name))
            {
                AddError(instance.Span, $"Instance '{instance.Name}' must implement associated const '{associatedConst.Name}' for trait '{traitDefinition.Name}'.");
            }
        }

        foreach (var associatedConst in instance.AssociatedConsts)
        {
            if (!string.IsNullOrWhiteSpace(associatedConst.Name) &&
                !required.Contains(associatedConst.Name))
            {
                AddError(associatedConst.Span, $"Instance '{instance.Name}' provides unknown associated const '{associatedConst.Name}' for trait '{traitDefinition.Name}'.");
            }

            if (associatedConst.Value == null)
            {
                AddError(associatedConst.Span, $"Instance '{instance.Name}' associated const '{associatedConst.Name}' requires a value.");
            }
        }
    }

    private bool TryGetImplTargetTypeFromTraitRef(TraitRef? traitRef, out TypePath implementingTypePath, out TypeId targetTypeId)
    {
        implementingTypePath = null!;
        targetTypeId = TypeId.None;

        if (traitRef?.TypeArgs.Count is null or 0 ||
            traitRef.TypeArgs[0] is not TypePath typePath)
        {
            return false;
        }

        var symbolId = typePath.SymbolId;
        if (!symbolId.IsValid && !string.IsNullOrWhiteSpace(typePath.TypeName))
        {
            symbolId = _symbolTable.LookupType(typePath.TypeName) ?? SymbolId.None;
        }

        if (!symbolId.IsValid ||
            _symbolTable.GetSymbol(symbolId) is not Symbol symbol ||
            !symbol.TypeId.IsValid)
        {
            return false;
        }

        implementingTypePath = typePath;
        targetTypeId = symbol.TypeId;
        return true;
    }

    private SymbolId TryDeclareNamedInstanceImpl(
        InstanceDecl instance,
        SymbolId traitId,
        ImplTraitReference traitRef,
        TypePath implementingTypePath,
        TypeId targetTypeId,
        IReadOnlyList<ImplTypeArgTraitRequirement>? implementingTypeRequirements)
    {
        string? requirementError = null;
        if (implementingTypeRequirements == null &&
            TryBuildImplTypeRequirements(
                instance.TypeParams.Concat(instance.Methods.FirstOrDefault()?.TypeParams ?? []),
                implementingTypePath,
                out var builtRequirements,
                out requirementError))
        {
            implementingTypeRequirements = builtRequirements;
        }
        else if (implementingTypeRequirements == null)
        {
            AddError(instance.Span, requirementError ?? DiagnosticMessages.UnsupportedConstrainedImplHead);
            return SymbolId.None;
        }

        var canonicalTraitTypeArgs = CanonicalizeImplTraitTypeArgs(traitRef);
        var traitTypeArgKeys = BuildImplTraitTypeArgKeys(traitId, traitRef);
        var canonicalTraitTypeArgKeys = BuildCanonicalImplTraitTypeArgKeys(
            canonicalTraitTypeArgs,
            traitTypeArgKeys);
        var canonicalImplementingType = CanonicalizeTypePathForImplHead(implementingTypePath);
        var requestedHeadShape = BuildCanonicalImplHeadShape(
            traitId,
            traitRef.GenericArguments.Count > 0 ? [] : traitRef.TypeArgs,
            implementingTypePath,
            canonicalTraitTypeArgs,
            canonicalTraitTypeArgKeys,
            canonicalImplementingType);

        if (TryGetConflictingImplRegistration(
                traitId,
                targetTypeId,
                canonicalImplementingType,
                canonicalTraitTypeArgs,
                traitRef.TypeArgTexts,
                requestedHeadShape,
                out var conflictingImpl))
        {
            var traitDisplay = FormatTraitReferenceDisplay(traitRef);
            var implementingTypeDisplay = NormalizeTypePath(implementingTypePath, selfType: null, traitTypeArgBindings: null);
            var requestedHead = FormatImplHeadDisplay(traitDisplay, implementingTypeDisplay);
            var conflictingHead = FormatImplHeadDisplay(
                BuildTraitDisplay(GetTraitName(conflictingImpl!.Trait), conflictingImpl.TraitTypeArgs),
                string.IsNullOrWhiteSpace(conflictingImpl.ImplementingTypeDisplay)
                    ? implementingTypeDisplay
                    : conflictingImpl.ImplementingTypeDisplay);
            var requestedCanonical = FormatImplHeadDisplay(
                BuildTraitDisplay(GetTraitName(traitId), canonicalTraitTypeArgs),
                canonicalImplementingType);
            var conflictingCanonical = FormatImplHeadDisplay(
                BuildTraitDisplay(GetTraitName(conflictingImpl.Trait), conflictingImpl.CanonicalTraitTypeArgs),
                string.IsNullOrWhiteSpace(conflictingImpl.CanonicalImplementingType)
                    ? canonicalImplementingType
                    : conflictingImpl.CanonicalImplementingType);
            var conflictingHeadShape = BuildImplHeadShape(conflictingImpl);
            var specializationRelation = ImplSpecializationComparer.CompareHeads(requestedHeadShape, conflictingHeadShape);
            _diagnostics.Add(
                BuildOverlappingImplRegistrationDiagnostic(
                    instance.Span,
                    requestedHead,
                    conflictingImpl,
                    conflictingHead,
                    requestedCanonical,
                    conflictingCanonical,
                    specializationRelation));
            return SymbolId.None;
        }

        var implId = _symbolTable.DeclareImpl(
            traitId,
            targetTypeId,
            instance.Span,
            traitRef.TypeArgTexts,
            NormalizeTypePath(implementingTypePath, selfType: null, traitTypeArgBindings: null),
            canonicalImplementingType,
            canonicalTraitTypeArgs,
            traitTypeArgKeys,
            canonicalTraitTypeArgKeys,
            implementingTypeRequirements,
            requestedHeadShape,
            BuildImplTypeRefKey(implementingTypePath),
            instance.SymbolId,
            GetSyntaxBindingName(instance, instance.Name));
        instance.SymbolId = implId;
        return implId;
    }

    private static bool IsSameNamedInstanceImplementingType(
        TypePath? leftPath,
        TypeId leftTypeId,
        TypePath rightPath,
        TypeId rightTypeId)
    {
        if (leftTypeId.IsValid && rightTypeId.IsValid && leftTypeId == rightTypeId)
        {
            return true;
        }

        return leftPath != null &&
               string.Equals(leftPath.TypeName, rightPath.TypeName, StringComparison.Ordinal) &&
               leftPath.ModulePath.SequenceEqual(rightPath.ModulePath, StringComparer.Ordinal);
    }
}
