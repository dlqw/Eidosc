using System.CommandLine.Builder;
using System.CommandLine;
using System.CommandLine.Parsing;
using Eidosc.Cli.Commands;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

[Collection(ConsoleCliTestCollection.Name)]
public sealed class StyleDenyCliTests
{
    private const string StyleViolationSource = "BadFunction :: Int -> Int { value => value }";

    [Fact]
    public async Task AnalyzeCommand_DenyStyle_PromotesNamingDiagnostic()
    {
        var exitCode = await InvokeSilentlyAsync(AnalyzeCommand.Create(), [
            "--source-text", StyleViolationSource,
            "--phase", "types",
            "--deny", "style",
            "--no-color"
        ]);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task BuildCommand_DenyStyle_PromotesNamingDiagnostic()
    {
        var exitCode = await InvokeSilentlyAsync(BuildCommand.Create(), [
            "--source-text", StyleViolationSource,
            "--target", "Typed",
            "--deny", "style",
            "--no-color"
        ]);

        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task DebugCommand_DenyStyle_PromotesNamingDiagnostic()
    {
        var debugDirectory = Path.Combine(Path.GetTempPath(), $"eidosc_style_debug_{Guid.NewGuid():N}");
        try
        {
            var exitCode = await InvokeSilentlyAsync(DebugCommand.Create(), [
                "--source-text", StyleViolationSource,
                "--debug-output", debugDirectory,
                "--deny", "style",
                "--no-color"
            ]);

            Assert.Equal(1, exitCode);
        }
        finally
        {
            if (Directory.Exists(debugDirectory))
            {
                Directory.Delete(debugDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public async Task RunCommand_DenyStyle_PromotesNamingDiagnostic()
    {
        var outputPath = Path.Combine(Path.GetTempPath(), $"eidosc_style_run_{Guid.NewGuid():N}.exe");
        var exitCode = await InvokeSilentlyAsync(RunCommand.Create(), [
            "--source-text", StyleViolationSource,
            "--deny", "style",
            "--output", outputPath,
            "--no-color"
        ]);

        Assert.Equal(1, exitCode);
        if (File.Exists(outputPath))
        {
            File.Delete(outputPath);
        }
    }

    [Fact]
    public async Task AnalyzeCommand_ProjectManifestNamingDiagnostics_AreEmitted()
    {
        using var workspace = Eidosc.Tests.Fixtures.TestTempWorkspace.Create("eidosc_manifest_style");
        Directory.CreateDirectory(Path.Combine(workspace.Root, "src"));
        File.WriteAllText(Path.Combine(workspace.Root, "eidos.toml"), """
            manifestSchema = 3
            sourceRoots = ["src"]

            [language]
            version = "0.7.0-alpha.1"

            [package]
            name = "Dev.eidos.HttpClient"
            version = "0.1.0"
            """);
        File.WriteAllText(Path.Combine(workspace.Root, "src", "Main.eidos"), "main :: Unit -> Unit { _ => () }");

        var output = new StringWriter();
        var error = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await AnalyzeCommand.Create().InvokeAsync([workspace.Root, "--phase", "parser", "--no-color"]);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Contains("S1107", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("S1105", output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(CommandFactories))]
    public async Task Commands_Help_DescribesDenyStyle(Func<System.CommandLine.Command> createCommand)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(output);
            Console.SetError(error);
            var exitCode = await createCommand().InvokeAsync(["--help"]);
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }

        Assert.Contains("--deny", output.ToString(), StringComparison.Ordinal);
        Assert.Contains("style", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    public static IEnumerable<object[]> CommandFactories() =>
    [
        [ (Func<System.CommandLine.Command>)AnalyzeCommand.Create ],
        [ (Func<System.CommandLine.Command>)BuildCommand.Create ],
        [ (Func<System.CommandLine.Command>)DebugCommand.Create ],
        [ (Func<System.CommandLine.Command>)RunCommand.Create ]
    ];

    private static async Task<int> InvokeSilentlyAsync(System.CommandLine.Command command, string[] arguments)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        try
        {
            Console.SetOut(TextWriter.Null);
            Console.SetError(TextWriter.Null);
            return await command.InvokeAsync(arguments);
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}
