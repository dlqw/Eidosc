using System.CommandLine;
using Eidosc.Cli.Commands;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

public sealed class CacheCommandTests
{
    [Fact]
    public void ResolveProjectDirectory_FromSourcePath_FindsNearestManifest()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_cache_command_resolve_{Guid.NewGuid():N}");
        try
        {
            var sourceDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(sourceDir);
            File.WriteAllText(Path.Combine(tempDir, "eidos.toml"), "[package]\nname = \"test\"\nversion = \"0.1.0\"\n");
            var sourcePath = Path.Combine(sourceDir, "Main.eidos");
            File.WriteAllText(sourcePath, "Main :: module {}");

            Assert.Equal(Path.GetFullPath(tempDir), CacheCommand.ResolveProjectDirectory(sourcePath));
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
    public async Task CleanCommand_DeletesProjectCache()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_cache_command_clean_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "eidos.toml"), "[package]\nname = \"test\"\nversion = \"0.1.0\"\n");
            var cache = new ModuleArtifactCache(Path.Combine(tempDir, "build", ".eidos-cache"));
            cache.StoreArtifact(CreateKey(), "signature", ".json", "{}");

            var exitCode = await CacheCommand.Create().InvokeAsync(["clean", tempDir, "--json"]);

            Assert.Equal(0, exitCode);
            Assert.Equal(0, cache.GetStatus().TotalBytes);
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
    public async Task PruneCommand_RejectsNegativeLimit()
    {
        var exitCode = await CacheCommand.Create().InvokeAsync(["prune", "--max-mib", "-1"]);

        Assert.NotEqual(0, exitCode);
    }

    private static ModuleArtifactKey CreateKey() =>
        new()
        {
            ModuleKey = "Main",
            SourceHash = "source",
            LanguageVersion = "0.7.0-alpha.1",
            DependencySignatureHash = "deps",
            TargetTriple = "test",
            FlagsHash = "flags"
        };
}
