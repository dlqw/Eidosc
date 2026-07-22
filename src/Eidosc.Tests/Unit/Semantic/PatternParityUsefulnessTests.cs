using System;
using System.Collections.Generic;
using System.Linq;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public class PatternParityUsefulnessTests
{
    public sealed record PatternParityCase(
        string Name,
        string Source,
        bool ExpectW4200,
        bool ExpectW4201,
        string? W4200MessageContains = null,
        string? W4201MessageContains = null,
        string? W4200NoteContains = null,
        string? W4201NoteContains = null);

    public static IEnumerable<object[]> Cases()
    {
        yield return
        [
            new PatternParityCase(
                "bool_or_exhaustive",
                """
classify :: Bool -> Int
{
    x => match x
    {
        true | false => 1
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: false)
        ];

        yield return
        [
            new PatternParityCase(
                "bool_and_unsatisfiable",
                """
classify :: Bool -> Int
{
    x => match x
    {
        true & false => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "unsatisfiable")
        ];

        yield return
        [
            new PatternParityCase(
                "bool_not_non_exhaustive",
                """
classify :: Bool -> Int
{
    x => match x
    {
        !false => 1
    }
}
""",
                ExpectW4200: true,
                ExpectW4201: false,
                W4200MessageContains: "missing bool cases: false",
                W4200NoteContains: "Missing-case witnesses: false")
        ];

        yield return
        [
            new PatternParityCase(
                "bool_or_as_exhaustive",
                """
classify :: Bool -> Int
{
    x => match x
    {
        (true as b) | (false as b) => 1
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: false)
        ];

        yield return
        [
            new PatternParityCase(
                "bool_or_guard_case_split",
                """
classify :: Bool -> Int
{
    x => match x
    {
        true | false when x => 1,
        false => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: false)
        ];

        yield return
        [
            new PatternParityCase(
                "bool_or_guard_unsat",
                """
classify :: Bool -> Int
{
    x => match x
    {
        true | false when !x && x => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "guard is constant false")
        ];

        yield return
        [
            new PatternParityCase(
                "bool_duplicate_literal_covered",
                """
classify :: Bool -> Int
{
    x => match x
    {
        true => 1,
        true => 2,
        false => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "already covered",
                W4201NoteContains: "Covered-case witnesses: true")
        ];

        yield return
        [
            new PatternParityCase(
                "bool_shadow_after_irrefutable",
                """
classify :: Bool -> Int
{
    x => match x
    {
        _ => 0,
        true => 1
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "irrefutable")
        ];

        yield return
        [
            new PatternParityCase(
                "tuple_or_exhaustive",
                """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (true, true) | (true, false) => 1,
        (false, _) => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: false)
        ];

        yield return
        [
            new PatternParityCase(
                "tuple_not_non_exhaustive",
                """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        !(false, false) => 1
    }
}
""",
                ExpectW4200: true,
                ExpectW4201: false,
                W4200MessageContains: "missing tuple bool cases: (false, false)",
                W4200NoteContains: "Missing-case witnesses: (false, false)")
        ];

        yield return
        [
            new PatternParityCase(
                "tuple_and_unsatisfiable",
                """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (true, false) & (false, true) => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "unsatisfiable")
        ];

        yield return
        [
            new PatternParityCase(
                "tuple_covered_branch",
                """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (true, _) => 1,
        (true, false) => 2,
        (false, _) => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "already covered",
                W4201NoteContains: "Covered-case witnesses: (true, false)")
        ];

        yield return
        [
            new PatternParityCase(
                "tuple_or_guard_false",
                """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (true, _) | (false, _) when false => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "guard is constant false")
        ];

        yield return
        [
            new PatternParityCase(
                "tuple_guard_exhaustive_by_case_split",
                """
classify :: (Bool, Bool) -> Int
{
    t => match t
    {
        (a, b) when a && b => 1,
        (a, b) when a && !b => 2,
        (a, b) when !a && b => 3,
        (a, b) when !a && !b => 4
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: false)
        ];

        yield return
        [
            new PatternParityCase(
                "tuple_guard_partial_precise_missing_case",
                """
classify :: (Bool, Bool) -> Int
{
    t => match t
    {
        (a, b) when a => 1,
        (false, false) => 0
    }
}
""",
                ExpectW4200: true,
                ExpectW4201: false,
                W4200MessageContains: "missing tuple bool cases: (false, true)",
                W4200NoteContains: "Missing-case witnesses: (false, true)")
        ];

        yield return
        [
            new PatternParityCase(
                "tuple_guard_unsatisfiable_branch",
                """
classify :: (Bool, Bool) -> Int
{
    t => match t
    {
        (a, b) when a && !a => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "unsatisfiable")
        ];

        yield return
        [
            new PatternParityCase(
                "tuple_guard_covered_by_previous",
                """
classify :: (Bool, Bool) -> Int
{
    t => match t
    {
        (a, b) when a => 1,
        (true, false) => 2,
        (false, _) => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "already covered",
                W4201NoteContains: "Covered-case witnesses: (true, false)")
        ];

        yield return
        [
            new PatternParityCase(
                "adt_not_non_exhaustive",
                """
OptionI :: type {
    Some:: type(Int) , None :: type {}
}

classify :: OptionI -> Int
{
    x => match x
    {
        !None => 1
    }
}
""",
                ExpectW4200: true,
                ExpectW4201: false,
                W4200MessageContains: "missing constructors: None",
                W4200NoteContains: "Missing-case witnesses: None")
        ];

        yield return
        [
            new PatternParityCase(
                "adt_and_unsatisfiable",
                """
OptionI :: type {
    Some:: type(Int) , None :: type {}
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some(v) & !Some(_) => v,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "unsatisfiable")
        ];

        yield return
        [
            new PatternParityCase(
                "adt_duplicate_ctor_covered",
                """
OptionI :: type {
    Some:: type(Int) , None :: type {}
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some(v) => v,
        Some(v) => v + 1,
        None => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "already covered")
        ];

        yield return
        [
            new PatternParityCase(
                "adt_or_exhaustive",
                """
OptionI :: type {
    Some:: type(Int) , None :: type {}
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some(_) | None => 1
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: false)
        ];

        yield return
        [
            new PatternParityCase(
                "adt_guard_binding_proves_ctor_coverage",
                """
OptionB :: type {
    Some:: type(Bool) , None :: type {}
}

classify :: OptionB -> Int
{
    x => match x
    {
        Some((true as flag)) when flag => 1,
        None => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: false)
        ];

        yield return
        [
            new PatternParityCase(
                "adt_guard_binding_partial_missing_ctor",
                """
OptionB :: type {
    Some:: type(Bool) , None :: type {}
}

classify :: OptionB -> Int
{
    x => match x
    {
        Some((true as flag)) when flag => 1
    }
}
""",
                ExpectW4200: true,
                ExpectW4201: false,
                W4200MessageContains: "missing constructors: None",
                W4200NoteContains: "Missing-case witnesses: None")
        ];

        yield return
        [
            new PatternParityCase(
                "adt_guard_binding_unsatisfiable_ctor_case",
                """
OptionB :: type {
    Some:: type(Bool) , None :: type {}
}

classify :: OptionB -> Int
{
    x => match x
    {
        Some((false as flag)) when flag => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "unsatisfiable")
        ];

        yield return
        [
            new PatternParityCase(
                "adt_guard_unknown_predicate_reports_guard_note",
                """
OptionI :: type {
    Some:: type(Int) , None :: type {}
}

pred :: Int -> Bool
{
    x => x > 0
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some(v) when pred(v) => 1,
        None => 0
    }
}
""",
                ExpectW4200: true,
                ExpectW4201: false,
                W4200MessageContains: "missing constructors: Some",
                W4200NoteContains: "unresolved predicates were conservatively excluded from exact coverage: #1")
        ];

        yield return
        [
            new PatternParityCase(
                "tuple_guard_unknown_predicate_not_unsatisfiable",
                """
pred :: Bool -> Bool
{
    x => x
}

classify :: (Bool, Bool) -> Int
{
    t => match t
    {
        (a, b) when pred(a) => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: false)
        ];

        yield return
        [
            new PatternParityCase(
                "adt_as_and_not_exhaustive",
                """
OptionI :: type {
    Some:: type(Int) , None :: type {}
}

classify :: OptionI -> Int
{
    x => match x
    {
        (Some(v) as whole) & !None => 1,
        None => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: false)
        ];

        yield return
        [
            new PatternParityCase(
                "range_guard_false",
                """
classify :: Int -> Int
{
    x => match x
    {
        1..3 when false => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "guard is constant false")
        ];

        yield return
        [
            new PatternParityCase(
                "range_or_with_fallback",
                """
classify :: Int -> Int
{
    x => match x
    {
        1..3 | 4..5 => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: false)
        ];

        yield return
        [
            new PatternParityCase(
                "view_guard_false",
                """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    x => match x
    {
        (normalize -> 1) when false => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "guard is constant false")
        ];

        yield return
        [
            new PatternParityCase(
                "view_not_with_fallback",
                """
normalize :: Int -> Int
{
    n => n
}

classify :: Int -> Int
{
    x => match x
    {
        (normalize -> !0) => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: false)
        ];

        yield return
        [
            new PatternParityCase(
                "tuple_and_partial_non_exhaustive",
                """
classify :: (Bool, Bool) -> Int
{
    x => match x
    {
        (true, _) & (_, false) => 1,
        (false, _) => 0
    }
}
""",
                ExpectW4200: true,
                ExpectW4201: false,
                W4200MessageContains: "missing tuple bool cases: (true, true)",
                W4200NoteContains: "Missing-case witnesses: (true, true)")
        ];

        yield return
        [
            new PatternParityCase(
                "bool_double_not_and_unsatisfiable",
                """
classify :: Bool -> Int
{
    x => match x
    {
        !true & !false => 1,
        _ => 0
    }
}
""",
                ExpectW4200: false,
                ExpectW4201: true,
                W4201MessageContains: "unsatisfiable")
        ];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void CompilationPipeline_PatternParityCoverage_EmitsExpectedDiagnostics(PatternParityCase testCase)
    {
        var result = new CompilationPipeline(testCase.Source, new CompilationOptions
        {
            InputFile = $"pattern_parity_{testCase.Name}.eidos",
            StopAtPhase = CompilationPhase.Namer,
            UseColors = false
        }).Run();

        Assert.True(result.Success);

        var w4200 = result.Diagnostics
            .Where(diagnostic => diagnostic.Code == "W4200")
            .ToList();
        var w4201 = result.Diagnostics
            .Where(diagnostic => diagnostic.Code == "W4201")
            .ToList();

        if (testCase.ExpectW4200)
        {
            Assert.NotEmpty(w4200);
        }
        else
        {
            Assert.Empty(w4200);
        }

        if (testCase.ExpectW4201)
        {
            Assert.NotEmpty(w4201);
        }
        else
        {
            Assert.Empty(w4201);
        }

        if (!string.IsNullOrWhiteSpace(testCase.W4200MessageContains))
        {
            Assert.Contains(
                w4200,
                diagnostic => diagnostic.Message.Contains(testCase.W4200MessageContains, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(testCase.W4201MessageContains))
        {
            Assert.Contains(
                w4201,
                diagnostic => diagnostic.Message.Contains(testCase.W4201MessageContains, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(testCase.W4200NoteContains))
        {
            Assert.Contains(
                w4200,
                diagnostic => diagnostic.Notes.Any(note => note.Contains(testCase.W4200NoteContains, StringComparison.Ordinal)));
        }

        if (!string.IsNullOrWhiteSpace(testCase.W4201NoteContains))
        {
            Assert.Contains(
                w4201,
                diagnostic => diagnostic.Notes.Any(note => note.Contains(testCase.W4201NoteContains, StringComparison.Ordinal)));
        }
    }
}
