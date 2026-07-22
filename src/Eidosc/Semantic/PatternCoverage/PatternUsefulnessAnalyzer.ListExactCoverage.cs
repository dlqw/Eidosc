using Eidosc.Symbols;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic.PatternCoverage;

internal static partial class PatternUsefulnessAnalyzer
{
    private readonly record struct ListAdtTokenCandidate(SymbolId ConstructorId, ScalarCoverageCase? FieldCase);

    internal static bool TryGetListCoverageHintCases(
        Pattern pattern,
        out IReadOnlyList<string> cases)
    {
        if (!TryGetExactListCoverageCases(pattern, out var exactCases))
        {
            cases = [];
            return false;
        }

        cases = exactCases
            .OrderBy(@case => @case, ListLengthCaseComparer.Instance)
            .Select(FormatListLengthCaseDisplay)
            .ToList();
        return true;
    }

    internal static bool TryGetExactListCoverageCases(
        Pattern pattern,
        out IReadOnlyList<ListCoverageCase> cases,
        bool preferBoolVectorSplit = false)
    {
        if (!TryGetListPatternMaxPrefixLength(pattern, out var maxTrackedLength))
        {
            cases = [];
            return false;
        }

        var boolSplitLengths = new HashSet<int>();
        var enableBoolElementSplits = false;
        if (TryDetermineListBoolSplitLengths(
            [pattern],
            maxTrackedLength,
            out var constrainedSplitLengths))
        {
            enableBoolElementSplits = true;
            boolSplitLengths.UnionWith(constrainedSplitLengths);
        }

        if (preferBoolVectorSplit &&
            TryDetermineListBoolSplitLengthsForGuard(
                [pattern],
                maxTrackedLength,
                out var guardSplitLengths))
        {
            enableBoolElementSplits = true;
            boolSplitLengths.UnionWith(guardSplitLengths);
        }

        var enableIntElementSplits = TryDetermineListIntSplitDomains(
            [pattern],
            maxTrackedLength,
            out var intSplitDomainsByLength);

        var matchedCases = new HashSet<ListCoverageCase>();
        if (!TryCollectExactListLengthCases(
                pattern,
                maxTrackedLength,
                boolSplitLengths,
                enableBoolElementSplits,
                intSplitDomainsByLength,
                enableIntElementSplits,
                adtSplitDomainsByLength: new Dictionary<int, IReadOnlyList<string>>(),
                enableAdtElementSplits: false,
                symbolTable: null!,
                matchedCases))
        {
            cases = [];
            return false;
        }

        cases = matchedCases
            .OrderBy(@case => @case, ListLengthCaseComparer.Instance)
            .ToList();
        return true;
    }

    private static bool TryCollectExactListLengthCases(
        Pattern pattern,
        int maxTrackedLength,
        IReadOnlySet<int> boolSplitLengths,
        bool enableBoolElementSplits,
        IReadOnlyDictionary<int, IReadOnlyList<string>> intSplitDomainsByLength,
        bool enableIntElementSplits,
        IReadOnlyDictionary<int, IReadOnlyList<string>> adtSplitDomainsByLength,
        bool enableAdtElementSplits,
        SymbolTable symbolTable,
        ISet<ListCoverageCase> cases)
    {
        if (maxTrackedLength < 0)
        {
            return false;
        }

        switch (pattern)
        {
            case ListPattern listPattern:
                return AddListLengthCases(
                    listPattern,
                    maxTrackedLength,
                    boolSplitLengths,
                    enableBoolElementSplits,
                    intSplitDomainsByLength,
                    enableIntElementSplits,
                    adtSplitDomainsByLength,
                    enableAdtElementSplits,
                    symbolTable,
                    cases);

            case TuplePattern tuplePattern when TryProjectTupleListCoveragePattern(tuplePattern, out var projectedListPattern):
                return TryCollectExactListLengthCases(
                    projectedListPattern,
                    maxTrackedLength,
                    boolSplitLengths,
                    enableBoolElementSplits,
                    intSplitDomainsByLength,
                    enableIntElementSplits,
                    adtSplitDomainsByLength,
                    enableAdtElementSplits,
                    symbolTable,
                    cases);

            case AsPattern { InnerPattern: not null } asPattern:
                return TryCollectExactListLengthCases(
                    asPattern.InnerPattern,
                    maxTrackedLength,
                    boolSplitLengths,
                    enableBoolElementSplits,
                    intSplitDomainsByLength,
                    enableIntElementSplits,
                    adtSplitDomainsByLength,
                    enableAdtElementSplits,
                    symbolTable,
                    cases);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var mergedCases = new HashSet<ListCoverageCase>();
                foreach (var alternative in orPattern.Alternatives)
                {
                    var alternativeCases = new HashSet<ListCoverageCase>();
                    if (!TryCollectExactListLengthCases(
                            alternative,
                            maxTrackedLength,
                            boolSplitLengths,
                            enableBoolElementSplits,
                            intSplitDomainsByLength,
                            enableIntElementSplits,
                            adtSplitDomainsByLength,
                            enableAdtElementSplits,
                            symbolTable,
                            alternativeCases))
                    {
                        return false;
                    }

                    mergedCases.UnionWith(alternativeCases);
                }

                foreach (var matchedCase in mergedCases)
                {
                    cases.Add(matchedCase);
                }

                return true;
            }

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                HashSet<ListCoverageCase>? intersectionCases = null;
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    var conjunctCases = new HashSet<ListCoverageCase>();
                    if (!TryCollectExactListLengthCases(
                            conjunct,
                            maxTrackedLength,
                            boolSplitLengths,
                            enableBoolElementSplits,
                            intSplitDomainsByLength,
                            enableIntElementSplits,
                            adtSplitDomainsByLength,
                            enableAdtElementSplits,
                            symbolTable,
                            conjunctCases))
                    {
                        return false;
                    }

                    if (intersectionCases == null)
                    {
                        intersectionCases = conjunctCases;
                    }
                    else
                    {
                        intersectionCases.IntersectWith(conjunctCases);
                    }
                }

                if (intersectionCases == null)
                {
                    return false;
                }

                foreach (var matchedCase in intersectionCases)
                {
                    cases.Add(matchedCase);
                }

                return true;
            }

            case NotPattern { InnerPattern: not null } notPattern:
            {
                var innerCases = new HashSet<ListCoverageCase>();
                if (!TryCollectExactListLengthCases(
                        notPattern.InnerPattern,
                        maxTrackedLength,
                        boolSplitLengths,
                        enableBoolElementSplits,
                        intSplitDomainsByLength,
                        enableIntElementSplits,
                        adtSplitDomainsByLength,
                        enableAdtElementSplits,
                        symbolTable,
                        innerCases))
                {
                    return false;
                }

                var universe = BuildListLengthUniverse(
                    maxTrackedLength,
                    boolSplitLengths,
                    enableBoolElementSplits,
                    intSplitDomainsByLength,
                    enableIntElementSplits,
                    adtSplitDomainsByLength,
                    enableAdtElementSplits,
                    maxTrackedLength + 1);
                for (var i = 0; i < universe.Count; i++)
                {
                    var universeCase = universe[i];
                    if (!innerCases.Contains(universeCase))
                    {
                        cases.Add(universeCase);
                    }
                }

                return true;
            }

            default:
                return false;
        }
    }

    private static bool AddListLengthCases(
        ListPattern listPattern,
        int maxTrackedLength,
        IReadOnlySet<int> boolSplitLengths,
        bool enableBoolElementSplits,
        IReadOnlyDictionary<int, IReadOnlyList<string>> intSplitDomainsByLength,
        bool enableIntElementSplits,
        IReadOnlyDictionary<int, IReadOnlyList<string>> adtSplitDomainsByLength,
        bool enableAdtElementSplits,
        SymbolTable symbolTable,
        ISet<ListCoverageCase> cases)
    {
        if (listPattern.SuffixElements.Count > 0)
        {
            return false;
        }

        var minLength = listPattern.Elements.Count;
        var hasRest = listPattern.HasRestMarker;
        if (minLength < 0)
        {
            return false;
        }

        if (HasUntrackedRefutableListElementPattern(
                listPattern,
                boolSplitLengths,
                enableBoolElementSplits,
                intSplitDomainsByLength,
                enableIntElementSplits,
                adtSplitDomainsByLength,
                enableAdtElementSplits,
                symbolTable))
        {
            return false;
        }

        IReadOnlyList<IReadOnlyList<bool>> elementBoolCases = [];
        var hasBoolElementSets = enableBoolElementSplits &&
                                 TryCollectListElementBoolCaseSets(
                                     listPattern.Elements,
                                     out elementBoolCases);
        var hasConstrainedBoolElements = hasBoolElementSets &&
                                         elementBoolCases.Any(elementCases => elementCases.Count < 2);
        var hasConstrainedIntElements = false;

        if (hasRest)
        {
            for (var length = minLength; length <= maxTrackedLength; length++)
            {
                IReadOnlyList<IReadOnlyList<string>> elementIntTokenCases = [];
                var useIntSplitForPattern = false;
                IReadOnlyList<IReadOnlyList<string>> elementAdtTokenCases = [];
                var useAdtSplitForPattern = false;
                if (enableAdtElementSplits &&
                    adtSplitDomainsByLength.TryGetValue(length, out var adtDomainTokens) &&
                    adtDomainTokens != null)
                {
                    if (!TryCollectListElementAdtTokenCaseSets(
                            listPattern.Elements,
                            adtDomainTokens,
                            symbolTable,
                            out elementAdtTokenCases))
                    {
                        return false;
                    }

                    useAdtSplitForPattern = true;
                }

                if (enableIntElementSplits &&
                    !useAdtSplitForPattern &&
                    intSplitDomainsByLength.TryGetValue(length, out var intDomainTokens) &&
                    intDomainTokens != null)
                {
                    if (!TryCollectListElementIntTokenCaseSets(
                            listPattern.Elements,
                            intDomainTokens,
                            out elementIntTokenCases))
                    {
                        return false;
                    }

                    useIntSplitForPattern = true;
                    hasConstrainedIntElements |= elementIntTokenCases.Any(elementCases =>
                        elementCases.Count < intDomainTokens.Count);
                }

                if (!AddListCasesForLength(
                    length,
                    boolSplitLengths,
                    hasBoolElementSets,
                    elementBoolCases,
                    intSplitDomainsByLength,
                    hasIntElementSets: useIntSplitForPattern,
                    elementIntTokenCases,
                    useIntSplitForPattern,
                    adtSplitDomainsByLength,
                    hasAdtElementSets: useAdtSplitForPattern,
                    elementAdtTokenCases,
                    useAdtSplitForPattern,
                    requireSplitExactness: false,
                    cases))
                {
                    return false;
                }
            }

            if (maxTrackedLength >= minLength &&
                enableAdtElementSplits &&
                adtSplitDomainsByLength.TryGetValue(maxTrackedLength, out var overflowAdtDomainTokens) &&
                overflowAdtDomainTokens != null)
            {
                if (!TryCollectListElementAdtTokenCaseSets(
                        listPattern.Elements,
                        overflowAdtDomainTokens,
                        symbolTable,
                        out var overflowElementAdtTokenCases))
                {
                    return false;
                }

                AddListAtLeastTokenCasesForLength(
                    maxTrackedLength,
                    maxTrackedLength + 1,
                    overflowAdtDomainTokens,
                    overflowElementAdtTokenCases,
                    cases);
                return true;
            }

            if (!hasConstrainedBoolElements && !hasConstrainedIntElements)
            {
                cases.Add(new ListCoverageCase(IsAtLeast: true, Length: maxTrackedLength + 1));
            }

            return true;
        }

        IReadOnlyList<IReadOnlyList<string>> fixedLengthIntTokenCases = [];
        var useIntSplitForFixedLength = false;
        IReadOnlyList<IReadOnlyList<string>> fixedLengthAdtTokenCases = [];
        var useAdtSplitForFixedLength = false;
        if (enableAdtElementSplits &&
            adtSplitDomainsByLength.TryGetValue(minLength, out var fixedLengthAdtDomainTokens) &&
            fixedLengthAdtDomainTokens != null)
        {
            if (!TryCollectListElementAdtTokenCaseSets(
                    listPattern.Elements,
                    fixedLengthAdtDomainTokens,
                    symbolTable,
                    out fixedLengthAdtTokenCases))
            {
                return false;
            }

            useAdtSplitForFixedLength = true;
        }

        if (enableIntElementSplits &&
            !useAdtSplitForFixedLength &&
            intSplitDomainsByLength.TryGetValue(minLength, out var fixedLengthIntDomainTokens) &&
            fixedLengthIntDomainTokens != null)
        {
            if (!TryCollectListElementIntTokenCaseSets(
                    listPattern.Elements,
                    fixedLengthIntDomainTokens,
                    out fixedLengthIntTokenCases))
            {
                return false;
            }

            useIntSplitForFixedLength = true;
        }

        return AddListCasesForLength(
            minLength,
            boolSplitLengths,
            hasBoolElementSets,
            elementBoolCases,
            intSplitDomainsByLength,
            hasIntElementSets: useIntSplitForFixedLength,
            fixedLengthIntTokenCases,
            useIntSplitForPattern: useIntSplitForFixedLength,
            adtSplitDomainsByLength,
            hasAdtElementSets: useAdtSplitForFixedLength,
            fixedLengthAdtTokenCases,
            useAdtSplitForPattern: useAdtSplitForFixedLength,
            requireSplitExactness: true,
            cases);
    }

    private static bool AddListCasesForLength(
        int length,
        IReadOnlySet<int> boolSplitLengths,
        bool hasBoolElementSets,
        IReadOnlyList<IReadOnlyList<bool>> elementBoolCases,
        IReadOnlyDictionary<int, IReadOnlyList<string>> intSplitDomainsByLength,
        bool hasIntElementSets,
        IReadOnlyList<IReadOnlyList<string>> elementIntTokenCases,
        bool useIntSplitForPattern,
        IReadOnlyDictionary<int, IReadOnlyList<string>> adtSplitDomainsByLength,
        bool hasAdtElementSets,
        IReadOnlyList<IReadOnlyList<string>> elementAdtTokenCases,
        bool useAdtSplitForPattern,
        bool requireSplitExactness,
        ISet<ListCoverageCase> output)
    {
        if (boolSplitLengths.Contains(length))
        {
            if (!hasBoolElementSets)
            {
                if (requireSplitExactness)
                {
                    return false;
                }

                output.Add(new ListCoverageCase(IsAtLeast: false, Length: length));
                return true;
            }

            var valueSets = new List<IReadOnlyList<bool>>(length);
            for (var index = 0; index < length; index++)
            {
                if (hasBoolElementSets && index < elementBoolCases.Count)
                {
                    valueSets.Add(elementBoolCases[index]);
                    continue;
                }

                valueSets.Add([false, true]);
            }

            BuildListBoolVectorCases(valueSets, 0, new List<bool>(length), output, length);
            return true;
        }

        if (useAdtSplitForPattern &&
            adtSplitDomainsByLength.TryGetValue(length, out var adtDomainTokens))
        {
            var valueSets = new List<IReadOnlyList<string>>(length);
            for (var index = 0; index < length; index++)
            {
                if (hasAdtElementSets && index < elementAdtTokenCases.Count)
                {
                    valueSets.Add(elementAdtTokenCases[index]);
                    continue;
                }

                valueSets.Add(adtDomainTokens);
            }

            BuildListTokenVectorCases(valueSets, 0, new List<string>(length), output, length);
            return true;
        }

        if (adtSplitDomainsByLength.ContainsKey(length))
        {
            if (requireSplitExactness)
            {
                return false;
            }

            output.Add(new ListCoverageCase(IsAtLeast: false, Length: length));
            return true;
        }

        if (useIntSplitForPattern &&
            intSplitDomainsByLength.TryGetValue(length, out var intDomainTokens))
        {
            var valueSets = new List<IReadOnlyList<string>>(length);
            for (var index = 0; index < length; index++)
            {
                if (hasIntElementSets && index < elementIntTokenCases.Count)
                {
                    valueSets.Add(elementIntTokenCases[index]);
                    continue;
                }

                valueSets.Add(intDomainTokens);
            }

            BuildListTokenVectorCases(valueSets, 0, new List<string>(length), output, length);
            return true;
        }

        if (intSplitDomainsByLength.ContainsKey(length))
        {
            if (requireSplitExactness)
            {
                return false;
            }

            output.Add(new ListCoverageCase(IsAtLeast: false, Length: length));
            return true;
        }

        output.Add(new ListCoverageCase(IsAtLeast: false, Length: length));
        return true;
    }

    private static void BuildListTokenVectorCases(
        IReadOnlyList<IReadOnlyList<string>> valueSets,
        int index,
        List<string> current,
        ICollection<ListCoverageCase> output,
        int length)
    {
        if (index >= valueSets.Count)
        {
            output.Add(new ListCoverageCase(
                IsAtLeast: false,
                Length: length,
                BoolVectorKey: EncodeTokenVector(current)));
            return;
        }

        var values = valueSets[index];
        for (var i = 0; i < values.Count; i++)
        {
            current.Add(values[i]);
            BuildListTokenVectorCases(valueSets, index + 1, current, output, length);
            current.RemoveAt(current.Count - 1);
        }
    }

    private static void AddListAtLeastTokenCasesForLength(
        int trackedLength,
        int overflowStartLength,
        IReadOnlyList<string> domainTokens,
        IReadOnlyList<IReadOnlyList<string>> elementTokenCases,
        ISet<ListCoverageCase> output)
    {
        var valueSets = new List<IReadOnlyList<string>>(trackedLength);
        for (var index = 0; index < trackedLength; index++)
        {
            if (index < elementTokenCases.Count)
            {
                valueSets.Add(elementTokenCases[index]);
                continue;
            }

            valueSets.Add(domainTokens);
        }

        BuildListAtLeastTokenVectorCases(
            valueSets,
            0,
            new List<string>(trackedLength),
            output,
            overflowStartLength);
    }

    private static void BuildListAtLeastTokenVectorCases(
        IReadOnlyList<IReadOnlyList<string>> valueSets,
        int index,
        List<string> current,
        ICollection<ListCoverageCase> output,
        int overflowStartLength)
    {
        if (index >= valueSets.Count)
        {
            output.Add(new ListCoverageCase(
                IsAtLeast: true,
                Length: overflowStartLength,
                BoolVectorKey: EncodeTokenVector(current)));
            return;
        }

        var values = valueSets[index];
        for (var i = 0; i < values.Count; i++)
        {
            current.Add(values[i]);
            BuildListAtLeastTokenVectorCases(valueSets, index + 1, current, output, overflowStartLength);
            current.RemoveAt(current.Count - 1);
        }
    }

    private static void BuildListBoolVectorCases(
        IReadOnlyList<IReadOnlyList<bool>> valueSets,
        int index,
        List<bool> current,
        ICollection<ListCoverageCase> output,
        int length)
    {
        if (index >= valueSets.Count)
        {
            output.Add(new ListCoverageCase(
                IsAtLeast: false,
                Length: length,
                BoolVectorKey: EncodeBoolVector(current)));
            return;
        }

        var values = valueSets[index];
        for (var i = 0; i < values.Count; i++)
        {
            current.Add(values[i]);
            BuildListBoolVectorCases(valueSets, index + 1, current, output, length);
            current.RemoveAt(current.Count - 1);
        }
    }

    private static bool TryCollectListElementBoolCaseSets(
        IReadOnlyList<Pattern> elements,
        out IReadOnlyList<IReadOnlyList<bool>> elementBoolCases)
    {
        var collected = new List<IReadOnlyList<bool>>(elements.Count);
        for (var i = 0; i < elements.Count; i++)
        {
            var cases = new HashSet<bool>();
            if (!TryGetExactBoolCases(elements[i], cases))
            {
                elementBoolCases = [];
                return false;
            }

            collected.Add(cases.OrderBy(value => value).ToList());
        }

        elementBoolCases = collected;
        return true;
    }

    private static bool TryCollectListElementIntTokenCaseSets(
        IReadOnlyList<Pattern> elements,
        IReadOnlyList<string> domainTokens,
        out IReadOnlyList<IReadOnlyList<string>> elementTokenCases)
    {
        if (domainTokens.Count == 0)
        {
            elementTokenCases = [];
            return false;
        }

        if (elements.Count == 0)
        {
            elementTokenCases = [];
            return true;
        }

        var collected = new List<IReadOnlyList<string>>(elements.Count);
        for (var i = 0; i < elements.Count; i++)
        {
            if (!TryGetExactIntTokenCases(elements[i], domainTokens, out var tokenCases))
            {
                elementTokenCases = [];
                return false;
            }

            collected.Add(domainTokens.Where(token => tokenCases.Contains(token)).ToList());
        }

        elementTokenCases = collected;
        return true;
    }

    private static bool TryCollectListElementAdtTokenCaseSets(
        IReadOnlyList<Pattern> elements,
        IReadOnlyList<string> domainTokens,
        SymbolTable symbolTable,
        out IReadOnlyList<IReadOnlyList<string>> elementTokenCases)
    {
        if (domainTokens.Count == 0)
        {
            elementTokenCases = [];
            return false;
        }

        if (elements.Count == 0)
        {
            elementTokenCases = [];
            return true;
        }

        var collected = new List<IReadOnlyList<string>>(elements.Count);
        for (var i = 0; i < elements.Count; i++)
        {
            if (!TryGetExactAdtTokenCases(elements[i], domainTokens, symbolTable, out var tokenCases))
            {
                elementTokenCases = [];
                return false;
            }

            collected.Add(domainTokens.Where(token => tokenCases.Contains(token)).ToList());
        }

        elementTokenCases = collected;
        return true;
    }

    private static bool TryGetExactAdtTokenCases(
        Pattern pattern,
        IReadOnlyList<string> domainTokens,
        SymbolTable symbolTable,
        out HashSet<string> tokenCases)
    {
        tokenCases = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < domainTokens.Count; i++)
        {
            var token = domainTokens[i];
            if (!TryParseAdtDomainToken(token, out var constructorId, out var fieldCase, out var isOther) ||
                !TryPatternMatchesAdtDomainToken(
                    pattern,
                    constructorId,
                    fieldCase,
                    isOther,
                    domainTokens,
                    symbolTable,
                    out var matches))
            {
                tokenCases.Clear();
                return false;
            }

            if (matches)
            {
                tokenCases.Add(token);
            }
        }

        return true;
    }

    private static bool TryPatternMatchesAdtDomainToken(
        Pattern pattern,
        SymbolId constructorId,
        ScalarCoverageCase? fieldCase,
        bool isOther,
        IReadOnlyList<string> domainTokens,
        SymbolTable symbolTable,
        out bool matches)
    {
        var truth = EvaluateAdtDomainTokenTruth(
            pattern,
            constructorId,
            fieldCase,
            isOther,
            domainTokens,
            symbolTable);
        matches = truth is FiniteDomainMatchTruth.Match;
        return truth is not FiniteDomainMatchTruth.Unknown;
    }

    private static FiniteDomainMatchTruth EvaluateAdtDomainTokenTruth(
        Pattern pattern,
        SymbolId constructorId,
        ScalarCoverageCase? fieldCase,
        bool isOther,
        IReadOnlyList<string> domainTokens,
        SymbolTable symbolTable)
    {
        switch (pattern)
        {
            case CtorPattern ctorPattern when ctorPattern.SymbolId.IsValid:
                return EvaluateCtorPatternAdtTokenTruth(
                    ctorPattern,
                    constructorId,
                    fieldCase,
                    isOther);

            case WildcardPattern:
            case VarPattern:
                return FiniteDomainMatchTruth.Match;

            case AsPattern asPattern:
                if (asPattern.InnerPattern == null)
                {
                    return FiniteDomainMatchTruth.Match;
                }

                return EvaluateAdtDomainTokenTruth(
                    asPattern.InnerPattern,
                    constructorId,
                    fieldCase,
                    isOther,
                    domainTokens,
                    symbolTable);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var sawUnknown = false;
                foreach (var alternative in orPattern.Alternatives)
                {
                    var altTruth = EvaluateAdtDomainTokenTruth(
                        alternative,
                        constructorId,
                        fieldCase,
                        isOther,
                        domainTokens,
                        symbolTable);
                    if (altTruth is FiniteDomainMatchTruth.Match)
                    {
                        return FiniteDomainMatchTruth.Match;
                    }

                    if (altTruth is FiniteDomainMatchTruth.Unknown)
                    {
                        sawUnknown = true;
                    }
                }

                return sawUnknown
                    ? FiniteDomainMatchTruth.Unknown
                    : FiniteDomainMatchTruth.NoMatch;
            }

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                var sawUnknown = false;
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    var conjunctTruth = EvaluateAdtDomainTokenTruth(
                        conjunct,
                        constructorId,
                        fieldCase,
                        isOther,
                        domainTokens,
                        symbolTable);
                    if (conjunctTruth is FiniteDomainMatchTruth.NoMatch)
                    {
                        return FiniteDomainMatchTruth.NoMatch;
                    }

                    if (conjunctTruth is FiniteDomainMatchTruth.Unknown)
                    {
                        sawUnknown = true;
                    }
                }

                return sawUnknown
                    ? FiniteDomainMatchTruth.Unknown
                    : FiniteDomainMatchTruth.Match;
            }

            case NotPattern { InnerPattern: not null } notPattern:
            {
                var innerTruth = EvaluateAdtDomainTokenTruth(
                    notPattern.InnerPattern,
                    constructorId,
                    fieldCase,
                    isOther,
                    domainTokens,
                    symbolTable);
                return innerTruth switch
                {
                    FiniteDomainMatchTruth.Match => FiniteDomainMatchTruth.NoMatch,
                    FiniteDomainMatchTruth.NoMatch => FiniteDomainMatchTruth.Match,
                    _ => FiniteDomainMatchTruth.Unknown
                };
            }

            case ViewPattern viewPattern:
                if (viewPattern.InnerPattern == null)
                {
                    return FiniteDomainMatchTruth.Match;
                }

                return EvaluateAdtDomainTokenTruth(
                    viewPattern.InnerPattern,
                    constructorId,
                    fieldCase,
                    isOther,
                    domainTokens,
                    symbolTable);

            default:
                return FiniteDomainMatchTruth.Unknown;
        }
    }

    private static FiniteDomainMatchTruth EvaluateCtorPatternAdtTokenTruth(
        CtorPattern pattern,
        SymbolId constructorId,
        ScalarCoverageCase? fieldCase,
        bool isOther)
    {
        if (pattern.SymbolId != constructorId)
        {
            return FiniteDomainMatchTruth.NoMatch;
        }

        if (pattern.PositionalPatterns.Count == 0)
        {
            return FiniteDomainMatchTruth.Match;
        }

        if (pattern.PositionalPatterns.Count != 1 || pattern.NamedPatterns.Count > 0)
        {
            return FiniteDomainMatchTruth.Unknown;
        }

        var fieldPattern = pattern.PositionalPatterns[0];
        if (IsPatternIrrefutableForFiniteCoverage(fieldPattern))
        {
            return FiniteDomainMatchTruth.Match;
        }

        if (!TryCreateScalarCoverageCaseFromPattern(fieldPattern, out var expected))
        {
            return FiniteDomainMatchTruth.Unknown;
        }

        return !isOther && fieldCase is { } actual && string.Equals(actual.Key, expected.Key, StringComparison.Ordinal)
            ? FiniteDomainMatchTruth.Match
            : FiniteDomainMatchTruth.NoMatch;
    }

    private static bool TryGetExactIntTokenCases(
        Pattern pattern,
        IReadOnlyList<string> domainTokens,
        out HashSet<string> tokenCases)
    {
        tokenCases = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < domainTokens.Count; i++)
        {
            var token = domainTokens[i];
            if (!TryParseIntDomainToken(token, out var value, out var isOther) ||
                !TryPatternMatchesIntDomainToken(
                    pattern,
                    value,
                    isOther,
                    domainTokens,
                    out var matches))
            {
                tokenCases.Clear();
                return false;
            }

            if (matches)
            {
                tokenCases.Add(token);
            }
        }

        return true;
    }

    private static bool TryPatternMatchesIntDomainToken(
        Pattern pattern,
        long value,
        bool isOther,
        IReadOnlyList<string> domainTokens,
        out bool matches)
    {
        var truth = EvaluateIntDomainTokenTruth(pattern, value, isOther, domainTokens);
        matches = truth is FiniteDomainMatchTruth.Match;
        return truth is not FiniteDomainMatchTruth.Unknown;
    }

    private static FiniteDomainMatchTruth EvaluateIntDomainTokenTruth(
        Pattern pattern,
        long value,
        bool isOther,
        IReadOnlyList<string> domainTokens)
    {
        switch (pattern)
        {
            case LiteralPattern literalPattern:
                if (!TryGetIntegerLiteralValue(literalPattern, out var literalValue))
                {
                    return FiniteDomainMatchTruth.Unknown;
                }

                return !isOther && value == literalValue
                    ? FiniteDomainMatchTruth.Match
                    : FiniteDomainMatchTruth.NoMatch;

            case RangePattern rangePattern:
                if (!TryGetIntegerRangeBounds(rangePattern, out var start, out var end))
                {
                    return FiniteDomainMatchTruth.Unknown;
                }

                return !isOther && value >= start && value <= end
                    ? FiniteDomainMatchTruth.Match
                    : FiniteDomainMatchTruth.NoMatch;

            case WildcardPattern:
            case VarPattern:
                return FiniteDomainMatchTruth.Match;

            case AsPattern asPattern:
                if (asPattern.InnerPattern == null)
                {
                    return FiniteDomainMatchTruth.Match;
                }

                return EvaluateIntDomainTokenTruth(
                    asPattern.InnerPattern,
                    value,
                    isOther,
                    domainTokens);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var sawUnknown = false;
                foreach (var alternative in orPattern.Alternatives)
                {
                    var altTruth = EvaluateIntDomainTokenTruth(
                        alternative,
                        value,
                        isOther,
                        domainTokens);
                    if (altTruth is FiniteDomainMatchTruth.Match)
                    {
                        return FiniteDomainMatchTruth.Match;
                    }

                    if (altTruth is FiniteDomainMatchTruth.Unknown)
                    {
                        sawUnknown = true;
                    }
                }

                return sawUnknown
                    ? FiniteDomainMatchTruth.Unknown
                    : FiniteDomainMatchTruth.NoMatch;
            }

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
            {
                var sawUnknown = false;
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    var conjunctTruth = EvaluateIntDomainTokenTruth(
                        conjunct,
                        value,
                        isOther,
                        domainTokens);
                    if (conjunctTruth is FiniteDomainMatchTruth.NoMatch)
                    {
                        return FiniteDomainMatchTruth.NoMatch;
                    }

                    if (conjunctTruth is FiniteDomainMatchTruth.Unknown)
                    {
                        sawUnknown = true;
                    }
                }

                return sawUnknown
                    ? FiniteDomainMatchTruth.Unknown
                    : FiniteDomainMatchTruth.Match;
            }

            case NotPattern { InnerPattern: not null } notPattern:
            {
                var innerTruth = EvaluateIntDomainTokenTruth(
                    notPattern.InnerPattern,
                    value,
                    isOther,
                    domainTokens);
                return innerTruth switch
                {
                    FiniteDomainMatchTruth.Match => FiniteDomainMatchTruth.NoMatch,
                    FiniteDomainMatchTruth.NoMatch => FiniteDomainMatchTruth.Match,
                    _ => FiniteDomainMatchTruth.Unknown
                };
            }

            case ViewPattern viewPattern:
                if (viewPattern.InnerPattern == null)
                {
                    return FiniteDomainMatchTruth.Match;
                }

                return EvaluateIntDomainTokenTruth(
                    viewPattern.InnerPattern,
                    value,
                    isOther,
                    domainTokens);

            default:
                return FiniteDomainMatchTruth.Unknown;
        }
    }

    private static bool TryDetermineListIntSplitDomains(
        IEnumerable<Pattern> patterns,
        int maxTrackedLength,
        out Dictionary<int, IReadOnlyList<string>> splitDomainsByLength)
    {
        splitDomainsByLength = new Dictionary<int, IReadOnlyList<string>>();
        if (maxTrackedLength <= 0)
        {
            return false;
        }

        var listPatterns = new List<ListPattern>();
        foreach (var pattern in patterns)
        {
            CollectListPatterns(pattern, listPatterns);
        }

        if (listPatterns.Count == 0)
        {
            return false;
        }

        var domainValuesByLength = new Dictionary<int, HashSet<long>>();
        var constrainedLengths = new HashSet<int>();
        for (var i = 0; i < listPatterns.Count; i++)
        {
            var listPattern = listPatterns[i];
            var length = listPattern.Elements.Count;
            if (length == 0 || length > MaxListIntSplitLength)
            {
                continue;
            }

            if (!domainValuesByLength.TryGetValue(length, out var domainValues))
            {
                domainValues = [];
                domainValuesByLength[length] = domainValues;
            }

            var hasConstrainedCase = false;
            for (var elementIndex = 0; elementIndex < listPattern.Elements.Count; elementIndex++)
            {
                if (!TryCollectIntDomainCandidatesFromPattern(
                        listPattern.Elements[elementIndex],
                        domainValues,
                        ref hasConstrainedCase))
                {
                    continue;
                }

                if (domainValues.Count > MaxListIntSplitDomainSize)
                {
                    splitDomainsByLength.Clear();
                    return false;
                }
            }

            if (hasConstrainedCase)
            {
                constrainedLengths.Add(length);

                if (listPattern.HasRestMarker)
                {
                    var maxSplitLength = Math.Min(maxTrackedLength, MaxListIntSplitLength);
                    for (var expandedLength = length; expandedLength <= maxSplitLength; expandedLength++)
                    {
                        if (!domainValuesByLength.TryGetValue(expandedLength, out var expandedDomainValues))
                        {
                            expandedDomainValues = [];
                            domainValuesByLength[expandedLength] = expandedDomainValues;
                        }

                        expandedDomainValues.UnionWith(domainValues);
                        constrainedLengths.Add(expandedLength);
                    }
                }
            }
        }

        foreach (var (length, domainValues) in domainValuesByLength.OrderBy(pair => pair.Key))
        {
            if (!constrainedLengths.Contains(length) ||
                domainValues.Count == 0 ||
                !CanExpandIntSplitCases(length, domainValues.Count + 1))
            {
                continue;
            }

            var tokens = domainValues
                .OrderBy(value => value)
                .Select(value => $"i:{value}")
                .ToList();
            tokens.Add(ListIntOtherToken);
            splitDomainsByLength[length] = tokens;
        }

        return splitDomainsByLength.Count > 0;
    }

    private static bool TryDetermineListAdtSplitDomains(
        IEnumerable<Pattern> patterns,
        int maxTrackedLength,
        SymbolTable symbolTable,
        out Dictionary<int, IReadOnlyList<string>> splitDomainsByLength)
    {
        splitDomainsByLength = new Dictionary<int, IReadOnlyList<string>>();
        if (maxTrackedLength <= 0)
        {
            return false;
        }

        var listPatterns = new List<ListPattern>();
        foreach (var pattern in patterns)
        {
            CollectListPatterns(pattern, listPatterns);
        }

        if (listPatterns.Count == 0)
        {
            return false;
        }

        var candidatesByLength = new Dictionary<int, List<ListAdtTokenCandidate>>();
        var constrainedLengths = new HashSet<int>();
        var adtByLength = new Dictionary<int, SymbolId>();

        for (var i = 0; i < listPatterns.Count; i++)
        {
            var listPattern = listPatterns[i];
            var length = listPattern.Elements.Count;
            if (length == 0 || length > MaxListAdtSplitLength)
            {
                continue;
            }

            if (!candidatesByLength.TryGetValue(length, out var candidates))
            {
                candidates = [];
                candidatesByLength[length] = candidates;
            }

            var hasConstrainedCase = false;
            SymbolId? resolvedAdt = null;
            for (var elementIndex = 0; elementIndex < listPattern.Elements.Count; elementIndex++)
            {
                if (!TryCollectAdtDomainCandidatesFromPattern(
                        listPattern.Elements[elementIndex],
                        symbolTable,
                        candidates,
                        ref resolvedAdt,
                        ref hasConstrainedCase))
                {
                    continue;
                }

                if (candidates.Count > MaxListAdtSplitDomainSize)
                {
                    splitDomainsByLength.Clear();
                    return false;
                }
            }

            if (!hasConstrainedCase || resolvedAdt is not { IsValid: true } adtId)
            {
                continue;
            }

            if (adtByLength.TryGetValue(length, out var existingAdt) && existingAdt != adtId)
            {
                splitDomainsByLength.Clear();
                return false;
            }

            adtByLength[length] = adtId;
            constrainedLengths.Add(length);

            if (listPattern.HasRestMarker)
            {
                var maxSplitLength = Math.Min(maxTrackedLength, MaxListAdtSplitLength);
                for (var expandedLength = length; expandedLength <= maxSplitLength; expandedLength++)
                {
                    if (expandedLength == length)
                    {
                        continue;
                    }

                    if (!candidatesByLength.TryGetValue(expandedLength, out var expandedCandidates))
                    {
                        expandedCandidates = [];
                        candidatesByLength[expandedLength] = expandedCandidates;
                    }

                    foreach (var candidate in candidates)
                    {
                        if (!expandedCandidates.Contains(candidate))
                        {
                            expandedCandidates.Add(candidate);
                        }
                    }
                    adtByLength[expandedLength] = adtId;
                    constrainedLengths.Add(expandedLength);
                }
            }
        }

        foreach (var (length, candidates) in candidatesByLength.OrderBy(pair => pair.Key))
        {
            if (!constrainedLengths.Contains(length) ||
                !adtByLength.TryGetValue(length, out var adtId) ||
                !adtId.IsValid ||
                !AdtCoverageSpace.TryGetAdtConstructors(symbolTable, adtId, out var constructors))
            {
                continue;
            }

            var tokens = BuildAdtDomainTokens(constructors, candidates, symbolTable);
            if (tokens.Count == 0 ||
                tokens.Count > MaxListAdtSplitDomainSize ||
                !CanExpandAdtSplitCases(length, tokens.Count))
            {
                continue;
            }

            splitDomainsByLength[length] = tokens;
        }

        return splitDomainsByLength.Count > 0;
    }

    private static bool TryCollectAdtDomainCandidatesFromPattern(
        Pattern pattern,
        SymbolTable symbolTable,
        ICollection<ListAdtTokenCandidate> candidates,
        ref SymbolId? resolvedAdt,
        ref bool hasConstrainedCase)
    {
        switch (pattern)
        {
            case CtorPattern ctorPattern when ctorPattern.SymbolId.IsValid:
            {
                var ctorSymbol = symbolTable.GetSymbol<CtorSymbol>(ctorPattern.SymbolId);
                if (ctorSymbol == null || !ctorSymbol.OwnerAdt.IsValid)
                {
                    return false;
                }

                var constructorAdt = symbolTable.GetClosedCaseRoot(ctorSymbol.OwnerAdt);
                if (resolvedAdt is { IsValid: true } currentAdt && currentAdt != constructorAdt)
                {
                    return false;
                }

                resolvedAdt = constructorAdt;
                hasConstrainedCase = true;

                if (ctorPattern.PositionalPatterns.Count == 1 &&
                    ctorPattern.NamedPatterns.Count == 0 &&
                    TryCreateScalarCoverageCaseFromPattern(ctorPattern.PositionalPatterns[0], out var fieldCase))
                {
                    candidates.Add(new ListAdtTokenCandidate(ctorPattern.SymbolId, fieldCase));
                    return true;
                }

                candidates.Add(new ListAdtTokenCandidate(ctorPattern.SymbolId, FieldCase: null));
                return true;
            }

            case WildcardPattern:
            case VarPattern:
                return true;

            case AsPattern asPattern:
                return asPattern.InnerPattern == null ||
                       TryCollectAdtDomainCandidatesFromPattern(
                           asPattern.InnerPattern,
                           symbolTable,
                           candidates,
                           ref resolvedAdt,
                           ref hasConstrainedCase);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    if (!TryCollectAdtDomainCandidatesFromPattern(
                            alternative,
                            symbolTable,
                            candidates,
                            ref resolvedAdt,
                            ref hasConstrainedCase))
                    {
                        return false;
                    }
                }

                return true;

            case AndPattern { Conjuncts.Count: > 0 } andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    if (!TryCollectAdtDomainCandidatesFromPattern(
                            conjunct,
                            symbolTable,
                            candidates,
                            ref resolvedAdt,
                            ref hasConstrainedCase))
                    {
                        return false;
                    }
                }

                return true;

            case NotPattern { InnerPattern: not null } notPattern:
                return TryCollectAdtDomainCandidatesFromPattern(
                    notPattern.InnerPattern,
                    symbolTable,
                    candidates,
                    ref resolvedAdt,
                    ref hasConstrainedCase);

            case ViewPattern viewPattern when IsPatternIrrefutableForFiniteCoverage(viewPattern.InnerPattern):
                return true;

            case ViewPattern viewPattern:
                return viewPattern.InnerPattern == null ||
                       TryCollectAdtDomainCandidatesFromPattern(
                           viewPattern.InnerPattern,
                           symbolTable,
                           candidates,
                           ref resolvedAdt,
                           ref hasConstrainedCase);

            default:
                return false;
        }
    }

    private static IReadOnlyList<string> BuildAdtDomainTokens(
        IReadOnlyList<SymbolId> constructors,
        IReadOnlyList<ListAdtTokenCandidate> candidates,
        SymbolTable symbolTable)
    {
        var tokens = new List<string>();
        foreach (var constructorId in constructors.OrderBy(id => id.Value))
        {
            var fieldCases = candidates
                .Where(candidate => candidate.ConstructorId == constructorId && candidate.FieldCase != null)
                .Select(candidate => candidate.FieldCase!.Value)
                .GroupBy(@case => @case.Key, StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(@case => @case.Key, StringComparer.Ordinal)
                .ToList();

            if (fieldCases.Count == 0)
            {
                tokens.Add(EncodeAdtDomainToken(constructorId, fieldCase: null, isOther: true));
                continue;
            }

            foreach (var fieldCase in fieldCases)
            {
                tokens.Add(EncodeAdtDomainToken(constructorId, fieldCase, isOther: false));
            }

            tokens.Add(EncodeAdtDomainToken(constructorId, fieldCase: null, isOther: true));
        }

        return tokens;
    }

    private static string EncodeAdtDomainToken(SymbolId constructorId, ScalarCoverageCase? fieldCase, bool isOther)
    {
        if (isOther || fieldCase == null)
        {
            return $"a:{constructorId.Value}:*";
        }

        return $"a:{constructorId.Value}:f:{Uri.EscapeDataString(fieldCase.Value.Key)}:{Uri.EscapeDataString(fieldCase.Value.DisplayText)}";
    }

    private static bool TryParseAdtDomainToken(
        string token,
        out SymbolId constructorId,
        out ScalarCoverageCase? fieldCase,
        out bool isOther)
    {
        constructorId = SymbolId.None;
        fieldCase = null;
        isOther = false;

        if (!token.StartsWith("a:", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = token.Split(':', 5);
        if (parts.Length < 3 || !int.TryParse(parts[1], out var constructorValue))
        {
            return false;
        }

        constructorId = new SymbolId(constructorValue);
        if (parts[2] == WellKnownStrings.Operators.Multiply)
        {
            isOther = true;
            return true;
        }

        if (parts.Length == 5 && parts[2] == "f")
        {
            fieldCase = new ScalarCoverageCase(
                Uri.UnescapeDataString(parts[3]),
                Uri.UnescapeDataString(parts[4]));
            return true;
        }

        return false;
    }

    private static bool TryCreateScalarCoverageCaseFromPattern(Pattern? pattern, out ScalarCoverageCase @case)
    {
        @case = default;
        if (pattern is LiteralPattern literalPattern &&
            TryCreateScalarCoverageCase(literalPattern, out _, out @case))
        {
            return true;
        }

        return false;
    }

    private static bool HasUntrackedRefutableListElementPattern(
        ListPattern listPattern,
        IReadOnlySet<int> boolSplitLengths,
        bool enableBoolElementSplits,
        IReadOnlyDictionary<int, IReadOnlyList<string>> intSplitDomainsByLength,
        bool enableIntElementSplits,
        IReadOnlyDictionary<int, IReadOnlyList<string>> adtSplitDomainsByLength,
        bool enableAdtElementSplits,
        SymbolTable symbolTable)
    {
        if (listPattern.Elements.Count == 0)
        {
            return false;
        }

        if (listPattern.HasRestMarker)
        {
            return HasUntrackedRefutableListElementPatternForLength(
                listPattern.Elements,
                listPattern.Elements.Count,
                boolSplitLengths,
                enableBoolElementSplits,
                intSplitDomainsByLength,
                enableIntElementSplits,
                adtSplitDomainsByLength,
                enableAdtElementSplits,
                symbolTable);
        }

        return HasUntrackedRefutableListElementPatternForLength(
            listPattern.Elements,
            listPattern.Elements.Count,
            boolSplitLengths,
            enableBoolElementSplits,
            intSplitDomainsByLength,
            enableIntElementSplits,
            adtSplitDomainsByLength,
            enableAdtElementSplits,
            symbolTable);
    }

    private static bool HasUntrackedRefutableListElementPatternForLength(
        IReadOnlyList<Pattern> elements,
        int length,
        IReadOnlySet<int> boolSplitLengths,
        bool enableBoolElementSplits,
        IReadOnlyDictionary<int, IReadOnlyList<string>> intSplitDomainsByLength,
        bool enableIntElementSplits,
        IReadOnlyDictionary<int, IReadOnlyList<string>> adtSplitDomainsByLength,
        bool enableAdtElementSplits,
        SymbolTable symbolTable)
    {
        if (enableBoolElementSplits &&
            boolSplitLengths.Contains(length) &&
            TryCollectListElementBoolCaseSets(elements, out _))
        {
            return false;
        }

        if (enableIntElementSplits &&
            intSplitDomainsByLength.TryGetValue(length, out var intDomainTokens) &&
            TryCollectListElementIntTokenCaseSets(elements, intDomainTokens, out _))
        {
            return false;
        }

        if (enableAdtElementSplits &&
            adtSplitDomainsByLength.TryGetValue(length, out var adtDomainTokens) &&
            TryCollectListElementAdtTokenCaseSets(elements, adtDomainTokens, symbolTable, out _))
        {
            return false;
        }

        return elements.Any(IsUntrackedRefutableListElementPattern);
    }

    private static bool IsUntrackedRefutableListElementPattern(Pattern? pattern)
    {
        if (pattern == null || IsPatternIrrefutableForFiniteCoverage(pattern))
        {
            return false;
        }

        return pattern switch
        {
            LiteralPattern { Type: LiteralType.Boolean or LiteralType.Integer or LiteralType.Char } => false,
            LiteralPattern => true,
            RangePattern rangePattern => !TryGetIntegerRangeBounds(rangePattern, out _, out _),
            CtorPattern => true,
            TuplePattern => true,
            ListPattern => true,
            AsPattern asPattern => IsUntrackedRefutableListElementPattern(asPattern.InnerPattern),
            OrPattern { Alternatives.Count: > 0 } orPattern =>
                orPattern.Alternatives.Any(IsUntrackedRefutableListElementPattern),
            AndPattern { Conjuncts.Count: > 0 } andPattern =>
                andPattern.Conjuncts.Any(IsUntrackedRefutableListElementPattern),
            NotPattern { InnerPattern: not null } notPattern =>
                IsUntrackedRefutableListElementPattern(notPattern.InnerPattern),
            ViewPattern => false,
            _ => true
        };
    }
}
