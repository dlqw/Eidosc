using Eidosc.Symbols;
using Eidosc.Ast.Declarations;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void DeclareNestedModules(ModuleDecl module)
    {
        foreach (var decl in module.Declarations)
        {
            if (decl is not ModuleDecl childModule)
            {
                continue;
            }

            DeclareModuleTree(childModule);
        }
    }

    private void DeclareModuleTree(ModuleDecl module)
    {
        var moduleName = module.Path.Count > 0 ? module.Path[^1] : WellKnownStrings.SpecialNames.Main;
        var modulePath = module.Path.Count > 0 ? module.Path : [moduleName];
        var moduleId = _symbolTable.DeclareModule(
            moduleName,
            modulePath,
            module.Span,
            usesExplicitExports: module.UsesExplicitExports,
            packageAlias: module.PackageAlias,
            packageInstanceKey: module.PackageInstanceKey);
        module.SymbolId = moduleId;
        _moduleDeclarations[moduleId] = module;
        _symbolTable.AddMemberToModule(_currentModule, moduleId);

        using var currentModuleScope = PushCurrentModuleScope(moduleId);
        DeclareNestedModules(module);
    }

    private void CollectModuleDeclarationsRecursive(ModuleDecl module, SymbolId moduleId)
    {
        var previousModule = _currentModule;
        using var moduleScope = PushCollectionModuleScope(previousModule, moduleId);
        using var currentModuleScope = PushCurrentModuleScope(moduleId);

        var originalDeclarationCount = module.Declarations.Count;
        for (var index = 0; index < originalDeclarationCount; index++)
        {
            var decl = module.Declarations[index];
            if (decl is ImportDecl or ModuleDecl)
            {
                continue;
            }

            CollectDeclaration(decl);

            if (decl.SymbolId.IsValid)
            {
                _symbolTable.AddMemberToModule(_currentModule, decl.SymbolId);
                if (decl.IsExported)
                {
                    TryAddExportBinding(
                        _currentModule,
                        new ModuleBindingEntry
                        {
                            Name = GetExportBindingName(decl.SymbolId),
                            SymbolId = decl.SymbolId,
                            Kind = GetExportResolutionKind(decl.SymbolId)
                        },
                        decl.Span);
                }
            }
        }

        foreach (var childModule in module.Declarations.OfType<ModuleDecl>())
        {
            if (childModule.SymbolId.IsValid)
            {
                CollectModuleDeclarationsRecursive(childModule, childModule.SymbolId);
            }
        }
    }

    private void ResolveModuleReferencesRecursive(ModuleDecl module, SymbolId moduleId)
    {
        using var moduleScope = PushResolutionModuleScope(moduleId);
        using var currentModuleScope = PushCurrentModuleScope(moduleId);

        ResolveModuleReferences(module);

        foreach (var childModule in module.Declarations.OfType<ModuleDecl>())
        {
            if (childModule.SymbolId.IsValid)
            {
                ResolveModuleReferencesRecursive(childModule, childModule.SymbolId);
            }
        }
    }

    private bool EnterModuleScopeForCollection(SymbolId moduleId, SymbolId parentModuleId)
    {
        if (!moduleId.IsValid || _symbolTable.CurrentScope == null)
        {
            return false;
        }

        if (_moduleScopes.TryGetValue(moduleId, out var existingScope))
        {
            if (ReferenceEquals(_symbolTable.CurrentScope, existingScope))
            {
                return false;
            }

            _symbolTable.PushScope(existingScope);
            return true;
        }

        var parentScope = GetModuleScopeParent(parentModuleId);
        var createdScope = _symbolTable.PushScopeWithParent(ScopeKind.Module, parentScope);
        _moduleScopes[moduleId] = createdScope;
        return true;
    }

    private bool EnterModuleScopeForResolution(SymbolId moduleId)
    {
        if (!moduleId.IsValid || !_moduleScopes.TryGetValue(moduleId, out var scope))
        {
            return false;
        }

        if (ReferenceEquals(_symbolTable.CurrentScope, scope))
        {
            return false;
        }

        _symbolTable.PushScope(scope);
        return true;
    }

    private Scope? GetModuleScopeParent(SymbolId parentModuleId)
    {
        if (!parentModuleId.IsValid)
        {
            return _symbolTable.BuiltinScope;
        }

        if (!_moduleScopes.TryGetValue(parentModuleId, out var parentScope))
        {
            return _symbolTable.BuiltinScope;
        }

        return parentModuleId == _rootModule
            ? parentScope.Parent ?? _symbolTable.BuiltinScope
            : parentScope;
    }

    private void ResolveModuleReferences(ModuleDecl module)
    {
        foreach (var decl in module.Declarations)
        {
            ResolveDeclarationReferences(decl);
        }
    }
}
