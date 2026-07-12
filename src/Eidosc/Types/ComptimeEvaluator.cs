using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Utils;

namespace Eidosc.Types;

internal sealed record ComptimeValue(object? Value);

internal enum ComptimeSequenceKind
{
    Tuple,
    List
}

internal sealed record ComptimeSequence(ComptimeSequenceKind Kind, object?[] Elements);

internal sealed record ComptimeEvaluationContext(
    IReadOnlyDictionary<SymbolId, ComptimeValue> Values,
    IReadOnlyDictionary<SymbolId, FuncDef> Functions,
    int CallDepth = 0);

internal static class ComptimeEvaluator
{
    private const int MaxCallDepth = 32;

    public static bool TryEvaluate(
        EidosAstNode? node,
        IReadOnlyDictionary<SymbolId, ComptimeValue> values,
        out ComptimeValue value,
        out string reason)
    {
        return TryEvaluate(node, values, new Dictionary<SymbolId, FuncDef>(), out value, out reason);
    }

    public static bool TryEvaluate(
        EidosAstNode? node,
        IReadOnlyDictionary<SymbolId, ComptimeValue> values,
        IReadOnlyDictionary<SymbolId, FuncDef> functions,
        out ComptimeValue value,
        out string reason)
    {
        return TryEvaluate(node, new ComptimeEvaluationContext(values, functions), out value, out reason);
    }

    private static bool TryEvaluate(
        EidosAstNode? node,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = new ComptimeValue(null);
        reason = "";

        switch (node)
        {
            case null:
                reason = "missing expression";
                return false;

            case LiteralExpr { IsRecoveredError: false } literal:
                value = new ComptimeValue(literal.Value);
                return true;

            case IdentifierExpr identifier:
                return TryEvaluateSymbolReference(identifier.SymbolId, identifier.Name, context.Values, out value, out reason);

            case PathExpr path:
                return TryEvaluateSymbolReference(path.SymbolId, string.Join("::", path.Path), context.Values, out value, out reason);

            case TupleExpr tuple:
                return TryEvaluateTuple(tuple, context, out value, out reason);

            case ListExpr list:
                return TryEvaluateList(list, context, out value, out reason);

            case UnaryExpr unary:
                return TryEvaluateUnary(unary, context, out value, out reason);

            case BinaryExpr binary:
                return TryEvaluateBinary(binary, context, out value, out reason);

            case IfExpr ifExpr:
                return TryEvaluateIf(ifExpr, context, out value, out reason);

            case MatchExpr match:
                return TryEvaluateMatch(match, context, out value, out reason);

            case BlockExpr block:
                return TryEvaluateBlock(block, context, out value, out reason);

            case CallExpr call:
                return TryEvaluateCall(call, context, out value, out reason);

            default:
                reason = $"{node.GetType().Name} is not supported by the phase-1 comptime evaluator";
                return false;
        }
    }

    private static bool TryEvaluateSymbolReference(
        SymbolId symbolId,
        string displayName,
        IReadOnlyDictionary<SymbolId, ComptimeValue> values,
        out ComptimeValue value,
        out string reason)
    {
        if (!symbolId.IsValid)
        {
            value = new ComptimeValue(null);
            reason = $"identifier '{displayName}' was not resolved";
            return false;
        }

        if (values.TryGetValue(symbolId, out var storedValue))
        {
            value = storedValue;
            reason = "";
            return true;
        }

        value = new ComptimeValue(null);
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
            value = new ComptimeValue(null);
            return false;
        }

        if (condition.Value is not bool conditionValue)
        {
            value = new ComptimeValue(null);
            reason = "if condition must evaluate to a comptime bool";
            return false;
        }

        var selectedBranch = conditionValue ? ifExpr.ThenBranch : ifExpr.ElseBranch;
        if (selectedBranch == null)
        {
            value = new ComptimeValue(null);
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
            value = new ComptimeValue(null);
            reason = "match expression is missing a matched expression";
            return false;
        }

        if (!TryEvaluate(match.MatchedExpression, context, out var matchedValue, out reason))
        {
            value = new ComptimeValue(null);
            return false;
        }

        foreach (var branch in match.Branches)
        {
            if (!TryPatternMatches(branch.Pattern, matchedValue.Value, out var matches, out var bindings, out reason))
            {
                value = new ComptimeValue(null);
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
                value = new ComptimeValue(null);
                return false;
            }

            branchContext = guardedContext;
            if (branch.Guard != null && !guardMatches)
            {
                continue;
            }

            if (branch.Expression == null)
            {
                value = new ComptimeValue(null);
                reason = "matching comptime branch is missing a result expression";
                return false;
            }

            return TryEvaluate(branch.Expression, branchContext, out value, out reason);
        }

        value = new ComptimeValue(null);
        reason = "comptime match expression has no matching branch";
        return false;
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

                if (!TryPatternMatches(patternGuard.Pattern, sourceValue.Value, out matches, out var guardBindings, out reason))
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

                if (guardValue.Value is not bool boolValue)
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
        object? value,
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
                matches = ValuesEqual(value, literal.Value);
                return true;

            case VarPattern varPattern:
                if (varPattern.BindingMode != PatternBindingMode.ByValue)
                {
                    matches = false;
                    reason = "borrow binding patterns are not supported by the phase-1 comptime match evaluator";
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
                    [varPattern.SymbolId] = new ComptimeValue(value)
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
                    !TryGetInteger(rangePattern.Start.Value, out var start) ||
                    !TryGetInteger(rangePattern.End.Value, out var end))
                {
                    matches = false;
                    reason = "only integer range patterns are supported by the phase-1 comptime evaluator";
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

            default:
                matches = false;
                reason = $"{normalizedPattern.GetType().Name} is not supported by the phase-1 comptime match evaluator";
                return false;
        }
    }

    private static bool TrySequencePatternMatches(
        IReadOnlyList<Pattern> elementPatterns,
        bool hasRestMarker,
        Pattern? restPattern,
        IReadOnlyList<Pattern> suffixPatterns,
        object? value,
        string sequenceKind,
        out bool matches,
        out IReadOnlyDictionary<SymbolId, ComptimeValue> bindings,
        out string reason)
    {
        var sequenceBindings = new Dictionary<SymbolId, ComptimeValue>();
        bindings = sequenceBindings;

        if (value is not ComptimeSequence sequence ||
            !IsExpectedSequenceKind(sequence.Kind, sequenceKind))
        {
            matches = false;
            reason = "";
            return true;
        }

        var elements = sequence.Elements;
        var minimumLength = elementPatterns.Count + suffixPatterns.Count;
        if (!hasRestMarker && elements.Length != minimumLength)
        {
            matches = false;
            reason = "";
            return true;
        }

        if (hasRestMarker && elements.Length < minimumLength)
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
            var sourceIndex = elements.Length - suffixPatterns.Count + i;
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
                elements[elementPatterns.Count..(elements.Length - suffixPatterns.Count)],
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

    private static bool TryBindRestPattern(
        Pattern? restPattern,
        ComptimeSequenceKind sequenceKind,
        object?[] restElements,
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
                bindings[varPattern.SymbolId] = new ComptimeValue(new ComptimeSequence(sequenceKind, restElements));
                return true;

            case VarPattern { BindingMode: not PatternBindingMode.ByValue }:
                reason = "borrow list rest binding patterns are not supported by the phase-1 comptime match evaluator";
                return false;

            case VarPattern varPattern:
                reason = $"list rest binding pattern '{varPattern.Name}' was not resolved";
                return false;

            default:
                reason = "only wildcard and by-value binding rest patterns are supported by the phase-1 comptime match evaluator";
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
        if (block.ResultExpression == null)
        {
            value = new ComptimeValue(null);
            reason = "comptime block must have a result expression";
            return false;
        }

        if (block.Statements.Any(statement => !ReferenceEquals(statement, block.ResultExpression)))
        {
            value = new ComptimeValue(null);
            reason = "comptime block statements are not supported by the phase-1 comptime evaluator";
            return false;
        }

        return TryEvaluate(block.ResultExpression, context, out value, out reason);
    }

    private static bool TryEvaluateCall(
        CallExpr call,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = new ComptimeValue(null);
        if (call.NamedArgs.Count > 0)
        {
            reason = "named arguments are not supported by the phase-1 comptime call evaluator";
            return false;
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

        if (!context.Functions.TryGetValue(calleeSymbolId, out var function) ||
            !function.IsComptime)
        {
            reason = $"call target '{displayName}' is not a comptime-only function";
            return false;
        }

        if (FunctionRequiresRuntimeAbilities(function))
        {
            reason = $"comptime-only function '{function.Name}' requires runtime abilities";
            return false;
        }

        var argValues = new List<ComptimeValue>(call.PositionalArgs.Count);
        foreach (var arg in call.PositionalArgs)
        {
            if (!TryEvaluate(arg, context, out var argValue, out reason))
            {
                return false;
            }

            argValues.Add(argValue);
        }

        if (function.Body.Count == 0)
        {
            reason = $"comptime function '{function.Name}' has no body";
            return false;
        }

        return TryEvaluateFunctionBranches(function, argValues, context, out value, out reason);
    }

    private static bool TryEvaluateFunctionBranches(
        FuncDef function,
        IReadOnlyList<ComptimeValue> argValues,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (TryEvaluateFunctionBranchesWithFirstArgument(
                function,
                argValues.Count == 0 ? [new ComptimeValue("()")] : argValues,
                context with { CallDepth = context.CallDepth + 1 },
                out value,
                out reason))
        {
            return true;
        }

        if (argValues.Count <= 1 ||
            !reason.Contains("no matching branch", StringComparison.Ordinal))
        {
            return false;
        }

        var tupleArg = new ComptimeValue(new ComptimeSequence(
            ComptimeSequenceKind.Tuple,
            argValues.Select(static arg => arg.Value).ToArray()));
        return TryEvaluateFunctionBranchesWithFirstArgument(
            function,
            [tupleArg],
            context with { CallDepth = context.CallDepth + 1 },
            out value,
            out reason);
    }

    private static bool TryEvaluateFunctionBranchesWithFirstArgument(
        FuncDef function,
        IReadOnlyList<ComptimeValue> argValues,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = new ComptimeValue(null);
        reason = "";
        var effectiveArg = argValues[0];
        foreach (var branch in function.Body)
        {
            if (!TryPatternMatches(branch.Pattern, effectiveArg.Value, out var matches, out var bindings, out reason))
            {
                return false;
            }

            if (!matches)
            {
                continue;
            }

            var callContext = context with
            {
                Values = MergeValues(context.Values, bindings)
            };

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
            return TryEvaluate(expression, context, out value, out reason);
        }

        if (expression is not LambdaExpr lambda)
        {
            value = new ComptimeValue(null);
            reason = $"{expression.GetType().Name} cannot accept remaining comptime call arguments";
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
        value = new ComptimeValue(null);
        reason = "";
        if (lambda.Parameters.Count == 0)
        {
            reason = "comptime lambda has no parameters for remaining call arguments";
            return false;
        }

        if (argValues.Count < lambda.Parameters.Count)
        {
            reason = "partial comptime function application is not supported by the phase-1 comptime call evaluator";
            return false;
        }

        var lambdaValues = context.Values;
        for (var i = 0; i < lambda.Parameters.Count; i++)
        {
            if (!TryPatternMatches(lambda.Parameters[i], argValues[i].Value, out var matches, out var bindings, out reason))
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
        var elements = new List<object?>(tuple.Elements.Count);
        foreach (var element in tuple.Elements)
        {
            if (!TryEvaluate(element, context, out var elementValue, out reason))
            {
                value = new ComptimeValue(null);
                return false;
            }

            elements.Add(elementValue.Value);
        }

        value = new ComptimeValue(new ComptimeSequence(ComptimeSequenceKind.Tuple, elements.ToArray()));
        reason = "";
        return true;
    }

    private static bool TryEvaluateList(
        ListExpr list,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (list.HasRest)
        {
            value = new ComptimeValue(null);
            reason = "list spread is not supported by the phase-1 comptime evaluator";
            return false;
        }

        var elements = new List<object?>(list.Elements.Count);
        foreach (var element in list.Elements)
        {
            if (!TryEvaluate(element, context, out var elementValue, out reason))
            {
                value = new ComptimeValue(null);
                return false;
            }

            elements.Add(elementValue.Value);
        }

        value = new ComptimeValue(new ComptimeSequence(ComptimeSequenceKind.List, elements.ToArray()));
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
            value = new ComptimeValue(null);
            return false;
        }

        switch (unary.Operator)
        {
            case UnaryOp.Negate when TryGetInteger(operand.Value, out var intValue):
                value = new ComptimeValue(-intValue);
                return Succeed(out reason);

            case UnaryOp.Negate when TryGetFloat(operand.Value, out var floatValue):
                value = new ComptimeValue(-floatValue);
                return Succeed(out reason);

            case UnaryOp.Not when operand.Value is bool boolValue:
                value = new ComptimeValue(!boolValue);
                return Succeed(out reason);

            default:
                value = new ComptimeValue(null);
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
        if (!TryEvaluate(binary.Left, context, out var left, out reason) ||
            !TryEvaluate(binary.Right, context, out var right, out reason))
        {
            value = new ComptimeValue(null);
            return false;
        }

        if (TryGetInteger(left.Value, out _) && TryGetInteger(right.Value, out _))
        {
            return TryEvaluateIntegerBinary(binary.Operator, left.Value, right.Value, out value, out reason);
        }

        if (TryGetFloat(left.Value, out _) && TryGetFloat(right.Value, out _))
        {
            return TryEvaluateFloatBinary(binary.Operator, left.Value, right.Value, out value, out reason);
        }

        if (left.Value is bool && right.Value is bool)
        {
            return TryEvaluateBoolBinary(binary.Operator, left.Value, right.Value, out value, out reason);
        }

        if (left.Value is string && right.Value is string)
        {
            return TryEvaluateStringBinary(binary.Operator, left.Value, right.Value, out value, out reason);
        }

        value = new ComptimeValue(null);
        reason = $"binary operator '{binary.Operator.ToSymbol()}' is not supported for comptime operands";
        return false;
    }

    private static bool TryEvaluateIntegerBinary(
        BinaryOp op,
        object? left,
        object? right,
        out ComptimeValue value,
        out string reason)
    {
        value = new ComptimeValue(null);
        reason = "";
        if (!TryGetInteger(left, out var a) || !TryGetInteger(right, out var b))
        {
            return false;
        }

        switch (op)
        {
            case BinaryOp.Add:
                value = new ComptimeValue(a + b);
                return true;
            case BinaryOp.Subtract:
                value = new ComptimeValue(a - b);
                return true;
            case BinaryOp.Multiply:
                value = new ComptimeValue(a * b);
                return true;
            case BinaryOp.Divide:
                if (b == 0)
                {
                    reason = "integer division by zero";
                    return false;
                }

                value = new ComptimeValue(a / b);
                return true;
            case BinaryOp.Modulo:
                if (b == 0)
                {
                    reason = "integer modulo by zero";
                    return false;
                }

                value = new ComptimeValue(a % b);
                return true;
            case BinaryOp.Less:
                value = new ComptimeValue(a < b);
                return true;
            case BinaryOp.Greater:
                value = new ComptimeValue(a > b);
                return true;
            case BinaryOp.LessEqual:
                value = new ComptimeValue(a <= b);
                return true;
            case BinaryOp.GreaterEqual:
                value = new ComptimeValue(a >= b);
                return true;
            case BinaryOp.Equal:
                value = new ComptimeValue(a == b);
                return true;
            case BinaryOp.NotEqual:
                value = new ComptimeValue(a != b);
                return true;
            default:
                reason = $"binary operator '{op.ToSymbol()}' is not supported for comptime integer operands";
                return false;
        }
    }

    private static bool TryEvaluateFloatBinary(
        BinaryOp op,
        object? left,
        object? right,
        out ComptimeValue value,
        out string reason)
    {
        value = new ComptimeValue(null);
        reason = "";
        if (!TryGetFloat(left, out var a) || !TryGetFloat(right, out var b))
        {
            return false;
        }

        switch (op)
        {
            case BinaryOp.Add:
                value = new ComptimeValue(a + b);
                return true;
            case BinaryOp.Subtract:
                value = new ComptimeValue(a - b);
                return true;
            case BinaryOp.Multiply:
                value = new ComptimeValue(a * b);
                return true;
            case BinaryOp.Divide:
                if (b == 0)
                {
                    reason = "float division by zero";
                    return false;
                }

                value = new ComptimeValue(a / b);
                return true;
            case BinaryOp.Less:
                value = new ComptimeValue(a < b);
                return true;
            case BinaryOp.Greater:
                value = new ComptimeValue(a > b);
                return true;
            case BinaryOp.LessEqual:
                value = new ComptimeValue(a <= b);
                return true;
            case BinaryOp.GreaterEqual:
                value = new ComptimeValue(a >= b);
                return true;
            case BinaryOp.Equal:
                value = new ComptimeValue(a.Equals(b));
                return true;
            case BinaryOp.NotEqual:
                value = new ComptimeValue(!a.Equals(b));
                return true;
            default:
                reason = $"binary operator '{op.ToSymbol()}' is not supported for comptime float operands";
                return false;
        }
    }

    private static bool TryEvaluateBoolBinary(
        BinaryOp op,
        object? left,
        object? right,
        out ComptimeValue value,
        out string reason)
    {
        value = new ComptimeValue(null);
        reason = "";
        if (left is not bool a || right is not bool b)
        {
            return false;
        }

        switch (op)
        {
            case BinaryOp.And:
                value = new ComptimeValue(a && b);
                return true;
            case BinaryOp.Or:
                value = new ComptimeValue(a || b);
                return true;
            case BinaryOp.Equal:
                value = new ComptimeValue(a == b);
                return true;
            case BinaryOp.NotEqual:
                value = new ComptimeValue(a != b);
                return true;
            default:
                reason = $"binary operator '{op.ToSymbol()}' is not supported for comptime bool operands";
                return false;
        }
    }

    private static bool TryEvaluateStringBinary(
        BinaryOp op,
        object? left,
        object? right,
        out ComptimeValue value,
        out string reason)
    {
        value = new ComptimeValue(null);
        reason = "";
        if (left is not string a || right is not string b)
        {
            return false;
        }

        switch (op)
        {
            case BinaryOp.Concat:
            case BinaryOp.Add:
                value = new ComptimeValue(a + b);
                return true;
            case BinaryOp.Equal:
                value = new ComptimeValue(string.Equals(a, b, StringComparison.Ordinal));
                return true;
            case BinaryOp.NotEqual:
                value = new ComptimeValue(!string.Equals(a, b, StringComparison.Ordinal));
                return true;
            default:
                reason = $"binary operator '{op.ToSymbol()}' is not supported for comptime string operands";
                return false;
        }
    }

    private static bool TryGetInteger(object? value, out long result)
    {
        switch (value)
        {
            case byte v:
                result = v;
                return true;
            case short v:
                result = v;
                return true;
            case int v:
                result = v;
                return true;
            case long v:
                result = v;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool TryGetFloat(object? value, out double result)
    {
        switch (value)
        {
            case float v:
                result = v;
                return true;
            case double v:
                result = v;
                return true;
            default:
                result = 0;
                return false;
        }
    }

    private static bool Succeed(out string reason)
    {
        reason = "";
        return true;
    }

    private static bool ValuesEqual(object? left, object? right)
    {
        if (TryGetInteger(left, out var leftInt) && TryGetInteger(right, out var rightInt))
        {
            return leftInt == rightInt;
        }

        if (TryGetFloat(left, out var leftFloat) && TryGetFloat(right, out var rightFloat))
        {
            return leftFloat.Equals(rightFloat);
        }

        return Equals(left, right);
    }
}
