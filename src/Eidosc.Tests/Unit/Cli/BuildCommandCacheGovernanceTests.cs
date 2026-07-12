using System.CommandLine;
using System.Reflection;
using Eidosc.Cli.Commands;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

public sealed class BuildCommandCacheGovernanceTests
{
    [Fact]
    public async Task BuildCommand_NoCache_DisablesPersistentReadsAndWrites()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_build_no_cache_{Guid.NewGuid():N}");
        try
        {
            var sourceDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(sourceDir);
            var projectPath = Path.Combine(tempDir, "eidos.toml");
            var entryPath = Path.Combine(sourceDir, "Main.eidos");
            File.WriteAllText(projectPath, """
name = "cache-governance-test"
sourceRoots = ["src"]

[[targets]]
name = "main"
kind = "executable"
entry = "src/Main.eidos"
""");
            File.WriteAllText(entryPath, """
Main :: module {
    main :: Unit -> Unit { _ => () }
}
""");
            var options = new BuildCommand.BuildOptions
            {
                Source = entryPath,
                Project = projectPath,
                TargetName = "main",
                Target = CompileTarget.Typed,
                NoCache = true,
                NoImplicitPrelude = true,
                NoColor = true,
                Jobs = 2
            };

            Assert.Equal(0, await ExecuteBuildAsync(options));
            var cacheDirectory = Path.Combine(tempDir, "build", ".eidos-cache");
            Assert.False(Directory.Exists(cacheDirectory));

            options.NoCache = false;
            Assert.Equal(0, await ExecuteBuildAsync(options));
            Assert.True(Directory.Exists(cacheDirectory));
            Assert.True(Directory.EnumerateFiles(cacheDirectory).Any());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public async Task BuildCommand_RejectsInvalidJobsAndCacheLimit()
    {
        Assert.NotEqual(0, await BuildCommand.Create().InvokeAsync(["--jobs", "0"]));
        Assert.NotEqual(0, await BuildCommand.Create().InvokeAsync(["--cache-max-mib", "-1"]));
    }

    private static async Task<int> ExecuteBuildAsync(BuildCommand.BuildOptions options)
    {
        var method = typeof(BuildCommand).GetMethod(
            "Execute",
            BindingFlags.Static | BindingFlags.NonPublic);
        var task = Assert.IsAssignableFrom<Task<int>>(method!.Invoke(null, [options]));
        return await task;
    }
}
