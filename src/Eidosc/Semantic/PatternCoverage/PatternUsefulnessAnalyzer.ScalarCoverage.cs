using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic.PatternCoverage;

internal static partial class PatternUsefulnessAnalyzer
{
    private static bool TryBuildScalarCoverageDomain(
        IReadOnlyList<PatternUsefulnessBranchFact> branches,
        out ScalarCoverageKind kind,
        out List<ScalarCoverageCase> domainCases)
    {
        kind = ScalarCoverageKind.None;
        domainCases = [];
        var discoveredCases = new Dictionary<string, ScalarCoverageCase>(StringComparer.Ordinal);

        for (var i = 0; i < branches.Count; i++)
        {
            CollectScalarLiteralDomainCases(branches[i].Pattern, ref kind, discoveredCases);
        }

        if (kind == ScalarCoverageKind.None || discoveredCases.Count == 0)
        {
            return false;
        }

        foreach (var @case in discoveredCases.Values.OrderBy(@case => @case.Key, StringComparer.Ordinal))
        {
            domainCases.Add(@case);
        }

        domainCases.Add(new ScalarCoverageCase(
            Key: $"scalar:{kind.ToString().ToLowerInvariant()}:other",
            DisplayText: "other",
            IsOther: true));
        return true;
    }

    private static void CollectScalarLiteralDomainCases(
        Pattern pattern,
        ref ScalarCoverageKind kind,
        IDictionary<string, ScalarCoverageCase> cases)
    {
        switch (pattern)
        {
            case LiteralPattern literalPattern:
                if (!TryCreateScalarCoverageCase(literalPattern, out var literalKind, out var literalCase))
                {
                    return;
                }

                if (kind == ScalarCoverageKind.None)
                {
                    kind = literalKind;
                }
                else if (kind != literalKind)
                {
                    kind = ScalarCoverageKind.None;
                    cases.Clear();
                    return;
                }

                cases[literalCase.Key] = literalCase;
                return;

            case CtorPattern ctorPattern:
                foreach (var positional in ctorPattern.PositionalPatterns)
                {
                    CollectScalarLiteralDomainCases(positional, ref kind, cases);
                }

                foreach (var named in ctorPattern.NamedPatterns)
                {
                    if (named.Pattern != null)
                    {
                        CollectScalarLiteralDomainCases(named.Pattern, ref kind, cases);
                    }
                }
                return;

            case TuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    CollectScalarLiteralDomainCases(element, ref kind, cases);
                }
                return;

            case ListPattern listPattern:
                foreach (var element in listPattern.Elements)
                {
                    CollectScalarLiteralDomainCases(element, ref kind, cases);
                }

                if (listPattern.RestPattern != null)
                {
                    CollectScalarLiteralDomainCases(listPattern.RestPattern, ref kind, cases);
                }

                foreach (var element in listPattern.SuffixElements)
                {
                    CollectScalarLiteralDomainCases(element, ref kind, cases);
                }
                return;

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    CollectScalarLiteralDomainCases(alternative, ref kind, cases);
                }
                return;

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    CollectScalarLiteralDomainCases(conjunct, ref kind, cases);
                }
                return;

            case NotPattern { InnerPattern: not null } notPattern:
                CollectScalarLiteralDomainCases(notPattern.InnerPattern, ref kind, cases);
                return;

            case ViewPattern { InnerPattern: not null } viewPattern:
                CollectScalarLiteralDomainCases(viewPattern.InnerPattern, ref kind, cases);
                return;

            case AsPattern asPattern when asPattern.InnerPattern != null:
                CollectScalarLiteralDomainCases(asPattern.InnerPattern, ref kind, cases);
                return;

            default:
                return;
        }
    }

    private static bool TryGetExactScalarCases(
        Pattern pattern,
        ScalarCoverageKind kind,
        IReadOnlyList<ScalarCoverageCase> domainCases,
        SymbolTable symbolTable,
        out HashSet<ScalarCoverageCase> cases)
    {
        cases = new HashSet<ScalarCoverageCase>(ScalarCoverageCaseComparer.Instance);

        switch (pattern)
        {
            case WildcardPattern:
            case VarPattern:
                cases.UnionWith(domainCases);
                return true;

            case LiteralPattern literalPattern:
                if (!TryCreateScalarCoverageCase(literalPattern, out var literalKind, out var literalCase) ||
                    literalKind != kind)
                {
                    return false;
                }

                cases.Add(literalCase);
                return true;

            case AsPattern { InnerPattern: null }:
                cases.UnionWith(domainCases);
                return true;

            case AsPattern { InnerPattern: not null } asPattern:
                return TryGetExactScalarCases(asPattern.InnerPattern, kind, domainCases, symbolTable, out cases);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    if (!TryGetExactScalarCases(alternative, kind, domainCases, symbolTable, out var alternativeCases))
                    {
                        cases = [];
                        return false;
                    }

                    cases.UnionWith(alternativeCases);
                }

                return true;

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                HashSet<ScalarCoverageCase>? intersection = null;
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    if (!TryGetExactScalarCases(conjunct, kind, domainCases, symbolTable, out var conjunctCases))
                    {
                        cases = [];
                        return false;
                    }

                    if (intersection == null)
                    {
                        intersection = new HashSet<ScalarCoverageCase>(conjunctCases, ScalarCoverageCaseComparer.Instance);
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

                cases = intersection;
                return true;
            }

            case NotPattern { InnerPattern: not null } notPattern:
                if (!TryGetExactScalarCases(notPattern.InnerPattern, kind, domainCases, symbolTable, out var innerCases))
                {
                    return false;
                }

                foreach (var domainCase in domainCases)
                {
                    if (!innerCases.Contains(domainCase))
                    {
                        cases.Add(domainCase);
                    }
                }

                return true;

            case ViewPattern viewPattern:
                if (IsPatternIrrefutableForFiniteCoverage(viewPattern.InnerPattern))
                {
                    cases.UnionWith(domainCases);
                    return true;
                }

                if (!IsTransparentScalarView(viewPattern.ViewExpression, symbolTable) ||
                    viewPattern.InnerPattern == null)
                {
                    return false;
                }

                return TryGetExactScalarCases(viewPattern.InnerPattern, kind, domainCases, symbolTable, out cases);

            default:
                return false;
        }
    }

    private static bool TryCreateScalarCoverageCase(
        LiteralPattern literalPattern,
        out ScalarCoverageKind kind,
        out ScalarCoverageCase @case)
    {
        kind = ScalarCoverageKind.None;
        @case = default;

        switch (literalPattern.Type)
        {
            case LiteralType.Integer when literalPattern.Value is int intValue:
                kind = ScalarCoverageKind.Int;
                @case = new ScalarCoverageCase($"scalar:int:{intValue}", intValue.ToString());
                return true;

            case LiteralType.Integer when literalPattern.Value is long longValue:
                kind = ScalarCoverageKind.Int;
                @case = new ScalarCoverageCase($"scalar:int:{longValue}", longValue.ToString());
                return true;

            case LiteralType.Float when literalPattern.Value != null:
            {
                var text = literalPattern.Value.ToString() ?? "0.0";
                kind = ScalarCoverageKind.Float;
                @case = new ScalarCoverageCase($"scalar:float:{text}", text);
                return true;
            }

            case LiteralType.String when literalPattern.Value is string stringValue:
            {
                var text = $"\"{stringValue.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
                kind = ScalarCoverageKind.String;
                @case = new ScalarCoverageCase($"scalar:string:{text}", text);
                return true;
            }

            case LiteralType.Char when literalPattern.Value is char charValue:
            {
                var text = charValue switch
                {
                    '\\' => "'\\\\'",
                    '\'' => "'\\''",
                    '\n' => "'\\n'",
                    '\r' => "'\\r'",
                    '\t' => "'\\t'",
                    '\0' => "'\\0'",
                    _ => $"'{charValue}'"
                };
                kind = ScalarCoverageKind.Char;
                @case = new ScalarCoverageCase($"scalar:char:{(int)charValue}", text);
                return true;
            }

            default:
                return false;
        }
    }

    private static bool IsTransparentScalarView(
        global::Eidosc.Ast.EidosAstNode? viewExpression,
        SymbolTable symbolTable)
    {
        if (viewExpression is Ast.Expressions.LambdaExpr lambdaExpr &&
            lambdaExpr.Parameters.Count == 1 &&
            lambdaExpr.Parameters[0] is VarPattern { Name.Length: > 0 } parameter &&
            lambdaExpr.Body is Ast.Expressions.IdentifierExpr identifierExpr)
        {
            return string.Equals(parameter.Name, identifierExpr.Name, StringComparison.Ordinal);
        }

        return false;
    }
}
