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
    public void ListComprehensionFunctionFixture_LlvmIrContainsRuntimeArrayApiCalls()
    {
        var result = RunFixtureAtLlvm(Fx("control/list_comp_func.eidos"));
        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        var irLines = llvmIr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToList();

        Assert.Contains(irLines, line => line.Contains("call", StringComparison.Ordinal) &&
                                         line.Contains("@eidos_array_new", StringComparison.Ordinal));
        Assert.Contains(irLines, line => line.Contains("call", StringComparison.Ordinal) &&
                                         line.Contains("@eidos_array_set", StringComparison.Ordinal));
        Assert.Contains(irLines, line => line.Contains("call", StringComparison.Ordinal) &&
                                         line.Contains("@eidos_array_get", StringComparison.Ordinal));
        Assert.Contains(irLines, line => line.Contains("call", StringComparison.Ordinal) &&
                                         line.Contains("@eidos_array_push", StringComparison.Ordinal));
    }

    [Fact]
    public void StdPreludeImportFixture_ImportedFunctionsAreLoweredAsDefinitions()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_prelude_import.eidos"));

        Assert.True(
            result.Success,
            $"Completed={result.CompletedPhase}, Errors={result.ErrorCount}, Warnings={result.WarningCount}{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "W5331");
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        var pipeFunc = Assert.Single(
            llvmModule.Functions,
            function => function.Name.Contains("pipe", StringComparison.Ordinal));
        var composeFunc = Assert.Single(
            llvmModule.Functions,
            function => function.Name.Contains("compose", StringComparison.Ordinal));
        Assert.Equal("i64", pipeFunc.ReturnType.ToIrString());
        Assert.Equal("i64", composeFunc.ReturnType.ToIrString());

        Assert.DoesNotContain(
            llvmModule.Declarations,
            declaration => declaration.Name.Contains("pipe", StringComparison.Ordinal));
        Assert.DoesNotContain(
            llvmModule.Declarations,
            declaration => declaration.Name.Contains("compose", StringComparison.Ordinal));
    }

    [Fact]
    public void StdPreludeImportFixture_IndirectCallsDoNotUseVoidTypedCallOperands()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_prelude_import.eidos"));
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);

        Assert.DoesNotContain("call void %f(", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("call void %g(", llvmIr, StringComparison.Ordinal);

        // Indirect calls through closure objects use closure invoke convention:
        // the invoke function pointer is loaded from the closure struct (via GEP + load),
        // then called. The closure object is passed as the first argument.
        // The loaded invoke pointer has i64 return type under RuntimeWordAbi.
        Assert.Matches(@"call\s+i64\s+%", llvmIr);
    }

    [Fact]
    public void StdPreludeCoreImportFixture_ReexportsMatureHelpers()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_prelude_core_import.eidos"));

        Assert.True(
            result.Success,
            $"Completed={result.CompletedPhase}, Errors={result.ErrorCount}, Warnings={result.WarningCount}{Environment.NewLine}" +
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => diagnostic.ToString())));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.DoesNotContain("closure_stack", result.LlvmIrText, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolved_ref__", result.LlvmIrText, StringComparison.Ordinal);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Text__to_lower_ascii", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("File__read_text_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__map_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Result__contains_err", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__fold", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__select_le", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__select_ge", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__then_compare_ordering", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__compare_int_desc", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__compare_char_desc", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__compare_bool_desc", StringComparison.Ordinal));
    }

    [Fact]
    public void StdPreludeFnPlusFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_prelude_fn_plus.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("apply", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("flip", StringComparison.Ordinal));
    }

    [Fact]
    public void StdFnPlusFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_fn_plus.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Fn__second", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Fn__not_pred", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Fn__and_pred", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Fn__or_pred", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Fn__juxt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Fn__converge", StringComparison.Ordinal));
    }

    [Fact]
    public void StdSeqImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(StdlibListImportFixture());

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Declarations,
            declaration => declaration.Name.Contains("eidos_array_length", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__is_empty", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__head", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__head_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__get_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__get_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__tail", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__tail_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__find", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__find_index", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__last_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__map", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__filter", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__fold_left", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__fold_right", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__count", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__partition", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__none", StringComparison.Ordinal));
    }

    [Fact]
    public void StdSeqImportFixture_StringConcatIsLoweredToRuntimeCall()
    {
        var result = RunFixtureAtLlvm(StdlibListImportFixture());
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);

        Assert.Contains("@eidos_string_concat", llvmIr, StringComparison.Ordinal);
        Assert.DoesNotContain("add ptr", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void StdSeqTraversableAliasApplicativeFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_list_traversable_alias_applicative.eidos"));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => $"{d.Code}: {d.Message}")));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var applicativeTraitId = ResolveStdApplicativeTrait(symbolTable);

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertCrossModuleAliasApplicativeMetadata(mirModule, applicativeTraitId);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("traverse_seq_with_alias_applicative__spec_", StringComparison.Ordinal)) >= 2);
        AssertNoUnresolvedMirFunctionReferences(mirModule);
        Assert.True(CountTraversableApplicativeHelperSpecializations(mirModule) >= 2);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__traverse", StringComparison.Ordinal));
    }

    [Fact]
    public void StdTraversableAliasApplicativeEmptyCasesFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_traversable_alias_applicative_empty_cases.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var applicativeTraitId = ResolveStdApplicativeTrait(symbolTable);

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertCrossModuleAliasApplicativeMetadata(mirModule, applicativeTraitId);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("traverse_option_with_alias_applicative__spec_", StringComparison.Ordinal)) >= 2);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("traverse_seq_with_alias_applicative__spec_", StringComparison.Ordinal)) >= 2);
        AssertNoUnresolvedMirFunctionReferences(mirModule);
        Assert.True(CountTraversableApplicativeHelperSpecializations(mirModule) >= 2);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__TraversableOption__traverse", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__traverse", StringComparison.Ordinal));
    }

    [Fact]
    public void StdTraversableSequenceAliasApplicativeFixture_BuildsThroughMir()
    {
        var result = RunFixtureAtMir(Fx("stdlib/std_traversable_sequence_alias_applicative.eidos"));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => $"{d.Code}: {d.Message}")));
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);

        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var applicativeTraitId = ResolveStdApplicativeTrait(symbolTable);

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        AssertCrossModuleAliasApplicativeMetadata(mirModule, applicativeTraitId);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("sequence_result_with_alias_applicative__spec_", StringComparison.Ordinal)) >= 1);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("sequence_seq_with_alias_applicative__spec_", StringComparison.Ordinal)) >= 1);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("sequence_option_with_alias_applicative__spec_", StringComparison.Ordinal)) >= 1);
    }

    [Fact]
    public void StdSequenceResultApplicativeFixture_BuildsThroughMir()
    {
        var result = RunFixtureAtMir(Fx("stdlib/std_sequence_result_applicative.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.MirModule);

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        Assert.Contains(
            mirModule.Functions,
            function => function.Name.Contains("Option__sequence", StringComparison.Ordinal));
        Assert.Contains(
            mirModule.Functions,
            function => function.Name.Contains("Seq__sequence", StringComparison.Ordinal));
        Assert.Contains(
            mirModule.Functions,
            function => function.Name.Contains("Result__sequence", StringComparison.Ordinal));

        Assert.Contains(
            mirModule.Functions,
            function => function.Name.Contains("collapse_option_result", StringComparison.Ordinal));
        Assert.Contains(
            mirModule.Functions,
            function => function.Name.Contains("collapse_seq_result", StringComparison.Ordinal));
        Assert.Contains(
            mirModule.Functions,
            function => function.Name.Contains("collapse_result_result", StringComparison.Ordinal));
    }

    [Fact]
    public void StdGenericTraversableSequenceFixture_BuildsThroughMir()
    {
        var result = RunFixtureAtMir(Fx("stdlib/std_generic_traversable_sequence.eidos"));

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
            mirModule.Functions.Count(function => function.Name.StartsWith("Std__Traversable__sequence__spec_", StringComparison.Ordinal)) >= 6);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("generic_sequence__spec_", StringComparison.Ordinal)) >= 6);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("Std__Traversable__for_each__spec_", StringComparison.Ordinal)) >= 2);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("generic_for_each__spec_", StringComparison.Ordinal)) >= 2);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("Std__Traversable__sequence_map__spec_", StringComparison.Ordinal)) >= 1);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("generic_sequence_map__spec_", StringComparison.Ordinal)) >= 1);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("Std__Traversable__sequence_void__spec_", StringComparison.Ordinal)) >= 1);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("generic_sequence_void__spec_", StringComparison.Ordinal)) >= 1);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("Std__Traversable__traverse_map__spec_", StringComparison.Ordinal)) >= 1);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("generic_traverse_map__spec_", StringComparison.Ordinal)) >= 1);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("Std__Traversable__for_each_void__spec_", StringComparison.Ordinal)) >= 1);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("generic_for_each_void__spec_", StringComparison.Ordinal)) >= 1);
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("Std__Applicative__map__spec_", StringComparison.Ordinal)) >= 2);
    }

    [Fact]
    public void StdQualifiedTraitMethodPathsFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_qualified_trait_method_paths.eidos"));

        Assert.True(result.Success, string.Join("\n", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => $"{d.Code}: {d.Message}")));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var allFuncNames = string.Join(", ", mirModule.Functions.Select(f => f.Name));
        Assert.True(
            mirModule.Functions.Count(function => function.Name.StartsWith("eq_via_module_trait__spec_", StringComparison.Ordinal)) >= 1,
            $"Expected 'eq_via_module_trait__spec_*' function. All functions: {allFuncNames}");
    }

    [Fact]
    public void StdSeqCountHigherOrderFixture_MirSpecializationCarriesConcretePredicateFunctionType()
    {
        var result = RunFixtureAtMir(Fx("stdlib/std_list_count_native.eidos"));

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);

        Assert.Contains(
            mirModule.DynamicTypeKeys,
            entry => entry.Value.StartsWith(
                $"Fun(T{BaseTypes.IntId})->T{BaseTypes.BoolId}",
                StringComparison.Ordinal));
        var countSpecializations = mirModule.Functions
            .Where(function => function.Name.StartsWith("Std__Seq__count__spec_", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(countSpecializations);
        Assert.All(
            countSpecializations,
            function =>
            {
                var predicateParameter = function.Locals.Where(local => local.IsParameter).Skip(1).First();
                Assert.True(
                    mirModule.DynamicTypeKeys.TryGetValue(predicateParameter.TypeId.Value, out var predicateTypeKey),
                    $"Missing dynamic type key for {function.Name} parameter type {predicateParameter.TypeId}.");
                Assert.StartsWith(
                    $"Fun(T{BaseTypes.IntId})->T{BaseTypes.BoolId}",
                    predicateTypeKey,
                    StringComparison.Ordinal);
            });
    }

    [Fact]
    public void SourceGenericTraitHelper_WithSpecializedImpl_RewritesMirToMostSpecificMethod()
    {
        const string source = """
Show :: trait {
    show :: Self -> Int
}

Option[T] :: type {
    Some(T) , None
}

@impl(Show)
show[T] :: Option[T] -> Int
{
    _ => 0
}

@impl(Show)
show :: Option[Int] -> Int
{
    _ => 1
}

render[T: Show] :: T -> Int
{
    value => show(value)
}

main :: Unit -> Int
{
    _ => render(Some(1))
}
""";

        var result = RunSourceAtMir(source, "mir_trait_specialization_source.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);

        var showTraitId = Assert.IsType<SymbolId>(symbolTable.LookupType("Show")!.Value);
        var optionSymbolId = Assert.IsType<SymbolId>(symbolTable.LookupType("Option")!.Value);
        var optionTypeId = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(optionSymbolId)).TypeId;
        var specializedImpl = symbolTable.LookupImplForTrait(optionTypeId, showTraitId, "Option[Int]");

        Assert.NotNull(specializedImpl);
        var specializedMethodId = Assert.Single(specializedImpl!.Methods);

        var selectedShow = Assert.Single(mirModule.Functions, function => function.Name == "show");
        var selectedShowReturn = Assert.IsType<MirReturn>(selectedShow.BasicBlocks.Single().Terminator);
        var selectedShowValue = Assert.IsType<MirConstant>(selectedShowReturn.Value);
        var selectedShowInt = Assert.IsType<MirConstantValue.IntValue>(selectedShowValue.Value);

        Assert.Equal(1, selectedShowInt.Value);

        var specializedRender = Assert.Single(
            mirModule.Functions,
            function => function.Name.StartsWith("render__spec_", StringComparison.Ordinal));
        var specializedRenderCall = Assert.Single(
            specializedRender.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var specializedRenderTarget = Assert.IsType<MirFunctionRef>(specializedRenderCall.Function);

        Assert.Equal(selectedShow.Name, specializedRenderTarget.Name);
        Assert.Equal(specializedMethodId, specializedRenderTarget.SymbolId);

        var main = Assert.Single(mirModule.Functions, function => function.Name == "main");
        var mainCall = Assert.Single(
            main.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>(),
            call => call.Function is MirFunctionRef { Name: var name } &&
                    name.StartsWith("render__spec_", StringComparison.Ordinal));
        var mainTarget = Assert.IsType<MirFunctionRef>(mainCall.Function);

        Assert.StartsWith("render__spec_", mainTarget.Name, StringComparison.Ordinal);
    }

    [Fact]
    public void SourceImportedGenericAdt_SpecializedConstructorLayoutContainsConcreteFieldType()
    {
        const string source = """
import M

M :: module {
    Box[T] :: type {
        Box(T)
    }

    make_box[T] :: T -> Box[T]
    {
        value => Box(value)
    }
}

main :: Unit -> Int
{
    _ => {
        box := make_box(1);
        0
    }
}
""";

        var result = RunSourceAtMir(source, "mir_cross_module_generic_adt_layout.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var intType = new TypeId(BaseTypes.IntId);
        var boxIntLayouts = mirModule.ConstructorLayouts.Values
            .SelectMany(static layouts => layouts)
            .Where(layout => string.Equals(layout.ConstructorName, "Box", StringComparison.Ordinal))
            .Where(layout => layout.FieldTypeIds.SequenceEqual([intType]))
            .ToList();

        Assert.Single(boxIntLayouts);
    }

    [Fact]
    public void SourceImportedExplicitGenericCall_MirFunctionRefCarriesConcreteSignatureDescriptor()
    {
        const string source = """
import M

M :: module {
    id[T] :: T -> T
    {
        value => value
    }
}

main :: Unit -> Int
{
    _ => id[Int](1)
}
""";

        var result = RunSourceAtMir(source, "mir_imported_explicit_generic_call.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var main = Assert.Single(mirModule.Functions, function => function.Name == "main");
        var call = Assert.Single(main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>());
        var functionRef = Assert.IsType<MirFunctionRef>(call.Function);
        var intType = new TypeId(BaseTypes.IntId);

        Assert.True(functionRef.SignatureTypeId.IsValid);
        Assert.True(mirModule.TypeDescriptors.ContainsKey(functionRef.SignatureTypeId.Value));
        var descriptor = Assert.IsType<TypeDescriptor.Function>(mirModule.TypeDescriptors[functionRef.SignatureTypeId.Value]);
        Assert.Equal(intType, descriptor.ReturnType);
        Assert.Equal(intType, Assert.Single(descriptor.ParamTypes));
    }

    [Fact]
    public void SourceImportedExplicitGenericFunctionValue_MirFunctionRefCarriesConcreteSignatureDescriptor()
    {
        const string source = """
import M

M :: module {
    id[T] :: T -> T
    {
        value => value
    }
}

main :: Unit -> Int
{
    _ => {
        f: Int -> Int := id[Int];
        f(1)
    }
}
""";

        var result = RunSourceAtMir(source, "mir_imported_explicit_generic_function_value.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var main = Assert.Single(mirModule.Functions, function => function.Name == "main");
        var assignment = Assert.Single(
            main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirAssign>(),
            assign => assign.Source is MirFunctionRef { TypeArgumentIds.Count: 1 });
        var functionRef = Assert.IsType<MirFunctionRef>(assignment.Source);
        var intType = new TypeId(BaseTypes.IntId);

        Assert.True(functionRef.SignatureTypeId.IsValid);
        Assert.True(mirModule.TypeDescriptors.ContainsKey(functionRef.SignatureTypeId.Value));
        var descriptor = Assert.IsType<TypeDescriptor.Function>(mirModule.TypeDescriptors[functionRef.SignatureTypeId.Value]);
        Assert.Equal(intType, descriptor.ReturnType);
        Assert.Equal(intType, Assert.Single(descriptor.ParamTypes));
    }

    [Fact]
    public void SourceImportedGenericAdtConstructorCall_MirFunctionRefCarriesConcreteTyConDescriptor()
    {
        const string source = """
import M

M :: module {
    Box[T] :: type {
        NewBox(T)
    }
}

main :: Unit -> Int
{
    _ => {
        boxed: Box[Int] := NewBox(1);
        0
    }
}
""";

        var result = RunSourceAtMir(source, "mir_imported_generic_adt_constructor_call.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var main = Assert.Single(mirModule.Functions, function => function.Name == "main");
        var constructorCall = Assert.Single(
            main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(),
            call => call.Function is MirFunctionRef { Name: "NewBox" });
        var functionRef = Assert.IsType<MirFunctionRef>(constructorCall.Function);
        var intType = new TypeId(BaseTypes.IntId);

        Assert.True(functionRef.TypeId.IsValid);
        Assert.True(mirModule.TypeDescriptors.ContainsKey(functionRef.TypeId.Value));
        var descriptor = Assert.IsType<TypeDescriptor.TyCon>(mirModule.TypeDescriptors[functionRef.TypeId.Value]);
        Assert.Equal(intType, Assert.Single(descriptor.TypeArgs));
    }

    [Fact]
    public void SourceGenericTraitHelper_WithSpecializedImpl_NativeSmoke_ReturnsConcreteImplValue()
    {
        const string source = """
Show :: trait {
    show :: Self -> Int
}

Option[T] :: type {
    Some(T) , None
}

@impl(Show)
show[T] :: Option[T] -> Int
{
    _ => 0
}

@impl(Show)
show :: Option[Int] -> Int
{
    _ => 1
}

render[T: Show] :: T -> Int
{
    value => show(value)
}

main :: Unit -> Int
{
    _ => render(Some(1))
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_trait_specialization_source.eidos",
            "native_trait_specialization_source");

        Assert.Equal(1, execution.ExitCode);
    }

    [Fact]
    public void SourceTraitArgSpecializedImpl_RewritesDirectTraitMethodCallToConcreteImpl()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Result[T, E] :: type {
    Ok(T) , Err(E)
}

ResultWith[E, T] :: type = Result[T, E];

@impl(Applicative[ResultWith[E]])
pure[A, E] :: A -> ResultWith[E, A]
{
    value => Ok(value)
}

@impl(Applicative[ResultWith[String]])
pure[A] :: A -> ResultWith[String, A]
{
    value => Ok(value)
}

make :: Unit -> Result[Int, String]
{
    _ => pure(1)
}
""";

        var result = RunSourceAtMir(source, "mir_trait_arg_specialization_source.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var applicativeTraitId = Assert.IsType<SymbolId>(symbolTable.LookupType("Applicative")!.Value);
        var applicativeTrait = Assert.IsType<TraitSymbol>(symbolTable.GetSymbol(applicativeTraitId));
        var traitMethodId = Assert.Single(applicativeTrait.Methods);
        var make = Assert.Single(mirModule.Functions, function => function.Name == "make");
        var makeCall = Assert.Single(make.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var makeTarget = Assert.IsType<MirFunctionRef>(makeCall.Function);
        Assert.Single(
            symbolTable.Symbols.Values.OfType<ImplSymbol>(),
            impl => impl.Trait == applicativeTraitId &&
                    impl.TraitTypeArgs.Any(arg => arg.Contains("ResultWith[String]", StringComparison.Ordinal)));

        Assert.StartsWith("pure", makeTarget.Name, StringComparison.Ordinal);
        Assert.True(makeTarget.SymbolId.IsValid);
        Assert.NotEqual(traitMethodId, makeTarget.SymbolId);
        Assert.DoesNotContain(
            mirModule.Functions,
            function => function.Name == "make" &&
                        function.BasicBlocks.SelectMany(block => block.Instructions)
                            .OfType<MirCall>()
                            .Any(call => call.Function is MirFunctionRef { Name: "pure", SymbolId: var symbolId } &&
                                         !symbolId.IsValid));
    }

    [Fact]
    public void SourceTraitArgSpecializedGenericHelper_RewritesInnerTraitCallToConcreteImpl()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Result[T, E] :: type {
    Ok(T) , Err(E)
}

ResultWith[E, T] :: type = Result[T, E];

@impl(Applicative[ResultWith[E]])
pure[A, E] :: A -> ResultWith[E, A]
{
    value => Ok(value)
}

@impl(Applicative[ResultWith[String]])
pure[A] :: A -> ResultWith[String, A]
{
    value => Ok(value)
}

lift[A, G: kind2 : Applicative[G]] :: A -> G[A]
{
    value => pure(value)
}

make :: Unit -> Result[Int, String]
{
    _ => lift(1)
}
""";

        var result = RunSourceAtMir(source, "mir_trait_arg_specialization_generic_helper_source.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var applicativeTraitId = Assert.IsType<SymbolId>(symbolTable.LookupType("Applicative")!.Value);
        var applicativeTrait = Assert.IsType<TraitSymbol>(symbolTable.GetSymbol(applicativeTraitId));
        var traitMethodId = Assert.Single(applicativeTrait.Methods);
        var make = Assert.Single(mirModule.Functions, function => function.Name == "make");
        var makeCall = Assert.Single(make.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var makeTarget = Assert.IsType<MirFunctionRef>(makeCall.Function);

        Assert.StartsWith("lift__spec_", makeTarget.Name, StringComparison.Ordinal);

        var specializedLift = Assert.Single(
            mirModule.Functions,
            function => function.Name == makeTarget.Name);
        var specializedLiftCall = Assert.Single(
            specializedLift.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var specializedLiftTarget = Assert.IsType<MirFunctionRef>(specializedLiftCall.Function);
        Assert.Single(
            symbolTable.Symbols.Values.OfType<ImplSymbol>(),
            impl => impl.Trait == applicativeTraitId &&
                    impl.TraitTypeArgs.Any(arg => arg.Contains("ResultWith[String]", StringComparison.Ordinal)));

        Assert.StartsWith("pure", specializedLiftTarget.Name, StringComparison.Ordinal);
        Assert.True(specializedLiftTarget.SymbolId.IsValid);
        Assert.NotEqual(traitMethodId, specializedLiftTarget.SymbolId);
    }

    [Fact]
    public void SourceHigherKindedApplicativeGenericHelper_NativeSmoke_ReturnsLiftedValue()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Result[T, E] :: type {
    Ok(T) , Err(E)
}

ResultWith[E, T] :: type = Result[T, E];

@impl(Applicative[ResultWith[String]])
pure[A] :: A -> ResultWith[String, A]
{
    value => Ok(value)
}

lift[A, G: kind2 : Applicative[G]] :: A -> G[A]
{
    value => pure(value)
}

main :: Unit -> Int
{
    _ => {
        lifted: Result[Int, String] := lift(7);
        match lifted
        {
            Ok(value) => value,
            Err(_) => 0
        }
    }
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_hkt_applicative_generic_helper.eidos",
            "native_hkt_applicative_generic_helper");

        Assert.Equal(7, execution.ExitCode);
    }

    [Fact]
    public void SourceReExportedNestedTraitHelper_NativeSmoke_UsesConcreteImpl()
    {
        const string source = """
import TraitReexport.Facade

main :: Unit -> Int
{
    _ => Facade.BaseApi.render(Facade.BaseApi.make(11))
}
""";

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_reexported_nested_trait_helper.eidos",
            "native_reexported_nested_trait_helper",
            additionalFiles: new Dictionary<string, string>
            {
                ["TraitReexport/Base.eidos"] = """
                    TraitReexport.Base :: module
                    {
                        Score :: trait {
                            score :: Self -> Int
                        }

                        Box :: type {
                            Box(Int)
                        }

                        @impl(Score)
                        score :: Box -> Int
                        {
                            Box(value) => value
                        }

                        render[T: Score] :: T -> Int
                        {
                            value => score(value)
                        }

                        make :: Int -> Box
                        {
                            value => Box(value)
                        }
                    }
                    """,
                ["TraitReexport/Facade.eidos"] = """
                    TraitReexport.Facade :: module
                    {
                        export BaseApi :: import TraitReexport.Base;
                    }
                    """
            });

        Assert.Equal(11, execution.ExitCode);
    }

    [Fact]
    public void SourceOpenAliasTraitArgSpecializedImpl_RewritesDirectTraitMethodCallToConcreteImpl()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Triple[A, B, C] :: type {
    Triple(A, B, C)
}

KeepEdges[L, R, X] :: type = Triple[L, X, R];

@impl(Applicative[KeepEdges[String, Bool]])
pure[A] :: A -> KeepEdges[String, Bool, A]
{
    value => Triple("ctx", value, true)
}

make :: Unit -> Triple[String, Int, Bool]
{
    _ => pure(1)
}
""";

        var result = RunSourceAtMir(source, "mir_open_alias_trait_arg_specialization_source.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var applicativeTraitId = Assert.IsType<SymbolId>(symbolTable.LookupType("Applicative")!.Value);
        var applicativeTrait = Assert.IsType<TraitSymbol>(symbolTable.GetSymbol(applicativeTraitId));
        var traitMethodId = Assert.Single(applicativeTrait.Methods);
        var make = Assert.Single(mirModule.Functions, function => function.Name == "make");
        var makeCall = Assert.Single(make.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var makeTarget = Assert.IsType<MirFunctionRef>(makeCall.Function);

        Assert.Single(
            symbolTable.Symbols.Values.OfType<ImplSymbol>(),
            impl => impl.Trait == applicativeTraitId &&
                    impl.TraitTypeArgs.Any(arg => arg.Contains("KeepEdges[String,Bool]", StringComparison.Ordinal)));

        Assert.StartsWith("pure", makeTarget.Name, StringComparison.Ordinal);
        Assert.True(makeTarget.SymbolId.IsValid);
        Assert.NotEqual(traitMethodId, makeTarget.SymbolId);
    }

    [Fact]
    public void SourceOpenAliasTraitArgSpecializedGenericHelper_RewritesInnerTraitCallToConcreteImpl()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Triple[A, B, C] :: type {
    Triple(A, B, C)
}

KeepEdges[L, R, X] :: type = Triple[L, X, R];

@impl(Applicative[KeepEdges[String, Bool]])
pure[A] :: A -> KeepEdges[String, Bool, A]
{
    value => Triple("ctx", value, true)
}

lift[A, G: kind2 : Applicative[G]] :: A -> G[A]
{
    value => pure(value)
}

make :: Unit -> Triple[String, Int, Bool]
{
    _ => lift(1)
}
""";

        var result = RunSourceAtMir(source, "mir_open_alias_trait_arg_specialization_generic_helper_source.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var applicativeTraitId = Assert.IsType<SymbolId>(symbolTable.LookupType("Applicative")!.Value);
        var applicativeTrait = Assert.IsType<TraitSymbol>(symbolTable.GetSymbol(applicativeTraitId));
        var traitMethodId = Assert.Single(applicativeTrait.Methods);
        var make = Assert.Single(mirModule.Functions, function => function.Name == "make");
        var makeCall = Assert.Single(make.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var makeTarget = Assert.IsType<MirFunctionRef>(makeCall.Function);

        Assert.StartsWith("lift__spec_", makeTarget.Name, StringComparison.Ordinal);

        var specializedLift = Assert.Single(
            mirModule.Functions,
            function => function.Name == makeTarget.Name);
        var specializedLiftCall = Assert.Single(
            specializedLift.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>());
        var specializedLiftTarget = Assert.IsType<MirFunctionRef>(specializedLiftCall.Function);

        Assert.Single(
            symbolTable.Symbols.Values.OfType<ImplSymbol>(),
            impl => impl.Trait == applicativeTraitId &&
                    impl.TraitTypeArgs.Any(arg => arg.Contains("KeepEdges[String,Bool]", StringComparison.Ordinal)));

        Assert.StartsWith("pure__spec_", specializedLiftTarget.Name, StringComparison.Ordinal);
        Assert.True(specializedLiftTarget.SymbolId.IsValid);
        Assert.NotEqual(traitMethodId, specializedLiftTarget.SymbolId);
    }

}
