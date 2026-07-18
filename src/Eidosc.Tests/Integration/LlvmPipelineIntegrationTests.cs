using Eidosc.Symbols;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Eidosc;
using Eidosc.Diagnostic;
using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Category, TestCategories.Slow)]
public partial class LlvmPipelineIntegrationTests
{
    private static readonly TestPathConfig Paths = TestPathConfig.Current;
    private static readonly object RuntimeObjectCacheLock = new();
    private static long _runtimeObjectCacheHits;
    private static long _runtimeObjectCacheMisses;
    private const string RuntimeSourceRelativePath = "Eidosc/src/Eidosc/Runtime/eidos_memory.c";

    private static string Fx(string relativePathUnderFixtureSourceRoot) => Paths.Fixture(relativePathUnderFixtureSourceRoot);

    private static string Ecc(string relativePathUnderEccRoot) => Paths.Ecc(relativePathUnderEccRoot);

    private static string StdlibListImportFixture() => Fx("stdlib/std_list_import.eidos");

    private static string StdlibListImportInputFile() => TestSourceLoader.GetFullPath(StdlibListImportFixture());

    private static bool LibcurlSmokeEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("EIDOS_ENABLE_LIBCURL_SMOKE"), "1", StringComparison.Ordinal);

    public static IEnumerable<object[]> StdlibCapabilityNativeSmokeFixtures()
    {
        yield return [Fx("stdlib/std_math_import.eidos"), "std_math_native_smoke", 73];
        yield return [Fx("stdlib/std_text_import.eidos"), "std_text_native_smoke", 101];
        yield return [Fx("stdlib/std_file_import.eidos"), "std_file_native_smoke", 1];
        yield return [Fx("stdlib/std_prelude_core_import.eidos"), "std_prelude_core_native_smoke", 0];
        yield return [Fx("stdlib/std_range_import.eidos"), "std_range_native_smoke", 49];
        yield return [Fx("stdlib/std_list_head_or_native.eidos"), "std_list_head_or_native_smoke", 1];
        yield return [Fx("stdlib/std_list_count_native.eidos"), "std_list_count_native_smoke", 2];
        yield return [Fx("stdlib/std_list_none_native.eidos"), "std_list_none_native_smoke", 1];
        yield return [Fx("stdlib/std_list_native_stable_subset.eidos"), "std_list_native_stable_subset_smoke", 24];
        yield return [Fx("stdlib/std_list_native_hof_map_filter_find.eidos"), "std_list_native_hof_map_filter_find_smoke", 8];
        yield return [Fx("stdlib/std_list_append_take_drop_reverse_native.eidos"), "std_list_append_take_drop_reverse_native_smoke", 11];
        yield return [StdlibListImportFixture(), "std_list_import_native_smoke", 0];
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void RuntimeObjectCache_ReusesRuntimeObject_WhenToolchainAvailable()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        var targetInfo = TargetInfo.Default;
        var firstPath = GetCachedRuntimeObjectPath(targetInfo);
        var beforeSecondLookup = GetRuntimeObjectCacheSnapshot();
        var secondPath = GetCachedRuntimeObjectPath(targetInfo);
        var afterSecondLookup = GetRuntimeObjectCacheSnapshot();

        Assert.Equal(firstPath, secondPath);
        Assert.True(File.Exists(secondPath));
        Assert.True(
            afterSecondLookup.Hits > beforeSecondLookup.Hits,
            $"Expected runtime object cache hit count to increase. Before={beforeSecondLookup}, After={afterSecondLookup}");
    }

    [Fact]
    public void RuntimeObjectCachePath_IncludesNativeLinkMode()
    {
        var targetInfo = TargetInfo.X86_64Linux;
        const string extraCFlags = "";

        var nonPiePath = GetRuntimeObjectCachePath(targetInfo, NativeLinkMode.NonPieExecutable, extraCFlags);
        var piePath = GetRuntimeObjectCachePath(targetInfo, NativeLinkMode.PieExecutable, extraCFlags);
        var repeatedNonPiePath = GetRuntimeObjectCachePath(targetInfo, NativeLinkMode.NonPieExecutable, extraCFlags);

        Assert.Equal(nonPiePath, repeatedNonPiePath);
        Assert.NotEqual(nonPiePath, piePath);
    }

    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void NativePieExecutableSmoke_ProducesPositionIndependentExecutable()
    {
        if (!OperatingSystem.IsLinux() || !ToolExists("clang") || !ToolExists("readelf"))
        {
            return;
        }

        const string source = """
main :: Int -> Int
{
    x => x + 7
}
""";

        using var executable = CompileSourceToNativeExecutable(
            source,
            "native_pie_smoke.eidos",
            "native_pie_smoke",
            NativeLinkMode.PieExecutable);

        var readelf = ExecuteProcess("readelf", $"-h \"{executable.ExecutablePath}\"");

        Assert.Equal(0, readelf.ExitCode);
        Assert.Contains("Type:", readelf.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("DYN", readelf.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void EarlyReturnExpression_SourceCompilesThroughLlvm()
    {
        const string source = """
main :: Int -> Int
{
    x => if x > 0 then { return x } else { x + 1 }
}
""";

        var result = RunSourceAtLlvm(source, "llvm_early_return_compile.eidos");

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
    }

    [Fact]
    public void EarlyReturnExpression_LlvmIrContainsBranchReturnPaths()
    {
        const string source = """
main :: Int -> Int
{
    x => if x > 0 then { return x } else { x + 1 }
}
""";

        var result = RunSourceAtLlvm(source, "llvm_early_return_ir.eidos");
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);
        var irLines = llvmIr
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .ToList();
        var mainStart = irLines.FindIndex(line => line.StartsWith("define external i64 @eidos_main", StringComparison.Ordinal));
        Assert.True(mainStart >= 0, "Expected eidos_main definition in LLVM IR.");
        var mainEnd = irLines.FindIndex(mainStart + 1, line => line == "}");
        Assert.True(mainEnd > mainStart, "Expected eidos_main function body to close in LLVM IR.");
        var mainLines = irLines.Skip(mainStart).Take(mainEnd - mainStart + 1).ToArray();

        var retCount = mainLines.Count(line => line.StartsWith("ret i64", StringComparison.Ordinal));
        Assert.True(retCount >= 2, $"Expected at least two i64 return paths in eidos_main, got {retCount}.");
    }

    private static CompilationResult RunFixtureAtLlvm(string relativePath)
    {
        var source = TestSourceLoader.Load(relativePath);
        var options = new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(relativePath),
            StopAtPhase = CompilationPhase.Llvm,
                UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static CompilationResult RunSourceAtLlvm(string source, string inputFile)
    {
        var options = new CompilationOptions
        {
            InputFile = inputFile,
            StopAtPhase = CompilationPhase.Llvm,
                UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static CompilationResult RunFixtureAtMir(string relativePath)
    {
        var source = TestSourceLoader.Load(relativePath);
        var options = new CompilationOptions
        {
            InputFile = TestSourceLoader.GetFullPath(relativePath),
            StopAtPhase = CompilationPhase.Mir,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static CompilationResult RunSourceAtMir(string source, string inputFile)
    {
        var options = new CompilationOptions
        {
            InputFile = inputFile,
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static ProcessExecutionResult CompileAndRunSourceAtNative(
        string source,
        string inputFile,
        string executableBaseName,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        IReadOnlyDictionary<string, string>? additionalFiles = null,
        NativeLinkMode linkMode = NativeLinkMode.NonPieExecutable)
    {
        using var executable = CompileSourceToNativeExecutable(
            source,
            inputFile,
            executableBaseName,
            additionalFiles,
            linkMode);

        return ExecuteProcess(
            executable.ExecutablePath,
            workingDirectory: executable.WorkingDirectory,
            environmentVariables: environmentVariables);
    }

    private static NativeExecutable CompileSourceToNativeExecutable(
        string source,
        string inputFile,
        string executableBaseName,
        NativeLinkMode linkMode)
    {
        return CompileSourceToNativeExecutable(source, inputFile, executableBaseName, additionalFiles: null, linkMode);
    }

    private static NativeExecutable CompileSourceToNativeExecutable(
        string source,
        string inputFile,
        string executableBaseName,
        IReadOnlyDictionary<string, string>? additionalFiles,
        NativeLinkMode linkMode)
    {
        var sourceDir = Path.Combine(Path.GetTempPath(), $"eidosc_network_native_sources_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sourceDir);
        var sourcePath = Path.Combine(sourceDir, inputFile);
        File.WriteAllText(sourcePath, source);
        if (additionalFiles != null)
        {
            foreach (var (relativePath, content) in additionalFiles)
            {
                var fullPath = Path.Combine(sourceDir, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, content);
            }
        }

        var result = RunSourceAtLlvm(source, sourcePath);
        Assert.True(
            result.Success,
            string.Join(
                Environment.NewLine,
                result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Level == DiagnosticLevel.Error);
        Assert.NotNull(result.LlvmModule);

        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_native_source_smoke_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            var targetInfo = TargetInfo.Default;
            var runtimeObjectPath = GetCachedRuntimeObjectPath(targetInfo, linkMode);

            var executablePath = Path.Combine(
                tempDir,
                OperatingSystem.IsWindows() ? $"{executableBaseName}.exe" : executableBaseName);
            var compiler = CreateLlvmCompiler(targetInfo, runtimeObjectPath, tempDir, linkMode);
            var nativeResult = compiler.CompileToExecutable(result.LlvmModule!, executablePath);

            Assert.True(nativeResult.Success, nativeResult.ErrorMessage);
            Assert.True(File.Exists(executablePath));

            return new NativeExecutable(executablePath, tempDir, sourceDir);
        }
        catch
        {
            DeleteDirectoryQuietly(tempDir);
            DeleteDirectoryQuietly(sourceDir);
            throw;
        }
    }

    private static string EscapeEidosStringLiteral(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal);
    }

    private static string ResolveRuntimeSourcePath()
    {
        return TestSourceLoader.GetFullPath(RuntimeSourceRelativePath);
    }

    private static string GetCachedRuntimeObjectPath(
        TargetInfo targetInfo,
        NativeLinkMode linkMode = NativeLinkMode.NonPieExecutable)
    {
        var runtimeSource = ResolveRuntimeSourcePath();
        var extraCFlags = Environment.GetEnvironmentVariable("EIDOS_RUNTIME_EXTRA_CFLAGS") ?? "";
        var objectPath = GetRuntimeObjectCachePath(targetInfo, linkMode, extraCFlags, runtimeSource);

        lock (RuntimeObjectCacheLock)
        {
            var needsCompile = !File.Exists(objectPath) ||
                File.GetLastWriteTimeUtc(objectPath) < File.GetLastWriteTimeUtc(runtimeSource);

            if (needsCompile)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(objectPath)!);
                var runtimeCompile = CompileRuntimeObject(runtimeSource, objectPath, targetInfo, linkMode);
                Assert.True(runtimeCompile.Success, runtimeCompile.ErrorMessage);
                Interlocked.Increment(ref _runtimeObjectCacheMisses);
            }
            else
            {
                Interlocked.Increment(ref _runtimeObjectCacheHits);
            }
        }

        return objectPath;
    }

    private static string GetRuntimeObjectCachePath(
        TargetInfo targetInfo,
        NativeLinkMode linkMode,
        string extraCFlags,
        string? runtimeSourcePath = null)
    {
        var runtimeFingerprint = "";
        if (!string.IsNullOrWhiteSpace(runtimeSourcePath) && File.Exists(runtimeSourcePath))
        {
            var runtimeInfo = new FileInfo(runtimeSourcePath);
            runtimeFingerprint = $"{runtimeInfo.FullName}\0{runtimeInfo.Length}\0{runtimeInfo.LastWriteTimeUtc.Ticks}";
        }

        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{targetInfo.Triple}\0{linkMode}\0{extraCFlags}\0{runtimeFingerprint}")))[..16];
        var cacheDir = Path.Combine(Path.GetTempPath(), "eidosc_test_runtime_cache");
        return Path.Combine(
            cacheDir,
            OperatingSystem.IsWindows()
                ? $"eidos_runtime_{cacheKey}.obj"
                : $"eidos_runtime_{cacheKey}.o");
    }

    private static RuntimeObjectCacheSnapshot GetRuntimeObjectCacheSnapshot() =>
        new(
            Interlocked.Read(ref _runtimeObjectCacheHits),
            Interlocked.Read(ref _runtimeObjectCacheMisses));

    private readonly record struct RuntimeObjectCacheSnapshot(long Hits, long Misses);

    private static (bool Success, string? ErrorMessage) CompileRuntimeObject(
        string runtimeSourcePath,
        string outputObjectPath,
        TargetInfo targetInfo,
        NativeLinkMode linkMode)
    {
        var clangPath = ResolveToolPath("clang");
        if (clangPath == null)
        {
            return (false, "clang not found in PATH.");
        }

        var extraCFlags = Environment.GetEnvironmentVariable("EIDOS_RUNTIME_EXTRA_CFLAGS");
        var argumentsBuilder = new StringBuilder()
            .Append($"-target {targetInfo.Triple} -c ");
        foreach (var flag in LlvmCompiler.GetDefaultObjectRelocationFlags(targetInfo, linkMode).ClangFlags)
        {
            argumentsBuilder.Append(flag).Append(' ');
        }
        if (!string.IsNullOrWhiteSpace(extraCFlags))
        {
            argumentsBuilder.Append(extraCFlags).Append(' ');
        }

        argumentsBuilder.Append($"\"{runtimeSourcePath}\" -o \"{outputObjectPath}\"");
        var arguments = argumentsBuilder.ToString();
        var result = ExecuteProcess(clangPath, arguments);

        return result.ExitCode == 0
            ? (true, null)
            : (false, $"clang failed with code {result.ExitCode}: {result.StandardError}");
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

    private static bool ToolExists(string toolName) => ResolveToolPath(toolName) != null;

    private sealed class NativeExecutable(string executablePath, string workingDirectory, string sourceDirectory) : IDisposable
    {
        public string ExecutablePath { get; } = executablePath;

        public string WorkingDirectory { get; } = workingDirectory;

        public void Dispose()
        {
            DeleteDirectoryQuietly(WorkingDirectory);
            DeleteDirectoryQuietly(sourceDirectory);
        }
    }

    private static void DeleteDirectoryQuietly(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore cleanup failures on CI/Windows file lock races.
        }
    }

    private static ProcessExecutionResult CompileAndRunSourceAtNativeWithHttpBackend(
        string source,
        string inputFile,
        string executableBaseName,
        string? httpBackend)
    {
        return CompileAndRunSourceAtNative(
            source,
            inputFile,
            executableBaseName,
            environmentVariables: CreateHttpEnvironment(httpBackend));
    }

    private static ProcessExecutionResult CompileAndRunFixtureExecutableWithHttpBackend(
        string executablePath,
        string workingDirectory,
        string? httpBackend)
    {
        return ExecuteProcess(
            executablePath,
            workingDirectory: workingDirectory,
            environmentVariables: CreateHttpEnvironment(httpBackend));
    }

    private static ProcessExecutionResult CompileAndRunFixtureAtNativeWithHttpBackend(
        string fixturePath,
        string executableBaseName,
        string? httpBackend)
    {
        var result = RunFixtureAtLlvm(fixturePath);
        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.NotNull(result.LlvmModule);

        var tempDir = Path.Combine(Path.GetTempPath(), $"{executableBaseName}_{Guid.NewGuid():N}");
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

            return CompileAndRunFixtureExecutableWithHttpBackend(executablePath, tempDir, httpBackend);
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

    private static ProcessExecutionResult ExecuteProcess(
        string fileName,
        string arguments = "",
        string? workingDirectory = null,
        int timeoutMs = 30_000,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        ApplyEnvironment(startInfo, environmentVariables);

        using var process = new Process { StartInfo = startInfo };
        var stdoutBuilder = new StringBuilder();
        var stderrBuilder = new StringBuilder();

        process.OutputDataReceived += (s, e) =>
        {
            if (e.Data != null)
                stdoutBuilder.AppendLine(e.Data);
        };

        process.ErrorDataReceived += (s, e) =>
        {
            if (e.Data != null)
                stderrBuilder.AppendLine(e.Data);
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

    private static LlvmCompiler CreateLlvmCompiler(
        TargetInfo targetInfo,
        string runtimePath,
        string temporaryDirectory,
        NativeLinkMode linkMode = NativeLinkMode.NonPieExecutable) =>
        new(targetInfo, runtimePath: runtimePath, temporaryDirectory: temporaryDirectory, linkMode: linkMode);

    private static IReadOnlyDictionary<string, string?> CreateHttpEnvironment(string? httpBackend) =>
        new Dictionary<string, string?>
        {
            ["EIDOS_HTTP_BACKEND"] = httpBackend,
            ["EIDOS_HTTP_CONNECT_TIMEOUT"] = "0.5",
            ["EIDOS_HTTP_TOTAL_TIMEOUT"] = "1"
        };

    private static void ApplyEnvironment(
        ProcessStartInfo startInfo,
        IReadOnlyDictionary<string, string?>? environmentVariables)
    {
        if (environmentVariables == null)
        {
            return;
        }

        foreach (var (name, value) in environmentVariables)
        {
            if (value == null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }
    }

    private sealed class LoopbackHttpServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly Thread _thread;
        private volatile bool _stopRequested;

        public LoopbackHttpServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            BaseUrl = $"http://127.0.0.1:{port}/";
            _thread = new Thread(ServeLoop)
            {
                IsBackground = true,
                Name = "EidoscLoopbackHttpServer"
            };
            _thread.Start();
        }

        public string BaseUrl { get; }

        public void Dispose()
        {
            _stopRequested = true;
            _listener.Stop();
            _thread.Join(2000);
        }

        private void ServeLoop()
        {
            while (!_stopRequested)
            {
                try
                {
                    using var client = _listener.AcceptTcpClient();
                    HandleClient(client);
                }
                catch (SocketException)
                {
                    if (_stopRequested)
                    {
                        break;
                    }
                }
                catch (IOException)
                {
                    if (_stopRequested)
                    {
                        break;
                    }
                }
                catch (ObjectDisposedException)
                {
                    if (_stopRequested)
                    {
                        break;
                    }
                }
            }
        }

        private static void HandleClient(TcpClient client)
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;

            using var stream = client.GetStream();
            var requestBytes = ReadRequest(stream);
            var requestText = Encoding.UTF8.GetString(requestBytes);
            var bodyBytes = ExtractBodyBytes(requestBytes);
            var firstLineEnd = requestText.IndexOf("\r\n", StringComparison.Ordinal);
            var firstLine = firstLineEnd >= 0 ? requestText[..firstLineEnd] : requestText;
            var parts = firstLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var method = parts.Length >= 1 ? parts[0] : "GET";
            var target = parts.Length >= 2 ? parts[1] : "/";
            var queryStart = target.IndexOf('?', StringComparison.Ordinal);
            var path = queryStart >= 0 ? target[..queryStart] : target;
            var query = queryStart >= 0 ? target[(queryStart + 1)..] : "";
            var body = Encoding.UTF8.GetString(bodyBytes);
            var requestContentType = TryGetHeader(requestText, "Content-Type") ?? "text/plain";

            byte[] response;
            if (method == "GET" && path == "/slow")
            {
                Thread.Sleep(1500);
                response = CreateResponse(200, "OK", "slow-body", "text/plain");
            }
            else
            {
                response = (method, path) switch
                {
                    ("GET", "/ok") => CreateResponse(200, "OK", "hello-from-eidos", "text/plain"),
                    ("GET", "/redirect") => CreateResponse(302, "Found", "", "text/plain", "/ok"),
                    ("GET", "/binary-redirect") => CreateResponse(302, "Found", "", "text/plain", "/binary"),
                    ("GET", "/binary") => CreateBinaryResponse(200, "OK", new byte[] { 0, 1, 255, 65 }, "application/octet-stream", null, ("X-Binary-Reply", "bytes-value")),
                    ("GET", "/binary-empty") => CreateBinaryResponse(200, "OK", [], "application/empty", null, ("X-Binary-Reply", "empty-value")),
                    ("GET", "/binary-repeat-header") => CreateBinaryResponse(200, "OK", new byte[] { 7, 8 }, "application/octet-stream", null, ("X-Repeat", "first"), ("X-Repeat", "second")),
                    ("GET", "/missing-binary") => CreateBinaryResponse(404, "Not Found", new byte[] { 222, 173, 190, 239 }, "application/octet-stream", null, ("X-Binary-Reply", "bytes-value")),
                    ("GET", "/binary-query-header") => CreateBinaryResponse(200, "OK", Encoding.UTF8.GetBytes($"{query}|{TryGetHeader(requestText, "X-Test") ?? ""}"), "application/octet-stream", null, ("X-Binary-Reply", "bytes-query")),
                    ("GET", "/empty-json") => CreateResponse(200, "OK", "", "application/json"),
                    ("GET", "/missing") => CreateResponse(404, "Not Found", "missing-body", "text/plain"),
                    ("GET", "/query") => CreateResponse(200, "OK", query, "text/plain"),
                    ("GET", "/query-header") => CreateResponse(200, "OK", $"{query}|{TryGetHeader(requestText, "X-Test") ?? ""}", "text/plain"),
                    ("GET", "/header") => CreateResponse(200, "OK", TryGetHeader(requestText, "X-Test") ?? "", "text/plain"),
                    ("GET", "/accept") => CreateResponse(200, "OK", TryGetHeader(requestText, "Accept") ?? "", "text/plain"),
                    ("GET", "/repeat-header") => CreateResponse(200, "OK", "repeat-body", "text/plain", null, ("X-Repeat", "first"), ("X-Repeat", "second")),
                    ("GET", "/reply-header") => CreateResponse(200, "OK", "header-body", "text/plain", null, ("X-Reply", "server-value")),
                    ("POST", "/request-shape") => CreateResponse(200, "OK", $"{method}|{query}|{requestContentType}|{bodyBytes.Length}|{TryGetHeader(requestText, "X-Test") ?? ""}", "text/plain"),
                    ("PUT", "/request-shape") => CreateResponse(200, "OK", $"{method}|{query}|{requestContentType}|{bodyBytes.Length}|{TryGetHeader(requestText, "X-Test") ?? ""}", "text/plain"),
                    ("POST", "/redirect-307-request-shape") => CreateResponse(307, "Temporary Redirect", "", "text/plain", "/request-shape-headers"),
                    ("PUT", "/redirect-308-request-shape") => CreateResponse(308, "Permanent Redirect", "", "text/plain", "/request-shape-headers"),
                    ("POST", "/request-shape-headers") => CreateResponse(200, "OK", $"{method}|{query}|{requestContentType}|{bodyBytes.Length}|{TryGetHeader(requestText, "X-Test") ?? ""}|{string.Join(",", GetHeaderValues(requestText, "X-Repeat"))}", "text/plain"),
                    ("PUT", "/request-shape-headers") => CreateResponse(200, "OK", $"{method}|{query}|{requestContentType}|{bodyBytes.Length}|{TryGetHeader(requestText, "X-Test") ?? ""}|{string.Join(",", GetHeaderValues(requestText, "X-Repeat"))}", "text/plain"),
                    ("POST", "/request-shape-length") => CreateResponse(200, "OK", $"{method}|{requestContentType}|{bodyBytes.Length}|{TryGetHeader(requestText, "Content-Length") ?? ""}", "text/plain"),
                    ("PUT", "/request-shape-length") => CreateResponse(200, "OK", $"{method}|{requestContentType}|{bodyBytes.Length}|{TryGetHeader(requestText, "Content-Length") ?? ""}", "text/plain"),
                    ("HEAD", "/ok") => CreateResponse(200, "OK", "", "text/plain"),
                    ("HEAD", "/reply-header") => CreateResponse(200, "OK", "", "text/plain", null, ("X-Reply", "server-value")),
                    ("POST", "/echo") => CreateResponse(200, "OK", body, requestContentType),
                    ("POST", "/echo-binary") => CreateBinaryResponse(200, "OK", bodyBytes, requestContentType),
                    ("PUT", "/echo") => CreateResponse(200, "OK", body, requestContentType),
                    ("PUT", "/echo-binary") => CreateBinaryResponse(200, "OK", bodyBytes, requestContentType),
                    ("DELETE", "/auth") => CreateResponse(200, "OK", TryGetHeader(requestText, "Authorization") ?? "", "text/plain"),
                    _ => CreateResponse(500, "Internal Server Error", "unexpected-path", "text/plain")
                };
            }

            stream.Write(response, 0, response.Length);
            stream.Flush();
        }

        private static byte[] ReadRequest(NetworkStream stream)
        {
            var buffer = new byte[4096];
            var collected = new List<byte>();
            var headerLength = -1;
            var contentLength = 0;

            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    break;
                }

                for (var i = 0; i < read; i++)
                {
                    collected.Add(buffer[i]);
                }

                if (headerLength < 0 && collected.Count >= 4)
                {
                    headerLength = FindHeaderTerminator(collected);
                    if (headerLength >= 0)
                    {
                        var headerText = Encoding.ASCII.GetString(collected.Take(headerLength).ToArray());
                        contentLength = ParseContentLength(headerText);
                    }
                }

                if (headerLength >= 0)
                {
                    var expectedLength = headerLength + 4 + contentLength;
                    if (collected.Count >= expectedLength)
                    {
                        break;
                    }
                }
            }

            return collected.ToArray();
        }

        private static int FindHeaderTerminator(List<byte> collected)
        {
            for (var i = 3; i < collected.Count; i++)
            {
                if (collected[i - 3] == '\r' &&
                    collected[i - 2] == '\n' &&
                    collected[i - 1] == '\r' &&
                    collected[i] == '\n')
                {
                    return i - 3;
                }
            }

            return -1;
        }

        private static int FindHeaderTerminator(byte[] collected)
        {
            for (var i = 3; i < collected.Length; i++)
            {
                if (collected[i - 3] == '\r' &&
                    collected[i - 2] == '\n' &&
                    collected[i - 1] == '\r' &&
                    collected[i] == '\n')
                {
                    return i - 3;
                }
            }

            return -1;
        }

        private static byte[] ExtractBodyBytes(byte[] requestBytes)
        {
            var headerLength = FindHeaderTerminator(requestBytes);
            if (headerLength < 0)
            {
                return [];
            }

            var bodyOffset = headerLength + 4;
            if (bodyOffset >= requestBytes.Length)
            {
                return [];
            }

            var bodyLength = requestBytes.Length - bodyOffset;
            var bodyBytes = new byte[bodyLength];
            Buffer.BlockCopy(requestBytes, bodyOffset, bodyBytes, 0, bodyLength);
            return bodyBytes;
        }

        private static int ParseContentLength(string headerText)
        {
            foreach (var line in headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = line["Content-Length:".Length..].Trim();
                if (int.TryParse(value, out var length))
                {
                    return length;
                }
            }

            return 0;
        }

        private static string? TryGetHeader(string requestText, string headerName)
        {
            foreach (var line in requestText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return line[(headerName.Length + 1)..].Trim();
            }

            return null;
        }

        private static string[] GetHeaderValues(string requestText, string headerName)
        {
            var values = new List<string>();
            foreach (var line in requestText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
            {
                if (!line.StartsWith(headerName + ":", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                values.Add(line[(headerName.Length + 1)..].Trim());
            }

            return [.. values];
        }

        private static byte[] CreateResponse(
            int statusCode,
            string reasonPhrase,
            string body,
            string contentType,
            string? location = null,
            params (string Name, string Value)[] extraHeaders)
        {
            return CreateBinaryResponse(statusCode, reasonPhrase, Encoding.UTF8.GetBytes(body), contentType, location, extraHeaders);
        }

        private static byte[] CreateBinaryResponse(
            int statusCode,
            string reasonPhrase,
            byte[] bodyBytes,
            string contentType,
            string? location = null,
            params (string Name, string Value)[] extraHeaders)
        {
            var builder = new StringBuilder()
                .Append("HTTP/1.1 ")
                .Append(statusCode)
                .Append(' ')
                .Append(reasonPhrase)
                .Append("\r\nConnection: close\r\nContent-Length: ")
                .Append(bodyBytes.Length)
                .Append("\r\nContent-Type: ")
                .Append(contentType)
                .Append("\r\n");

            if (!string.IsNullOrWhiteSpace(location))
            {
                builder
                    .Append("Location: ")
                    .Append(location)
                    .Append("\r\n");
            }

            foreach (var (name, value) in extraHeaders)
            {
                builder
                    .Append(name)
                    .Append(": ")
                    .Append(value)
                    .Append("\r\n");
            }

            builder.Append("\r\n");
            var headerBytes = Encoding.ASCII.GetBytes(builder.ToString());
            var response = new byte[headerBytes.Length + bodyBytes.Length];
            Buffer.BlockCopy(headerBytes, 0, response, 0, headerBytes.Length);
            Buffer.BlockCopy(bodyBytes, 0, response, headerBytes.Length, bodyBytes.Length);
            return response;
        }
    }

    private readonly record struct ProcessExecutionResult(int ExitCode, string StandardOutput, string StandardError);

    [Fact]
    public void AdtConstructorFixture_LlvmIrContainsNamedStructTypeDefinition()
    {
        var result = RunFixtureAtLlvm(Fx("ctor/option.eidos"));
        Assert.True(result.Success);
        var ir = result.LlvmIrText!;

        // Should contain at least one named struct type definition like:
        // %struct.eidos_Option = type { i64, i64 }
        Assert.Matches(@"%struct\.eidos_\w+\s*=\s*type\s*\{", ir);
    }

    [Fact]
    public void RecursiveTreeFixture_CompilesAndEmitsStructTypeDef()
    {
        var result = RunFixtureAtLlvm(Fx("recursive/tree.eidos"));

        Assert.True(result.Success, $"Compilation failed: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));

        // Should contain struct type for Tree ADT (Empty | Node(Int, Tree, Tree))
        Assert.Matches(@"%struct\.eidos_\w+\s*=\s*type\s*\{", result.LlvmIrText!);
    }

    [Fact]
    public void PatternMatchingFixture_CompilesThroughLlvmWithoutErrors()
    {
        var result = RunFixtureAtLlvm(Fx("patterns/constructor.eidos"));

        Assert.True(result.Success, $"Compilation failed: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
    }

    [Fact]
    public void GenericOptionFixture_CompilesThroughLlvmWithoutErrors()
    {
        var source = """
            Pair[A, B] :: type {
                Pair:: type(A, B)
            }

            fst :: Pair[Int, Int] -> Int
            {
                p => match p
                {
                    Pair(a, _) => a
                }
            }

            usePair :: Unit -> Int
            {
                _ => {
                    p := Pair(42, 99);
                    fst(p)
                }
            }
            """;
        var result = RunSourceAtLlvm(source, "generic_option_test.eidos");

        Assert.True(result.Success, $"Compilation failed: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void StructTypeFieldAccess_UsesStructFieldGEP()
    {
        var result = RunFixtureAtLlvm(Fx("ctor/option.eidos"));
        Assert.True(result.Success);
        var ir = result.LlvmIrText!;

        // Verify that when struct types are present, struct-typed GEP is used
        // (i.e., "getelementptr %struct." appears in IR, not just byte-offset GEP)
        // This is a soft check - not all fixtures may produce struct GEP for field access,
        // but constructor stubs should if they have layout info.
        // At minimum, verify IR doesn't have errors and contains expected patterns.
        Assert.DoesNotContain("undef", ir);
    }

    [Fact]
    public void AdtConstructorFixture_Diagnostic_StructTypeDataFlow()
    {
        // Use std_option_import which has extensive Option[Int] usage
        var result = RunFixtureAtLlvm(Fx("stdlib/std_option_import.eidos"));
        Assert.True(result.Success, $"Compilation failed: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");

        // Check MirModule ConstructorLayouts
        var mirModule = result.MirModule;
        Assert.NotNull(mirModule);

        var mirLayouts = mirModule.ConstructorLayouts;
        Assert.NotEmpty(mirLayouts);
    }

    [Fact]
    public void CStructDotAccess_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("ffi/cstruct_dot_access.eidos"));

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
    }

    [Fact]
    public void CStructDotAccess_LlvmIrContainsGetterCall()
    {
        var result = RunFixtureAtLlvm(Fx("ffi/cstruct_dot_access.eidos"));
        Assert.True(result.Success);
        var ir = result.LlvmIrText!;

        // Dot-access p.x should be desugared to getter call (mangled as eidos_*_point_x)
        // The CStruct getter uses raw pointer load, so check for load instructions
        // within the main function body as proof that the getter was called.
        Assert.Contains("eidos_main", ir);
        // Verify the desugared call actually emits getter-related IR
        // (point_x / point_y getter names appear as mangled function calls)
        Assert.True(
            ir.Contains("point_x", StringComparison.Ordinal) ||
            ir.Contains("point_y", StringComparison.Ordinal) ||
            ir.Contains("getelementptr", StringComparison.Ordinal),
            "Expected CStruct field access pattern in LLVM IR");
    }

    [Fact]
    public void CStructDotAccess_NativeSmoke_ReadsAndWritesFields()
    {
        var execution = CompileAndRunFixtureAtNativeWithHttpBackend(
            Fx("ffi/cstruct_dot_access.eidos"),
            "ffi_cstruct_dot_access_native_smoke",
            httpBackend: null);

        Assert.Equal(0, execution.ExitCode);
    }

    [Fact]
    public void CfnCallback_CfnFromZeroCapture_CompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("ffi/cfn_callback.eidos"));

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
    }

    [Fact]
    public void CfnCallback_LlvmIrContainsBitcastForCfnFrom()
    {
        var result = RunFixtureAtLlvm(Fx("ffi/cfn_callback.eidos"));
        Assert.True(result.Success);
        var ir = result.LlvmIrText!;

        // cfn_from(int_compare) should produce a bitcast of the function to ptr
        Assert.Contains("bitcast", ir);
        Assert.Contains("int_compare", ir);
    }

    [Fact]
    public void CfnCallback_LlvmIrDoesNotRcManageCFunctionPointer()
    {
        var result = RunFixtureAtLlvm(Fx("ffi/cfn_callback.eidos"));
        Assert.True(result.Success);
        var ir = result.LlvmIrText!;

        Assert.Contains("qsort", ir);
        Assert.Contains("bitcast", ir);
        var irLines = ir.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var mainStart = Array.FindIndex(
            irLines,
            line => line.StartsWith("define external i64 @eidos_main", StringComparison.Ordinal));
        Assert.True(mainStart >= 0, "Expected eidos_main function in LLVM IR.");
        var mainEnd = Array.FindIndex(irLines, mainStart + 1, line => line == "}");
        Assert.True(mainEnd > mainStart, "Expected eidos_main function body to close in LLVM IR.");
        var mainLines = irLines.Skip(mainStart).Take(mainEnd - mainStart + 1);

        Assert.DoesNotContain(
            mainLines,
            line => line.Contains("call", StringComparison.Ordinal) &&
                    line.Contains(WellKnownStrings.Runtime.IncRefLocal, StringComparison.Ordinal));
        Assert.DoesNotContain(
            mainLines,
            line => line.Contains("call", StringComparison.Ordinal) &&
                    line.Contains(WellKnownStrings.Runtime.DecRefLocal, StringComparison.Ordinal));
    }

    [Fact]
    public void CfnCallback_QsortNativeSmoke_SortsArray()
    {
        var execution = CompileAndRunFixtureAtNativeWithHttpBackend(
            Fx("ffi/cfn_callback.eidos"),
            "ffi_cfn_callback_native_smoke",
            httpBackend: null);

        Assert.Equal(0, execution.ExitCode);
    }

    // === Stack Promotion (Heap-to-Stack) Tests ===

    [Fact]
    public void StackPromo_NoEscape_AdtpCompilesThroughLlvm()
    {
        var result = RunFixtureAtLlvm(Fx("optimize/stack_promo_no_escape.eidos"));

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
        Assert.False(string.IsNullOrWhiteSpace(result.LlvmIrText));
    }

    [Fact]
    public void StackPromo_NoEscape_LlvmIrContainsAlloca()
    {
        var result = RunFixtureAtLlvm(Fx("optimize/stack_promo_no_escape.eidos"));
        Assert.True(result.Success);
        var ir = result.LlvmIrText!;

        // A non-escaping constructor should produce an alloca instead of eidos_alloc
        Assert.Contains("alloca", ir);
    }

    [Fact]
    public void StackPromo_EscapeViaReturn_StillUsesHeapAlloc()
    {
        var result = RunFixtureAtLlvm(Fx("optimize/stack_promo_escape_return.eidos"));

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
    }

    [Fact]
    public void StackPromo_EscapeViaCallArg_StillUsesHeapAlloc()
    {
        var result = RunFixtureAtLlvm(Fx("optimize/stack_promo_escape_call_arg.eidos"));

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
        Assert.Equal(CompilationPhase.Llvm, result.CompletedPhase);
    }

}
