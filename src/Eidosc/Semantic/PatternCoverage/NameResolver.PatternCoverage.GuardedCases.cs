using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static bool TryCollectTupleBoolCoverageFromGuardedPattern(
        Pattern pattern,
        EidosAstNode? guardExpression,
        ISet<string> tupleCoverageCases,
        out int arity)
    {
        arity = 0;
        var exactCases = new HashSet<string>(StringComparer.Ordinal);
        if (!TryGetExactTupleBoolPatternCases(pattern, out arity, exactCases) || arity <= 0)
        {
            return false;
        }

        var tupleArity = arity;
        return TryCollectExactGuardedCoverageCases(
            OrderTupleBoolCoverageCases(exactCases),
            tupleCoverageCases,
            tupleCase => EvaluateGuardTruthForTupleCoverageCase(pattern, tupleCase, tupleArity, guardExpression));
    }

    private static bool TryCollectListCoverageFromGuardedPattern(
        Pattern pattern,
        EidosAstNode? guardExpression,
        ISet<ListCoverageCase> listCoverageCases)
    {
        if (!PatternUsefulnessAnalyzer.TryGetExactListCoverageCases(
                pattern,
                out var exactCases,
                preferBoolVectorSplit: true))
        {
            return false;
        }

        var intDomainsByIndex = CollectListIntDomainsByIndex(exactCases);
        return TryCollectExactGuardedCoverageCases(
            OrderListCoverageCasesForGuardedCoverage(exactCases),
            listCoverageCases,
            listCase => EvaluateGuardTruthForListCoverageCase(
                pattern,
                listCase,
                guardExpression,
                intDomainsByIndex));
    }

    private static GuardTruth EvaluateGuardTruthForBoolCoverageCase(
        Pattern pattern,
        bool boolCoverageCase,
        EidosAstNode? guardExpression)
    {
        if (EvaluateBoolPatternDeterministicNonViewTruth(pattern, boolCoverageCase) is not DeterministicNonViewMatchTruth.Match ||
            !TryCollectDeterministicBoolBindingsForTargetValues(
                pattern,
                [boolCoverageCase],
                out var knownBoolBindings) ||
            !TryCollectKnownIntBindingsFromPattern(pattern, out var knownIntBindings))
        {
            return GuardTruth.Unknown;
        }

        return EvaluateGuardTruthWithBindings(
            guardExpression,
            knownBoolBindings,
            knownIntBindings);
    }

    private static GuardTruth EvaluateGuardTruthForTupleCoverageCase(
        Pattern pattern,
        string tupleCase,
        int arity,
        EidosAstNode? guardExpression)
    {
        if (!TryParseTupleBoolWitness(tupleCase, arity, out var tupleValues))
        {
            return GuardTruth.False;
        }

        if (!TryMatchTupleBoolPatternWithBindings(pattern, tupleValues, out var bindings))
        {
            return GuardTruth.False;
        }

        return EvaluateGuardTruth(
            guardExpression,
            identifier => bindings.TryGetValue(identifier, out var value)
                ? ToGuardTruth(value)
                : GuardTruth.Unknown);
    }

    private static GuardTruth EvaluateGuardTruthForTupleBoolCoverageCase(
        Pattern pattern,
        IReadOnlyList<bool> tupleValues,
        EidosAstNode? guardExpression)
    {
        if (EvaluateTupleBoolPatternDeterministicNonViewTruth(pattern, tupleValues) is not DeterministicNonViewMatchTruth.Match ||
            !TryCollectKnownGuardBindings(pattern, out var knownBoolBindings, out var knownIntBindings) ||
            !TryMatchTupleBoolPatternWithBindings(pattern, tupleValues, out var tupleBindings) ||
            !TryMergeKnownBoolBindings(knownBoolBindings, tupleBindings, out knownBoolBindings))
        {
            return GuardTruth.Unknown;
        }

        return EvaluateGuardTruthWithBindings(
            guardExpression,
            knownBoolBindings,
            knownIntBindings);
    }

    private static GuardTruth EvaluateGuardTruthForListCoverageCase(
        Pattern pattern,
        ListCoverageCase listCoverageCase,
        EidosAstNode? guardExpression,
        IReadOnlyDictionary<int, IReadOnlySet<long>> intDomainsByIndex)
    {
        if (!TryCollectKnownGuardBindings(pattern, out var knownBoolBindings, out var knownIntBindings))
        {
            return GuardTruth.Unknown;
        }

        if (TryParseListCaseBoolVector(listCoverageCase, out var boolValues))
        {
            if (!TryMatchListPatternWithBoolVectorBindings(pattern, boolValues, out var vectorBindings) ||
                !TryMergeKnownBoolBindings(knownBoolBindings, vectorBindings, out knownBoolBindings))
            {
                return GuardTruth.Unknown;
            }
        }
        else if (TryParseListCaseIntVector(listCoverageCase, out var intTokens))
        {
            if (!TryMatchListPatternWithIntVectorBindings(
                    pattern,
                    intTokens,
                    intDomainsByIndex,
                    out var vectorIntBindings) ||
                !TryMergeKnownIntBindings(knownIntBindings, vectorIntBindings, out knownIntBindings))
            {
                return GuardTruth.Unknown;
            }
        }
        else if (!string.IsNullOrWhiteSpace(listCoverageCase.BoolVectorKey))
        {
            return GuardTruth.Unknown;
        }

        return EvaluateGuardTruthWithBindings(
            guardExpression,
            knownBoolBindings,
            knownIntBindings);
    }

    private bool TryCollectAdtCoverageFromGuardedPattern(
        Pattern pattern,
        EidosAstNode? guardExpression,
        ISet<SymbolId> coveredConstructors,
        out SymbolId adtId)
    {
        adtId = SymbolId.None;
        if (!PatternUsefulnessAnalyzer.TryGetExactAdtConstructorCases(
                pattern,
                _symbolTable,
                out var resolvedAdt,
                out var constructorIds) ||
            !resolvedAdt.IsValid)
        {
            return false;
        }

        adtId = resolvedAdt;
        return TryCollectExactGuardedCoverageCases(
            OrderConstructorIdsByValue(constructorIds),
            coveredConstructors,
            ctorId => EvaluateGuardTruthForAdtConstructor(pattern, ctorId, guardExpression));
    }

    private GuardTruth EvaluateGuardTruthForAdtConstructor(
        Pattern pattern,
        SymbolId constructorId,
        EidosAstNode? guardExpression)
    {
        if (!TryMatchAdtConstructorPatternWithKnownBindings(
                pattern,
                constructorId,
                out var knownBoolBindings,
                out var knownIntBindings))
        {
            return GuardTruth.Unknown;
        }

        return EvaluateGuardTruthWithBindings(
            guardExpression,
            knownBoolBindings,
            knownIntBindings);
    }

    private static bool TryCollectKnownGuardBindings(
        Pattern pattern,
        out Dictionary<string, bool> knownBoolBindings,
        out Dictionary<string, ListGuardIntBinding> knownIntBindings)
    {
        knownBoolBindings = [];
        knownIntBindings = [];
        return TryCollectKnownBoolBindingsFromPattern(pattern, out knownBoolBindings) &&
               TryCollectKnownIntBindingsFromPattern(pattern, out knownIntBindings);
    }

    private static IEnumerable<string> OrderTupleBoolCoverageCases(IEnumerable<string> tupleCoverageCases)
    {
        return tupleCoverageCases.OrderBy(value => value, StringComparer.Ordinal);
    }

    private static IEnumerable<ListCoverageCase> OrderListCoverageCasesForGuardedCoverage(
        IEnumerable<ListCoverageCase> listCoverageCases)
    {
        return listCoverageCases
            .OrderBy(@case => @case.IsAtLeast)
            .ThenBy(@case => @case.Length)
            .ThenBy(@case => @case.BoolVectorKey, StringComparer.Ordinal);
    }

    private static IEnumerable<SymbolId> OrderConstructorIdsByValue(IEnumerable<SymbolId> constructorIds)
    {
        return constructorIds.OrderBy(id => id.Value);
    }

    private static bool TryCollectExactGuardedCoverageCases<TCase>(
        IEnumerable<TCase> exactCases,
        ISet<TCase> coveredCases,
        Func<TCase, GuardTruth> evaluateGuardTruth)
    {
        var hasUnknownGuardCase = false;
        foreach (var exactCase in exactCases)
        {
            var truth = evaluateGuardTruth(exactCase);
            if (truth is GuardTruth.True)
            {
                coveredCases.Add(exactCase);
            }
            else if (truth is GuardTruth.Unknown)
            {
                hasUnknownGuardCase = true;
            }
        }

        // Unknown guard truth on any finite case means the coverage result is only a lower bound.
        return !hasUnknownGuardCase;
    }
}
