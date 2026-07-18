using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;

namespace Eidosc.Types;

internal enum ComptimeControlKind
{
    Return,
    Break,
    Continue
}

internal sealed record ComptimeControlValue(
    ComptimeControlKind Kind,
    ComptimeValue Value) : ComptimeValue
{
    protected override string UntypedCanonicalText =>
        $"control:{Kind.ToString().ToLowerInvariant()}:{Value.CanonicalText}";
}

internal sealed record ComptimeLambdaValue(
    LambdaExpr Lambda,
    IReadOnlyDictionary<SymbolId, ComptimeValue> Captures,
    IReadOnlyList<ComptimeValue> BoundArguments) : ComptimeValue
{
    protected override string UntypedCanonicalText =>
        $"lambda:{Lambda.Span.Position}:{Lambda.Span.Length}:" +
        $"captures[{string.Join(";", Captures
            .OrderBy(static capture => capture.Key.Value)
            .Select(static capture => $"{capture.Key.Value}={capture.Value.CanonicalText}"))}]:" +
        $"bound[{string.Join(";", BoundArguments.Select(static argument => argument.CanonicalText))}]";
}

internal sealed record ComptimeFunctionValue(
    FuncDef Function,
    IReadOnlyDictionary<SymbolId, ComptimeValue> Captures,
    IReadOnlyList<ComptimeValue> BoundArguments) : ComptimeValue
{
    protected override string UntypedCanonicalText =>
        $"function:{ComptimeValue.EncodeText(Function.Name)}:{Function.Span.Position}:{Function.Span.Length}:" +
        $"captures[{string.Join(";", Captures
            .OrderBy(static capture => capture.Key.Value)
            .Select(static capture => $"{capture.Key.Value}={capture.Value.CanonicalText}"))}]:" +
        $"bound[{string.Join(";", BoundArguments.Select(static argument => argument.CanonicalText))}]";
}

internal sealed class ComptimeEvaluationFrame(ComptimeEvaluationFrame? parent = null)
{
    private readonly Dictionary<SymbolId, ComptimeValue> _values = [];

    public bool TryGet(SymbolId symbolId, out ComptimeValue value)
    {
        if (_values.TryGetValue(symbolId, out value!))
        {
            return true;
        }

        if (parent != null)
        {
            return parent.TryGet(symbolId, out value);
        }

        value = ComptimeUnitValue.Instance;
        return false;
    }

    public void Define(SymbolId symbolId, ComptimeValue value)
    {
        if (symbolId.IsValid)
        {
            _values[symbolId] = value;
        }
    }

    public void DefineRange(IReadOnlyDictionary<SymbolId, ComptimeValue> values)
    {
        foreach (var (symbolId, value) in values)
        {
            Define(symbolId, value);
        }
    }

    public bool Assign(SymbolId symbolId, ComptimeValue value)
    {
        if (_values.ContainsKey(symbolId))
        {
            _values[symbolId] = value;
            return true;
        }

        return parent?.Assign(symbolId, value) == true;
    }

    public IReadOnlyDictionary<SymbolId, ComptimeValue> Snapshot()
    {
        var snapshot = parent == null
            ? new Dictionary<SymbolId, ComptimeValue>()
            : new Dictionary<SymbolId, ComptimeValue>(parent.Snapshot());
        foreach (var (symbolId, value) in _values)
        {
            snapshot[symbolId] = value;
        }

        return snapshot;
    }
}

internal static partial class ComptimeEvaluator
{
    private static bool TryEvaluateControlFlowBlock(
        BlockExpr block,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        var blockContext = context with { Frame = new ComptimeEvaluationFrame(context.Frame) };
        foreach (var statement in block.Statements)
        {
            if (ReferenceEquals(statement, block.ResultExpression))
            {
                continue;
            }

            if (statement is LetDecl binding)
            {
                if (!TryEvaluateLocalBinding(binding, blockContext, out reason))
                {
                    value = ComptimeUnitValue.Instance;
                    return false;
                }
                continue;
            }

            if (statement is LetQuestionDecl propagatingBinding)
            {
                if (!TryEvaluatePropagatingBinding(propagatingBinding, blockContext, out var propagation, out reason))
                {
                    value = ComptimeUnitValue.Instance;
                    return false;
                }

                if (propagation != null)
                {
                    value = propagation;
                    return true;
                }
                continue;
            }

            if (!TryEvaluate(statement, blockContext, out var statementValue, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            if (statementValue is ComptimeControlValue)
            {
                value = statementValue;
                return true;
            }
        }

        if (block.ResultExpression == null)
        {
            value = ComptimeUnitValue.Instance;
            reason = string.Empty;
            return true;
        }

        return TryEvaluate(block.ResultExpression, blockContext, out value, out reason);
    }

    private static bool TryEvaluateLocalBinding(
        LetDecl binding,
        ComptimeEvaluationContext context,
        out string reason)
    {
        if (binding.Value == null || binding.Pattern == null)
        {
            reason = "comptime local binding must have a pattern and initializer";
            return false;
        }

        if (!TryEvaluate(binding.Value, context, out var bindingValue, out reason) ||
            !TryPatternMatches(binding.Pattern, bindingValue, out var matches, out var bindings, out reason))
        {
            return false;
        }

        if (!matches)
        {
            reason = "comptime local binding pattern did not match its initializer";
            return false;
        }

        context.Frame!.DefineRange(bindings);
        return true;
    }

    private static bool TryEvaluatePropagatingBinding(
        LetQuestionDecl binding,
        ComptimeEvaluationContext context,
        out ComptimeControlValue? propagation,
        out string reason)
    {
        propagation = null;
        if (binding.Value == null || binding.Pattern == null)
        {
            reason = "comptime let? binding must have a pattern and initializer";
            return false;
        }

        if (!TryEvaluate(binding.Value, context, out var source, out reason))
        {
            return false;
        }

        if (source is not ComptimeAdtValue sum)
        {
            reason = "comptime let? initializer must evaluate to Option or Result";
            return false;
        }

        if (sum.HasSameConstructor(binding.FailureConstructorSymbolId, GetFailureConstructorName(binding)))
        {
            propagation = new ComptimeControlValue(ComptimeControlKind.Return, sum);
            reason = string.Empty;
            return true;
        }

        if (!sum.HasSameConstructor(binding.SuccessConstructorSymbolId, GetSuccessConstructorName(binding)) ||
            sum.PositionalValues.Count != 1)
        {
            reason = "comptime let? initializer has an invalid Option or Result representation";
            return false;
        }

        if (!TryPatternMatches(binding.Pattern, sum.PositionalValues[0], out var matches, out var bindings, out reason))
        {
            return false;
        }

        if (!matches)
        {
            reason = "comptime let? binding pattern did not match the success payload";
            return false;
        }

        context.Frame!.DefineRange(bindings);
        reason = string.Empty;
        return true;
    }

    private static string GetSuccessConstructorName(LetQuestionDecl binding) =>
        binding.BindingKind == LetQuestionBindingKind.Result ? "Ok" : "Some";

    private static string GetFailureConstructorName(LetQuestionDecl binding) =>
        binding.BindingKind == LetQuestionBindingKind.Result ? "Err" : "None";

    private static bool TryEvaluateAssignment(
        Assignment assignment,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        if (assignment.Value == null || !assignment.TargetSymbolId.IsValid)
        {
            reason = "comptime assignment requires a resolved local target and value";
            return false;
        }

        if (!TryEvaluate(assignment.Value, context, out var assignedValue, out reason))
        {
            return false;
        }

        if (context.Frame?.Assign(assignment.TargetSymbolId, assignedValue) != true)
        {
            reason = $"comptime assignment target '{assignment.Target}' is not a local binding in the active frame";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryCreateControlSignal(
        ComptimeControlKind kind,
        EidosAstNode? expression,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (expression == null)
        {
            value = new ComptimeControlValue(kind, ComptimeUnitValue.Instance);
            reason = string.Empty;
            return true;
        }

        if (!TryEvaluate(expression, context, out var result, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = new ComptimeControlValue(kind, result);
        return true;
    }

    private static bool TryEvaluateLoop(
        LoopExpr loop,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (loop.Body == null)
        {
            return FailControl("comptime loop is missing its body", out value, out reason);
        }

        while (true)
        {
            if (!TryEvaluate(loop.Body, context, out var iteration, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            switch (iteration)
            {
                case ComptimeControlValue { Kind: ComptimeControlKind.Break } broken:
                    value = broken.Value;
                    return true;
                case ComptimeControlValue { Kind: ComptimeControlKind.Continue }:
                    continue;
                case ComptimeControlValue { Kind: ComptimeControlKind.Return }:
                    value = iteration;
                    return true;
            }
        }
    }

    private static bool TryEvaluateWhileLet(
        WhileLetExpr whileLet,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        if (whileLet.Pattern == null || whileLet.MatchedExpression == null || whileLet.Body == null)
        {
            return FailControl("comptime while-let is missing its pattern, subject, or body", out value, out reason);
        }

        while (true)
        {
            if (!TryEvaluate(whileLet.MatchedExpression, context, out var subject, out reason) ||
                !TryPatternMatches(whileLet.Pattern, subject, out var matches, out var bindings, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            if (!matches)
            {
                value = ComptimeUnitValue.Instance;
                reason = string.Empty;
                return true;
            }

            var iterationContext = context with { Frame = new ComptimeEvaluationFrame(context.Frame) };
            iterationContext.Frame.DefineRange(bindings);
            if (!TryEvaluate(whileLet.Body, iterationContext, out var iteration, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            switch (iteration)
            {
                case ComptimeControlValue { Kind: ComptimeControlKind.Break } broken:
                    value = broken.Value;
                    return true;
                case ComptimeControlValue { Kind: ComptimeControlKind.Continue }:
                    continue;
                case ComptimeControlValue { Kind: ComptimeControlKind.Return }:
                    value = iteration;
                    return true;
            }
        }
    }

    private static bool FailControl(string message, out ComptimeValue value, out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = message;
        return false;
    }
}
