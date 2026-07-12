using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Expressions;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static GuardTruth EvaluateGuardTruthWithBindings(
        EidosAstNode? expression,
        IReadOnlyDictionary<string, bool> boolBindings,
        IReadOnlyDictionary<string, ListGuardIntBinding> intBindings)
    {
        if (expression == null)
        {
            return GuardTruth.Unknown;
        }

        switch (expression)
        {
            case SequentialGuardExpr sequentialGuard:
            {
                var result = GuardTruth.True;
                foreach (var guard in sequentialGuard.Guards)
                {
                    result = AndGuardTruth(result, EvaluateGuardTruthWithBindings(guard, boolBindings, intBindings));
                    if (result is GuardTruth.False or GuardTruth.Unknown)
                    {
                        return result;
                    }
                }

                return result;
            }

            case LiteralExpr literal when TryGetBoolLiteral(literal, out var literalValue):
                return ToGuardTruth(literalValue);

            case IdentifierExpr identifier when !string.IsNullOrWhiteSpace(identifier.Name):
                return boolBindings.TryGetValue(identifier.Name, out var boolValue)
                    ? ToGuardTruth(boolValue)
                    : GuardTruth.Unknown;

            case UnaryExpr { Operator: UnaryOp.Not } unary:
                return NegateGuardTruth(EvaluateGuardTruthWithBindings(unary.Operand, boolBindings, intBindings));

            case BinaryExpr { Operator: BinaryOp.And } binary:
                if (TryIsGuardNegationPair(binary.Left, binary.Right))
                {
                    return GuardTruth.False;
                }

                return AndGuardTruth(
                    EvaluateGuardTruthWithBindings(binary.Left, boolBindings, intBindings),
                    EvaluateGuardTruthWithBindings(binary.Right, boolBindings, intBindings));

            case BinaryExpr { Operator: BinaryOp.Or } binary:
                if (TryIsGuardNegationPair(binary.Left, binary.Right))
                {
                    return GuardTruth.True;
                }

                return OrGuardTruth(
                    EvaluateGuardTruthWithBindings(binary.Left, boolBindings, intBindings),
                    EvaluateGuardTruthWithBindings(binary.Right, boolBindings, intBindings));

            case BinaryExpr binary when binary.Operator is
                BinaryOp.Equal or
                BinaryOp.NotEqual or
                BinaryOp.Less or
                BinaryOp.Greater or
                BinaryOp.LessEqual or
                BinaryOp.GreaterEqual:
                return EvaluateGuardComparisonTruth(binary, boolBindings, intBindings);

            default:
                return GuardTruth.Unknown;
        }
    }

    private static bool TryIsGuardNegationPair(EidosAstNode? left, EidosAstNode? right)
    {
        if (right is UnaryExpr { Operator: UnaryOp.Not } rightNot &&
            AreGuardExprEquivalent(left, rightNot.Operand))
        {
            return true;
        }

        if (left is UnaryExpr { Operator: UnaryOp.Not } leftNot &&
            AreGuardExprEquivalent(right, leftNot.Operand))
        {
            return true;
        }

        return false;
    }

    private static bool AreGuardExprEquivalent(EidosAstNode? left, EidosAstNode? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left == null || right == null)
        {
            return false;
        }

        if (left is IdentifierExpr leftIdentifier &&
            right is IdentifierExpr rightIdentifier)
        {
            return string.Equals(
                leftIdentifier.Name,
                rightIdentifier.Name,
                StringComparison.Ordinal);
        }

        if (left is LiteralExpr leftLiteral && right is LiteralExpr rightLiteral)
        {
            if (TryGetBoolLiteral(leftLiteral, out var leftBool) &&
                TryGetBoolLiteral(rightLiteral, out var rightBool))
            {
                return leftBool == rightBool;
            }

            if (TryGetIntegerLiteral(leftLiteral, out var leftInt) &&
                TryGetIntegerLiteral(rightLiteral, out var rightInt))
            {
                return leftInt == rightInt;
            }
        }

        if (left is UnaryExpr leftUnary &&
            right is UnaryExpr rightUnary &&
            leftUnary.Operator == rightUnary.Operator)
        {
            return AreGuardExprEquivalent(leftUnary.Operand, rightUnary.Operand);
        }

        if (left is BinaryExpr leftBinary &&
            right is BinaryExpr rightBinary &&
            leftBinary.Operator == rightBinary.Operator)
        {
            return AreGuardExprEquivalent(leftBinary.Left, rightBinary.Left) &&
                   AreGuardExprEquivalent(leftBinary.Right, rightBinary.Right);
        }

        return false;
    }

    private static GuardTruth EvaluateGuardComparisonTruth(
        BinaryExpr expression,
        IReadOnlyDictionary<string, bool> boolBindings,
        IReadOnlyDictionary<string, ListGuardIntBinding> intBindings)
    {
        var left = EvaluateGuardScalarValue(expression.Left, boolBindings, intBindings);
        var right = EvaluateGuardScalarValue(expression.Right, boolBindings, intBindings);

        if (expression.Operator is BinaryOp.Equal)
        {
            return EvaluateGuardScalarEquality(left, right);
        }

        if (expression.Operator is BinaryOp.NotEqual)
        {
            return NegateGuardTruth(EvaluateGuardScalarEquality(left, right));
        }

        if (left.Kind is GuardScalarKind.IntExact && right.Kind is GuardScalarKind.IntExact)
        {
            return expression.Operator switch
            {
                BinaryOp.Less => ToGuardTruth(left.IntValue < right.IntValue),
                BinaryOp.Greater => ToGuardTruth(left.IntValue > right.IntValue),
                BinaryOp.LessEqual => ToGuardTruth(left.IntValue <= right.IntValue),
                BinaryOp.GreaterEqual => ToGuardTruth(left.IntValue >= right.IntValue),
                _ => GuardTruth.Unknown
            };
        }

        if (!TryGetFiniteIntComparisonSet(left, out var leftValues) ||
            !TryGetFiniteIntComparisonSet(right, out var rightValues) ||
            leftValues.Count == 0 ||
            rightValues.Count == 0)
        {
            return GuardTruth.Unknown;
        }

        var leftMin = leftValues.Min();
        var leftMax = leftValues.Max();
        var rightMin = rightValues.Min();
        var rightMax = rightValues.Max();

        return expression.Operator switch
        {
            BinaryOp.Less when leftMax < rightMin => GuardTruth.True,
            BinaryOp.Less when leftMin >= rightMax => GuardTruth.False,
            BinaryOp.Greater when leftMin > rightMax => GuardTruth.True,
            BinaryOp.Greater when leftMax <= rightMin => GuardTruth.False,
            BinaryOp.LessEqual when leftMax <= rightMin => GuardTruth.True,
            BinaryOp.LessEqual when leftMin > rightMax => GuardTruth.False,
            BinaryOp.GreaterEqual when leftMin >= rightMax => GuardTruth.True,
            BinaryOp.GreaterEqual when leftMax < rightMin => GuardTruth.False,
            _ => GuardTruth.Unknown
        };
    }

    private static GuardTruth EvaluateGuardScalarEquality(GuardScalarValue left, GuardScalarValue right)
    {
        if (left.Kind is GuardScalarKind.Bool && right.Kind is GuardScalarKind.Bool)
        {
            return ToGuardTruth(left.BoolValue == right.BoolValue);
        }

        if (left.Kind is GuardScalarKind.IntExact && right.Kind is GuardScalarKind.IntExact)
        {
            return ToGuardTruth(left.IntValue == right.IntValue);
        }

        if (left.Kind is GuardScalarKind.IntExact && right.Kind is GuardScalarKind.IntOther)
        {
            return right.ExcludedIntValues.Contains(left.IntValue)
                ? GuardTruth.False
                : GuardTruth.Unknown;
        }

        if (left.Kind is GuardScalarKind.IntOther && right.Kind is GuardScalarKind.IntExact)
        {
            return left.ExcludedIntValues.Contains(right.IntValue)
                ? GuardTruth.False
                : GuardTruth.Unknown;
        }

        if (left.Kind is GuardScalarKind.IntFiniteSet && right.Kind is GuardScalarKind.IntExact)
        {
            if (left.FiniteIntValues.Count == 0)
            {
                return GuardTruth.Unknown;
            }

            if (!left.FiniteIntValues.Contains(right.IntValue))
            {
                return GuardTruth.False;
            }

            return left.FiniteIntValues.Count == 1
                ? GuardTruth.True
                : GuardTruth.Unknown;
        }

        if (left.Kind is GuardScalarKind.IntExact && right.Kind is GuardScalarKind.IntFiniteSet)
        {
            return EvaluateGuardScalarEquality(right, left);
        }

        if (left.Kind is GuardScalarKind.IntFiniteSet && right.Kind is GuardScalarKind.IntFiniteSet)
        {
            var intersection = left.FiniteIntValues
                .Where(value => right.FiniteIntValues.Contains(value))
                .ToList();
            if (intersection.Count == 0)
            {
                return GuardTruth.False;
            }

            return left.FiniteIntValues.Count == 1 && right.FiniteIntValues.Count == 1
                ? GuardTruth.True
                : GuardTruth.Unknown;
        }

        if (left.Kind is GuardScalarKind.IntFiniteSet && right.Kind is GuardScalarKind.IntOther)
        {
            return left.FiniteIntValues.All(value => right.ExcludedIntValues.Contains(value))
                ? GuardTruth.False
                : GuardTruth.Unknown;
        }

        if (left.Kind is GuardScalarKind.IntOther && right.Kind is GuardScalarKind.IntFiniteSet)
        {
            return right.FiniteIntValues.All(value => left.ExcludedIntValues.Contains(value))
                ? GuardTruth.False
                : GuardTruth.Unknown;
        }

        return GuardTruth.Unknown;
    }

    private static GuardScalarValue EvaluateGuardScalarValue(
        EidosAstNode? expression,
        IReadOnlyDictionary<string, bool> boolBindings,
        IReadOnlyDictionary<string, ListGuardIntBinding> intBindings)
    {
        if (expression == null)
        {
            return GuardScalarValue.Unknown;
        }

        switch (expression)
        {
            case LiteralExpr literal when TryGetBoolLiteral(literal, out var boolValue):
                return GuardScalarValue.FromBool(boolValue);

            case LiteralExpr literal when TryGetIntegerLiteral(literal, out var intValue):
                return GuardScalarValue.FromIntExact(intValue);

            case IdentifierExpr identifier when !string.IsNullOrWhiteSpace(identifier.Name):
                if (boolBindings.TryGetValue(identifier.Name, out var boolBinding))
                {
                    return GuardScalarValue.FromBool(boolBinding);
                }

                if (intBindings.TryGetValue(identifier.Name, out var intBinding))
                {
                    if (intBinding.IsOtherBucket)
                    {
                        return GuardScalarValue.FromIntOther(intBinding.ExcludedValues);
                    }

                    var finiteValues = GetFiniteBindingValues(intBinding);
                    if (finiteValues.Count == 1)
                    {
                        return GuardScalarValue.FromIntExact(finiteValues.First());
                    }

                    if (finiteValues.Count > 1)
                    {
                        return GuardScalarValue.FromIntFiniteSet(finiteValues);
                    }

                    return GuardScalarValue.FromIntExact(intBinding.Value);
                }

                return GuardScalarValue.Unknown;

            case UnaryExpr { Operator: UnaryOp.Negate } unary:
            {
                var operand = EvaluateGuardScalarValue(unary.Operand, boolBindings, intBindings);
                if (operand.Kind is GuardScalarKind.IntExact)
                {
                    return operand.IntValue == long.MinValue
                        ? GuardScalarValue.Unknown
                        : GuardScalarValue.FromIntExact(-operand.IntValue);
                }

                if (operand.Kind is GuardScalarKind.IntFiniteSet)
                {
                    var negated = new HashSet<long>();
                    foreach (var value in operand.FiniteIntValues)
                    {
                        if (value == long.MinValue)
                        {
                            return GuardScalarValue.Unknown;
                        }

                        negated.Add(-value);
                    }

                    return GuardScalarValue.FromIntFiniteSet(negated);
                }

                return GuardScalarValue.Unknown;
            }

            case BinaryExpr binary when binary.Operator is
                BinaryOp.Add or
                BinaryOp.Subtract or
                BinaryOp.Multiply or
                BinaryOp.Divide or
                BinaryOp.Modulo:
                return EvaluateGuardArithmeticScalar(binary, boolBindings, intBindings);

            default:
                return GuardScalarValue.Unknown;
        }
    }

    private const int MaxGuardArithmeticFiniteCaseCount = 64;

    private static GuardScalarValue EvaluateGuardArithmeticScalar(
        BinaryExpr expression,
        IReadOnlyDictionary<string, bool> boolBindings,
        IReadOnlyDictionary<string, ListGuardIntBinding> intBindings)
    {
        var left = EvaluateGuardScalarValue(expression.Left, boolBindings, intBindings);
        var right = EvaluateGuardScalarValue(expression.Right, boolBindings, intBindings);

        if (!TryGetFiniteIntComparisonSet(left, out var leftValues) ||
            !TryGetFiniteIntComparisonSet(right, out var rightValues) ||
            leftValues.Count == 0 ||
            rightValues.Count == 0)
        {
            return GuardScalarValue.Unknown;
        }

        var results = new HashSet<long>();
        foreach (var leftValue in leftValues)
        {
            foreach (var rightValue in rightValues)
            {
                if (!TryApplyGuardArithmeticOperator(
                        expression.Operator,
                        leftValue,
                        rightValue,
                        out var result))
                {
                    return GuardScalarValue.Unknown;
                }

                results.Add(result);
                if (results.Count > MaxGuardArithmeticFiniteCaseCount)
                {
                    return GuardScalarValue.Unknown;
                }
            }
        }

        if (results.Count == 0)
        {
            return GuardScalarValue.Unknown;
        }

        if (results.Count == 1)
        {
            return GuardScalarValue.FromIntExact(results.First());
        }

        return GuardScalarValue.FromIntFiniteSet(results);
    }

    private static bool TryApplyGuardArithmeticOperator(
        BinaryOp op,
        long left,
        long right,
        out long result)
    {
        result = 0;
        try
        {
            checked
            {
                switch (op)
                {
                    case BinaryOp.Add:
                        result = left + right;
                        return true;
                    case BinaryOp.Subtract:
                        result = left - right;
                        return true;
                    case BinaryOp.Multiply:
                        result = left * right;
                        return true;
                    case BinaryOp.Divide:
                        if (right == 0 || (left == long.MinValue && right == -1))
                        {
                            return false;
                        }

                        result = left / right;
                        return true;
                    case BinaryOp.Modulo:
                        if (right == 0 || (left == long.MinValue && right == -1))
                        {
                            return false;
                        }

                        result = left % right;
                        return true;
                    default:
                        return false;
                }
            }
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static bool TryGetFiniteIntComparisonSet(GuardScalarValue scalar, out IReadOnlySet<long> values)
    {
        if (scalar.Kind is GuardScalarKind.IntExact)
        {
            values = new HashSet<long> { scalar.IntValue };
            return true;
        }

        if (scalar.Kind is GuardScalarKind.IntFiniteSet)
        {
            values = scalar.FiniteIntValues;
            return true;
        }

        values = new HashSet<long>();
        return false;
    }

    private static bool TryGetIntegerLiteral(LiteralExpr literal, out long value)
    {
        value = 0;
        if (literal.Kind is not (LiteralKind.Integer or LiteralKind.Char))
        {
            return false;
        }

        switch (literal.Value)
        {
            case long longValue:
                value = longValue;
                return true;
            case int intValue:
                value = intValue;
                return true;
            case short shortValue:
                value = shortValue;
                return true;
            case byte byteValue:
                value = byteValue;
                return true;
            case char charValue:
                value = charValue;
                return true;
            case string text when literal.Kind == LiteralKind.Char && text.Length == 1:
                value = text[0];
                return true;
            case string text when long.TryParse(text, out var parsed):
                value = parsed;
                return true;
            default:
                return false;
        }
    }
}
