using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eidosc.Diagnostic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.BuildSystem;

public sealed record EidosBuildStepExecution(
    string Name,
    string Tool,
    int ExitCode,
    bool CacheHit,
    TimeSpan Elapsed,
    string StandardOutput,
    string StandardError);

public sealed record EidosBuildOutput(
    string Path,
    string Sha256,
    long Length);

public sealed record EidosBuildGraphExecutionResult(
    bool Success,
    bool CacheHit,
    IReadOnlyList<EidosBuildStepExecution> Steps,
    IReadOnlyList<EidosBuildOutput> Outputs,
    IReadOnlyList<Diagnostic.Diagnostic> Diagnostics);

internal static class EidosBuildGraphExecutor
{
    private sealed record CacheManifest(
        int SchemaVersion,
        string ExecutionKey,
        IReadOnlyList<EidosBuildOutput> Outputs);

    public static async Task<EidosBuildGraphExecutionResult> ExecuteAsync(
        EidosBuildGraph graph,
        BuildComptimeContext context,
        string cacheRoot,
        bool useCache,
        TimeSpan processTimeout,
        CancellationToken cancellationToken)
    {
        var executionKey = HashText($"eidos-build-execution-v1\0{context.CapabilityIdentity}\0{graph.CanonicalHash}");
        var cachePath = Path.Combine(cacheRoot, "build-host", $"{executionKey}.json");
        if (useCache && TryRestoreCache(cachePath, executionKey, context, out var cachedOutputs))
        {
            return new EidosBuildGraphExecutionResult(
                Success: true,
                CacheHit: true,
                Steps: graph.Steps.Select(static step => new EidosBuildStepExecution(
                    step.Name,
                    step.Tool,
                    0,
                    CacheHit: true,
                    TimeSpan.Zero,
                    string.Empty,
                    string.Empty)).ToArray(),
                Outputs: cachedOutputs,
                Diagnostics: []);
        }

        if (!EidosBuildGraphValidator.TryTopologicalOrder(graph.Steps, out var order, out var cycle))
        {
            var message = $"BuildGraph dependency cycle detected: {string.Join(" -> ", cycle)}.";
            return Failure(message, "E5010");
        }

        var stepExecutions = new List<EidosBuildStepExecution>(order.Count);
        foreach (var step in order)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!context.TryGetTool(step.Tool, out var tool, out var reason))
            {
                return Failure(reason, "E5020", stepExecutions);
            }

            var declaredOutputPaths = new List<string>(step.Outputs.Count);
            foreach (var relativeOutput in step.Outputs)
            {
                if (!context.TryResolveProjectPath(relativeOutput, out var outputPath, out reason))
                {
                    return Failure(reason, "E5021", stepExecutions);
                }

                var directory = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                declaredOutputPaths.Add(outputPath);
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = tool.FullPath,
                WorkingDirectory = context.ProjectDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            foreach (var argument in step.Arguments)
            {
                processStartInfo.ArgumentList.Add(argument);
            }

            processStartInfo.Environment.Clear();
            foreach (var variable in context.EnvironmentCapabilities.Where(static variable => variable.IsPresent))
            {
                processStartInfo.Environment[variable.Name] = variable.Value;
            }

            using var process = new Process { StartInfo = processStartInfo };
            var stopwatch = Stopwatch.StartNew();
            try
            {
                if (!process.Start())
                {
                    return Failure(
                        $"Registered BuildProcess tool '{step.Tool}' could not be started.",
                        "E5022",
                        stepExecutions);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                return Failure(
                    $"Registered BuildProcess tool '{step.Tool}' could not be started: {ex.Message}",
                    "E5022",
                    stepExecutions);
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(processTimeout);
            try
            {
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKill(process);
                var stdout = await CompleteOutputAsync(stdoutTask).ConfigureAwait(false);
                var stderr = await CompleteOutputAsync(stderrTask).ConfigureAwait(false);
                stopwatch.Stop();
                stepExecutions.Add(new EidosBuildStepExecution(
                    step.Name,
                    step.Tool,
                    -1,
                    CacheHit: false,
                    stopwatch.Elapsed,
                    stdout,
                    stderr));
                return Failure(
                    $"BuildGraph step '{step.Name}' exceeded the process timeout of {processTimeout}.",
                    "E5023",
                    stepExecutions);
            }

            var standardOutput = await stdoutTask.ConfigureAwait(false);
            var standardError = await stderrTask.ConfigureAwait(false);
            stopwatch.Stop();
            stepExecutions.Add(new EidosBuildStepExecution(
                step.Name,
                step.Tool,
                process.ExitCode,
                CacheHit: false,
                stopwatch.Elapsed,
                standardOutput,
                standardError));
            if (process.ExitCode != 0)
            {
                return Failure(
                    $"BuildGraph step '{step.Name}' failed with exit code {process.ExitCode}.",
                    "E5024",
                    stepExecutions,
                    standardError);
            }

            foreach (var outputPath in declaredOutputPaths)
            {
                if (!File.Exists(outputPath))
                {
                    return Failure(
                        $"BuildGraph step '{step.Name}' did not produce declared output '{context.ToRelativePath(outputPath)}'.",
                        "E5025",
                        stepExecutions);
                }
            }
        }

        var outputs = SnapshotOutputs(graph, context);
        if (useCache)
        {
            StoreCache(cachePath, new CacheManifest(1, executionKey, outputs));
        }

        return new EidosBuildGraphExecutionResult(
            Success: true,
            CacheHit: false,
            Steps: stepExecutions,
            Outputs: outputs,
            Diagnostics: []);
    }

    private static bool TryRestoreCache(
        string cachePath,
        string executionKey,
        BuildComptimeContext context,
        out IReadOnlyList<EidosBuildOutput> outputs)
    {
        outputs = [];
        if (!File.Exists(cachePath))
        {
            return false;
        }

        CacheManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<CacheManifest>(File.ReadAllText(cachePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }

        if (manifest is not { SchemaVersion: 1 } ||
            !string.Equals(manifest.ExecutionKey, executionKey, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var output in manifest.Outputs)
        {
            if (!context.TryResolveProjectPath(output.Path, out var fullPath, out _) ||
                !File.Exists(fullPath))
            {
                return false;
            }

            var info = new FileInfo(fullPath);
            if (info.Length != output.Length ||
                !string.Equals(HashFile(fullPath), output.Sha256, StringComparison.Ordinal))
            {
                return false;
            }
        }

        outputs = manifest.Outputs;
        return true;
    }

    private static IReadOnlyList<EidosBuildOutput> SnapshotOutputs(
        EidosBuildGraph graph,
        BuildComptimeContext context)
    {
        return graph.Steps
            .SelectMany(static step => step.Outputs)
            .Distinct(context.PathComparer)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .Select(path =>
            {
                context.TryResolveProjectPath(path, out var fullPath, out _);
                var info = new FileInfo(fullPath);
                return new EidosBuildOutput(path, HashFile(fullPath), info.Length);
            })
            .ToArray();
    }

    private static void StoreCache(string cachePath, CacheManifest manifest)
    {
        var directory = Path.GetDirectoryName(cachePath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, $".{Path.GetFileName(cachePath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporaryPath, cachePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static EidosBuildGraphExecutionResult Failure(
        string message,
        string code,
        IReadOnlyList<EidosBuildStepExecution>? steps = null,
        string? note = null)
    {
        var diagnostic = Diagnostic.Diagnostic.Error(message, code).WithLabel(SourceSpan.Empty, message);
        if (!string.IsNullOrWhiteSpace(note))
        {
            diagnostic.WithNote(note);
        }

        return new EidosBuildGraphExecutionResult(
            Success: false,
            CacheHit: false,
            Steps: steps ?? [],
            Outputs: [],
            Diagnostics: [diagnostic]);
    }

    private static async Task<string> CompleteOutputAsync(Task<string> output)
    {
        try
        {
            return await output.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
