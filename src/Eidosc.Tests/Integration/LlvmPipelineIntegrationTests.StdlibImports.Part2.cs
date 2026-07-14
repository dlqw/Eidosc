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
    public void SourceDeepAliasTraitArgSpecializedImpl_RewritesDirectTraitMethodCallToConcreteImpl()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Result[T, E] :: type {
    Ok(T) , Err(E)
}

ResultWith[E, T] :: type = Result[T, E];
BoxedResult[E, T] :: type = ResultWith[E, T];
DeepBoxedResult[E, T] :: type = BoxedResult[E, T];

@impl(Applicative[DeepBoxedResult[String]])
pure[A] :: A -> DeepBoxedResult[String, A]
{
    value => Ok(value)
}

make :: Unit -> Result[Int, String]
{
    _ => pure(1)
}
""";

        var result = RunSourceAtMir(source, "mir_deep_alias_trait_arg_specialization_source.eidos");

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
                    impl.TraitTypeArgs.Any(arg => arg.Contains("DeepBoxedResult[String]", StringComparison.Ordinal)));

        Assert.StartsWith("pure", makeTarget.Name, StringComparison.Ordinal);
        Assert.True(makeTarget.SymbolId.IsValid);
        Assert.NotEqual(traitMethodId, makeTarget.SymbolId);
    }

    [Fact]
    public void SourceDeepAliasTraitArgSpecializedGenericHelper_RewritesInnerTraitCallToConcreteImpl()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Result[T, E] :: type {
    Ok(T) , Err(E)
}

ResultWith[E, T] :: type = Result[T, E];
BoxedResult[E, T] :: type = ResultWith[E, T];
DeepBoxedResult[E, T] :: type = BoxedResult[E, T];

@impl(Applicative[DeepBoxedResult[String]])
pure[A] :: A -> DeepBoxedResult[String, A]
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

        var result = RunSourceAtMir(source, "mir_deep_alias_trait_arg_specialization_generic_helper_source.eidos");

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
                    impl.TraitTypeArgs.Any(arg => arg.Contains("DeepBoxedResult[String]", StringComparison.Ordinal)));

        Assert.StartsWith("pure__spec_", specializedLiftTarget.Name, StringComparison.Ordinal);
        Assert.True(specializedLiftTarget.SymbolId.IsValid);
        Assert.NotEqual(traitMethodId, specializedLiftTarget.SymbolId);
    }

    [Fact]
    public void SourceDeepAliasTraitConstrainedHigherOrderCall_CompilesThroughLlvm()
    {
        const string source = """
Applicative[F: kind2] :: trait {
    pure[A] :: A -> F[A]
}

Result[T, E] :: type {
    Ok(T) , Err(E)
}

ResultWith[E, T] :: type = Result[T, E];
BoxedResult[E, T] :: type = ResultWith[E, T];
DeepBoxedResult[E, T] :: type = BoxedResult[E, T];

@impl(Applicative[DeepBoxedResult[String]])
pure[A] :: A -> DeepBoxedResult[String, A]
{
    value => Ok(value)
}

use[A, B, G: kind2 : Applicative[G]] :: (A -> G[B]) -> A -> G[B]
{
    f => value => f(value)
}

positive_result :: Int -> Result[Int, String]
{
    x => if x > 0 then { Ok(x + 1) } else { Err("bad") }
}

main :: Unit -> Int
{
    _ => match use(positive_result)(2)
    {
        Ok(v) => v,
        Err(_) => 0
    }
}
""";

        var result = RunSourceAtLlvm(source, "llvm_deep_alias_trait_higher_order_source.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var useSpecialization = Assert.Single(
            mirModule.Functions,
            function => function.Name.StartsWith("use__spec_", StringComparison.Ordinal));
        Assert.DoesNotContain(
            mirModule.SpecializationFailures,
            failure => failure.Reason == "unresolved-constructor-binding");
        AssertNoOpenConstructorVariablesInSignature(mirModule, useSpecialization);
    }

    [Fact]
    public void SourceOpenAliasTraitConstrainedHigherOrderCall_CompilesThroughLlvm()
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

use[A, B, G: kind2 : Applicative[G]] :: (A -> G[B]) -> A -> G[B]
{
    f => value => f(value)
}

produce :: Int -> Triple[String, Int, Bool]
{
    x => Triple("ctx", x + 1, true)
}

main :: Unit -> Int
{
    _ => match use(produce)(2)
    {
        Triple(_, value, true) => value,
        Triple(_, _, false) => 0
    }
}
""";

        var result = RunSourceAtLlvm(source, "llvm_open_alias_trait_higher_order_source.eidos");

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var useSpecialization = Assert.Single(
            mirModule.Functions,
            function => function.Name.StartsWith("use__spec_", StringComparison.Ordinal));
        Assert.DoesNotContain(
            mirModule.SpecializationFailures,
            failure => failure.Reason == "unresolved-constructor-binding");
        AssertNoOpenConstructorVariablesInSignature(mirModule, useSpecialization);
    }

    [Fact]
    public void StdSeqAppendTakeDropReverseFixture_MirSpecializesContainerOnlySeqBindings()
    {
        var result = RunFixtureAtMir(Fx("stdlib/std_list_append_take_drop_reverse_native.eidos"));

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);

        Assert.Contains(
            mirModule.Functions,
            function => function.Name.StartsWith("Std__Seq__append__spec_", StringComparison.Ordinal));
        Assert.Contains(
            mirModule.Functions,
            function => function.Name.StartsWith("Std__Seq__take__spec_", StringComparison.Ordinal));
        Assert.Contains(
            mirModule.Functions,
            function => function.Name.StartsWith("Std__Seq__drop__spec_", StringComparison.Ordinal));
        Assert.Contains(
            mirModule.Functions,
            function => function.Name.StartsWith("Std__Seq__reverse__spec_", StringComparison.Ordinal));
    }

    [Fact]
    public void StdSeqPlusImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_list_plus.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__head", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__last", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__map", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__fmap", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__pure", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__apply", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__bind", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__fold_left", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__take", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__zip_with", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__append", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Seq__reverse", StringComparison.Ordinal));
    }

    [Fact]
    public void StdSeqPlusImportFixture_ZipTupleElementsUseWideRuntimeElementSize()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_list_plus.eidos"));
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);

        Assert.True(result.Success);
        // Seq.zip now builds its Seq[(A, B)] result with an index loop that grows
        // an array via array_push (the cons-per-element array_new/array_set form was
        // the previous, runtime-crashing lowering). The key invariant this test
        // guards is that tuple elements use their wide (16-byte for (Int, Int))
        // runtime element size on both allocation and element writes.
        Assert.Contains("call ptr @eidos_array_new_with_policy(i64 0, i64 16", llvmIr, StringComparison.Ordinal);
        Assert.Contains("call ptr @eidos_array_push", llvmIr, StringComparison.Ordinal);
        Assert.Contains(", i64 16)", llvmIr, StringComparison.Ordinal);
    }

    [Fact]
    public void StdOptionImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_option_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__map", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__map_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__map_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__and_then", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__and_", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__or_", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__xor", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__unwrap_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__unwrap_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__contains", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__filter", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__zip", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__zip_with", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__flatten", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__FunctorOption__fmap", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__ApplicativeOption__pure", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__ApplicativeOption__apply", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__sequence", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__FoldableOption__fold_left", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__FoldableOption__fold_right", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__MonadOption__bind", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__TraversableOption__traverse", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__EqOptionT__eq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__OrdOptionT__compare", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__ShowOptionT__show", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__is_some", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Option__or_else_with", StringComparison.Ordinal));
    }

    [Fact]
    public void StdOrderingImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_ordering_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__compare_int", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__compare_char", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__compare_bool", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__eq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__OrdOrdering__compare", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__ShowOrdering__show", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__then_with", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__is_le", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__is_ge", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__is_ne", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__to_int", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__from_int", StringComparison.Ordinal));
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
            function => function.Name.Contains("Ordering__then_compare_char", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__then_compare_int", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__then_compare_bool", StringComparison.Ordinal));
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
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Ordering__reverse", StringComparison.Ordinal));
    }

    [Fact]
    public void StdHashImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_hash_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Hash__hash_int", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Hash__hash_bool", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Hash__hash_string", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Hash__bucket_index", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Hash__combine3", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Hash__combine_all", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Hash__combine_seq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Hash__hash_seq_with_seed", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Hash__hash_seq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Hash__hash_pair_with_seed", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Hash__hash_pair", StringComparison.Ordinal));
    }

    [Fact]
    public void StdAlternativeImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_alternative_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__empty_seq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__or_else_seq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__is_empty_seq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__or_else_seq_with", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__guard_seq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__when_seq", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__empty_option", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__or_else_option", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__is_empty_option", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__or_else_option_with", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__guard_option", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__when_option", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__empty_for", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Alternative__choose", StringComparison.Ordinal));
    }

    [Fact]
    public void StdTaskRuntimeImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_task_runtime_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.DoesNotContain("closure_stack", result.LlvmIrText, StringComparison.Ordinal);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__completed_raw_opt", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__completed_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__is_pending", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__status", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__status_is_completed", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__status_is_pending", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__status_value_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__status_value_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__spawn_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__await_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__spawn", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__await_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__try_await_raw_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__try_await_raw_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__try_await_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__try_await_value_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__try_await_value_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__is_empty", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__has_pending", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__is_accepting", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__spawn_raw_if_accepting", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__spawn_if_accepting", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__spawn_raw_or_completed", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__spawn_or_completed", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__is_cancelled_or_empty", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__spawn_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__spawn", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__join_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__join", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__collect_values", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__join_values", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__status", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__status_pending_count", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__status_is_cancelled", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__status_is_idle", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__status_has_pending", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__status_value_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("TaskGroup__status_value_or_else", StringComparison.Ordinal));
        Assert.Contains("eidos_task_spawn_closure_value", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("eidos_task_await_closure_value", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("eidos_taskgroup_spawn_closure_value", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void StdPromiseRuntimeImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_promise_runtime_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__try_await_raw_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__try_await_raw_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__fulfill_raw_if_non_null", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__is_fulfilled", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__is_pending", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__status", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__status_is_fulfilled", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__status_is_pending", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__status_value_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__status_value_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__fulfill_raw_if_pending", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__new_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__fulfill_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__try_await_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__try_await_value_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__try_await_value_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Promise__fulfill_value_if_pending", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.Contains("eidos_promise_new_single", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("call void @eidos_incref_shared", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("call void @eidos_decref_shared", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("call void @eidos_register_destructor", result.LlvmIrText, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolved_ref__", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void StdChannelRuntimeImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_channel_runtime_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__new_unbuffered", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__try_send_raw_if_non_null", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__try_recv_raw_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__try_recv_raw_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__try_recv_raw_status", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__receive_status_has_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__receive_status_is_pending", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__receive_status_is_closed", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__receive_status_value_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__receive_status_value_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__receive_status_fold", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__receive_status_fold_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__new_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__try_send", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__try_recv", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__try_recv_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Channel__try_recv_or_else", StringComparison.Ordinal));
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
        Assert.Contains("eidos_channel_new_capacity", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("call void @eidos_incref_shared", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("call void @eidos_decref_shared", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("call void @eidos_register_destructor", result.LlvmIrText, StringComparison.Ordinal);
        Assert.DoesNotContain("unresolved_ref__", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void StdChannelRuntimeImportFixture_MirRewritesReturnOnlyGenericUnboxValueCalls()
    {
        var result = RunFixtureAtMir(Fx("stdlib/std_channel_runtime_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);
        Assert.NotNull(result.MirModule);

        var mirModule = result.MirModule!;
        AssertNoRemainingGenericUnboxValueCalls(mirModule);
    }

    [Fact]
    public void StdPromiseRuntimeImportFixture_MirRewritesReturnOnlyGenericUnboxValueCalls()
    {
        var result = RunFixtureAtMir(Fx("stdlib/std_promise_runtime_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Mir, result.CompletedPhase);
        Assert.NotNull(result.MirModule);

        var mirModule = result.MirModule!;
        AssertNoRemainingGenericUnboxValueCalls(mirModule);
    }

    private static void AssertNoRemainingGenericUnboxValueCalls(MirModule mirModule)
    {
        var remainingUnboxCalls = mirModule.Functions
            .SelectMany(function => function.BasicBlocks.SelectMany(block => block.Instructions.OfType<MirCall>())
                .Select(call => (Function: function, Call: call)))
            .Where(item => item.Call.Function is MirFunctionRef functionRef &&
                           string.Equals(functionRef.Name, "Std__FFI__unbox_value", StringComparison.Ordinal))
            .ToList();

        Assert.True(
            remainingUnboxCalls.Count == 0,
            string.Join(
                Environment.NewLine,
                remainingUnboxCalls.Select(item =>
                {
                    var functionRef = (MirFunctionRef)item.Call.Function;
                    var targetType = item.Call.Target?.TypeId.Value.ToString() ?? "<none>";
                    var unboxFunctions = string.Join(
                        "; ",
                        mirModule.Functions
                            .Where(function => function.Name.Contains("FFI__unbox_value", StringComparison.Ordinal))
                            .Select(function => $"{function.Name}:sym={function.SymbolId.Value}:ret={function.ReturnType.Value}:gen={function.GenericParameterCount}:genIds=[{string.Join(",", function.GenericTypeParameterIds.Select(typeId => typeId.Value))}]"));
                    var failures = string.Join(
                        "; ",
                        mirModule.SpecializationFailures.Select(failure =>
                            $"{failure.TemplateName}:{failure.Reason}:{failure.TemplateKey}:{failure.SignatureKey}:{failure.SignatureDisplay}"));
                    return $"{item.Function.Name}: target={targetType} fnSym={functionRef.SymbolId.Value} fnType={functionRef.TypeId.Value} fnSig={functionRef.SignatureTypeId.Value} fnArgs=[{string.Join(",", functionRef.TypeArgumentIds.Select(typeId => typeId.Value))}] funcs={unboxFunctions} failures={failures}";
                })));
    }

    [Fact]
    public void StdBarrierRuntimeImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_barrier_runtime_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__new_single", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__arrive_pending", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__arrive_tripped", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__arrive_status", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__arrival_is_pending", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__arrival_is_tripped", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__arrival_value_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__arrival_value_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__arrive_value_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__arrive_value_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__wait_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Barrier__wait", StringComparison.Ordinal));
    }

    [Fact]
    public void StdSyncRuntimeImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_sync_runtime_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Mutex__try_lock_then_unlock", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Mutex__try_with_lock", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Mutex__new_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Mutex__try_with_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Mutex__try_update", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Mutex__status", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Mutex__status_is_available", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Mutex__status_is_locked", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Mutex__status_value_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Mutex__status_value_or_else", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__try_read_then_unlock", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__try_write_then_unlock", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__try_with_read", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__try_with_write", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__new_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__try_with_read_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__try_with_write_value", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__try_update", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__status", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__status_is_idle", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__status_is_readable", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__status_is_blocked", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__status_value_or", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("RwLock__status_value_or_else", StringComparison.Ordinal));

        Assert.Contains("eidos_mutex_get_inner", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("eidos_mutex_replace_inner", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("eidos_rwlock_get_inner", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("eidos_rwlock_replace_inner", result.LlvmIrText, StringComparison.Ordinal);
    }

    [Fact]
    public void StdAsyncRuntimeImportFixture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_async_runtime_import.eidos"));

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var llvmModule = result.LlvmModule!;
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("AsyncRuntime__run", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("AsyncRuntime__start", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("AsyncRuntime__start_default", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("AsyncRuntime__shutdown", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("AsyncRuntime__worker_index", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("AsyncRuntime__with_scheduler", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Async__spawn_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Async__await_raw", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Async__spawn", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Async__await", StringComparison.Ordinal));
        Assert.Contains(
            llvmModule.Functions,
            function => function.Name.Contains("Task__try_await_value_or_else", StringComparison.Ordinal));
        Assert.Contains("eidos_task_spawn_closure_value", result.LlvmIrText, StringComparison.Ordinal);
        Assert.Contains("eidos_task_await_closure_value", result.LlvmIrText, StringComparison.Ordinal);
    }

}
