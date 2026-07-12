using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Symbols;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class ComptimeBindingTests
{
    [Fact]
    public void Namer_NameFirstComptimeBinding_RegistersComptimeSymbol()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime 64;
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Namer);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var symbolId = symbolTable.LookupValue("DefaultCapacity");
        Assert.True(symbolId.HasValue);
        var symbol = Assert.IsType<VarSymbol>(symbolTable.GetSymbol(symbolId.Value));
        Assert.True(symbol.IsComptime);
        Assert.False(symbol.IsMutable);
    }

    [Fact]
    public void Namer_NameFirstComptimeFunction_RegistersComptimeSymbol()
    {
        var result = RunNameFirst(
            """
            fieldCount :: comptime Unit -> Int { _ => 1 }
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Namer);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var symbolId = symbolTable.LookupValue("fieldCount");
        Assert.True(symbolId.HasValue);
        var symbol = Assert.IsType<FuncSymbol>(symbolTable.GetSymbol(symbolId.Value));
        Assert.True(symbol.IsComptime);
    }

    [Fact]
    public void Types_RuntimeCallToComptimeFunction_ReportsPhaseBoundary()
    {
        var result = RunNameFirst(
            """
            fieldCount :: comptime Unit -> Int { _ => 1 }
            main :: Unit -> Int { _ => fieldCount(()) }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Cannot use comptime-only function 'fieldCount' from runtime code.", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeFunctionWithExplicitEffect_ReportsEffectBoundary()
    {
        var result = RunNameFirst(
            """
            allocate :: comptime Int -> Int need FFI { size => size }
            DefaultCapacity :: comptime allocate(8);
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Comptime-only function 'allocate' must be pure", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("comptime boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void Abilities_ComptimeFunctionWithImplicitFfi_ReportsEffectBoundary()
    {
        var result = RunNameFirst(
            """
            @ffi("malloc") malloc :: Int -> RawPtr;
            allocate :: comptime Int -> RawPtr { size => malloc(size) }
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Effects);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Comptime-only function 'allocate' must be pure", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("comptime boundary", StringComparison.Ordinal));
    }

    [Fact]
    public void Namer_ComptimeLocalAssignment_ReportsComptimeAssignment()
    {
        var result = RunNameFirst(
            """
            main :: Unit -> Int {
              _ => {
                comptime size := 32;
                size = 33;
                size
              }
            }
            """,
            CompilationPhase.Namer);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Cannot assign to comptime binding 'size'", StringComparison.Ordinal));
    }

    [Fact]
    public void Hir_ComptimeBinding_PreservesComptimePhase()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime 64;
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Hir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var module = Assert.IsType<HirModule>(result.HirModule);
        var value = Assert.Single(module.Declarations.OfType<HirVal>(), val => val.Name == "DefaultCapacity");
        Assert.True(value.IsComptime);
        Assert.Contains("ComptimeVal DefaultCapacity", HirFormatter.FormatHir(module), StringComparison.Ordinal);
    }

    [Fact]
    public void Types_ModuleComptimeLiteralExpression_Succeeds()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime 40 + 2;
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success);
    }

    [Fact]
    public void Types_LocalComptimeLiteralExpression_Succeeds()
    {
        var result = RunNameFirst(
            """
            main :: Unit -> Int {
              _ => {
                comptime size := 16 * 2;
                size
              }
            }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success);
    }

    [Fact]
    public void Types_ComptimeBindingCanReferenceEarlierComptimeBinding()
    {
        var result = RunNameFirst(
            """
            BaseCapacity :: comptime 40;
            DefaultCapacity :: comptime BaseCapacity + 2;
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeBindingCannotReferenceRuntimeStaticBinding()
    {
        var result = RunNameFirst(
            """
            BaseCapacity :: 40;
            DefaultCapacity :: comptime BaseCapacity + 2;
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("not a previously evaluated comptime binding", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_LocalComptimeBindingCanReferenceEarlierLocalComptimeBinding()
    {
        var result = RunNameFirst(
            """
            main :: Unit -> Int {
              _ => {
                comptime base := 16;
                comptime size := base * 2;
                size
              }
            }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeFunctionCall_EvaluatesArgumentBinding()
    {
        var result = RunNameFirst(
            """
            double :: comptime Int -> Int { value => value * 2 }
            DefaultCapacity :: comptime double(21);
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Types_ComptimeFunctionCall_EvaluatesZeroArgumentUnitCall()
    {
        var result = RunNameFirst(
            """
            answer :: comptime Unit -> Int { _ => 42 }
            DefaultCapacity :: comptime answer();
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeFunctionCall_EvaluatesGroupedMultiArgumentCall()
    {
        var result = RunNameFirst(
            """
            add :: comptime Int -> Int -> Int { left => right => left + right }
            DefaultCapacity :: comptime add(20, 22);
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Types_ComptimeFunctionBodyCanCallComptimeFunction()
    {
        var result = RunNameFirst(
            """
            double :: comptime Int -> Int { value => value * 2 }
            quadruple :: comptime Int -> Int { value => double(double(value)) }
            DefaultCapacity :: comptime quadruple(10) + 2;
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Types_RuntimeFunctionBodyCannotCallComptimeFunction()
    {
        var result = RunNameFirst(
            """
            fieldCount :: comptime Unit -> Int { _ => 1 }
            runtime :: Unit -> Int { _ => fieldCount(()) }
            main :: Unit -> Int { _ => runtime(()) }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Cannot use comptime-only function 'fieldCount' from runtime code.", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeIfExpression_EvaluatesSelectedThenBranch()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime if true then { 40 + 2 } else { 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Types_ComptimeIfExpression_EvaluatesSelectedElseBranch()
    {
        var result = RunNameFirst(
            """
            UseDefault :: comptime false;
            DefaultCapacity :: comptime if UseDefault then { 0 } else { 40 + 2 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeIfExpression_DoesNotEvaluateUnselectedBranch()
    {
        var result = RunNameFirst(
            """
            compute :: Unit -> Int { _ => 0 }
            DefaultCapacity :: comptime if true then { 42 } else { compute() };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeIfExpression_RejectsNonBoolCondition()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime if 1 then { 42 } else { 0 };
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("if condition must evaluate to a comptime bool", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesLiteralBranch()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 2 { 1 => 10, 2 => 42, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Types_ComptimeMatchExpression_DoesNotEvaluateUnselectedBranch()
    {
        var result = RunNameFirst(
            """
            compute :: Unit -> Int { _ => 0 }
            DefaultCapacity :: comptime match 2 { 1 => compute(), 2 => 42 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesOrRangeAndWildcardPatterns()
    {
        var result = RunNameFirst(
            """
            Tier :: comptime 4;
            DefaultCapacity :: comptime match Tier {
              0 | 1 => 16,
              2..4 => 42,
              _ => 0
            };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesTuplePattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match (1, 2) { (1, 2) => 42, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesTupleBindingPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match (40, 2) { (base, extra) => base + extra, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesListPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match [1, 2, 3] { [1, 2, 3] => 42, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesListBindingPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match [40, 2] { [base, extra] => base + extra, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesListRestDiscardPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match [1, 2, 3] { [1, .._] => 42, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesListRestBindingPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match [1, 2, 3] {
              [1, ..tail] => match tail { [2, 3] => 42, _ => 0 },
              _ => 0
            };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesGuardWithBinding()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 2 {
              value when value > 10 => 0,
              value when value > 1 => value + 40,
              _ => 0
            };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesPatternGuardBinding()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 0 {
              _ when [base, extra] <- [40, 2] => base + extra,
              _ => 0
            };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_RejectsNonBoolGuard()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 2 { value when value => 42, _ => 0 };
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("match guard must evaluate to a comptime bool", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_EvaluatesScalarBindingPattern()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 2 { value => value + 40, _ => 0 };
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Types);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
    }

    [Fact]
    public void Types_ComptimeMatchExpression_RejectsNoMatchingBranch()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime match 2 { 1 => 10 };
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("no matching branch", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeCallExpression_Fails()
    {
        var result = RunNameFirst(
            """
            compute :: Unit -> Int { _ => 42 }
            DefaultCapacity :: comptime compute();
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("comptime binding RHS must be evaluable", StringComparison.Ordinal) &&
                diagnostic.Message.Contains("not a comptime-only function", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ComptimeDivisionByZero_Fails()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime 1 / 0;
            main :: Unit -> Int { _ => 0 }
            """,
            CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("integer division by zero", StringComparison.Ordinal));
    }

    [Fact]
    public void Mir_ModuleComptimeBinding_IsInlinedAndDoesNotEmitModuleValueGetter()
    {
        var result = RunNameFirst(
            """
            DefaultCapacity :: comptime 40 + 2;
            main :: Unit -> Int { _ => DefaultCapacity }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        Assert.DoesNotContain(mirModule.Functions, function => function.Name.Contains("__module_val__", StringComparison.Ordinal));

        AssertMirContainsIntConstant(mirModule, 42);
    }

    [Fact]
    public void Mir_LocalComptimeBindings_DoNotCreateRuntimeLocals()
    {
        var result = RunNameFirst(
            """
            main :: Unit -> Int {
              _ => {
                comptime base := 16;
                comptime size := base * 2;
                0
              }
            }
            """,
            CompilationPhase.Mir);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.Message)));
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var main = Assert.Single(mirModule.Functions, function => function.Name == "main");

        Assert.DoesNotContain(main.Locals, local => local.Name == "base");
        Assert.DoesNotContain(main.Locals, local => local.Name == "size");
    }

    private static void AssertMirContainsIntConstant(MirModule module, long expected)
    {
        Assert.Contains($"return {expected}", MirFormatter.FormatMir(module), StringComparison.Ordinal);
    }

    private static CompilationResult RunNameFirst(string source, CompilationPhase stopAtPhase)
    {
        return new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "comptime_binding.eidos",
            StopAtPhase = stopAtPhase,
            LanguageVersion = EidosLanguageVersions.Current,
            EnableMirOptimizations = false,
            UseColors = false
        }).Run();
    }
}
