using System.Diagnostics;
using System.Text;
using Eidosc.CodeGen;
using Eidosc.Diagnostic;
using Eidosc.CodeGen.Llvm;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void RuntimeReadChar_AfterReadLine_WithRedirectedStdin_ReadsNextByte()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
main :: Unit -> Int need ffi, io
{
    _ => {
        line := read_line();
        terminal_set_raw();
        ch := read_char();
        terminal_restore();
        if ch == 113 then { 0 } else { 10 }
    }
}
""";

        var execution = CompileAndRunSourceAtNativeWithStandardInput(
            source,
            "native_redirected_read_char_source.eidos",
            "native_redirected_read_char_source",
            $"{Environment.NewLine}q");

        Assert.Equal(0, execution.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(execution.StandardError), execution.StandardError);
    }

    private static ProcessExecutionResult CompileAndRunSourceAtNativeWithStandardInput(
        string source,
        string inputFile,
        string executableBaseName,
        string standardInput)
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"eidosc_runtime_stdin_sources_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var sourcePath = Path.Combine(sourceDir, inputFile);
        File.WriteAllText(sourcePath, source);

        var result = RunSourceAtLlvm(source, sourcePath);
        Assert.True(
            result.Success,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_native_stdin_smoke_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var targetInfo = TargetInfo.Default;
            var runtimeObjectPath = GetCachedRuntimeObjectPath(targetInfo);

            var executablePath = Path.Combine(
                tempDir,
                OperatingSystem.IsWindows() ? $"{executableBaseName}.exe" : executableBaseName);
            var compiler = CreateLlvmCompiler(targetInfo, runtimeObjectPath, tempDir);
            var nativeResult = compiler.CompileToExecutable(result.LlvmModule!, executablePath);

            Assert.True(nativeResult.Success, nativeResult.ErrorMessage);
            Assert.True(File.Exists(executablePath));

            return ExecuteProcessWithStandardInput(executablePath, standardInput, workingDirectory: tempDir);
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

            try
            {
                Directory.Delete(sourceDir, recursive: true);
            }
            catch
            {
                // Ignore cleanup failures on CI/Windows file lock races.
            }
        }
    }

    private static ProcessExecutionResult ExecuteProcessWithStandardInput(
        string fileName,
        string standardInput,
        string? workingDirectory = null,
        int timeoutMs = 30_000)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stdoutBuilder.AppendLine(e.Data);
            }
        };

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data != null)
            {
                stderrBuilder.AppendLine(e.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Write(standardInput);
        process.StandardInput.Close();

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
}
