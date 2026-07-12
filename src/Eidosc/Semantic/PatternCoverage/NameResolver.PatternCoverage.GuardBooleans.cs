using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static bool TryEvaluateGuardBooleanConstant(EidosAstNode? expression, out bool value)
    {
        value = false;
        var truth = EvaluateGuardTruth(expression, _ => GuardTruth.Unknown);
        if (truth is GuardTruth.Unknown)
        {
            return false;
        }

        value = truth is GuardTruth.True;
        return true;
    }

    private static bool TryEvaluateGuardBooleanConstantForPattern(
        Pattern pattern,
        EidosAstNode? guardExpression,
        string? guardSubjectName,
        out bool value)
    {
        value = false;
        var cases = new HashSet<bool>();
        if (!TryGetExactBoolPatternCases(pattern, cases) || cases.Count == 0)
        {
            return false;
        }

        TryGetPrimaryPatternBindingName(pattern, out var bindingName);
        if (string.IsNullOrWhiteSpace(bindingName) && string.IsNullOrWhiteSpace(guardSubjectName))
        {
            return false;
        }

        var hasKnownTruth = false;
        var knownTruth = GuardTruth.Unknown;

        foreach (var patternCase in cases)
        {
            var truth = EvaluateGuardTruth(
                guardExpression,
                identifier =>
                    (!string.IsNullOrWhiteSpace(bindingName) &&
                     string.Equals(identifier, bindingName, StringComparison.Ordinal)) ||
                    (!string.IsNullOrWhiteSpace(guardSubjectName) &&
                     string.Equals(identifier, guardSubjectName, StringComparison.Ordinal))
                        ? ToGuardTruth(patternCase)
                        : GuardTruth.Unknown);
            if (truth is GuardTruth.Unknown)
            {
                return false;
            }

            if (!hasKnownTruth)
            {
                knownTruth = truth;
                hasKnownTruth = true;
                continue;
            }

            if (knownTruth != truth)
            {
                return false;
            }
        }

        if (!hasKnownTruth)
        {
            return false;
        }

        value = knownTruth is GuardTruth.True;
        return true;
    }

    private static bool TryGetBoolLiteral(LiteralExpr literal, out bool value)
    {
        value = false;
        if (literal.Kind == LiteralKind.Boolean && literal.Value is bool boolValue)
        {
            value = boolValue;
            return true;
        }

        if (literal.Value is bool fallbackBool)
        {
            value = fallbackBool;
            return true;
        }

        return false;
    }

    private static void TryCollectBoolCoverageFromGuardedPattern(
        Pattern pattern,
        EidosAstNode? guardExpression,
        ISet<bool> boolCoverage,
        string? guardSubjectName,
        bool allowGuardSubjectInference)
    {
        if (allowGuardSubjectInference &&
            !string.IsNullOrWhiteSpace(guardSubjectName))
        {
            var literalCases = new HashSet<bool>();
            if (TryGetExactBoolPatternCases(pattern, literalCases))
            {
                foreach (var literalCase in literalCases)
                {
                    if (IsGuardGuaranteedTrueForBindingValue(
                            guardExpression,
                            guardSubjectName,
                            literalCase,
                            guardSubjectName))
                    {
                        boolCoverage.Add(literalCase);
                    }
                }

                return;
            }
        }

        if (!TryGetPrimaryPatternBindingName(pattern, out var bindingName))
        {
            if (!allowGuardSubjectInference ||
                string.IsNullOrWhiteSpace(guardSubjectName) ||
                !IsPatternIrrefutable(pattern))
            {
                return;
            }

            bindingName = guardSubjectName;
        }

        if (IsGuardGuaranteedTrueForBindingValue(guardExpression, bindingName, true, guardSubjectName))
        {
            boolCoverage.Add(true);
        }

        if (IsGuardGuaranteedTrueForBindingValue(guardExpression, bindingName, false, guardSubjectName))
        {
            boolCoverage.Add(false);
        }
    }

    private static bool IsGuardGuaranteedTrueForBindingValue(
        EidosAstNode? guardExpression,
        string bindingName,
        bool bindingValue,
        string? subjectAliasName)
    {
        var truth = EvaluateGuardTruth(
            guardExpression,
            identifier =>
                string.Equals(identifier, bindingName, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(subjectAliasName) &&
                 string.Equals(identifier, subjectAliasName, StringComparison.Ordinal))
                    ? ToGuardTruth(bindingValue)
                    : GuardTruth.Unknown);
        return truth is GuardTruth.True;
    }

    private static bool TryGetPrimaryPatternBindingName(Pattern pattern, out string bindingName)
    {
        bindingName = string.Empty;
        switch (pattern)
        {
            case VarPattern varPattern when !string.IsNullOrWhiteSpace(varPattern.Name):
                bindingName = varPattern.Name;
                return true;

            case AsPattern asPattern when !string.IsNullOrWhiteSpace(asPattern.BindingName):
                bindingName = asPattern.BindingName;
                return true;

            default:
                return false;
        }
    }

    private static bool TryGetIdentifierName(EidosAstNode? expression, out string identifierName)
    {
        identifierName = string.Empty;
        if (expression is not IdentifierExpr identifier || string.IsNullOrWhiteSpace(identifier.Name))
        {
            return false;
        }

        identifierName = identifier.Name;
        return true;
    }

    private static GuardTruth EvaluateGuardTruth(EidosAstNode? expression, Func<string, GuardTruth> identifierResolver)
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
                    result = AndGuardTruth(result, EvaluateGuardTruth(guard, identifierResolver));
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
                return identifierResolver(identifier.Name);

            case UnaryExpr { Operator: UnaryOp.Not } unary:
                return NegateGuardTruth(EvaluateGuardTruth(unary.Operand, identifierResolver));

            case BinaryExpr { Operator: BinaryOp.And } binary:
                return AndGuardTruth(
                    EvaluateGuardTruth(binary.Left, identifierResolver),
                    EvaluateGuardTruth(binary.Right, identifierResolver));

            case BinaryExpr { Operator: BinaryOp.Or } binary:
                return OrGuardTruth(
                    EvaluateGuardTruth(binary.Left, identifierResolver),
                    EvaluateGuardTruth(binary.Right, identifierResolver));

            case BinaryExpr { Operator: BinaryOp.Equal } binary:
                return EqualGuardTruth(
                    EvaluateGuardTruth(binary.Left, identifierResolver),
                    EvaluateGuardTruth(binary.Right, identifierResolver));

            case BinaryExpr { Operator: BinaryOp.NotEqual } binary:
                return NegateGuardTruth(EqualGuardTruth(
                    EvaluateGuardTruth(binary.Left, identifierResolver),
                    EvaluateGuardTruth(binary.Right, identifierResolver)));

            default:
                return GuardTruth.Unknown;
        }
    }

    private static GuardTruth ToGuardTruth(bool value)
    {
        return value ? GuardTruth.True : GuardTruth.False;
    }

    private static GuardTruth NegateGuardTruth(GuardTruth value)
    {
        return value switch
        {
            GuardTruth.True => GuardTruth.False,
            GuardTruth.False => GuardTruth.True,
            _ => GuardTruth.Unknown
        };
    }

    private static GuardTruth AndGuardTruth(GuardTruth left, GuardTruth right)
    {
        if (left is GuardTruth.False || right is GuardTruth.False)
        {
            return GuardTruth.False;
        }

        if (left is GuardTruth.True && right is GuardTruth.True)
        {
            return GuardTruth.True;
        }

        return GuardTruth.Unknown;
    }

    private static GuardTruth OrGuardTruth(GuardTruth left, GuardTruth right)
    {
        if (left is GuardTruth.True || right is GuardTruth.True)
        {
            return GuardTruth.True;
        }

        if (left is GuardTruth.False && right is GuardTruth.False)
        {
            return GuardTruth.False;
        }

        return GuardTruth.Unknown;
    }

    private static GuardTruth EqualGuardTruth(GuardTruth left, GuardTruth right)
    {
        if (left is GuardTruth.Unknown || right is GuardTruth.Unknown)
        {
            return GuardTruth.Unknown;
        }

        return left == right ? GuardTruth.True : GuardTruth.False;
    }

    private enum GuardTruth
    {
        Unknown,
        False,
        True
    }

    private enum GuardScalarKind
    {
        Unknown,
        Bool,
        IntExact,
        IntOther,
        IntFiniteSet
    }

    private readonly record struct ListIntCaseToken(long Value, bool IsOtherBucket);

    private readonly record struct ListGuardIntBinding(
        long Value,
        bool IsOtherBucket,
        IReadOnlySet<long> ExcludedValues,
        IReadOnlySet<long> FiniteValues)
    {
        public static ListGuardIntBinding FromExact(long value)
        {
            return new ListGuardIntBinding(
                value,
                IsOtherBucket: false,
                ExcludedValues: new HashSet<long>(),
                FiniteValues: new HashSet<long> { value });
        }

        public static ListGuardIntBinding FromOther(IReadOnlySet<long> excludedValues)
        {
            return new ListGuardIntBinding(
                0,
                IsOtherBucket: true,
                ExcludedValues: excludedValues.Count == 0
                    ? new HashSet<long>()
                    : new HashSet<long>(excludedValues),
                FiniteValues: new HashSet<long>());
        }

        public static ListGuardIntBinding FromFiniteSet(IReadOnlySet<long> values)
        {
            if (values.Count == 0)
            {
                return new ListGuardIntBinding(
                    0,
                    IsOtherBucket: false,
                    ExcludedValues: new HashSet<long>(),
                    FiniteValues: new HashSet<long>());
            }

            if (values.Count == 1)
            {
                return FromExact(values.First());
            }

            var finite = new HashSet<long>(values);
            return new ListGuardIntBinding(
                finite.First(),
                IsOtherBucket: false,
                ExcludedValues: new HashSet<long>(),
                FiniteValues: finite);
        }
    }

    private readonly record struct GuardScalarValue(
        GuardScalarKind Kind,
        bool BoolValue,
        long IntValue,
        IReadOnlySet<long> ExcludedIntValues,
        IReadOnlySet<long> FiniteIntValues)
    {
        public static GuardScalarValue Unknown { get; } = new(
            GuardScalarKind.Unknown,
            BoolValue: false,
            IntValue: 0,
            ExcludedIntValues: new HashSet<long>(),
            FiniteIntValues: new HashSet<long>());

        public static GuardScalarValue FromBool(bool value)
        {
            return new GuardScalarValue(
                GuardScalarKind.Bool,
                BoolValue: value,
                IntValue: 0,
                ExcludedIntValues: new HashSet<long>(),
                FiniteIntValues: new HashSet<long>());
        }

        public static GuardScalarValue FromIntExact(long value)
        {
            return new GuardScalarValue(
                GuardScalarKind.IntExact,
                BoolValue: false,
                IntValue: value,
                ExcludedIntValues: new HashSet<long>(),
                FiniteIntValues: new HashSet<long> { value });
        }

        public static GuardScalarValue FromIntOther(IReadOnlySet<long> excludedValues)
        {
            return new GuardScalarValue(
                GuardScalarKind.IntOther,
                BoolValue: false,
                IntValue: 0,
                ExcludedIntValues: excludedValues.Count == 0
                    ? new HashSet<long>()
                    : new HashSet<long>(excludedValues),
                FiniteIntValues: new HashSet<long>());
        }

        public static GuardScalarValue FromIntFiniteSet(IReadOnlySet<long> values)
        {
            if (values.Count == 0)
            {
                return Unknown;
            }

            if (values.Count == 1)
            {
                return FromIntExact(values.First());
            }

            return new GuardScalarValue(
                GuardScalarKind.IntFiniteSet,
                BoolValue: false,
                IntValue: 0,
                ExcludedIntValues: new HashSet<long>(),
                FiniteIntValues: new HashSet<long>(values));
        }
    }

    private static bool IsPatternIrrefutable(Pattern? pattern)
    {
        if (pattern == null)
        {
            return false;
        }

        return pattern switch
        {
            VarPattern => true,
            WildcardPattern => true,
            AsPattern asPattern when asPattern.InnerPattern == null => true,
            AsPattern asPattern => IsPatternIrrefutable(asPattern.InnerPattern),
            TuplePattern tuplePattern => tuplePattern.Elements.All(IsPatternIrrefutable),
            ListPattern listPattern => listPattern.HasRestMarker &&
                                       listPattern.Elements.Count == 0 &&
                                       listPattern.SuffixElements.Count == 0 &&
                                       (listPattern.RestPattern == null || IsPatternIrrefutable(listPattern.RestPattern)),
            OrPattern orPattern => orPattern.Alternatives.Any(IsPatternIrrefutable),
            AndPattern andPattern => andPattern.Conjuncts.Count > 0 &&
                                     andPattern.Conjuncts.All(IsPatternIrrefutable),
            _ => false
        };
    }

    private static void CollectBoolLiteralCoverage(Pattern? pattern, ISet<bool> values)
    {
        if (pattern == null)
        {
            return;
        }

        switch (pattern)
        {
            case LiteralPattern { Type: LiteralType.Boolean, Value: bool value }:
                values.Add(value);
                return;

            case AsPattern { InnerPattern: not null } asPattern:
                CollectBoolLiteralCoverage(asPattern.InnerPattern, values);
                return;

            case OrPattern orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    CollectBoolLiteralCoverage(alternative, values);
                }
                return;

            default:
                return;
        }
    }

    private static bool PatternContainsBoolLiteral(Pattern? pattern)
    {
        if (pattern == null)
        {
            return false;
        }

        var values = new HashSet<bool>();
        CollectBoolLiteralCoverage(pattern, values);
        return values.Count > 0;
    }

    private static bool TryGetExactBoolPatternCases(Pattern? pattern, ISet<bool> cases)
    {
        if (pattern == null)
        {
            return false;
        }

        switch (pattern)
        {
            case LiteralPattern { Type: LiteralType.Boolean, Value: bool value }:
                cases.Add(value);
                return true;

            case AsPattern { InnerPattern: not null } asPattern:
                return TryGetExactBoolPatternCases(asPattern.InnerPattern, cases);

            case OrPattern orPattern when orPattern.Alternatives.Count > 0:
            {
                var hasAlternative = false;
                foreach (var alternative in orPattern.Alternatives)
                {
                    var alternativeCases = new HashSet<bool>();
                    if (!TryGetExactBoolPatternCases(alternative, alternativeCases))
                    {
                        return false;
                    }

                    hasAlternative = true;
                    foreach (var alternativeCase in alternativeCases)
                    {
                        cases.Add(alternativeCase);
                    }
                }

                return hasAlternative;
            }

            case AndPattern andPattern when andPattern.Conjuncts.Count > 0:
            {
                HashSet<bool>? intersection = null;
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    var conjunctCases = new HashSet<bool>();
                    if (!TryGetExactBoolPatternCases(conjunct, conjunctCases))
                    {
                        return false;
                    }

                    if (intersection == null)
                    {
                        intersection = conjunctCases;
                    }
                    else
                    {
                        intersection.IntersectWith(conjunctCases);
                    }
                }

                if (intersection == null)
                {
                    return false;
                }

                foreach (var intersectionCase in intersection)
                {
                    cases.Add(intersectionCase);
                }

                return true;
            }

            case NotPattern { InnerPattern: not null } notPattern:
            {
                var innerCases = new HashSet<bool>();
                if (!TryGetExactBoolPatternCases(notPattern.InnerPattern, innerCases))
                {
                    return false;
                }

                if (!innerCases.Contains(true))
                {
                    cases.Add(true);
                }

                if (!innerCases.Contains(false))
                {
                    cases.Add(false);
                }

                return true;
            }

            case ViewPattern { InnerPattern: not null, IsTransparentIdentityView: true } viewPattern:
                return TryGetExactBoolPatternCases(viewPattern.InnerPattern, cases);

            default:
                return false;
        }
    }
}
