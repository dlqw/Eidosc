using Eidosc.Ast.Declarations;
using Eidosc.Symbols;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    internal int RehydratedMetaInvocationCount => _metaInvocationOccurrences.Count;

    internal int RehydratedCompletedMetaInvocationCount => _completedMetaInvocations.Count;

    internal int GetRehydratedMetaInvocationCount(ClauseStage stage) =>
        _metaInvocationOccurrences.Count(occurrence => occurrence.Invocation.Stage == stage);

    internal string SchedulerRehydrationFailure { get; private set; } = string.Empty;

    internal bool TryRehydrateDeferredMetaExpansionState(ModuleDecl root)
    {
        ArgumentNullException.ThrowIfNull(root);

        _rootInputFilePath = root.Span.FilePath;
        _rootModule = root.SymbolId;
        _currentModule = SymbolId.None;
        _importScopes.Clear();
        _importsProcessed.Clear();
        _importsProcessing.Clear();
        _moduleScopes.Clear();
        _moduleDeclarations.Clear();
        _adtDefinitions.Clear();
        _traitDefinitions.Clear();
        _declarationsBySymbol.Clear();
        _genericParameterKindsBySymbol.Clear();
        _metaInvocationOccurrences.Clear();
        _completedMetaInvocations.Clear();
        _metaInvocationInputFingerprints.Clear();
        _queryDependentMetaInvocations.Clear();
        _metaInvocationQueryFingerprints.Clear();
        _closedMetaExpansionStages.Clear();
        _generatedDeclarationIdentities.Clear();
        _metaResolvedComptimeSymbols.Clear();
        _instanceDeclarations.Clear();
        _functionOverloadDeclarations.Clear();
        _traitOwnerModules.Clear();
        _ctorPatternShapes.Clear();

        if (_rootModule.IsValid && _symbolTable.Modules.GetModule(_rootModule) != null)
        {
            if (!TryRehydrateModule(root, parentScope: _symbolTable.BuiltinScope))
            {
                return false;
            }
        }
        else
        {
            _rootModule = SymbolId.None;
            foreach (var child in root.Declarations.OfType<ModuleDecl>())
            {
                if (!child.SymbolId.IsValid || _symbolTable.Modules.GetModule(child.SymbolId) == null)
                {
                    continue;
                }

                if (!_rootModule.IsValid)
                {
                    _rootModule = child.SymbolId;
                }
                if (!TryRehydrateModule(child, parentScope: _symbolTable.BuiltinScope))
                {
                    return false;
                }
            }
            if (!_rootModule.IsValid)
            {
                return FailSchedulerRehydration("missingRootModuleSymbol");
            }
        }

        foreach (var moduleId in _moduleDeclarations.Keys.OrderBy(static id => id.Value))
        {
            EnsureModuleImportsProcessed(moduleId);
        }
        _completedMetaInvocations.Clear();
        foreach (var occurrence in _metaInvocationOccurrences)
        {
            if (occurrence.Invocation.Stage == ClauseStage.Syntax ||
                occurrence.Invocation.Stage == ClauseStage.Semantic)
            {
                _completedMetaInvocations.Add(occurrence.Invocation.OccurrenceId);
            }
        }
        _closedMetaExpansionStages.Add(ClauseStage.Syntax);
        _closedMetaExpansionStages.Add(ClauseStage.Semantic);
        var expectedCompletedInvocationCount = _metaInvocationOccurrences.Count(static occurrence =>
            occurrence.Invocation.Stage == ClauseStage.Syntax ||
            occurrence.Invocation.Stage == ClauseStage.Semantic);
        if (_completedMetaInvocations.Count != expectedCompletedInvocationCount)
        {
            throw new InvalidOperationException(
                $"deferred meta scheduler rehydration mismatch: completed={_completedMetaInvocations.Count}, " +
                $"expected={expectedCompletedInvocationCount}, stages={string.Join(',', _metaInvocationOccurrences.Select(static occurrence => occurrence.Invocation.Stage))}");
        }
        _currentModule = _rootModule;
        return !_diagnostics.Any(static diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error) ||
               FailSchedulerRehydration("rehydrationDiagnostics");
    }

    private bool TryRehydrateModule(ModuleDecl module, Scope? parentScope)
    {
        if (!module.SymbolId.IsValid ||
            _symbolTable.Modules.GetModule(module.SymbolId) is not { } moduleSymbol)
        {
            return FailSchedulerRehydration("missingModuleSymbol");
        }

        var moduleScope = CreateRehydratedModuleScope(moduleSymbol, parentScope);
        _moduleScopes[module.SymbolId] = moduleScope;
        _moduleDeclarations[module.SymbolId] = module;
        _declarationsBySymbol[module.SymbolId] = module;

        using (_symbolTable.PushScopeGuard(moduleScope))
        using (PushCurrentModuleScope(module.SymbolId))
        {
            foreach (var declaration in module.Declarations)
            {
                if (declaration is not (ImportDecl or ModuleDecl) &&
                    !TryRehydrateDeclaration(declaration))
                {
                    return false;
                }
            }
        }

        foreach (var child in module.Declarations.OfType<ModuleDecl>())
        {
            if (!child.SymbolId.IsValid || _symbolTable.Modules.GetModule(child.SymbolId) == null)
            {
                if (ContainsDeferredMetaInvocations(child))
                {
                    return FailSchedulerRehydration("missingDeferredModuleSymbol");
                }
                continue;
            }
            if (!TryRehydrateModule(child, moduleScope))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ContainsDeferredMetaInvocations(ModuleDecl module)
    {
        foreach (var declaration in module.Declarations)
        {
            if (declaration.MetaInvocations.Any(static invocation =>
                    invocation.Stage is ClauseStage.Body or ClauseStage.Layout))
            {
                return true;
            }
            if (declaration is AdtDef adt && ContainsDeferredCaseMetaInvocations(adt.Cases))
            {
                return true;
            }
            if (declaration is ModuleDecl child && ContainsDeferredMetaInvocations(child))
            {
                return true;
            }
        }
        return false;
    }

    private static bool ContainsDeferredCaseMetaInvocations(IEnumerable<CaseTypeDef> cases) =>
        cases.Any(static caseType =>
            caseType.MetaInvocations.Any(static invocation =>
                invocation.Stage is ClauseStage.Body or ClauseStage.Layout) ||
            ContainsDeferredCaseMetaInvocations(caseType.Cases));

    private Scope CreateRehydratedModuleScope(ModuleSymbol module, Scope? parentScope)
    {
        var scope = new Scope(parentScope) { Kind = ScopeKind.Module };
        foreach (var memberId in module.Members.Distinct())
        {
            switch (_symbolTable.GetSymbol(memberId))
            {
                case FuncSymbol function:
                    scope.BindFunction(function.Name, function.Id);
                    break;
                case VarSymbol variable:
                    scope.BindValue(variable.Name, variable.Id);
                    break;
                case AdtSymbol adt:
                    scope.BindType(adt.Name, adt.Id);
                    foreach (var constructorId in adt.Constructors)
                    {
                        if (_symbolTable.GetSymbol<CtorSymbol>(constructorId) is { } constructor)
                        {
                            scope.BindConstructor(constructor.Name, constructor.Id);
                        }
                    }
                    break;
                case TraitSymbol trait:
                    scope.BindTrait(trait.Name, trait.Id);
                    break;
                case EffectSymbol effect:
                    scope.BindEffect(effect.Name, effect.Id);
                    break;
            }
        }

        return scope;
    }

    private bool TryRehydrateDeclaration(Declaration declaration)
    {
        if (declaration is InstanceDecl instance)
        {
            if (!string.IsNullOrWhiteSpace(instance.Name) &&
                !_instanceDeclarations.TryAdd(instance.Name, instance))
            {
                return FailSchedulerRehydration("duplicateInstance");
            }

            return true;
        }

        if (!declaration.SymbolId.IsValid ||
            _symbolTable.GetSymbol(declaration.SymbolId) is not { } symbol)
        {
            return FailSchedulerRehydration("missingDeclarationSymbol");
        }

        _declarationsBySymbol[declaration.SymbolId] = declaration;
        if (declaration is FuncDef { IsComptime: true } or LetDecl { IsComptime: true })
        {
            _metaResolvedComptimeSymbols.Add(declaration.SymbolId);
        }
        switch (declaration)
        {
            case FuncDef function:
                RegisterGenericParameterKinds(function.SymbolId, function.TypeParams);
                if (symbol is FuncSymbol { IsTraitImplementation: false })
                {
                    RegisterFunctionOverloadDeclaration(
                        function.Name,
                        function.Signature,
                        function.TypeParams,
                        function.Span,
                        function.SymbolId);
                }
                break;
            case FuncDecl function:
                RegisterGenericParameterKinds(function.SymbolId, function.TypeParams);
                RegisterFunctionOverloadDeclaration(
                    function.Name,
                    function.Signature,
                    function.TypeParams,
                    function.Span,
                    function.SymbolId);
                break;
            case AdtDef adt:
                _adtDefinitions[adt.SymbolId] = adt;
                RegisterGenericParameterKinds(adt.SymbolId, adt.TypeParams);
                RehydrateCaseTypes(adt.Cases);
                foreach (var constructor in adt.Constructors)
                {
                    if (constructor.SymbolId.IsValid)
                    {
                        _ctorPatternShapes[constructor.SymbolId] = BuildCtorPatternShape(constructor);
                    }
                }
                break;
            case TraitDef trait:
                _traitDefinitions[trait.SymbolId] = trait;
                _traitOwnerModules[trait.SymbolId] = _currentModule;
                RegisterGenericParameterKinds(trait.SymbolId, trait.TypeParams);
                break;
        }

        if (symbol.GeneratedOrigin is { } origin)
        {
            var payloadHash = CreateGeneratedDeclarationPayloadHash(declaration);
            if (_generatedDeclarationIdentities.Register(origin.StableIdentity, payloadHash) ==
                GeneratedDeclarationIdentityRegistration.Conflict)
            {
                return FailSchedulerRehydration("generatedIdentityConflict");
            }
        }

        if (declaration is AdtDef adtDeclaration)
        {
            ProcessDeclarationMetaClauses(adtDeclaration);
        }
        else
        {
            var targetName = GetMetaTargetName(declaration);
            ProcessDeclarationMetaClauses(declaration, deriveShape: null, targetName, [targetName]);
        }

        return true;
    }

    private bool FailSchedulerRehydration(string failure)
    {
        SchedulerRehydrationFailure = failure;
        return false;
    }

    private void RehydrateCaseTypes(IEnumerable<CaseTypeDef> cases)
    {
        foreach (var caseType in cases)
        {
            if (caseType.SymbolId.IsValid)
            {
                _declarationsBySymbol[caseType.SymbolId] = caseType;
                RegisterGenericParameterKinds(caseType.SymbolId, caseType.TypeParams);
            }
            RehydrateCaseTypes(caseType.Cases);
        }
    }

}
