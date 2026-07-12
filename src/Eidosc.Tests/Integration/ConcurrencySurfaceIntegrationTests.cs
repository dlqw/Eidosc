using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Category, TestCategories.Slow)]
public class ConcurrencySurfaceIntegrationTests
{
    private static readonly TestPathConfig Paths = TestPathConfig.Current;

    [Fact]
    public void StdConcurrencySurfaceFixture_CompilesThroughSendPhase()
    {
        var fixturePath = Paths.Fixture("concurrency/std_concurrency_surface.eidos");
        var source = TestSourceLoader.Load(fixturePath);
        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(fixturePath),
            StopAtPhase = CompilationPhase.Send,
            UseColors = false
        }).Run();

        Assert.Equal(CompilationPhase.Send, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
    }
}
