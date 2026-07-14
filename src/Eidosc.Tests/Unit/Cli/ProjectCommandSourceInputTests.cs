using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Eidosc.Cli.Commands;
using Eidosc.ProjectSystem;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

[Collection(ConsoleCliTestCollection.Name)]
public sealed class ProjectCommandSourceInputTests
{
    private const string InlineSource = """
Message :: type { Quit , Move(Int, Int) , Write(String) }

handle :: Message -> Int {
    Quit() => 0,
    Move(x, y) => x + y,
    Write(text) => text.string_length()
}

main :: Int -> Int {
    _ => handle(Move(10, 20))
}
""";

    [Fact]
    public async Task ResolveAndLoadAsync_SourceText_UsesVirtualInputFile()
    {
        var sourceInput = await ProjectCommandSourceInputResolver.ResolveAndLoadAsync(
            source: null,
            project: null,
            targetName: null,
            explicitImportRoots: null,
            sourceText: InlineSource,
            stdin: false);

        Assert.True(sourceInput.IsInMemorySource);
        Assert.Equal(InlineSource, sourceInput.SourceText);
        Assert.Equal(Path.GetFullPath("inline.eidos"), sourceInput.SourceFilePath);
    }

    [Fact]
    public async Task ResolveAndLoadAsync_Stdin_ReadsTextReader()
    {
        using var reader = new StringReader(InlineSource);

        var sourceInput = await ProjectCommandSourceInputResolver.ResolveAndLoadAsync(
            source: null,
            project: null,
            targetName: null,
            explicitImportRoots: null,
            sourceText: null,
            stdin: true,
            stdinReader: reader);

        Assert.True(sourceInput.IsInMemorySource);
        Assert.Equal(InlineSource, sourceInput.SourceText);
    }

    [Fact]
    public async Task ResolveAndLoadAsync_SourceTextWithStdin_RejectsAmbiguousInput()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            ProjectCommandSourceInputResolver.ResolveAndLoadAsync(
                source: null,
                project: null,
                targetName: null,
                explicitImportRoots: null,
                sourceText: InlineSource,
                stdin: true));
    }

    [Fact]
    public async Task AnalyzeCommand_SourceTextInvocation_CompilesInlineSource()
    {
        var parser = new CommandLineBuilder(AnalyzeCommand.Create()).Build();

        var exitCode = await parser.InvokeAsync([
            "--source-text",
            InlineSource,
            "--phase",
            "types",
            "--no-color"
        ]);

        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task AnalyzeCommand_ProjectManifestSyntax_ParsesNameFirstSource()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_project_input");
        var tempDir = workspace.Root;
        var output = Console.Out;
        var error = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                """
                manifestSchema = 3
                sourceRoots = ["src"]

                [language]
                version = "0.6.0-alpha.1"
                """);
            File.WriteAllText(
                Path.Combine(tempDir, "src", "Main.eidos"),
                """
                Main :: module {
                  main :: Unit -> Int { _ => 0 }
                }
                """);

            var parser = new CommandLineBuilder(AnalyzeCommand.Create()).Build();
            var exitCode = await parser.InvokeAsync([
                tempDir,
                "--phase",
                "parser",
                "--no-color"
            ]);

            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    [Fact]
    public async Task BuildCommand_ProjectManifestSyntax_EmitsEntryWrapperForNameFirstTarget()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_project_input");
        var tempDir = workspace.Root;
        var output = Console.Out;
        var error = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                """
                manifestSchema = 3
                sourceRoots = ["src"]

                [language]
                version = "0.6.0-alpha.1"
                """);
            File.WriteAllText(
                Path.Combine(tempDir, "src", "Main.eidos"),
                """
                Main :: module {
                  main :: Unit -> Int { _ => 0 }
                }
                """);
            var llvmPath = Path.Combine(tempDir, "build", "main.ll");

            var parser = new CommandLineBuilder(BuildCommand.Create()).Build();
            var exitCode = await parser.InvokeAsync([
                tempDir,
                "--target",
                "LlvmIr",
                "--output",
                llvmPath,
                "--no-color"
            ]);

            Assert.Equal(0, exitCode);
            Assert.Contains("define external i64 @eidos_main", File.ReadAllText(llvmPath), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
        }
    }

    [Fact]
    public async Task FmtCommand_ProjectManifestSyntax_FormatsNameFirstSource()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_project_input");
        var tempDir = workspace.Root;
        var output = Console.Out;
        var error = Console.Error;
        var input = Console.In;
        await using var outputWriter = new StringWriter();
        await using var errorWriter = new StringWriter();
        try
        {
            Console.SetOut(outputWriter);
            Console.SetError(errorWriter);
            Console.SetIn(new StringReader("Main :: module { main :: Unit -> Int { _ => 0 } }"));
            Directory.CreateDirectory(Path.Combine(tempDir, "src"));
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                """
                manifestSchema = 3
                sourceRoots = ["src"]

                [language]
                version = "0.6.0-alpha.1"
                """);
            var mainPath = Path.Combine(tempDir, "src", "Main.eidos");
            File.WriteAllText(mainPath, "Main :: module { main :: Unit -> Int { _ => 0 } }");

            var parser = new CommandLineBuilder(FmtCommand.Create()).Build();
            var exitCode = await parser.InvokeAsync([mainPath, "--stdin"]);

            Assert.Equal(0, exitCode);
            Assert.Contains("Main :: module", outputWriter.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("syntax validation failed", errorWriter.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Console.SetOut(output);
            Console.SetError(error);
            Console.SetIn(input);
        }
    }
}
