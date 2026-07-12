using System.Diagnostics;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Native)]
[Trait(TestCategories.Category, TestCategories.Slow)]
public sealed class RuntimeConcurrencyNativeSmokeTests
{
    private static readonly string RuntimeRoot = TestSourceLoader.GetFullPath("Eidosc/src/Eidosc/Runtime");

    [Fact]
    public void RuntimeConcurrencyIntegration_CoversTaskGroupChannelMutex()
    {
        if (ResolveToolPath("clang") is null)
        {
            return;
        }

        var execution = CompileAndRunRuntimeTest(
            "tests/test_integration.c",
            "runtime_concurrency_integration",
            [
                "eidos_sync.c",
                "eidos_task.c",
                "eidos_scheduler.c",
                "eidos_channel.c",
                "eidos_promise.c",
                "eidos_memory.c"
            ]);

        AssertSuccessfulExecution(execution);
        Assert.Contains("10/10 passed", execution.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeSyncPrimitives_CoversMutexAndRwLock()
    {
        if (ResolveToolPath("clang") is null)
        {
            return;
        }

        var execution = CompileAndRunRuntimeTest(
            "tests/test_sync.c",
            "runtime_sync_primitives",
            [
                "eidos_sync.c",
                "eidos_task.c",
                "eidos_scheduler.c",
                "eidos_memory.c"
            ]);

        AssertSuccessfulExecution(execution);
        Assert.Contains("4 run, 4 passed, 0 failed", execution.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void RuntimeConcurrencyStress_CoversBoundedTaskChannelMutexPressure()
    {
        if (ResolveToolPath("clang") is null)
        {
            return;
        }

        var execution = CompileAndRunRuntimeTest(
            "tests/test_stress.c",
            "runtime_concurrency_stress",
            [
                "eidos_sync.c",
                "eidos_task.c",
                "eidos_scheduler.c",
                "eidos_channel.c",
                "eidos_memory.c"
            ]);

        AssertSuccessfulExecution(execution);
        Assert.Contains("3/3 passed", execution.StandardOutput, StringComparison.Ordinal);
    }

    private static void AssertSuccessfulExecution(ProcessExecutionResult execution)
    {
        Assert.True(
            execution.ExitCode == 0,
            $"Native test exited with code {execution.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{execution.StandardOutput}{Environment.NewLine}stderr:{Environment.NewLine}{execution.StandardError}");
    }

    private static ProcessExecutionResult CompileAndRunRuntimeTest(
        string testSourceRelativePath,
        string executableBaseName,
        IReadOnlyList<string> runtimeSourceRelativePaths)
    {
        var clangPath = ResolveToolPath("clang");
        Assert.False(string.IsNullOrWhiteSpace(clangPath), "clang not found in PATH.");

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidos_runtime_concurrency_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var executablePath = Path.Combine(
                tempDir,
                OperatingSystem.IsWindows() ? $"{executableBaseName}.exe" : executableBaseName);
            var compileStartInfo = new ProcessStartInfo
            {
                FileName = clangPath,
                WorkingDirectory = RuntimeRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            compileStartInfo.ArgumentList.Add(Path.Combine(RuntimeRoot, testSourceRelativePath));
            foreach (var runtimeSourceRelativePath in runtimeSourceRelativePaths)
            {
                compileStartInfo.ArgumentList.Add(Path.Combine(RuntimeRoot, runtimeSourceRelativePath));
            }

            compileStartInfo.ArgumentList.Add("-I");
            compileStartInfo.ArgumentList.Add(RuntimeRoot);
            compileStartInfo.ArgumentList.Add("-o");
            compileStartInfo.ArgumentList.Add(executablePath);

            var compile = ExecuteProcess(compileStartInfo);
            Assert.True(
                compile.ExitCode == 0,
                $"clang failed with code {compile.ExitCode}:{Environment.NewLine}{compile.StandardOutput}{compile.StandardError}");

            var runStartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = tempDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            return ExecuteProcess(runStartInfo);
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

    private static string? ResolveToolPath(string toolName)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            return null;
        }

        foreach (var dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir))
            {
                continue;
            }

            var candidate = Path.Combine(dir, toolName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (OperatingSystem.IsWindows())
            {
                var exeCandidate = Path.Combine(dir, $"{toolName}.exe");
                if (File.Exists(exeCandidate))
                {
                    return exeCandidate;
                }
            }
        }

        return null;
    }

    private static ProcessExecutionResult ExecuteProcess(ProcessStartInfo startInfo, int timeoutMs = 30_000)
    {
        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringWriter();
        var stderrBuilder = new StringWriter();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdoutBuilder.WriteLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderrBuilder.WriteLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(timeoutMs))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
            }
            catch
            {
                // Ignore kill failures for cleanup.
            }

            return new ProcessExecutionResult(
                -1,
                stdoutBuilder.ToString(),
                $"Process timed out after {timeoutMs / 1000} seconds.{Environment.NewLine}{stderrBuilder}");
        }

        process.WaitForExit();
        return new ProcessExecutionResult(process.ExitCode, stdoutBuilder.ToString(), stderrBuilder.ToString());
    }

    private readonly record struct ProcessExecutionResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
