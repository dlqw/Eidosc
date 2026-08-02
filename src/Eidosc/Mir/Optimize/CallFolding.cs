using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Compile-time evaluator for calls to module functions whose arguments are
/// all constants. The evaluator only understands scalar SSA shapes: constant
/// propagation, arithmetic folding, constant switches and nested pure calls.
/// Any other instruction (loads, stores, allocations, pattern injection, ...)
/// aborts the fold and leaves the call untouched. Recursion is bounded by a
/// depth limit and a per-call instruction budget, so terminating recurrences
/// such as fib(10) fold while unbounded ones safely give up.
/// </summary>
public sealed class CallFolding
{
    private const int MaxCallDepth = 64;
    private const long MaxStepsPerFold = 1_000_000;

    private readonly Dictionary<string, MirFunc> _functionsByKey;
    private long _steps;

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
        _steps = 0;
        try
        {
            return TryFoldCore(function, arguments, depth: 0);
        }
        catch (BudgetExceededException)
        {
            return null;
        }
    }

    private MirConstant? TryFoldCore(
        MirFunctionRef function,
        IReadOnlyList<MirOperand> arguments,
        int depth)
    {
        if (depth > MaxCallDepth)
        {
            throw new BudgetExceededException();
        }

        if (!_functionsByKey.TryGetValue(MirFunctionIdentity.GetStableKey(function), out var callee))
        {
            return null;
        }

        var parameters = callee.Locals.Where(static local => local.IsParameter).ToList();
        if (parameters.Count != arguments.Count)
        {
            return null;
        }

        var environment = new Dictionary<LocalId, MirConstant>();
        for (var i = 0; i < parameters.Count; i++)
        {
            if (arguments[i] is MirConstant constant)
            {
                environment[parameters[i].Id] = constant;
            }
            else
            {
                return null;
            }
        }

        return EvaluateFunction(callee, environment, depth);
    }

    private MirConstant? EvaluateFunction(
        MirFunc function,
        Dictionary<LocalId, MirConstant> initialEnvironment,
        int depth)
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
            foreach (var instruction in block.Instructions)
            {
                CountStep();
                if (!TryExecuteInstruction(instruction, environment, depth))
                {
                    return null;
                }
            }

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
                    current = gotoTerminator.Target;
                    continue;
                case MirSwitch switchTerminator:
                    if (!TryResolveSwitch(switchTerminator, environment, out var next))
                    {
                        return null;
                    }

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
        int depth)
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

                var callResult = TryFoldCore(functionRef, resolvedArguments, depth + 1);
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

    private static bool ConstantValuesEqual(MirConstantValue left, MirConstantValue right) =>
        (left, right) switch
        {
            (MirConstantValue.IntValue l, MirConstantValue.IntValue r) => l.Value == r.Value,
            (MirConstantValue.FloatValue l, MirConstantValue.FloatValue r) => l.Value.Equals(r.Value),
            (MirConstantValue.BoolValue l, MirConstantValue.BoolValue r) => l.Value == r.Value,
            (MirConstantValue.CharValue l, MirConstantValue.CharValue r) => l.Value == r.Value,
            (MirConstantValue.StringValue l, MirConstantValue.StringValue r) => string.Equals(l.Value, r.Value, StringComparison.Ordinal),
            _ => false
        };

    private void CountStep()
    {
        if (++_steps > MaxStepsPerFold)
        {
            throw new BudgetExceededException();
        }
    }

    private sealed class BudgetExceededException : Exception;
}
