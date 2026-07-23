using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Utils;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;

namespace Eidosc.Types;

/// <summary>
/// OCap 调用授权检查：
/// “必须持有能力才可调用”在语义阶段强制生效。
/// </summary>
public sealed class EffectAuthorizationChecker
{
    private const string MissingCapabilityErrorCode = "E3003";

    private readonly SymbolTable _symbolTable;
    private readonly IReadOnlyDictionary<FuncDef, FunctionEffectSummary> _functionSummaries;
    private readonly Dictionary<SymbolId, FuncDef> _functionDefinitionsBySymbol = new();
    private readonly Stack<EffectRow> _capabilityStack = new();
    private readonly Stack<FuncDef> _functionStack = new();
    private readonly List<EidoscDiagnostic> _diagnostics = [];
    private readonly bool _allowImplicitEntryRootCapabilities;
    private string? _checkedPackageInstanceKey;

    /// <summary>
    /// 诊断信息。
    /// </summary>
    public List<EidoscDiagnostic> Diagnostics => _diagnostics;

    public EffectAuthorizationChecker(
        SymbolTable symbolTable,
        IReadOnlyDictionary<FuncDef, FunctionEffectSummary> functionSummaries,
        bool allowImplicitEntryRootCapabilities = true)
    {
        _symbolTable = symbolTable;
        _functionSummaries = functionSummaries;
        _allowImplicitEntryRootCapabilities = allowImplicitEntryRootCapabilities;
    }

    public bool Check(ModuleDecl module)
    {
        _functionDefinitionsBySymbol.Clear();
        _capabilityStack.Clear();
        _functionStack.Clear();
        _diagnostics.Clear();
        _checkedPackageInstanceKey = module.PackageInstanceKey;

        CollectFunctionDefinitions(module);

        _capabilityStack.Push(EffectRow.Pure);
        VisitModule(module, isRoot: true);
        _capabilityStack.Pop();

        return !_diagnostics.Any(d => d.Level == EidoscDiagnosticLevel.Error);
    }

    private EffectRow CurrentCapabilities => _capabilityStack.Count > 0
        ? _capabilityStack.Peek()
        : EffectRow.Pure;

    private void VisitModule(ModuleDecl module, bool isRoot = false)
    {
        if (!isRoot && !ShouldVisitModule(module))
        {
            return;
        }

        foreach (var decl in module.Declarations)
        {
            VisitDeclaration(decl);
        }
    }

    private void VisitDeclaration(Declaration decl)
    {
        switch (decl)
        {
            case FuncDef func:
                VisitFunction(func);
                break;
            case LetDecl letDecl:
                if (letDecl.Value != null)
                {
                    VisitExpression(letDecl.Value);
                }
                break;
            case LetQuestionDecl letQuestionDecl:
                if (letQuestionDecl.Value != null)
                {
                    VisitExpression(letQuestionDecl.Value);
                }
                break;
            case Assignment assign:
                if (assign.Value != null)
                {
                    VisitExpression(assign.Value);
                }
                break;
            case ModuleDecl nestedModule:
                VisitModule(nestedModule);
                break;
        }
    }

    private bool ShouldVisitModule(ModuleDecl module)
    {
        return string.Equals(
            NormalizePackageInstanceKey(module.PackageInstanceKey),
            NormalizePackageInstanceKey(_checkedPackageInstanceKey),
            StringComparison.Ordinal);
    }

    private static string NormalizePackageInstanceKey(string? packageInstanceKey)
    {
        return string.IsNullOrWhiteSpace(packageInstanceKey)
            ? string.Empty
            : packageInstanceKey;
    }

    private void VisitFunction(FuncDef func)
    {
        var declaredCapabilities = ResolveDeclaredFunctionCapabilities(func);
        if (func.IsComptime)
        {
            var comptimeCapabilities = declaredCapabilities;
            if (_functionSummaries.TryGetValue(func, out var summary))
            {
                comptimeCapabilities = comptimeCapabilities.Union(summary.InferredEffects);
            }

            if (!ExpandEffectRow(comptimeCapabilities).IsPure)
            {
                var diagnostic = new EidoscDiagnostic(
                    EidoscDiagnosticLevel.Error,
                    $"Comptime-only function '{func.Name}' must be pure; runtime abilities are not allowed across the comptime boundary.");
                diagnostic.WithLabel(func.Span, "comptime boundary requires a pure function");
                _diagnostics.Add(diagnostic);
            }
        }

        if (_allowImplicitEntryRootCapabilities && _capabilityStack.Count == 1)
        {
            declaredCapabilities = declaredCapabilities.Union(CreateEntryPointCapabilities());
        }
        _capabilityStack.Push(declaredCapabilities);
        _functionStack.Push(func);

        try
        {
            foreach (var branch in func.Body)
            {
                if (branch.Guard != null)
                {
                    VisitExpression(branch.Guard);
                }

                if (branch.Expression != null)
                {
                    VisitExpression(branch.Expression);
                }
            }
        }
        finally
        {
            _functionStack.Pop();
            _capabilityStack.Pop();
        }
    }

    private void VisitExpression(EidosAstNode expr)
    {
        switch (expr)
        {
            case LiteralExpr:
            case IdentifierExpr:
            case PathExpr:
            case ContinueExpr:
            case UnreachableExpr:
                break;

            case BinaryExpr binary:
                if (binary.Left != null)
                {
                    VisitExpression(binary.Left);
                }
                if (binary.Right != null)
                {
                    VisitExpression(binary.Right);
                }
                break;

            case UnaryExpr unary:
                if (unary.Operand != null)
                {
                    VisitExpression(unary.Operand);
                }
                break;

            case CallExpr call:
                VisitCall(call);
                break;

            case MethodCallExpr methodCall:
                VisitMethodCall(methodCall);
                break;

            case InfixCallExpr infixCall:
                VisitInfixCall(infixCall);
                break;

            case LambdaExpr lambda:
                _capabilityStack.Push(CurrentCapabilities);
                try
                {
                    if (lambda.Body != null)
                    {
                        VisitExpression(lambda.Body);
                    }
                }
                finally
                {
                    _capabilityStack.Pop();
                }
                break;

            case IfExpr ifExpr:
                if (ifExpr.Condition != null)
                {
                    VisitExpression(ifExpr.Condition);
                }
                if (ifExpr.ThenBranch != null)
                {
                    VisitExpression(ifExpr.ThenBranch);
                }
                if (ifExpr.ElseBranch != null)
                {
                    VisitExpression(ifExpr.ElseBranch);
                }
                break;

            case IfLetExpr ifLetExpr:
                if (ifLetExpr.MatchedExpression != null)
                {
                    VisitExpression(ifLetExpr.MatchedExpression);
                }
                _capabilityStack.Push(CurrentCapabilities);
                try
                {
                    if (ifLetExpr.ThenBranch != null)
                    {
                        VisitExpression(ifLetExpr.ThenBranch);
                    }
                }
                finally
                {
                    _capabilityStack.Pop();
                }
                if (ifLetExpr.ElseBranch != null)
                {
                    VisitExpression(ifLetExpr.ElseBranch);
                }
                break;

            case WhileLetExpr whileLetExpr:
                if (whileLetExpr.MatchedExpression != null)
                {
                    VisitExpression(whileLetExpr.MatchedExpression);
                }
                _capabilityStack.Push(CurrentCapabilities);
                try
                {
                    if (whileLetExpr.Body != null)
                    {
                        VisitExpression(whileLetExpr.Body);
                    }
                }
                finally
                {
                    _capabilityStack.Pop();
                }
                break;

            case MatchExpr match:
                if (match.MatchedExpression != null)
                {
                    VisitExpression(match.MatchedExpression);
                }
                foreach (var branch in match.Branches)
                {
                    _capabilityStack.Push(CurrentCapabilities);
                    try
                    {
                        if (branch.Guard != null)
                        {
                            VisitExpression(branch.Guard);
                        }
                        if (branch.Expression != null)
                        {
                            VisitExpression(branch.Expression);
                        }
                    }
                    finally
                    {
                        _capabilityStack.Pop();
                    }
                }
                break;

            case SelectionExpr selection:
                if (selection.Subject != null)
                {
                    VisitExpression(selection.Subject);
                }
                if (selection.ThenArm != null)
                {
                    VisitExpression(selection.ThenArm);
                }
                if (selection.ElseArm != null)
                {
                    VisitExpression(selection.ElseArm);
                }
                break;

            case PatternGuardExpr patternGuard:
                if (patternGuard.SourceExpression != null)
                {
                    VisitExpression(patternGuard.SourceExpression);
                }
                break;

            case SequentialGuardExpr sequentialGuard:
                foreach (var guard in sequentialGuard.Guards)
                {
                    VisitExpression(guard);
                }
                break;

            case DoExpr doExpr:
                foreach (var binding in doExpr.Bindings)
                {
                    if (binding.Value != null)
                    {
                        VisitExpression(binding.Value);
                    }
                }
                break;

            case BlockExpr block:
                _capabilityStack.Push(CurrentCapabilities);
                try
                {
                    foreach (var stmt in block.Statements)
                    {
                        if (stmt is Declaration decl)
                        {
                            VisitDeclaration(decl);
                        }
                        else
                        {
                            VisitExpression(stmt);
                        }
                    }
                }
                finally
                {
                    _capabilityStack.Pop();
                }
                break;

            case ReturnExpr ret:
                if (ret.Value != null)
                {
                    VisitExpression(ret.Value);
                }
                break;

            case BreakExpr breakExpr:
                if (breakExpr.Value != null)
                {
                    VisitExpression(breakExpr.Value);
                }
                break;

            case LoopExpr loop:
                if (loop.Body != null)
                {
                    VisitExpression(loop.Body);
                }
                break;

            case TupleExpr tuple:
                foreach (var element in tuple.Elements)
                {
                    VisitExpression(element);
                }
                break;

            case ListExpr list:
                foreach (var element in list.Elements)
                {
                    VisitExpression(element);
                }
                break;

            case ListComprehension listComp:
                _capabilityStack.Push(CurrentCapabilities);
                try
                {
                    foreach (var qualifier in listComp.Qualifiers)
                    {
                        if (qualifier.GeneratorExpression != null)
                        {
                            VisitExpression(qualifier.GeneratorExpression);
                        }
                        if (qualifier.GuardExpression != null)
                        {
                            VisitExpression(qualifier.GuardExpression);
                        }
                    }
                    if (listComp.Output != null)
                    {
                        VisitExpression(listComp.Output);
                    }
                }
                finally
                {
                    _capabilityStack.Pop();
                }
                break;

            case CtorExpr ctor:
                foreach (var positionalArg in ctor.PositionalArgs)
                {
                    VisitExpression(positionalArg);
                }
                if (ctor.UpdateBase != null)
                {
                    VisitExpression(ctor.UpdateBase);
                }
                foreach (var namedArg in ctor.NamedArgs)
                {
                    if (namedArg.Value != null)
                    {
                        VisitExpression(namedArg.Value);
                    }
                }
                break;

            case RecordUpdateExpr recordUpdate:
                if (recordUpdate.Base != null)
                {
                    VisitExpression(recordUpdate.Base);
                }
                foreach (var namedArg in recordUpdate.NamedArgs)
                {
                    if (namedArg.Value != null)
                    {
                        VisitExpression(namedArg.Value);
                    }
                }
                break;

            case ContextualRecordLiteralExpr contextualRecord:
                foreach (var namedArg in contextualRecord.NamedArgs)
                {
                    if (namedArg.Value != null)
                    {
                        VisitExpression(namedArg.Value);
                    }
                }
                break;

            case IndexExpr index:
                if (index.Object != null)
                {
                    VisitExpression(index.Object);
                }
                if (index.Index != null)
                {
                    VisitExpression(index.Index);
                }
                break;

            case GivenExpr given:
                if (given.Target != null)
                {
                    VisitExpression(given.Target);
                }
                break;

            case AssociatedConstExpr associatedConst:
                if (associatedConst.ImplementationValue != null)
                {
                    VisitExpression(associatedConst.ImplementationValue);
                }
                break;
        }
    }

    private void VisitCall(CallExpr call)
    {
        if (call.Function != null)
        {
            VisitExpression(call.Function);
        }

        foreach (var arg in call.PositionalArgs)
        {
            VisitExpression(arg);
        }

        foreach (var arg in call.NamedArgs)
        {
            if (arg.Value != null)
            {
                VisitExpression(arg.Value);
            }
        }

        var requiredByCallee = ResolveCallRequiredAbilities(call, call.Function);
        if (requiredByCallee.IsPure)
        {
            return;
        }

        var effectiveRequired = requiredByCallee;
        var missing = ComputeMissingAbilities(effectiveRequired, CurrentCapabilities);

        if (missing.Count == 0)
        {
            return;
        }

        AddMissingEffectDiagnostic(call.Span, call.Function, requiredByCallee, missing);
    }

    private void VisitInfixCall(InfixCallExpr infixCall)
    {
        if (infixCall.Left != null)
        {
            VisitExpression(infixCall.Left);
        }
        if (infixCall.Right != null)
        {
            VisitExpression(infixCall.Right);
        }

        var requiredByCallee = ResolveInfixCallRequiredAbilities(infixCall);
        if (requiredByCallee.IsPure)
        {
            return;
        }

        var missing = ComputeMissingAbilities(requiredByCallee, CurrentCapabilities);
        if (missing.Count == 0)
        {
            return;
        }

        AddMissingEffectDiagnostic(infixCall.Span, infixCall, requiredByCallee, missing);
    }

    private void AddMissingEffectDiagnostic(
        SourceSpan span,
        EidosAstNode? calleeExpr,
        EffectRow requiredByCallee,
        IReadOnlyCollection<EffectTag> missing)
    {
        var callerName = GetCurrentCallerDisplayName();
        var calleeName = ResolveCalleeDisplayName(calleeExpr);
        var requiredText = FormatEffectRow(requiredByCallee);
        var missingText = FormatEffectRow(new EffectRow(missing));
        var availableText = FormatEffectRow(CurrentCapabilities);

        var diagnostic = new EidoscDiagnostic(
            EidoscDiagnosticLevel.Error,
            DiagnosticMessages.EffectAuthorizationFailed(callerName, calleeName),
            MissingCapabilityErrorCode);
        diagnostic.WithLabel(span, DiagnosticMessages.EffectAuthorizationFailedLabel);
        diagnostic.WithNote(DiagnosticMessages.EffectAuthorizationCallerNote(callerName));
        diagnostic.WithNote(DiagnosticMessages.EffectAuthorizationCalleeNote(calleeName));
        diagnostic.WithNote(DiagnosticMessages.EffectAuthorizationRequiredNote(requiredText));
        diagnostic.WithNote(DiagnosticMessages.EffectAuthorizationMissingNote(missingText));
        diagnostic.WithNote(DiagnosticMessages.EffectAuthorizationAvailableNote(availableText));
        diagnostic.WithHelp(DiagnosticMessages.EffectAuthorizationHelp);
        _diagnostics.Add(diagnostic);
    }

    private void VisitMethodCall(MethodCallExpr methodCall)
    {
        if (methodCall.ResolvedAsFieldAccess)
        {
            if (methodCall.Receiver != null)
            {
                VisitExpression(methodCall.Receiver);
            }

            return;
        }

        var desugared = methodCall.ToDesugaredCall();
        desugared.InferredType = methodCall.InferredType;
        desugared.InferredEffects = methodCall.InferredEffects;
        VisitCall(desugared);
    }

    private EffectRow ResolveDeclaredFunctionCapabilities(FuncDef func)
    {
        if (func.InferredType is Type inferredType)
        {
            var inferredCapabilities = ExtractFunctionEffects(inferredType);
            if (!inferredCapabilities.IsPure)
            {
                var expanded = ExpandEffectRow(inferredCapabilities);

                // If the declared ability set is polymorphic (e.g., {T} where T: Emitter),
                // resolve the type parameter constraints into concrete abilities.
                // The function body can use any ability that satisfies the constraint.
                if (expanded.IsPolymorphic)
                {
                    return ResolvePolymorphicConstraints(func, expanded);
                }

                return expanded;
            }
        }

        var declared = EffectRow.Pure;

        foreach (var requirement in func.RequiredAbilities)
        {
            var abilityPath = requirement.Path
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Select(part => part.Trim())
                .ToList();
            if (abilityPath.Count == 0)
            {
                continue;
            }

            var declaredEffect = ResolveEffectTag(requirement.SymbolId, abilityPath);
            if (!string.IsNullOrWhiteSpace(declaredEffect.Name) || declaredEffect.Symbol.IsValid)
            {
                declared = declared.Add(declaredEffect);
            }
        }

        return ExpandEffectRow(declared);
    }

    /// <summary>
    /// Resolve polymorphic ability constraints from type parameters.
    /// When a function declares <c>{T}</c> where <c>T: Emitter</c>,
    /// the constraint <c>Emitter</c> is treated as an available ability
    /// within the function body.
    /// </summary>
    private EffectRow ResolvePolymorphicConstraints(FuncDef func, EffectRow polymorphicSet)
    {
        var resolved = new HashSet<EffectTag>(polymorphicSet.Effects);

        foreach (var typeParam in func.TypeParams)
        {
            foreach (var constraint in typeParam.TraitConstraints)
            {
                if (string.IsNullOrWhiteSpace(constraint.TraitName))
                {
                    continue;
                }

                var ability = ResolveEffectTag(constraint.TraitName);
                if (!string.IsNullOrWhiteSpace(ability.Name) || ability.Symbol.IsValid)
                {
                    resolved.Add(ability);
                }
            }
        }

        return new EffectRow(resolved, polymorphicSet.Variables);
    }

    private EffectRow ResolveCallRequiredAbilities(CallExpr call, EidosAstNode? calleeExpr)
    {
        if (call.InferredEffects is { IsPure: false } callEffects)
        {
            return ExpandEffectRow(callEffects);
        }

        if (call.InferredType is Type)
        {
            return EffectRow.Pure;
        }

        var calleeSymbolId = ResolveCalleeSymbolId(calleeExpr);
        if (!calleeSymbolId.IsValid)
        {
            return EffectRow.Pure;
        }

        return ResolveFunctionRequiredAbilities(calleeSymbolId, inferredCallRequirements: null);
    }

    private EffectRow ResolveInfixCallRequiredAbilities(InfixCallExpr infixCall)
    {
        if (infixCall.InferredEffects is { IsPure: false } callEffects)
        {
            return ExpandEffectRow(callEffects);
        }

        if (infixCall.InferredType is Type)
        {
            return EffectRow.Pure;
        }

        if (TryResolveFunctionSymbolId(infixCall.FunctionSymbolId, out var resolvedSymbolId))
        {
            return ResolveFunctionRequiredAbilities(resolvedSymbolId, inferredCallRequirements: null);
        }

        foreach (var candidate in infixCall.FunctionCandidateSymbolIds)
        {
            if (TryResolveFunctionSymbolId(candidate, out resolvedSymbolId))
            {
                return ResolveFunctionRequiredAbilities(resolvedSymbolId, inferredCallRequirements: null);
            }
        }

        if (TryResolveFunctionSymbolId(infixCall.FunctionName, out resolvedSymbolId))
        {
            return ResolveFunctionRequiredAbilities(resolvedSymbolId, inferredCallRequirements: null);
        }

        return EffectRow.Pure;
    }

    private EffectRow ResolveFunctionRequiredAbilities(SymbolId calleeSymbolId, EffectRow? inferredCallRequirements)
    {
        if (_symbolTable.GetSymbol(calleeSymbolId) is not FuncSymbol funcSymbol)
        {
            return inferredCallRequirements ?? EffectRow.Pure;
        }

        if (funcSymbol.ImplicitAbilities.Count > 0)
        {
            var implicitAbilities = ResolveImplicitAbilities(funcSymbol);
            if (!implicitAbilities.IsPure)
            {
                return ExpandEffectRow(implicitAbilities);
            }
        }

        if (_functionDefinitionsBySymbol.TryGetValue(calleeSymbolId, out var calleeDef) &&
            _functionSummaries.TryGetValue(calleeDef, out var summary))
        {
            var expanded = inferredCallRequirements == null
                ? ExpandEffectRow(summary.DeclaredUpperBound)
                : ExpandEffectRow(summary.DeclaredUpperBound.Union(inferredCallRequirements));

            if (expanded.IsPolymorphic)
            {
                return ExpandEffectRow(ResolvePolymorphicConstraints(calleeDef, expanded));
            }

            return expanded;
        }

        return inferredCallRequirements ?? EffectRow.Pure;
    }

    private static EffectRow ExtractFunctionEffects(Type type)
    {
        return type switch
        {
            TyFun { Effects: { } abilities } when !abilities.IsPure => abilities,
            TyVar or TyCon or TyTuple or TyRef or TyMutRef or TyShared or EffectRow or EffectTag or TyFun => EffectRow.Pure,
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    private static EffectRow ExtractDeclaredAbilitiesFromHandlerType(Type type)
    {
        return type switch
        {
            TyFun { Effects: { } abilities } when !abilities.IsPure => abilities,
            TyVar or TyCon or TyTuple or TyRef or TyMutRef or TyShared or EffectRow or EffectTag or TyFun => EffectRow.Pure,
            _ => throw new System.Diagnostics.UnreachableException()
        };
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

    private string GetCurrentCallerDisplayName()
    {
        return _functionStack.Count > 0
            ? _functionStack.Peek().Name
            : "<module-init>";
    }

    private string ResolveCalleeDisplayName(EidosAstNode? functionExpr)
    {
        var calleeSymbolId = ResolveCalleeSymbolId(functionExpr);
        if (calleeSymbolId.IsValid && _symbolTable.GetSymbol(calleeSymbolId) is FuncSymbol calleeSymbol)
        {
            return calleeSymbol.Name;
        }

        return functionExpr switch
        {
            IdentifierExpr ident when !string.IsNullOrWhiteSpace(ident.Name) => ident.Name,
            PathExpr path when path.Path.Count > 0 => string.Join(WellKnownStrings.Separators.Path, path.Path),
            InfixCallExpr infix when !string.IsNullOrWhiteSpace(infix.FunctionName) => infix.FunctionName,
            _ => "<unknown-callee>"
        };
    }

    private List<EffectTag> ComputeMissingAbilities(EffectRow required, EffectRow available)
    {
        var missing = new List<EffectTag>();
        foreach (var ability in required.Effects)
        {
            if (!ContainsEffect(available, ability))
            {
                missing.Add(ability);
            }
        }

        return missing;
    }

    private static bool ContainsEffect(EffectRow set, EffectTag ability)
    {
        return set.Effects.Any(candidate => IsSameEffect(candidate, ability));
    }

    private static bool IsSameEffect(EffectTag left, EffectTag right)
    {
        if (IsSameBuiltinEffectName(left, right))
        {
            return true;
        }

        if (left.Symbol.IsValid && right.Symbol.IsValid)
        {
            return left.Symbol == right.Symbol;
        }

        if (left.Symbol.IsValid || right.Symbol.IsValid)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(left.Name) && !string.IsNullOrWhiteSpace(right.Name))
        {
            return string.Equals(left.Name, right.Name, StringComparison.Ordinal);
        }

        return left.Equals(right);
    }

    private static bool IsSameBuiltinEffectName(EffectTag left, EffectTag right)
    {
        if (string.IsNullOrWhiteSpace(left.Name) ||
            string.IsNullOrWhiteSpace(right.Name) ||
            !string.Equals(left.Name, right.Name, StringComparison.Ordinal))
        {
            return false;
        }

        return string.Equals(left.Name, WellKnownStrings.BuiltinAbilities.FFI, StringComparison.Ordinal) ||
               string.Equals(left.Name, WellKnownStrings.BuiltinAbilities.IO, StringComparison.Ordinal);
    }

    private EffectRow ExpandEffectRow(EffectRow set)
    {
        if (set.IsPure)
        {
            return EffectRow.Pure;
        }

        var expanded = new HashSet<EffectTag>();
        foreach (var ability in set.Effects)
        {
            ExpandEffectInto(ability, expanded, new HashSet<SymbolId>());
        }

        return new EffectRow(expanded, set.Variables);
    }

    private void ExpandEffectInto(
        EffectTag ability,
        HashSet<EffectTag> target,
        HashSet<SymbolId> visitedAbilities)
    {
        target.Add(ability);

        if (!ability.Symbol.IsValid || !visitedAbilities.Add(ability.Symbol))
        {
            return;
        }

        _ = _symbolTable.GetSymbol(ability.Symbol);
    }

    private EffectTag ResolveEffectTag(IReadOnlyList<string> abilityPath)
    {
        var normalizedPath = abilityPath
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim())
            .ToList();
        if (normalizedPath.Count == 0)
        {
            return new EffectTag();
        }

        var fallbackName = string.Join(WellKnownStrings.Separators.Path, normalizedPath);
        return ResolveEffectTagCore(normalizedPath, fallbackName);
    }

    private EffectTag ResolveEffectTag(string abilityName)
    {
        if (string.IsNullOrWhiteSpace(abilityName))
        {
            return new EffectTag();
        }

        var normalized = abilityName.Trim();
        var path = SplitQualifiedName(normalized);
        if (path.Count == 0)
        {
            return new EffectTag();
        }

        return ResolveEffectTagCore(path, normalized);
    }

    private EffectTag ResolveEffectTag(SymbolId symbolId, IReadOnlyList<string> abilityPath)
    {
        if (symbolId.IsValid && _symbolTable.GetSymbol(symbolId) is EffectSymbol abilitySymbol)
        {
            return new EffectTag(symbolId, abilitySymbol.Name);
        }

        return ResolveEffectTag(abilityPath);
    }

    private EffectTag ResolveEffectTag(SymbolId symbolId, string abilityName)
    {
        if (symbolId.IsValid && _symbolTable.GetSymbol(symbolId) is EffectSymbol abilitySymbol)
        {
            return new EffectTag(symbolId, abilitySymbol.Name);
        }

        return ResolveEffectTag(abilityName);
    }

    private EffectTag ResolveEffectTagCore(
        IReadOnlyList<string> path,
        string fallbackDisplayName)
    {
        var byPath = TryResolveEffectByPath(path);
        if (byPath.HasValue && byPath.Value.IsValid &&
            _symbolTable.GetSymbol(byPath.Value) is EffectSymbol abilityByPath)
        {
            return new EffectTag(byPath.Value, abilityByPath.Name);
        }

        var fullName = path.Count > 0
            ? string.Join(WellKnownStrings.Separators.Path, path)
            : fallbackDisplayName;

        return new EffectTag
        {
            Name = fullName
        };
    }

    private EffectRow ResolveImplicitAbilities(FuncSymbol funcSymbol)
    {
        var abilities = new HashSet<EffectTag>();
        foreach (var name in funcSymbol.ImplicitAbilities)
            abilities.Add(ResolveEffectTag(name));
        return new EffectRow(abilities);
    }

    private EffectRow CreateEntryPointCapabilities()
    {
        return new EffectRow(
        [
            ResolveEffectTag(WellKnownStrings.BuiltinAbilities.FFI),
            ResolveEffectTag(WellKnownStrings.BuiltinAbilities.IO)
        ]);
    }

    private SymbolId? TryResolveEffectByPath(IReadOnlyList<string> path)
    {
        if (path.Count == 0)
        {
            return null;
        }

        var result = _symbolTable.ResolvePathWithResult(path);
        if (result.IsSuccess && result.Kind == ResolutionKind.Effect)
        {
            return result.SymbolId;
        }

        return null;
    }

    private static List<string> SplitQualifiedName(string name)
    {
        return name
            .Replace(WellKnownStrings.Separators.ModulePath, WellKnownStrings.Separators.Path, StringComparison.Ordinal)
            .Split(WellKnownStrings.Separators.Path, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();
    }

    private static string FormatEffectDisplayName(EffectTag ability)
    {
        return !string.IsNullOrWhiteSpace(ability.Name)
            ? ability.Name
            : ability.Symbol.IsValid
                ? $"#{ability.Symbol.Value}"
                : "<unknown>";
    }

    private static string FormatEffectRow(EffectRow set)
    {
        if (set.IsPure || set.Effects.Count == 0)
        {
            return "{}";
        }

        return string.Join(", ", set.Effects
            .Select(FormatEffectDisplayName)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal));
    }

    private void CollectFunctionDefinitions(ModuleDecl module)
    {
        if (!ShouldVisitModule(module))
        {
            return;
        }

        foreach (var decl in module.Declarations)
        {
            switch (decl)
            {
                case FuncDef func when func.SymbolId.IsValid:
                    _functionDefinitionsBySymbol[func.SymbolId] = func;
                    break;
                case ModuleDecl nested:
                    CollectFunctionDefinitions(nested);
                    break;
            }
        }
    }

}
