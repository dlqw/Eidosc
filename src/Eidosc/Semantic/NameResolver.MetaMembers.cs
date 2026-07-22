using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Symbols;
using Eidosc.Syntax;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private bool TryValidateGeneratedMembers(
        Declaration target,
        IReadOnlyList<MaterializedMetaNode> members,
        out string reason)
    {
        reason = string.Empty;
        if (members.Count == 0)
        {
            return true;
        }

        return target switch
        {
            AdtDef adt => TryValidateGeneratedTypeMembers(adt, members, out reason),
            CaseTypeDef caseType => TryValidateGeneratedTypeMembers(caseType, members, out reason),
            TraitDef trait => TryValidateGeneratedAssociatedMembers(
                trait.Name,
                trait.Methods,
                trait.AssociatedTypes,
                trait.AssociatedConsts,
                members,
                out reason),
            InstanceDecl instance => TryValidateGeneratedAssociatedMembers(
                instance.Name,
                instance.Methods,
                instance.AssociatedTypes,
                instance.AssociatedConsts,
                members,
                out reason),
            ModuleDecl => TryValidateGeneratedModuleMembers(members, out reason),
            _ => FailGeneratedMemberValidation(
                $"{target.GetType().Name} does not own addable syntax members",
                out reason)
        };
    }

    private bool TryValidateGeneratedTypeMembers(
        Declaration target,
        IReadOnlyList<MaterializedMetaNode> members,
        out string reason,
        CaseTypeDef? replacedCase = null)
    {
        var existingFields = target switch
        {
            AdtDef adt => adt.Fields,
            CaseTypeDef caseType => caseType.Fields,
            _ => []
        };
        var existingCases = target switch
        {
            AdtDef adt => adt.Cases,
            CaseTypeDef caseType => caseType.Cases,
            _ => []
        };
        var fieldNames = existingFields.Select(static field => field.Name).ToHashSet(StringComparer.Ordinal);
        var caseNames = existingCases
            .Where(caseType => !ReferenceEquals(caseType, replacedCase))
            .Select(static caseType => caseType.Name)
            .ToHashSet(StringComparer.Ordinal);

        if (!TryGetGeneratedCaseContext(target, out var root, out var inheritedFields, out _, out _))
        {
            reason = $"generated type members cannot locate the closed-case root for {GetMetaTargetName(target)}";
            return false;
        }

        var inheritedFieldNames = inheritedFields
            .Select(static field => field.Name)
            .ToHashSet(StringComparer.Ordinal);
        var replacedConstructors = replacedCase == null
            ? null
            : EnumerateLeafCases(replacedCase)
                .Select(static leaf => leaf.ConstructorSymbolId)
                .ToHashSet();
        var constructorNames = root.Constructors
            .Where(constructor => replacedConstructors == null || !replacedConstructors.Contains(constructor.SymbolId))
            .Select(static constructor => constructor.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (target is CaseTypeDef { IsLeaf: true } leaf)
        {
            constructorNames.Remove(leaf.Name);
        }

        foreach (var materialized in members)
        {
            switch (materialized.Node)
            {
                case ExpandDeclaration { SiteCategory: SyntaxCategory.Member }:
                    break;

                case Field field:
                    if (!TryValidateGeneratedMemberName(field.Name, "field", out reason) ||
                        !fieldNames.Add(field.Name))
                    {
                        reason = string.IsNullOrEmpty(reason)
                            ? $"generated field '{field.Name}' collides with an existing member of {GetMetaTargetName(target)}"
                            : reason;
                        return false;
                    }
                    inheritedFieldNames.Add(field.Name);
                    break;

                case CaseTypeDef caseType:
                    if (!caseNames.Add(caseType.Name))
                    {
                        reason = $"generated case type '{caseType.Name}' collides with an existing direct case of {GetMetaTargetName(target)}";
                        return false;
                    }
                    if (!TryValidateGeneratedCaseTree(
                            caseType,
                            inheritedFieldNames,
                            constructorNames,
                            out reason))
                    {
                        return false;
                    }
                    break;

                default:
                    reason = $"{materialized.Node.GetType().Name} is not a legal type member; expected Field or CaseTypeDef";
                    return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryValidateGeneratedCaseTree(
        CaseTypeDef caseType,
        IReadOnlySet<string> inheritedFieldNames,
        HashSet<string> constructorNames,
        out string reason)
    {
        if (!TryValidateGeneratedMemberName(caseType.Name, "case type", out reason))
        {
            return false;
        }

        var effectiveFieldNames = inheritedFieldNames.ToHashSet(StringComparer.Ordinal);
        foreach (var field in caseType.Fields)
        {
            if (!TryValidateGeneratedMemberName(field.Name, "field", out reason) ||
                !effectiveFieldNames.Add(field.Name))
            {
                reason = string.IsNullOrEmpty(reason)
                    ? $"generated field '{field.Name}' duplicates an inherited field in case type '{caseType.Name}'"
                    : reason;
                return false;
            }
        }

        if (caseType.IsLeaf)
        {
            if (!constructorNames.Add(caseType.Name))
            {
                reason = $"generated leaf case '{caseType.Name}' collides with an existing constructor";
                return false;
            }
            return true;
        }

        var directCaseNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var child in caseType.Cases)
        {
            if (!directCaseNames.Add(child.Name))
            {
                reason = $"generated case type '{caseType.Name}' contains duplicate direct case '{child.Name}'";
                return false;
            }
            if (!TryValidateGeneratedCaseTree(child, effectiveFieldNames, constructorNames, out reason))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryValidateGeneratedAssociatedMembers(
        string targetName,
        IReadOnlyList<FuncDef> existingMethods,
        IReadOnlyList<AssociatedTypeDecl> existingTypes,
        IReadOnlyList<AssociatedConstDecl> existingConsts,
        IReadOnlyList<MaterializedMetaNode> members,
        out string reason,
        FuncDef? replacedMethod = null)
    {
        var methodSignatures = existingMethods
            .Where(method => !ReferenceEquals(method, replacedMethod))
            .Select(static method => BuildFunctionOverloadSignatureKey(
                method.Name,
                method.Signature,
                method.TypeParams))
            .ToHashSet(StringComparer.Ordinal);
        var associatedNames = existingTypes.Select(static member => member.Name)
            .Concat(existingConsts.Select(static member => member.Name))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var materialized in members)
        {
            switch (materialized.Node)
            {
                case ExpandDeclaration { SiteCategory: SyntaxCategory.Member }:
                    break;

                case FuncDef method:
                    if (!TryValidateGeneratedMemberName(method.Name, "method", out reason))
                    {
                        return false;
                    }
                    var signature = BuildFunctionOverloadSignatureKey(
                        method.Name,
                        method.Signature,
                        method.TypeParams);
                    if (!methodSignatures.Add(signature))
                    {
                        reason = $"generated method '{method.Name}' duplicates an existing overload in '{targetName}'";
                        return false;
                    }
                    break;

                case AssociatedTypeDecl associatedType:
                    if (!TryValidateGeneratedMemberName(associatedType.Name, "associated type", out reason) ||
                        !associatedNames.Add(associatedType.Name))
                    {
                        reason = string.IsNullOrEmpty(reason)
                            ? $"generated associated member '{associatedType.Name}' collides in '{targetName}'"
                            : reason;
                        return false;
                    }
                    break;

                case AssociatedConstDecl associatedConst:
                    if (!TryValidateGeneratedMemberName(associatedConst.Name, "associated const", out reason) ||
                        !associatedNames.Add(associatedConst.Name))
                    {
                        reason = string.IsNullOrEmpty(reason)
                            ? $"generated associated member '{associatedConst.Name}' collides in '{targetName}'"
                            : reason;
                        return false;
                    }
                    break;

                default:
                    reason = $"{materialized.Node.GetType().Name} is not a legal trait or instance member";
                    return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryValidateGeneratedModuleMembers(
        IReadOnlyList<MaterializedMetaNode> members,
        out string reason)
    {
        foreach (var member in members)
        {
            if (member.Node is not Declaration declaration)
            {
                reason = $"{member.Node.GetType().Name} is not a legal module member declaration";
                return false;
            }

            var name = GetGeneratedDeclarationName(declaration);
            if (!string.IsNullOrEmpty(name) &&
                !TryValidateGeneratedMemberName(name, "module declaration", out reason))
            {
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool TryApplyGeneratedMember(
        MetaInvocationOccurrence invocation,
        EidosAstNode node,
        GeneratedDeclarationOrigin origin,
        Dictionary<SymbolId, FuncDef> functions,
        out string reason)
    {
        return invocation.Target switch
        {
            AdtDef adt => TryApplyGeneratedTypeMember(adt, node, origin, out reason),
            CaseTypeDef caseType => TryApplyGeneratedTypeMember(caseType, node, origin, out reason),
            TraitDef trait => TryApplyGeneratedTraitMember(trait, node, origin, functions, out reason),
            InstanceDecl instance => TryApplyGeneratedInstanceMember(instance, node, origin, functions, out reason),
            ModuleDecl module => TryApplyGeneratedModuleMember(
                invocation.ModuleId,
                module,
                node,
                origin,
                functions,
                out reason),
            _ => FailGeneratedMemberValidation(
                $"{invocation.Target.GetType().Name} does not own addable syntax members",
                out reason)
        };
    }

    private bool TryApplyGeneratedTypeMember(
        Declaration target,
        EidosAstNode node,
        GeneratedDeclarationOrigin origin,
        out string reason,
        int? insertionIndex = null)
    {
        if (!TryGetGeneratedCaseContext(
                target,
                out var root,
                out var inheritedFields,
                out var inheritedTypeParams,
                out var targetPath))
        {
            reason = $"generated type member target '{GetMetaTargetName(target)}' has no closed-case context";
            return false;
        }

        var ownerId = target.SymbolId;
        var isPublic = _symbolTable.GetSymbol(ownerId)?.IsPublic ?? true;
        switch (node)
        {
            case Field field:
            {
                var ownerFields = target is AdtDef adt ? adt.Fields : ((CaseTypeDef)target).Fields;
                var fieldIndex = insertionIndex ?? ownerFields.Count;
                var updatedFields = ownerFields.ToList();
                updatedFields.Insert(fieldIndex, field);
                if (target is AdtDef fieldOwner)
                {
                    fieldOwner.SetFields(updatedFields);
                    if (insertionIndex == null)
                    {
                        fieldOwner.AppendMember(field);
                    }
                }
                else
                {
                    var caseOwner = (CaseTypeDef)target;
                    caseOwner.SetFields(updatedFields);
                    if (insertionIndex == null)
                    {
                        caseOwner.AppendMember(field);
                    }
                }

                field.SymbolId = _symbolTable.DeclareField(
                    GetSyntaxBindingName(field, field.Name),
                    field.Span,
                    ownerId,
                    fieldIndex,
                    isPublic);
                RegisterSyntaxIdentitySymbol(field, field.SymbolId);
                SetGeneratedOrigin(field.SymbolId, origin);
                for (var index = fieldIndex + 1; index < updatedFields.Count; index++)
                {
                    var existing = updatedFields[index];
                    if (_symbolTable.GetSymbol<FieldSymbol>(existing.SymbolId) is { } symbol)
                    {
                        _symbolTable.UpdateSymbol(symbol with { Index = index });
                    }
                }
                var projectedFieldIndex = inheritedFields.Count - ownerFields.Count + fieldIndex;
                AddFieldToProjectedConstructors(root, target, field, projectedFieldIndex);
                reason = string.Empty;
                return true;
            }

            case CaseTypeDef caseType:
            {
                var targetWasLeaf = target is CaseTypeDef { IsLeaf: true };
                var rootHadNoCases = target is AdtDef { Cases.Count: 0 };
                var ownerCases = target is AdtDef adt ? adt.Cases : ((CaseTypeDef)target).Cases;
                var caseIndex = insertionIndex ?? ownerCases.Count;
                var constructorIndex = FindProjectedConstructorInsertionIndex(
                    root,
                    target,
                    ownerCases,
                    caseIndex,
                    targetWasLeaf);
                if (rootHadNoCases)
                {
                    constructorIndex = 0;
                }
                if (targetWasLeaf)
                {
                    RemoveLeafConstructor(root, (CaseTypeDef)target);
                }
                else if (rootHadNoCases)
                {
                    RemoveSynthesizedProductConstructor(root);
                }

                var updatedCases = ownerCases.ToList();
                updatedCases.Insert(caseIndex, caseType);
                if (target is AdtDef caseOwner)
                {
                    caseOwner.SetCases(updatedCases);
                    if (insertionIndex == null)
                    {
                        caseOwner.AppendMember(caseType);
                    }
                }
                else
                {
                    var nestedCaseOwner = (CaseTypeDef)target;
                    nestedCaseOwner.SetCases(updatedCases);
                    if (insertionIndex == null)
                    {
                        nestedCaseOwner.AppendMember(caseType);
                    }
                }

                var projectedConstructors = ClosedCaseConstructorProjection.Create(
                    caseType,
                    inheritedFields,
                    inheritedTypeParams);
                AttachGeneratedOriginChain(caseType, caseType.GeneratedOriginChain);
                foreach (var projectedConstructor in projectedConstructors)
                {
                    AttachGeneratedOriginChain(projectedConstructor, caseType.GeneratedOriginChain);
                }
                var updatedConstructors = root.Constructors.ToList();
                updatedConstructors.InsertRange(constructorIndex, projectedConstructors);
                root.SetConstructors(updatedConstructors);
                var leaves = new List<CaseTypeDef>();
                CollectCaseTypeTree(caseType, root.SymbolId, ownerId, isPublic, leaves);
                if (leaves.Count != projectedConstructors.Count)
                {
                    reason = $"generated case projection mismatch for '{caseType.Name}'";
                    return false;
                }

                for (var index = 0; index < leaves.Count; index++)
                {
                    var leaf = leaves[index];
                    var constructor = projectedConstructors[index];
                    EidosAstNode constructorIdentityOwner = constructor.AttachedSyntaxIdentity == null ? leaf : constructor;
                    var constructorId = _symbolTable.DeclareConstructor(
                        GetSyntaxBindingName(constructorIdentityOwner, constructor.Name),
                        constructor.Span,
                        leaf.SymbolId,
                        isPublic);
                    constructor.SymbolId = constructorId;
                    RegisterSyntaxIdentitySymbol(constructorIdentityOwner, constructorId);
                    leaf.ConstructorSymbolId = constructorId;
                    AddClosedCaseConstructorToAncestors(leaf.SymbolId, constructorId);
                    _symbolTable.AddMemberToModule(_currentModule, constructorId);
                    _ctorPatternShapes[constructorId] = BuildCtorPatternShape(constructor);
                    if (_symbolTable.GetSymbol<AdtSymbol>(leaf.SymbolId) is { } leafSymbol)
                    {
                        _symbolTable.UpdateSymbol(leafSymbol with { CaseConstructor = constructorId });
                    }
                    SetGeneratedOrigin(constructorId, origin);
                }

                SetGeneratedCaseTreeOrigins(caseType, origin);
                ProcessCaseMetaClauses(root, caseType, [.. targetPath, caseType.Name]);
                reason = string.Empty;
                return true;
            }

            default:
                reason = $"{node.GetType().Name} is not a legal type member";
                return false;
        }
    }

    private bool TryApplyGeneratedTraitMember(
        TraitDef trait,
        EidosAstNode node,
        GeneratedDeclarationOrigin origin,
        Dictionary<SymbolId, FuncDef> functions,
        out string reason,
        int? insertionIndex = null)
    {
        switch (node)
        {
            case FuncDef method:
                var methods = trait.Methods.ToList();
                var methodIndex = insertionIndex ?? methods.Count;
                methods.Insert(methodIndex, method);
                trait.SetMethods(methods);
                if (insertionIndex == null)
                {
                    trait.AppendMember(method);
                }
                if (!TryCollectGeneratedTraitMethod(trait, method, origin, out reason, methodIndex))
                {
                    return false;
                }
                functions[method.SymbolId] = method;
                return true;

            case AssociatedTypeDecl associatedType:
                CollectAssociatedTypeSymbol(
                    associatedType,
                    trait.SymbolId,
                    SymbolId.None,
                    _symbolTable.GetSymbol(trait.SymbolId)?.IsPublic ?? true,
                    origin);
                var associatedTypes = trait.AssociatedTypes.ToList();
                associatedTypes.Insert(insertionIndex ?? associatedTypes.Count, associatedType);
                trait.SetAssociatedTypes(associatedTypes);
                if (insertionIndex == null)
                {
                    trait.AppendMember(associatedType);
                }
                reason = string.Empty;
                return true;

            case AssociatedConstDecl associatedConst:
                CollectAssociatedConstSymbol(
                    associatedConst,
                    trait.SymbolId,
                    SymbolId.None,
                    _symbolTable.GetSymbol(trait.SymbolId)?.IsPublic ?? true,
                    origin);
                var associatedConsts = trait.AssociatedConsts.ToList();
                associatedConsts.Insert(insertionIndex ?? associatedConsts.Count, associatedConst);
                trait.SetAssociatedConsts(associatedConsts);
                if (insertionIndex == null)
                {
                    trait.AppendMember(associatedConst);
                }
                reason = string.Empty;
                return true;

            default:
                reason = $"{node.GetType().Name} is not a legal trait member";
                return false;
        }
    }

    private bool TryCollectGeneratedTraitMethod(
        TraitDef trait,
        FuncDef method,
        GeneratedDeclarationOrigin origin,
        out string reason,
        int? insertionIndex = null)
    {
        var binding = DeclarationClauseBinder.Bind(method, LanguageVersion, CompilerOwnedSourceGrant.None);
        method.SetBoundClauses(binding.Clauses, binding.MetaInvocations);
        if (binding.Diagnostics.Count > 0)
        {
            reason = string.Join("; ", binding.Diagnostics.Select(static diagnostic => diagnostic.Message));
            return false;
        }

        var isPublic = _symbolTable.GetSymbol(trait.SymbolId)?.IsPublic ?? true;
        var hasDefaultBody = method.Body.Count > 0;
        var methodId = _symbolTable.DeclareFunction(
            GetSyntaxBindingName(method, method.Name),
            method.Span,
            hasBody: hasDefaultBody,
            isPublic,
            method.IsComptime);
        method.SymbolId = methodId;
        RegisterSyntaxIdentitySymbol(method, methodId);
        RegisterGenericParameterKinds(methodId, method.TypeParams);
        UpdateFunctionSymbolSignature(methodId, GetDeclaredArity(method, defaultUnaryWhenUnknown: true));
        var selfUsage = AnalyzeTraitMethodSelfUsage(trait, method);
        if (_symbolTable.GetSymbol<FuncSymbol>(methodId) is { } methodSymbol)
        {
            _symbolTable.UpdateSymbol(methodSymbol with
            {
                DefinitionModuleId = _currentModule,
                OwnerTrait = trait.SymbolId,
                TraitSelfPosition = selfUsage.Position,
                TraitSelfParameterIndices = selfUsage.ParameterIndices,
                TraitSelfInResult = selfUsage.InResult,
                TraitMethodRole = ResolveTraitMethodRole(trait, method),
                IsDefaultImplementation = hasDefaultBody,
                GeneratedOrigin = origin
            });
        }

        if (_symbolTable.GetSymbol<TraitSymbol>(trait.SymbolId) is { } traitSymbol)
        {
            var methods = traitSymbol.Methods.ToList();
            methods.Insert(insertionIndex ?? methods.Count, methodId);
            _symbolTable.UpdateSymbol(traitSymbol with
            {
                Methods = methods,
                SelfPosition = DeriveTraitSelfPosition(trait)
            });
        }

        _declarationsBySymbol[methodId] = method;
        ProcessDeclarationMetaClauses(method, deriveShape: null, method.Name, [trait.Name, method.Name]);
        reason = string.Empty;
        return true;
    }

    private bool TryApplyGeneratedInstanceMember(
        InstanceDecl instance,
        EidosAstNode node,
        GeneratedDeclarationOrigin origin,
        Dictionary<SymbolId, FuncDef> functions,
        out string reason,
        int? insertionIndex = null)
    {
        switch (node)
        {
            case FuncDef method:
                var binding = DeclarationClauseBinder.Bind(method, LanguageVersion, CompilerOwnedSourceGrant.None);
                method.SetBoundClauses(binding.Clauses, binding.MetaInvocations);
                if (binding.Diagnostics.Count > 0)
                {
                    reason = string.Join("; ", binding.Diagnostics.Select(static diagnostic => diagnostic.Message));
                    return false;
                }

                var methods = instance.Methods.ToList();
                methods.Insert(insertionIndex ?? methods.Count, method);
                instance.SetMethods(methods);
                if (insertionIndex == null)
                {
                    instance.AppendMember(method);
                }
                _instanceMethodDeclarationDepth++;
                try
                {
                    CollectFuncDef(method);
                }
                finally
                {
                    _instanceMethodDeclarationDepth--;
                }
                if (!method.SymbolId.IsValid)
                {
                    instance.SetMethods(instance.Methods.Where(candidate => !ReferenceEquals(candidate, method)).ToList());
                    reason = $"generated instance method '{method.Name}' could not be collected";
                    return false;
                }
                SetGeneratedOrigin(method.SymbolId, origin);
                _declarationsBySymbol[method.SymbolId] = method;
                functions[method.SymbolId] = method;
                ProcessDeclarationMetaClauses(
                    method,
                    deriveShape: null,
                    method.Name,
                    [instance.Name, method.Name]);
                reason = string.Empty;
                return true;

            case AssociatedTypeDecl associatedType:
                CollectAssociatedTypeSymbol(
                    associatedType,
                    SymbolId.None,
                    instance.SymbolId,
                    _symbolTable.GetSymbol(instance.SymbolId)?.IsPublic ?? true,
                    origin);
                var associatedTypes = instance.AssociatedTypes.ToList();
                associatedTypes.Insert(insertionIndex ?? associatedTypes.Count, associatedType);
                instance.SetAssociatedTypes(associatedTypes);
                if (insertionIndex == null)
                {
                    instance.AppendMember(associatedType);
                }
                reason = string.Empty;
                return true;

            case AssociatedConstDecl associatedConst:
                CollectAssociatedConstSymbol(
                    associatedConst,
                    SymbolId.None,
                    instance.SymbolId,
                    _symbolTable.GetSymbol(instance.SymbolId)?.IsPublic ?? true,
                    origin);
                var associatedConsts = instance.AssociatedConsts.ToList();
                associatedConsts.Insert(insertionIndex ?? associatedConsts.Count, associatedConst);
                instance.SetAssociatedConsts(associatedConsts);
                if (insertionIndex == null)
                {
                    instance.AppendMember(associatedConst);
                }
                reason = string.Empty;
                return true;

            default:
                reason = $"{node.GetType().Name} is not a legal instance member";
                return false;
        }
    }

    private bool TryApplyGeneratedModuleMember(
        SymbolId moduleId,
        ModuleDecl module,
        EidosAstNode node,
        GeneratedDeclarationOrigin origin,
        Dictionary<SymbolId, FuncDef> functions,
        out string reason)
    {
        if (node is not Declaration declaration)
        {
            reason = $"{node.GetType().Name} is not a legal module member declaration";
            return false;
        }

        module.Declarations.Add(declaration);
        CollectDeclaration(declaration, isGeneratedSource: true);
        if (!declaration.SymbolId.IsValid)
        {
            reason = $"generated module member '{GetGeneratedDeclarationName(declaration)}' could not be collected";
            return false;
        }

        _symbolTable.AddMemberToModule(moduleId, declaration.SymbolId);
        SetGeneratedOrigin(declaration.SymbolId, origin);
        if (declaration is FuncDef function)
        {
            functions[function.SymbolId] = function;
        }
        reason = string.Empty;
        return true;
    }

    private bool TryGetGeneratedCaseContext(
        Declaration target,
        out AdtDef root,
        out IReadOnlyList<Field> inheritedFields,
        out IReadOnlyList<TypeParam> inheritedTypeParams,
        out IReadOnlyList<string> targetPath)
    {
        if (target is AdtDef adt)
        {
            root = adt;
            inheritedFields = adt.Fields;
            inheritedTypeParams = [];
            targetPath = [adt.Name];
            return true;
        }

        if (target is not CaseTypeDef requestedCase)
        {
            root = null!;
            inheritedFields = [];
            inheritedTypeParams = [];
            targetPath = [];
            return false;
        }

        var rootId = requestedCase.SymbolId;
        var visited = new HashSet<SymbolId>();
        while (rootId.IsValid && visited.Add(rootId) &&
               _symbolTable.GetSymbol<AdtSymbol>(rootId) is { } symbol &&
               symbol.ParentAdt.IsValid)
        {
            rootId = symbol.ParentAdt;
        }
        if (!_adtDefinitions.TryGetValue(rootId, out var foundRoot) ||
            !TryFindCasePath(foundRoot, requestedCase, out var casePath))
        {
            root = null!;
            inheritedFields = [];
            inheritedTypeParams = [];
            targetPath = [];
            return false;
        }

        root = foundRoot;
        var fields = new List<Field>(root.Fields);
        var typeParams = new List<TypeParam>();
        foreach (var pathCase in casePath)
        {
            fields.AddRange(pathCase.Fields);
            typeParams.AddRange(pathCase.TypeParams);
        }
        inheritedFields = fields;
        inheritedTypeParams = typeParams;
        targetPath = [root.Name, .. casePath.Select(static caseType => caseType.Name)];
        return true;
    }

    private static bool TryFindCasePath(
        AdtDef root,
        CaseTypeDef target,
        out IReadOnlyList<CaseTypeDef> path)
    {
        var current = new List<CaseTypeDef>();
        foreach (var caseType in root.Cases)
        {
            if (TryFindCasePath(caseType, target, current))
            {
                path = current.ToArray();
                return true;
            }
        }
        path = [];
        return false;
    }

    private static bool TryFindCasePath(
        CaseTypeDef current,
        CaseTypeDef target,
        List<CaseTypeDef> path)
    {
        path.Add(current);
        if (ReferenceEquals(current, target))
        {
            return true;
        }
        foreach (var child in current.Cases)
        {
            if (TryFindCasePath(child, target, path))
            {
                return true;
            }
        }
        path.RemoveAt(path.Count - 1);
        return false;
    }

    private static int FindProjectedConstructorInsertionIndex(
        AdtDef root,
        Declaration target,
        IReadOnlyList<CaseTypeDef> ownerCases,
        int caseIndex,
        bool targetWasLeaf)
    {
        if (targetWasLeaf && target is CaseTypeDef leaf)
        {
            var leafIndex = root.Constructors.FindIndex(constructor =>
                constructor.SymbolId == leaf.ConstructorSymbolId);
            return leafIndex < 0 ? root.Constructors.Count : leafIndex;
        }

        if (caseIndex >= ownerCases.Count)
        {
            return root.Constructors.Count;
        }

        var followingLeaf = EnumerateLeafCases(ownerCases[caseIndex]).FirstOrDefault();
        if (followingLeaf == null)
        {
            return root.Constructors.Count;
        }

        var index = root.Constructors.FindIndex(constructor =>
            constructor.SymbolId == followingLeaf.ConstructorSymbolId);
        return index < 0 ? root.Constructors.Count : index;
    }

    private void AddFieldToProjectedConstructors(
        AdtDef root,
        Declaration target,
        Field field,
        int projectedFieldIndex)
    {
        HashSet<SymbolId>? descendantConstructors = null;
        if (target is CaseTypeDef caseType)
        {
            descendantConstructors = EnumerateLeafCases(caseType)
                .Select(static leaf => leaf.ConstructorSymbolId)
                .Where(static id => id.IsValid)
                .ToHashSet();
        }

        foreach (var constructor in root.Constructors)
        {
            if (descendantConstructors != null && !descendantConstructors.Contains(constructor.SymbolId))
            {
                continue;
            }
            constructor.InsertNamedArg(projectedFieldIndex, field);
            if (constructor.SymbolId.IsValid)
            {
                _ctorPatternShapes[constructor.SymbolId] = BuildCtorPatternShape(constructor);
            }
        }
    }

    private static IEnumerable<CaseTypeDef> EnumerateLeafCases(CaseTypeDef caseType)
    {
        if (caseType.IsLeaf)
        {
            yield return caseType;
            yield break;
        }
        foreach (var child in caseType.Cases)
        {
            foreach (var leaf in EnumerateLeafCases(child))
            {
                yield return leaf;
            }
        }
    }

    private void RemoveLeafConstructor(AdtDef root, CaseTypeDef leaf)
    {
        var constructorId = leaf.ConstructorSymbolId;
        if (!constructorId.IsValid)
        {
            return;
        }

        var constructor = root.Constructors.FirstOrDefault(candidate => candidate.SymbolId == constructorId);
        root.SetConstructors(root.Constructors.Where(candidate => candidate.SymbolId != constructorId).ToList());
        RemoveConstructorFromAncestors(leaf.SymbolId, constructorId);
        _symbolTable.Modules.RemoveMemberFromModule(_currentModule, constructorId);
        _symbolTable.RemoveSymbol(constructorId);
        _ctorPatternShapes.Remove(constructorId);
        leaf.ConstructorSymbolId = SymbolId.None;
        if (constructor != null)
        {
            constructor.SymbolId = SymbolId.None;
        }
        if (_symbolTable.GetSymbol<AdtSymbol>(leaf.SymbolId) is { } leafSymbol)
        {
            _symbolTable.UpdateSymbol(leafSymbol with { CaseConstructor = SymbolId.None });
        }
    }

    private void RemoveSynthesizedProductConstructor(AdtDef root)
    {
        if (root.Constructors.Count != 1 ||
            !string.Equals(root.Constructors[0].Name, root.Name, StringComparison.Ordinal))
        {
            return;
        }

        var constructor = root.Constructors[0];
        root.SetConstructors([]);
        if (!constructor.SymbolId.IsValid)
        {
            return;
        }
        RemoveConstructorFromAncestors(root.SymbolId, constructor.SymbolId);
        _symbolTable.Modules.RemoveMemberFromModule(_currentModule, constructor.SymbolId);
        _symbolTable.RemoveSymbol(constructor.SymbolId);
        _ctorPatternShapes.Remove(constructor.SymbolId);
        constructor.SymbolId = SymbolId.None;
    }

    private void RemoveConstructorFromAncestors(SymbolId start, SymbolId constructorId)
    {
        var current = start;
        var visited = new HashSet<SymbolId>();
        while (current.IsValid && visited.Add(current))
        {
            if (_symbolTable.GetSymbol<AdtSymbol>(current) is not { } adt)
            {
                break;
            }
            _symbolTable.UpdateSymbol(adt with
            {
                Constructors = adt.Constructors.Where(id => id != constructorId).ToList()
            });
            current = adt.ParentAdt;
        }
    }

    private void SetGeneratedCaseTreeOrigins(CaseTypeDef caseType, GeneratedDeclarationOrigin origin)
    {
        SetGeneratedOrigin(caseType.SymbolId, origin);
        foreach (var field in caseType.Fields)
        {
            SetGeneratedOrigin(field.SymbolId, origin);
        }
        foreach (var child in caseType.Cases)
        {
            SetGeneratedCaseTreeOrigins(child, origin);
        }
    }

    private void SetGeneratedOrigin(SymbolId symbolId, GeneratedDeclarationOrigin origin)
    {
        if (symbolId.IsValid && _symbolTable.GetSymbol(symbolId) is { } symbol)
        {
            _symbolTable.UpdateSymbol(symbol with { GeneratedOrigin = origin });
        }
    }

    private static bool TryValidateGeneratedMemberName(
        string name,
        string memberKind,
        out string reason)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            reason = $"generated {memberKind} requires a non-empty name";
            return false;
        }
        if (ReservedInternalNames.TryMatch(name, out var reservedPrefix))
        {
            reason = $"generated {memberKind} '{name}' uses reserved internal marker '{reservedPrefix}'";
            return false;
        }
        reason = string.Empty;
        return true;
    }

    private static string GetGeneratedDeclarationName(Declaration declaration) => declaration switch
    {
        AdtDef adt => adt.Name,
        CaseTypeDef caseType => caseType.Name,
        FuncDef function => function.Name,
        FuncDecl function => function.Name,
        TraitDef trait => trait.Name,
        EffectDef effect => effect.Name,
        InstanceDecl instance => instance.Name,
        ModuleDecl module => module.Path.LastOrDefault() ?? string.Empty,
        LetDecl { Pattern: Ast.Patterns.VarPattern variable } => variable.Name,
        _ => string.Empty
    };

    private static bool FailGeneratedMemberValidation(string message, out string reason)
    {
        reason = message;
        return false;
    }
}
