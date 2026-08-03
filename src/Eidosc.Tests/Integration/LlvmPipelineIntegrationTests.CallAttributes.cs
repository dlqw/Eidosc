using Eidosc.CodeGen.Llvm;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void CallAttributes_ManagedAdtSyntheticLifecycleFunctions_AreNounwind()
    {
        const string source = """
            Tok :: type {
                TkKeyword:: type(String), TkEof:: type {}
            }

            main :: Unit -> Int
            {
                _ => match TkKeyword("int") {
                    TkKeyword(value) => 0,
                    TkEof() => 1
                }
            }
            """;

        var result = RunSourceAtLlvm(source, "call_attributes_managed_adt.eidos");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var module = Assert.IsType<LlvmModule>(result.LlvmModule);
        var lifecycleFunctions = module.Functions
            .Where(static function =>
                function.Name.StartsWith("eidos_destructor_", StringComparison.Ordinal) ||
                function.Name.StartsWith("eidos_retain_fields__", StringComparison.Ordinal) ||
                function.Name == "eidos_module_init")
            .ToList();

        Assert.Equal(3, lifecycleFunctions.Count);
        Assert.All(lifecycleFunctions, function => Assert.True(HasNounwindAttribute(module, function)));
        Assert.All(
            module.Functions.Where(static function => function.Name.StartsWith("eidos_main", StringComparison.Ordinal)),
            function => Assert.True(HasNounwindAttribute(module, function)));
    }

    [Fact]
    public void CallAttributes_ClosureHelpersUseLoweredDirectAndIndirectCallBoundaries()
    {
        const string source = """
            apply :: (Int -> Int) -> Int -> Int
            {
                f => value => f(value)
            }

            main :: Unit -> Int
            {
                _ => {
                    offset := 1;
                    add_offset := value => value + offset;
                    apply(add_offset)(41)
                }
            }
            """;

        var result = RunSourceAtLlvm(source, "call_attributes_closure_helpers.eidos");
        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message)));

        var module = Assert.IsType<LlvmModule>(result.LlvmModule);
        var closureHelpers = module.Functions
            .Where(static function => function.Name.StartsWith("eidos_closure_invoke_", StringComparison.Ordinal))
            .ToList();
        var lambdaFunctions = module.Functions
            .Where(static function => function.Name.Contains("lambda", StringComparison.Ordinal))
            .ToList();
        var applyName = new NameMangler().MangleFunctionName("", "apply");
        var applyFunction = Assert.Single(
            module.Functions,
            function => function.Name.StartsWith(applyName, StringComparison.Ordinal));

        Assert.NotEmpty(closureHelpers);
        Assert.NotEmpty(lambdaFunctions);
        Assert.All(closureHelpers, function => Assert.True(HasNounwindAttribute(module, function)));
        Assert.All(lambdaFunctions, function => Assert.True(HasNounwindAttribute(module, function)));
        Assert.False(HasNounwindAttribute(module, applyFunction));
    }
}
