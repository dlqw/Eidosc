using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast.Patterns;
using Eidosc.Diagnostic;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void ResolveOrPatternBindings(OrPattern orPattern)
    {
        if (orPattern.Alternatives.Count == 0)
        {
            return;
        }

        var occurrencesByAlternative = new List<List<PatternBindingOccurrence>>(orPattern.Alternatives.Count);
        for (var i = 0; i < orPattern.Alternatives.Count; i++)
        {
            var alternative = orPattern.Alternatives[i];
            using var context = PushPatternDiagnosticContext($"alternative#{i + 1}");
            ResolvePatternReferencesWithoutBinding(alternative);
            var occurrences = new List<PatternBindingOccurrence>();
            CollectPatternBindingOccurrences(alternative, "pattern", occurrences);
            occurrencesByAlternative.Add(occurrences);
        }

        var expected = occurrencesByAlternative[0];
        var hasSlotShape = DoOrPatternAlternativesShareBindingShape(
            expected,
            occurrencesByAlternative,
            out var shapeMismatchDetails);
        var hasLegacyNameShape = DoOrPatternAlternativesShareLegacyBindingShape(
            expected,
            occurrencesByAlternative,
            out var legacyShapeMismatchDetails);

        if (!hasSlotShape && !hasLegacyNameShape)
        {
            var message = shapeMismatchDetails.Count > 0
                ? DiagnosticMessages.OrPatternAlternativesMustBindSameValueSlotsWithDetails(
                    string.Join("; ", shapeMismatchDetails))
                : DiagnosticMessages.OrPatternAlternativesMustBindSameValueSlots;

            AddPatternError(orPattern.Span, message);
            return;
        }

        if (hasSlotShape)
        {
            var modeMismatchDetails = BuildOrPatternBindingSlotModeMismatchDetails(expected, occurrencesByAlternative);
            if (modeMismatchDetails.Count > 0)
            {
                AddPatternError(
                    orPattern.Span,
                    DiagnosticMessages.OrPatternAlternativesMustUseSameBindingMode(
                        string.Join("; ", modeMismatchDetails)));
                return;
            }

            BindOrPatternUsingSlotShape(orPattern, expected, occurrencesByAlternative);
            return;
        }

        var legacyModeMismatchDetails = BuildOrPatternLegacyBindingModeMismatchDetails(expected, occurrencesByAlternative);
        if (legacyModeMismatchDetails.Count > 0)
        {
            AddPatternError(
                orPattern.Span,
                DiagnosticMessages.OrPatternAlternativesMustUseSameLegacyBindingMode(
                    string.Join("; ", legacyModeMismatchDetails)));
            return;
        }

        BindOrPatternUsingLegacyNameShape(orPattern, expected);
    }

    private void BindOrPatternUsingSlotShape(
        OrPattern orPattern,
        IReadOnlyList<PatternBindingOccurrence> expected,
        IReadOnlyList<List<PatternBindingOccurrence>> occurrencesByAlternative)
    {
        if (expected.Count == 0 || _symbolTable.CurrentScope == null)
        {
            return;
        }

        var slotSymbols = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
        var aliasSymbols = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
        for (var slotIndex = 0; slotIndex < expected.Count; slotIndex++)
        {
            var slot = expected[slotIndex];
            var bindingName = string.IsNullOrWhiteSpace(slot.Name)
                ? $"$or{slotIndex + 1}"
                : slot.Name;
            var symbolId = DeclarePatternVariable(
                bindingName,
                orPattern.Span,
                isParameter: false,
                isPatternBound: true,
                bindingMode: slot.BindingMode);
            slotSymbols[slot.SlotPath] = symbolId;

            for (var altIndex = 0; altIndex < occurrencesByAlternative.Count; altIndex++)
            {
                var aliasName = occurrencesByAlternative[altIndex][slotIndex].Name;
                if (string.IsNullOrWhiteSpace(aliasName))
                {
                    continue;
                }

                if (aliasSymbols.TryGetValue(aliasName, out var existingAliasSymbol))
                {
                    if (existingAliasSymbol != symbolId)
                    {
                        AddPatternError(
                            orPattern.Span,
                            DiagnosticMessages.OrPatternAliasResolvesDifferentValueSlots(aliasName));
                        return;
                    }

                    continue;
                }

                if (_symbolTable.CurrentScope.GetLocalBindings().TryGetValue(aliasName, out var existingScopeSymbol))
                {
                    if (existingScopeSymbol != symbolId)
                    {
                        AddPatternError(
                            orPattern.Span,
                            DiagnosticMessages.PatternVariableBoundMoreThanOnce(aliasName));
                        return;
                    }
                }
                else
                {
                    _symbolTable.CurrentScope.BindValue(aliasName, symbolId);
                }

                aliasSymbols[aliasName] = symbolId;
            }
        }

        for (var i = 0; i < orPattern.Alternatives.Count; i++)
        {
            AssignPatternBindingSymbols(
                orPattern.Alternatives[i],
                BuildOrPatternAlternativeBindingMap(occurrencesByAlternative[i], slotSymbols));
        }
    }

    private void BindOrPatternUsingLegacyNameShape(
        OrPattern orPattern,
        IReadOnlyList<PatternBindingOccurrence> expected)
    {
        if (expected.Count == 0 || _symbolTable.CurrentScope == null)
        {
            return;
        }

        var bindingSymbols = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
        foreach (var occurrence in expected)
        {
            if (string.IsNullOrWhiteSpace(occurrence.Name) ||
                bindingSymbols.ContainsKey(occurrence.Name))
            {
                continue;
            }

            var symbolId = DeclarePatternVariable(
                occurrence.Name,
                orPattern.Span,
                isParameter: false,
                isPatternBound: true,
                bindingMode: occurrence.BindingMode);
            bindingSymbols[occurrence.Name] = symbolId;
        }

        foreach (var alternative in orPattern.Alternatives)
        {
            AssignPatternBindingSymbols(alternative, bindingSymbols);
        }
    }

    private static bool DoOrPatternAlternativesShareBindingShape(
        IReadOnlyList<PatternBindingOccurrence> expected,
        IReadOnlyList<List<PatternBindingOccurrence>> occurrencesByAlternative,
        out List<string> details)
    {
        details = [];

        for (var i = 1; i < occurrencesByAlternative.Count; i++)
        {
            var current = occurrencesByAlternative[i];
            if (current.Count != expected.Count)
            {
                details.Add($"alt#{i + 1} binds {current.Count} slot(s), expected {expected.Count}");
                continue;
            }

            for (var slotIndex = 0; slotIndex < expected.Count; slotIndex++)
            {
                var expectedPath = expected[slotIndex].SlotPath;
                var currentPath = current[slotIndex].SlotPath;
                if (string.Equals(expectedPath, currentPath, StringComparison.Ordinal))
                {
                    continue;
                }

                details.Add(
                    $"alt#{i + 1} slot#{slotIndex + 1} expected '{expectedPath}' but got '{currentPath}'");
                break;
            }
        }

        return details.Count == 0;
    }

    private static bool DoOrPatternAlternativesShareLegacyBindingShape(
        IReadOnlyList<PatternBindingOccurrence> expected,
        IReadOnlyList<List<PatternBindingOccurrence>> occurrencesByAlternative,
        out List<string> details)
    {
        details = [];

        for (var i = 1; i < occurrencesByAlternative.Count; i++)
        {
            var current = occurrencesByAlternative[i];
            if (current.Count != expected.Count)
            {
                details.Add($"alt#{i + 1} binds {current.Count} name slot(s), expected {expected.Count}");
                continue;
            }

            for (var slotIndex = 0; slotIndex < expected.Count; slotIndex++)
            {
                var expectedName = expected[slotIndex].Name;
                var currentName = current[slotIndex].Name;
                if (string.Equals(expectedName, currentName, StringComparison.Ordinal))
                {
                    continue;
                }

                details.Add(
                    $"alt#{i + 1} slot#{slotIndex + 1} expected binding '{expectedName}' but got '{currentName}'");
                break;
            }
        }

        return details.Count == 0;
    }

    private static Dictionary<string, SymbolId> BuildOrPatternAlternativeBindingMap(
        IReadOnlyList<PatternBindingOccurrence> occurrences,
        IReadOnlyDictionary<string, SymbolId> slotSymbols)
    {
        var bindingSymbols = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
        for (var i = 0; i < occurrences.Count; i++)
        {
            var occurrence = occurrences[i];
            if (string.IsNullOrWhiteSpace(occurrence.Name) ||
                !slotSymbols.TryGetValue(occurrence.SlotPath, out var symbolId))
            {
                continue;
            }

            bindingSymbols[occurrence.Name] = symbolId;
        }

        return bindingSymbols;
    }

    private static List<string> BuildOrPatternBindingSlotModeMismatchDetails(
        IReadOnlyList<PatternBindingOccurrence> expected,
        IReadOnlyList<List<PatternBindingOccurrence>> occurrencesByAlternative)
    {
        var details = new List<string>();

        for (var i = 1; i < occurrencesByAlternative.Count; i++)
        {
            var currentOccurrences = occurrencesByAlternative[i];
            for (var slotIndex = 0; slotIndex < expected.Count; slotIndex++)
            {
                var expectedOccurrence = expected[slotIndex];
                var currentOccurrence = currentOccurrences[slotIndex];
                if (currentOccurrence.BindingMode == expectedOccurrence.BindingMode)
                {
                    continue;
                }

                details.Add(
                    $"alt#{i + 1} slot#{slotIndex + 1} expected {expectedOccurrence.BindingMode.ToDisplayText()} but got {currentOccurrence.BindingMode.ToDisplayText()}");
            }
        }

        return details;
    }

    private static List<string> BuildOrPatternLegacyBindingModeMismatchDetails(
        IReadOnlyList<PatternBindingOccurrence> expected,
        IReadOnlyList<List<PatternBindingOccurrence>> occurrencesByAlternative)
    {
        var details = new List<string>();

        for (var i = 1; i < occurrencesByAlternative.Count; i++)
        {
            var currentOccurrences = occurrencesByAlternative[i];
            for (var slotIndex = 0; slotIndex < expected.Count; slotIndex++)
            {
                var expectedOccurrence = expected[slotIndex];
                var currentOccurrence = currentOccurrences[slotIndex];
                if (currentOccurrence.BindingMode == expectedOccurrence.BindingMode)
                {
                    continue;
                }

                var bindingName = string.IsNullOrWhiteSpace(expectedOccurrence.Name)
                    ? $"slot#{slotIndex + 1}"
                    : expectedOccurrence.Name;
                details.Add(
                    $"alt#{i + 1} binding '{bindingName}' expected {expectedOccurrence.BindingMode.ToDisplayText()} but got {currentOccurrence.BindingMode.ToDisplayText()}");
                break;
            }
        }

        return details;
    }

    private static void CollectPatternBindingOccurrences(
        Pattern pattern,
        string path,
        ICollection<PatternBindingOccurrence> occurrences)
    {
        var slotPath = NormalizeOrPatternBindingSlotPath(path);
        switch (pattern)
        {
            case VarPattern varPattern when !string.IsNullOrWhiteSpace(varPattern.Name):
                occurrences.Add(new PatternBindingOccurrence(path, slotPath, varPattern.Name, varPattern.BindingMode));
                return;

            case CtorPattern ctorPattern:
                for (var i = 0; i < ctorPattern.PositionalPatterns.Count; i++)
                {
                    CollectPatternBindingOccurrences(
                        ctorPattern.PositionalPatterns[i],
                        $"{path}/positional#{i + 1}",
                        occurrences);
                }

                foreach (var named in ctorPattern.NamedPatterns)
                {
                    if (named.Pattern == null)
                    {
                        continue;
                    }

                    var fieldPath = string.IsNullOrWhiteSpace(named.FieldName)
                        ? $"{path}/field#<unnamed>"
                        : $"{path}/field#{named.FieldName}";
                    CollectPatternBindingOccurrences(named.Pattern, fieldPath, occurrences);
                }
                return;

            case TuplePattern tuplePattern:
                for (var i = 0; i < tuplePattern.Elements.Count; i++)
                {
                    CollectPatternBindingOccurrences(tuplePattern.Elements[i], $"{path}/element#{i + 1}", occurrences);
                }
                return;

            case ListPattern listPattern:
                for (var i = 0; i < listPattern.Elements.Count; i++)
                {
                    CollectPatternBindingOccurrences(listPattern.Elements[i], $"{path}/element#{i + 1}", occurrences);
                }

                if (listPattern.RestPattern != null)
                {
                    CollectPatternBindingOccurrences(listPattern.RestPattern, $"{path}/rest", occurrences);
                }

                for (var i = 0; i < listPattern.SuffixElements.Count; i++)
                {
                    CollectPatternBindingOccurrences(
                        listPattern.SuffixElements[i],
                        $"{path}/suffix#{i + 1}",
                        occurrences);
                }
                return;

            case AndPattern andPattern:
                for (var i = 0; i < andPattern.Conjuncts.Count; i++)
                {
                    CollectPatternBindingOccurrences(andPattern.Conjuncts[i], $"{path}/conjunct#{i + 1}", occurrences);
                }
                return;

            case OrPattern { Alternatives.Count: > 0 } nestedOrPattern:
                CollectPatternBindingOccurrences(nestedOrPattern.Alternatives[0], path, occurrences);
                return;

            case RangePattern rangePattern:
                if (rangePattern.Start != null)
                {
                    CollectPatternBindingOccurrences(rangePattern.Start, $"{path}/start", occurrences);
                }

                if (rangePattern.End != null)
                {
                    CollectPatternBindingOccurrences(rangePattern.End, $"{path}/end", occurrences);
                }
                return;

            case ViewPattern viewPattern when viewPattern.InnerPattern != null:
                CollectPatternBindingOccurrences(viewPattern.InnerPattern, $"{path}/view-inner", occurrences);
                return;

            case AsPattern asPattern:
                if (!string.IsNullOrWhiteSpace(asPattern.BindingName))
                {
                    occurrences.Add(new PatternBindingOccurrence(path, slotPath, asPattern.BindingName, asPattern.BindingMode));
                }

                if (asPattern.InnerPattern != null)
                {
                    CollectPatternBindingOccurrences(asPattern.InnerPattern, $"{path}/as-inner", occurrences);
                }
                return;

            default:
                return;
        }
    }

    private static string NormalizeOrPatternBindingSlotPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return path;
        }

        var segments = path
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(segment => !segment.StartsWith("conjunct#", StringComparison.Ordinal))
            .ToArray();
        return segments.Length == 0
            ? path
            : string.Join(WellKnownStrings.Operators.Divide, segments);
    }

    private void ResolveNotPatternBindings(NotPattern notPattern)
    {
        if (notPattern.InnerPattern == null)
        {
            AddPatternError(notPattern.Span, DiagnosticMessages.NotPatternMissingInnerPattern);
            return;
        }

        using (PushPatternDiagnosticContext("inner"))
        {
            ResolvePatternReferencesWithoutBinding(notPattern.InnerPattern);
        }

        var bindingNames = new HashSet<string>(StringComparer.Ordinal);
        CollectPatternPotentialBindingNames(notPattern.InnerPattern, bindingNames);
        if (bindingNames.Count == 0)
        {
            return;
        }

        var names = string.Join(", ", bindingNames.OrderBy(name => name, StringComparer.Ordinal));
        AddPatternError(notPattern.Span, DiagnosticMessages.NotPatternCannotBindVariables(names));
    }

    private void ResolveAndPatternBindings(AndPattern andPattern)
    {
        if (andPattern.Conjuncts.Count == 0)
        {
            return;
        }

        var seenBindingNames = new HashSet<string>(StringComparer.Ordinal);
        var seenBindingModes = new Dictionary<string, PatternBindingMode>(StringComparer.Ordinal);
        var duplicateNames = new HashSet<string>(StringComparer.Ordinal);
        var bindingSets = new List<HashSet<string>>(andPattern.Conjuncts.Count);

        for (var i = 0; i < andPattern.Conjuncts.Count; i++)
        {
            var conjunct = andPattern.Conjuncts[i];
            using var context = PushPatternDiagnosticContext($"conjunct#{i + 1}");
            ResolvePatternReferencesWithoutBinding(conjunct);
            var bindingNames = new HashSet<string>(StringComparer.Ordinal);
            CollectPatternBindingNames(conjunct, bindingNames);
            bindingSets.Add(bindingNames);

            var bindingModes = new Dictionary<string, PatternBindingMode>(StringComparer.Ordinal);
            CollectPatternBindingModes(conjunct, bindingModes);
            foreach (var bindingName in bindingNames)
            {
                if (!seenBindingNames.Add(bindingName))
                {
                    duplicateNames.Add(bindingName);
                }
                else if (bindingModes.TryGetValue(bindingName, out var mode))
                {
                    seenBindingModes[bindingName] = mode;
                }
            }
        }

        if (duplicateNames.Count > 0)
        {
            var names = string.Join(", ", duplicateNames.OrderBy(name => name, StringComparer.Ordinal));
            var details = BuildAndPatternDuplicateBindingDetails(bindingSets);
            var message = details.Count > 0
                ? DiagnosticMessages.AndPatternConjunctsCannotBindSameVariableMoreThanOnceWithDetails(
                    names,
                    string.Join("; ", details))
                : DiagnosticMessages.AndPatternConjunctsCannotBindSameVariableMoreThanOnce(names);

            AddPatternError(
                andPattern.Span,
                message);
            return;
        }

        if (seenBindingNames.Count == 0)
        {
            return;
        }

        var bindingSymbols = new Dictionary<string, SymbolId>(StringComparer.Ordinal);
        foreach (var bindingName in seenBindingNames)
        {
            var bindingMode = seenBindingModes.TryGetValue(bindingName, out var mode)
                ? mode
                : PatternBindingMode.ByValue;
            var symbolId = DeclarePatternVariable(
                bindingName,
                andPattern.Span,
                isParameter: false,
                isPatternBound: true,
                bindingMode: bindingMode);
            bindingSymbols[bindingName] = symbolId;
        }

        foreach (var conjunct in andPattern.Conjuncts)
        {
            AssignPatternBindingSymbols(conjunct, bindingSymbols);
        }
    }

    private static List<string> BuildAndPatternDuplicateBindingDetails(IReadOnlyList<HashSet<string>> bindingSets)
    {
        var details = new List<string>();
        var seenBindingNames = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < bindingSets.Count; i++)
        {
            var repeated = bindingSets[i]
                .Where(name => !seenBindingNames.Add(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            if (repeated.Count == 0)
            {
                continue;
            }

            details.Add($"conjunct#{i + 1} repeats [{string.Join(", ", repeated)}]");
        }

        return details;
    }
}
