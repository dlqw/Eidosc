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
        // mref 借用者在共享借用创建点仍活跃（结尾使用 y）：真冲突；
        // 效果定义存在时借用检查照常报告。
        const string source = """
Console :: effect;

demo :: Int -> Int
{
    x => {
        mref y := x;
        ref z := x;
        x + y + z
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
