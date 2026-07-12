using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private (Type? ExpectedParamType, int ConsumedParameterCount) GetPatternBranchParameterExpectation(
        PatternBranch branch,
        IReadOnlyList<Type> parameterTypes,
        bool consumeWholeParameterList)
    {
        var parameterCount = parameterTypes.Count;
        if (parameterCount <= 0)
        {
            return (null, 0);
        }

        if (branch.Pattern is TuplePattern tuplePattern &&
            tuplePattern.Elements.Count > 0)
        {
            if (consumeWholeParameterList && tuplePattern.Elements.Count < parameterCount)
            {
                var prefixCount = parameterCount - tuplePattern.Elements.Count + 1;
                var expectedElements = new List<Type>(tuplePattern.Elements.Count)
                {
                    BuildGroupedPatternExpectedType(parameterTypes.Take(prefixCount))
                };

                for (var i = 1; i < tuplePattern.Elements.Count; i++)
                {
                    expectedElements.Add(parameterTypes[prefixCount + i - 1]);
                }

                return (new TyTuple { Elements = expectedElements }, parameterCount);
            }

            if (tuplePattern.Elements.Count <= parameterCount)
            {
                return (
                    new TyTuple { Elements = parameterTypes.Take(tuplePattern.Elements.Count).ToList() },
                    tuplePattern.Elements.Count);
            }
        }

        if (consumeWholeParameterList)
        {
            var leadingLambdaParameters = CountLeadingLambdaParameters(branch.Expression);
            if (leadingLambdaParameters == 0 &&
                branch.Expression is IdentifierExpr or PathExpr)
            {
                return (parameterTypes[0], 1);
            }

            var consumedParameterCount = Math.Clamp(parameterCount - leadingLambdaParameters, 1, parameterCount);
            var expectedParamType = consumedParameterCount == 1
                ? parameterTypes[0]
                : new TyTuple { Elements = parameterTypes.Take(consumedParameterCount).ToList() };
            return (expectedParamType, consumedParameterCount);
        }

        return (parameterTypes[0], 1);
    }

    private static Type BuildGroupedPatternExpectedType(IEnumerable<Type> parameterTypes)
    {
        var parameters = parameterTypes.ToList();
        return parameters.Count == 1
            ? parameters[0]
            : new TyTuple { Elements = parameters };
    }

    private static int CountLeadingLambdaParameters(EidosAstNode? expression)
    {
        var count = 0;
        while (expression is LambdaExpr lambda)
        {
            count += lambda.Parameters.Count;
            expression = lambda.Body;
        }

        return count;
    }

    private Type GetBranchResultType(TyFun funcType, int consumedParameterCount)
    {
        Type current = _substitution.Apply(funcType);
        var remaining = consumedParameterCount;
        while (current is TyFun function)
        {
            if (remaining < function.Params.Count)
            {
                return new TyFun
                {
                    Params = function.Params.Skip(remaining).Select(_substitution.Apply).ToList(),
                    Result = _substitution.Apply(function.Result),
                    Effects = (EffectRow)_substitution.Apply(function.Effects)
                };
            }

            remaining -= function.Params.Count;
            current = _substitution.Apply(function.Result);
            if (remaining == 0)
            {
                return current;
            }
        }

        return current;
    }
}
