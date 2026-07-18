using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void ResolveAdtDefReferences(AdtDef adt)
    {
        var hasTypeParams = adt.TypeParams.Count > 0;
        using var scopeGuard = hasTypeParams ? _symbolTable.PushScopeGuard(ScopeKind.Module) : default;
        if (hasTypeParams)
        {
            foreach (var typeParam in adt.TypeParams)
            {
                DeclareTypeParameterIfValid(typeParam);
                ResolveTypeParamReferences(typeParam);
            }

            if (_symbolTable.GetSymbol(adt.SymbolId) is AdtSymbol adtSymbol)
            {
                _symbolTable.UpdateSymbol(adtSymbol with
                {
                    TypeParams = adt.TypeParams.Select(typeParam => typeParam.SymbolId).ToList()
                });
            }

            if (adt.AliasTarget != null)
            {
                ResolveTypeReferences(adt.AliasTarget);
            }

            UpdateAdtAliasTargetSymbol(adt);

            ResolveAdtConstructorsAndClosedCases(adt);

            UpdateClosedCaseTypeParamSymbols(adt.Cases);
        }
        else
        {
            if (adt.AliasTarget != null)
            {
                ResolveTypeReferences(adt.AliasTarget);
            }

            UpdateAdtAliasTargetSymbol(adt);

            ResolveAdtConstructorsAndClosedCases(adt);

            UpdateClosedCaseTypeParamSymbols(adt.Cases);
        }
    }

    private void ResolveAdtConstructorsAndClosedCases(AdtDef adt)
    {
        if (adt.Members.Count > 0)
        {
            if (!ResolveTypeMemberRange(adt, 0))
            {
                return;
            }
            foreach (var constructor in adt.Constructors)
            {
                UpdateConstructorTypeParamSymbols(constructor);
            }
            return;
        }

        if (adt.Cases.Count == 0)
        {
            foreach (var constructor in adt.Constructors)
            {
                ResolveConstructorReferences(constructor);
                UpdateConstructorTypeParamSymbols(constructor);
            }
            return;
        }

        ResolveClosedCaseReferences(adt.Cases);
        foreach (var constructor in adt.Constructors)
        {
            UpdateConstructorTypeParamSymbols(constructor);
        }
    }

    private void ResolveClosedCaseReferences(IReadOnlyList<CaseTypeDef> cases)
    {
        foreach (var caseType in cases)
        {
            if (!ResolveClosedCaseReference(caseType))
            {
                return;
            }
        }
    }

    private bool ResolveClosedCaseReference(CaseTypeDef caseType)
    {
        using var scopeGuard = _symbolTable.PushScopeGuard(ScopeKind.Module);
        foreach (var typeParam in caseType.TypeParams)
        {
            DeclareTypeParameterIfValid(typeParam);
            ResolveTypeParamReferences(typeParam);
        }

        if (_symbolTable.GetSymbol<AdtSymbol>(caseType.SymbolId) is { } caseSymbol)
        {
            _symbolTable.UpdateSymbol(caseSymbol with
            {
                TypeParams = caseType.TypeParams.Select(static parameter => parameter.SymbolId).ToList()
            });
        }

        if (caseType.ParentSpecialization != null)
        {
            ResolveTypeReferences(caseType.ParentSpecialization);
        }

        if (_symbolTable.GetSymbol<AdtSymbol>(caseType.SymbolId) is { } resolvedCaseSymbol)
        {
            _symbolTable.UpdateSymbol(resolvedCaseSymbol with
            {
                CanonicalParentSpecialization = caseType.ParentSpecialization != null
                    ? CanonicalizeTypeNodeForImplHead(caseType.ParentSpecialization)
                    : BuildDefaultClosedCaseParentSpecialization(resolvedCaseSymbol.ParentAdt)
            });
        }

        foreach (var positionalField in caseType.PositionalFields)
        {
            ResolveTypeReferences(positionalField);
        }

        return caseType.Members.Count > 0
            ? ResolveTypeMemberRange(caseType, 0)
            : ResolveLegacyClosedCaseMembers(caseType);
    }

    private bool ResolveLegacyClosedCaseMembers(CaseTypeDef caseType)
    {
        foreach (var field in caseType.Fields)
        {
            if (field.Type != null)
            {
                ResolveTypeReferences(field.Type);
            }
        }

        ResolveClosedCaseReferences(caseType.Cases);
        return true;
    }

    private string BuildDefaultClosedCaseParentSpecialization(SymbolId parentId)
    {
        if (_symbolTable.GetSymbol<AdtSymbol>(parentId) is not { } parent)
        {
            return string.Empty;
        }

        var name = GetClosedCaseQualifiedName(parentId);
        var parameters = _symbolTable.GetClosedCaseEffectiveGenericParameterIds(parentId)
            .Select(_symbolTable.GetSymbol<TypeParamSymbol>)
            .OfType<TypeParamSymbol>()
            .Select(static parameter => parameter.Name)
            .ToArray();
        return parameters.Length == 0
            ? name
            : $"{name}[{string.Join(",", parameters)}]";
    }

    private string GetClosedCaseQualifiedName(SymbolId typeId)
    {
        var names = _symbolTable.GetClosedCaseAncestors(typeId)
            .Reverse()
            .Select(id => _symbolTable.GetSymbol<AdtSymbol>(id)?.Name)
            .Where(static name => !string.IsNullOrWhiteSpace(name));
        return string.Join(WellKnownStrings.Separators.Path, names);
    }

    private void UpdateConstructorTypeParamSymbols(Constructor constructor)
    {
        if (_symbolTable.GetSymbol<CtorSymbol>(constructor.SymbolId) is not { } symbol)
        {
            return;
        }

        _symbolTable.UpdateSymbol(symbol with
        {
            TypeParams = constructor.TypeParams.Select(static parameter => parameter.SymbolId).ToList()
        });
    }

    private void UpdateClosedCaseTypeParamSymbols(IReadOnlyList<CaseTypeDef> cases)
    {
        foreach (var caseType in cases)
        {
            if (_symbolTable.GetSymbol<AdtSymbol>(caseType.SymbolId) is { } caseSymbol)
            {
                _symbolTable.UpdateSymbol(caseSymbol with
                {
                    TypeParams = caseType.TypeParams.Select(static parameter => parameter.SymbolId).ToList()
                });
            }

            UpdateClosedCaseTypeParamSymbols(caseType.Cases);
        }
    }

    private void UpdateAdtAliasTargetSymbol(AdtDef adt)
    {
        if (adt.AliasTarget == null ||
            adt.SymbolId == SymbolId.None ||
            _symbolTable.GetSymbol(adt.SymbolId) is not AdtSymbol adtSymbol)
        {
            return;
        }

        if (TryResolveAliasTargetTypeId(adt.AliasTarget, out var aliasTargetTypeId))
        {
            _symbolTable.UpdateSymbol(adtSymbol with { AliasTarget = aliasTargetTypeId });
        }
    }

    private bool TryResolveAliasTargetTypeId(TypeNode aliasTarget, out TypeId aliasTargetTypeId)
    {
        aliasTargetTypeId = TypeId.None;

        if (aliasTarget is not TypePath typePath)
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

        aliasTargetTypeId = symbol.TypeId;
        return true;
    }

    private void ResolveConstructorReferences(Constructor ctor)
    {
        var hasTypeParams = ctor.TypeParams.Count > 0;
        using var scopeGuard = hasTypeParams ? _symbolTable.PushScopeGuard(ScopeKind.Module) : default;
        if (hasTypeParams)
        {
            foreach (var typeParam in ctor.TypeParams)
            {
                if (_symbolTable.CurrentScope?.GetLocalTypes().ContainsKey(typeParam.Name) == true ||
                    _symbolTable.CurrentScope?.Parent?.GetLocalTypes().ContainsKey(typeParam.Name) == true)
                {
                    AddError(typeParam.Span, $"Duplicate type parameter '{typeParam.Name}'.");
                    continue;
                }

                DeclareTypeParameterIfValid(typeParam);
                ResolveTypeParamReferences(typeParam);
            }
        }

        ResolveConstructorReferencesCore(ctor);
    }

    private void ResolveConstructorReferencesCore(Constructor ctor)
    {
        foreach (var argType in ctor.PositionalArgs)
        {
            ResolveTypeReferences(argType);
        }

        foreach (var field in ctor.NamedArgs)
        {
            if (field.Type != null)
            {
                ResolveTypeReferences(field.Type);
            }
        }

        if (ctor.ReturnType != null)
        {
            ResolveTypeReferences(ctor.ReturnType);
        }
    }

    private void ResolveEffectDefReferences(EffectDef ability)
    {
        _ = ability;
    }

    private void ResolveTraitDefReferences(TraitDef trait, bool resolveBodies = true)
    {
        var hasTypeParams = trait.TypeParams.Count > 0;
        using var scopeGuard = hasTypeParams ? _symbolTable.PushScopeGuard(ScopeKind.Module) : default;
        if (hasTypeParams)
        {
            foreach (var typeParam in trait.TypeParams)
            {
                DeclareTypeParameterIfValid(typeParam);
                ResolveTypeParamReferences(typeParam);
            }

            if (_symbolTable.GetSymbol(trait.SymbolId) is TraitSymbol traitSymbol)
            {
                _symbolTable.UpdateSymbol(traitSymbol with
                {
                    TypeParams = trait.TypeParams.Select(typeParam => typeParam.SymbolId).ToList()
                });
            }
        }

        if (trait.Members.Count > 0)
        {
            ResolveTraitMemberRange(trait, 0, resolveBodies);
            return;
        }

        _traitSignatureDepth++;
        try
        {
            foreach (var associatedType in trait.AssociatedTypes)
            {
                ResolveAssociatedTypeReferences(associatedType);
            }

            foreach (var associatedConst in trait.AssociatedConsts)
            {
                ResolveAssociatedConstReferences(associatedConst, resolveValue: resolveBodies);
            }

            foreach (var method in trait.Methods)
            {
                ResolveFuncDefReferences(method, resolveBodies);
            }
        }
        finally
        {
            _traitSignatureDepth--;
        }
    }

    private void ResolveAssociatedTypeReferences(AssociatedTypeDecl associatedType)
    {
        var hasTypeParams = associatedType.TypeParams.Count > 0;
        using var scopeGuard = hasTypeParams ? _symbolTable.PushScopeGuard(ScopeKind.Module) : default;
        if (hasTypeParams)
        {
            foreach (var typeParam in associatedType.TypeParams)
            {
                DeclareTypeParameterIfValid(typeParam);
                ResolveTypeParamReferences(typeParam);
            }
        }

        if (associatedType.ValueType != null)
        {
            ResolveTypeReferences(associatedType.ValueType);
        }
    }

    private void ResolveAssociatedConstReferences(AssociatedConstDecl associatedConst, bool resolveValue = true)
    {
        if (associatedConst.Type != null)
        {
            ResolveTypeReferences(associatedConst.Type);
        }

        if (resolveValue && associatedConst.Value != null)
        {
            ResolveExpressionReferences(associatedConst.Value);
        }
    }

    private void ResolveTypeReferences(TypeNode type)
    {
        switch (type)
        {
            case ExpandType expansion:
                ResolveExpandTypeReferences(expansion);
                break;

            case TypePath typePath:
                ResolveTypePathReference(typePath);
                break;

            case AssociatedTypeProjection projection:
                ResolveAssociatedTypeProjectionReference(projection);
                break;

            case ArrowType arrow:
                if (arrow.ParamType != null)
                {
                    ResolveTypeReferences(arrow.ParamType);
                }

                if (arrow.ReturnType != null)
                {
                    ResolveTypeReferences(arrow.ReturnType);
                }

                ResolveEffectRequirements(arrow.RequiredEffects);

                break;

            case EffectfulType effectful:
                var declaredEffectPaths = effectful.EnumerateEffectPaths()
                    .Select(path => path
                        .Where(part => !string.IsNullOrWhiteSpace(part))
                        .Select(part => part.Trim())
                        .ToList())
                    .Where(path => path.Count > 0)
                    .ToList();

                if (declaredEffectPaths.Count > 0)
                {
                    effectful.SymbolId = SymbolId.None;
                    effectful.EffectSymbolIds = [];
                    for (var i = 0; i < declaredEffectPaths.Count; i++)
                    {
                        var abilityPath = declaredEffectPaths[i];

                        // If the effect path is a single identifier that resolves to a type parameter,
                        // it's an ability-variable reference (e.g. ->{T} where T is a type param),
                        // not a concrete ability. Skip resolution and leave SymbolId.None for
                        // downstream passes (ability inferer) to handle.
                        if (abilityPath.Count == 1 &&
                            _symbolTable.LookupType(abilityPath[0]) is { } lookedUpId &&
                            lookedUpId.IsValid &&
                            _symbolTable.GetSymbol(lookedUpId) is TypeParamSymbol)
                        {
                            effectful.EffectSymbolIds.Add(SymbolId.None);
                            continue;
                        }

                        var abilitySymbolId = TryResolveEffectByPath(abilityPath, out var resolveError);
                        if (abilitySymbolId.HasValue && abilitySymbolId.Value.IsValid)
                        {
                            effectful.EffectSymbolIds.Add(abilitySymbolId.Value);
                            if (!effectful.SymbolId.IsValid)
                            {
                                effectful.SymbolId = abilitySymbolId.Value;
                            }
                        }
                        else
                        {
                            effectful.EffectSymbolIds.Add(SymbolId.None);
                            var displayName = string.Join(WellKnownStrings.Separators.Path, abilityPath);
                            if (!string.IsNullOrWhiteSpace(resolveError))
                            {
                                AddError(GetEffectPathErrorSpan(effectful, i), resolveError);
                            }
                            else
                            {
                                AddUndefinedEffectError(
                                    GetEffectPathErrorSpan(effectful, i),
                                    displayName,
                                    BuildUndefinedEffectDiagnostic(displayName));
                            }
                        }
                    }
                }

                if (effectful.InputType != null)
                {
                    ResolveTypeReferences(effectful.InputType);
                }

                if (effectful.OutputType != null)
                {
                    ResolveTypeReferences(effectful.OutputType);
                }

                break;

            case TupleType tuple:
                foreach (var elem in tuple.Elements)
                {
                    ResolveTypeReferences(elem);
                }

                break;

            // TypeParam 不是 TypeNode 的子类，需要单独处理
            // 在需要的地方直接调用 ResolveTypeParamReferences
        }
    }

    private void ResolveAssociatedTypeProjectionReference(AssociatedTypeProjection projection)
    {
        if (projection.Target == null)
        {
            AddError(projection.Span, $"Associated type projection '.{projection.MemberName}' requires a target trait application.");
            return;
        }

        ResolveTypeReferences(projection.Target);

        if (projection.Target.SymbolId.IsValid &&
            _symbolTable.GetSymbol<AdtSymbol>(projection.Target.SymbolId) is { } parentType &&
            _symbolTable.LookupDirectCase(parentType.Id, projection.MemberName) is { IsValid: true } caseTypeId)
        {
            projection.SymbolId = caseTypeId;
            if (projection.GenericArguments.Count > 0)
            {
                projection.SetGenericArguments(ResolveGenericArguments(
                    caseTypeId,
                    projection.GenericArguments,
                    projection.Span));
            }
            return;
        }

        if (projection.Target is not TypePath targetPath ||
            !targetPath.SymbolId.IsValid ||
            _symbolTable.GetSymbol(targetPath.SymbolId) is not TraitSymbol)
        {
            AddError(projection.Span, $"Associated type projection '.{projection.MemberName}' requires a trait type target.");
            return;
        }

        if (!_traitDefinitions.TryGetValue(targetPath.SymbolId, out var traitDefinition))
        {
            AddError(projection.Span, $"Trait definition for associated type projection '{targetPath.TypeName}.{projection.MemberName}' is unavailable.");
            return;
        }

        var associatedType = traitDefinition.AssociatedTypes
            .FirstOrDefault(item => string.Equals(item.Name, projection.MemberName, StringComparison.Ordinal));
        if (associatedType == null)
        {
            AddError(projection.Span, $"Trait '{traitDefinition.Name}' does not declare associated type '{projection.MemberName}'.");
            return;
        }

        projection.SymbolId = targetPath.SymbolId;
    }

    private void ResolveEffectRequirements(IReadOnlyList<EffectRequirementNode> requirements)
    {
        foreach (var requirement in requirements)
        {
            var abilityPath = NormalizeEffectRequirementPath(requirement.Path);
            if (abilityPath.Count == 0)
            {
                requirement.SymbolId = SymbolId.None;
                continue;
            }

            if (abilityPath.Count == 1 &&
                _symbolTable.LookupType(abilityPath[0]) is { } lookedUpId &&
                lookedUpId.IsValid &&
                _symbolTable.GetSymbol(lookedUpId) is TypeParamSymbol)
            {
                requirement.SymbolId = SymbolId.None;
                continue;
            }

            var abilitySymbolId = TryResolveEffectByPath(abilityPath, out var resolveError);
            if (abilitySymbolId.HasValue && abilitySymbolId.Value.IsValid)
            {
                requirement.SymbolId = abilitySymbolId.Value;
                continue;
            }

            requirement.SymbolId = SymbolId.None;
            var displayName = string.Join(WellKnownStrings.Separators.Path, abilityPath);
            if (!string.IsNullOrWhiteSpace(resolveError))
            {
                AddError(requirement.Span, resolveError);
            }
            else
            {
                AddUndefinedEffectError(
                    requirement.Span,
                    displayName,
                    BuildUndefinedEffectDiagnostic(displayName));
            }
        }
    }

    private static List<string> NormalizeEffectRequirementPath(IEnumerable<string> abilityPath)
    {
        return abilityPath
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim())
            .ToList();
    }

    private void ResolveTypePathReference(TypePath typePath)
    {
        if (TryUseAttachedSyntaxSymbol(typePath, out _))
        {
            if (typePath.GenericArguments.Count > 0)
            {
                typePath.SetGenericArguments(ResolveGenericArguments(
                    typePath.SymbolId,
                    typePath.GenericArguments,
                    typePath.Span));
            }
            else
            {
                foreach (var argument in typePath.TypeArgs)
                {
                    ResolveTypeReferences(argument);
                }
            }
            return;
        }

        if (_traitSignatureDepth > 0 &&
            typePath.ModulePath.Count == 0 &&
            string.Equals(typePath.TypeName, ReservedSelfTypeName, StringComparison.Ordinal))
        {
            // Trait 方法签名中的 Self 是合法占位符，不参与普通路径解析。
            return;
        }

        if (typePath.ModulePath.Count == 0 &&
            string.IsNullOrWhiteSpace(typePath.PackageAlias) &&
            typePath.TypeName == WellKnownStrings.BuiltinTypes.TypeEq)
        {
            foreach (var arg in typePath.TypeArgs)
            {
                ResolveTypeReferences(arg);
            }

            return;
        }

        if (HasUnresolvedHygienicSyntaxIdentity(typePath))
        {
            AddUnresolvedHygienicIdentifierError(typePath, typePath.TypeName);
            return;
        }

        var parts = typePath.ToQualifiedPathParts();

        var result = !string.IsNullOrWhiteSpace(typePath.PackageAlias)
            ? ResolvePackageQualifiedPath(typePath.PackageAlias, typePath.ModulePath.Concat([typePath.TypeName]).ToList(), TypeResolutionKinds)
            : ResolvePathWithImports(parts, TypeResolutionKinds);
        if (!result.IsSuccess && !string.IsNullOrWhiteSpace(typePath.PackageAlias))
        {
            result = ResolvePathWithImports(parts, TypeResolutionKinds);
        }

        if (result.IsSuccess)
        {
            typePath.SymbolId = result.SymbolId;
        }
        else
        {
            AddPathResolutionError(
                typePath.Span,
                parts,
                result.ErrorMessage ?? DiagnosticMessages.UndefinedType(typePath.TypeName),
                requireTypeLikeTarget: true);
        }

        if (typePath.GenericArguments.Count > 0)
        {
            typePath.SetGenericArguments(ResolveGenericArguments(
                typePath.SymbolId,
                typePath.GenericArguments,
                typePath.Span));
            return;
        }

        foreach (var arg in typePath.TypeArgs)
        {
            ResolveTypeReferences(arg);
        }
    }

    private static SourceSpan GetEffectPathErrorSpan(EffectfulType effectful, int effectIndex)
    {
        if (effectIndex >= 0 &&
            effectIndex < effectful.EffectPathSpans.Count &&
            HasSourceSpan(effectful.EffectPathSpans[effectIndex]))
        {
            return effectful.EffectPathSpans[effectIndex];
        }

        return effectful.Span;
    }

    private static bool HasSourceSpan(SourceSpan span)
    {
        return !string.IsNullOrWhiteSpace(span.FilePath) ||
               span.Length > 0 ||
               span.Location.Position > 0 ||
               span.Location.Line > 0 ||
               span.Location.Column > 0;
    }

    private void ResolveTypeParamReferences(TypeParam typeParam)
    {
        var resolvedTraitIds = new List<SymbolId>();

        if (typeParam.ComptimeTypeAnnotation != null &&
            !IsComptimeTypeMetaAnnotation(typeParam.ComptimeTypeAnnotation))
        {
            ResolveTypeReferences(typeParam.ComptimeTypeAnnotation);
        }

        foreach (var traitConstraint in typeParam.TraitConstraints)
        {
            ResolveTraitRefReferences(traitConstraint);
            if (!traitConstraint.SymbolId.IsValid)
            {
                if (IsBuiltinTraitConstraintName(traitConstraint.TraitName))
                {
                    continue;
                }

                var displayName = ComposeTraitRefDisplayName(traitConstraint);
                AddUndefinedTraitError(
                    traitConstraint.Span,
                    traitConstraint,
                    DiagnosticMessages.UndefinedTrait(displayName));
                continue;
            }

            var resolvedSymbol = _symbolTable.GetSymbol(traitConstraint.SymbolId);
            if (resolvedSymbol is EffectSymbol)
            {
                // Allow abilities as type-parameter constraints for ability polymorphism.
                // Store the ability's SymbolId in the resolved trait IDs so downstream passes
                // can detect ability-constrained type parameters.
                if (!resolvedTraitIds.Contains(traitConstraint.SymbolId))
                {
                    resolvedTraitIds.Add(traitConstraint.SymbolId);
                }
                continue;
            }

            if (resolvedSymbol is not TraitSymbol)
            {
                var displayName = ComposeTraitRefDisplayName(traitConstraint);
                AddError(traitConstraint.Span, DiagnosticMessages.DisplayNameIsNotTrait(displayName));
                continue;
            }

            if (!resolvedTraitIds.Contains(traitConstraint.SymbolId))
            {
                resolvedTraitIds.Add(traitConstraint.SymbolId);
            }
        }

        if (!typeParam.SymbolId.IsValid ||
            _symbolTable.GetSymbol(typeParam.SymbolId) is not TypeParamSymbol typeParamSymbol)
        {
            return;
        }

        _symbolTable.UpdateSymbol(typeParamSymbol with
        {
            TraitConstraints = resolvedTraitIds
        });
    }

    private static bool IsComptimeTypeMetaAnnotation(TypeNode typeAnnotation)
    {
        return typeAnnotation is TypePath
        {
            PackageAlias: null,
            ModulePath.Count: 0,
            TypeName: WellKnownStrings.BuiltinTypes.Type,
            TypeArgs.Count: 0
        };
    }

    private void ResolveTraitRefReferences(TraitRef traitRef)
    {
        var pathParts = new List<string>(traitRef.ModulePath);
        if (!string.IsNullOrWhiteSpace(traitRef.TraitName))
        {
            pathParts.Add(traitRef.TraitName);
        }

        if (pathParts.Count == 0)
        {
            traitRef.SymbolId = SymbolId.None;
            return;
        }

        var result = ResolvePathWithImports(pathParts);
        traitRef.SymbolId = result.IsSuccess ? result.SymbolId : SymbolId.None;

        if (traitRef.GenericArguments.Count > 0)
        {
            traitRef.SetGenericArguments(ResolveGenericArguments(
                traitRef.SymbolId,
                traitRef.GenericArguments,
                traitRef.Span));
            return;
        }

        foreach (var typeArg in traitRef.TypeArgs)
        {
            ResolveTypeReferences(typeArg);
        }
    }

    private static string ComposeTraitRefDisplayName(TraitRef traitRef)
    {
        if (traitRef.ModulePath.Count == 0)
        {
            return traitRef.TraitName;
        }

        return $"{string.Join(WellKnownStrings.Separators.Path, traitRef.ModulePath)}::{traitRef.TraitName}";
    }

    private static bool IsBuiltinTraitConstraintName(string traitName)
    {
        return traitName is BuiltinTraits.TraitNames.Eq
            or BuiltinTraits.TraitNames.Ord
            or BuiltinTraits.TraitNames.Num
            or BuiltinTraits.TraitNames.Show
            or BuiltinTraits.TraitNames.Clone;
    }
}
