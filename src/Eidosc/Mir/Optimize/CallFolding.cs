using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Compile-time evaluator for calls to module functions whose arguments are
/// all constants. The evaluator only understands scalar SSA shapes: constant
/// propagation, arithmetic folding, constant switches and nested pure calls.
/// Any other instruction (loads, stores, allocations, pattern injection, ...)
/// aborts the fold and leaves the call untouched. Recursion is bounded by a
/// depth limit and a shared evaluation budget, so terminating recurrences
/// such as fib(10) fold while unbounded ones safely give up.
/// </summary>
public sealed class CallFolding
{
    private const int MaxCallDepth = 64;
    private const long MaxStepsPerFold = 1_000_000;

    private readonly Dictionary<string, MirFunc> _functionsByKey;
    private readonly Dictionary<EvaluationKey, MemoizedEvaluation> _memoizedResults = [];

    public CallFolding(MirModule module)
    {
        _functionsByKey = module.Functions
            .GroupBy(static function => MirFunctionIdentity.GetStableKey(function), StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Tries to evaluate a call with constant arguments. Returns the folded
    /// constant, or null when the callee is missing, the body uses operations
    /// the evaluator cannot handle, or the depth/step budget is exhausted.
    /// </summary>
    public MirConstant? TryFold(MirFunctionRef function, IReadOnlyList<MirOperand> arguments)
    {
        var context = new EvaluationContext(MaxStepsPerFold);
        try
        {
            return TryFoldCore(function, arguments, depth: 0, context);
        }
        catch (BudgetExceededException)
        {
            return null;
        }
    }

    private MirConstant? TryFoldCore(
        MirFunctionRef function,
        IReadOnlyList<MirOperand> arguments,
        int depth,
        EvaluationContext context)
    {
        context.CountStep();
        if (depth > MaxCallDepth)
        {
            throw new BudgetExceededException();
        }

        var functionKey = MirFunctionIdentity.GetStableKey(function);
        if (!_functionsByKey.TryGetValue(functionKey, out var callee))
        {
            return null;
        }

        var parameters = callee.Locals.Where(static local => local.IsParameter).ToList();
        if (parameters.Count != arguments.Count)
        {
            return null;
        }

        var constantArguments = new MirConstant[arguments.Count];
        var environment = new Dictionary<LocalId, MirConstant>();
        for (var i = 0; i < parameters.Count; i++)
        {
            if (arguments[i] is MirConstant constant)
            {
                constantArguments[i] = constant;
                environment[parameters[i].Id] = constant;
            }
            else
            {
                return null;
            }
        }

        var evaluationKey = new EvaluationKey(functionKey, constantArguments);
        if (_memoizedResults.TryGetValue(evaluationKey, out var memoized))
        {
            context.CountSteps(memoized.StepCost);
            return memoized.Result;
        }

        if (!context.TryEnter(evaluationKey))
        {
            return null;
        }

        var stepsBeforeEvaluation = context.StepsConsumed;
        MirConstant? result;
        try
        {
            result = EvaluateFunction(callee, environment, depth, context);
        }
        finally
        {
            context.Leave(evaluationKey);
        }

        _memoizedResults[evaluationKey] = new MemoizedEvaluation(
            result,
            context.StepsConsumed - stepsBeforeEvaluation);
        return result;
    }

    private MirConstant? EvaluateFunction(
        MirFunc function,
        Dictionary<LocalId, MirConstant> initialEnvironment,
        int depth,
        EvaluationContext context)
    {
        if (depth > MaxCallDepth)
        {
            throw new BudgetExceededException();
        }

        var blocks = function.BasicBlocks.ToDictionary(static block => block.Id);
        var current = function.EntryBlockId;
        var environment = new Dictionary<LocalId, MirConstant>(initialEnvironment);

        while (current.IsValid && blocks.TryGetValue(current, out var block))
        {
            context.CountStep();
            foreach (var instruction in block.Instructions)
            {
                context.CountStep();
                if (!TryExecuteInstruction(instruction, environment, depth, context))
                {
                    return null;
                }
            }

            context.CountStep();
            switch (block.Terminator)
            {
                case MirReturn { Value: null }:
                    return new MirConstant
                    {
                        Value = new MirConstantValue.UnitValue(),
                        TypeId = new TypeId(BaseTypes.UnitId)
                    };
                case MirReturn { Value: { } value }:
                    return ResolveOperand(value, environment);
                case MirGoto gotoTerminator:
                    context.CountStep();
                    current = gotoTerminator.Target;
                    continue;
                case MirSwitch switchTerminator:
                    if (!TryResolveSwitch(switchTerminator, environment, out var next))
                    {
                        return null;
                    }

                    context.CountStep();
                    current = next;
                    continue;
                default:
                    return null;
            }
        }

        return null;
    }

    private bool TryExecuteInstruction(
        MirInstruction instruction,
        Dictionary<LocalId, MirConstant> environment,
        int depth,
        EvaluationContext context)
    {
        switch (instruction)
        {
            case MirAssign { Target: MirPlace { Kind: PlaceKind.Local } target } assign:
                var assigned = ResolveOperand(assign.Source, environment);
                if (assigned == null)
                {
                    return false;
                }

                environment[target.Local] = assigned;
                return true;
            case MirCopy { Target: MirPlace { Kind: PlaceKind.Local } target } copy:
                var copied = ResolveOperand(copy.Source, environment);
                if (copied == null)
                {
                    return false;
                }

                environment[target.Local] = copied;
                return true;
            case MirBinOp { Target: MirPlace { Kind: PlaceKind.Local } target } binOp:
                var left = ResolveOperand(binOp.Left, environment);
                var right = ResolveOperand(binOp.Right, environment);
                if (left == null || right == null)
                {
                    return false;
                }

                var folded = ConstantFolding.TryFoldConstants(binOp.Operator, left, right);
                if (folded == null)
                {
                    return false;
                }

                environment[target.Local] = folded;
                return true;
            case MirUnaryOp { Target: MirPlace { Kind: PlaceKind.Local } target } unaryOp:
                var operand = ResolveOperand(unaryOp.Operand, environment);
                if (operand == null)
                {
                    return false;
                }

                var unary = ConstantFolding.TryFoldUnary(unaryOp.Operator, operand);
                if (unary == null)
                {
                    return false;
                }

                environment[target.Local] = unary;
                return true;
            case MirSelect { Target: { Kind: PlaceKind.Local } target } select:
                var condition = ResolveOperand(select.Condition, environment);
                if (condition?.Value is not MirConstantValue.BoolValue { Value: var chooseTrue })
                {
                    return false;
                }

                var selected = ResolveOperand(
                    chooseTrue ? select.TrueValue : select.FalseValue,
                    environment);
                if (selected == null)
                {
                    return false;
                }

                environment[target.Local] = selected;
                return true;
            case MirCall { Function: MirFunctionRef functionRef } call:
                if (call.Target is not MirPlace { Kind: PlaceKind.Local } callTarget)
                {
                    return false;
                }

                var resolvedArguments = new List<MirOperand>(call.Arguments.Count);
                foreach (var argument in call.Arguments)
                {
                    var resolved = ResolveOperand(argument, environment);
                    if (resolved == null)
                    {
                        return false;
                    }

                    resolvedArguments.Add(resolved);
                }

                var callResult = TryFoldCore(functionRef, resolvedArguments, depth + 1, context);
                if (callResult == null)
                {
                    return false;
                }

                environment[callTarget.Local] = callResult;
                return true;
            default:
                return false;
        }
    }

    private static MirConstant? ResolveOperand(
        MirOperand operand,
        Dictionary<LocalId, MirConstant> environment)
    {
        return operand switch
        {
            MirConstant constant => constant,
            MirPlace { Kind: PlaceKind.Local } place => environment.GetValueOrDefault(place.Local),
            _ => null
        };
    }

    private static bool TryResolveSwitch(
        MirSwitch switchTerminator,
        Dictionary<LocalId, MirConstant> environment,
        out BlockId next)
    {
        next = BlockId.None;
        var discriminant = ResolveOperand(switchTerminator.Discriminant, environment);
        if (discriminant == null)
        {
            return false;
        }

        foreach (var branch in switchTerminator.Branches)
        {
            if (branch.BoundVariable != null || !ConstantValuesEqual(branch.Value.Value, discriminant.Value))
            {
                continue;
            }

            next = branch.Target;
            return true;
        }

        if (switchTerminator.DefaultTarget.HasValue)
        {
            next = switchTerminator.DefaultTarget.Value;
            return true;
        }

        return false;
    }

    private static bool ConstantValuesEqual(MirConstantValue left, MirConstantValue right) => left.Equals(right);

    private readonly record struct ConstantKey(TypeId TypeId, MirConstantValue Value);

    private readonly record struct MemoizedEvaluation(MirConstant? Result, long StepCost);

    private sealed class EvaluationKey : IEquatable<EvaluationKey>
    {
        private readonly ConstantKey[] _arguments;

        public EvaluationKey(string functionKey, IReadOnlyList<MirConstant> arguments)
        {
            FunctionKey = functionKey;
            _arguments = arguments
                .Select(static argument => new ConstantKey(argument.TypeId, argument.Value))
                .ToArray();
        }

        private string FunctionKey { get; }

        public bool Equals(EvaluationKey? other) =>
            other != null &&
            string.Equals(FunctionKey, other.FunctionKey, StringComparison.Ordinal) &&
            _arguments.AsSpan().SequenceEqual(other._arguments);

        public override bool Equals(object? obj) => obj is EvaluationKey other && Equals(other);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            hash.Add(FunctionKey, StringComparer.Ordinal);
            foreach (var argument in _arguments)
            {
                hash.Add(argument);
            }

            return hash.ToHashCode();
        }
    }

    private sealed class EvaluationContext(long remainingSteps)
    {
        private readonly HashSet<EvaluationKey> _inProgress = [];
        private readonly long _initialSteps = remainingSteps;
        private long _remainingSteps = remainingSteps;

        public long StepsConsumed => _initialSteps - _remainingSteps;

        public bool TryEnter(EvaluationKey key) => _inProgress.Add(key);

        public void Leave(EvaluationKey key) => _inProgress.Remove(key);

        public void CountStep() => CountSteps(1);

        public void CountSteps(long steps)
        {
            if (steps < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(steps));
            }

            if (steps > _remainingSteps)
            {
                throw new BudgetExceededException();
            }

            _remainingSteps -= steps;
        }
    }

    private sealed class BudgetExceededException : Exception;
}
