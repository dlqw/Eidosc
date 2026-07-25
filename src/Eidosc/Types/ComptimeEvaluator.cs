using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

internal sealed record ComptimeEvaluationContext(
    IReadOnlyDictionary<SymbolId, ComptimeValue> Values,
    IReadOnlyDictionary<SymbolId, FuncDef> Functions,
    Func<Type, Type>? ResolveType = null,
    int CallDepth = 0,
    MetaComptimeContext? Meta = null,
    BuildComptimeContext? Build = null,
    ComptimeResourceBudget? Budget = null,
    ComptimeEvaluationFrame? Frame = null)
{
    private readonly ComptimeEvaluationResources _resources = new(
        Budget ?? Meta?.Resources ?? Build?.Resources ?? new ComptimeResourceBudget());

    public ComptimeResourceBudget Resources => _resources.Budget;

    public ComptimeValueArena ValuesArena => _resources.Arena;
}

internal sealed class ComptimeEvaluationResources(ComptimeResourceBudget budget)
{
    public ComptimeResourceBudget Budget { get; } = budget;

    public ComptimeValueArena Arena { get; } = new(budget);
}

internal sealed class ComptimeValueArena(ComptimeResourceBudget resources)
{
    private readonly object _gate = new();
    private readonly HashSet<string> _canonicalValues = new(StringComparer.Ordinal);
    private long _allocatedBytes;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _canonicalValues.Count;
            }
        }
    }

    public long AllocatedBytes
    {
        get
        {
            lock (_gate)
            {
                return _allocatedBytes;
            }
        }
    }

    public bool TryAllocate(ComptimeValue value, out string reason)
    {
        var canonical = value.CanonicalText;
        lock (_gate)
        {
            if (_canonicalValues.Contains(canonical))
            {
                reason = string.Empty;
                return true;
            }

            if (!resources.TryReserveValue(value, out reason))
            {
                return false;
            }

            _canonicalValues.Add(canonical);
            _allocatedBytes += System.Text.Encoding.UTF8.GetByteCount(canonical);
            return true;
        }
    }
}

internal sealed class ComptimeResourceBudget(
    long fuel = ComptimeResourceBudget.DefaultFuel,
    long allocatedBytes = ComptimeResourceBudget.DefaultAllocatedBytes,
    int diagnosticCount = ComptimeResourceBudget.DefaultDiagnosticCount,
    int queryCount = ComptimeResourceBudget.DefaultQueryCount,
    long queryResultBytes = ComptimeResourceBudget.DefaultQueryResultBytes,
    int syntaxNodeCount = ComptimeResourceBudget.DefaultSyntaxNodeCount)
{
    public const long DefaultFuel = 100_000;
    public const long DefaultAllocatedBytes = 64 * 1024 * 1024;
    public const int DefaultDiagnosticCount = 128;
    public const int DefaultQueryCount = 4_096;
    public const long DefaultQueryResultBytes = 16 * 1024 * 1024;
    public const int DefaultSyntaxNodeCount = 100_000;

    private long _remainingFuel = Math.Max(1, fuel);
    private long _remainingAllocatedBytes = Math.Max(1, allocatedBytes);
    private int _remainingDiagnosticCount = Math.Max(1, diagnosticCount);
    private int _remainingQueryCount = Math.Max(1, queryCount);
    private long _remainingQueryResultBytes = Math.Max(1, queryResultBytes);
    private int _remainingSyntaxNodeCount = Math.Max(1, syntaxNodeCount);

    public bool TryConsumeInstruction(out string reason)
    {
        if (Interlocked.Decrement(ref _remainingFuel) >= 0)
        {
            reason = string.Empty;
            return true;
        }

        reason = "comptime executed-instruction fuel budget exceeded";
        return false;
    }

    public bool TryReserveValue(ComptimeValue value, out string reason)
    {
        var byteCount = System.Text.Encoding.UTF8.GetByteCount(value.CanonicalText);
        if (Interlocked.Add(ref _remainingAllocatedBytes, -byteCount) >= 0)
        {
            reason = string.Empty;
            return true;
        }

        reason = "comptime allocated-value byte budget exceeded";
        return false;
    }

    public bool TryConsumeDiagnostic(out string reason)
    {
        if (Interlocked.Decrement(ref _remainingDiagnosticCount) >= 0)
        {
            reason = string.Empty;
            return true;
        }

        reason = "comptime diagnostic count budget exceeded";
        return false;
    }

    public bool TryConsumeQuery(ComptimeValue result, out string reason)
    {
        if (Interlocked.Decrement(ref _remainingQueryCount) < 0)
        {
            reason = "comptime meta query count budget exceeded";
            return false;
        }

        var byteCount = System.Text.Encoding.UTF8.GetByteCount(result.CanonicalText);
        if (Interlocked.Add(ref _remainingQueryResultBytes, -byteCount) < 0)
        {
            reason = "comptime meta query-result byte budget exceeded";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool TryConsumeSyntaxNodes(int count, out string reason)
    {
        if (Interlocked.Add(ref _remainingSyntaxNodeCount, -Math.Max(0, count)) >= 0)
        {
            reason = string.Empty;
            return true;
        }

        reason = "comptime syntax-node budget exceeded";
        return false;
    }
}

internal static partial class ComptimeEvaluator
{
    private const int MaxCallDepth = 32;

    public static bool TryEvaluate(
        EidosAstNode? node,
        IReadOnlyDictionary<SymbolId, ComptimeValue> values,
        out ComptimeValue value,
        out string reason)
    {
        return TryEvaluate(node, values, new Dictionary<SymbolId, FuncDef>(), resolveType: null, out value, out reason);
    }

    public static bool TryEvaluate(
        EidosAstNode? node,
        IReadOnlyDictionary<SymbolId, ComptimeValue> values,
        IReadOnlyDictionary<SymbolId, FuncDef> functions,
        out ComptimeValue value,
        out string reason)
    {
        return TryEvaluate(node, values, functions, resolveType: null, out value, out reason);
    }

    public static bool TryEvaluate(
        EidosAstNode? node,
        IReadOnlyDictionary<SymbolId, ComptimeValue> values,
        IReadOnlyDictionary<SymbolId, FuncDef> functions,
        Func<Type, Type>? resolveType,
        out ComptimeValue value,
        out string reason)
    {
        return TryEvaluate(node, new ComptimeEvaluationContext(values, functions, resolveType), out value, out reason);
    }

    internal static bool TryEvaluate(
        EidosAstNode? node,
        IReadOnlyDictionary<SymbolId, ComptimeValue> values,
        IReadOnlyDictionary<SymbolId, FuncDef> functions,
        Func<Type, Type>? resolveType,
        MetaComptimeContext? meta,
        out ComptimeValue value,
        out string reason)
    {
        return TryEvaluate(node, new ComptimeEvaluationContext(values, functions, resolveType, Meta: meta), out value, out reason);
    }

    internal static bool TryEvaluate(
        EidosAstNode? node,
        IReadOnlyDictionary<SymbolId, ComptimeValue> values,
        IReadOnlyDictionary<SymbolId, FuncDef> functions,
        Func<Type, Type>? resolveType,
        MetaComptimeContext? meta,
        BuildComptimeContext? build,
        out ComptimeValue value,
        out string reason)
    {
        return TryEvaluate(
            node,
            new ComptimeEvaluationContext(values, functions, resolveType, Meta: meta, Build: build),
            out value,
            out reason);
    }

    internal static bool TryInvoke(
        FuncDef function,
        IReadOnlyList<ComptimeValue> arguments,
        IReadOnlyDictionary<SymbolId, ComptimeValue> values,
        IReadOnlyDictionary<SymbolId, FuncDef> functions,
        MetaComptimeContext? meta,
        out ComptimeValue value,
        out string reason)
    {
        return TryEvaluateFunctionBranches(
            function,
            arguments,
            new ComptimeEvaluationContext(values, functions, Meta: meta),
            out value,
            out reason);
    }

    internal static bool TryEvaluateNode(
        EidosAstNode? node,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason) => TryEvaluate(node, context, out value, out reason);

    private static bool TryEvaluate(
        EidosAstNode? node,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryEvaluateCore(node, context, out value, out reason))
        {
            return false;
        }

        if (node?.InferredType is Type inferredType)
        {
            value = value with
            {
                StaticType = value.StaticType ?? context.ResolveType?.Invoke(inferredType) ?? inferredType
            };
        }

        if (!context.ValuesArena.TryAllocate(value, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        return true;
    }

    private static bool TryEvaluateCore(
        EidosAstNode? node,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = "";

        if (!context.Resources.TryConsumeInstruction(out reason))
        {
            return false;
        }

        switch (node)
        {
            case null:
                reason = "missing expression";
                return false;

            case IdentifierExpr { ReflectedType: TyCon, SymbolId.IsValid: true } reflectedIdentifier
                when context.Meta?.SymbolTable.GetSymbol<AdtSymbol>(reflectedIdentifier.SymbolId) is
                    { IsTypeAlias: true } reflectedAlias:
                value = MetaComptimeIntrinsics.CreateTypeValue(reflectedAlias, context.Meta.SymbolTable);
                return true;

            case PathExpr { ReflectedType: TyCon, SymbolId.IsValid: true } reflectedPath
                when context.Meta?.SymbolTable.GetSymbol<AdtSymbol>(reflectedPath.SymbolId) is
                    { IsTypeAlias: true } reflectedPathAlias:
                value = MetaComptimeIntrinsics.CreateTypeValue(reflectedPathAlias, context.Meta.SymbolTable);
                return true;

            case Expression { ReflectedType: TyCon reflectedType } when context.Meta != null:
                value = MetaComptimeIntrinsics.CreateTypeValue(
                    (TyCon)(context.ResolveType?.Invoke(reflectedType) ?? reflectedType),
                    context.Meta.SymbolTable);
                return true;

            case LiteralExpr { IsRecoveredError: false } literal:
                if (ComptimeValue.TryFromLiteral(literal.Value, out value))
                {
                    return true;
                }

                reason = $"literal value of kind '{GetLiteralKind(literal.Value)}' is not supported by the comptime evaluator";
                return false;

            case QuoteExpr quote:
                return ComptimeSyntaxEvaluator.TryEvaluate(quote, context, out value, out reason);
            case ExpandExpr { ExpandedExpression: { } expanded }:
                return TryEvaluateNode(expanded, context, out value, out reason);
            case ExpandExpr:
                value = ComptimeUnitValue.Instance;
                reason = "expression expand was not materialized before comptime evaluation";
                return false;

            case IdentifierExpr identifier:
                return TryEvaluateSymbolReference(identifier, identifier.SymbolId, identifier.Name, context, out value, out reason);

            case PathExpr path:
                return TryEvaluateSymbolReference(path, path.SymbolId, string.Join("::", path.Path), context, out value, out reason);

            case TupleExpr tuple:
                return TryEvaluateTuple(tuple, context, out value, out reason);

            case ListExpr list:
                return TryEvaluateList(list, context, out value, out reason);

            case IndexExpr { IsTypeApplication: false } index:
                return TryEvaluateIndex(index, context, out value, out reason);

            case UnaryExpr unary:
                return TryEvaluateUnary(unary, context, out value, out reason);

            case BinaryExpr binary:
                return TryEvaluateBinary(binary, context, out value, out reason);

            case IfExpr ifExpr:
                return TryEvaluateIf(ifExpr, context, out value, out reason);

            case MatchExpr match:
                return TryEvaluateMatch(match, context, out value, out reason);

            case SelectionExpr selection:
                return TryEvaluateSelection(selection, context, out value, out reason);

            case BlockExpr block:
                return TryEvaluateBlock(block, context, out value, out reason);

            case LoopExpr loop:
                return TryEvaluateLoop(loop, context, out value, out reason);

            case WhileLetExpr whileLet:
                return TryEvaluateWhileLet(whileLet, context, out value, out reason);

            case ReturnExpr returnExpression:
                return TryCreateControlSignal(ComptimeControlKind.Return, returnExpression.Value, context, out value, out reason);

            case BreakExpr breakExpression:
                return TryCreateControlSignal(ComptimeControlKind.Break, breakExpression.Value, context, out value, out reason);

            case ContinueExpr:
                value = new ComptimeControlValue(ComptimeControlKind.Continue, ComptimeUnitValue.Instance);
                return true;

            case LambdaExpr lambda:
                value = CreateLambdaValue(lambda, context, []);
                return true;

            case Assignment assignment:
                return TryEvaluateAssignment(assignment, context, out value, out reason);

            case CallExpr call:
                return TryEvaluateCall(call, context, out value, out reason);

            case CtorExpr constructor:
                return TryEvaluateConstructor(constructor, context, out value, out reason);

            case MethodCallExpr { ResolvedStaticExpression: not null } staticReference:
                return TryEvaluate(staticReference.ResolvedStaticExpression, context, out value, out reason);

            case MethodCallExpr { ResolvedAsStaticPath: true, HasExplicitCallSyntax: false } staticReference:
                return TryEvaluateSymbolReference(
                    staticReference,
                    staticReference.SymbolId,
                    staticReference.MethodName,
                    context,
                    out value,
                    out reason);

            case MethodCallExpr { ResolvedAsFieldAccess: true } fieldAccess:
                return TryEvaluateFieldAccess(fieldAccess, context, out value, out reason);

            case MethodCallExpr { HasExplicitCallSyntax: true } methodCall:
                return TryEvaluateCall(methodCall.ToDesugaredCall(), context, out value, out reason);

            default:
                reason = $"syntax kind '{GetSyntaxKind(node)}' is not supported by the comptime evaluator";
                return false;
        }
    }

    private static bool TryEvaluateIndex(
        IndexExpr index,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        if (index.Object == null || index.Index == null)
        {
            reason = "comptime index expression is incomplete";
            return false;
        }

        if (!TryEvaluate(index.Object, context, out var subject, out reason) ||
            !TryEvaluate(index.Index, context, out var indexValue, out reason))
        {
            return false;
        }

        if (indexValue is not ComptimeIntegerValue integer)
        {
            reason = "comptime index must evaluate to Int";
            return false;
        }

        if (integer.Value < 0 || integer.Value > int.MaxValue)
        {
            reason = $"comptime index {integer.Value} is outside the supported range";
            return false;
        }

        var offset = (int)integer.Value;
        switch (subject)
        {
            case ComptimeSequenceValue sequence when offset < sequence.Elements.Count:
                value = sequence.Elements[offset];
                reason = string.Empty;
                return true;
            case ComptimeSequenceValue sequence:
                reason = $"comptime index {offset} is out of bounds for sequence of length {sequence.Elements.Count}";
                return false;
            case ComptimeStringValue text when offset < text.Value.Length:
                value = new ComptimeCharValue(text.Value[offset]);
                reason = string.Empty;
                return true;
            case ComptimeStringValue text:
                reason = $"comptime index {offset} is out of bounds for string of length {text.Value.Length}";
                return false;
            default:
                reason = $"comptime value kind '{GetValueKind(subject)}' is not indexable";
                return false;
        }
    }

    private static bool TryEvaluateFieldAccess(
        MethodCallExpr fieldAccess,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryEvaluate(fieldAccess.Receiver, context, out var receiver, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        if (receiver is ComptimeMetaObjectValue metaObject &&
            metaObject.TryGet(fieldAccess.MethodName, out value))
        {
            reason = string.Empty;
            return true;
        }

        if (receiver is not ComptimeAdtValue adt)
        {
            value = ComptimeUnitValue.Instance;
            reason = $"field access '{fieldAccess.MethodName}' requires a comptime structured value";
            return false;
        }

        foreach (var namedValue in adt.NamedValues)
        {
            if (string.Equals(namedValue.Name, fieldAccess.MethodName, StringComparison.Ordinal))
            {
                value = namedValue.Value;
                reason = string.Empty;
                return true;
            }
        }

        value = ComptimeUnitValue.Instance;
        reason = $"comptime ADT value '{adt.ConstructorName}' has no field '{fieldAccess.MethodName}'";
        return false;
    }

    private static bool TryEvaluateSymbolReference(
        EidosAstNode reference,
        SymbolId symbolId,
        string displayName,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (!symbolId.IsValid)
        {
            value = ComptimeUnitValue.Instance;
            reason = $"identifier '{displayName}' was not resolved";
            return false;
        }

        if (context.Frame?.TryGet(symbolId, out var storedValue) == true ||
            context.Values.TryGetValue(symbolId, out storedValue))
        {
            value = storedValue;
            reason = "";
            return true;
        }

        var typeSymbol = context.Meta?.SymbolTable.GetSymbol(symbolId);
        if (typeSymbol is AdtSymbol or TraitSymbol or
            TypeParamSymbol { ParameterKind: GenericParameterKind.Type })
        {
            var inferredType = reference.InferredType is not Type rawInferredType
                ? null
                : context.ResolveType?.Invoke(rawInferredType) ?? rawInferredType;
            value = inferredType is TyCon inferredConstructor &&
                    inferredConstructor.Symbol == symbolId
                ? MetaComptimeIntrinsics.CreateTypeValue(inferredConstructor, context.Meta!.SymbolTable)
                : MetaComptimeIntrinsics.CreateTypeValue(typeSymbol, context.Meta!.SymbolTable);
            reason = string.Empty;
            return true;
        }

        value = ComptimeUnitValue.Instance;
        reason = $"identifier '{displayName}' is not a previously evaluated comptime binding";
        return false;
    }

    private static bool TryEvaluateIf(
        IfExpr ifExpr,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryEvaluate(ifExpr.Condition, context, out var condition, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        if (condition is not ComptimeBoolValue { Value: var conditionValue })
        {
            value = ComptimeUnitValue.Instance;
            reason = "if condition must evaluate to a comptime bool";
            return false;
        }

        var selectedBranch = conditionValue ? ifExpr.ThenBranch : ifExpr.ElseBranch;
        if (selectedBranch == null)
        {
            value = ComptimeUnitValue.Instance;
            reason = conditionValue
                ? "if expression is missing a then branch"
                : "if expression is missing an else branch";
            return false;
        }

        return TryEvaluate(selectedBranch, context, out value, out reason);
    }

    private static bool TryEvaluateMatch(
        MatchExpr match,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (match.MatchedExpression == null)
        {
            value = ComptimeUnitValue.Instance;
            reason = "match expression is missing a matched expression";
            return false;
        }

        if (!TryEvaluate(match.MatchedExpression, context, out var matchedValue, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        foreach (var branch in match.Branches)
        {
            if (!TryPatternMatches(branch.Pattern, matchedValue, out var matches, out var bindings, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            if (!matches)
            {
                continue;
            }

            var branchValues = MergeValues(context.Values, bindings);
            var branchContext = context with { Values = branchValues };
            var guardMatches = true;
            var guardedContext = branchContext;
            if (branch.Guard != null &&
                !TryEvaluateGuard(branch.Guard, branchContext, out guardMatches, out guardedContext, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            branchContext = guardedContext;
            if (branch.Guard != null && !guardMatches)
            {
                continue;
            }

            if (branch.Expression == null)
            {
                value = ComptimeUnitValue.Instance;
                reason = "matching comptime branch is missing a result expression";
                return false;
            }

            return TryEvaluate(branch.Expression, branchContext, out value, out reason);
        }

        value = ComptimeUnitValue.Instance;
        reason = "comptime match expression has no matching branch";
        return false;
    }

    private static bool TryEvaluateSelection(
        SelectionExpr selection,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = string.Empty;
        if (selection.Subject == null ||
            !TryEvaluate(selection.Subject, context, out var evaluatedSubject, out reason))
        {
            reason = selection.Subject == null ? "selection expression is missing a subject" : reason;
            return false;
        }

        var subjectValues = selection.IsGroup
            ? evaluatedSubject is ComptimeSequenceValue { Kind: ComptimeSequenceKind.Tuple } tuple
                ? tuple.Elements
                : []
            : [evaluatedSubject];
        if (subjectValues.Count != selection.Subjects.Count)
        {
            reason = "selection subject shape does not match its type-directed lowering";
            return false;
        }

        var positivePayloads = new List<ComptimeValue>();
        var negativePayloads = new List<ComptimeValue>();
        var allPositive = true;
        for (var index = 0; index < selection.Subjects.Count; index++)
        {
            var desugaring = selection.Subjects[index];
            var subjectValue = subjectValues[index];
            var isPositive = desugaring.Kind switch
            {
                SelectionSubjectKind.Bool when subjectValue is ComptimeBoolValue boolean => boolean.Value,
                SelectionSubjectKind.Option or SelectionSubjectKind.Result or SelectionSubjectKind.Either
                    when subjectValue is ComptimeAdtValue adt => adt.HasSameConstructor(
                        desugaring.PositiveConstructorSymbolId,
                        desugaring.Kind switch
                        {
                            SelectionSubjectKind.Option => "Some",
                            SelectionSubjectKind.Result => "Ok",
                            _ => "Right"
                        }),
                _ => false
            };
            allPositive &= isPositive;

            if (subjectValue is ComptimeAdtValue payloadAdt)
            {
                if (isPositive)
                {
                    positivePayloads.AddRange(payloadAdt.PositionalValues);
                }
                else if (!selection.IsGroup)
                {
                    negativePayloads.AddRange(payloadAdt.PositionalValues);
                }
            }
        }

        var arm = allPositive ? selection.ThenArm : selection.ElseArm;
        if (arm == null)
        {
            reason = string.Empty;
            return true;
        }

        var payloads = allPositive ? positivePayloads : negativePayloads;
        var symbols = allPositive ? selection.ThenPlaceholderSymbols : selection.ElsePlaceholderSymbols;
        var bindings = new Dictionary<SymbolId, ComptimeValue>();
        foreach (var (payloadIndex, symbolId) in symbols)
        {
            if (symbolId.IsValid && payloadIndex >= 0 && payloadIndex < payloads.Count)
            {
                bindings[symbolId] = payloads[payloadIndex];
            }
        }

        var armContext = context with { Values = MergeValues(context.Values, bindings) };
        return TryEvaluate(arm, armContext, out value, out reason);
    }

    private static bool TryEvaluateGuard(
        EidosAstNode guard,
        ComptimeEvaluationContext context,
        out bool matches,
        out ComptimeEvaluationContext guardContext,
        out string reason)
    {
        guardContext = context;

        switch (guard)
        {
            case SequentialGuardExpr sequentialGuard:
                foreach (var nestedGuard in sequentialGuard.Guards)
                {
                    if (!TryEvaluateGuard(nestedGuard, guardContext, out matches, out guardContext, out reason))
                    {
                        return false;
                    }

                    if (!matches)
                    {
                        return true;
                    }
                }

                matches = true;
                reason = "";
                return true;

            case PatternGuardExpr patternGuard:
                if (patternGuard.SourceExpression == null)
                {
                    matches = false;
                    reason = "pattern guard is missing a source expression";
                    return false;
                }

                if (patternGuard.Pattern == null)
                {
                    matches = false;
                    reason = "pattern guard is missing a pattern";
                    return false;
                }

                if (!TryEvaluate(patternGuard.SourceExpression, context, out var sourceValue, out reason))
                {
                    matches = false;
                    return false;
                }

                if (!TryPatternMatches(patternGuard.Pattern, sourceValue, out matches, out var guardBindings, out reason))
                {
                    return false;
                }

                if (matches)
                {
                    guardContext = context with { Values = MergeValues(context.Values, guardBindings) };
                }

                return true;

            default:
                if (!TryEvaluate(guard, context, out var guardValue, out reason))
                {
                    matches = false;
                    return false;
                }

                if (guardValue is not ComptimeBoolValue { Value: var boolValue })
                {
                    matches = false;
                    reason = "match guard must evaluate to a comptime bool";
                    return false;
                }

                matches = boolValue;
                return true;
        }
    }

    private static bool TryPatternMatches(
        Pattern? pattern,
        ComptimeValue value,
        out bool matches,
        out IReadOnlyDictionary<SymbolId, ComptimeValue> bindings,
        out string reason)
    {
        bindings = new Dictionary<SymbolId, ComptimeValue>();
        reason = "";

        var normalizedPattern = Pattern.NormalizePatternNode(pattern ?? new WildcardPattern());
        switch (normalizedPattern)
        {
            case WildcardPattern:
                matches = true;
                return true;

            case LiteralPattern literal:
                if (!ComptimeValue.TryFromLiteral(literal.Value, out var literalValue))
                {
                    matches = false;
                    reason = $"literal pattern value of kind '{GetLiteralKind(literal.Value)}' is not supported by the comptime evaluator";
                    return false;
                }

                matches = ValuesEqual(value, literalValue);
                return true;

            case VarPattern varPattern:
                if (varPattern.BindingMode != PatternBindingMode.ByValue)
                {
                    matches = false;
                    reason = "borrow binding patterns are not supported by the comptime match evaluator";
                    return false;
                }

                if (!varPattern.SymbolId.IsValid)
                {
                    matches = false;
                    reason = $"binding pattern '{varPattern.Name}' was not resolved";
                    return false;
                }

                bindings = new Dictionary<SymbolId, ComptimeValue>
                {
                    [varPattern.SymbolId] = value
                };
                matches = true;
                return true;

            case OrPattern orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    if (!TryPatternMatches(alternative, value, out var alternativeMatches, out var alternativeBindings, out reason))
                    {
                        matches = false;
                        return false;
                    }

                    if (alternativeMatches)
                    {
                        bindings = alternativeBindings;
                        matches = true;
                        return true;
                    }
                }

                matches = false;
                return true;

            case RangePattern rangePattern:
                if (!TryGetInteger(value, out var intValue) ||
                    rangePattern.Start?.Value == null ||
                    rangePattern.End?.Value == null ||
                    !ComptimeValue.TryFromLiteral(rangePattern.Start.Value, out var startValue) ||
                    !ComptimeValue.TryFromLiteral(rangePattern.End.Value, out var endValue) ||
                    !TryGetInteger(startValue, out var start) ||
                    !TryGetInteger(endValue, out var end))
                {
                    matches = false;
                    reason = "only integer range patterns are supported by the comptime evaluator";
                    return false;
                }

                matches = intValue >= start && intValue <= end;
                return true;

            case TuplePattern tuplePattern:
                return TrySequencePatternMatches(
                    tuplePattern.Elements,
                    hasRestMarker: false,
                    restPattern: null,
                    suffixPatterns: [],
                    value,
                    "tuple",
                    out matches,
                    out bindings,
                    out reason);

            case ListPattern listPattern:
                return TrySequencePatternMatches(
                    listPattern.Elements,
                    listPattern.HasRestMarker,
                    listPattern.RestPattern,
                    listPattern.SuffixElements,
                    value,
                    "list",
                    out matches,
                    out bindings,
                    out reason);

            case CtorPattern constructorPattern:
                return TryConstructorPatternMatches(
                    constructorPattern,
                    value,
                    out matches,
                    out bindings,
                    out reason);

            default:
                matches = false;
                reason = $"pattern kind '{GetSyntaxKind(normalizedPattern)}' is not supported by the comptime match evaluator";
                return false;
        }
    }

    private static bool TrySequencePatternMatches(
        IReadOnlyList<Pattern> elementPatterns,
        bool hasRestMarker,
        Pattern? restPattern,
        IReadOnlyList<Pattern> suffixPatterns,
        ComptimeValue value,
        string sequenceKind,
        out bool matches,
        out IReadOnlyDictionary<SymbolId, ComptimeValue> bindings,
        out string reason)
    {
        var sequenceBindings = new Dictionary<SymbolId, ComptimeValue>();
        bindings = sequenceBindings;

        if (value is not ComptimeSequenceValue sequence ||
            !IsExpectedSequenceKind(sequence.Kind, sequenceKind))
        {
            matches = false;
            reason = "";
            return true;
        }

        var elements = sequence.Elements;
        var minimumLength = elementPatterns.Count + suffixPatterns.Count;
        if (!hasRestMarker && elements.Count != minimumLength)
        {
            matches = false;
            reason = "";
            return true;
        }

        if (hasRestMarker && elements.Count < minimumLength)
        {
            matches = false;
            reason = "";
            return true;
        }

        for (var i = 0; i < elementPatterns.Count; i++)
        {
            if (!TryPatternMatches(elementPatterns[i], elements[i], out var elementMatches, out var elementBindings, out reason))
            {
                matches = false;
                return false;
            }

            if (!elementMatches)
            {
                matches = false;
                return true;
            }

            foreach (var binding in elementBindings)
            {
                sequenceBindings[binding.Key] = binding.Value;
            }
        }

        for (var i = 0; i < suffixPatterns.Count; i++)
        {
            var sourceIndex = elements.Count - suffixPatterns.Count + i;
            if (!TryPatternMatches(
                    suffixPatterns[i],
                    elements[sourceIndex],
                    out var suffixMatches,
                    out var suffixBindings,
                    out reason))
            {
                matches = false;
                return false;
            }

            if (!suffixMatches)
            {
                matches = false;
                return true;
            }

            foreach (var binding in suffixBindings)
            {
                sequenceBindings[binding.Key] = binding.Value;
            }
        }

        if (hasRestMarker && !TryBindRestPattern(
                restPattern,
                sequence.Kind,
                elements
                    .Skip(elementPatterns.Count)
                    .Take(elements.Count - minimumLength)
                    .ToArray(),
                sequenceBindings,
                out reason))
        {
            matches = false;
            return false;
        }

        matches = true;
        reason = "";
        return true;
    }

    private static bool TryConstructorPatternMatches(
        CtorPattern pattern,
        ComptimeValue value,
        out bool matches,
        out IReadOnlyDictionary<SymbolId, ComptimeValue> bindings,
        out string reason)
    {
        var constructorBindings = new Dictionary<SymbolId, ComptimeValue>();
        bindings = constructorBindings;
        reason = "";

        if (value is not ComptimeAdtValue adtValue ||
            !adtValue.HasSameConstructor(pattern.SymbolId, BuildConstructorDisplayName(pattern)))
        {
            matches = false;
            return true;
        }

        if (pattern.PositionalPatterns.Count != adtValue.PositionalValues.Count)
        {
            matches = false;
            return true;
        }

        for (var i = 0; i < pattern.PositionalPatterns.Count; i++)
        {
            if (!TryPatternMatches(
                    pattern.PositionalPatterns[i],
                    adtValue.PositionalValues[i],
                    out var elementMatches,
                    out var elementBindings,
                    out reason))
            {
                matches = false;
                return false;
            }

            if (!elementMatches)
            {
                matches = false;
                return true;
            }

            MergePatternBindings(constructorBindings, elementBindings);
        }

        if (!pattern.HasRecordRest &&
            pattern.NamedPatterns.Count > 0 &&
            pattern.NamedPatterns.Count != adtValue.NamedValues.Count)
        {
            matches = false;
            return true;
        }

        var namedValues = adtValue.NamedValues.ToDictionary(
            static field => field.Name,
            static field => field.Value,
            StringComparer.Ordinal);
        foreach (var fieldPattern in pattern.NamedPatterns)
        {
            if (!namedValues.TryGetValue(fieldPattern.FieldName, out var fieldValue) ||
                fieldPattern.Pattern == null)
            {
                matches = false;
                return true;
            }

            if (!TryPatternMatches(
                    fieldPattern.Pattern,
                    fieldValue,
                    out var fieldMatches,
                    out var fieldBindings,
                    out reason))
            {
                matches = false;
                return false;
            }

            if (!fieldMatches)
            {
                matches = false;
                return true;
            }

            MergePatternBindings(constructorBindings, fieldBindings);
        }

        matches = true;
        return true;
    }

    private static void MergePatternBindings(
        Dictionary<SymbolId, ComptimeValue> target,
        IReadOnlyDictionary<SymbolId, ComptimeValue> source)
    {
        foreach (var binding in source)
        {
            target[binding.Key] = binding.Value;
        }
    }

    private static string BuildConstructorDisplayName(CtorPattern pattern)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(pattern.PackageAlias))
        {
            parts.Add(pattern.PackageAlias);
        }

        parts.AddRange(pattern.ModulePath);
        parts.Add(pattern.ConstructorName);
        return string.Join("::", parts);
    }

    private static bool TryBindRestPattern(
        Pattern? restPattern,
        ComptimeSequenceKind sequenceKind,
        IReadOnlyList<ComptimeValue> restElements,
        Dictionary<SymbolId, ComptimeValue> bindings,
        out string reason)
    {
        reason = "";
        var normalizedRestPattern = restPattern == null
            ? null
            : Pattern.NormalizePatternNode(restPattern);

        switch (normalizedRestPattern)
        {
            case null:
            case WildcardPattern:
                return true;

            case VarPattern { BindingMode: PatternBindingMode.ByValue, SymbolId.IsValid: true } varPattern:
                bindings[varPattern.SymbolId] = new ComptimeSequenceValue(sequenceKind, restElements);
                return true;

            case VarPattern { BindingMode: not PatternBindingMode.ByValue }:
                reason = "borrow list rest binding patterns are not supported by the comptime match evaluator";
                return false;

            case VarPattern varPattern:
                reason = $"list rest binding pattern '{varPattern.Name}' was not resolved";
                return false;

            default:
                reason = "only wildcard and by-value binding rest patterns are supported by the comptime match evaluator";
                return false;
        }
    }

    private static bool IsExpectedSequenceKind(ComptimeSequenceKind actual, string expected)
    {
        return expected switch
        {
            "tuple" => actual == ComptimeSequenceKind.Tuple,
            "list" => actual == ComptimeSequenceKind.List,
            _ => false
        };
    }

    private static bool FunctionRequiresRuntimeAbilities(FuncDef function)
    {
        if (function.RequiredAbilities.Count > 0)
        {
            return true;
        }

        return function.InferredType is Type inferredType &&
            FunctionTypeRequiresRuntimeAbilities(inferredType);
    }

    private static bool FunctionTypeRequiresRuntimeAbilities(Type? type)
    {
        return type switch
        {
            TyFun { Effects: { } abilities } when !abilities.IsPure => true,
            TyFun { Result: TyFun nested } => FunctionTypeRequiresRuntimeAbilities(nested),
            _ => false
        };
    }

    private static IReadOnlyDictionary<SymbolId, ComptimeValue> MergeValues(
        IReadOnlyDictionary<SymbolId, ComptimeValue> values,
        IReadOnlyDictionary<SymbolId, ComptimeValue> bindings)
    {
        if (bindings.Count == 0)
        {
            return values;
        }

        var merged = new Dictionary<SymbolId, ComptimeValue>(values);
        foreach (var binding in bindings)
        {
            merged[binding.Key] = binding.Value;
        }

        return merged;
    }

    private static bool TryEvaluateBlock(
        BlockExpr block,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        return TryEvaluateControlFlowBlock(block, context, out value, out reason);
    }

    private static bool TryEvaluateConstructor(
        CtorExpr constructor,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = "";

        var positionalValues = new List<ComptimeValue>(constructor.PositionalArgs.Count);
        foreach (var argument in constructor.PositionalArgs)
        {
            if (!TryEvaluate(argument, context, out var argumentValue, out reason))
            {
                return false;
            }

            positionalValues.Add(argumentValue);
        }

        var namedValues = new List<ComptimeNamedValue>();
        if (constructor.UpdateBase != null)
        {
            if (!TryEvaluate(constructor.UpdateBase, context, out var baseValue, out reason))
            {
                return false;
            }

            var constructorName = BuildConstructorDisplayName(constructor);
            var constructorIdentity = ResolveConstructorIdentity(constructor, context, constructorName);
            if (baseValue is not ComptimeAdtValue baseAdt ||
                !baseAdt.HasSameConstructor(constructor.SymbolId, constructorName, constructorIdentity))
            {
                reason = "comptime constructor update base must have the same constructor";
                return false;
            }

            positionalValues.AddRange(baseAdt.PositionalValues);
            namedValues.AddRange(baseAdt.NamedValues);
        }

        foreach (var field in constructor.NamedArgs)
        {
            if (field.Value == null)
            {
                reason = $"comptime constructor field '{field.FieldName}' is missing a value";
                return false;
            }

            if (!TryEvaluate(field.Value, context, out var fieldValue, out reason))
            {
                return false;
            }

            var existingIndex = namedValues.FindIndex(existing =>
                string.Equals(existing.Name, field.FieldName, StringComparison.Ordinal));
            var namedValue = new ComptimeNamedValue(field.FieldName, fieldValue);
            if (existingIndex >= 0)
            {
                namedValues[existingIndex] = namedValue;
            }
            else
            {
                namedValues.Add(namedValue);
            }
        }

        var displayName = BuildConstructorDisplayName(constructor);
        value = new ComptimeAdtValue(
            constructor.SymbolId,
            displayName,
            positionalValues,
            namedValues)
        {
            ConstructorIdentity = ResolveConstructorIdentity(constructor, context, displayName)
        };
        return true;
    }

    private static string ResolveConstructorIdentity(
        CtorExpr constructor,
        ComptimeEvaluationContext context,
        string fallback)
    {
        if (constructor.SymbolId.IsValid &&
            context.Meta?.SymbolTable.GetSymbol(constructor.SymbolId) is { } symbol)
        {
            return MetaComptimeIntrinsics.CreateStableIdentity(symbol, context.Meta.SymbolTable);
        }

        return fallback;
    }

    private static string BuildConstructorDisplayName(CtorExpr constructor)
    {
        var parts = constructor.ConstructorPath?.ToQualifiedPathParts() ?? [];
        return parts.Count > 0
            ? string.Join(WellKnownStrings.Separators.Path, parts)
            : constructor.ConstructorName;
    }

    private static bool TryEvaluateCall(
        CallExpr call,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        if (TryCreateResolvedConstructorExpression(call, out var constructor))
        {
            return TryEvaluateConstructor(constructor, context, out value, out reason);
        }

        if (context.CallDepth >= MaxCallDepth)
        {
            reason = "comptime function call depth exceeded";
            return false;
        }

        if (!TryGetCallTargetSymbol(call.Function, out var calleeSymbolId, out var displayName))
        {
            reason = "comptime call target was not resolved";
            return false;
        }

        if (context.Meta?.SymbolTable.GetSymbol<FuncSymbol>(calleeSymbolId) is { } intrinsicSymbol &&
            MetaSchemaRegistry.IsMetaIntrinsic(intrinsicSymbol, out var intrinsicName))
        {
            return MetaComptimeIntrinsics.TryEvaluate(intrinsicName, call, context, out value, out reason);
        }

        var intrinsicSymbolTable = context.Build?.SymbolTable ?? context.Meta?.SymbolTable;
        if (intrinsicSymbolTable?.GetSymbol<FuncSymbol>(calleeSymbolId) is { } buildIntrinsicSymbol &&
            BuildSchemaRegistry.IsBuildIntrinsic(buildIntrinsicSymbol, out var buildIntrinsicName))
        {
            return BuildComptimeIntrinsics.TryEvaluate(buildIntrinsicName, call, context, out value, out reason);
        }

        context.Functions.TryGetValue(calleeSymbolId, out var function);

        var argValues = new List<ComptimeValue>(call.PositionalArgs.Count + call.NamedArgs.Count);
        foreach (var arg in call.PositionalArgs)
        {
            if (!TryEvaluate(arg, context, out var argValue, out reason))
            {
                return false;
            }

            argValues.Add(argValue);
        }

        foreach (var arg in call.NamedArgs)
        {
            if (arg.Value == null)
            {
                reason = $"named comptime argument '{arg.Name}' is missing a value";
                return false;
            }
            if (!TryEvaluate(arg.Value, context, out var argValue, out reason))
            {
                return false;
            }
            argValues.Add(argValue);
        }

        if (function == null && TryEvaluate(call.Function, context, out var callable, out _))
        {
            switch (callable)
            {
                case ComptimeLambdaValue lambdaValue:
                    return TryApplyLambdaValue(lambdaValue, argValues, context, out value, out reason);
                case ComptimeFunctionValue functionValue:
                    return TryApplyFunctionValue(functionValue, argValues, context, out value, out reason);
            }
        }

        if (function == null || function.Body.Count == 0)
        {
            if (!TryResolveComptimeTraitDispatch(calleeSymbolId, argValues, context, out function))
            {
                reason = $"call target '{displayName}' has no body available for comptime lowering";
                return false;
            }
        }

        if (FunctionRequiresRuntimeAbilities(function))
        {
            reason = $"function '{function.Name}' requires runtime abilities that are unavailable during comptime evaluation";
            return false;
        }

        var callableArity = GetCallableArity(function.InferredType as Type);
        var usesImplicitUnitArgument = argValues.Count == 0 && HasSingleUnitParameter(function.InferredType as Type);
        if (callableArity > 0 && argValues.Count < callableArity && !usesImplicitUnitArgument)
        {
            value = new ComptimeFunctionValue(
                function,
                context.Frame?.Snapshot() ?? new Dictionary<SymbolId, ComptimeValue>(),
                argValues.ToArray());
            reason = string.Empty;
            return true;
        }

        return TryEvaluateFunctionBranches(function, argValues, context, out value, out reason);
    }

    private static bool HasSingleUnitParameter(Type? type)
    {
        return type is TyFun { Params: [var parameter] } && IsUnitComptimeType(parameter);
    }

    private static bool IsUnitComptimeType(Type type)
    {
        return type switch
        {
            TyVar { Instance: { } instance } => IsUnitComptimeType(instance),
            TyCon constructor =>
                constructor.Id.Value == BaseTypes.UnitId ||
                constructor.Name is WellKnownStrings.BuiltinTypes.Unit or "()",
            _ => false
        };
    }

    private static bool TryEvaluateFunctionBranches(
        FuncDef function,
        IReadOnlyList<ComptimeValue> argValues,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        var trace = context.Meta?.Trace;
        var tracePhase = context.Meta?.TracePhase ?? "comptime";
        trace?.Record(
            tracePhase,
            "call",
            function.Name,
            "begin",
            $"arguments={argValues.Count}",
            function.Span,
            context.CallDepth + 1);

        if (TryEvaluateFunctionBranchesWithFirstArgument(
                function,
                argValues.Count == 0 ? [ComptimeUnitValue.Instance] : argValues,
                context with { CallDepth = context.CallDepth + 1 },
                out value,
                out reason))
        {
            trace?.Record(
                tracePhase,
                "call",
                function.Name,
                "success",
                value.CanonicalText,
                function.Span,
                context.CallDepth + 1);
            return true;
        }

        if (argValues.Count <= 1 ||
            !reason.Contains("no matching branch", StringComparison.Ordinal))
        {
            trace?.Record(
                tracePhase,
                "call",
                function.Name,
                "failure",
                reason,
                function.Span,
                context.CallDepth + 1);
            return false;
        }

        var tupleArg = new ComptimeSequenceValue(
            ComptimeSequenceKind.Tuple,
            argValues.ToArray());
        var tupleResult = TryEvaluateFunctionBranchesWithFirstArgument(
            function,
            [tupleArg],
            context with { CallDepth = context.CallDepth + 1 },
            out value,
            out reason);
        trace?.Record(
            tracePhase,
            "call",
            function.Name,
            tupleResult ? "success" : "failure",
            tupleResult ? value.CanonicalText : reason,
            function.Span,
            context.CallDepth + 1);
        return tupleResult;
    }

    private static bool TryEvaluateFunctionBranchesWithFirstArgument(
        FuncDef function,
        IReadOnlyList<ComptimeValue> argValues,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = "";
        var effectiveArg = argValues[0];
        foreach (var branch in function.Body)
        {
            if (!TryPatternMatches(branch.Pattern, effectiveArg, out var matches, out var bindings, out reason))
            {
                return false;
            }

            if (!matches)
            {
                continue;
            }

            var callContext = context with
            {
                Frame = new ComptimeEvaluationFrame(context.Frame)
            };
            callContext.Frame.DefineRange(bindings);

            var guardMatches = true;
            if (branch.Guard != null &&
                !TryEvaluateGuard(branch.Guard, callContext, out guardMatches, out callContext, out reason))
            {
                return false;
            }

            if (!guardMatches)
            {
                continue;
            }

            if (branch.Expression == null)
            {
                reason = $"matching comptime function branch for '{function.Name}' is missing a result expression";
                return false;
            }

            return TryEvaluateCallableResult(
                branch.Expression,
                argValues.Skip(1).ToList(),
                callContext,
                out value,
                out reason);
        }

        reason = $"comptime function '{function.Name}' has no matching branch";
        return false;
    }

    private static bool TryEvaluateCallableResult(
        EidosAstNode expression,
        IReadOnlyList<ComptimeValue> remainingArgs,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (remainingArgs.Count == 0)
        {
            if (expression is LambdaExpr deferredLambda)
            {
                value = CreateLambdaValue(deferredLambda, context, []);
                reason = string.Empty;
                return true;
            }

            if (!TryEvaluate(expression, context, out value, out reason))
            {
                return false;
            }

            if (value is ComptimeControlValue { Kind: ComptimeControlKind.Return } returned)
            {
                value = returned.Value;
                return true;
            }

            if (value is ComptimeControlValue control)
            {
                reason = $"'{control.Kind.ToString().ToLowerInvariant()}' escaped its enclosing comptime control-flow construct";
                value = ComptimeUnitValue.Instance;
                return false;
            }

            return true;
        }

        if (expression is not LambdaExpr lambda)
        {
            value = ComptimeUnitValue.Instance;
            reason = $"syntax kind '{GetSyntaxKind(expression)}' cannot accept remaining comptime call arguments";
            return false;
        }

        return TryApplyLambda(lambda, remainingArgs, context, out value, out reason);
    }

    private static bool TryApplyLambda(
        LambdaExpr lambda,
        IReadOnlyList<ComptimeValue> argValues,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = "";
        if (lambda.Parameters.Count == 0)
        {
            reason = "comptime lambda has no parameters for remaining call arguments";
            return false;
        }

        if (argValues.Count < lambda.Parameters.Count)
        {
            value = CreateLambdaValue(lambda, context, argValues);
            reason = string.Empty;
            return true;
        }

        var lambdaValues = context.Values;
        for (var i = 0; i < lambda.Parameters.Count; i++)
        {
            if (!TryPatternMatches(lambda.Parameters[i], argValues[i], out var matches, out var bindings, out reason))
            {
                return false;
            }

            if (!matches)
            {
                reason = "comptime lambda argument did not match its parameter pattern";
                return false;
            }

            lambdaValues = MergeValues(lambdaValues, bindings);
        }

        if (lambda.Body == null)
        {
            reason = "comptime lambda is missing a body";
            return false;
        }

        return TryEvaluateCallableResult(
            lambda.Body,
            argValues.Skip(lambda.Parameters.Count).ToList(),
            context with { Values = lambdaValues },
            out value,
            out reason);
    }

    private static ComptimeLambdaValue CreateLambdaValue(
        LambdaExpr lambda,
        ComptimeEvaluationContext context,
        IReadOnlyList<ComptimeValue> boundArguments) =>
        new(
            lambda,
            context.Frame?.Snapshot() ?? new Dictionary<SymbolId, ComptimeValue>(),
            boundArguments.ToArray());

    private static bool TryApplyLambdaValue(
        ComptimeLambdaValue lambdaValue,
        IReadOnlyList<ComptimeValue> arguments,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        var frame = new ComptimeEvaluationFrame(context.Frame);
        frame.DefineRange(lambdaValue.Captures);
        return TryApplyLambda(
            lambdaValue.Lambda,
            lambdaValue.BoundArguments.Concat(arguments).ToArray(),
            context with { Frame = frame },
            out value,
            out reason);
    }

    private static bool TryApplyFunctionValue(
        ComptimeFunctionValue functionValue,
        IReadOnlyList<ComptimeValue> arguments,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        var combinedArguments = functionValue.BoundArguments.Concat(arguments).ToArray();
        var callableArity = GetCallableArity(functionValue.Function.InferredType as Type);
        if (callableArity > 0 && combinedArguments.Length < callableArity)
        {
            value = functionValue with { BoundArguments = combinedArguments };
            reason = string.Empty;
            return true;
        }

        var frame = new ComptimeEvaluationFrame(context.Frame);
        frame.DefineRange(functionValue.Captures);
        return TryEvaluateFunctionBranches(
            functionValue.Function,
            combinedArguments,
            context with { Frame = frame },
            out value,
            out reason);
    }

    private static int GetCallableArity(Type? type)
    {
        var arity = 0;
        while (type is TyFun function)
        {
            arity += function.Params.Count;
            type = function.Result;
        }

        return arity;
    }

    private static bool TryGetCallTargetSymbol(
        EidosAstNode? target,
        out SymbolId symbolId,
        out string displayName)
    {
        switch (target)
        {
            case IdentifierExpr identifier:
                symbolId = identifier.SymbolId;
                displayName = identifier.Name;
                return symbolId.IsValid;

            case PathExpr path:
                symbolId = path.SymbolId;
                displayName = string.Join("::", path.Path);
                return symbolId.IsValid;

            default:
                symbolId = SymbolId.None;
                displayName = "<unknown>";
                return false;
        }
    }

    private static bool TryEvaluateTuple(
        TupleExpr tuple,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        var elements = new List<ComptimeValue>(tuple.Elements.Count);
        foreach (var element in tuple.Elements)
        {
            if (!TryEvaluate(element, context, out var elementValue, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            elements.Add(elementValue);
        }

        value = new ComptimeSequenceValue(ComptimeSequenceKind.Tuple, elements);
        reason = "";
        return true;
    }

    private static bool TryCreateResolvedConstructorExpression(CallExpr call, out CtorExpr constructor)
    {
        constructor = null!;
        var application = call.Function as IndexExpr;
        var target = application is { IsTypeApplication: true, Object: not null }
            ? application.Object
            : call.Function;

        string name;
        SymbolId symbolId;
        TypePath? path = null;
        switch (target)
        {
            case IdentifierExpr { IsConstructor: true } identifier:
                name = identifier.Name;
                symbolId = identifier.SymbolId;
                break;
            case PathExpr { IsConstructorPath: true } constructorPath:
                name = constructorPath.Name;
                symbolId = constructorPath.SymbolId;
                path = new TypePath();
                path.SetSpan(constructorPath.Span);
                path.SetPackageAlias(constructorPath.PackageAlias);
                path.ModulePath = [.. constructorPath.ModulePath];
                path.SetTypeName(constructorPath.Name);
                path.SetGenericArguments(constructorPath.GenericArguments);
                break;
            default:
                return false;
        }

        if (application is { IsTypeApplication: true })
        {
            path ??= new TypePath();
            path.SetSpan(application.Span);
            path.SetTypeName(name);
            path.SetGenericArguments(application.GenericArguments);
        }

        constructor = new CtorExpr();
        constructor.SetSpan(call.Span);
        constructor.SetConstructorName(name);
        constructor.SymbolId = symbolId;
        if (path != null)
        {
            path.SymbolId = symbolId;
            constructor.SetConstructorPath(path);
        }

        foreach (var argument in call.PositionalArgs)
        {
            constructor.AddPositionalArg(argument);
        }

        foreach (var namedArgument in call.NamedArgs)
        {
            var field = new FieldInit();
            field.SetSpan(namedArgument.Span);
            field.SetFieldName(namedArgument.Name);
            if (namedArgument.Value != null)
            {
                field.SetValue(namedArgument.Value);
            }
            constructor.AddNamedArg(field);
        }

        return true;
    }

    private static bool TryEvaluateList(
        ListExpr list,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        var elements = new List<ComptimeValue>(list.Elements.Count);
        var elementCount = list.HasRest ? list.Elements.Count - 1 : list.Elements.Count;
        for (var i = 0; i < elementCount; i++)
        {
            if (!TryEvaluate(list.Elements[i], context, out var elementValue, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            elements.Add(elementValue);
        }

        if (list.HasRest)
        {
            if (list.Elements.Count == 0)
            {
                value = ComptimeUnitValue.Instance;
                reason = "comptime list spread is missing its tail expression";
                return false;
            }

            if (!TryEvaluate(list.Elements[^1], context, out var tail, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            if (tail is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } tailList)
            {
                value = ComptimeUnitValue.Instance;
                reason = "comptime list spread tail must evaluate to a list";
                return false;
            }

            elements.AddRange(tailList.Elements);
        }

        value = new ComptimeSequenceValue(ComptimeSequenceKind.List, elements);
        reason = "";
        return true;
    }

    private static bool TryEvaluateUnary(
        UnaryExpr unary,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryEvaluate(unary.Operand, context, out var operand, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        switch (unary.Operator)
        {
            case UnaryOp.Negate when TryGetInteger(operand, out var intValue):
                value = new ComptimeIntegerValue(-intValue);
                return Succeed(out reason);

            case UnaryOp.Negate when TryGetFloat(operand, out var floatValue):
                value = new ComptimeFloatValue(-floatValue);
                return Succeed(out reason);

            case UnaryOp.Not when operand is ComptimeBoolValue { Value: var boolValue }:
                value = new ComptimeBoolValue(!boolValue);
                return Succeed(out reason);

            default:
                value = ComptimeUnitValue.Instance;
                reason = $"unary operator '{unary.Operator.ToSymbol()}' is not supported for comptime operand";
                return false;
        }
    }

    private static bool TryEvaluateBinary(
        BinaryExpr binary,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryEvaluate(binary.Left, context, out var left, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        if (binary.Operator is BinaryOp.And or BinaryOp.Or)
        {
            if (left is not ComptimeBoolValue { Value: var leftBool })
            {
                value = ComptimeUnitValue.Instance;
                reason = $"logical operator '{binary.Operator.ToSymbol()}' requires Bool operands";
                return false;
            }

            if (binary.Operator == BinaryOp.And && !leftBool)
            {
                value = new ComptimeBoolValue(false);
                reason = string.Empty;
                return true;
            }

            if (binary.Operator == BinaryOp.Or && leftBool)
            {
                value = new ComptimeBoolValue(true);
                reason = string.Empty;
                return true;
            }
        }

        if (!TryEvaluate(binary.Right, context, out var right, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        if (TryGetInteger(left, out _) && TryGetInteger(right, out _))
        {
            return TryEvaluateIntegerBinary(binary.Operator, left, right, out value, out reason);
        }

        if (TryGetFloat(left, out _) && TryGetFloat(right, out _))
        {
            return TryEvaluateFloatBinary(binary.Operator, left, right, out value, out reason);
        }

        if (left is ComptimeBoolValue && right is ComptimeBoolValue)
        {
            return TryEvaluateBoolBinary(binary.Operator, left, right, out value, out reason);
        }

        if (left is ComptimeStringValue && right is ComptimeStringValue)
        {
            return TryEvaluateStringBinary(binary.Operator, left, right, out value, out reason);
        }

        if (binary.Operator is BinaryOp.Equal or BinaryOp.NotEqual)
        {
            var equals = ValuesEqual(left, right);
            value = new ComptimeBoolValue(binary.Operator == BinaryOp.Equal ? equals : !equals);
            reason = "";
            return true;
        }

        value = ComptimeUnitValue.Instance;
        reason = $"binary operator '{binary.Operator.ToSymbol()}' is not supported for comptime operands";
        return false;
    }

    private static bool TryEvaluateIntegerBinary(
        BinaryOp op,
        ComptimeValue left,
        ComptimeValue right,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = "";
        if (!TryGetInteger(left, out var a) || !TryGetInteger(right, out var b))
        {
            return false;
        }

        switch (op)
        {
            case BinaryOp.Add:
                value = new ComptimeIntegerValue(a + b);
                return true;
            case BinaryOp.Subtract:
                value = new ComptimeIntegerValue(a - b);
                return true;
            case BinaryOp.Multiply:
                value = new ComptimeIntegerValue(a * b);
                return true;
            case BinaryOp.Divide:
                if (b == 0)
                {
                    reason = "integer division by zero";
                    return false;
                }

                value = new ComptimeIntegerValue(a / b);
                return true;
            case BinaryOp.Modulo:
                if (b == 0)
                {
                    reason = "integer modulo by zero";
                    return false;
                }

                value = new ComptimeIntegerValue(a % b);
                return true;
            case BinaryOp.Less:
                value = new ComptimeBoolValue(a < b);
                return true;
            case BinaryOp.Greater:
                value = new ComptimeBoolValue(a > b);
                return true;
            case BinaryOp.LessEqual:
                value = new ComptimeBoolValue(a <= b);
                return true;
            case BinaryOp.GreaterEqual:
                value = new ComptimeBoolValue(a >= b);
                return true;
            case BinaryOp.Equal:
                value = new ComptimeBoolValue(a == b);
                return true;
            case BinaryOp.NotEqual:
                value = new ComptimeBoolValue(a != b);
                return true;
            default:
                reason = $"binary operator '{op.ToSymbol()}' is not supported for comptime integer operands";
                return false;
        }
    }

    private static bool TryEvaluateFloatBinary(
        BinaryOp op,
        ComptimeValue left,
        ComptimeValue right,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = "";
        if (!TryGetFloat(left, out var a) || !TryGetFloat(right, out var b))
        {
            return false;
        }

        switch (op)
        {
            case BinaryOp.Add:
                value = new ComptimeFloatValue(a + b);
                return true;
            case BinaryOp.Subtract:
                value = new ComptimeFloatValue(a - b);
                return true;
            case BinaryOp.Multiply:
                value = new ComptimeFloatValue(a * b);
                return true;
            case BinaryOp.Divide:
                if (b == 0)
                {
                    reason = "float division by zero";
                    return false;
                }

                value = new ComptimeFloatValue(a / b);
                return true;
            case BinaryOp.Less:
                value = new ComptimeBoolValue(a < b);
                return true;
            case BinaryOp.Greater:
                value = new ComptimeBoolValue(a > b);
                return true;
            case BinaryOp.LessEqual:
                value = new ComptimeBoolValue(a <= b);
                return true;
            case BinaryOp.GreaterEqual:
                value = new ComptimeBoolValue(a >= b);
                return true;
            case BinaryOp.Equal:
                value = new ComptimeBoolValue(a.Equals(b));
                return true;
            case BinaryOp.NotEqual:
                value = new ComptimeBoolValue(!a.Equals(b));
                return true;
            default:
                reason = $"binary operator '{op.ToSymbol()}' is not supported for comptime float operands";
                return false;
        }
    }

    private static bool TryEvaluateBoolBinary(
        BinaryOp op,
        ComptimeValue left,
        ComptimeValue right,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = "";
        if (left is not ComptimeBoolValue { Value: var a } ||
            right is not ComptimeBoolValue { Value: var b })
        {
            return false;
        }

        switch (op)
        {
            case BinaryOp.And:
                value = new ComptimeBoolValue(a && b);
                return true;
            case BinaryOp.Or:
                value = new ComptimeBoolValue(a || b);
                return true;
            case BinaryOp.Equal:
                value = new ComptimeBoolValue(a == b);
                return true;
            case BinaryOp.NotEqual:
                value = new ComptimeBoolValue(a != b);
                return true;
            default:
                reason = $"binary operator '{op.ToSymbol()}' is not supported for comptime bool operands";
                return false;
        }
    }

    private static bool TryEvaluateStringBinary(
        BinaryOp op,
        ComptimeValue left,
        ComptimeValue right,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = "";
        if (left is not ComptimeStringValue { Value: var a } ||
            right is not ComptimeStringValue { Value: var b })
        {
            return false;
        }

        switch (op)
        {
            case BinaryOp.Concat:
            case BinaryOp.Add:
                value = new ComptimeStringValue(a + b);
                return true;
            case BinaryOp.Equal:
                value = new ComptimeBoolValue(string.Equals(a, b, StringComparison.Ordinal));
                return true;
            case BinaryOp.NotEqual:
                value = new ComptimeBoolValue(!string.Equals(a, b, StringComparison.Ordinal));
                return true;
            default:
                reason = $"binary operator '{op.ToSymbol()}' is not supported for comptime string operands";
                return false;
        }
    }

    private static bool TryGetInteger(ComptimeValue value, out long result)
    {
        if (value is ComptimeIntegerValue integer)
        {
            result = integer.Value;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryGetFloat(ComptimeValue value, out double result)
    {
        if (value is ComptimeFloatValue floating)
        {
            result = floating.Value;
            return true;
        }

        result = 0;
        return false;
    }

    private static bool Succeed(out string reason)
    {
        reason = "";
        return true;
    }

    private static bool ValuesEqual(ComptimeValue left, ComptimeValue right)
    {
        if (TryGetInteger(left, out var leftInt) && TryGetInteger(right, out var rightInt))
        {
            return leftInt == rightInt;
        }

        if (TryGetFloat(left, out var leftFloat) && TryGetFloat(right, out var rightFloat))
        {
            return leftFloat.Equals(rightFloat);
        }

        return left.StructuralEquals(right);
    }
}
