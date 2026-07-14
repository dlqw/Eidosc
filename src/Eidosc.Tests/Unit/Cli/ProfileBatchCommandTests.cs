using Eidosc.Cli.Commands;
using Eidosc.CodeGen;
using Eidosc.Pipeline;
using System.CommandLine;
using System.Text.Json;
using Xunit;

namespace Eidosc.Tests.Unit.Cli;

public sealed class ProfileBatchCommandTests
{
    [Fact]
    public void GetEffectiveSnapshotTotalTimeMs_UsesCodeGenEventsWhenPipelineTimeIsZero()
    {
        var snapshot = new CompilationProfilingSnapshot
        {
            TotalTimeMs = 0,
            CodeGenEvents =
            [
                new CodeGenProfileEvent { Category = "link", Name = "clang_link_executable", ElapsedMs = 125.5, Success = true },
                new CodeGenProfileEvent { Category = "tool", Name = "find.clang", ElapsedMs = 0.5, Success = true }
            ]
        };

        Assert.Equal(126.0, ProfileBatchCommand.GetEffectiveSnapshotTotalTimeMs(snapshot), precision: 3);
    }

    [Fact]
    public void GetEffectiveSnapshotTotalTimeMs_PrefersPipelineTimeWhenPresent()
    {
        var snapshot = new CompilationProfilingSnapshot
        {
            TotalTimeMs = 42,
            CodeGenEvents =
            [
                new CodeGenProfileEvent { Category = "link", Name = "clang_link_executable", ElapsedMs = 125.5, Success = true }
            ]
        };

        Assert.Equal(42, ProfileBatchCommand.GetEffectiveSnapshotTotalTimeMs(snapshot));
    }

    [Fact]
    public async Task ProfileBatch_WithObjectGroupsCodegenMode_UsesSplitObjectPath()
    {
        if (!ToolExists("clang") || (!ToolExists("llc") && !ToolExists("clang")))
        {
            return;
        }

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_profile_batch_object_groups_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        var projectDir = Path.Combine(tempDir, "hello");
        var srcDir = Path.Combine(projectDir, "src");
        Directory.CreateDirectory(srcDir);
        var sourcePath = Path.Combine(srcDir, "Main.eidos");
        var projectPath = Path.Combine(projectDir, "eidos.toml");
        var outputPath = Path.Combine(tempDir, "profile.json");
        var executablePath = Path.Combine(tempDir, OperatingSystem.IsWindows() ? "hello.exe" : "hello");
        var manifestPath = Path.Combine(tempDir, "manifest.json");

        await File.WriteAllTextAsync(
            sourcePath,
            """
            Main :: module {
                main :: Unit -> Unit { _ => () }
            }
            """);
        await File.WriteAllTextAsync(
            projectPath,
            """
            [language]
            version = "0.6.0-alpha.1"

            [package]
            name = "hello-profile-object-groups"
            version = "0.1.0"
            """);
        await File.WriteAllTextAsync(
            manifestPath,
            $$"""
            {
              "Name": "object-groups-smoke",
              "Cases": [
                {
                  "Name": "hello-object-groups",
                  "Source": {{JsonSerializer.Serialize(sourcePath)}},
                  "Project": {{JsonSerializer.Serialize(projectPath)}},
                  "Target": "Native",
                  "Output": {{JsonSerializer.Serialize(executablePath)}},
                  "BuildMode": "Dev",
                  "OptimizationLevel": 0,
                  "CodegenMode": "ObjectGroups",
                  "MaxObjectGroups": 1,
                  "ClearObjectCacheBeforeRun": true
                }
              ]
            }
            """);

        try
        {
            var command = ProfileBatchCommand.Create();
            var exitCode = await command.InvokeAsync([
                manifestPath,
                "--format",
                "Json",
                "--output",
                outputPath,
                "--iterations",
                "1",
                "--no-color"
            ]);

            Assert.Equal(0, exitCode);
            Assert.True(File.Exists(executablePath));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(outputPath));
            var caseElement = document.RootElement.GetProperty("Cases")[0];
            var codeGenEvents = caseElement.GetProperty("CodeGenEvents");
            Assert.Contains(
                codeGenEvents.EnumerateArray(),
                static element => element.GetProperty("Name").GetString() == "object_cache.llvm_envelope");
            Assert.Contains(
                codeGenEvents.EnumerateArray(),
                static element => element.GetProperty("Name").GetString() == "object_cache.llvm_object_group");
            var summary = Assert.Single(
                codeGenEvents.EnumerateArray(),
                static element => element.GetProperty("Name").GetString() == "llvm_object_group_summary");
            Assert.True(int.Parse(summary.GetProperty("Metadata").GetProperty("groups").GetString()!) > 0);
            Assert.True(int.Parse(summary.GetProperty("Metadata").GetProperty("successfulObjects").GetString()!) > 0);
            Assert.True(summary.GetProperty("Metadata").TryGetProperty("cacheHits", out _));
            Assert.True(summary.GetProperty("Metadata").TryGetProperty("compileBatchElapsedMs", out _));
        }
        finally
        {
            try
            {
                Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    private static bool ToolExists(string toolName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            return false;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            if (File.Exists(Path.Combine(dir, toolName)))
            {
                return true;
            }

            if (OperatingSystem.IsWindows() &&
                File.Exists(Path.Combine(dir, $"{toolName}.exe")))
            {
                return true;
            }
        }

        return false;
    }
}
