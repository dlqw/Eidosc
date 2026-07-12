using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Query;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Query;

public sealed class QueryImplicitPreludeTests
{
    [Fact]
    public void Compile_DefaultOptions_ImportsStdPrelude()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_query_prelude");
        const string source = """
main :: Unit -> Int
{
    _ => id(41) + 1
}
""";
        var sourcePath = workspace.WriteText("Main.eidos", source);
        var session = new PipelineQuerySession();

        var result = session.Compile(
            sourcePath,
            source,
            new CompilationOptions
            {
                InputFile = sourcePath,
                StopAtPhase = CompilationPhase.Types,
                LanguageVersion = EidosLanguageVersions.Current
            });

        Assert.True(result.Success, TestDiagnosticFormatter.Format(result));
    }

    [Fact]
    public void Compile_NoImplicitPrelude_DoesNotImportStdPrelude()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_query_prelude");
        const string source = """
main :: Unit -> Int
{
    _ => id(41) + 1
}
""";
        var sourcePath = workspace.WriteText("Main.eidos", source);
        var session = new PipelineQuerySession();

        var result = session.Compile(
            sourcePath,
            source,
            new CompilationOptions
            {
                InputFile = sourcePath,
                StopAtPhase = CompilationPhase.Types,
                LanguageVersion = EidosLanguageVersions.Current,
                NoImplicitPrelude = true
            });

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("id", StringComparison.Ordinal));
    }
}
