using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Semantic;

namespace Eidosc.Types;

/// <summary>
/// 能力推断器 - 推断函数和表达式的能力需求
/// Infers ability requirements for functions and expressions.
/// </summary>
public sealed class EffectInferer
{
    private readonly SymbolTable _symbolTable;
    private readonly EffectContext _abilityContext;
    private readonly Dictionary<FuncDef, FunctionEffectSummary> _functionSummaries = new();
    private readonly Dictionary<SymbolId, FuncDef> _functionDefinitionsBySymbol = new();
    private readonly Dictionary<FuncDef, List<FunctionCallSite>> _functionCallSites = new();
    private FuncDef? _currentFunction;
    private readonly EffectVariableGenerator _abilityVarGen = new();
    private readonly Dictionary<string, EffectVariable> _typeParamEffectVars = [];

    /// <summary>
    /// 函数能力需求映射
    /// </summary>
    public IReadOnlyDictionary<FuncDef, FunctionEffectSummary> FunctionSummaries => _functionSummaries;

    public IReadOnlyDictionary<SymbolId, FunctionEffectSummary> FunctionSummariesBySymbol =>
        _functionSummaries
            .Where(static binding => binding.Key.SymbolId.IsValid)
            .ToDictionary(static binding => binding.Key.SymbolId, static binding => binding.Value);

    public EffectInferer(SymbolTable symbolTable)
    {
        _symbolTable = symbolTable;
        _abilityContext = new EffectContext();
    }

    /// <summary>
    /// 推断模块的能力需求
    /// </summary>
    public EffectInferenceResult Infer(ModuleDecl module)
    {
        _functionSummaries.Clear();
        _functionDefinitionsBySymbol.Clear();
        _functionCallSites.Clear();
        _currentFunction = null;
        CollectFunctionDefinitions(module);

        foreach (var decl in module.Declarations)
        {
            InferDeclaration(decl);
        }

        PropagateFunctionCallAbilities();

        return new EffectInferenceResult(_functionSummaries);
    }

    public EffectInferenceResult Restore(
        ModuleDecl module,
        IReadOnlyDictionary<SymbolId, FunctionEffectSummary> summaries)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(summaries);
        _functionSummaries.Clear();
        _functionDefinitionsBySymbol.Clear();
        _functionCallSites.Clear();
        _currentFunction = null;
        CollectFunctionDefinitions(module);
        foreach (var (symbolId, function) in _functionDefinitionsBySymbol)
        {
            if (summaries.TryGetValue(symbolId, out var summary))
            {
                StoreFunctionSummary(function, summary);
            }
        }

        return new EffectInferenceResult(_functionSummaries);
    }

    /// <summary>
    /// 推断声明的能力需求
    /// </summary>
    private void InferDeclaration(Declaration decl)
    {
        switch (decl)
        {
            case FuncDef func:
                InferFunction(func);
                break;
            case EffectDef:
                // Effect definitions themselves are pure
                break;
            case LetDecl letDecl:
                if (letDecl.Value != null)
                    InferExpression(letDecl.Value);
                break;
            case LetQuestionDecl letQuestionDecl:
                if (letQuestionDecl.Value != null)
                    InferExpression(letQuestionDecl.Value);
                break;
            case ModuleDecl nestedModule:
                foreach (var nestedDecl in nestedModule.Declarations)
                {
                    InferDeclaration(nestedDecl);
                }
                break;
        }
    }

    /// <summary>
    /// 推断函数的能力需求
    /// </summary>
    private void InferFunction(FuncDef func)
    {
        var previousFunction = _currentFunction;
        _currentFunction = func;

        // Detect type params with ability constraints — create EffectVariables for them
        _typeParamEffectVars.Clear();
        foreach (var typeParam in func.TypeParams)
        {
            if (typeParam.IsEffectSet)
            {
                _typeParamEffectVars[typeParam.Name] = _abilityVarGen.Fresh();
                continue;
            }

            foreach (var constraint in typeParam.TraitConstraints)
            {
                var constraintName = constraint.TraitName;
                if (!string.IsNullOrEmpty(constraintName))
                {
                    // Check if this constraint resolves to an EffectSymbol
                    var path = constraintName
                        .Replace(WellKnownStrings.Separators.ModulePath, WellKnownStrings.Separators.Path, StringComparison.Ordinal)
                        .Split(WellKnownStrings.Separators.Path, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(part => !string.IsNullOrWhiteSpace(part))
                        .ToList();
                    if (path.Count > 0)
                    {
                        var resolved = _symbolTable.ResolvePathWithResult(path);
                        if (resolved.IsSuccess && resolved.Kind == ResolutionKind.Effect)
                        {
                            var abilityVar = _abilityVarGen.Fresh();
                            _typeParamEffectVars[typeParam.Name] = abilityVar;
                            break; // One ability constraint per type param is enough
                        }
                    }
                }
            }
        }

        _abilityContext.Reset();
        _abilityContext.EnterScope();
        var declaredUpperBound = ResolveFunctionDeclaredAbilities(func);

        // Infer each branch
        foreach (var branch in func.Body)
        {
            if (branch.Guard != null)
            {
                InferExpression(branch.Guard);
            }

            if (branch.Expression != null)
            {
                InferExpression(branch.Expression);
            }
        }

        // Store the function's ability requirements
        StoreFunctionSummary(func, new FunctionEffectSummary(
            declaredUpperBound,
            _abilityContext.CurrentRequirements));

        _abilityContext.ExitScope();
        _currentFunction = previousFunction;
    }

    /// <summary>
    /// 推断表达式的能力需求
    /// </summary>
    private void InferExpression(EidosAstNode expr)
    {
        switch (expr)
        {
            case LiteralExpr:
                // Literals are pure
                break;

            case IdentifierExpr ident:
                InferIdentifier(ident);
                break;

            case PathExpr path:
                InferPath(path);
                break;

            case BinaryExpr binary:
                if (binary.Left != null)
                    InferExpression(binary.Left);
                if (binary.Right != null)
                    InferExpression(binary.Right);
                break;

            case UnaryExpr unary:
                if (unary.Operand != null)
                    InferExpression(unary.Operand);
                break;

            case CallExpr call:
                InferCall(call);
                break;

            case MethodCallExpr methodCall:
                InferMethodCall(methodCall);
                break;

            case InfixCallExpr infixCall:
                InferInfixCall(infixCall);
                break;

            case LambdaExpr lambda:
                InferLambda(lambda);
                break;

            case IfExpr ifExpr:
                InferIf(ifExpr);
                break;

            case IfLetExpr ifLetExpr:
                InferIfLet(ifLetExpr);
                break;

            case WhileLetExpr whileLetExpr:
                InferWhileLet(whileLetExpr);
                break;

            case MatchExpr match:
                InferMatch(match);
                break;

            case SelectionExpr selection:
                if (selection.Subject != null)
                    InferExpression(selection.Subject);
                if (selection.ThenArm != null)
                    InferExpression(selection.ThenArm);
                if (selection.ElseArm != null)
                    InferExpression(selection.ElseArm);
                break;

            case PatternGuardExpr patternGuard:
                if (patternGuard.SourceExpression != null)
                {
                    InferExpression(patternGuard.SourceExpression);
                }
                break;

            case SequentialGuardExpr sequentialGuard:
                foreach (var guard in sequentialGuard.Guards)
                {
                    InferExpression(guard);
                }
                break;

            case DoExpr doExpr:
                InferDoExpr(doExpr);
                break;

            case BlockExpr block:
                InferBlock(block);
                break;

            case ReturnExpr ret:
                if (ret.Value != null)
                    InferExpression(ret.Value);
                break;

            case BreakExpr breakExpr:
                if (breakExpr.Value != null)
                    InferExpression(breakExpr.Value);
                break;

            case ContinueExpr:
                break;

            case UnreachableExpr:
                break;

            case LoopExpr loop:
                if (loop.Body != null)
                    InferExpression(loop.Body);
                break;

            case TupleExpr tuple:
                foreach (var elem in tuple.Elements)
                    InferExpression(elem);
                break;

            case ListExpr list:
                foreach (var elem in list.Elements)
                    InferExpression(elem);
                break;

            case ListComprehension listComp:
                InferListComprehension(listComp);
                break;

            case CtorExpr ctor:
                foreach (var arg in ctor.PositionalArgs)
                    InferExpression(arg);
                if (ctor.UpdateBase != null)
                    InferExpression(ctor.UpdateBase);
                foreach (var field in ctor.NamedArgs)
                    if (field.Value != null)
                        InferExpression(field.Value);
                break;

            case RecordUpdateExpr recordUpdate:
                if (recordUpdate.Base != null)
                    InferExpression(recordUpdate.Base);
                foreach (var field in recordUpdate.NamedArgs)
                    if (field.Value != null)
                        InferExpression(field.Value);
                break;

            case ContextualRecordLiteralExpr contextualRecord:
                foreach (var field in contextualRecord.NamedArgs)
                    if (field.Value != null)
                        InferExpression(field.Value);
                break;

            case IndexExpr index:
                if (index.Object != null)
                    InferExpression(index.Object);
                if (index.Index != null)
                    InferExpression(index.Index);
                break;

            case GivenExpr given:
                if (given.Target != null)
                    InferExpression(given.Target);
                break;

            case AssociatedConstExpr associatedConst:
                if (associatedConst.ImplementationValue != null)
                    InferExpression(associatedConst.ImplementationValue);
                break;
        }
    }

    private void InferDoExpr(DoExpr doExpr)
    {
        foreach (var binding in doExpr.Bindings)
        {
            if (binding.Value != null)
            {
                InferExpression(binding.Value);
            }
        }
    }

    /// <summary>
    /// 推断标识符的能力需求
    /// </summary>
    private void InferIdentifier(IdentifierExpr ident)
    {
        _ = ident;
    }

    /// <summary>
    /// 推断路径表达式的能力需求
    /// </summary>
    private void InferPath(PathExpr path)
    {
        _ = path;
    }

    /// <summary>
    /// 推断调用表达式的能力需求
    /// </summary>
    private void InferCall(CallExpr call)
    {
        // Infer the function being called
        if (call.Function != null)
            InferExpression(call.Function);

        // Infer arguments
        foreach (var arg in call.PositionalArgs)
            InferExpression(arg);
        foreach (var arg in call.NamedArgs)
            if (arg.Value != null)
                InferExpression(arg.Value);

        var requiredByCall = ResolveExpressionRequiredAbilities(call);
        if (!requiredByCall.IsPure)
        {
            _abilityContext.AddRequirements(requiredByCall);
        }

        RecordFunctionCallSite(call);
    }

    /// <summary>
    /// 推断方法调用表达式的能力需求
    /// </summary>
    private void InferMethodCall(MethodCallExpr methodCall)
    {
        if (methodCall.ResolvedAsFieldAccess)
        {
            if (methodCall.Receiver != null)
            {
                InferExpression(methodCall.Receiver);
            }

            return;
        }

        var desugared = methodCall.ToDesugaredCall();
        desugared.InferredType = methodCall.InferredType;
        desugared.InferredEffects = methodCall.InferredEffects;
        InferCall(desugared);
    }

    private void InferInfixCall(InfixCallExpr infixCall)
    {
        if (infixCall.Left != null)
            InferExpression(infixCall.Left);
        if (infixCall.Right != null)
            InferExpression(infixCall.Right);

        var requiredByCall = ResolveExpressionRequiredAbilities(infixCall);
        if (!requiredByCall.IsPure)
        {
            _abilityContext.AddRequirements(requiredByCall);
        }
    }

    /// <summary>
    /// 推断 lambda 表达式的能力需求
    /// </summary>
    private void InferLambda(LambdaExpr lambda)
    {
        _abilityContext.EnterScope();

        if (lambda.Body != null)
            InferExpression(lambda.Body);

        _abilityContext.ExitScope();
    }

    /// <summary>
    /// 推断 if 表达式的能力需求
    /// </summary>
    private void InferIf(IfExpr ifExpr)
    {
        if (ifExpr.Condition != null)
            InferExpression(ifExpr.Condition);
        if (ifExpr.ThenBranch != null)
            InferExpression(ifExpr.ThenBranch);
        if (ifExpr.ElseBranch != null)
            InferExpression(ifExpr.ElseBranch);
    }

    /// <summary>
    /// 推断 if-let 表达式的能力需求
    /// </summary>
    private void InferIfLet(IfLetExpr ifLetExpr)
    {
        if (ifLetExpr.MatchedExpression != null)
        {
            InferExpression(ifLetExpr.MatchedExpression);
        }

        _abilityContext.EnterScope();
        if (ifLetExpr.ThenBranch != null)
        {
            InferExpression(ifLetExpr.ThenBranch);
        }
        _abilityContext.ExitScope();

        if (ifLetExpr.ElseBranch != null)
        {
            InferExpression(ifLetExpr.ElseBranch);
        }
    }

    /// <summary>
    /// 推断 while-let 表达式的能力需求
    /// </summary>
    private void InferWhileLet(WhileLetExpr whileLetExpr)
    {
        if (whileLetExpr.MatchedExpression != null)
        {
            InferExpression(whileLetExpr.MatchedExpression);
        }

        _abilityContext.EnterScope();
        if (whileLetExpr.Body != null)
        {
            InferExpression(whileLetExpr.Body);
        }
        _abilityContext.ExitScope();
    }

    /// <summary>
    /// 推断 match 表达式的能力需求
    /// </summary>
    private void InferMatch(MatchExpr match)
    {
        if (match.MatchedExpression != null)
            InferExpression(match.MatchedExpression);

        foreach (var branch in match.Branches)
        {
            _abilityContext.EnterScope();
            if (branch.Guard != null)
            {
                InferExpression(branch.Guard);
            }
            if (branch.Expression != null)
                InferExpression(branch.Expression);
            _abilityContext.ExitScope();
        }
    }

    /// <summary>
    /// 推断 block 表达式的能力需求
    /// </summary>
    private void InferBlock(BlockExpr block)
    {
        _abilityContext.EnterScope();

        foreach (var stmt in block.Statements)
        {
            if (stmt is Declaration decl)
                InferDeclaration(decl);
            else
                InferExpression(stmt);
        }

        _abilityContext.ExitScope();
    }

    private EffectRow ResolveFunctionDeclaredAbilities(FuncDef func)
    {
        if (func.InferredType is Type inferredType)
        {
            var abilitiesFromType = ExtractFunctionEffects(inferredType);
            if (!abilitiesFromType.IsPure)
            {
                return abilitiesFromType;
            }
        }

        var declared = EffectRow.Pure;
        foreach (var requirement in func.RequiredAbilities)
        {
            var path = requirement.Path
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim())
                .ToList();
            if (path.Count == 0)
            {
                continue;
            }

            if (path.Count == 1 && _typeParamEffectVars.TryGetValue(path[0], out var abilityVar))
            {
                declared = declared.Union(EffectRow.FromEffectVariable(abilityVar));
            }
            else
            {
                declared = declared.Add(ResolveEffectTag(string.Join(WellKnownStrings.Separators.Path, path)));
            }
        }

        return declared;
    }

    private EffectRow ResolveExpressionRequiredAbilities(EidosAstNode expr)
    {
        if (expr.InferredEffects is { IsPure: false } inferredEffects)
        {
            return inferredEffects;
        }

        if (expr.InferredType is Type)
        {
            return EffectRow.Pure;
        }

        return expr switch
        {
            CallExpr call => ResolveFallbackCallAbilities(call.Function),
            MethodCallExpr methodCall => methodCall.ResolvedAsFieldAccess
                ? EffectRow.Pure
                : ResolveFallbackCallAbilities(methodCall.ToDesugaredCall().Function),
            InfixCallExpr infixCall => ResolveFallbackInfixCallAbilities(infixCall),
            _ => EffectRow.Pure
        };
    }

    private EffectRow ResolveFallbackInfixCallAbilities(InfixCallExpr infixCall)
    {
        if (TryResolveFunctionSymbolId(infixCall.FunctionSymbolId, out var resolvedSymbolId))
        {
            return ResolveFallbackFunctionAbilities(resolvedSymbolId);
        }

        foreach (var candidate in infixCall.FunctionCandidateSymbolIds)
        {
            if (TryResolveFunctionSymbolId(candidate, out resolvedSymbolId))
            {
                return ResolveFallbackFunctionAbilities(resolvedSymbolId);
            }
        }

        return TryResolveFunctionSymbolId(infixCall.FunctionName, out resolvedSymbolId)
            ? ResolveFallbackFunctionAbilities(resolvedSymbolId)
            : EffectRow.Pure;
    }

    private EffectRow ResolveFallbackCallAbilities(EidosAstNode? calleeExpr)
    {
        var calleeSymbolId = ResolveCalleeSymbolId(calleeExpr);
        return calleeSymbolId.IsValid
            ? ResolveFallbackFunctionAbilities(calleeSymbolId)
            : EffectRow.Pure;
    }

    private EffectRow ResolveFallbackFunctionAbilities(SymbolId calleeSymbolId)
    {
        if (_symbolTable.GetSymbol(calleeSymbolId) is not FuncSymbol funcSymbol)
        {
            return EffectRow.Pure;
        }

        if (funcSymbol.ImplicitAbilities.Count > 0)
        {
            var implicitSet = new HashSet<EffectTag>();
            foreach (var name in funcSymbol.ImplicitAbilities)
                implicitSet.Add(ResolveEffectTag(name));
            return new EffectRow(implicitSet);
        }

        return EffectRow.Pure;
    }

    private static EffectRow ExtractFunctionEffects(Type type)
    {
        return type switch
        {
            TyFun { Effects: { } abilities } when !abilities.IsPure => abilities,
            _ => EffectRow.Pure
        };
    }

    private EffectTag ResolveEffectTag(SymbolId symbolId, string abilityName)
    {
        if (symbolId.IsValid && _symbolTable.GetSymbol(symbolId) is EffectSymbol abilitySymbol)
        {
            return new EffectTag
            {
                Symbol = symbolId,
                Name = abilitySymbol.Name
            };
        }

        return ResolveEffectTag(abilityName);
    }

    private EffectTag ResolveEffectTag(string abilityName)
    {
        if (string.IsNullOrWhiteSpace(abilityName))
        {
            return new EffectTag();
        }

        var normalized = abilityName.Trim();
        var path = normalized
            .Replace(WellKnownStrings.Separators.ModulePath, WellKnownStrings.Separators.Path, StringComparison.Ordinal)
            .Split(WellKnownStrings.Separators.Path, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
        if (path.Count == 0)
        {
            return new EffectTag();
        }

        if (path.Count > 0)
        {
            var resolved = _symbolTable.ResolvePathWithResult(path);
            if (resolved.IsSuccess &&
                resolved.Kind == ResolutionKind.Effect &&
                resolved.SymbolId.IsValid &&
                _symbolTable.GetSymbol(resolved.SymbolId) is EffectSymbol resolvedEffect)
            {
                return new EffectTag
                {
                    Symbol = resolved.SymbolId,
                    Name = resolvedEffect.Name
                };
            }
        }

        return new EffectTag { Name = normalized };
    }

    private void CollectFunctionDefinitions(ModuleDecl module)
    {
        foreach (var decl in module.Declarations)
        {
            switch (decl)
            {
                case FuncDef func when func.SymbolId.IsValid:
                    _functionDefinitionsBySymbol[func.SymbolId] = func;
                    break;

                case ModuleDecl nestedModule:
                    CollectFunctionDefinitions(nestedModule);
                    break;
            }
        }
    }

    private void PropagateFunctionCallAbilities()
    {
        if (_functionCallSites.Count == 0)
        {
            return;
        }

        var dependents = new Dictionary<FuncDef, HashSet<FuncDef>>();
        foreach (var (caller, callSites) in _functionCallSites)
        {
            foreach (var callSite in callSites)
            {
                if (!_functionDefinitionsBySymbol.TryGetValue(callSite.CalleeSymbolId, out var callee))
                {
                    continue;
                }

                if (!dependents.TryGetValue(callee, out var callers))
                {
                    callers = [];
                    dependents[callee] = callers;
                }
                callers.Add(caller);
            }
        }

        var queue = new Queue<FuncDef>(_functionCallSites.Keys);
        var queued = new HashSet<FuncDef>(_functionCallSites.Keys);
        while (queue.TryDequeue(out var caller))
        {
            queued.Remove(caller);
            if (!_functionSummaries.TryGetValue(caller, out var callerSummary) ||
                !_functionCallSites.TryGetValue(caller, out var callSites))
            {
                continue;
            }

            var propagatedEffects = EffectRow.Pure;
            foreach (var callSite in callSites)
            {
                if (_functionDefinitionsBySymbol.TryGetValue(callSite.CalleeSymbolId, out var callee) &&
                    _functionSummaries.TryGetValue(callee, out var calleeSummary))
                {
                    propagatedEffects = propagatedEffects.Union(calleeSummary.InferredEffects);
                }
            }

            var mergedEffects = callerSummary.InferredEffects.Union(propagatedEffects);
            if (mergedEffects.Equals(callerSummary.InferredEffects))
            {
                continue;
            }

            StoreFunctionSummary(caller, callerSummary with { InferredEffects = mergedEffects });
            if (dependents.TryGetValue(caller, out var callersToRevisit))
            {
                foreach (var dependent in callersToRevisit)
                {
                    if (queued.Add(dependent)) queue.Enqueue(dependent);
                }
            }
        }
    }

    private void RecordFunctionCallSite(CallExpr call)
    {
        if (_currentFunction == null)
        {
            return;
        }

        var calleeSymbolId = ResolveCalleeSymbolId(call.Function);
        if (!calleeSymbolId.IsValid || !_functionDefinitionsBySymbol.ContainsKey(calleeSymbolId))
        {
            return;
        }

        if (!_functionCallSites.TryGetValue(_currentFunction, out var callSites))
        {
            callSites = [];
            _functionCallSites[_currentFunction] = callSites;
        }

        callSites.Add(new FunctionCallSite(calleeSymbolId));
    }

    private SymbolId ResolveCalleeSymbolId(EidosAstNode? functionExpr)
    {
        switch (functionExpr)
        {
            case IdentifierExpr identifier:
                if (TryResolveFunctionSymbolId(identifier.SymbolId, out var identifierSymbolId))
                {
                    return identifierSymbolId;
                }

                if (identifier.SymbolId.IsValid)
                {
                    return SymbolId.None;
                }

                foreach (var candidate in identifier.ValueCandidateSymbolIds)
                {
                    if (TryResolveFunctionSymbolId(candidate, out var candidateSymbolId))
                    {
                        return candidateSymbolId;
                    }
                }

                if (identifier.ValueCandidateSymbolIds.Count > 0)
                {
                    return SymbolId.None;
                }

                return TryResolveFunctionSymbolId(identifier.Name, out var namedSymbolId)
                    ? namedSymbolId
                    : SymbolId.None;

            case PathExpr path:
                if (TryResolveFunctionSymbolId(path.SymbolId, out var pathSymbolId))
                {
                    return pathSymbolId;
                }

                var result = _symbolTable.ResolvePathWithResult(path.Path);
                return result is { IsSuccess: true, Kind: ResolutionKind.Value } &&
                       TryResolveFunctionSymbolId(result.SymbolId, out var resolvedPathSymbolId)
                    ? resolvedPathSymbolId
                    : SymbolId.None;

            default:
                return SymbolId.None;
        }
    }

    private bool TryResolveFunctionSymbolId(SymbolId symbolId, out SymbolId resolved)
    {
        resolved = SymbolId.None;
        if (!symbolId.IsValid ||
            _symbolTable.GetSymbol<FuncSymbol>(symbolId) == null)
        {
            return false;
        }

        resolved = symbolId;
        return true;
    }

    private bool TryResolveFunctionSymbolId(string name, out SymbolId resolved)
    {
        resolved = SymbolId.None;
        if (string.IsNullOrWhiteSpace(name) ||
            _symbolTable.LookupValue(name) is not { } symbolId)
        {
            return false;
        }

        return TryResolveFunctionSymbolId(symbolId, out resolved);
    }

    /// <summary>
    /// 推断列表推导式的能力需求
    /// </summary>
    private void InferListComprehension(ListComprehension listComp)
    {
        _abilityContext.EnterScope();

        // Process qualifiers
        foreach (var qualifier in listComp.Qualifiers)
        {
            if (qualifier.GeneratorExpression != null)
                InferExpression(qualifier.GeneratorExpression);
            if (qualifier.GuardExpression != null)
                InferExpression(qualifier.GuardExpression);
        }

        // Process output
        if (listComp.Output != null)
            InferExpression(listComp.Output);

        _abilityContext.ExitScope();
    }

    private void StoreFunctionSummary(FuncDef function, FunctionEffectSummary summary)
    {
        _functionSummaries[function] = summary;
        if (function.SymbolId.IsValid &&
            _symbolTable.GetSymbol(function.SymbolId) is FuncSymbol functionSymbol)
        {
            _symbolTable.UpdateSymbol(functionSymbol with { EffectSummary = summary });
        }
    }

    private readonly record struct FunctionCallSite(SymbolId CalleeSymbolId);
}

/// <summary>
/// 能力推断结果
/// </summary>
public sealed class EffectInferenceResult
{
    public IReadOnlyDictionary<FuncDef, FunctionEffectSummary> FunctionSummaries { get; }

    public EffectInferenceResult(IReadOnlyDictionary<FuncDef, FunctionEffectSummary> functionSummaries)
    {
        FunctionSummaries = functionSummaries;
    }
}
