using System;
using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Diagnostic;
using Eidosc.Hir;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Parser_LetQuestionBlockStatement_CreatesAstDeclaration()
    {
        var result = RunPipeline(LetQuestionOptionSource, CompilationPhase.Parser, UseLetQuestionFixturePath);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "use_option");
        var branch = Assert.Single(function.Body);
        var block = Assert.IsType<BlockExpr>(branch.Expression);
        var letQuestion = Assert.IsType<LetQuestionDecl>(Assert.Single(block.Statements, item => item is LetQuestionDecl));
        var pattern = Assert.IsType<VarPattern>(letQuestion.Pattern);

        Assert.Equal("next", pattern.Name);
        Assert.NotNull(letQuestion.Value);
    }

    [Fact]
    public void Types_LetQuestionOptionAndResult_SucceedWithStdConstructors()
    {
        var result = RunPipeline(LetQuestionCombinedSource, CompilationPhase.Types, UseLetQuestionFixturePath);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var optionLet = FindLetQuestion(module, "use_option");
        var resultLet = FindLetQuestion(module, "use_result");

        Assert.Equal(LetQuestionBindingKind.Option, optionLet.BindingKind);
        Assert.True(optionLet.SuccessConstructorSymbolId.IsValid);
        Assert.True(optionLet.FailureConstructorSymbolId.IsValid);

        Assert.Equal(LetQuestionBindingKind.Result, resultLet.BindingKind);
        Assert.True(resultLet.SuccessConstructorSymbolId.IsValid);
        Assert.True(resultLet.FailureConstructorSymbolId.IsValid);
        Assert.True(resultLet.FailureBindingSymbolId.IsValid);
    }

    [Fact]
    public void Hir_LetQuestionOption_DesugarsToMatchAndReturn()
    {
        var result = RunPipeline(LetQuestionOptionSource, CompilationPhase.Hir, UseLetQuestionFixturePath);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        AssertNoLetQuestionHirOrMirNodeTypes();

        var function = Assert.Single(result.HirModule!.Declarations.OfType<HirFunc>(), item => item.Name == "use_option");
        var body = Assert.IsType<HirBlock>(function.Body);
        var match = Assert.IsType<HirMatch>(body.Result);

        Assert.Equal(2, match.Branches.Count);
        var successPattern = Assert.IsType<HirCtorPattern>(match.Branches[0].Pattern);
        Assert.Equal("Some", successPattern.ConstructorName);
        Assert.IsType<HirBlock>(match.Branches[0].Body);

        var failurePattern = Assert.IsType<HirCtorPattern>(match.Branches[1].Pattern);
        Assert.Equal("None", failurePattern.ConstructorName);
        var failureReturn = Assert.IsType<HirReturn>(match.Branches[1].Body);
        var failureValue = Assert.IsType<HirCall>(failureReturn.Value);
        var failureConstructor = Assert.IsType<HirVar>(failureValue.Function);
        Assert.Equal("None", failureConstructor.Name);
    }

    [Fact]
    public void Hir_LetQuestionResult_DesugarsToMatchAndErrorReturn()
    {
        var result = RunPipeline(LetQuestionResultSource, CompilationPhase.Hir, UseLetQuestionFixturePath);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        AssertNoLetQuestionHirOrMirNodeTypes();

        var function = Assert.Single(result.HirModule!.Declarations.OfType<HirFunc>(), item => item.Name == "use_result");
        var body = Assert.IsType<HirBlock>(function.Body);
        var match = Assert.IsType<HirMatch>(body.Result);

        Assert.Equal(2, match.Branches.Count);
        var successPattern = Assert.IsType<HirCtorPattern>(match.Branches[0].Pattern);
        Assert.Equal("Ok", successPattern.ConstructorName);

        var failurePattern = Assert.IsType<HirCtorPattern>(match.Branches[1].Pattern);
        Assert.Equal("Err", failurePattern.ConstructorName);
        var errorField = Assert.Single(failurePattern.Fields);
        var errorBinding = Assert.IsType<HirVarPattern>(errorField.Pattern);
        Assert.StartsWith("__let_question_error_", errorBinding.Name, StringComparison.Ordinal);
        Assert.True(errorBinding.SymbolId.IsValid);

        var failureReturn = Assert.IsType<HirReturn>(match.Branches[1].Body);
        var failureValue = Assert.IsType<HirCall>(failureReturn.Value);
        var failureConstructor = Assert.IsType<HirVar>(failureValue.Function);
        Assert.Equal("Err", failureConstructor.Name);
        var returnedError = Assert.IsType<HirVar>(Assert.Single(failureValue.Arguments));
        Assert.Equal(errorBinding.SymbolId, returnedError.SymbolId);
    }

    [Fact]
    public void Mir_LetQuestion_IsAlreadyEliminatedBeforeMir()
    {
        var result = RunPipeline(LetQuestionCombinedSource, CompilationPhase.Mir, UseLetQuestionFixturePath);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.NotNull(result.MirModule);
        AssertNoLetQuestionHirOrMirNodeTypes();
    }

    [Theory]
    [InlineData(TopLevelLetQuestionSource, "let? can only be used inside a function")]
    [InlineData(LetQuestionNonOptionResultSource, "let? right-hand side must have type Option")]
    [InlineData(LetQuestionOptionInResultReturnSource, "let? on Option[T] requires")]
    [InlineData(LetQuestionResultErrorMismatchSource, "let? on Result[T, E] requires")]
    [InlineData(LetQuestionRefutablePatternSource, "let? binding requires an irrefutable pattern")]
    public void Types_LetQuestionInvalidUses_ReportDedicatedDiagnostic(string source, string expectedMessage)
    {
        var result = RunPipeline(source, CompilationPhase.Types, UseLetQuestionFixturePath);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains(expectedMessage, StringComparison.Ordinal));
    }

    private static LetQuestionDecl FindLetQuestion(ModuleDecl module, string functionName)
    {
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == functionName);
        var block = Assert.IsType<BlockExpr>(Assert.Single(function.Body).Expression);
        return Assert.IsType<LetQuestionDecl>(Assert.Single(block.Statements, item => item is LetQuestionDecl));
    }

    private static void AssertNoLetQuestionHirOrMirNodeTypes()
    {
        Assert.DoesNotContain(
            typeof(HirModule).Assembly.GetTypes(),
            type => type.Namespace?.StartsWith("Eidosc.Hir", StringComparison.Ordinal) == true &&
                    type.Name.Contains("LetQuestion", StringComparison.Ordinal));

        Assert.DoesNotContain(
            typeof(Eidosc.Mir.MirModule).Assembly.GetTypes(),
            type => type.Namespace?.StartsWith("Eidosc.Mir", StringComparison.Ordinal) == true &&
                    type.Name.Contains("LetQuestion", StringComparison.Ordinal));
    }

    private static void UseLetQuestionFixturePath(CompilationOptions options)
    {
        options.InputFile = TestSourceLoader.GetFullPath("projects/test/src/stdlib/std_option_import.eidos");
    }

    private const string LetQuestionOptionSource = """
import Std.Option

maybe_inc :: Int -> Option[Int]
{
    value => if value > 0 then { Some(value + 1) } else { None() }
}

use_option :: Int -> Option[Int]
{
    value => {
        let? next = maybe_inc(value);
        Some(next + 1)
    }
}
""";

    private const string LetQuestionResultSource = """
import Std.Result

parse_like :: Int -> Result.ResultWith[String, Int]
{
    value => if value > 0 then { Ok(value + 1) } else { Err("bad") }
}

use_result :: Int -> Result.ResultWith[String, Int]
{
    value => {
        let? next = parse_like(value);
        Ok(next + 1)
    }
}
""";

    private const string LetQuestionCombinedSource = $"""
{LetQuestionOptionSource}

{LetQuestionResultSource}
""";

    private const string TopLevelLetQuestionSource = """
import Std.Option

let? value = Some(1);
""";

    private const string LetQuestionNonOptionResultSource = """
import Std.Option

bad :: Int -> Option[Int]
{
    value => {
        let? next = value;
        Some(next)
    }
}
""";

    private const string LetQuestionOptionInResultReturnSource = """
import Std.Option
import Std.Result

maybe_inc :: Int -> Option[Int]
{
    value => Some(value + 1)
}

bad :: Int -> Result.ResultWith[String, Int]
{
    value => {
        let? next = maybe_inc(value);
        Ok(next)
    }
}
""";

    private const string LetQuestionResultErrorMismatchSource = """
import Std.Result

parse_like :: Int -> Result.ResultWith[String, Int]
{
    value => if value > 0 then { Ok(value + 1) } else { Err("bad") }
}

bad :: Int -> Result.ResultWith[Int, Int]
{
    value => {
        let? next = parse_like(value);
        Ok(next)
    }
}
""";

    private const string LetQuestionRefutablePatternSource = """
import Std.Option

maybe_inc :: Int -> Option[Int]
{
    value => Some(value + 1)
}

bad :: Int -> Option[Int]
{
    value => {
        let? Some(next) = maybe_inc(value);
        Some(next)
    }
}
""";
}
