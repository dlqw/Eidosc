using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
public class EffectBorrowIntegrationTests
{
    [Fact]
    public void EffectDefinitions_WithBorrowConflict_StillReportsE1002()
    {
        const string source = """
Console :: effect;

main[A] :: Int -> A
{
    _ => {
        mut x := "hello";
        mut cond := x == "hello";
        x := "world";
        cond := false;
        0;
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "effect_borrow_conflict.eidos",
            StopAtPhase = CompilationPhase.Borrow,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error && diagnostic.Code == "E1002");
    }
}
