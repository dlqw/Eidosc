using System.CommandLine.Builder;
using System.CommandLine.Parsing;
using Eidosc.Cli.Commands.Pkg;
using Eidosc.ProjectSystem;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Cli;

[Collection(ConsoleCliTestCollection.Name)]
public sealed class PkgCommandManifestTests
{
    [Fact]
    public async Task PkgAdd_PathDependency_WritesTomlDependencyTable()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_pkg_manifest");
        var tempDir = workspace.Root;
        var previousDirectory = Directory.GetCurrentDirectory();
        var output = Console.Out;
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            Console.SetOut(TextWriter.Null);
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                """
                manifestSchema = 3
                sourceRoots = ["src"]
                """);

            var parser = new CommandLineBuilder(PkgAddCommand.Create()).Build();
            var exitCode = await parser.InvokeAsync(["crypto_a", "--path", "../crypto-a"]);

            Assert.Equal(0, exitCode);
            var manifestPath = Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName);
            var manifest = EidosProjectManifestDocument.Load(manifestPath);
            Assert.Equal("../crypto-a", manifest.Dependencies!["crypto_a"].Path);
            var manifestText = File.ReadAllText(manifestPath);
            Assert.Contains("[dependencies]", manifestText, StringComparison.Ordinal);
            Assert.Contains("""crypto_a = { path = "../crypto-a" }""", manifestText, StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(output);
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    [Fact]
    public async Task PkgAdd_VersionShorthand_WritesCompactVersionDependency()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_pkg_manifest");
        var tempDir = workspace.Root;
        var previousDirectory = Directory.GetCurrentDirectory();
        var output = Console.Out;
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            Console.SetOut(TextWriter.Null);
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                """
                manifestSchema = 3
                """);

            var parser = new CommandLineBuilder(PkgAddCommand.Create()).Build();
            var exitCode = await parser.InvokeAsync(["Json@^1.2.0"]);

            Assert.Equal(0, exitCode);
            var manifestPath = Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName);
            var manifest = EidosProjectManifestDocument.Load(manifestPath);
            Assert.Equal("^1.2.0", manifest.Dependencies!["Json"].Version);
            Assert.Contains("Json = \"^1.2.0\"", File.ReadAllText(manifestPath), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(output);
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    [Fact]
    public async Task PkgAdd_NameWithoutSource_WritesWildcardVersionDependency()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_pkg_manifest");
        var tempDir = workspace.Root;
        var previousDirectory = Directory.GetCurrentDirectory();
        var output = Console.Out;
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            Console.SetOut(TextWriter.Null);
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                """
                manifestSchema = 3
                """);

            var parser = new CommandLineBuilder(PkgAddCommand.Create()).Build();
            var exitCode = await parser.InvokeAsync(["Json"]);

            Assert.Equal(0, exitCode);
            var manifestPath = Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName);
            var manifest = EidosProjectManifestDocument.Load(manifestPath);
            Assert.Equal("*", manifest.Dependencies!["Json"].Version);
            Assert.Contains("Json = \"*\"", File.ReadAllText(manifestPath), StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(output);
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    [Fact]
    public async Task PkgAdd_GitHubShorthand_WritesExplicitGitDependency()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_pkg_manifest");
        var tempDir = workspace.Root;
        var previousDirectory = Directory.GetCurrentDirectory();
        var output = Console.Out;
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            Console.SetOut(TextWriter.Null);
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                """
                manifestSchema = 3
                """);

            var parser = new CommandLineBuilder(PkgAddCommand.Create()).Build();
            var exitCode = await parser.InvokeAsync(["github:eidos-pkgs/json@1.2.0"]);

            Assert.Equal(0, exitCode);
            var manifestPath = Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName);
            var manifest = EidosProjectManifestDocument.Load(manifestPath);
            var dependency = manifest.Dependencies!["Json"];
            Assert.Equal("https://github.com/eidos-pkgs/json.git", dependency.Git);
            Assert.Equal("1.2.0", dependency.Tag);
            Assert.Contains(
                """Json = { git = "https://github.com/eidos-pkgs/json.git", tag = "1.2.0" }""",
                File.ReadAllText(manifestPath),
                StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(output);
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }

    [Fact]
    public async Task PkgRemove_ExistingDependency_RewritesTomlWithoutAlias()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_pkg_manifest");
        var tempDir = workspace.Root;
        var previousDirectory = Directory.GetCurrentDirectory();
        var output = Console.Out;
        try
        {
            Directory.SetCurrentDirectory(tempDir);
            Console.SetOut(TextWriter.Null);
            File.WriteAllText(
                Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName),
                """
                manifestSchema = 3

                [dependencies.crypto_a]
                path = "../crypto-a"

                [dependencies.crypto_b]
                git = "https://example.invalid/crypto-b.git"
                tag = "v1.0.0"
                """);

            var parser = new CommandLineBuilder(PkgRemoveCommand.Create()).Build();
            var exitCode = await parser.InvokeAsync(["crypto_a"]);

            Assert.Equal(0, exitCode);
            var manifestPath = Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName);
            var manifestText = File.ReadAllText(manifestPath);
            var manifest = EidosProjectManifestDocument.Load(manifestPath);
            Assert.False(manifest.Dependencies!.ContainsKey("crypto_a"));
            Assert.Equal("https://example.invalid/crypto-b.git", manifest.Dependencies["crypto_b"].Git);
            Assert.DoesNotContain("crypto_a", manifestText, StringComparison.Ordinal);
            Assert.Contains("[dependencies]", manifestText, StringComparison.Ordinal);
            Assert.Contains(
                """crypto_b = { git = "https://example.invalid/crypto-b.git", tag = "v1.0.0" }""",
                manifestText,
                StringComparison.Ordinal);
        }
        finally
        {
            Console.SetOut(output);
            Directory.SetCurrentDirectory(previousDirectory);
        }
    }
}
