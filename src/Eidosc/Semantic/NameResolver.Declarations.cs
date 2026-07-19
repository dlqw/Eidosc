using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void CollectDeclaration(Declaration decl, bool isGeneratedSource = false)
    {
        if (string.Equals(LanguageVersion, ProjectSystem.EidosLanguageVersions.Current, StringComparison.Ordinal))
        {
            foreach (var attribute in decl.Attributes)
            {
                AddError(
                    attribute.Span,
                    $"attribute '@{attribute.Name}' is not part of the 0.7 declaration model; run 'eidosc migrate clauses --to {ProjectSystem.EidosLanguageVersions.Current}'");
            }
        }

        BindDeclarationSyntaxClauses(decl, isGeneratedSource);

        switch (decl)
        {
            case FuncDef func:
                CollectFuncDef(func);
                break;
            case FuncDecl funcDecl:
                CollectFuncDecl(funcDecl);
                break;
            case LetDecl letDecl:
                CollectLetDecl(letDecl);
                break;
            case LetQuestionDecl:
                // let? 是块级顺序绑定，成功分支绑定只对后续代码可见。
                break;
            case AdtDef adt:
                CollectAdtDef(adt);
                break;
            case EffectDef ability:
                CollectEffectDef(ability);
                break;
            case TraitDef trait:
                CollectTraitDef(trait);
                break;
            case InstanceDecl instance:
                CollectInstanceDecl(instance);
                break;
            case ImportDecl:
                // 导入语句在 ProcessImports 中处理
                break;
        }

        if (!decl.SymbolId.IsValid)
        {
            return;
        }

        if (_currentModule.IsValid &&
            _symbolTable.GetSymbol(decl.SymbolId) is { DefinitionModuleId.IsValid: false } symbol)
        {
            _symbolTable.UpdateSymbol(symbol with { DefinitionModuleId = _currentModule });
        }

        _declarationsBySymbol[decl.SymbolId] = decl;

        if (decl is AdtDef adtDeclaration)
        {
            ProcessDeclarationMetaClauses(adtDeclaration);
        }
        else
        {
            ProcessDeclarationMetaClauses(decl, deriveShape: null, GetMetaTargetName(decl), [GetMetaTargetName(decl)]);
        }
    }

    private bool IsDeclarationPublic(Declaration declaration)
    {
        if (_forcedPrivateGeneratedDeclarationDepth > 0)
        {
            return false;
        }

        if (HasClause(declaration, DeclarationClauseKind.Internal))
        {
            return false;
        }

        return !_moduleDeclarations.TryGetValue(_currentModule, out var moduleDecl) ||
               !moduleDecl.UsesExplicitExports ||
               declaration.IsExported;
    }

    private void TryAddExportBinding(
        SymbolId moduleId,
        ModuleBindingEntry binding,
        SourceSpan span)
    {
        if (string.IsNullOrWhiteSpace(binding.Name) || !binding.SymbolId.IsValid)
        {
            return;
        }

        if (_symbolTable.Modules.TryAddExportToModule(moduleId, binding))
        {
            return;
        }

        AddError(
            span,
            DiagnosticMessages.DuplicateExportedName(binding.Name, FormatModuleDisplayName(moduleId)));
    }

    private string FormatModuleDisplayName(SymbolId moduleId)
    {
        var module = _symbolTable.Modules.GetModule(moduleId);
        if (module?.Path is { Count: > 0 } path)
        {
            return string.Join(WellKnownStrings.Separators.Path, path);
        }

        return WellKnownStrings.SpecialNames.Main;
    }

    private string GetExportBindingName(SymbolId symbolId)
    {
        return _symbolTable.GetSymbol(symbolId)?.Name ?? string.Empty;
    }

    private ResolutionKind GetExportResolutionKind(SymbolId symbolId)
    {
        return _symbolTable.GetSymbol(symbolId) switch
        {
            FuncSymbol => ResolutionKind.Value,
            VarSymbol => ResolutionKind.Value,
            AdtSymbol => ResolutionKind.Type,
            CtorSymbol => ResolutionKind.Constructor,
            TraitSymbol => ResolutionKind.Type,
            EffectSymbol => ResolutionKind.Effect,
            ModuleSymbol => ResolutionKind.Module,
            _ => ResolutionKind.Value
        };
    }

    private void CollectFuncDef(FuncDef func)
    {
        AddCounter("Namer.collect.funcDef.count");
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            CollectFuncDefCore(func);
        }
        finally
        {
            AddAllocationCounter(
                "Namer.collect.funcDef.allocatedBytes",
                GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore);
        }
    }

    private void CollectFuncDefCore(FuncDef func)
    {
        if (TryReportReservedInternalNameDeclaration(func.Name, func.Span, "function"))
        {
            func.SymbolId = SymbolId.None;
            return;
        }

        var bindingName = GetSyntaxBindingName(func, func.Name);
        var clausesAllocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var clauseSemantics = _clauseSemanticBinder.Bind(func, func.Name);
        AddAllocationCounter(
            "Namer.collect.funcDef.bindClauses.allocatedBytes",
            GC.GetAllocatedBytesForCurrentThread() - clausesAllocatedBytesBefore);
        AddClauseSemanticDiagnostics(clauseSemantics);
        var ffiInfo = clauseSemantics.Ffi;
        var intrinsicInfo = clauseSemantics.Intrinsic;

        // extern declarations cannot have an Eidos body.
        if (ffiInfo != null && func.Body.Count > 0)
        {
            func.SymbolId = SymbolId.None;
            return;
        }

        if (intrinsicInfo != null && func.Body.Count > 0)
        {
            AddError(func.Span, DiagnosticMessages.IntrinsicFunctionCannotHaveBody(func.Name), "E3050");
            func.SymbolId = SymbolId.None;
            return;
        }

        var hasBody = func.Body.Count > 0 && ffiInfo == null && intrinsicInfo == null;

        var isTraitImplementation = HasClause(func, DeclarationClauseKind.Impl) || _instanceMethodDeclarationDepth > 0;
        if (isTraitImplementation)
        {
            AddCounter("Namer.collect.traitImplementationFunction.count");
        }

        if (!isTraitImplementation &&
            TryReportInvalidFunctionOverloadDeclaration(bindingName, func.Signature, func.TypeParams, func.Span))
        {
            func.SymbolId = SymbolId.None;
            return;
        }

        var declareFunctionAllocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var symbolId = _symbolTable.DeclareFunction(
            bindingName,
            func.Span,
            hasBody: hasBody,
            isPublic: IsDeclarationPublic(func),
            isComptime: func.IsComptime);
        if (_symbolTable.GetSymbol<FuncSymbol>(symbolId) is { } declaredFunction)
        {
            _symbolTable.UpdateSymbol(declaredFunction with { DefinitionModuleId = _currentModule });
        }
        AddAllocationCounter(
            "Namer.collect.funcDef.declareFunction.allocatedBytes",
            GC.GetAllocatedBytesForCurrentThread() - declareFunctionAllocatedBytesBefore);
        func.SymbolId = symbolId;
        RegisterSyntaxIdentitySymbol(func, symbolId);
        RegisterGenericParameterKinds(symbolId, func.TypeParams);
        if (isTraitImplementation &&
            _symbolTable.GetSymbol<FuncSymbol>(symbolId) is { } traitImplSymbol)
        {
            var traitImplementationUpdateAllocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
            _symbolTable.UpdateSymbol(traitImplSymbol with { IsTraitImplementation = true });
            AddAllocationCounter(
                "Namer.collect.funcDef.markTraitImplementation.allocatedBytes",
                GC.GetAllocatedBytesForCurrentThread() - traitImplementationUpdateAllocatedBytesBefore);
        }
        else
        {
            var overloadRegistrationAllocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
            RegisterFunctionOverloadDeclaration(bindingName, func.Signature, func.TypeParams, func.Span, symbolId);
            AddAllocationCounter(
                "Namer.collect.funcDef.registerOverload.allocatedBytes",
                GC.GetAllocatedBytesForCurrentThread() - overloadRegistrationAllocatedBytesBefore);
        }
        var arityAllocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var arity = GetDeclaredArity(func, defaultUnaryWhenUnknown: false);
        // For extern functions, Unit -> T is equivalent to () -> T:
        // Unit carries no meaningful argument, so the call-site arity is 0.
        if (ffiInfo != null && func.Signature.Count == 1)
        {
            arity = CountDeclaredArity(func.Signature[0]);
        }
        AddAllocationCounter(
            "Namer.collect.funcDef.computeArity.allocatedBytes",
            GC.GetAllocatedBytesForCurrentThread() - arityAllocatedBytesBefore);
        var signatureUpdateAllocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        UpdateFunctionSymbolSignature(symbolId, arity);
        AddAllocationCounter(
            "Namer.collect.funcDef.updateSignature.allocatedBytes",
            GC.GetAllocatedBytesForCurrentThread() - signatureUpdateAllocatedBytesBefore);

        var metadataAllocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        if (ffiInfo != null)
        {
            var funcSymbol = _symbolTable.GetSymbol<FuncSymbol>(symbolId);
            if (funcSymbol != null)
            {
                funcSymbol.IsExternal = true;
                funcSymbol.ExternalSymbolName = ffiInfo.SymbolName;
                funcSymbol.ExternalLibrary = ffiInfo.LibraryName;
                funcSymbol.ImplicitAbilities = [WellKnownStrings.BuiltinAbilities.FFI];
            }
        }
        else if (intrinsicInfo != null)
        {
            ApplyIntrinsicMetadata(symbolId, intrinsicInfo);
        }
        else
        {
            UpdateTransparentIdentityFunctionFlag(symbolId, func);
        }
        AddAllocationCounter(
            "Namer.collect.funcDef.applyMetadata.allocatedBytes",
            GC.GetAllocatedBytesForCurrentThread() - metadataAllocatedBytesBefore);

    }

    private void AddClauseSemanticDiagnostics(DeclarationClauseSemanticBindingResult clauses)
    {
        foreach (var diagnostic in clauses.Diagnostics)
        {
            AddError(diagnostic.Span, diagnostic.Message);
        }
    }

    private void CollectFuncDecl(FuncDecl func)
    {
        AddCounter("Namer.collect.funcDecl.count");
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            CollectFuncDeclCore(func);
        }
        finally
        {
            AddAllocationCounter(
                "Namer.collect.funcDecl.allocatedBytes",
                GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore);
        }
    }

    private void CollectFuncDeclCore(FuncDecl func)
    {
        if (TryReportReservedInternalNameDeclaration(func.Name, func.Span, "function"))
        {
            func.SymbolId = SymbolId.None;
            return;
        }

        var bindingName = GetSyntaxBindingName(func, func.Name);
        var clauseSemantics = _clauseSemanticBinder.Bind(func, func.Name);
        AddClauseSemanticDiagnostics(clauseSemantics);
        var ffiInfo = clauseSemantics.Ffi;
        var intrinsicInfo = clauseSemantics.Intrinsic;

        if (TryReportInvalidFunctionOverloadDeclaration(bindingName, func.Signature, func.TypeParams, func.Span))
        {
            func.SymbolId = SymbolId.None;
            return;
        }

        var symbolId = _symbolTable.DeclareFunction(
            bindingName,
            func.Span,
            hasBody: false,
            isPublic: IsDeclarationPublic(func),
            isComptime: func.IsComptime);
        func.SymbolId = symbolId;
        RegisterSyntaxIdentitySymbol(func, symbolId);
        RegisterGenericParameterKinds(symbolId, func.TypeParams);
        RegisterFunctionOverloadDeclaration(bindingName, func.Signature, func.TypeParams, func.Span, symbolId);
        UpdateFunctionSymbolSignature(symbolId, GetDeclaredArity(func, defaultUnaryWhenUnknown: false));

        if (ffiInfo != null)
        {
            var funcSymbol = _symbolTable.GetSymbol<FuncSymbol>(symbolId);
            if (funcSymbol != null)
            {
                funcSymbol.IsExternal = true;
                funcSymbol.ExternalSymbolName = ffiInfo.SymbolName;
                funcSymbol.ExternalLibrary = ffiInfo.LibraryName;
                funcSymbol.ImplicitAbilities = [WellKnownStrings.BuiltinAbilities.FFI];
            }
        }
        else if (intrinsicInfo != null)
        {
            ApplyIntrinsicMetadata(symbolId, intrinsicInfo);
        }
    }

    private void ApplyIntrinsicMetadata(SymbolId symbolId, IntrinsicBindingInfo intrinsicInfo)
    {
        var funcSymbol = _symbolTable.GetSymbol<FuncSymbol>(symbolId);
        if (funcSymbol == null)
        {
            return;
        }

        funcSymbol.IntrinsicName = intrinsicInfo.Name;
        funcSymbol.BuiltinIntrinsicRole = IntrinsicRegistry.GetRole(intrinsicInfo.Name);

        var effects = intrinsicInfo.Effects.Count > 0
            ? intrinsicInfo.Effects
            : IntrinsicRegistry.TryGet(intrinsicInfo.Name, out var declaration)
                ? declaration.Effects
                : [];
        if (effects.Count > 0)
        {
            funcSymbol.ImplicitAbilities = effects.ToList();
        }
    }

    private void UpdateTransparentIdentityFunctionFlag(SymbolId functionId, FuncDef func)
    {
        if (_symbolTable.GetSymbol<FuncSymbol>(functionId) is not { } funcSymbol)
        {
            return;
        }

        funcSymbol.IsTransparentIdentity = IsTransparentIdentityFunction(func);
        funcSymbol.IsProofTransparent = HasClause(func, DeclarationClauseKind.Transparent);
        funcSymbol.ProofUnfoldTargetName = TryGetFirstClauseArgument(func, DeclarationClauseKind.ProofUnfold);
        funcSymbol.HasProofUnfoldTarget = HasClause(func, DeclarationClauseKind.ProofUnfold);
    }

    private static bool HasClause(Declaration declaration, DeclarationClauseKind kind) =>
        declaration.Clauses.Any(clause => clause.ClauseKind == kind);

    private static string? TryGetFirstClauseArgument(Declaration declaration, DeclarationClauseKind kind)
    {
        var clauseText = declaration.Clauses
            .Where(clause => clause.ClauseKind == kind)
            .SelectMany(static clause => clause.ArgumentTokens)
            .Select(static argument => argument.Trim().Trim('"'))
            .FirstOrDefault(static argument => !string.IsNullOrWhiteSpace(argument));
        if (!string.IsNullOrWhiteSpace(clauseText))
        {
            return clauseText;
        }

        return null;
    }

    private static bool IsTransparentIdentityFunction(FuncDef func)
    {
        if (func.Body.Count != 1)
        {
            return false;
        }

        var branch = func.Body[0];
        return branch is
        {
            Guard: null,
            Pattern: VarPattern { Name.Length: > 0 } parameter,
            Expression: IdentifierExpr { Name.Length: > 0 } identifier
        } && string.Equals(parameter.Name, identifier.Name, StringComparison.Ordinal);
    }

    private void CollectLetDecl(LetDecl letDecl)
    {
        AddCounter("Namer.collect.letDecl.count");
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            CollectLetDeclCore(letDecl);
        }
        finally
        {
            AddAllocationCounter(
                "Namer.collect.letDecl.allocatedBytes",
                GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore);
        }
    }

    private void CollectLetDeclCore(LetDecl letDecl)
    {
        if (_symbolTable.CurrentScope?.Kind != ScopeKind.Module)
        {
            return;
        }

        if (letDecl.Pattern is not VarPattern { Name.Length: > 0 } varPattern ||
            string.Equals(varPattern.Name, WellKnownStrings.Punctuation.Underscore, StringComparison.Ordinal))
        {
            return;
        }

        if (TryReportReservedInternalNameDeclaration(varPattern.Name, varPattern.Span, "value"))
        {
            varPattern.SymbolId = SymbolId.None;
            letDecl.SymbolId = SymbolId.None;
            return;
        }

        var bindingName = GetSyntaxBindingName(varPattern, varPattern.Name);

        if (CurrentScopeHasFunctionOverloadGroup(bindingName))
        {
            AddError(varPattern.Span, DiagnosticMessages.ValueConflictsWithFunctionOverload(varPattern.Name), "E3001");
            varPattern.SymbolId = SymbolId.None;
            letDecl.SymbolId = SymbolId.None;
            return;
        }

        var symbolId = _symbolTable.DeclareVariable(
            bindingName,
            varPattern.Span,
            isMutable: letDecl.IsMutable,
            isPatternBound: true,
            bindingMode: varPattern.BindingMode,
            isComptime: letDecl.IsComptime,
            isPublic: IsDeclarationPublic(letDecl));
        varPattern.SymbolId = symbolId;
        letDecl.SymbolId = symbolId;
        RegisterSyntaxIdentitySymbol(varPattern, symbolId);
        RegisterSyntaxIdentitySymbol(letDecl, symbolId);
    }

    private void CollectAdtDef(AdtDef adt)
    {
        AddCounter("Namer.collect.adtDef.count");
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            CollectAdtDefCore(adt);
        }
        finally
        {
            AddAllocationCounter(
                "Namer.collect.adtDef.allocatedBytes",
                GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore);
        }
    }

    private void CollectAdtDefCore(AdtDef adt)
    {
        if (TryReportReservedSelfDeclaration(adt.Name, adt.Span, WellKnownStrings.Keywords.Type))
        {
            adt.SymbolId = SymbolId.None;
            return;
        }

        if (TryReportReservedInternalNameDeclaration(adt.Name, adt.Span, WellKnownStrings.Keywords.Type))
        {
            adt.SymbolId = SymbolId.None;
            return;
        }

        var adtTypeParams = adt.TypeParams.Count == 0
            ? null
            : adt.TypeParams.Select(_ => SymbolId.None).ToList();
        var isPublic = IsDeclarationPublic(adt);
        var adtId = _symbolTable.DeclareAdt(
            GetSyntaxBindingName(adt, adt.Name),
            adt.Span,
            adtTypeParams,
            isPublic);
        adt.SymbolId = adtId;
        RegisterSyntaxIdentitySymbol(adt, adtId);
        RegisterGenericParameterKinds(adtId, adt.TypeParams);
        _adtDefinitions[adtId] = adt;
        CollectDeclaredFields(adt.Fields, adtId, isPublic);

        if (HasReprCClause(adt))
        {
            CollectCStructDef(adt, adtId);
            return;
        }

        // 裸积类型脱糖：`T :: type { a: A, b: B }` 合成同名默认构造子，
        // 等价于 `T :: type { T { a: A, b: B } }`。
        SynthesizeDefaultConstructorIfBareProduct(adt);

        if (adt.Cases.Count > 0)
        {
            CollectClosedCaseTypes(adt, adtId, isPublic);
        }
        else
        {
            for (var i = 0; i < adt.Constructors.Count; i++)
            {
                var ctor = adt.Constructors[i];
                if (TryReportReservedInternalNameDeclaration(ctor.Name, ctor.Span, "constructor"))
                {
                    ctor.SymbolId = SymbolId.None;
                    continue;
                }

                EidosAstNode constructorIdentityOwner = ctor.AttachedSyntaxIdentity == null ? adt : ctor;
                var ctorId = _symbolTable.DeclareConstructor(
                    GetSyntaxBindingName(constructorIdentityOwner, ctor.Name),
                    ctor.Span,
                    adtId,
                    isPublic);
                ctor.SymbolId = ctorId;
                RegisterSyntaxIdentitySymbol(constructorIdentityOwner, ctorId);
                _symbolTable.AddConstructorToAdt(adtId, ctorId);
                _symbolTable.AddMemberToModule(_currentModule, ctorId);
                _ctorPatternShapes[ctorId] = BuildCtorPatternShape(ctor);
            }
        }

    }

    private void CollectClosedCaseTypes(AdtDef adt, SymbolId rootAdtId, bool isPublic)
    {
        var leaves = new List<CaseTypeDef>();
        foreach (var caseType in adt.Cases)
        {
            CollectCaseTypeTree(caseType, rootAdtId, rootAdtId, isPublic, leaves);
        }

        if (leaves.Count != adt.Constructors.Count)
        {
            AddError(adt.Span, $"closed case lowering mismatch for '{adt.Name}'");
            return;
        }

        for (var index = 0; index < leaves.Count; index++)
        {
            var caseType = leaves[index];
            var ctor = adt.Constructors[index];
            if (TryReportReservedInternalNameDeclaration(ctor.Name, ctor.Span, "constructor"))
            {
                ctor.SymbolId = SymbolId.None;
                continue;
            }

            EidosAstNode constructorIdentityOwner = ctor.AttachedSyntaxIdentity == null ? caseType : ctor;
            var ctorId = _symbolTable.DeclareConstructor(
                GetSyntaxBindingName(constructorIdentityOwner, ctor.Name),
                ctor.Span,
                caseType.SymbolId,
                isPublic);
            ctor.SymbolId = ctorId;
            RegisterSyntaxIdentitySymbol(constructorIdentityOwner, ctorId);
            caseType.ConstructorSymbolId = ctorId;
            AddClosedCaseConstructorToAncestors(caseType.SymbolId, ctorId);
            _symbolTable.AddMemberToModule(_currentModule, ctorId);
            _ctorPatternShapes[ctorId] = BuildCtorPatternShape(ctor);

            if (_symbolTable.GetSymbol<AdtSymbol>(caseType.SymbolId) is { } caseSymbol)
            {
                _symbolTable.UpdateSymbol(caseSymbol with { CaseConstructor = ctorId });
            }
        }
    }

    private void BindDeclarationSyntaxClauses(Declaration declaration, bool isGeneratedSource = false)
    {
        var binding = DeclarationClauseBinder.Bind(
            declaration,
            LanguageVersion,
            isGeneratedSource ? CompilerOwnedSourceGrant.None : CompilerOwnedSourceGrant);
        declaration.SetBoundClauses(binding.Clauses, binding.MetaInvocations);
        foreach (var diagnostic in binding.Diagnostics)
        {
            AddError(diagnostic.Span, diagnostic.Message, diagnostic.Code);
        }
    }

    private void AddClosedCaseConstructorToAncestors(SymbolId leafCaseId, SymbolId constructorId)
    {
        var current = leafCaseId;
        var visited = new HashSet<SymbolId>();
        while (current.IsValid && visited.Add(current))
        {
            _symbolTable.AddConstructorToAdt(current, constructorId);
            current = _symbolTable.GetSymbol<AdtSymbol>(current)?.ParentAdt ?? SymbolId.None;
        }
    }

    private void CollectCaseTypeTree(
        CaseTypeDef caseType,
        SymbolId rootAdtId,
        SymbolId parentAdtId,
        bool isPublic,
        List<CaseTypeDef> leaves)
    {
        var clauseBinding = DeclarationClauseBinder.Bind(caseType, LanguageVersion, CompilerOwnedSourceGrant);
        caseType.SetBoundClauses(clauseBinding.Clauses, clauseBinding.MetaInvocations);
        foreach (var diagnostic in clauseBinding.Diagnostics)
        {
            AddError(diagnostic.Span, diagnostic.Message, diagnostic.Code);
        }

        if (isPublic && clauseBinding.Clauses.Any(static clause =>
                clause.Kind == DeclarationClauseKind.Internal && clause.HasCompilerOwnedSourceGrant))
        {
            var rootName = _symbolTable.GetSymbol<AdtSymbol>(rootAdtId)?.Name ?? "<closed-root>";
            AddError(
                caseType.Span,
                DiagnosticMessages.PublicClosedCaseRootCannotContainInternalDescendant(rootName, caseType.Name),
                nameof(ErrorCode.E3061_PublicClosedCaseContainsInternalDescendant));
        }

        if (TryReportReservedInternalNameDeclaration(caseType.Name, caseType.Span, "case type"))
        {
            caseType.SymbolId = SymbolId.None;
            return;
        }

        var typeParams = caseType.TypeParams.Count == 0
            ? null
            : caseType.TypeParams.Select(_ => SymbolId.None).ToList();
        caseType.SymbolId = _symbolTable.DeclareCaseType(
            GetSyntaxBindingName(caseType, caseType.Name),
            caseType.Span,
            parentAdtId,
            typeParams,
            isPublic);
        RegisterSyntaxIdentitySymbol(caseType, caseType.SymbolId);
        _declarationsBySymbol[caseType.SymbolId] = caseType;
        RegisterGenericParameterKinds(caseType.SymbolId, caseType.TypeParams);
        CollectDeclaredFields(caseType.Fields, caseType.SymbolId, isPublic);

        if (caseType.IsLeaf)
        {
            leaves.Add(caseType);
            return;
        }

        foreach (var child in caseType.Cases)
        {
            CollectCaseTypeTree(child, rootAdtId, caseType.SymbolId, isPublic, leaves);
        }
    }

    private void CollectDeclaredFields(
        IReadOnlyList<Field> fields,
        SymbolId ownerType,
        bool isPublic)
    {
        for (var index = 0; index < fields.Count; index++)
        {
            var field = fields[index];
            if (TryReportReservedInternalNameDeclaration(field.Name, field.Span, "field"))
            {
                field.SymbolId = SymbolId.None;
                continue;
            }

            field.SymbolId = _symbolTable.DeclareField(
                GetSyntaxBindingName(field, field.Name),
                field.Span,
                ownerType,
                index,
                isPublic);
            RegisterSyntaxIdentitySymbol(field, field.SymbolId);
        }
    }

    private static bool HasReprCClause(Declaration declaration) =>
        declaration.Clauses.Any(clause =>
            clause.ClauseKind == DeclarationClauseKind.Repr &&
            clause.ArgumentTokens.Any(argument => string.Equals(argument.Trim(), "c", StringComparison.Ordinal)));

    /// <summary>
    /// 处理 @cstruct 类型声明。
    /// 计算原生 C 布局（含对齐填充），注册字段访问器内置函数。
    /// </summary>
    private void CollectCStructDef(AdtDef adt, SymbolId adtId)
    {
        var adtSymbol = _symbolTable.GetSymbol<AdtSymbol>(adtId);
        if (adtSymbol == null)
        {
            return;
        }

        // 验证：@cstruct 必须是积类型（有字段，无构造器变体）
        if (adt.Cases.Count > 0)
        {
            AddError(adt.Span, $"repr c cannot be applied to sealed sum type '{adt.Name}'; use a product type with FFI-safe fields");
            return;
        }

        if (adt.Fields.Count == 0)
        {
            AddError(adt.Span, DiagnosticMessages.CStructTypeRequiresAtLeastOneField(adt.Name));
            return;
        }

        if (adt.Constructors.Count > 0)
        {
            AddError(adt.Span, DiagnosticMessages.CStructTypeDoesNotSupportConstructorVariants(adt.Name));
            return;
        }

        if (adt.TypeParams.Count > 0)
        {
            AddError(adt.Span, DiagnosticMessages.CStructTypeDoesNotSupportTypeParameters(adt.Name));
            return;
        }

        adtSymbol.IsCStruct = true;

        // 收集字段信息并验证 FFI 安全性
        var fieldEntries = new List<(string Name, TypeId TypeId, string TypeName)>();
        for (var i = 0; i < adt.Fields.Count; i++)
        {
            var field = adt.Fields[i];

            if (string.IsNullOrWhiteSpace(field.Name))
            {
                AddError(field.Span, DiagnosticMessages.CStructFieldMissingName(adt.Name, i + 1));
                continue;
            }

            if (field.Type is not TypePath typePath)
            {
                AddError(field.Span, DiagnosticMessages.CStructFieldTypeMustBeSimpleTypePath(adt.Name, field.Name));
                continue;
            }

            var typeName = typePath.TypeName;
            var typeInfo = CStructLayoutComputer.GetTypeInfoByName(typeName);
            if (typeInfo == null)
            {
                AddError(field.Span, DiagnosticMessages.CStructFieldTypeNotFfiSafe(adt.Name, field.Name, typeName));
                continue;
            }

            // 查找 TypeId（使用基础类型名或全局类型查找）
            var fieldTypeId = ResolveCStructFieldTypeId(typeName);
            fieldEntries.Add((field.Name, fieldTypeId, typeName));
        }

        if (fieldEntries.Count != adt.Fields.Count)
        {
            return; // 存在校验错误，不继续
        }

        // 计算布局
        var layoutFields = fieldEntries
            .Select(f => (f.Name, f.TypeId))
            .ToList();

        var layout = CStructLayoutComputer.Compute(
            adt.Name,
            layoutFields,
            typeId => GetCStructTypeSize(typeId));

        adtSymbol.CStructLayoutInfo = layout;

        // 注册字段访问器内置函数
        RegisterCStructAccessors(adt.Name, layout, adt.Span);
    }

    /// <summary>
    /// 解析 @cstruct 字段类型名称到 TypeId
    /// </summary>
    private TypeId ResolveCStructFieldTypeId(string typeName)
    {
        // 优先使用内置类型 ID
        var builtInId = BaseTypes.GetBuiltInTypeId(typeName);
        if (builtInId.IsValid)
        {
            return builtInId;
        }

        // 查找全局类型
        var symbolId = _symbolTable.LookupType(typeName);
        if (symbolId != null)
        {
            var symbol = _symbolTable.GetSymbol(symbolId.Value);
            if (symbol != null)
            {
                return symbol.TypeId;
            }
        }

        return TypeId.None;
    }

    /// <summary>
    /// 获取 C 结构体字段的 C ABI 类型大小和对齐
    /// </summary>
    private static (int Size, int Alignment) GetCStructTypeSize(TypeId typeId)
    {
        var info = CStructLayoutComputer.GetFfiTypeInfo(typeId);
        if (info != null)
        {
            return info.Value;
        }

        // 默认指针大小
        return (8, 8);
    }

    /// <summary>
    /// 为 @cstruct 的每个字段注册 getter 和 setter 内置函数。
    /// 命名规范：{struct}_{field}（getter），{struct}_{field}_set（setter）
    /// </summary>
    private void RegisterCStructAccessors(string structName, CStructLayout layout, SourceSpan span)
    {
        var prefix = structName.ToLowerInvariant();

        foreach (var field in layout.Fields)
        {
            // Getter: {prefix}_{field}(ptr) -> FieldType
            var getterName = $"{prefix}_{field.Name}";
            _symbolTable.RegisterCStructAccessor(getterName, span, field.Offset, field.TypeId, isGetter: true);

            // Setter: {prefix}_{field}_set(ptr, value) -> Unit
            var setterName = $"{prefix}_{field.Name}_set";
            _symbolTable.RegisterCStructAccessor(setterName, span, field.Offset, field.TypeId, isGetter: false);
        }
    }

    private static CtorPatternShape BuildCtorPatternShape(Constructor ctor)
    {
        var namedFields = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in ctor.NamedArgs)
        {
            if (!string.IsNullOrWhiteSpace(field.Name))
            {
                namedFields.Add(field.Name);
            }
        }

        // Constructor shape is always known after AST extraction:
        // - nullary ctor => positional arity 0, named fields 0
        // - positional/named ctor => collected from ctor args
        var isShapeKnown = true;
        return new CtorPatternShape(
            IsShapeKnown: isShapeKnown,
            PositionalArity: ctor.PositionalArgs.Count,
            NamedFields: namedFields);
    }

    private void CollectEffectDef(EffectDef ability)
    {
        AddCounter("Namer.collect.abilityDef.count");
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            CollectEffectDefCore(ability);
        }
        finally
        {
            AddAllocationCounter(
                "Namer.collect.abilityDef.allocatedBytes",
                GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore);
        }
    }

    private void CollectEffectDefCore(EffectDef ability)
    {
        if (TryReportReservedSelfDeclaration(ability.Name, ability.Span, WellKnownStrings.Keywords.Effect))
        {
            ability.SymbolId = SymbolId.None;
            return;
        }

        if (TryReportReservedInternalNameDeclaration(ability.Name, ability.Span, WellKnownStrings.Keywords.Effect))
        {
            ability.SymbolId = SymbolId.None;
            return;
        }

        var isPublic = IsDeclarationPublic(ability);
        var abilityId = _symbolTable.DeclareEffect(
            GetSyntaxBindingName(ability, ability.Name),
            ability.Span,
            isPublic);
        ability.SymbolId = abilityId;
        RegisterSyntaxIdentitySymbol(ability, abilityId);
    }

    private void CollectTraitDef(TraitDef trait)
    {
        AddCounter("Namer.collect.traitDef.count");
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            CollectTraitDefCore(trait);
        }
        finally
        {
            AddAllocationCounter(
                "Namer.collect.traitDef.allocatedBytes",
                GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore);
        }
    }

    private void CollectTraitDefCore(TraitDef trait)
    {
        if (TryReportReservedSelfDeclaration(trait.Name, trait.Span, WellKnownStrings.Keywords.Trait))
        {
            trait.SymbolId = SymbolId.None;
            return;
        }

        if (TryReportReservedInternalNameDeclaration(trait.Name, trait.Span, WellKnownStrings.Keywords.Trait))
        {
            trait.SymbolId = SymbolId.None;
            return;
        }

        var traitTypeParams = trait.TypeParams.Count == 0
            ? null
            : trait.TypeParams.Select(_ => SymbolId.None).ToList();
        var isPublic = IsDeclarationPublic(trait);
        var traitId = _symbolTable.DeclareTrait(
            GetSyntaxBindingName(trait, trait.Name),
            trait.Span,
            traitTypeParams,
            isPublic);
        trait.SymbolId = traitId;
        RegisterSyntaxIdentitySymbol(trait, traitId);
        RegisterGenericParameterKinds(traitId, trait.TypeParams);
        _traitDefinitions[traitId] = trait;
        _traitOwnerModules[traitId] = _currentModule;

        foreach (var associatedType in trait.AssociatedTypes)
        {
            if (TryReportReservedInternalNameDeclaration(associatedType.Name, associatedType.Span, "associated type"))
            {
                associatedType.SymbolId = SymbolId.None;
                continue;
            }

            CollectAssociatedTypeSymbol(associatedType, traitId, SymbolId.None, isPublic);
        }

        foreach (var associatedConst in trait.AssociatedConsts)
        {
            if (TryReportReservedInternalNameDeclaration(associatedConst.Name, associatedConst.Span, "associated const"))
            {
                associatedConst.SymbolId = SymbolId.None;
                continue;
            }

            CollectAssociatedConstSymbol(associatedConst, traitId, SymbolId.None, isPublic);
        }

        // Resolve supertrait references and populate ParentTraits
        var parentTraitIds = ResolveSupertraitReferences(trait);

        var traitMethodOverloads = new Dictionary<string, List<FunctionOverloadDeclaration>>(StringComparer.Ordinal);
        foreach (var method in trait.Methods)
        {
            BindDeclarationSyntaxClauses(method);

            if (TryReportReservedInternalNameDeclaration(method.Name, method.Span, "trait method"))
            {
                method.SymbolId = SymbolId.None;
                continue;
            }

            if (TryReportInvalidTraitMethodOverloadDeclaration(trait.Name, method, traitMethodOverloads))
            {
                method.SymbolId = SymbolId.None;
                continue;
            }

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
            RegisterTraitMethodOverloadDeclaration(method, methodId, traitMethodOverloads);
            UpdateFunctionSymbolSignature(methodId, GetDeclaredArity(method, defaultUnaryWhenUnknown: true));
            var methodSelfUsage = AnalyzeTraitMethodSelfUsage(trait, method);

            if (_symbolTable.GetSymbol(methodId) is FuncSymbol methodSymbol)
            {
                _symbolTable.UpdateSymbol(methodSymbol with
                {
                    DefinitionModuleId = _currentModule,
                    OwnerTrait = traitId,
                    TraitSelfPosition = methodSelfUsage.Position,
                    TraitSelfParameterIndices = methodSelfUsage.ParameterIndices,
                    TraitSelfInResult = methodSelfUsage.InResult,
                    TraitMethodRole = ResolveTraitMethodRole(trait, method),
                    IsDefaultImplementation = hasDefaultBody
                });
            }

            if (_symbolTable.GetSymbol(traitId) is TraitSymbol traitSymbol)
            {
                var methods = new List<SymbolId>(traitSymbol.Methods) { methodId };
                _symbolTable.UpdateSymbol(traitSymbol with { Methods = methods });
            }

            _declarationsBySymbol[methodId] = method;
            ProcessDeclarationMetaClauses(method, deriveShape: null, method.Name, [trait.Name, method.Name]);
        }

        // Proof collection removed during migration

        // Derive SelfPosition from method signatures for dispatch strategy
        var selfPosition = DeriveTraitSelfPosition(trait);
        if (_symbolTable.GetSymbol(traitId) is TraitSymbol finalTraitSymbol)
        {
            _symbolTable.UpdateSymbol(finalTraitSymbol with
            {
                SelfPosition = selfPosition,
                ParentTraits = parentTraitIds
            });
        }
    }

    private static bool TryReportInvalidTraitMethodOverloadDeclaration(
        string traitName,
        FuncDef method,
        Dictionary<string, List<FunctionOverloadDeclaration>> declarations,
        out string diagnosticMessage)
    {
        diagnosticMessage = string.Empty;
        var signatureKey = BuildFunctionOverloadSignatureKey(method.Name, method.Signature, method.TypeParams);
        if (!declarations.TryGetValue(method.Name, out var overloads))
        {
            return false;
        }

        var duplicate = overloads.FirstOrDefault(overload =>
            string.Equals(overload.SignatureKey, signatureKey, StringComparison.Ordinal));
        if (duplicate == null)
        {
            return false;
        }

        diagnosticMessage = $"{DiagnosticMessages.DuplicateFunctionOverloadSignature(method.Name, signatureKey)} in trait '{traitName}'";
        return true;
    }

    private bool TryReportInvalidTraitMethodOverloadDeclaration(
        string traitName,
        FuncDef method,
        Dictionary<string, List<FunctionOverloadDeclaration>> declarations)
    {
        if (!TryReportInvalidTraitMethodOverloadDeclaration(traitName, method, declarations, out var message))
        {
            return false;
        }

        AddError(method.Span, message, "E3001");
        return true;
    }

    private static void RegisterTraitMethodOverloadDeclaration(
        FuncDef method,
        SymbolId symbolId,
        Dictionary<string, List<FunctionOverloadDeclaration>> declarations)
    {
        if (!declarations.TryGetValue(method.Name, out var overloads))
        {
            overloads = [];
            declarations[method.Name] = overloads;
        }

        overloads.Add(new FunctionOverloadDeclaration(
            method.Name,
            BuildFunctionOverloadSignatureKey(method.Name, method.Signature, method.TypeParams),
            method.Span,
            symbolId));
    }

    private void CollectInstanceDecl(InstanceDecl instance)
    {
        AddCounter("Namer.collect.instanceDecl.count");
        var allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        try
        {
            CollectInstanceDeclCore(instance);
        }
        finally
        {
            AddAllocationCounter(
                "Namer.collect.instanceDecl.allocatedBytes",
                GC.GetAllocatedBytesForCurrentThread() - allocatedBytesBefore);
        }
    }

    private void CollectInstanceDeclCore(InstanceDecl instance)
    {
        if (TryReportReservedInternalNameDeclaration(instance.Name, instance.Span, "instance"))
        {
            instance.SymbolId = SymbolId.None;
            return;
        }

        if (!string.IsNullOrWhiteSpace(instance.Name) &&
            !_instanceDeclarations.TryAdd(instance.Name, instance))
        {
            AddError(instance.Span, $"Duplicate instance declaration '{instance.Name}'.");
        }

        if (!instance.SymbolId.IsValid)
        {
            instance.SymbolId = _symbolTable.DeclarePendingImpl(
                GetSyntaxBindingName(instance, instance.Name),
                instance.Span,
                IsDeclarationPublic(instance));
            RegisterSyntaxIdentitySymbol(instance, instance.SymbolId);
        }

        var instanceIsPublic = IsDeclarationPublic(instance);
        foreach (var associatedType in instance.AssociatedTypes)
        {
            if (TryReportReservedInternalNameDeclaration(associatedType.Name, associatedType.Span, "associated type"))
            {
                associatedType.SymbolId = SymbolId.None;
                continue;
            }

            CollectAssociatedTypeSymbol(associatedType, SymbolId.None, instance.SymbolId, instanceIsPublic);
        }

        foreach (var associatedConst in instance.AssociatedConsts)
        {
            if (TryReportReservedInternalNameDeclaration(associatedConst.Name, associatedConst.Span, "associated const"))
            {
                associatedConst.SymbolId = SymbolId.None;
                continue;
            }

            CollectAssociatedConstSymbol(associatedConst, SymbolId.None, instance.SymbolId, instanceIsPublic);
        }

        _instanceMethodDeclarationDepth++;
        try
        {
            foreach (var method in instance.Methods)
            {
                BindDeclarationSyntaxClauses(method);
                CollectFuncDef(method);
                if (!method.SymbolId.IsValid)
                {
                    continue;
                }

                _declarationsBySymbol[method.SymbolId] = method;
                ProcessDeclarationMetaClauses(
                    method,
                    deriveShape: null,
                    method.Name,
                    [instance.Name, method.Name]);
            }
        }
        finally
        {
            _instanceMethodDeclarationDepth--;
        }
    }

    private SymbolId CollectAssociatedTypeSymbol(
        AssociatedTypeDecl associatedType,
        SymbolId ownerTrait,
        SymbolId ownerImpl,
        bool isPublic,
        GeneratedDeclarationOrigin? origin = null)
    {
        var typeParams = associatedType.TypeParams.Count == 0
            ? null
            : associatedType.TypeParams.Select(static _ => SymbolId.None).ToArray();
        var symbolId = _symbolTable.DeclareAssociatedType(
            GetSyntaxBindingName(associatedType, associatedType.Name),
            associatedType.Span,
            _currentModule,
            ownerTrait,
            ownerImpl,
            typeParams,
            isPublic);
        associatedType.SymbolId = symbolId;
        RegisterSyntaxIdentitySymbol(associatedType, symbolId);
        if (origin != null)
        {
            SetGeneratedOrigin(symbolId, origin);
        }
        AddAssociatedItemToOwner(symbolId, ownerTrait, ownerImpl, isType: true);
        return symbolId;
    }

    private SymbolId CollectAssociatedConstSymbol(
        AssociatedConstDecl associatedConst,
        SymbolId ownerTrait,
        SymbolId ownerImpl,
        bool isPublic,
        GeneratedDeclarationOrigin? origin = null)
    {
        var symbolId = _symbolTable.DeclareAssociatedConst(
            GetSyntaxBindingName(associatedConst, associatedConst.Name),
            associatedConst.Span,
            _currentModule,
            ownerTrait,
            ownerImpl,
            isPublic);
        associatedConst.SymbolId = symbolId;
        RegisterSyntaxIdentitySymbol(associatedConst, symbolId);
        if (origin != null)
        {
            SetGeneratedOrigin(symbolId, origin);
        }
        AddAssociatedItemToOwner(symbolId, ownerTrait, ownerImpl, isType: false);
        return symbolId;
    }

    private void AddAssociatedItemToOwner(
        SymbolId itemId,
        SymbolId ownerTrait,
        SymbolId ownerImpl,
        bool isType)
    {
        if (ownerTrait.IsValid && _symbolTable.GetSymbol<TraitSymbol>(ownerTrait) is { } trait)
        {
            _symbolTable.UpdateSymbol(isType
                ? trait with { AssociatedTypes = [.. trait.AssociatedTypes, itemId] }
                : trait with { AssociatedConsts = [.. trait.AssociatedConsts, itemId] });
        }

        if (ownerImpl.IsValid && _symbolTable.GetSymbol<ImplSymbol>(ownerImpl) is { } impl)
        {
            _symbolTable.UpdateSymbol(isType
                ? impl with { AssociatedTypes = [.. impl.AssociatedTypes, itemId] }
                : impl with { AssociatedConsts = [.. impl.AssociatedConsts, itemId] });
        }
    }

    /// <summary>
    /// Resolves supertrait TraitRef references from the AST to SymbolIds.
    /// Reports diagnostics for undefined supertraits, duplicates, and self-references.
    /// </summary>
    private List<SymbolId> ResolveSupertraitReferences(TraitDef trait)
    {
        if (trait.SuperTraits.Count == 0)
        {
            return [];
        }

        var parentTraitIds = new List<SymbolId>();
        var seenNames = new HashSet<string>(StringComparer.Ordinal);

        foreach (var superRef in trait.SuperTraits)
        {
            var displayName = superRef.TraitName;

            // Self-reference check
            if (string.Equals(displayName, trait.Name, StringComparison.Ordinal))
            {
                AddError(superRef.Span,
                    DiagnosticMessages.SelfReferentialSupertrait(trait.Name),
                    nameof(ErrorCode.E3059_SelfReferentialSupertrait));
                continue;
            }

            // Duplicate check
            if (!seenNames.Add(displayName))
            {
                AddError(superRef.Span,
                    DiagnosticMessages.DuplicateSupertrait(displayName, trait.Name),
                    nameof(ErrorCode.E3060_DuplicateSupertrait));
                continue;
            }

            // Resolve the trait reference
            var traitPath = new List<string>();
            if (superRef.ModulePath.Count > 0)
            {
                traitPath.AddRange(superRef.ModulePath);
            }
            traitPath.Add(displayName);

            var result = ResolvePathWithImports(traitPath);
            if (result.IsSuccess && _symbolTable.GetSymbol(result.SymbolId) is TraitSymbol parentTrait)
            {
                // Check type argument count
                var expectedArgs = parentTrait.TypeParams.Count;
                var actualArgs = superRef.TypeArgs.Count;
                if (expectedArgs != actualArgs)
                {
                    AddError(superRef.Span,
                        DiagnosticMessages.SupertraitTypeArgumentCountMismatch(displayName, expectedArgs, actualArgs, trait.Name),
                        nameof(ErrorCode.E3058_SupertraitTypeArgumentCountMismatch));
                    continue;
                }

                parentTraitIds.Add(result.SymbolId);
                superRef.SymbolId = result.SymbolId;
            }
            else
            {
                // Fallback: try simple name lookup
                var fallback = _symbolTable.LookupTrait(displayName);
                if (fallback is { } fallbackId && _symbolTable.GetSymbol(fallbackId) is TraitSymbol)
                {
                    parentTraitIds.Add(fallbackId);
                    superRef.SymbolId = fallbackId;
                }
                else
                {
                    AddError(superRef.Span,
                        DiagnosticMessages.UndefinedSupertrait(displayName, trait.Name),
                        nameof(ErrorCode.E3057_UndefinedSupertrait));
                }
            }
        }

        return parentTraitIds;
    }

    // Proof collection removed during migration

    /// <summary>
    /// Detects cycles in the supertrait graph across all declared traits.
    /// Must be called after all traits have been collected (ParentTraits populated).
    /// Reports E3056_CyclicSupertrait for each cycle found.
    /// </summary>
    private void DetectSupertraitCycles()
    {
        // Collect all traits that have parent traits
        var traitsWithParents = new List<(SymbolId Id, TraitSymbol Symbol, TraitDef Ast)>();
        foreach (var (traitId, traitDef) in _traitDefinitions)
        {
            if (_symbolTable.GetSymbol(traitId) is TraitSymbol traitSymbol &&
                traitSymbol.ParentTraits.Count > 0)
            {
                traitsWithParents.Add((traitId, traitSymbol, traitDef));
            }
        }

        if (traitsWithParents.Count == 0)
        {
            return;
        }

        // DFS cycle detection with full path tracking
        var visited = new HashSet<SymbolId>();
        var onStack = new HashSet<SymbolId>();
        var stackPath = new List<SymbolId>();
        var reported = new HashSet<SymbolId>();

        foreach (var (startId, _, _) in traitsWithParents)
        {
            if (visited.Contains(startId))
            {
                continue;
            }

            DfsCycleCheck(startId, visited, onStack, stackPath, reported);
        }
    }

    private void DfsCycleCheck(
        SymbolId current,
        HashSet<SymbolId> visited,
        HashSet<SymbolId> onStack,
        List<SymbolId> stackPath,
        HashSet<SymbolId> reported)
    {
        visited.Add(current);
        onStack.Add(current);
        stackPath.Add(current);

        if (_symbolTable.GetSymbol(current) is TraitSymbol traitSymbol)
        {
            foreach (var parentId in traitSymbol.ParentTraits)
            {
                if (!parentId.IsValid)
                {
                    continue;
                }

                if (onStack.Contains(parentId))
                {
                    // Found a cycle — extract the cycle path
                    if (!reported.Contains(parentId))
                    {
                        reported.Add(parentId);
                        var cycleStart = stackPath.IndexOf(parentId);
                        var cyclePath = stackPath[cycleStart..];
                        var cycleNames = cyclePath
                            .Select(id => _symbolTable.GetSymbol(id) is TraitSymbol ts ? ts.Name : "?")
                            .Append(_symbolTable.GetSymbol(parentId) is TraitSymbol ps ? ps.Name : "?");
                        var pathStr = string.Join(" → ", cycleNames);

                        // Report on the trait that closes the cycle
                        var cycleTraitDef = _traitDefinitions.GetValueOrDefault(current);
                        var span = cycleTraitDef?.Span ?? SourceSpan.Empty;
                        AddError(span,
                            DiagnosticMessages.CyclicSupertrait(
                                _symbolTable.GetSymbol(current) is TraitSymbol cts ? cts.Name : "?",
                                pathStr),
                            nameof(ErrorCode.E3056_CyclicSupertrait));
                    }
                    continue;
                }

                if (!visited.Contains(parentId))
                {
                    DfsCycleCheck(parentId, visited, onStack, stackPath, reported);
                }
            }
        }

        stackPath.RemoveAt(stackPath.Count - 1);
        onStack.Remove(current);
    }

    private void UpdateFunctionSymbolSignature(SymbolId functionId, int arity)
    {
        if (_symbolTable.GetSymbol(functionId) is not FuncSymbol functionSymbol)
        {
            return;
        }

        var normalizedArity = Math.Max(0, arity);
        _symbolTable.UpdateSymbol(functionSymbol with
        {
            Parameters = CreatePlaceholderParameters(normalizedArity),
            ParamTypes = CreatePlaceholderParamTypes(normalizedArity)
        });
    }

    private static List<SymbolId> CreatePlaceholderParameters(int arity)
    {
        arity = Math.Max(0, arity);
        var parameters = new List<SymbolId>(arity);
        for (var i = 0; i < arity; i++)
        {
            parameters.Add(SymbolId.None);
        }

        return parameters;
    }

    private static List<TypeId> CreatePlaceholderParamTypes(int arity)
    {
        arity = Math.Max(0, arity);
        var paramTypes = new List<TypeId>(arity);
        for (var i = 0; i < arity; i++)
        {
            paramTypes.Add(TypeId.None);
        }

        return paramTypes;
    }

    private bool TryReportInvalidFunctionOverloadDeclaration(
        string name,
        IReadOnlyList<TypeNode> signature,
        IReadOnlyList<TypeParam> typeParams,
        SourceSpan span)
    {
        AddCounter("Namer.collect.overloadValidation.count");
        var currentScope = _symbolTable.CurrentScope;
        if (currentScope == null)
        {
            return false;
        }

        var localCandidates = _symbolTable.LookupLocalValueCandidates(name);
        if (localCandidates.Any(candidate => _symbolTable.GetSymbol(candidate) is not FuncSymbol))
        {
            AddError(span, DiagnosticMessages.FunctionOverloadConflictsWithValue(name), "E3001");
            return true;
        }

        var signatureKeyAllocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var signatureKey = BuildFunctionOverloadSignatureKey(name, signature, typeParams);
        AddAllocationCounter(
            "Namer.collect.overloadValidation.signatureKey.allocatedBytes",
            GC.GetAllocatedBytesForCurrentThread() - signatureKeyAllocatedBytesBefore);
        if (!_functionOverloadDeclarations.TryGetValue(currentScope, out var declarations) ||
            !declarations.TryGetValue(name, out var overloads))
        {
            return false;
        }

        var duplicate = overloads.FirstOrDefault(overload =>
            string.Equals(overload.SignatureKey, signatureKey, StringComparison.Ordinal));
        if (duplicate == null)
        {
            return false;
        }

        AddError(span, DiagnosticMessages.DuplicateFunctionOverloadSignature(name, signatureKey), "E3001");
        return true;
    }

    private void RegisterFunctionOverloadDeclaration(
        string name,
        IReadOnlyList<TypeNode> signature,
        IReadOnlyList<TypeParam> typeParams,
        SourceSpan span,
        SymbolId symbolId)
    {
        AddCounter("Namer.collect.overloadRegistration.count");
        var currentScope = _symbolTable.CurrentScope;
        if (currentScope == null || !symbolId.IsValid)
        {
            return;
        }

        if (!_functionOverloadDeclarations.TryGetValue(currentScope, out var declarations))
        {
            declarations = new Dictionary<string, List<FunctionOverloadDeclaration>>(StringComparer.Ordinal);
            _functionOverloadDeclarations[currentScope] = declarations;
        }

        if (!declarations.TryGetValue(name, out var overloads))
        {
            overloads = [];
            declarations[name] = overloads;
        }

        var signatureKeyAllocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
        var signatureKey = BuildFunctionOverloadSignatureKey(name, signature, typeParams);
        AddAllocationCounter(
            "Namer.collect.overloadRegistration.signatureKey.allocatedBytes",
            GC.GetAllocatedBytesForCurrentThread() - signatureKeyAllocatedBytesBefore);

        overloads.Add(new FunctionOverloadDeclaration(
            name,
            signatureKey,
            span,
            symbolId));
    }

    private bool CurrentScopeHasFunctionOverloadGroup(string name)
    {
        return _symbolTable.LookupLocalValueCandidates(name)
            .Any(candidate => _symbolTable.GetSymbol(candidate) is FuncSymbol { IsTraitImplementation: false });
    }

    private static string BuildFunctionOverloadSignatureKey(
        string name,
        IReadOnlyList<TypeNode> signature,
        IReadOnlyList<TypeParam> typeParams)
    {
        var (paramTypes, _) = ExtractSignatureTypes(signature);
        var typeParamMap = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < typeParams.Count; i++)
        {
            var typeParam = typeParams[i];
            if (!string.IsNullOrWhiteSpace(typeParam.Name))
            {
                typeParamMap[typeParam.Name] = $"T{i}";
            }
        }

        var typeParamText = "<non-generic>";
        if (typeParams.Count > 0)
        {
            var builder = new System.Text.StringBuilder();
            for (var i = 0; i < typeParams.Count; i++)
            {
                if (i > 0)
                {
                    builder.Append(',');
                }

                builder.Append(typeParams[i].GetKindText());
            }

            typeParamText = builder.ToString();
        }

        var paramText = paramTypes.Count == 0
            ? "<none>"
            : JoinNormalizedOverloadTypes(paramTypes, typeParamMap);
        return $"{name}[{typeParamText}]({paramText})";
    }

    private static string JoinNormalizedOverloadTypes(
        IReadOnlyList<TypeNode> types,
        IReadOnlyDictionary<string, string> typeParamMap)
    {
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < types.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(',');
            }

            builder.Append(NormalizeOverloadTypeNode(types[i], typeParamMap));
        }

        return builder.ToString();
    }

    private static string NormalizeOverloadTypeNode(
        TypeNode node,
        IReadOnlyDictionary<string, string> typeParamMap)
    {
        return node switch
        {
            TypePath typePath => NormalizeOverloadTypePath(typePath, typeParamMap),
            ArrowType arrow => $"{NormalizeOverloadTypeNode(arrow.ParamType, typeParamMap)}->{NormalizeOverloadTypeNode(arrow.ReturnType, typeParamMap)}",
            EffectfulType effectful => $"{NormalizeOverloadTypeNode(effectful.InputType, typeParamMap)}=>{NormalizeOverloadTypeNode(effectful.OutputType ?? CreateUnitTypePath(effectful.Span), typeParamMap)}",
            TupleType tuple => $"({JoinNormalizedOverloadTypes(tuple.Elements, typeParamMap)})",
            WildcardType => "_",
            _ => node.GetType().Name
        };
    }

    private static TypePath CreateUnitTypePath(SourceSpan span)
    {
        var unit = new TypePath();
        unit.SetTypeName(WellKnownStrings.BuiltinTypes.Unit);
        unit.SetSpan(span);
        return unit;
    }

    private static string NormalizeOverloadTypePath(
        TypePath typePath,
        IReadOnlyDictionary<string, string> typeParamMap)
    {
        var name = typePath.ModulePath.Count > 0
            ? string.Join(WellKnownStrings.Separators.Path, typePath.ModulePath) + WellKnownStrings.Separators.Path + typePath.TypeName
            : typePath.TypeName;
        if (typeParamMap.TryGetValue(name, out var normalizedTypeParam))
        {
            name = normalizedTypeParam;
        }

        if (typePath.TypeArgs.Count == 0)
        {
            return name;
        }

        return $"{name}[{JoinNormalizedOverloadTypes(typePath.TypeArgs, typeParamMap)}]";
    }

    private static int GetDeclaredArity(FuncDecl function, bool defaultUnaryWhenUnknown)
    {
        if (function.Signature.Count == 0)
        {
            if (defaultUnaryWhenUnknown)
            {
                return 1;
            }

            return 0;
        }

        if (function.Signature.Count == 1)
        {
            // For function declarations (no body: @ffi, trait methods),
            // Unit -> T is equivalent to () -> T — Unit carries no
            // meaningful argument at the call site.
            var arity = CountDeclaredArity(function.Signature[0]);
            if (arity == 0 && defaultUnaryWhenUnknown)
            {
                return 1;
            }

            return arity;
        }

        return Math.Max(0, function.Signature.Count - 1);
    }

    /// <summary>
    /// Counts declared arity for a function declaration (FuncDecl).
    /// Unit parameters are not counted because Unit is a singleton type
    /// whose only value () carries no information and can be elided.
    /// This makes @ffi func f: Unit -> T callable as f().
    /// </summary>
    private static int CountDeclaredArity(TypeNode typeNode)
    {
        return typeNode switch
        {
            ArrowType arrow when arrow.ParamType is TypePath { TypeName: WellKnownStrings.BuiltinTypes.Unit, TypeArgs.Count: 0 }
                => CountDeclaredArity(arrow.ReturnType),
            ArrowType arrow => 1 + CountDeclaredArity(arrow.ReturnType),
            EffectfulType => 1,
            _ => 0
        };
    }

    private static int GetDeclaredArity(FuncDef function, bool defaultUnaryWhenUnknown)
    {
        if (function.Signature.Count == 0)
        {
            if (defaultUnaryWhenUnknown)
            {
                return 1;
            }

            return 0;
        }

        if (function.Signature.Count == 1)
        {
            var arity = CountTypeArity(function.Signature[0]);
            if (arity == 0 && defaultUnaryWhenUnknown)
            {
                return 1;
            }

            return arity;
        }

        return Math.Max(0, function.Signature.Count - 1);
    }

    private static int CountTypeArity(TypeNode typeNode)
    {
        return typeNode switch
        {
            ArrowType arrow => 1 + CountTypeArity(arrow.ReturnType),
            EffectfulType => 1,
            _ => 0
        };
    }

    /// <summary>
    /// Derives the <see cref="SelfPosition"/> for a trait by analyzing all method signatures.
    /// </summary>
    private static SelfPosition DeriveTraitSelfPosition(TraitDef trait)
    {
        bool hasSelfInParam = false;
        bool hasSelfInResult = false;

        var selfTypeNames = GetTraitSelfTypeNames(trait);

        foreach (var method in trait.Methods)
        {
            if (method.Signature.Count == 0)
                continue;

            var (paramTypes, returnType) = ExtractSignatureTypes(method.Signature);

            foreach (var paramType in paramTypes)
            {
                if (ContainsSelfType(paramType, selfTypeNames))
                {
                    hasSelfInParam = true;
                    break;
                }
            }

            if (returnType != null && ContainsSelfType(returnType, selfTypeNames))
            {
                hasSelfInResult = true;
            }

            if (hasSelfInParam && hasSelfInResult)
                break;
        }

        return (hasSelfInParam, hasSelfInResult) switch
        {
            (true, true) => SelfPosition.Both,
            (true, false) => SelfPosition.InParameter,
            (false, true) => SelfPosition.InResult,
            _ => SelfPosition.Unknown
        };
    }

    private static SelfPosition DeriveTraitMethodSelfPosition(TraitDef trait, FuncDef method)
    {
        return AnalyzeTraitMethodSelfUsage(trait, method).Position;
    }

    private static TraitSelfUsage AnalyzeTraitMethodSelfUsage(TraitDef trait, FuncDef method)
    {
        return method.Signature.Count == 0
            ? TraitSelfUsage.Unknown
            : AnalyzeSelfUsage(method.Signature, GetTraitSelfTypeNames(trait));
    }

    private static TraitMethodRole ResolveTraitMethodRole(TraitDef trait, FuncDef method)
    {
        if (string.Equals(trait.Name, BuiltinTraits.TraitNames.Eq, StringComparison.Ordinal) &&
            string.Equals(method.Name, BuiltinTraits.MethodNames.Eq, StringComparison.Ordinal))
        {
            return TraitMethodRole.Equality;
        }

        if (string.Equals(trait.Name, BuiltinTraits.TraitNames.Show, StringComparison.Ordinal) &&
            string.Equals(method.Name, BuiltinTraits.MethodNames.Show, StringComparison.Ordinal))
        {
            return TraitMethodRole.Show;
        }

        return TraitMethodRole.None;
    }

    private static SelfPosition DeriveSelfPositionFromSignature(
        List<TypeNode> signature,
        HashSet<string> selfTypeNames)
    {
        return AnalyzeSelfUsage(signature, selfTypeNames).Position;
    }

    private static TraitSelfUsage AnalyzeSelfUsage(
        List<TypeNode> signature,
        HashSet<string> selfTypeNames)
    {
        var (paramTypes, returnType) = ExtractSignatureTypes(signature);
        var parameterIndices = new List<int>();
        for (var index = 0; index < paramTypes.Count; index++)
        {
            if (ContainsSelfType(paramTypes[index], selfTypeNames))
            {
                parameterIndices.Add(index);
            }
        }

        var hasSelfInParam = parameterIndices.Count > 0;
        var hasSelfInResult = returnType != null && ContainsSelfType(returnType, selfTypeNames);
        return (hasSelfInParam, hasSelfInResult) switch
        {
            (true, true) => new TraitSelfUsage(SelfPosition.Both, parameterIndices, InResult: true),
            (true, false) => new TraitSelfUsage(SelfPosition.InParameter, parameterIndices, InResult: false),
            (false, true) => new TraitSelfUsage(SelfPosition.InResult, [], InResult: true),
            _ => TraitSelfUsage.Unknown
        };
    }

    private sealed record TraitSelfUsage(
        SelfPosition Position,
        List<int> ParameterIndices,
        bool InResult)
    {
        public static TraitSelfUsage Unknown { get; } = new(SelfPosition.Unknown, [], InResult: false);
    }

    /// <summary>
    /// Gets the set of type names that represent Self in this trait's signatures.
    /// For traits with type parameters (e.g., Applicative[F]), the type parameter names are Self equivalents.
    /// For traits without type parameters, "Self" is the only Self type.
    /// </summary>
    private static HashSet<string> GetTraitSelfTypeNames(TraitDef trait)
    {
        var selfTypeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            WellKnownStrings.Keywords.Self
        };

        foreach (var typeParam in trait.TypeParams)
        {
            if (!string.IsNullOrWhiteSpace(typeParam.Name))
            {
                selfTypeNames.Add(typeParam.Name);
            }
        }

        return selfTypeNames;
    }

    /// <summary>
    /// Extracts parameter types and return type from a function signature.
    /// </summary>
    private static (List<TypeNode> ParamTypes, TypeNode? ReturnType) ExtractSignatureTypes(IReadOnlyList<TypeNode> signature)
    {
        var paramTypes = new List<TypeNode>();
        TypeNode? returnType = null;

        if (signature.Count == 1)
        {
            DecomposeFunctionType(signature[0], paramTypes, out returnType);
        }
        else if (signature.Count > 1)
        {
            for (int i = 0; i < signature.Count - 1; i++)
            {
                paramTypes.Add(signature[i]);
            }
            returnType = signature[^1];
        }

        return (paramTypes, returnType);
    }

    /// <summary>
    /// Recursively decomposes a function type (ArrowType or EffectfulType) into parameter and return types.
    /// </summary>
    private static void DecomposeFunctionType(TypeNode typeNode, List<TypeNode> paramTypes, out TypeNode? returnType)
    {
        returnType = null;
        switch (typeNode)
        {
            case ArrowType arrow:
                paramTypes.Add(arrow.ParamType);
                DecomposeFunctionType(arrow.ReturnType, paramTypes, out returnType);
                break;
            case EffectfulType effectful:
                paramTypes.Add(effectful.InputType);
                returnType = effectful.OutputType;
                break;
            default:
                returnType = typeNode;
                break;
        }
    }

    /// <summary>
    /// Recursively checks whether a type node contains the Self type.
    /// </summary>
    private static bool ContainsSelfType(TypeNode? typeNode, HashSet<string> selfTypeNames)
    {
        if (typeNode == null)
            return false;

        return typeNode switch
        {
            TypePath typePath =>
                (typePath.ModulePath.Count == 0 &&
                 selfTypeNames.Contains(typePath.TypeName)) ||
                typePath.TypeArgs.Any(typeArg => ContainsSelfType(typeArg, selfTypeNames)),
            ArrowType arrow =>
                ContainsSelfType(arrow.ParamType, selfTypeNames) || ContainsSelfType(arrow.ReturnType, selfTypeNames),
            TupleType tuple =>
                tuple.Elements.Any(elem => ContainsSelfType(elem, selfTypeNames)),
            EffectfulType effectful =>
                ContainsSelfType(effectful.InputType, selfTypeNames) || ContainsSelfType(effectful.OutputType, selfTypeNames),
            _ => false
        };
    }

}
