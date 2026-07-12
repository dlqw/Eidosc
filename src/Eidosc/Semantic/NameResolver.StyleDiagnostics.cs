using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void WarnIfFunctionBranchUsesRedundantMatch(PatternBranch branch)
    {
        if (branch.Pattern == null ||
            branch.Guard != null ||
            branch.Expression is not MatchExpr match ||
            match.MatchedExpression == null ||
            !TryCollectSimpleBindingNames(branch.Pattern, out var bindingNames) ||
            !MatchesBindingNames(match.MatchedExpression, bindingNames))
        {
            return;
        }

        AddWarning(
            match.Span,
            "Redundant match over function parameter; use function body pattern branches directly.",
            RedundantFunctionBodyMatchWarningCode,
            "this match repeats the function parameter pattern",
            "Rewrite the match branches as function body branches, for example `Some(v) => v` instead of `x => match x { Some(v) => v }`.");
    }

    private static bool TryCollectSimpleBindingNames(Pattern pattern, out List<string> names)
    {
        names = [];
        return CollectSimpleBindingNames(pattern, names) && names.Count > 0;
    }

    private static bool CollectSimpleBindingNames(Pattern pattern, List<string> names)
    {
        switch (pattern)
        {
            case VarPattern { BindingMode: PatternBindingMode.ByValue } varPattern
                when !string.IsNullOrWhiteSpace(varPattern.Name):
                names.Add(varPattern.Name);
                return true;

            case TuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    if (!CollectSimpleBindingNames(element, names))
                    {
                        return false;
                    }
                }

                return tuplePattern.Elements.Count > 0;

            default:
                return false;
        }
    }

    private static bool MatchesBindingNames(EidosAstNode matchedExpression, IReadOnlyList<string> names)
    {
        if (names.Count == 1)
        {
            return matchedExpression is IdentifierExpr identifier &&
                   string.Equals(identifier.Name, names[0], StringComparison.Ordinal);
        }

        if (matchedExpression is not TupleExpr tuple ||
            tuple.Elements.Count != names.Count)
        {
            return false;
        }

        for (var i = 0; i < tuple.Elements.Count; i++)
        {
            if (tuple.Elements[i] is not IdentifierExpr identifier ||
                !string.Equals(identifier.Name, names[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}
