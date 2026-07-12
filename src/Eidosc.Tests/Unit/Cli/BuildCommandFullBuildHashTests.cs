using Eidosc.Cli.Commands;
using Eidosc.ProjectSystem;
using System.CommandLine;
using System.Text.Json;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

public sealed class BuildCommandFullBuildHashTests
{
    [Fact]
    public void ComputeFullBuildInputHash_ChangesWhenProjectSourceChanges()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_build_hash_{Guid.NewGuid():N}");
        try
        {
            var srcDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(srcDir);
            var entryPath = Path.Combine(srcDir, "Main.eidos");
            var helperPath = Path.Combine(srcDir, "Helper.eidos");
            var projectPath = Path.Combine(tempDir, "eidos.toml");
            WriteProject(projectPath);
            File.WriteAllText(entryPath, """
Main :: module {
    main :: Unit -> Unit { _ => () }
}
""");
            File.WriteAllText(helperPath, """
Helper :: module {
    value :: Int = 1;
}
""");

            var resolution = ResolveProject(projectPath, tempDir);
            var firstInput = new ProjectCommandSourceInput(
                resolution,
                File.ReadAllText(entryPath),
                IsInMemorySource: false);
            var firstHash = BuildCommand.ComputeFullBuildInputHash(firstInput, resolution);

            File.WriteAllText(helperPath, """
Helper :: module {
    value :: Int = 2;
}
""");
            var secondInput = new ProjectCommandSourceInput(
                resolution,
                File.ReadAllText(entryPath),
                IsInMemorySource: false);
            var secondHash = BuildCommand.ComputeFullBuildInputHash(secondInput, resolution);

            Assert.NotEqual(firstHash, secondHash);
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
    public void ComputeFullBuildInputHash_IsStableWithMultipleProjectSources()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_build_hash_stable_{Guid.NewGuid():N}");
        try
        {
            var srcDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(srcDir);
            var projectPath = Path.Combine(tempDir, "eidos.toml");
            var entryPath = Path.Combine(srcDir, "Main.eidos");
            WriteProject(projectPath);
            File.WriteAllText(entryPath, """
Main :: module {
    main :: Unit -> Unit { _ => () }
}
""");
            File.WriteAllText(Path.Combine(srcDir, "B.eidos"), """
B :: module {
    value :: Int = 2;
}
""");
            File.WriteAllText(Path.Combine(srcDir, "A.eidos"), """
A :: module {
    value :: Int = 1;
}
""");

            var resolution = ResolveProject(projectPath, tempDir);
            var input = new ProjectCommandSourceInput(
                resolution,
                File.ReadAllText(entryPath),
                IsInMemorySource: false);

            var first = BuildCommand.ComputeFullBuildInputHash(
                input,
                resolution,
                out var firstStats,
                maxDegreeOfParallelism: 1);
            var second = BuildCommand.ComputeFullBuildInputHash(
                input,
                resolution,
                out var secondStats,
                maxDegreeOfParallelism: 4);

            Assert.Equal(first, second);
            Assert.Equal(3, firstStats.SourceFileCount);
            Assert.Equal(1, firstStats.SourceRootCount);
            Assert.Equal(2, firstStats.ParallelReadCount);
            Assert.Equal(firstStats, secondStats);
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
    public async Task BuildProfile_IncludesFullBuildArtifactKeyEvent()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_build_hash_profile_{Guid.NewGuid():N}");
        try
        {
            var srcDir = Path.Combine(tempDir, "src");
            Directory.CreateDirectory(srcDir);
            var projectPath = Path.Combine(tempDir, "eidos.toml");
            var entryPath = Path.Combine(srcDir, "Main.eidos");
            var profilePath = Path.Combine(tempDir, "profile.json");
            WriteProject(projectPath);
            File.WriteAllText(entryPath, """
Main :: module {
    main :: Unit -> Unit { _ => () }
}
""");
            File.WriteAllText(Path.Combine(srcDir, "Helper.eidos"), """
Helper :: module {
    value :: Int = 1;
}
""");

            var command = BuildCommand.Create();
            var exitCode = await command.InvokeAsync([
                entryPath,
                "--project",
                projectPath,
                "--target",
                "Typed",
                "--profile-json",
                profilePath,
                "--no-color"
            ]);

            Assert.Equal(0, exitCode);
            using var document = JsonDocument.Parse(File.ReadAllText(profilePath));
            var events = document.RootElement.GetProperty("CodeGenEvents");
            var artifactKeyEvent = events.EnumerateArray()
                .Single(entry => entry.GetProperty("Name").GetString() == "full_build_artifact_key");
            var metadata = artifactKeyEvent.GetProperty("Metadata");
            Assert.Equal("Typed", metadata.GetProperty("target").GetString());
            Assert.Equal("2", metadata.GetProperty("sourceFiles").GetString());
            Assert.Equal("1", metadata.GetProperty("sourceRoots").GetString());
            Assert.Equal("1", metadata.GetProperty("parallelReads").GetString());
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static ProjectCommandInputResolution ResolveProject(string projectPath, string workingDirectory) =>
        ProjectCommandInputResolver.Resolve(
            projectPath,
            project: null,
            targetName: "main",
            explicitImportRoots: null,
            workingDirectory: workingDirectory);

    private static void WriteProject(string projectPath)
    {
        File.WriteAllText(projectPath, """
name = "hash-test"
sourceRoots = ["src"]

[[targets]]
name = "main"
entry = "src/Main.eidos"
kind = "executable"
""");
    }
}
