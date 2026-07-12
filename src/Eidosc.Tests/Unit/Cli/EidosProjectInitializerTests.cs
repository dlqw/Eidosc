using Eidosc.ProjectSystem;
using Eidosc.Cli.Commands;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Cli;

[Collection(ConsoleCliTestCollection.Name)]
public class EidosProjectInitializerTests
{
    [Fact]
    public void Initialize_DefaultExecutable_CreatesSrcAndSourceRootsManifest()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_project_init");
        var tempDir = workspace.Root;
        var output = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);

            var result = EidosProjectInitializer.Initialize(
                tempDir,
                new EidosProjectInitOptions { Name = "dev.eidos.app" },
                "test init",
                useColors: false);

            Assert.Equal(0, result);
            Assert.True(Directory.Exists(Path.Combine(tempDir, "src")));
            var mainPath = Path.Combine(tempDir, "src", "Main.eidos");
            Assert.True(File.Exists(mainPath));
            Assert.Contains("Main :: module", File.ReadAllText(mainPath), StringComparison.Ordinal);
            Assert.Contains("main :: Unit -> Int", File.ReadAllText(mainPath), StringComparison.Ordinal);

            var document = LoadManifest(tempDir);
            Assert.Equal(EidosLanguageVersions.DefaultForNewProjects, document.Language?.Version);
            Assert.Null(document.SourceRoots);
            Assert.Null(document.Targets);

            var loaded = EidosProjectConfigurationLoader.LoadFromPath(tempDir);
            Assert.Equal(EidosLanguageVersions.Current, loaded.Configuration.LanguageVersion);
            Assert.Equal(Path.GetFullPath(Path.Combine(tempDir, "src")), Assert.Single(loaded.Configuration.SourceRoots));
            Assert.Equal(
                Path.GetFullPath(Path.Combine(tempDir, "src", "Main.eidos")),
                loaded.Configuration.Targets[0].Entry);

            var manifestText = File.ReadAllText(Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName));
            Assert.Equal(
                """
                manifestSchema = 3

                [language]
                version = "0.4.0-alpha.1"

                [package]
                name = "dev.eidos.app"
                version = "0.1.0"

                """.Replace("\r\n", "\n", StringComparison.Ordinal),
                manifestText.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        finally
        {
            Console.SetOut(output);
        }
    }

    [Fact]
    public void Initialize_CustomSourceRoots_CreatesRootsAndUsesFirstRootForExecutableEntry()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_project_init");
        var tempDir = workspace.Root;
        var output = Console.Out;
        try
        {
            Console.SetOut(TextWriter.Null);

            var result = EidosProjectInitializer.Initialize(
                tempDir,
                new EidosProjectInitOptions
                {
                    Name = "dev.eidos.app",
                    SourceRoot = ["app", "generated", "app"]
                },
                "test init",
                useColors: false);

            Assert.Equal(0, result);
            Assert.True(Directory.Exists(Path.Combine(tempDir, "app")));
            Assert.True(Directory.Exists(Path.Combine(tempDir, "generated")));
            Assert.True(File.Exists(Path.Combine(tempDir, "app", "Main.eidos")));
            Assert.False(File.Exists(Path.Combine(tempDir, "generated", "Main.eidos")));

            var document = LoadManifest(tempDir);
            Assert.NotNull(document.SourceRoots);
            Assert.Equal(["app", "generated"], document.SourceRoots!);
            Assert.Equal("app/Main.eidos", document.Targets![0].Entry);
        }
        finally
        {
            Console.SetOut(output);
        }
    }

    private static EidosProjectManifestDocument LoadManifest(string projectDir)
    {
        return EidosProjectManifestDocument.Load(Path.Combine(projectDir, EidosProjectConfigurationLoader.DefaultFileName));
    }
}
