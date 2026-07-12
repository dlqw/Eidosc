using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Patterns;
using Eidosc.Diagnostic;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private void PredeclareFunctionSignatures(ModuleDecl module)
    {
        var bindings = new List<(SymbolId Symbol, TypeScheme Scheme)>();
        CollectFunctionSignatureBindings(module, bindings);
        if (bindings.Count > 0)
        {
            _env = _env.Extend(bindings);
        }
    }

    private void CollectFunctionSignatureBindings(
        ModuleDecl module,
        List<(SymbolId Symbol, TypeScheme Scheme)> bindings)
    {
        foreach (var decl in module.Declarations)
        {
            if (decl is ModuleDecl nestedModule)
            {
                CollectFunctionSignatureBindings(nestedModule, bindings);
                continue;
            }

            if (decl is InstanceDecl instance)
            {
                foreach (var method in instance.Methods)
                {
                    CollectFunctionSignatureBinding(method, bindings);
                }

                continue;
            }

            if (decl is TraitDef trait)
            {
                CollectTraitMethodSignatureBindings(trait, bindings);
                continue;
            }

            if (decl is not (FuncDef or FuncDecl) || !decl.SymbolId.IsValid)
            {
                continue;
            }

            CollectFunctionSignatureBinding(decl, bindings);
        }
    }

    private void CollectTraitMethodSignatureBindings(
        TraitDef trait,
        List<(SymbolId Symbol, TypeScheme Scheme)> bindings)
    {
        var traitTypeParamKindEnv = CreateTypeParamKindMapForOwner(trait.SymbolId, GetTraitTypeParamNames(trait));
        foreach (var method in trait.Methods)
        {
            if (!method.SymbolId.IsValid)
            {
                continue;
            }

            try
            {
                bindings.Add((method.SymbolId, InferTraitMethodSignatureScheme(trait, method, traitTypeParamKindEnv)));
            }
            catch (TypeInferenceException ex)
            {
                AddError(method.Span, ex.Message);
            }
        }
    }

    private void CollectFunctionSignatureBinding(
        Declaration decl,
        List<(SymbolId Symbol, TypeScheme Scheme)> bindings)
    {
        if (decl is not (FuncDef or FuncDecl) || !decl.SymbolId.IsValid)
        {
            return;
        }

        try
        {
            var scheme = decl switch
            {
                FuncDef func => InferFunctionSignatureScheme(func),
                FuncDecl funcDecl => InferFunctionDeclarationSignatureScheme(funcDecl),
                _ => null
            };

            if (scheme != null)
            {
                bindings.Add((decl.SymbolId, scheme));
            }
        }
        catch (TypeInferenceException ex)
        {
            AddError(decl.Span, ex.Message);
        }
    }

    private void InferModuleDeclarations(ModuleDecl module)
    {
        foreach (var decl in module.Declarations)
        {
            // 设置最大错误数量限制
            if (HasReachedTypeErrorLimit)
            {
                EnsureTypeErrorLimitReported(decl.Span);
            }

            try
            {
                switch (decl)
                {
                    case FuncDef func:
                        using (MeasureTypesStep("infer_function"))
                        {
                            InferFunction(func);
                        }
                        break;
                    case FuncDecl funcDecl:
                        using (MeasureTypesStep("infer_function_declaration"))
                        {
                            InferFunctionDeclaration(funcDecl);
                        }
                        break;
                    case LetDecl letDecl:
                        using (MeasureTypesStep("infer_let_declaration"))
                        {
                            InferLetDecl(letDecl);
                        }
                        break;
                    case LetQuestionDecl letQuestionDecl:
                        using (MeasureTypesStep("infer_let_question_declaration"))
                        {
                            InferLetQuestionDecl(letQuestionDecl);
                        }
                        break;
                    case EffectDef ability:
                        InferEffectDef(ability);
                        break;
                    case TraitDef trait:
                        InferTraitDef(trait);
                        break;
                    case ProofDecl:
                        // Proof declarations removed during proof migration
                        break;
                    case ModuleDecl nestedModule:
                        InferModuleDeclarations(nestedModule);
                        break;
                    case InstanceDecl instance:
                        using (MeasureTypesStep("infer_instance_declaration"))
                        {
                            InferInstanceDecl(instance);
                        }
                        break;
                }

                _recoveryContext.RecordSuccess();
            }
            catch (TypeInferenceException ex)
            {
                // 类型不匹配时记录错误继续分析
                AddError(decl.Span, ex.Message);
            }
        }
    }

    private void InferFunctionDeclaration(FuncDecl funcDecl)
    {
        if (!funcDecl.SymbolId.IsValid)
        {
            return;
        }

        var constraintStart = _constraintGenerator.Constraints.Count;
        var functionType = InferFunctionDeclarationType(funcDecl);
        var normalizedFunctionType = _substitution.Apply(functionType);
        var scheme = _env.Generalize(normalizedFunctionType, GetConstraintsSince(constraintStart));
        _env = _env.Extend(funcDecl.SymbolId, scheme);
        funcDecl.InferredType = normalizedFunctionType;
        UpdateFunctionDeclarationSymbolSignature(funcDecl.SymbolId, normalizedFunctionType);
    }

    /// <summary>
    /// 推断函数定义的类型
    /// </summary>
    private void InferFunction(FuncDef func)
    {
        // 1. 为类型参数创建类型变量并收集约束
        var typeVarEnv = new Dictionary<string, Type>();
        var kindEnvByName = CreateTypeParamKindMap(func.TypeParams);
        var kindEnvByTypeVar = new Dictionary<int, Kind>();
        var constraintStart = _constraintGenerator.Constraints.Count;
        foreach (var typeParam in func.TypeParams)
        {
            var typeVar = _substitution.FreshTypeVariable();
            typeVarEnv[typeParam.Name] = typeVar;
            if (typeVar is TyVar typeVariable &&
                kindEnvByName.TryGetValue(typeParam.Name, out var typeParamKind))
            {
                kindEnvByTypeVar[typeVariable.Index] = typeParamKind;
            }
        }
        if (func.SymbolId.IsValid && func.TypeParams.Count > 0)
        {
            var functionTypeParameters = new List<Type>(func.TypeParams.Count);
            foreach (var typeParam in func.TypeParams)
            {
                functionTypeParameters.Add(typeVarEnv[typeParam.Name]);
            }

            _functionTypeParametersBySymbol[func.SymbolId] = functionTypeParameters;
        }

        _typeParamEnvStack.Push(typeVarEnv);
        _typeParamKindStack.Push(kindEnvByName);
        _typeParamVarKindStack.Push(kindEnvByTypeVar);
        try
        {
            foreach (var typeParam in func.TypeParams)
            {
                if (!typeVarEnv.TryGetValue(typeParam.Name, out var typeVar))
                {
                    continue;
                }

                _constraintGenerator.CollectTypeParamConstraints(
                    typeParam,
                    typeVar,
                    typeNode => ConvertType(typeNode, typeVarEnv, allowTypeConstructorReference: true));
            }

            // 2. 解析函数签名
            // Signature 现在只包含一个 TypeNode（函数的完整类型签名）
            // 对于 Int -> Int，Signature[0] 是 ArrowType(ParamType=Int, ReturnType=Int)
            Type funcType;
            if (func.Signature.Count == 1)
            {
                // 直接使用签名类型作为函数类型
                funcType = ConvertType(func.Signature[0], typeVarEnv);
            }
            else if (func.Signature.Count == 0)
            {
                funcType = new TyFun { Result = BaseTypes.Unit };
            }
            else
            {
                // 多个签名类型（不应该发生，但作为备用）
                var paramTypes = new List<Type>();
                foreach (var typeNode in func.Signature)
                {
                    paramTypes.Add(ConvertType(typeNode, typeVarEnv));
                }
                funcType = new TyFun
                {
                    Params = CopyParamsFrom(paramTypes, 0, paramTypes.Count - 1),
                    Result = paramTypes[^1]
                };
            }

            funcType = ApplyRequiredAbilitiesToFunction(
                funcType,
                ResolveRequiredAbilities(func.RequiredAbilities, typeVarEnv));
            RejectComptimeFunctionAbilities(func, funcType);

            // For function declarations without a body (@ffi, trait methods),
            // strip Unit parameters: Unit -> T is equivalent to () -> T.
            // The symbol's arity (set during name resolution) already reflects
            // this, and the function type must match.
            if (func.Body.Count == 0 && funcType is TyFun ft)
            {
                funcType = StripLeadingUnitParams(ft);
            }

            var inferFunctionBody = func.Body.Count > 0 && !ShouldUseSignatureOnlyForFunction(func);

            // 3. 推断函数体（在函数体中可以递归引用自身）
            if (inferFunctionBody)
            {
                // 临时添加 Mono 类型用于递归
                var bodyEnv = _env.ExtendMono(func.SymbolId, funcType);
                var consumeWholeParameterList =
                    funcType is TyFun bodyFuncType &&
                    CollectParamTypes(bodyFuncType).Count > 1;

                // 保存当前环境
                var savedEnv = _env;
                var savedAllowComptimeFunctionReferences = _allowComptimeFunctionReferences;
                _env = bodyEnv;
                _allowComptimeFunctionReferences = func.IsComptime;
                PushFunctionReturnType(ResolveFunctionReturnType(funcType));

                try
                {
                    if (TryInferDirectFunctionBody(func, funcType))
                    {
                        // Direct single-branch bodies already inferred above.
                    }
                    else foreach (var branch in func.Body)
                    {
                        using (MeasureTypesStep("infer_function_body_branch"))
                        {
                            InferPatternBranchWithLocalRefinementIfNeeded(branch, funcType, consumeWholeParameterList);
                        }
                    }

                    // 6. 生成表达式约束
                    using (MeasureTypesStep("generate_function_body_constraints"))
                    {
                        foreach (var branch in func.Body)
                        {
                            _constraintGenerator.Generate(branch);
                        }
                    }
                }
                finally
                {
                    PopFunctionReturnType();
                    _allowComptimeFunctionReferences = savedAllowComptimeFunctionReferences;
                    _env = savedEnv;
                }
            }

            // 4. 在函数体与约束生成完成后再泛化，避免量化变量在后续合一中产生陈旧实例链。
            Type normalizedFuncType;
            TypeScheme scheme;
            using (MeasureTypesStep("generalize_function"))
            {
                normalizedFuncType = _substitution.Apply(funcType);
                scheme = _env.Generalize(normalizedFuncType, GetConstraintsSince(constraintStart));
            }

            // 5. 添加带约束的 scheme 到环境（用于外部调用）
            _env = _env.Extend(func.SymbolId, scheme);

            // 6. 存储推断出的类型
            func.InferredType = normalizedFuncType;
            if (func.Body.Count == 0 || !inferFunctionBody)
            {
                using (MeasureTypesStep("update_function_symbol_signature"))
                {
                    UpdateFunctionDeclarationSymbolSignature(func.SymbolId, normalizedFuncType);
                }
            }
            else
            {
                using (MeasureTypesStep("update_function_symbol_signature"))
                {
                    UpdateFunctionSymbolSignature(func.SymbolId, normalizedFuncType);
                }
            }

            var finalizedKindsByName = FinalizeTypeParamKinds(kindEnvByName);
            var finalizedKindsByIndex = new List<Kind>(func.TypeParams.Count);
            foreach (var typeParam in func.TypeParams)
            {
                finalizedKindsByIndex.Add(
                    finalizedKindsByName.TryGetValue(typeParam.Name, out var resolvedKind)
                        ? resolvedKind
                        : Kind.KStar.Instance);
            }

            UpdateTypeParamSymbolsWithKinds(func.TypeParams, finalizedKindsByIndex);
        }
        finally
        {
            _typeParamVarKindStack.Pop();
            _typeParamKindStack.Pop();
            _typeParamEnvStack.Pop();
        }
    }

    private bool ShouldUseSignatureOnlyForFunction(FuncDef func)
    {
        if (!UsePrecompiledImportSignatureOnly ||
            string.IsNullOrWhiteSpace(_rootInputFilePath) ||
            string.IsNullOrWhiteSpace(func.Span.FilePath) ||
            string.Equals(
                Path.GetFullPath(func.Span.FilePath),
                Path.GetFullPath(_rootInputFilePath),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var useSignatureOnly = func.Span.FilePath
            .Replace('\\', '/')
            .Contains("/Stdlib/Precompiled/", StringComparison.Ordinal);
        if (useSignatureOnly)
        {
            IncrementProfilingCounter("Types.precompiledImportSignatureOnly.functions");
        }

        return useSignatureOnly;
    }

    private void InferInstanceDecl(InstanceDecl instance)
    {
        foreach (var method in instance.Methods)
        {
            InferFunction(method);
        }

        foreach (var associatedConst in instance.AssociatedConsts)
        {
            if (associatedConst.Value != null)
            {
                associatedConst.Value.InferredType = SafeInferExpression(associatedConst.Value);
                _constraintGenerator.Generate(associatedConst.Value);
            }
        }
    }

    private List<TypeConstraint> GetConstraintsSince(int start)
    {
        var constraints = _constraintGenerator.Constraints.Constraints;
        if (start >= constraints.Count)
        {
            return [];
        }

        return constraints.GetRange(start, constraints.Count - start);
    }

    private void RejectComptimeFunctionAbilities(FuncDef func, Type funcType)
    {
        if (!func.IsComptime)
        {
            return;
        }

        var requiredAbilities = ExtractFunctionEffectBoundary(funcType);
        if (requiredAbilities.IsPure)
        {
            return;
        }

        AddError(
            func.Span,
            $"Comptime-only function '{func.Name}' must be pure; runtime abilities are not allowed across the comptime boundary.");
    }

    private static EffectRow ExtractFunctionEffectBoundary(Type type)
    {
        return type switch
        {
            TyFun { Effects: { } abilities } when !abilities.IsPure => abilities,
            TyFun { Result: TyFun nested } => ExtractFunctionEffectBoundary(nested),
            _ => EffectRow.Pure
        };
    }

    private Type InferFunctionDeclarationType(FuncDecl funcDecl)
    {
        var functionType = InferFunctionSignatureType(
            funcDecl.Signature,
            funcDecl.TypeParams,
            requiredAbilities: funcDecl.RequiredAbilities);

        return functionType is TyFun tyFun
            ? StripLeadingUnitParams(tyFun)
            : functionType;
    }

    private TypeScheme InferFunctionDeclarationSignatureScheme(FuncDecl funcDecl)
    {
        if (funcDecl.Signature.Count == 0)
        {
            return _env.Generalize(BaseTypes.Unit);
        }

        var functionType = InferFunctionDeclarationType(funcDecl);
        return _env.Generalize(functionType);
    }

    private void UpdateFunctionSymbolSignature(SymbolId functionSymbolId, Type functionType)
    {
        UpdateFunctionSymbolSignature(functionSymbolId, functionType, preserveExistingParametersWhenEmpty: true);
    }

    private void UpdateFunctionDeclarationSymbolSignature(SymbolId functionSymbolId, Type functionType)
    {
        UpdateFunctionSymbolSignature(functionSymbolId, functionType, preserveExistingParametersWhenEmpty: false);
    }

    private void UpdateFunctionSymbolSignature(
        SymbolId functionSymbolId,
        Type functionType,
        bool preserveExistingParametersWhenEmpty)
    {
        if (!functionSymbolId.IsValid ||
            _symbolTable.GetSymbol<FuncSymbol>(functionSymbolId) is not { } functionSymbol)
        {
            return;
        }

        var resolvedFunctionType = _substitution.Apply(functionType);
        if (resolvedFunctionType is not TyFun tyFun)
        {
            var valueReturnTypeId = ResolveSymbolMetadataTypeId(resolvedFunctionType);
            if (valueReturnTypeId.IsValid)
            {
                _symbolTable.UpdateSymbol(functionSymbol with
                {
                    ParamTypes = [],
                    ReturnType = valueReturnTypeId
                });
            }

            return;
        }

        var parameterTypes = CollectParamTypes(tyFun);
        var parameterTypeIds = new List<TypeId>(parameterTypes.Count);
        foreach (var parameterType in parameterTypes)
        {
            parameterTypeIds.Add(ResolveSymbolMetadataTypeId(_substitution.Apply(parameterType)));
        }
        var returnTypeId = ResolveSymbolMetadataTypeId(ResolveFunctionReturnType(tyFun));

        _symbolTable.UpdateSymbol(functionSymbol with
        {
            ParamTypes = parameterTypeIds.Count > 0 || !preserveExistingParametersWhenEmpty
                ? parameterTypeIds
                : functionSymbol.ParamTypes,
            ReturnType = returnTypeId.IsValid ? returnTypeId : functionSymbol.ReturnType
        });
    }

    private bool TryInferDirectFunctionBody(FuncDef func, Type funcType)
    {
        if (func.Body.Count != 1 ||
            funcType is not TyFun funType ||
            !IsDirectFunctionBranch(func.Body[0]) ||
            PatternHasGadtConstructor(func.Body[0].Pattern))
        {
            return false;
        }

        var branch = func.Body[0];
        var paramTypes = CollectParamTypes(funType);
        if (paramTypes.Count <= 1 ||
            branch.Pattern is TuplePattern)
        {
            return false;
        }

        var branchPattern = branch.Pattern;
        if (branchPattern == null)
        {
            return false;
        }

        var (expectedParamType, consumedParameterCount) = GetPatternBranchParameterExpectation(
            branch,
            paramTypes,
            consumeWholeParameterList: true);
        InferPattern(branchPattern, expectedParamType);

        if (branch.Expression != null)
        {
            var expectedExpressionType = GetBranchResultType(funType, consumedParameterCount);
            var exprType = InferExpressionWithExpectedType(branch.Expression, expectedExpressionType);
            var bodyResult = TryUnify(expectedExpressionType, exprType, branch.Expression.Span, DiagnosticMessages.FunctionBodyResultTypeMismatch(func.Name));
            if (ContainsErrorRecoveryType(bodyResult))
            {
                branch.Expression.InferredType = bodyResult;
            }
        }
        else
        {
            ReportMissingShape(branch.Span, DiagnosticMessages.FunctionBodyBranchRequiresBodyExpression(func.Name));
        }

        return true;
    }

    private void InferEffectDef(EffectDef ability)
    {
        // Effect 的类型参数 kind 绑定在索引阶段已收集并校验；
        // 这里保留入口，便于后续扩展 ability 操作体级别类型推断。
        _ = ability;
    }

    private void InferTraitDef(TraitDef trait)
    {
        PredeclareTraitMethodSignatures(trait);

        // Proof declarations removed during proof migration
    }

    private void PredeclareTraitMethodSignatures(TraitDef trait)
    {
        var traitTypeParamKindEnv = CreateTypeParamKindMapForOwner(trait.SymbolId, GetTraitTypeParamNames(trait));
        foreach (var method in trait.Methods)
        {
            if (!method.SymbolId.IsValid)
            {
                continue;
            }

            try
            {
                var scheme = InferTraitMethodSignatureScheme(trait, method, traitTypeParamKindEnv);
                _env = _env.Extend(method.SymbolId, scheme);
            }
            catch (TypeInferenceException ex)
            {
                AddError(method.Span, ex.Message);
            }
        }
    }

    private TypeScheme InferTraitMethodSignatureScheme(
        TraitDef trait,
        FuncDef method,
        IReadOnlyDictionary<string, Kind> traitTypeParamKindEnv)
    {
        var methodType = InferFunctionSignatureType(
            method.Signature,
            method.TypeParams,
            trait.TypeParams,
            traitTypeParamKindEnv,
            method.RequiredAbilities,
            _substitution.FreshTypeVariable());
        return _env.Generalize(methodType);
    }
}
