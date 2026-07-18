using Eidosc.Symbols;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Eidosc;
using Eidosc.Diagnostic;
using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void StdConcurrencyRuntimeImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_concurrency_runtime_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.DoesNotContain("closure_stack", result.LlvmIrText, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolved_ref__", result.LlvmIrText, StringComparison.Ordinal);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Task__spawn_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Task__await_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Async__spawn_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Async__await_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__TaskGroup__spawn_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__TaskGroup__join_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Barrier__wait_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Channel__receive_status_fold_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Promise__fulfill_raw_if_pending", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Mutex__try_with_lock", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__RwLock__try_with_write", StringComparison.Ordinal));

        Assert.Contains("eidos_task_spawn_closure_raw", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("eidos_task_await_closure_raw", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("eidos_taskgroup_spawn_closure_raw", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("eidos_taskgroup_join_closure_raw", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("eidos_barrier_wait_closure_raw", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void StdAlgebraMonoidImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_algebra_monoid_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Semigroup__append_int", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Semigroup__append3_seq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Semigroup__append3", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Monoid__empty_int", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Monoid__combine_strings", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Monoid__combine", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Monoid__is_empty", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Monoid__law_unit", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Monoid__inverse", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Monoid__multiply", StringComparison.Ordinal));
    }

    [Fact]
    public void StdResultImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_result_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__map", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__map_err", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__FunctorResultWithE__fmap", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__ApplicativeResultWithE__pure", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__ApplicativeResultWithE__apply", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__sequence", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__FoldableResultWithE__fold_left", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__FoldableResultWithE__fold_right", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__MonadResultWithE__bind", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__TraversableResultWithE__traverse", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__and_then", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__map_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__map_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__and_", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__or_", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__unwrap_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__unwrap_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__unwrap_err_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__flatten", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__transpose_option", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__EqResultTe__eq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__OrdResultTe__compare", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__ok", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__err", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__is_ok", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Result__is_err", StringComparison.Ordinal));
    }

    [Fact]
    public void StdTraversableAliasApplicativeFixture_BuildsThroughMir()
    {
        var result = RunFixtureAtMir(Fx("stdlib/std_traversable_alias_applicative.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var applicativeTraitId = ResolveStdApplicativeTrait(symbolTable);

        Assert.Contains(
            symbolTable.Symbols.Values.OfType<ImplSymbol>(),
            impl => impl.Trait == applicativeTraitId &&
                    impl.TraitTypeArgs.Any(arg => arg.Contains("KeepEdges[String,Bool]", StringComparison.Ordinal)));
        Assert.Contains(
            symbolTable.Symbols.Values.OfType<ImplSymbol>(),
            impl => impl.Trait == applicativeTraitId &&
                    impl.TraitTypeArgs.Any(arg => arg.Contains("DeepBoxedResult[String]", StringComparison.Ordinal)));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("traverse_with_alias_applicative__spec_", StringComparison.Ordinal)) >= 2);
        AssertNoUnresolvedMirFunctionReferences(mirModule);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("std__Traversable__map_applicative__spec_", StringComparison.Ordinal)) >= 2);
    }

    [Fact]
    public void StdRangeImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_range_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__make", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__normalize", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__is_normalized", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__is_singleton", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__midpoint_floor", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__contains", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__intersects", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__intersection_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__overlap_len", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__cover", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__is_before", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__is_after", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__shift", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Range__distance", StringComparison.Ordinal));
    }

    [Fact]
    public void StdTraitImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_trait_import.eidos"));

        var errorDetails = string.Join("\n", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => $"{d.Code}: {d.Message} | {string.Join(", ", d.Notes)}"));
        Assert.True(result.Success, $"Phase: {result.CompletedPhase}, Errors:\n{errorDetails}");
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Ordering__ShowOrdering__show", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__ShowString__show", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Ordering__OrdOrdering__compare", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__EqString__eq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__TraitInvoke__ne_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__TraitInvoke__lt_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__TraitInvoke__ge_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__TraitInvoke__min_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__TraitInvoke__max_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__TraitInvoke__clamp_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__TraitInvoke__between_inclusive", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__TraitInvoke__between_exclusive", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__TraitInvoke__hash_bucket_index", StringComparison.Ordinal));
    }

    [Fact]
    public void StdTextImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_text_import.eidos"));

        Assert.True(result.Success, $"Phase: {result.CompletedPhase}, Errors: {string.Join("; ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => $"{d.Code}: {d.Message}"))}");
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__clone", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__from_int", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__from_bool", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__from_code", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__char_code_at", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__char_code_at_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__char_code_at_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__char_at_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__char_at_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__trim_start", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__trim_end", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__trim", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__is_blank", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__is_ascii_alpha_code", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__to_lower_ascii", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__to_upper_ascii", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__capitalize_ascii", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__take", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__drop", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__take_last", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__drop_last", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__index_of_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__index_of_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__last_index_of_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__last_index_of_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__count", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__pad_left", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__join", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__concat", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Text__split", StringComparison.Ordinal));
    }

    [Fact]
    public void StdMathImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_math_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Math__gcd", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Math__pow", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Math__is_zero_f", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Math__square_f", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Math__hypot_f", StringComparison.Ordinal));
    }

    [Fact]
    public void StdFloatMathImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_float_math_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__FloatMath__approx_eq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__FloatMath__clamp", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__FloatMath__hypot", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__FloatMath__degrees_to_radians", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__FloatMath__inverse_lerp", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Declarations,
            declaration => declaration.Name == "sin");
        Assert.DoesNotContain(
            llvmModule.Declarations,
            declaration => declaration.Name == "llvm.sin.f64");
        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("m", llvmModule.LinkLibraries);
        }
        else
        {
            Assert.Contains("m", llvmModule.LinkLibraries);
        }
    }

    [Fact]
    public void StdConsoleImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_console_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__write_line", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__write_int_line", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__write_float_line", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__write_bool_line", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__write_char_line", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__write_text_int_line", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__write_text_bool_line", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__read_line_result", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__read_line_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__read_line_or_empty", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__read_line_or", StringComparison.Ordinal) &&
                        !function.Name.Contains("std__Console__read_line_or_empty", StringComparison.Ordinal) &&
                        !function.Name.Contains("std__Console__read_line_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Console__read_line_or_else", StringComparison.Ordinal));
    }

    [Fact]
    public void StdFfiImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_ffi_import.eidos"));

        Assert.True(
            result.Success,
            $"Completed={result.CompletedPhase}, Errors={result.ErrorCount}, Warnings={result.WarningCount}{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Ffi__alloc_or_null", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Ffi__free_if_non_null", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Ffi__with_calloc", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Ffi__zero_memory", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Ffi__copy_memory", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Ffi__box_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Ffi__unbox_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Ffi__free_boxed_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("destructor_value_box", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => string.Equals(function.Name, WellKnownStrings.Runtime.ModuleInit, StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("use_direct_malloc_free", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Declarations,
            declaration => declaration is { Name: "malloc", Origin: LlvmDeclarationOrigin.ExternalFfi });
        Assert.Contains(
            llvmModule.Declarations,
            declaration => declaration is { Name: "free", Origin: LlvmDeclarationOrigin.ExternalFfi });

        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        Assert.Contains("call ptr @malloc(i64 8)", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @free(ptr", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call ptr @eidos_alloc", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @eidos_incref_shared", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @eidos_decref_shared", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call void @eidos_register_destructor", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolved_ref__", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("@eidos_std__Ffi__malloc", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("@eidos_std__Ffi__free(", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void StdCollectionsJsonTimeImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_collections_json_time_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__JsonParser__parse_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__JsonParser__parse_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__JsonParser__is_valid", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__JsonParser__parse_as_array", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__JsonParser__parse_as_object", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__JsonParser__parse_bool_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__JsonParser__parse_float_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__JsonParser__parse_string_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__JsonParser__parse_array_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__JsonParser__parse_object_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__HashMap__insert_all", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__HashMap__remove_all", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__HashMap__contains_all_keys", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__HashMap__contains_any_key", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__HashSet__insert_all", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__HashSet__remove_all", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__HashSet__toggle", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__HashSet__contains_all", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__HashSet__equals", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Time__add_minutes", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Time__duration_weeks", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Time__duration_clamp", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Time__is_between_inclusive", StringComparison.Ordinal));
    }

    [Fact]
    public void StdRegexImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_regex_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__compile_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__with_compiled", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__with_compiled_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__with_compiled_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__free_if_compiled", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__is_match_text", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__is_match_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__is_match_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__is_match_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__find_text", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__find_text_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__find_text_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__find_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__find_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__Regex__is_valid_pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void StdFileImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_file_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__File__read_text", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__File__read_text_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__File__read_text_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__File__write_text_result", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__File__read_text_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__File__write_text_error_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__File__join_paths", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__File__normalize_separators", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__File__change_extension", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__File__last_success", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("std__File__last_error", StringComparison.Ordinal));
    }

    [Fact]
    public void StdResultImportFixture_MirRewritesTraitInvokeEqHelpers()
    {
        var result = RunFixtureAtMir(Fx("stdlib/std_result_import.eidos"));
        var mir = Assert.IsType<MirModule>(result.MirModule);
        var eqHelpers = mir.Functions
            .Where(function => function.Name.StartsWith("std__TraitInvoke__eq_value", StringComparison.Ordinal))
            .ToList();

        Assert.All(eqHelpers, helper =>
        {
            Assert.Equal(TraitInvokeHelperKind.EqValue, helper.TraitInvokeHelper);
            Assert.True(helper.TraitInvokeHelperTraitId.IsValid, $"Missing trait id for {helper.Name}.");
        });

        var unresolvedHelperCalls = eqHelpers
            .SelectMany(helper => helper.BasicBlocks.SelectMany(block => block.Instructions.OfType<MirCall>())
                .Select(call => (Helper: helper, Call: call)))
            .Where(item => item.Call.Function is MirFunctionRef functionRef &&
                           string.Equals(functionRef.Name, "eq", StringComparison.Ordinal))
            .ToList();

        var unresolvedDetails = string.Join(
            Environment.NewLine,
            unresolvedHelperCalls.Select(item =>
                $"{item.Helper.Name}: target={item.Call.Target?.TypeId.Value.ToString() ?? "<none>"} " +
                $"fn={item.Call.Function.TypeId.Value} " +
                $"args=[{string.Join(", ", item.Call.Arguments.Select(argument => FormatMirOperandType(item.Helper, argument)))}]"));
        unresolvedDetails += Environment.NewLine +
                             FormatTraitImpls(result.SymbolTable, eqHelpers.FirstOrDefault()?.TraitInvokeHelperTraitId ?? SymbolId.None);

        Assert.True(unresolvedHelperCalls.Count == 0, unresolvedDetails);
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => $"{d.Code}: {d.Message}")));
    }

    private static void AssertNoOpenConstructorVariablesInSignature(MirModule module, MirFunc function)
    {
        Assert.False(
            MirGenericAnalysis.ContainsOpenConstructorVariable(
                function.ReturnType,
                module.TypeDescriptors,
                module.DynamicTypeKeys),
            $"Return type of {function.Name} still contains an open constructor variable.");

        foreach (var parameter in function.Locals.Where(static local => local.IsParameter))
        {
            Assert.False(
                MirGenericAnalysis.ContainsOpenConstructorVariable(
                    parameter.TypeId,
                    module.TypeDescriptors,
                    module.DynamicTypeKeys),
                $"Parameter {parameter.Name} of {function.Name} still contains an open constructor variable.");
        }
    }

    private static SymbolId ResolveStdApplicativeTrait(SymbolTable symbolTable)
    {
        var moduleId = symbolTable.Modules.LookupModuleByPath("std", ["Applicative"]);
        Assert.True(moduleId.HasValue);
        Assert.True(symbolTable.Modules.TryLookupAccessibleBinding(
            moduleId.Value,
            "Applicative",
            requesterModuleId: null,
            out var binding));
        return binding.SymbolId;
    }

    private static void AssertCrossModuleAliasApplicativeMetadata(MirModule module, SymbolId applicativeTraitId)
    {
        Assert.Contains(
            module.TraitInfos,
            traitInfo => traitInfo.TraitId == applicativeTraitId &&
                         traitInfo.TypeParameterIds.Count > 0 &&
                         traitInfo.SelfPosition != SelfPosition.Unknown);

        var keepEdges = AssertMirTypeAlias(module, "KeepEdges", typeParameterCount: 3);
        var boxedResult = AssertMirTypeAlias(module, "BoxedResult", typeParameterCount: 2);
        var deepBoxedResult = AssertMirTypeAlias(module, "DeepBoxedResult", typeParameterCount: 2);

        AssertTraitImplCarriesProjectionShapeForAliasTarget(module, applicativeTraitId, keepEdges);
        AssertTraitImplCarriesProjectionShapeForAliasTarget(module, applicativeTraitId, deepBoxedResult);
        Assert.True(boxedResult.AliasId.IsValid);
    }

    private static MirTypeAliasInfo AssertMirTypeAlias(MirModule module, string name, int typeParameterCount)
    {
        var alias = Assert.Single(module.TypeAliases, aliasInfo => string.Equals(aliasInfo.Name, name, StringComparison.Ordinal));
        Assert.True(alias.AliasId.IsValid, $"{name} alias id is missing.");
        Assert.True(alias.TypeId.IsValid, $"{name} alias type id is missing.");
        Assert.True(alias.AliasTarget.IsValid, $"{name} alias target is missing.");
        Assert.Equal(typeParameterCount, alias.TypeParameterIds.Count);
        Assert.Contains(alias.AliasTarget.Value, module.TypeDescriptors.Keys);
        return alias;
    }

    private static void AssertTraitImplCarriesProjectionShapeForAliasTarget(
        MirModule module,
        SymbolId traitId,
        MirTypeAliasInfo alias)
    {
        var aliasTargetConstructorTypeId = ResolveAliasTargetConstructorTypeId(module, alias);
        Assert.True(aliasTargetConstructorTypeId.IsValid, $"{alias.Name} alias target constructor identity is missing.");

        var matchingShapes = module.TraitImpls
            .Where(impl => impl.Trait == traitId)
            .SelectMany(impl => impl.TraitTypeArgShapes)
            .Where(shape => ShapeMentionsConstructorTypeId(shape, aliasTargetConstructorTypeId))
            .ToList();

        Assert.True(
            matchingShapes.Count > 0,
            $"No structured trait arg shape mentions {alias.Name} resolved alias target constructor type {aliasTargetConstructorTypeId.Value}." +
            Environment.NewLine +
            DumpTraitImplShapesForTrait(module, traitId));
        Assert.All(matchingShapes, shape => Assert.True(ShapeUsesOnlyStructuredConstructors(shape)));
    }

    private static string DumpTraitImplShapesForTrait(MirModule module, SymbolId traitId)
    {
        return string.Join(
            Environment.NewLine,
            module.TraitImpls
                .Where(impl => impl.Trait == traitId)
                .Select(impl => $"impl {impl.Id.Value}: {string.Join("; ", impl.TraitTypeArgShapes.Select(FormatImplShape))}"));
    }

    private static string FormatImplShape(ImplTypeShapeNode shape)
    {
        return shape switch
        {
            ImplConstructorShapeNode constructor =>
                $"{constructor.Name}[sym:{constructor.SymbolId.Value},type:{constructor.TypeId.Value}]({string.Join(",", constructor.Args.Select(FormatImplShape))})",
            ImplTupleShapeNode tuple => $"({string.Join(",", tuple.Elements.Select(FormatImplShape))})",
            ImplArrowShapeNode arrow => $"{FormatImplShape(arrow.ParamType)}-" + $">{FormatImplShape(arrow.ReturnType)}",
            ImplEffectfulShapeNode effectful => $"{FormatImplShape(effectful.InputType)}=>{(effectful.OutputType == null ? "Unit" : FormatImplShape(effectful.OutputType))}",
            ImplVariableShapeNode variable => variable.Name,
            ImplWildcardShapeNode => "_",
            _ => shape.GetType().Name
        };
    }

    private static TypeId ResolveAliasTargetConstructorTypeId(MirModule module, MirTypeAliasInfo alias)
    {
        return ResolveAliasTargetConstructorTypeId(module, alias, []);
    }

    private static TypeId ResolveAliasTargetConstructorTypeId(
        MirModule module,
        MirTypeAliasInfo alias,
        HashSet<int> visitedAliasTypeIds)
    {
        if (!module.TypeDescriptors.TryGetValue(alias.AliasTarget.Value, out var descriptor) ||
            descriptor is not TypeDescriptor.TyCon tyCon)
        {
            return TypeId.None;
        }

        var constructorTypeId = ResolveConstructorDescriptorTypeId(module, tyCon.ConstructorDescriptor);
        if (!constructorTypeId.IsValid)
        {
            return TypeId.None;
        }

        var nestedAlias = module.TypeAliases.FirstOrDefault(candidate => candidate.TypeId == constructorTypeId);
        if (nestedAlias != null &&
            nestedAlias.AliasId.IsValid &&
            visitedAliasTypeIds.Add(constructorTypeId.Value))
        {
            var nestedTarget = ResolveAliasTargetConstructorTypeId(module, nestedAlias, visitedAliasTypeIds);
            if (nestedTarget.IsValid)
            {
                return nestedTarget;
            }
        }

        return constructorTypeId;
    }

    private static TypeId ResolveConstructorDescriptorTypeId(MirModule module, string constructorDescriptor)
    {
        if (constructorDescriptor.StartsWith("type:", StringComparison.Ordinal) &&
            int.TryParse(constructorDescriptor["type:".Length..], out var typeIdValue))
        {
            return new TypeId(typeIdValue);
        }

        if (!constructorDescriptor.StartsWith("sym:", StringComparison.Ordinal) ||
            !int.TryParse(constructorDescriptor["sym:".Length..], out var symbolIdValue))
        {
            return TypeId.None;
        }

        var symbolId = new SymbolId(symbolIdValue);
        var typeConstructorTypeId = module.TypeConstructors.FirstOrDefault(typeConstructor => typeConstructor.SymbolId == symbolId)?.TypeId
                                    ?? TypeId.None;
        if (typeConstructorTypeId.IsValid)
        {
            return typeConstructorTypeId;
        }

        return module.TypeAliases.FirstOrDefault(alias => alias.AliasId == symbolId)?.TypeId ?? TypeId.None;
    }

    private static bool ShapeMentionsConstructorTypeId(ImplTypeShapeNode shape, TypeId typeId)
    {
        return shape switch
        {
            ImplConstructorShapeNode constructor =>
                constructor.TypeId == typeId || constructor.Args.Any(arg => ShapeMentionsConstructorTypeId(arg, typeId)),
            ImplTupleShapeNode tuple => tuple.Elements.Any(element => ShapeMentionsConstructorTypeId(element, typeId)),
            ImplArrowShapeNode arrow =>
                ShapeMentionsConstructorTypeId(arrow.ParamType, typeId) ||
                ShapeMentionsConstructorTypeId(arrow.ReturnType, typeId),
            ImplEffectfulShapeNode effectful =>
                ShapeMentionsConstructorTypeId(effectful.InputType, typeId) ||
                (effectful.OutputType != null && ShapeMentionsConstructorTypeId(effectful.OutputType, typeId)),
            _ => false
        };
    }

    private static bool ShapeUsesOnlyStructuredConstructors(ImplTypeShapeNode shape)
    {
        return shape switch
        {
            ImplConstructorShapeNode constructor =>
                (constructor.SymbolId.IsValid || constructor.TypeId.IsValid) &&
                constructor.Args.All(ShapeUsesOnlyStructuredConstructors),
            ImplTupleShapeNode tuple => tuple.Elements.All(ShapeUsesOnlyStructuredConstructors),
            ImplArrowShapeNode arrow =>
                ShapeUsesOnlyStructuredConstructors(arrow.ParamType) &&
                ShapeUsesOnlyStructuredConstructors(arrow.ReturnType),
            ImplEffectfulShapeNode effectful =>
                ShapeUsesOnlyStructuredConstructors(effectful.InputType) &&
                (effectful.OutputType == null || ShapeUsesOnlyStructuredConstructors(effectful.OutputType)),
            ImplVariableShapeNode or ImplWildcardShapeNode => true,
            _ => false
        };
    }

    private static string FormatMirOperandType(MirFunc function, MirOperand operand)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local } place)
        {
            var localType = function.Locals.FirstOrDefault(local => local.Id == place.Local)?.TypeId ?? TypeId.None;
            return $"%{place.Local.Value}:operand={operand.TypeId.Value},local={localType.Value}";
        }

        return $"{operand}:operand={operand.TypeId.Value}";
    }

    private static void AssertNoUnresolvedMirFunctionReferences(MirModule mirModule)
    {
        var unresolvedReferences = mirModule.Functions
            .SelectMany(function => function.BasicBlocks.SelectMany(block => block.Instructions.SelectMany(EnumerateFunctionRefs))
                .Select(functionRef => $"{function.Name} -> {functionRef.Name}"))
            .Where(reference => reference.Contains("unresolved_ref__", StringComparison.Ordinal))
            .ToArray();

        Assert.Empty(unresolvedReferences);
    }

    private static int CountTraversableApplicativeHelperSpecializations(MirModule mirModule)
    {
        return mirModule.Functions.Count(function =>
            function.Name.StartsWith("std__Traversable__map_applicative__spec_", StringComparison.Ordinal) ||
            function.Name.StartsWith("std__Traversable__map2_applicative__spec_", StringComparison.Ordinal));
    }

    private static IEnumerable<MirFunctionRef> EnumerateFunctionRefs(MirInstruction instruction)
    {
        return instruction switch
        {
            MirCall call => EnumerateFunctionRefs(call.Function).Concat(call.Arguments.SelectMany(EnumerateFunctionRefs)),
            MirAssign assign => EnumerateFunctionRefs(assign.Source),
            _ => []
        };
    }

    private static IEnumerable<MirFunctionRef> EnumerateFunctionRefs(MirOperand operand)
    {
        return operand is MirFunctionRef functionRef ? [functionRef] : [];
    }

    private static string FormatTraitImpls(SymbolTable? symbolTable, SymbolId traitId)
    {
        if (symbolTable == null || !traitId.IsValid)
        {
            return "trait impls unavailable";
        }

        return string.Join(
            Environment.NewLine,
            symbolTable.Symbols.Values.OfType<ImplSymbol>()
                .Where(impl => impl.Trait == traitId)
                .Select(impl => $"impl {impl.Id.Value}: type={impl.ImplementingType.Value} methods=[{string.Join(", ", impl.Methods.Select(method => symbolTable.GetSymbol<FuncSymbol>(method)?.Name ?? method.Value.ToString()))}]"));
    }
}
