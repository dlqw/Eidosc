using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Diagnostic;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    /// <summary>
    /// 处理模块中的所有导入语句
    /// </summary>
    private void ProcessImports(ModuleDecl module)
    {
        var importScope = GetOrCreateImportScope(_currentModule);

        foreach (var decl in module.Declarations)
        {
            if (decl is ImportDecl import)
            {
                CollectImportDeclaration(import, _currentModule, importScope);
            }
        }
    }

    /// <summary>
    /// 收集导入声明
    /// </summary>
    private void CollectImportDeclaration(ImportDecl import, SymbolId currentModule, ImportScope importScope)
    {
        var result = _importResolver.Resolve(import, currentModule);
        if (!result.IsSuccess)
        {
            AddError(result.ErrorSpan ?? import.Span, result.ErrorMessage ?? DiagnosticMessages.ImportError);
            return;
        }

        import.ResolvedModule = currentModule;
        import.ResolvedSymbols = result.ImportedSymbols;

        foreach (var symbol in result.ImportedSymbols)
        {
            if (symbol.Kind == ResolutionKind.Module)
            {
                importScope.AddModuleImport(symbol.Name, symbol.SymbolId);
            }
            else
            {
                importScope.AddImport(symbol);
            }

            if (import.IsExported)
            {
                TryAddExportBinding(
                    currentModule,
                    new ModuleBindingEntry
                    {
                        Name = symbol.Name,
                        SymbolId = symbol.SymbolId,
                        Kind = symbol.Kind
                    },
                    import.Span);
            }
        }
    }

    /// <summary>
    /// 获取或创建导入作用域
    /// </summary>
    private ImportScope GetOrCreateImportScope(SymbolId moduleId)
    {
        if (!_importScopes.TryGetValue(moduleId, out var scope))
        {
            scope = new ImportScope();
            _importScopes[moduleId] = scope;
        }

        return scope;
    }

    private void ProcessImportsRecursive(ModuleDecl module, SymbolId moduleId)
    {
        EnsureModuleImportsProcessed(moduleId);
    }

    private void EnsureModuleImportsProcessed(SymbolId moduleId)
    {
        if (!moduleId.IsValid ||
            _importsProcessed.Contains(moduleId) ||
            !_moduleDeclarations.TryGetValue(moduleId, out var module) ||
            !_importsProcessing.Add(moduleId))
        {
            return;
        }

        using var currentModuleScope = PushCurrentModuleScope(moduleId);

        try
        {
            foreach (var import in module.Declarations.OfType<ImportDecl>())
            {
                foreach (var dependencyId in _symbolTable.Modules.LookupImportModuleCandidates(import.PackageAlias, import.ModulePath))
                {
                    if (dependencyId.IsValid && dependencyId != moduleId)
                    {
                        EnsureModuleImportsProcessed(dependencyId);
                    }
                }
            }

            ProcessImports(module);

            foreach (var childModule in module.Declarations.OfType<ModuleDecl>())
            {
                if (childModule.SymbolId.IsValid)
                {
                    EnsureModuleImportsProcessed(childModule.SymbolId);
                }
            }

            _importsProcessed.Add(moduleId);
        }
        finally
        {
            _importsProcessing.Remove(moduleId);
        }
    }
}
