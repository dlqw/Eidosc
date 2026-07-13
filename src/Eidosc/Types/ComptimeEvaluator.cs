using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Utils;

namespace Eidosc.Types;

internal sealed record ComptimeEvaluationContext(
    IReadOnlyDictionary<SymbolId, ComptimeValue> Values,
    IReadOnlyDictionary<SymbolId, FuncDef> Functions,
    Func<Type, Type>? ResolveType = null,
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
                StaticType = context.ResolveType?.Invoke(inferredType) ?? inferredType
            };
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

        switch (node)
        {
            case null:
                reason = "missing expression";
                return false;

            case LiteralExpr { IsRecoveredError: false } literal:
                if (ComptimeValue.TryFromLiteral(literal.Value, out value))
                {
                    return true;
                }

                reason = $"literal value of type '{literal.Value?.GetType().Name ?? "Unit"}' is not supported by the comptime evaluator";
                return false;

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

            case CtorExpr constructor:
                return TryEvaluateConstructor(constructor, context, out value, out reason);

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
            value = ComptimeUnitValue.Instance;
            reason = $"identifier '{displayName}' was not resolved";
            return false;
        }

        if (values.TryGetValue(symbolId, out var storedValue))
        {
            value = storedValue;
            reason = "";
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
                    reason = $"literal pattern value of type '{literal.Value?.GetType().Name ?? "Unit"}' is not supported by the comptime evaluator";
                    return false;
                }

                matches = ValuesEqual(value, literalValue);
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

            case CtorPattern constructorPattern:
                return TryConstructorPatternMatches(
                    constructorPattern,
                    value,
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
            value = ComptimeUnitValue.Instance;
            reason = "comptime block must have a result expression";
            return false;
        }

        var blockContext = context;
        foreach (var statement in block.Statements)
        {
            if (ReferenceEquals(statement, block.ResultExpression))
            {
                continue;
            }

            if (statement is LetDecl binding)
            {
                if (binding.IsMutable)
                {
                    value = ComptimeUnitValue.Instance;
                    reason = "mutable local bindings are not supported by the comptime evaluator";
                    return false;
                }

                if (binding.Value == null || binding.Pattern == null)
                {
                    value = ComptimeUnitValue.Instance;
                    reason = "comptime local binding must have a pattern and initializer";
                    return false;
                }

                if (!TryEvaluate(binding.Value, blockContext, out var bindingValue, out reason))
                {
                    value = ComptimeUnitValue.Instance;
                    return false;
                }

                if (!TryPatternMatches(
                        binding.Pattern,
                        bindingValue,
                        out var matches,
                        out var bindings,
                        out reason))
                {
                    value = ComptimeUnitValue.Instance;
                    return false;
                }

                if (!matches)
                {
                    value = ComptimeUnitValue.Instance;
                    reason = "comptime local binding pattern did not match its initializer";
                    return false;
                }

                blockContext = blockContext with
                {
                    Values = MergeValues(blockContext.Values, bindings)
                };
                continue;
            }

            if (!TryEvaluate(statement, blockContext, out _, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }
        }

        return TryEvaluate(block.ResultExpression, blockContext, out value, out reason);
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

            if (baseValue is not ComptimeAdtValue baseAdt ||
                !baseAdt.HasSameConstructor(constructor.SymbolId, BuildConstructorDisplayName(constructor)))
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

        value = new ComptimeAdtValue(
            constructor.SymbolId,
            BuildConstructorDisplayName(constructor),
            positionalValues,
            namedValues);
        return true;
    }

    private static string BuildConstructorDisplayName(CtorExpr constructor)
    {
        var parts = constructor.ConstructorPath?.ToQualifiedPathParts() ?? [];
        return parts.Count > 0
            ? string.Join("::", parts)
            : constructor.ConstructorName;
    }

    private static bool TryEvaluateCall(
        CallExpr call,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
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
                argValues.Count == 0 ? [ComptimeUnitValue.Instance] : argValues,
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

        var tupleArg = new ComptimeSequenceValue(
            ComptimeSequenceKind.Tuple,
            argValues.ToArray());
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
            value = ComptimeUnitValue.Instance;
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
        value = ComptimeUnitValue.Instance;
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

    private static bool TryEvaluateList(
        ListExpr list,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (list.HasRest)
        {
            value = ComptimeUnitValue.Instance;
            reason = "list spread is not supported by the phase-1 comptime evaluator";
            return false;
        }

        var elements = new List<ComptimeValue>(list.Elements.Count);
        foreach (var element in list.Elements)
        {
            if (!TryEvaluate(element, context, out var elementValue, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            elements.Add(elementValue);
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
        if (!TryEvaluate(binary.Left, context, out var left, out reason) ||
            !TryEvaluate(binary.Right, context, out var right, out reason))
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
