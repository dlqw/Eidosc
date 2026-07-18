using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Patterns;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using Eidosc.Pipeline;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private sealed record PendingMetaUserDiagnostic(
        MetaDiagnosticLevel Level,
        SourceSpan Span,
        string Message);

    private sealed record PreparedMetaNode(
        MaterializedMetaNode Materialized,
        GeneratedDeclarationOrigin Origin,
        PreparedGeneratedDeclarationIdentity Identity);

    private sealed record PreparedMetaTransformation(
        ModuleDecl? TargetModule,
        int TargetIndex,
        IReadOnlyList<PreparedMetaNode> Nodes,
        IReadOnlyList<PreparedGeneratedDeclarationIdentity> Identities);

    private bool TryPrepareMetaTransformation(
        MetaInvocationOccurrence invocation,
        FuncSymbol generatorSymbol,
        Symbol targetSymbol,
        string canonicalInvocationHash,
        MetaExpansionMaterializationResult materialization,
        int generatedDeclarationCount,
        out PreparedMetaTransformation prepared,
        out string reason,
        out string diagnosticCode)
    {
        prepared = null!;
        diagnosticCode = "E3616";
        if (!MetaTransformationValidator.TryValidate(
                invocation.Invocation.Stage,
                invocation.Target,
                materialization,
                out reason))
        {
            return false;
        }

        var origins = new List<GeneratedDeclarationOrigin>(materialization.Nodes.Count);
        var candidates = new List<GeneratedDeclarationIdentityCandidate>(materialization.Nodes.Count);
        foreach (var materialized in materialization.Nodes)
        {
            var origin = CreateGeneratedOrigin(
                invocation,
                generatorSymbol,
                targetSymbol,
                canonicalInvocationHash,
                materialized);
            origins.Add(origin);
            candidates.Add(new GeneratedDeclarationIdentityCandidate(
                origin.GenerationSlotIdentity,
                CreateGeneratedNodePayloadHash(materialized.Node)));
        }

        if (!_generatedDeclarationIdentities.TryPrepareBatch(
                candidates,
                out var preparedIdentities,
                out var conflictIdentity))
        {
            reason = $"generated declaration identity conflict for '{conflictIdentity}'";
            diagnosticCode = "E3605";
            return false;
        }

        var nodesToCommit = materialization.Nodes
            .Where((_, index) => preparedIdentities[index].Registration == GeneratedDeclarationIdentityRegistration.Added)
            .ToArray();
        var effectiveMaterialization = materialization with { Nodes = nodesToCommit };
        if (nodesToCommit.Length == 0 && !materialization.RemovesTarget)
        {
            prepared = new PreparedMetaTransformation(
                null,
                -1,
                materialization.Nodes.Select((node, index) => new PreparedMetaNode(
                    node,
                    origins[index],
                    preparedIdentities[index])).ToArray(),
                preparedIdentities);
            reason = string.Empty;
            return true;
        }

        ModuleDecl? targetModule = null;
        var targetIndex = -1;
        if (effectiveMaterialization.Nodes.Count > 0 || effectiveMaterialization.RemovesTarget)
        {
            if (!_moduleDeclarations.TryGetValue(invocation.ModuleId, out targetModule))
            {
                reason = "meta target module is unavailable";
                diagnosticCode = "E3600";
                return false;
            }

            var requiresDeclarationAnchor = effectiveMaterialization.RemovesTarget || effectiveMaterialization.Nodes.Any(static node =>
                node.Placement is MetaDeclarationPlacement.BeforeTarget or
                    MetaDeclarationPlacement.AfterTarget or
                    MetaDeclarationPlacement.ReplaceTarget);
            if (requiresDeclarationAnchor &&
                invocation.Target is ModuleDecl targetModuleDeclaration &&
                !TryFindContainingModule(targetModuleDeclaration, out targetModule))
            {
                reason = "the authorized module target has no containing module";
                diagnosticCode = "E3614";
                return false;
            }

            targetIndex = targetModule.Declarations.IndexOf(invocation.Target);
            var requiresTopLevelModuleAnchor = effectiveMaterialization.Nodes.Any(static node =>
                node.Placement is MetaDeclarationPlacement.BeforeTarget or
                    MetaDeclarationPlacement.AfterTarget);
            var requiresNestedDeclarationAnchor = effectiveMaterialization.RemovesTarget || effectiveMaterialization.Nodes.Any(static node =>
                node.Placement == MetaDeclarationPlacement.ReplaceTarget);
            if ((requiresTopLevelModuleAnchor && targetIndex < 0) ||
                (requiresNestedDeclarationAnchor && !ContainsDeclaration(targetModule, invocation.Target)))
            {
                reason = "the authorized meta target is no longer present in its owning module";
                diagnosticCode = "E3614";
                return false;
            }
        }

        var members = effectiveMaterialization.Nodes
            .Where(static node => node.Placement == MetaDeclarationPlacement.Member)
            .ToArray();
        if (!TryValidateGeneratedMembers(invocation.Target, members, out reason))
        {
            return false;
        }

        if (!TryValidateTargetMutation(invocation, targetModule, effectiveMaterialization, out reason))
        {
            diagnosticCode = effectiveMaterialization.RemovesTarget ? "E3615" : "E3614";
            return false;
        }

        if (!TryValidateGeneratedDeclarationsBeforeCommit(invocation, effectiveMaterialization.Nodes, out reason))
        {
            return false;
        }

        var addedCount = preparedIdentities.Count(static identity =>
            identity.Registration == GeneratedDeclarationIdentityRegistration.Added);
        if (generatedDeclarationCount + addedCount > MaxGeneratedDeclarationCount)
        {
            reason = "generated declaration count exceeded the compiler budget";
            diagnosticCode = "E3608";
            return false;
        }

        for (var index = 0; index < materialization.Nodes.Count; index++)
        {
            if (preparedIdentities[index].Registration != GeneratedDeclarationIdentityRegistration.Added)
            {
                continue;
            }
            AttachGeneratedOriginChain(
                materialization.Nodes[index].Node,
                BuildGeneratedOriginChain(invocation.Target, targetSymbol, origins[index]));
        }

        prepared = new PreparedMetaTransformation(
            targetModule,
            targetIndex,
            materialization.Nodes.Select((node, index) => new PreparedMetaNode(
                node,
                origins[index],
                preparedIdentities[index])).ToArray(),
            preparedIdentities);
        reason = string.Empty;
        return true;
    }

    private static IReadOnlyList<GeneratedDeclarationOrigin> BuildGeneratedOriginChain(
        Declaration target,
        Symbol targetSymbol,
        GeneratedDeclarationOrigin current)
    {
        var inherited = target.GeneratedOriginChain.Count > 0
            ? target.GeneratedOriginChain
            : targetSymbol.GeneratedOrigin == null
                ? []
                : [targetSymbol.GeneratedOrigin];
        return [.. inherited, current];
    }

    private static void AttachGeneratedOriginChain(
        EidosAstNode root,
        IReadOnlyList<GeneratedDeclarationOrigin> chain)
    {
        var pending = new Stack<EidosAstNode>();
        var visited = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!visited.Add(node))
            {
                continue;
            }

            node.AttachGeneratedOriginChain(chain);
            foreach (var child in AstStableNodeTraversal.GetStructuralChildren(node))
            {
                pending.Push(child);
            }
        }
    }

    private bool TryValidateTargetMutation(
        MetaInvocationOccurrence invocation,
        ModuleDecl? module,
        MetaExpansionMaterializationResult materialization,
        out string reason)
    {
        reason = string.Empty;

        if ((materialization.RemovesTarget || materialization.Nodes.Any(static node =>
                 node.Placement == MetaDeclarationPlacement.ReplaceTarget)) &&
            !TryValidateNominalMutationCoherence(invocation, out reason))
        {
            return false;
        }

        if (materialization.RemovesTarget)
        {
            if (invocation.Target is ImportDecl || module == null ||
                !invocation.Target.SymbolId.IsValid ||
                !ContainsDeclaration(module, invocation.Target))
            {
                reason = "Syntax target removal requires an existing declaration target in its owning module";
                return false;
            }

            if (invocation.Target is FuncDef removedMethod &&
                TryFindMethodOwner(module, removedMethod, out var removalOwner) &&
                removalOwner is InstanceDecl removalInstance &&
                !TryValidateProspectiveInstanceMutation(
                    removalInstance,
                    removalInstance.Methods.Where(method => !ReferenceEquals(method, removedMethod)),
                    removalInstance.AssociatedTypes,
                    removalInstance.AssociatedConsts,
                    out reason))
            {
                reason = $"removing method '{removedMethod.Name}' makes instance '{removalInstance.Name}' incoherent: {reason}";
                return false;
            }
        }

        var replacement = materialization.Nodes.SingleOrDefault(static node =>
            node.Placement == MetaDeclarationPlacement.ReplaceTarget);
        if (replacement == null)
        {
            reason = string.Empty;
            return true;
        }

        if (module == null || !invocation.Target.SymbolId.IsValid ||
            !ContainsDeclaration(module, invocation.Target))
        {
            reason = "target replacement requires an existing declaration in its owning module";
            return false;
        }

        if (replacement.Node is not Declaration replacementDeclaration ||
            !MetaTransformationValidator.HasSameTargetCategory(invocation.Target, replacementDeclaration))
        {
            reason = $"target replacement category '{MetaTransformationValidator.GetTargetCategory(replacement.Node as Declaration)}' " +
                     $"does not match authorized category '{MetaTransformationValidator.GetTargetCategory(invocation.Target)}'";
            return false;
        }

        if (invocation.Invocation.Stage == ClauseStage.Body)
        {
            if (invocation.Target is not FuncDef source || replacementDeclaration is not FuncDef replacementFunction)
            {
                reason = "Body replacement requires a function target and function syntax";
                return false;
            }

            if (!string.Equals(source.Name, replacementFunction.Name, StringComparison.Ordinal) ||
                !string.Equals(CanonicalFunctionContract(source), CanonicalFunctionContract(replacementFunction), StringComparison.Ordinal))
            {
                reason = "Body function replacement must preserve the target name, generic parameters, signature, and effects; " +
                         $"expected '{CanonicalFunctionContract(source)}', got '{CanonicalFunctionContract(replacementFunction)}'";
                return false;
            }
        }

        else if (invocation.Invocation.Stage == ClauseStage.Syntax &&
                 invocation.Target is CaseTypeDef sourceCase &&
                 replacementDeclaration is CaseTypeDef replacementCase)
        {
            if (module == null || !TryFindCaseOwner(module, sourceCase, out var owner, out _))
            {
                reason = "Syntax case replacement could not locate the authorized case in its closed-case owner";
                return false;
            }

            if (!TryValidateGeneratedTypeMembers(
                    owner,
                    [new MaterializedMetaNode(replacementCase, 0, Placement: MetaDeclarationPlacement.Member)],
                    out reason,
                    sourceCase))
            {
                return false;
            }
        }
        else if (invocation.Invocation.Stage == ClauseStage.Syntax &&
                 invocation.Target is FuncDef sourceMethod &&
                 replacementDeclaration is FuncDef replacementMethod &&
                 TryFindMethodOwner(module!, sourceMethod, out var methodOwner))
        {
            var replacementNode = new MaterializedMetaNode(
                replacementMethod,
                0,
                Placement: MetaDeclarationPlacement.Member);
            var valid = methodOwner switch
            {
                TraitDef trait => TryValidateGeneratedAssociatedMembers(
                    trait.Name,
                    trait.Methods,
                    trait.AssociatedTypes,
                    trait.AssociatedConsts,
                    [replacementNode],
                    out reason,
                    sourceMethod),
                InstanceDecl instance => TryValidateGeneratedAssociatedMembers(
                    instance.Name,
                    instance.Methods,
                    instance.AssociatedTypes,
                    instance.AssociatedConsts,
                    [replacementNode],
                    out reason,
                    sourceMethod),
                _ => false
            };
            if (!valid)
            {
                reason = string.IsNullOrWhiteSpace(reason)
                    ? "Syntax method replacement could not validate its declaration owner"
                    : reason;
                return false;
            }
            if (methodOwner is InstanceDecl replacementInstance &&
                !TryValidateProspectiveInstanceMutation(
                    replacementInstance,
                    replacementInstance.Methods.Select(method =>
                        ReferenceEquals(method, sourceMethod) ? replacementMethod : method),
                    replacementInstance.AssociatedTypes,
                    replacementInstance.AssociatedConsts,
                    out reason))
            {
                reason = $"replacing method '{sourceMethod.Name}' makes instance '{replacementInstance.Name}' incoherent: {reason}";
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private bool TryValidateGeneratedDeclarationsBeforeCommit(
        MetaInvocationOccurrence invocation,
        IReadOnlyList<MaterializedMetaNode> materializedNodes,
        out string reason)
    {
        var moduleDeclarations = new List<Declaration>();
        foreach (var materialized in materializedNodes)
        {
            foreach (var declaration in EnumerateGeneratedDeclarations(materialized.Node))
            {
                var declarationName = GetGeneratedDeclarationName(declaration);
                if (!string.IsNullOrWhiteSpace(declarationName) &&
                    !TryValidateGeneratedMemberName(declarationName, "declaration", out reason))
                {
                    return false;
                }
                if (declaration is AdtDef or TraitDef or EffectDef &&
                    string.Equals(declarationName, WellKnownStrings.Keywords.Self, StringComparison.Ordinal))
                {
                    reason = $"generated {declaration.GetType().Name} cannot declare the reserved type name 'Self'";
                    return false;
                }

                if (string.Equals(LanguageVersion, ProjectSystem.EidosLanguageVersions.Current, StringComparison.Ordinal) &&
                    declaration.Attributes.Count > 0)
                {
                    reason = "generated declarations cannot use the removed 0.7 attribute surface";
                    return false;
                }

                var binding = DeclarationClauseBinder.Bind(
                    declaration,
                    LanguageVersion,
                    CompilerOwnedSourceGrant.None);
                if (binding.Diagnostics.Count > 0)
                {
                    reason = string.Join("; ", binding.Diagnostics.Select(static diagnostic => diagnostic.Message));
                    return false;
                }

                var regressed = binding.MetaInvocations
                    .Select(meta => (
                        Invocation: meta,
                        Stage: ResolveDetachedMetaInvocationStage(meta, invocation.ModuleId)))
                    .FirstOrDefault(candidate => candidate.Stage < invocation.Invocation.Stage);
                if (regressed.Invocation != null)
                {
                    reason = $"generated declaration requested stage '{regressed.Stage}' after stage '{invocation.Invocation.Stage}' was already active";
                    return false;
                }

                if (declaration is FuncDef function)
                {
                    var clauseSemantics = _clauseSemanticBinder.Bind(function, function.Name);
                    if (clauseSemantics.Diagnostics.Count > 0)
                    {
                        reason = string.Join("; ", clauseSemantics.Diagnostics.Select(static diagnostic => diagnostic.Message));
                        return false;
                    }
                    if (function.Body.Count > 0 && clauseSemantics.Ffi != null)
                    {
                        reason = $"generated extern function '{function.Name}' cannot have an Eidos body";
                        return false;
                    }
                    if (function.Body.Count > 0 && clauseSemantics.Intrinsic != null)
                    {
                        reason = $"generated intrinsic function '{function.Name}' cannot have an Eidos body";
                        return false;
                    }
                }
            }

            if (materialized.Placement is MetaDeclarationPlacement.BeforeTarget or
                MetaDeclarationPlacement.AfterTarget ||
                materialized.Placement == MetaDeclarationPlacement.ReplaceTarget && invocation.Target is not CaseTypeDef ||
                materialized.Placement == MetaDeclarationPlacement.Member && invocation.Target is ModuleDecl)
            {
                if (materialized.Node is not Declaration declaration)
                {
                    reason = $"{materialized.Placement} produced {materialized.Node.GetType().Name}, not item declaration syntax";
                    return false;
                }
                moduleDeclarations.Add(declaration);
            }
        }

        var anchorModuleId = ResolveGeneratedDeclarationAnchorModuleId(invocation, materializedNodes);
        if (!TryValidateGeneratedModuleDeclarationCollisions(
                anchorModuleId,
            moduleDeclarations,
            materializedNodes.Any(static node => node.Placement == MetaDeclarationPlacement.ReplaceTarget)
                ? CollectOwnedDeclarationSymbolIds(invocation.Target)
                : null,
                out reason))
        {
            return false;
        }

        return TryValidateGeneratedInstanceCoherence(
            invocation,
            materializedNodes,
            anchorModuleId,
            out reason);
    }

    private ClauseStage ResolveDetachedMetaInvocationStage(
        MetaInvocationIR invocation,
        SymbolId moduleId)
    {
        if (invocation.Owner == MetaInvocationOwner.CompilerDerive)
        {
            return invocation.Stage;
        }

        using var moduleScope = PushResolutionModuleScope(moduleId);
        using var currentModuleScope = PushCurrentModuleScope(moduleId);
        var path = invocation.GeneratorPath;
        var symbolId = path.Count switch
        {
            1 => _lookupService.Lookup(path[0], LookupKind.Value, CreateLookupContext()).SymbolId,
            > 1 => ResolvePathWithImports(path).SymbolId,
            _ => SymbolId.None
        };
        if (!symbolId.IsValid || _symbolTable.GetSymbol<FuncSymbol>(symbolId) is not { IsComptime: true })
        {
            return invocation.Stage;
        }

        var generator = _moduleDeclarations.Values
            .SelectMany(EnumerateDeclarations)
            .OfType<FuncDef>()
            .FirstOrDefault(function => function.SymbolId == symbolId);
        return generator != null && TryGetTargetTransformationStage(
            generator,
            invocation.ExplicitArguments.Count,
            out var resolvedStage)
            ? resolvedStage
            : invocation.Stage;
    }

    private SymbolId ResolveGeneratedDeclarationAnchorModuleId(
        MetaInvocationOccurrence invocation,
        IReadOnlyList<MaterializedMetaNode> materializedNodes)
    {
        if (invocation.Target is ModuleDecl targetModule &&
            materializedNodes.Any(static node => node.Placement is
                MetaDeclarationPlacement.BeforeTarget or
                MetaDeclarationPlacement.AfterTarget or
                MetaDeclarationPlacement.ReplaceTarget) &&
            TryFindContainingModule(targetModule, out var containingModule))
        {
            return containingModule.SymbolId;
        }

        return invocation.ModuleId;
    }

    private bool TryFindContainingModule(ModuleDecl target, out ModuleDecl containingModule)
    {
        foreach (var candidate in _moduleDeclarations.Values)
        {
            if (candidate.Declarations.Any(declaration => ReferenceEquals(declaration, target)))
            {
                containingModule = candidate;
                return true;
            }
        }

        containingModule = null!;
        return false;
    }

    private bool TryValidateGeneratedModuleDeclarationCollisions(
        SymbolId moduleId,
        IReadOnlyList<Declaration> declarations,
        IReadOnlySet<SymbolId>? replacedSymbols,
        out string reason)
    {
        if (declarations.Count == 0)
        {
            reason = string.Empty;
            return true;
        }

        var existingByName = _symbolTable.Modules.GetModuleMembers(moduleId)
            .Select(_symbolTable.GetSymbol)
            .Where(static symbol => symbol != null)
            .Cast<Symbol>()
            .Where(symbol => replacedSymbols == null || !replacedSymbols.Contains(symbol.Id))
            .GroupBy(static symbol => symbol.Name, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var prospectiveByName = new Dictionary<string, List<Declaration>>(StringComparer.Ordinal);
        var overloadKeys = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var declaration in declarations)
        {
            var name = GetGeneratedDeclarationBindingName(declaration);
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            if (declaration is InstanceDecl &&
                _instanceDeclarations.TryGetValue(name, out var existingInstance) &&
                (replacedSymbols == null || !replacedSymbols.Contains(existingInstance.SymbolId)))
            {
                reason = $"generated instance '{name}' collides with an existing instance declaration";
                return false;
            }

            prospectiveByName.TryGetValue(name, out var prospective);
            prospective ??= [];
            prospectiveByName[name] = prospective;
            existingByName.TryGetValue(name, out var existing);
            existing ??= [];

            if (declaration is FuncDef function)
            {
                if (existing.Any(static symbol => symbol is not FuncSymbol) ||
                    prospective.Any(static candidate => candidate is not FuncDef))
                {
                    reason = $"generated function '{name}' collides with an existing non-function declaration";
                    return false;
                }

                if (!overloadKeys.TryGetValue(name, out var keys))
                {
                    keys = new HashSet<string>(StringComparer.Ordinal);
                    if (_symbolTable.CurrentScope is { } scope &&
                        _functionOverloadDeclarations.TryGetValue(scope, out var byName) &&
                        byName.TryGetValue(name, out var overloads))
                    {
                        keys.UnionWith(overloads
                            .Where(overload => replacedSymbols == null || !replacedSymbols.Contains(overload.SymbolId))
                            .Select(static overload => overload.SignatureKey));
                    }
                    overloadKeys[name] = keys;
                }

                var signature = BuildFunctionOverloadSignatureKey(
                    name,
                    function.Signature,
                    function.TypeParams);
                if (!keys.Add(signature))
                {
                    reason = $"generated function '{name}' duplicates an existing overload '{signature}'";
                    return false;
                }
            }
            else if (existing.Length > 0 || prospective.Count > 0)
            {
                reason = $"generated declaration '{name}' collides with an existing declaration in the target module";
                return false;
            }

            prospective.Add(declaration);
        }

        reason = string.Empty;
        return true;
    }

    private static IEnumerable<Declaration> EnumerateGeneratedDeclarations(EidosAstNode node)
    {
        if (node is not Declaration declaration)
        {
            yield break;
        }

        yield return declaration;
        switch (declaration)
        {
            case AdtDef adt:
                foreach (var caseType in adt.Cases.SelectMany(EnumerateCaseDeclarations))
                {
                    yield return caseType;
                }
                break;
            case CaseTypeDef caseType:
                foreach (var nested in caseType.Cases.SelectMany(EnumerateCaseDeclarations))
                {
                    yield return nested;
                }
                break;
            case TraitDef trait:
                foreach (var method in trait.Methods)
                {
                    yield return method;
                }
                break;
            case InstanceDecl instance:
                foreach (var method in instance.Methods)
                {
                    yield return method;
                }
                break;
            case ModuleDecl module:
                foreach (var child in module.Declarations.SelectMany(EnumerateGeneratedDeclarations))
                {
                    yield return child;
                }
                break;
        }
    }

    private static IEnumerable<CaseTypeDef> EnumerateCaseDeclarations(CaseTypeDef caseType)
    {
        yield return caseType;
        foreach (var child in caseType.Cases.SelectMany(EnumerateCaseDeclarations))
        {
            yield return child;
        }
    }

    private static bool ContainsDeclaration(ModuleDecl module, Declaration target)
    {
        foreach (var declaration in module.Declarations)
        {
            if (ReferenceEquals(declaration, target) || ContainsDeclaration(declaration, target))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsDeclaration(Declaration owner, Declaration target) => owner switch
    {
        ModuleDecl module => ContainsDeclaration(module, target),
        AdtDef adt => adt.Cases.Any(caseType =>
            ReferenceEquals(caseType, target) || ContainsDeclaration(caseType, target)),
        CaseTypeDef caseType => caseType.Cases.Any(child =>
            ReferenceEquals(child, target) || ContainsDeclaration(child, target)),
        TraitDef trait => trait.Methods.Any(method => ReferenceEquals(method, target)),
        InstanceDecl instance => instance.Methods.Any(method => ReferenceEquals(method, target)),
        _ => false
    };

    private HashSet<SymbolId> CollectOwnedDeclarationSymbolIds(Declaration declaration)
    {
        var result = new HashSet<SymbolId>();
        var pending = new Queue<SymbolId>();

        foreach (var entry in AstStableNodeTraversal.Enumerate(CreateTraversalModule(declaration)))
        {
            if (entry.Node is Declaration or Ast.Types.TypeParam or Constructor or Field or
                AssociatedTypeDecl or AssociatedConstDecl &&
                entry.Node.SymbolId.IsValid)
            {
                pending.Enqueue(entry.Node.SymbolId);
            }
        }

        var directlyOwnedFunctionIds = pending
            .Where(symbolId => _symbolTable.GetSymbol<FuncSymbol>(symbolId) != null)
            .ToHashSet();
        foreach (var implementation in _symbolTable.Symbols.Values.OfType<ImplSymbol>())
        {
            if (implementation.Methods.Any(directlyOwnedFunctionIds.Contains))
            {
                pending.Enqueue(implementation.Id);
            }
        }

        while (pending.TryDequeue(out var symbolId))
        {
            if (!symbolId.IsValid || !result.Add(symbolId))
            {
                continue;
            }

            var children = _symbolTable.GetSymbol(symbolId) switch
            {
                FuncSymbol function => function.TypeParams.Concat(function.Parameters),
                AdtSymbol adt => adt.TypeParams
                    .Concat(adt.Constructors)
                    .Concat(adt.Fields)
                    .Concat(adt.DirectCases)
                    .Concat(adt.CaseConstructor.IsValid ? [adt.CaseConstructor] : []),
                CtorSymbol constructor => constructor.TypeParams.Concat(constructor.NamedFields),
                TraitSymbol trait => trait.TypeParams
                    .Concat(trait.Methods)
                    .Concat(trait.AssociatedTypes)
                    .Concat(trait.AssociatedConsts),
                ImplSymbol implementation => implementation.Methods
                    .Concat(implementation.AssociatedTypes)
                    .Concat(implementation.AssociatedConsts),
                AssociatedTypeSymbol associatedType => associatedType.TypeParams,
                ModuleSymbol module => module.Members,
                _ => []
            };
            foreach (var child in children)
            {
                pending.Enqueue(child);
            }
        }

        return result;
    }

    private static ModuleDecl CreateTraversalModule(Declaration declaration)
    {
        if (declaration is ModuleDecl module)
        {
            return module;
        }

        var root = new ModuleDecl();
        root.SetPath([WellKnownStrings.SpecialNames.Main]);
        root.SetDeclarations([declaration]);
        return root;
    }

    private static string GetGeneratedDeclarationBindingName(Declaration declaration) => declaration switch
    {
        FuncDef function => GetSyntaxBindingName(function, function.Name),
        FuncDecl function => GetSyntaxBindingName(function, function.Name),
        LetDecl { Pattern: VarPattern variable } => GetSyntaxBindingName(variable, variable.Name),
        AdtDef adt => GetSyntaxBindingName(adt, adt.Name),
        TraitDef trait => GetSyntaxBindingName(trait, trait.Name),
        EffectDef effect => GetSyntaxBindingName(effect, effect.Name),
        InstanceDecl instance => GetSyntaxBindingName(instance, instance.Name),
        ModuleDecl module => GetSyntaxBindingName(module, module.Path.LastOrDefault() ?? string.Empty),
        _ => GetGeneratedDeclarationName(declaration)
    };

    private ForcedPrivateGeneratedDeclarationScope PushForcedPrivateGeneratedDeclaration(ClauseStage stage)
    {
        if (stage is ClauseStage.Body or ClauseStage.Layout)
        {
            _forcedPrivateGeneratedDeclarationDepth++;
            return new ForcedPrivateGeneratedDeclarationScope(this);
        }

        return default;
    }

    private readonly struct ForcedPrivateGeneratedDeclarationScope(NameResolver? resolver) : IDisposable
    {
        private readonly NameResolver? _resolver = resolver;

        public void Dispose()
        {
            if (_resolver != null)
            {
                _resolver._forcedPrivateGeneratedDeclarationDepth--;
            }
        }
    }
}
