using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Diagnostic;

namespace Eidosc.Semantic;

/// <summary>
/// 导入解析器
/// </summary>
public sealed class ImportResolver
{
    private readonly SymbolTable _symbolTable;
    private readonly ModuleRegistry _moduleRegistry;
    private readonly PathResolver _pathResolver;
    private readonly IReadOnlyDictionary<SymbolId, ModuleDecl> _moduleDeclarations;

    // 循环导入检测
    private readonly HashSet<SymbolId> _resolvingModules = [];

    public ImportResolver(
        SymbolTable symbolTable,
        PathResolver pathResolver,
        IReadOnlyDictionary<SymbolId, ModuleDecl>? moduleDeclarations = null)
    {
        _symbolTable = symbolTable;
        _moduleRegistry = symbolTable.Modules;
        _pathResolver = pathResolver;
        _moduleDeclarations = moduleDeclarations ?? new Dictionary<SymbolId, ModuleDecl>();
    }

    /// <summary>
    /// 检测循环导入
    /// </summary>
    public bool CheckCircularImport(SymbolId moduleId, List<string> importPath)
    {
        // 检查直接循环
        if (_resolvingModules.Contains(moduleId))
        {
            return true;
        }

        // 添加当前模块到解析栈
        _resolvingModules.Add(moduleId);

        try
        {
            // 获取模块的所有导入
            var module = _moduleRegistry.GetModule(moduleId);
            if (module?.Imports != null)
            {
                foreach (var importId in module.Imports)
                {
                    // 获取导入的模块符号
                    var importedModule = _symbolTable.GetSymbol<ModuleSymbol>(importId);
                    if (importedModule != null)
                    {
                        // 递归检查
                        if (CheckCircularImport(importId, importedModule.Path))
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }
        finally
        {
                // 移除当前模块
                _resolvingModules.Remove(moduleId);
        }
    }

    /// <summary>
    /// 解析导入声明
    /// </summary>
    /// <param name=WellKnownStrings.Keywords.Import>导入声明</param>
    /// <param name="currentModule">当前模块</param>
    /// <returns>导入的符号映射</returns>
    public ImportResolutionResult Resolve(ImportDecl import, SymbolId currentModule)
    {
        // 1. 查找被导入的模块
        var moduleCandidates = _moduleRegistry.LookupImportModuleCandidates(import.PackageAlias, import.ModulePath);
        if (moduleCandidates.Count == 0)
        {
            var qualifiedModulePath = import.ToQualifiedModulePath();
            return ImportResolutionResult.Error(
                DiagnosticMessages.ModuleNotFound(
                    string.Join(WellKnownStrings.Separators.Path, qualifiedModulePath)),
                import.Span);
        }

        if (moduleCandidates.Count > 1)
        {
            var modulePath = string.Join(WellKnownStrings.Operators.Divide, import.ModulePath);
            var candidates = string.Join(", ", moduleCandidates.Select(_moduleRegistry.FormatModuleFullName));
            return ImportResolutionResult.Error(
                DiagnosticMessages.AmbiguousModulePathWithCandidates(modulePath, candidates),
                import.Span);
        }

        var moduleId = moduleCandidates[0];

        // 2. 根据导入类型处理
        return import.Kind switch
        {
            ImportKind.Module => ResolveModuleImport(import, moduleId, currentModule),
            ImportKind.Selective => ResolveSelectiveImport(import, moduleId, currentModule),
            ImportKind.Wildcard => ResolveWildcardImport(import, moduleId, currentModule),
            _ => ImportResolutionResult.Error(DiagnosticMessages.UnknownImportKind, import.Span)
        };
    }

    /// <summary>
    /// 解析模块导入 (import A.B)
    /// </summary>
    private ImportResolutionResult ResolveModuleImport(
        ImportDecl import,
        SymbolId sourceModule,
        SymbolId currentModule)
    {
        var alias = import.Alias ?? import.ModulePath[^1];
        var importedSymbols = new List<ImportedSymbol>
        {
            new()
            {
                Name = alias,
                SymbolId = sourceModule,
                Kind = ResolutionKind.Module,
                IsAliased = import.Alias != null
            }
        };

        if (import.Alias == null && !IsCompilerStdlibModule(currentModule))
        {
            AppendPublicModuleMemberImports(importedSymbols, sourceModule, currentModule);
            AppendPublicModuleInstanceMethodImports(importedSymbols, sourceModule);
        }

        AppendPublicTraitMethodImports(importedSymbols, sourceModule, currentModule);
        return ImportResolutionResult.Success(importedSymbols);
    }

    /// <summary>
    /// 解析选择性导入 (import A.B.{X, Y})
    /// </summary>
    private ImportResolutionResult ResolveSelectiveImport(
        ImportDecl import,
        SymbolId sourceModule,
        SymbolId currentModule)
    {
        var importedSymbols = new List<ImportedSymbol>();

        foreach (var selective in import.SelectiveImports)
        {
            var bindings = _moduleRegistry.GetAccessibleBindingsByName(sourceModule, selective.Name, currentModule)
                .ToList();
            if (bindings.Count == 0)
            {
                if (!TryLookupModuleInstanceMethod(sourceModule, selective.Name, out var instanceMethod))
                {
                    return ImportResolutionResult.Error(
                        DiagnosticMessages.SymbolNotFoundInModule(
                            selective.Name,
                            string.Join(WellKnownStrings.Separators.Path, import.ToQualifiedModulePath())),
                        import.Span);
                }

                bindings.Add(new ModuleBindingEntry
                {
                    Name = selective.Name,
                    SymbolId = instanceMethod.SymbolId,
                    Kind = instanceMethod.Kind
                });
            }

            // 添加到导入列表
            var localName = selective.Alias ?? selective.Name;
            foreach (var binding in bindings)
            {
                importedSymbols.Add(new ImportedSymbol
                {
                    Name = localName,
                    SymbolId = binding.SymbolId,
                    Kind = binding.Kind,
                    IsAliased = selective.Alias != null
                });
            }
        }

        return ImportResolutionResult.Success(importedSymbols);
    }

    /// <summary>
    /// 解析通配符导入 (import A.B.*)
    /// </summary>
    private ImportResolutionResult ResolveWildcardImport(
        ImportDecl import,
        SymbolId sourceModule,
        SymbolId currentModule)
    {
        var importedSymbols = new List<ImportedSymbol>();

        foreach (var binding in _moduleRegistry.GetAccessibleBindings(sourceModule, currentModule))
        {
            importedSymbols.Add(new ImportedSymbol
            {
                Name = binding.Name,
                SymbolId = binding.SymbolId,
                Kind = binding.Kind,
                IsAliased = false
            });
        }

        AppendPublicModuleInstanceMethodImports(importedSymbols, sourceModule);
        return ImportResolutionResult.Success(importedSymbols);
    }

    /// <summary>
    /// 根据符号类型获取解析类型
    /// </summary>
    private static ResolutionKind GetResolutionKind(Symbol symbol)
    {
        return symbol switch
        {
            FuncSymbol => ResolutionKind.Value,
            VarSymbol => ResolutionKind.Value,
            AdtSymbol => ResolutionKind.Type,
            CtorSymbol => ResolutionKind.Constructor,
            TraitSymbol => ResolutionKind.Type,
            EffectSymbol => ResolutionKind.Effect,
            ModuleSymbol => ResolutionKind.Module,
            // ProofSymbol removed
            _ => ResolutionKind.Value
        };
    }

    /// <summary>
    /// 模块导入额外暴露公开成员的裸名；同名冲突由使用点按歧义处理。
    /// </summary>
    private void AppendPublicModuleMemberImports(
        List<ImportedSymbol> importedSymbols,
        SymbolId sourceModule,
        SymbolId currentModule)
    {
        foreach (var binding in _moduleRegistry.GetAccessibleBindings(sourceModule, currentModule))
        {
            if (binding.Kind is not (ResolutionKind.Value or ResolutionKind.Constructor))
            {
                continue;
            }

            importedSymbols.Add(new ImportedSymbol
            {
                Name = binding.Name,
                SymbolId = binding.SymbolId,
                Kind = binding.Kind,
                IsAliased = false,
                IsImplicitModuleMember = true
            });
        }
    }

    private bool IsCompilerStdlibModule(SymbolId moduleId)
    {
        if (_moduleRegistry.GetModule(moduleId) is not { Path.Count: > 0 } module)
        {
            return false;
        }

        return string.Equals(module.PackageAlias, WellKnownStrings.Std.Module, StringComparison.Ordinal) ||
               module.Path[0] is "Core";
    }

    /// <summary>
    /// 模块导入会额外暴露公开 trait 的方法名，保持 trait 方法裸调用可见性。
    /// </summary>
    private void AppendPublicTraitMethodImports(
        List<ImportedSymbol> importedSymbols,
        SymbolId sourceModule,
        SymbolId currentModule)
    {
        foreach (var binding in _moduleRegistry.GetAccessibleBindings(sourceModule, currentModule))
        {
            if (_symbolTable.GetSymbol(binding.SymbolId) is not TraitSymbol trait)
            {
                continue;
            }

            foreach (var methodId in trait.Methods)
            {
                if (_symbolTable.GetSymbol(methodId) is not FuncSymbol method ||
                    !method.IsPublic)
                {
                    continue;
                }

                importedSymbols.Add(new ImportedSymbol
                {
                    Name = method.Name,
                    SymbolId = methodId,
                    Kind = ResolutionKind.Value,
                    IsAliased = false,
                    IsImplicitModuleMember = true,
                    IsTraitMethod = true
                });
            }
        }
    }

    private void AppendPublicModuleInstanceMethodImports(
        List<ImportedSymbol> importedSymbols,
        SymbolId sourceModule)
    {
        if (!_moduleDeclarations.TryGetValue(sourceModule, out var module))
        {
            return;
        }

        foreach (var instance in module.Declarations.OfType<InstanceDecl>())
        {
            foreach (var method in instance.Methods)
            {
                if (!method.SymbolId.IsValid ||
                    _symbolTable.GetSymbol(method.SymbolId) is not FuncSymbol { IsPublic: true })
                {
                    continue;
                }

                importedSymbols.Add(new ImportedSymbol
                {
                    Name = method.Name,
                    SymbolId = method.SymbolId,
                    Kind = ResolutionKind.Value,
                    IsAliased = false,
                    IsImplicitModuleMember = true,
                    IsTraitMethod = true
                });
            }
        }
    }

    private bool TryLookupModuleInstanceMethod(
        SymbolId moduleId,
        string memberName,
        out ImportedSymbol importedSymbol)
    {
        importedSymbol = null!;
        if (!_moduleDeclarations.TryGetValue(moduleId, out var module))
        {
            return false;
        }

        foreach (var instance in module.Declarations.OfType<InstanceDecl>())
        {
            foreach (var method in instance.Methods)
            {
                if (!string.Equals(method.Name, memberName, StringComparison.Ordinal) ||
                    !method.SymbolId.IsValid ||
                    _symbolTable.GetSymbol(method.SymbolId) is not FuncSymbol { IsPublic: true })
                {
                    continue;
                }

                importedSymbol = new ImportedSymbol
                {
                    Name = memberName,
                    SymbolId = method.SymbolId,
                    Kind = ResolutionKind.Value,
                    IsAliased = false,
                    IsTraitMethod = true
                };
                return true;
            }
        }

        return false;
    }
}
