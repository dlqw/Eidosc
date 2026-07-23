using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Types;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void ResolveIdentifierReference(IdentifierExpr ident)
    {
        if (TryUseAttachedSyntaxSymbol(ident, out var attachedSymbol))
        {
            ident.IsConstructor = attachedSymbol is CtorSymbol;
            return;
        }

        if (HasUnresolvedHygienicSyntaxIdentity(ident))
        {
            AddUnresolvedHygienicIdentifierError(ident, ident.Name);
            return;
        }

        if (SelectionPlaceholderSyntax.LooksLikePlaceholder(ident.Name))
        {
            if (!SelectionPlaceholderSyntax.TryParse(ident.Name, out _, out var hasLeadingZero) && hasLeadingZero)
            {
                AddError(ident.Span, DiagnosticMessages.SelectionPlaceholderLeadingZero(ident.Name), "E4021");
                return;
            }

            var placeholderLookup = _lookupService.Lookup(
                ident.Name,
                LookupKind.Value,
                CreateLookupContext());
            if (placeholderLookup.IsSuccess)
            {
                ident.SymbolId = placeholderLookup.SymbolId;
                return;
            }

            AddError(ident.Span, DiagnosticMessages.SelectionPlaceholderOutsideArm(ident.Name), "E4020");
            return;
        }

        if (ident.Name == WellKnownStrings.Keywords.ReflConstructor)
        {
            ident.IsConstructor = true;
            return;
        }

        if (TryCollectVisibleFunctionCandidates(ident.Name, out var visibleFunctionCandidates))
        {
            ident.ClearValueCandidates();
            foreach (var candidate in visibleFunctionCandidates)
            {
                ident.AddValueCandidate(candidate);
            }

            return;
        }

        var result = _lookupService.Lookup(
            ident.Name,
            LookupKind.Value | LookupKind.Constructor,
            CreateLookupContext());
        if (result.IsSuccess)
        {
            ident.SymbolId = result.SymbolId;
            ident.IsConstructor = result.IsConstructor;
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.ErrorMessage))
        {
            if (_lookupService.TryCollectAmbiguousImportedValueCandidates(ident.Name, CreateLookupContext(), out var candidates))
            {
                ident.ClearValueCandidates();
                foreach (var candidate in candidates)
                {
                    ident.AddValueCandidate(candidate);
                }

                return;
            }

            AddError(ident.Span, result.ErrorMessage);
            return;
        }

        var typeResult = _lookupService.Lookup(
            ident.Name,
            LookupKind.Type,
            CreateLookupContext());
        if (typeResult.IsSuccess)
        {
            ident.SymbolId = typeResult.SymbolId;
            return;
        }

        AddUndefinedIdentifierError(ident.Span, ident.Name);
    }

    private LookupContext CreateLookupContext()
    {
        _importScopes.TryGetValue(_currentModule, out var importScope);
        return new LookupContext(
            _currentModule,
            importScope,
            ResolvePathWithImports,
            ResolvePackageQualifiedPath);
    }

    private void ResolvePathReference(PathExpr path)
    {
        if (TryUseAttachedSyntaxSymbol(path, out var attachedSymbol))
        {
            path.SetIsTypePath(IsTypeNamespaceSymbol(attachedSymbol.Id));
            path.SetIsConstructorPath(attachedSymbol is CtorSymbol);
            if (path.GenericArguments.Count > 0)
            {
                path.SetGenericArguments(ResolveGenericArguments(
                    path.SymbolId,
                    path.GenericArguments,
                    path.Span));
            }
            else
            {
                foreach (var typeArg in path.TypeArgs)
                {
                    ResolveTypeReferences(typeArg);
                }
            }
            return;
        }

        if (HasUnresolvedHygienicSyntaxIdentity(path))
        {
            AddUnresolvedHygienicIdentifierError(path, string.Join(WellKnownStrings.Separators.Path, path.Path));
            return;
        }

        // 反糖化：ptr_load_as[T](ptr) → ptr_load_{type}(ptr)
        //          ptr_store_as[T](ptr, val) → ptr_store_{type}(ptr, val)
        TryDesugarGenericPtrIntrinsic(path);

        if (path.ModulePath.Count == 0 &&
            string.IsNullOrWhiteSpace(path.PackageAlias) &&
            path.Name == WellKnownStrings.Keywords.ReflConstructor)
        {
            foreach (var typeArg in path.TypeArgs)
            {
                ResolveTypeReferences(typeArg);
            }

            return;
        }

        var fullPath = path.Path;
        if (TryCollectQualifiedFunctionCandidates(path, out var functionCandidates))
        {
            path.ClearValueCandidates();
            foreach (var candidate in functionCandidates)
            {
                path.AddValueCandidate(candidate);
            }

            foreach (var typeArg in path.TypeArgs)
            {
                ResolveTypeReferences(typeArg);
            }

            return;
        }

        var result = _lookupService.LookupPath(
            fullPath,
            LookupKind.Value | LookupKind.Type | LookupKind.Constructor | LookupKind.Module | LookupKind.Effect | LookupKind.Proof,
            CreateLookupContext(),
            path.PackageAlias,
            path.ModulePath.Concat([path.Name]).ToList());

        if (result.IsSuccess)
        {
            path.SymbolId = result.SymbolId;
            path.SetIsTypePath(IsTypeNamespaceSymbol(result.SymbolId));
            path.SetIsConstructorPath(result.IsConstructor);
        }
        else
        {
            AddPathResolutionError(
                path.Span,
                fullPath,
                result.ErrorMessage ?? DiagnosticMessages.CannotResolvePath(
                    string.Join(WellKnownStrings.Separators.Path, fullPath)));
        }

        if (path.GenericArguments.Count > 0)
        {
            path.SetGenericArguments(ResolveGenericArguments(
                path.SymbolId,
                path.GenericArguments,
                path.Span));
            return;
        }

        foreach (var typeArg in path.TypeArgs)
        {
            ResolveTypeReferences(typeArg);
        }
    }

    private bool TryUseAttachedSyntaxSymbol(EidosAstNode node, out Symbol symbol)
    {
        if (TryUseMappedSyntaxIdentity(node, out symbol))
        {
            return true;
        }

        var identity = node.AttachedSyntaxIdentity;
        if (identity is not { Kind: SyntaxIdentityKind.Declaration or SyntaxIdentityKind.Type or SyntaxIdentityKind.Identifier } ||
            !identity.SymbolId.IsValid ||
            node.SymbolId != identity.SymbolId ||
            _symbolTable.GetSymbol(identity.SymbolId) is not { } resolved)
        {
            symbol = null!;
            return false;
        }

        symbol = resolved;
        return true;
    }

    private bool TryCollectQualifiedFunctionCandidates(PathExpr path, out IReadOnlyList<SymbolId> candidates)
    {
        candidates = [];
        if (path.ModulePath.Count == 0 || string.IsNullOrWhiteSpace(path.Name))
        {
            return false;
        }

        var result = new List<SymbolId>();
        foreach (var moduleId in ResolveQualifiedCandidateOwnerModules(path))
        {
            if (!moduleId.IsValid)
            {
                continue;
            }

            foreach (var binding in _symbolTable.Modules.GetAccessibleBindingsByName(
                         moduleId,
                         path.Name,
                         _currentModule))
            {
                AddVisibleFunctionCandidate(result, binding.SymbolId);
            }
        }

        candidates = result.Distinct().ToArray();
        return candidates.Count > 1;
    }

    private IReadOnlyList<SymbolId> ResolveQualifiedCandidateOwnerModules(PathExpr path)
    {
        var moduleIds = new List<SymbolId>();

        if (string.IsNullOrWhiteSpace(path.PackageAlias) &&
            path.ModulePath.Count > 0 &&
            _currentModule.IsValid &&
            _importScopes.TryGetValue(_currentModule, out var importScope))
        {
            var importedModule = importScope.LookupImportedModule(path.ModulePath[0]);
            if (importedModule.HasValue && importedModule.Value.IsValid)
            {
                if (path.ModulePath.Count == 1)
                {
                    moduleIds.Add(importedModule.Value);
                }
                else
                {
                    var nestedModuleResult = ResolveImportedModulePath(
                        importedModule.Value,
                        path.ModulePath.Skip(1).ToList(),
                        allowedKinds: new HashSet<ResolutionKind> { ResolutionKind.Module });
                    if (nestedModuleResult.IsSuccess &&
                        nestedModuleResult.Kind == ResolutionKind.Module &&
                        nestedModuleResult.SymbolId.IsValid)
                    {
                        moduleIds.Add(nestedModuleResult.SymbolId);
                    }
                }
            }
        }

        foreach (var moduleId in _symbolTable.Modules.LookupImportModuleCandidates(path.PackageAlias, path.ModulePath))
        {
            if (moduleId.IsValid)
            {
                moduleIds.Add(moduleId);
            }
        }

        if (moduleIds.Count > 0)
        {
            return moduleIds;
        }

        var moduleLookup = _lookupService.LookupPath(
            path.ModulePath,
            LookupKind.Module,
            CreateLookupContext(),
            path.PackageAlias,
            path.ModulePath);
        if (moduleLookup.IsSuccess &&
            moduleLookup.Kind == ResolutionKind.Module &&
            moduleLookup.SymbolId.IsValid)
        {
            moduleIds.Add(moduleLookup.SymbolId);
        }

        return moduleIds
            .Distinct()
            .ToArray();
    }

    private void TryDesugarGenericPtrIntrinsic(PathExpr path)
    {
        if (path.Name is not (WellKnownStrings.InternalNames.PtrLoadAs or WellKnownStrings.InternalNames.PtrStoreAs))
            return;

        if (path.TypeArgs.Count == 0)
        {
            AddError(path.Span, DiagnosticMessages.PtrIntrinsicRequiresExplicitTypeArgument(path.Name));
            return;
        }

        if (path.TypeArgs.Count > 1)
        {
            AddError(path.Span, DiagnosticMessages.PtrIntrinsicRequiresExactlyOneTypeArgument(path.Name));
            return;
        }

        var typeName = ExtractTypeArgName(path.TypeArgs[0]);
        var desugared = MapPtrIntrinsicTypeToFunc(path.Name, typeName);

        if (desugared == null)
        {
            AddError(path.Span, DiagnosticMessages.UnsupportedPtrIntrinsicTypeArgument(typeName, path.Name));
            return;
        }

        path.Desugar(desugared);
    }

    private static string ExtractTypeArgName(TypeNode typeNode)
    {
        if (typeNode is TypePath typePath)
            return typePath.TypeName;
        return typeNode.GetType().Name;
    }

    private static string? MapPtrIntrinsicTypeToFunc(string intrinsic, string typeName)
    {
        return intrinsic switch
        {
            WellKnownStrings.InternalNames.PtrLoadAs => typeName switch
            {
                WellKnownStrings.BuiltinTypes.Int    => WellKnownStrings.InternalNames.PtrLoadInt,
                WellKnownStrings.BuiltinTypes.Float  => WellKnownStrings.InternalNames.PtrLoadFloat,
                WellKnownStrings.BuiltinTypes.RawPtr => WellKnownStrings.InternalNames.PtrLoadPtr,
                WellKnownStrings.BuiltinTypes.Ptr    => WellKnownStrings.InternalNames.PtrLoadPtr,
                WellKnownStrings.BuiltinTypes.Int32  => WellKnownStrings.InternalNames.PtrLoadI32,
                WellKnownStrings.BuiltinTypes.Int8   => WellKnownStrings.InternalNames.PtrLoadI8,
                WellKnownStrings.BuiltinTypes.Bool   => WellKnownStrings.InternalNames.PtrLoadBool,
                _ => null
            },
            WellKnownStrings.InternalNames.PtrStoreAs => typeName switch
            {
                WellKnownStrings.BuiltinTypes.Int    => WellKnownStrings.InternalNames.PtrStoreInt,
                WellKnownStrings.BuiltinTypes.Float  => WellKnownStrings.InternalNames.PtrStoreFloat,
                WellKnownStrings.BuiltinTypes.RawPtr => WellKnownStrings.InternalNames.PtrStorePtr,
                WellKnownStrings.BuiltinTypes.Ptr    => WellKnownStrings.InternalNames.PtrStorePtr,
                WellKnownStrings.BuiltinTypes.Int32  => WellKnownStrings.InternalNames.PtrStoreI32,
                WellKnownStrings.BuiltinTypes.Int8   => WellKnownStrings.InternalNames.PtrStoreI8,
                WellKnownStrings.BuiltinTypes.Bool   => WellKnownStrings.InternalNames.PtrStoreBool,
                _ => null
            },
            _ => null
        };
    }

    private bool TryResolveImportedModulePath(
        IReadOnlyList<string> path,
        out PathResolutionResult result)
        => TryResolveImportedModulePath(path, out result, allowedKinds: null);

    private bool TryResolveImportedModulePath(
        IReadOnlyList<string> path,
        out PathResolutionResult result,
        IReadOnlySet<ResolutionKind>? allowedKinds)
    {
        result = PathResolutionResult.NotFound(string.Empty);
        if (path.Count < 2 ||
            !_currentModule.IsValid ||
            !_importScopes.TryGetValue(_currentModule, out var importScope))
        {
            return false;
        }

        var importedModule = importScope.LookupImportedModule(path[0]);
        if (!importedModule.HasValue || !importedModule.Value.IsValid)
        {
            return false;
        }

        result = ResolveImportedModulePath(importedModule.Value, path.Skip(1).ToList(), allowedKinds);
        return result.IsSuccess;
    }

    private bool TryResolveImportedSymbolPath(
        IReadOnlyList<string> path,
        out PathResolutionResult result,
        IReadOnlySet<ResolutionKind>? allowedKinds)
    {
        result = PathResolutionResult.NotFound(string.Empty);
        if (path.Count == 0 ||
            !_currentModule.IsValid ||
            !_importScopes.TryGetValue(_currentModule, out var importScope))
        {
            return false;
        }

        var candidates = importScope.GetEffectiveImportDetails(path[0])
            .Where(candidate =>
                candidate.SymbolId.IsValid &&
                (allowedKinds == null || allowedKinds.Contains(candidate.Kind)))
            .DistinctBy(candidate => (candidate.SymbolId, candidate.Kind))
            .ToArray();
        if (candidates.Length == 0)
        {
            return false;
        }

        if (candidates.Length > 1)
        {
            result = PathResolutionResult.NotFound(
                $"Identifier '{path[0]}' is ambiguous across imported symbols in the requested semantic namespace.");
            return true;
        }

        var candidate = candidates[0];
        if (path.Count == 1)
        {
            result = PathResolutionResult.Found(candidate.SymbolId, candidate.Kind);
            return true;
        }

        result = ResolveImportedMemberPath(candidate.SymbolId, path.Skip(1).ToList(), allowedKinds);
        return true;
    }

    private PathResolutionResult ResolveImportedModulePath(SymbolId moduleId, IReadOnlyList<string> relativePath)
        => ResolveImportedModulePath(moduleId, relativePath, allowedKinds: null);

    private PathResolutionResult ResolveImportedModulePath(
        SymbolId moduleId,
        IReadOnlyList<string> relativePath,
        IReadOnlySet<ResolutionKind>? allowedKinds)
    {
        if (!moduleId.IsValid || relativePath.Count == 0)
        {
            return PathResolutionResult.NotFound(DiagnosticMessages.ImportedModulePathIsEmpty);
        }

        if ((allowedKinds == null || allowedKinds.Contains(ResolutionKind.Value)) &&
            relativePath.Count == 1 &&
            TryLookupTraitMemberInModule(moduleId, relativePath[0], out var traitMember))
        {
            return traitMember;
        }

        if (TryLookupModuleMember(moduleId, relativePath[0], out var memberResult, allowedKinds))
        {
            if (relativePath.Count == 1)
            {
                return memberResult;
            }

            var memberPathResult = ResolveImportedMemberPath(
                memberResult.SymbolId,
                relativePath.Skip(1).ToList(),
                allowedKinds);
            if (memberPathResult.IsSuccess)
            {
                return memberPathResult;
            }
        }

        if ((allowedKinds == null || allowedKinds.Contains(ResolutionKind.Effect)) &&
            relativePath.Count == 1 &&
            TryLookupSameNamedEffectMember(moduleId, relativePath[0], out var abilityMember))
        {
            return abilityMember;
        }

        if (_symbolTable.Modules.GetModule(moduleId) is { Path.Count: > 0 } importedModule)
        {
            var fullyQualifiedPath = importedModule.Path
                .Concat(relativePath)
                .ToList();
            var fullyQualifiedResult = _symbolTable.ResolvePathWithResult(fullyQualifiedPath);
            if (fullyQualifiedResult.IsSuccess &&
                (allowedKinds == null || allowedKinds.Contains(fullyQualifiedResult.Kind)))
            {
                return fullyQualifiedResult;
            }
        }

        return PathResolutionResult.NotFound(DiagnosticMessages.CannotResolveImportedModulePath(
            string.Join(WellKnownStrings.Separators.Path, relativePath)));
    }

    private PathResolutionResult ResolveImportedMemberPath(SymbolId symbolId, IReadOnlyList<string> remainingPath)
        => ResolveImportedMemberPath(symbolId, remainingPath, allowedKinds: null);

    private PathResolutionResult ResolveImportedMemberPath(
        SymbolId symbolId,
        IReadOnlyList<string> remainingPath,
        IReadOnlySet<ResolutionKind>? allowedKinds)
    {
        if (!symbolId.IsValid || remainingPath.Count == 0)
        {
            return PathResolutionResult.NotFound(DiagnosticMessages.ImportedModulePathIsEmpty);
        }

        return _symbolTable.GetSymbol(symbolId) switch
        {
            ModuleSymbol => ResolveImportedModulePath(symbolId, remainingPath, allowedKinds),
            AdtSymbol when remainingPath.Count == 1 => LookupTypeConstructor(symbolId, remainingPath[0]),
            TraitSymbol trait when remainingPath.Count == 1 &&
                                   (allowedKinds == null || allowedKinds.Contains(ResolutionKind.Value))
                => LookupTraitMethod(trait, remainingPath[0]),
            _ => PathResolutionResult.NotFound(DiagnosticMessages.CannotResolveImportedModulePath(
                string.Join(WellKnownStrings.Separators.Path, remainingPath)))
        };
    }

    private bool TryLookupModuleMember(SymbolId moduleId, string memberName, out PathResolutionResult result)
        => TryLookupModuleMember(moduleId, memberName, out result, allowedKinds: null);

    private bool TryLookupModuleMember(
        SymbolId moduleId,
        string memberName,
        out PathResolutionResult result,
        IReadOnlySet<ResolutionKind>? allowedKinds)
    {
        if (_symbolTable.Modules.TryLookupAccessibleBinding(
                moduleId,
                memberName,
                _currentModule,
                allowedKinds,
                out var binding))
        {
            result = PathResolutionResult.Found(binding.SymbolId, binding.Kind);
            return true;
        }

        if ((allowedKinds == null || allowedKinds.Contains(ResolutionKind.Value)) &&
            TryLookupModuleInstanceMethod(moduleId, memberName, out var instanceMethod))
        {
            result = instanceMethod;
            return true;
        }

        result = PathResolutionResult.NotFound(DiagnosticMessages.SymbolNotFoundInImportedModule(memberName));
        return false;
    }

    private bool TryLookupModuleInstanceMethod(
        SymbolId moduleId,
        string memberName,
        out PathResolutionResult result)
    {
        result = PathResolutionResult.NotFound(DiagnosticMessages.SymbolNotFoundInImportedModule(memberName));
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

                result = PathResolutionResult.Found(method.SymbolId, ResolutionKind.Value);
                return true;
            }
        }

        return false;
    }

    private bool TryLookupTraitMemberInModule(
        SymbolId moduleId,
        string memberName,
        out PathResolutionResult result)
    {
        result = PathResolutionResult.NotFound(DiagnosticMessages.SymbolNotFoundInImportedModule(memberName));
        var matches = new List<PathResolutionResult>();
        foreach (var binding in _symbolTable.Modules.GetAccessibleBindings(moduleId, _currentModule))
        {
            if (_symbolTable.GetSymbol(binding.SymbolId) is not TraitSymbol trait)
            {
                continue;
            }

            var candidate = LookupTraitMethod(trait, memberName);
            if (candidate.IsSuccess &&
                !matches.Any(match => match.SymbolId == candidate.SymbolId))
            {
                matches.Add(candidate);
            }
        }

        if (matches.Count != 1)
        {
            return false;
        }

        result = matches[0];
        return true;
    }

    private bool IsTypeNamespaceSymbol(SymbolId symbolId)
    {
        return symbolId.IsValid &&
               _symbolTable.GetSymbol(symbolId) is AdtSymbol or TraitSymbol or AssociatedTypeSymbol or
                   TypeParamSymbol { ParameterKind: GenericParameterKind.Type };
    }

    private bool TryLookupSameNamedEffectMember(
        SymbolId moduleId,
        string memberName,
        out PathResolutionResult result)
    {
        result = PathResolutionResult.NotFound(DiagnosticMessages.SymbolNotFoundInImportedModule(memberName));
        var module = _symbolTable.Modules.GetModule(moduleId);
        if (module == null || module.Path.Count == 0)
        {
            return false;
        }

        var ownerName = module.Path[^1];
        foreach (var binding in _symbolTable.Modules.GetAccessibleBindingsByName(
                     moduleId,
                     ownerName,
                     _currentModule,
                     EffectResolutionKinds))
        {
            _ = _symbolTable.GetSymbol(binding.SymbolId);
        }

        return false;
    }

    private bool TryResolveCurrentScopeEffectOperationPath(
        IReadOnlyList<string> path,
        out PathResolutionResult result)
    {
        result = PathResolutionResult.NotFound(string.Empty);
        if (path.Count != 2)
        {
            return false;
        }

        var abilityId = _symbolTable.LookupEffect(path[0]);
        if (!abilityId.HasValue ||
            !abilityId.Value.IsValid ||
            _symbolTable.GetSymbol(abilityId.Value) is not EffectSymbol ability)
        {
            return false;
        }

        return false;
    }

    private PathResolutionResult LookupTypeConstructor(SymbolId typeId, string constructorName)
    {
        var adt = _symbolTable.GetSymbol<AdtSymbol>(typeId);
        if (adt == null)
        {
            return PathResolutionResult.NotFound(DiagnosticMessages.TypeHasNoConstructors(typeId));
        }

        foreach (var ctorId in adt.Constructors)
        {
            var ctor = _symbolTable.GetSymbol<CtorSymbol>(ctorId);
            if (ctor?.Name == constructorName)
            {
                return PathResolutionResult.Found(ctorId, ResolutionKind.Constructor);
            }
        }

        return PathResolutionResult.NotFound(DiagnosticMessages.ConstructorNotFound(constructorName));
    }

    private PathResolutionResult LookupTraitMethod(TraitSymbol trait, string methodName)
    {
        foreach (var methodId in trait.Methods)
        {
            if (_symbolTable.GetSymbol(methodId) is FuncSymbol method &&
                string.Equals(method.Name, methodName, StringComparison.Ordinal))
            {
                return PathResolutionResult.Found(methodId, ResolutionKind.Value);
            }
        }

        return PathResolutionResult.NotFound(DiagnosticMessages.CannotResolvePath(
            $"{trait.Name}{WellKnownStrings.Separators.Path}{methodName}"));
    }

    private static ResolutionKind GetResolutionKind(Symbol symbol)
    {
        return symbol switch
        {
            FuncSymbol or VarSymbol => ResolutionKind.Value,
            AdtSymbol or TraitSymbol or AssociatedTypeSymbol => ResolutionKind.Type,
            AssociatedConstSymbol => ResolutionKind.Value,
            CtorSymbol => ResolutionKind.Constructor,
            ModuleSymbol => ResolutionKind.Module,
            EffectSymbol => ResolutionKind.Effect,
            // ProofSymbol removed
            _ => ResolutionKind.Value
        };
    }

    private SymbolId? TryResolveEffectSymbol(string abilityName, out string? errorMessage)
    {
        errorMessage = null;
        if (string.IsNullOrWhiteSpace(abilityName))
        {
            return null;
        }

        var normalized = abilityName.Trim();
        var path = normalized
            .Replace(WellKnownStrings.Separators.ModulePath, WellKnownStrings.Separators.Path, StringComparison.Ordinal)
            .Split(WellKnownStrings.Separators.Path, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        if (path.Count == 0)
        {
            return null;
        }

        var byPath = TryResolveEffectByPath(path, out errorMessage);
        if (byPath.HasValue && byPath.Value.IsValid)
        {
            return byPath.Value;
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            return null;
        }

        // 禁止全局短名兜底：能力可见性仅由本地声明、import 或限定路径决定。
        return null;
    }

    private SymbolId? TryResolveEffectByPath(IReadOnlyList<string> path, out string? errorMessage)
    {
        errorMessage = null;
        if (path.Count == 0)
        {
            return null;
        }

        if (path.Count == 1)
        {
            var builtinEffect = TryResolveBuiltinEffect(path[0]);
            if (builtinEffect.HasValue && builtinEffect.Value.IsValid)
            {
                return builtinEffect;
            }

            if (_currentModule.IsValid)
            {
                var localEffect = TryResolveEffectInModule(_currentModule, path, _currentModule);
                if (localEffect.HasValue && localEffect.Value.IsValid)
                {
                    return localEffect;
                }
            }

            if (_currentModule.IsValid && _importScopes.TryGetValue(_currentModule, out var shortPathImportScope))
            {
                var importedAbilities = GetImportedEffectSymbols(shortPathImportScope, path[0]);
                if (importedAbilities.Count > 1)
                {
                    errorMessage = BuildAmbiguousEffectImportDiagnostic(path[0], importedAbilities);
                    return null;
                }

                if (importedAbilities.Count == 1)
                {
                    return importedAbilities[0].SymbolId;
                }
            }

            return null;
        }

        var pathResult = _symbolTable.ResolvePathWithResult(path, _currentModule);
        if (pathResult.IsSuccess && pathResult.Kind == ResolutionKind.Effect)
        {
            return pathResult.SymbolId;
        }

        if (!_currentModule.IsValid || !_importScopes.TryGetValue(_currentModule, out var importScope))
        {
            return null;
        }

        var importedModule = importScope.LookupImportedModule(path[0]);
        if (importedModule.HasValue && importedModule.Value.IsValid)
        {
            return TryResolveEffectInModule(importedModule.Value, path.Skip(1).ToList(), _currentModule);
        }

        return null;
    }

    private SymbolId? TryResolveBuiltinEffect(string abilityName)
    {
        if (abilityName is not WellKnownStrings.BuiltinAbilities.FFI and not WellKnownStrings.BuiltinAbilities.IO)
        {
            return null;
        }

        var result = _symbolTable.LookupBuiltinEffect(abilityName);
        return result.HasValue &&
               result.Value.IsValid &&
               _symbolTable.GetSymbol(result.Value) is EffectSymbol
            ? result
            : null;
    }

    private List<ImportedSymbol> GetImportedEffectSymbols(ImportScope importScope, string abilityName)
    {
        var result = new List<ImportedSymbol>();
        foreach (var detail in importScope.GetImportDetails(abilityName))
        {
            if (detail.Kind != ResolutionKind.Effect)
            {
                continue;
            }

            if (!detail.SymbolId.IsValid || _symbolTable.GetSymbol(detail.SymbolId) is not EffectSymbol)
            {
                continue;
            }

            if (!result.Any(existing => existing.SymbolId.Equals(detail.SymbolId)))
            {
                result.Add(detail);
            }
        }

        return result;
    }

    private string BuildAmbiguousEffectImportDiagnostic(
        string abilityName,
        IReadOnlyList<ImportedSymbol> candidates)
    {
        var displays = new List<string>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!candidate.SymbolId.IsValid ||
                _symbolTable.GetSymbol(candidate.SymbolId) is not EffectSymbol ability)
            {
                continue;
            }

            var display = TryFormatQualifiedEffectName(candidate.SymbolId, ability);
            if (!displays.Contains(display, StringComparer.Ordinal))
            {
                displays.Add(display);
            }
        }

        displays.Sort(StringComparer.Ordinal);
        if (displays.Count == 0)
        {
            return DiagnosticMessages.AmbiguousEffect(abilityName);
        }

        return DiagnosticMessages.AmbiguousEffectWithCandidates(abilityName, string.Join(", ", displays));
    }

    private SymbolId? TryResolveEffectInModule(
        SymbolId moduleId,
        IReadOnlyList<string> relativePath,
        SymbolId? requesterModuleId)
    {
        if (!moduleId.IsValid || relativePath.Count == 0)
        {
            return null;
        }

        if (relativePath.Count == 1)
        {
            if (_symbolTable.Modules.TryLookupAccessibleBinding(
                    moduleId,
                    relativePath[0],
                    requesterModuleId,
                    out var binding) &&
                binding.Kind == ResolutionKind.Effect &&
                _symbolTable.GetSymbol(binding.SymbolId) is EffectSymbol)
            {
                return binding.SymbolId;
            }

            return null;
        }

        var nextModuleName = relativePath[0];
        if (_symbolTable.Modules.TryLookupAccessibleBinding(
                moduleId,
                nextModuleName,
                requesterModuleId,
                out var nextBinding) &&
            nextBinding.Kind == ResolutionKind.Module &&
            _symbolTable.GetSymbol(nextBinding.SymbolId) is ModuleSymbol)
        {
            return TryResolveEffectInModule(nextBinding.SymbolId, relativePath.Skip(1).ToList(), requesterModuleId);
        }

        return null;
    }

    private bool TryResolveValueSymbol(
        string name,
        bool allowConstructors,
        out SymbolId symbolId,
        out bool isConstructor,
        out string? errorMessage)
    {
        var result = _lookupService.Lookup(
            name,
            allowConstructors ? LookupKind.Value | LookupKind.Constructor : LookupKind.Value,
            CreateLookupContext());
        symbolId = result.SymbolId;
        isConstructor = result.IsConstructor;
        errorMessage = result.ErrorMessage;
        return result.IsSuccess;
    }

    private bool TryCollectVisibleFunctionCandidates(string name, out IReadOnlyList<SymbolId> candidates)
    {
        var result = new List<SymbolId>();
        var localCandidates = _symbolTable.LookupValueCandidates(name);
        if (localCandidates.Any(candidate => candidate.IsValid && _symbolTable.GetSymbol(candidate) is not FuncSymbol))
        {
            candidates = [];
            return false;
        }

        foreach (var candidate in localCandidates)
        {
            AddVisibleFunctionCandidate(result, candidate);
        }

        if (_currentModule.IsValid && _importScopes.TryGetValue(_currentModule, out var importScope))
        {
            foreach (var detail in importScope.GetImportDetails(name))
            {
                AddVisibleFunctionCandidate(result, detail.SymbolId);
            }

            AddVisibleFunctionCandidate(result, importScope.LookupImportedSymbol(name));
        }

        candidates = result.Distinct().ToArray();
        return candidates.Count > 1;
    }

    private void AddVisibleFunctionCandidate(List<SymbolId> candidates, SymbolId? symbolId)
    {
        if (symbolId is not { IsValid: true } candidate ||
            candidates.Contains(candidate) ||
            _symbolTable.GetSymbol(candidate) is not FuncSymbol
            {
                IsCStructAccessor: false,
                OwnerTrait: not { IsValid: true },
                IsTraitImplementation: false
            } ||
            IsTraitImplMethod(candidate))
        {
            return;
        }

        candidates.Add(candidate);
    }

    private bool IsTraitImplMethod(SymbolId candidate)
    {
        return GetTraitImplMethodIds().Contains(candidate);
    }

    private HashSet<SymbolId> GetTraitImplMethodIds()
    {
        if (_traitImplMethodIds is { } cached)
        {
            AddCounter("Namer.resolve.traitImplMethodCache.hits");
            return cached;
        }

        AddCounter("Namer.resolve.traitImplMethodCache.misses");
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = new HashSet<SymbolId>();
        foreach (var symbol in _symbolTable.Symbols.Values)
        {
            if (symbol is not ImplSymbol impl)
            {
                continue;
            }

            foreach (var method in impl.Methods)
            {
                if (method.IsValid)
                {
                    result.Add(method);
                }
            }

            foreach (var method in impl.TraitMethodImplementations.Values)
            {
                if (method.IsValid)
                {
                    result.Add(method);
                }
            }
        }

        _traitImplMethodIds = result;
        AddCounter("Namer.resolve.traitImplMethodCache.entries", result.Count);
        AddAllocationCounter(
            "Namer.resolve.traitImplMethodCache.build.allocatedBytes",
            GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore);
        return result;
    }
}
