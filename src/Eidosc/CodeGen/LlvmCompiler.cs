using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Eidosc.Diagnostic;
using Eidosc.CodeGen.Llvm;

namespace Eidosc.CodeGen;

/// <summary>
/// LLVM 编译器接口 - 将 LLVM IR 编译为本地代码
/// </summary>
public sealed partial class LlvmCompiler
{
    private static readonly object ToolCacheLock = new();
    private static readonly Dictionary<string, string?> ToolPathCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RuntimeSourceCacheLock = new();
    private static readonly Dictionary<string, string[]> RuntimeSourceListCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object RuntimeResolutionCacheLock = new();
    private static readonly Dictionary<string, string> RuntimeResolutionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly TargetInfo _targetInfo;
    private readonly string? _llvmToolsPath;
    private readonly int _optimizationLevel;
    private readonly bool _enableLto;
    private readonly string? _runtimePath;
    private readonly string? _extraCFlags;
    private readonly string? _extraLinkFlags;
    private readonly NativeLinkMode _linkMode;
    private readonly string _temporaryDirectory;
    private readonly CodeGenProfile? _profile;
    private readonly LlvmBackendConfiguration _backendConfiguration;
    private readonly int _maxDegreeOfParallelism;

    public LlvmCompiler(TargetInfo? targetInfo = null, string? llvmToolsPath = null,
        int optimizationLevel = 2, bool enableLto = false,
        string? runtimePath = null, string? extraCFlags = null,
        string? extraLinkFlags = null, string? temporaryDirectory = null,
        NativeLinkMode linkMode = NativeLinkMode.NonPieExecutable,
        CodeGenProfile? profile = null,
        int maxDegreeOfParallelism = 0)
    {
        _targetInfo = targetInfo ?? TargetInfo.Default;
        _llvmToolsPath = llvmToolsPath;
        _optimizationLevel = Math.Clamp(optimizationLevel, 0, 3);
        _enableLto = enableLto;
        _runtimePath = runtimePath;
        _extraCFlags = extraCFlags;
        _extraLinkFlags = extraLinkFlags;
        _linkMode = linkMode;
        _profile = profile;
        _maxDegreeOfParallelism = maxDegreeOfParallelism > 0
            ? maxDegreeOfParallelism
            : Math.Max(1, Environment.ProcessorCount);
        _temporaryDirectory = string.IsNullOrWhiteSpace(temporaryDirectory)
            ? Path.GetTempPath()
            : temporaryDirectory;
        _backendConfiguration = LlvmBackendConfiguration.Create(
            _targetInfo,
            _optimizationLevel,
            _enableLto,
            _linkMode,
            _extraCFlags,
            _extraLinkFlags);
        RecordCompilerConfiguration();
    }

    private void RecordCompilerConfiguration()
    {
        _profile?.Record(
            "config",
            "llvm_compiler",
            tool: null,
            TimeSpan.Zero,
            success: true,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["targetTriple"] = _targetInfo.Triple,
                ["targetCpu"] = _targetInfo.Cpu,
                ["targetFeatures"] = _targetInfo.Features,
                ["optimizationLevel"] = _optimizationLevel.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["lto"] = _enableLto.ToString(),
                ["nativeCpu"] = string.Equals(_targetInfo.Cpu, "native", StringComparison.OrdinalIgnoreCase).ToString(),
                ["linkMode"] = _linkMode.ToString(),
                ["runtimePath"] = _runtimePath ?? "",
                ["extraCFlags"] = _extraCFlags ?? "",
                ["extraLinkFlags"] = _extraLinkFlags ?? "",
                ["jobs"] = _maxDegreeOfParallelism.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["backendConfigHash"] = _backendConfiguration.StableHash
            });
    }

    /// <summary>
    /// 将 LLVM 模块编译为 LLVM IR 文本
    /// </summary>
    public string CompileToIr(LlvmModule module)
    {
        using var _ = _profile?.Measure("llvm", "emit_ir");
        var emitter = new LlvmEmitter();
        return emitter.Emit(module, _targetInfo.DataLayout, _targetInfo.Triple);
    }

    /// <summary>
    /// 将 LLVM IR 文本编译为目标文件
    /// </summary>
    public CodeGenResult CompileToObject(string llvmIr, string outputPath)
    {
        return CompileIrToObject(
            llvmIr,
            outputPath,
            "llvm-ir-object-v1",
            "object",
            "object_cache.llvm_ir",
            [$"backendConfig={_backendConfiguration.StableHash}"]);
    }

    private CodeGenResult CompileIrToObject(
        string llvmIr,
        string outputPath,
        string cacheSchema,
        string profileCategory,
        string profileName,
        IReadOnlyList<string> extraIdentityParts)
    {
        var cacheKey = ComputeObjectCacheKey(
            cacheSchema,
            llvmIr,
            outputPath,
            extraIdentityParts);
        var objectCache = TryCopyCachedObject(cacheKey, outputPath, profileCategory, profileName);
        if (objectCache != null)
        {
            return objectCache;
        }

        var tempIrPath = CreateTemporaryPath("", ".ll");

        try
        {
            using (_profile?.Measure("llvm", "write_temp_ir"))
            {
                File.WriteAllText(tempIrPath, llvmIr);
            }

            // 调用 llc 编译为目标文件
            var result = RunLlc(tempIrPath, outputPath);
            if (result.Success)
            {
                StoreObjectCache(cacheKey, outputPath, profileCategory, profileName);
                return new CodeGenResult
                {
                    Success = true,
                    Output = result.Output,
                    ErrorMessage = result.ErrorMessage,
                    ExitCode = result.ExitCode,
                    OutputPath = outputPath
                };
            }

            return result;
        }
        finally
        {
            if (File.Exists(tempIrPath))
            {
                File.Delete(tempIrPath);
            }
        }
    }

    /// <summary>
    /// 将目标文件链接为可执行文件
    /// </summary>
    public CodeGenResult LinkExecutable(
        string[] objectFiles,
        string outputPath,
        string[]? libraries = null,
        string[]? libraryPaths = null,
        string[]? linkerFlags = null)
    {
        return RunClang(objectFiles, outputPath, libraries, libraryPaths, linkerFlags);
    }

    /// <summary>
    /// 完整编译流程：LLVM 模块 -> 可执行文件
    /// </summary>
    public CodeGenResult CompileToExecutable(LlvmModule module, string outputPath)
    {
        // 1. 生成 LLVM IR
        var llvmIr = CompileToIr(module);

        // 2. 编译为目标文件
        var tempObjPath = CreateTemporaryPath("", _targetInfo.ObjectExtension);
        var tempEntrySourcePath = CreateTemporaryPath("entry_", ".c");
        var tempEntryObjPath = CreateTemporaryPath("entry_", _targetInfo.ObjectExtension);
        var tempNativeObjects = new List<string>();
        var tempRuntimeObjects = new List<string>();

        try
        {
            var objResult = CompileToObject(llvmIr, tempObjPath);
            if (!objResult.Success)
            {
                return objResult;
            }

            var objectFiles = new List<string> { tempObjPath };
            var entryResult = TryCompileEntryShim(module, tempEntrySourcePath, tempEntryObjPath);
            if (!entryResult.Success)
            {
                return entryResult;
            }

            if (File.Exists(tempEntryObjPath))
            {
                objectFiles.Add(tempEntryObjPath);
            }

            var nativeResults = CompileNativeSources(module.NativeSources, module.NativeIncludePaths);
            tempNativeObjects.AddRange(nativeResults.Select(result => result.ObjectPath));
            foreach (var nativeResult in nativeResults)
            {
                if (!nativeResult.Result.Success)
                {
                    return nativeResult.Result;
                }

                objectFiles.Add(nativeResult.ObjectPath);
            }

            var runtimeResolveResult = TryResolveRuntimeLinkInputs(out var runtimeLinkInputs, tempRuntimeObjects);
            if (!runtimeResolveResult.Success)
            {
                return runtimeResolveResult;
            }

            // 3. 链接为可执行文件
            objectFiles.AddRange(runtimeLinkInputs);
            var linkLibraries = module.LinkLibraries.Count > 0 ? module.LinkLibraries.ToArray() : null;
            var linkLibraryPaths = module.LinkLibraryPaths.Count > 0 ? module.LinkLibraryPaths.ToArray() : null;
            var linkerFlags = module.LinkerFlags.Count > 0 ? module.LinkerFlags.ToArray() : null;
            return LinkExecutable(objectFiles.ToArray(), outputPath, linkLibraries, linkLibraryPaths, linkerFlags);
        }
        finally
        {
            if (File.Exists(tempObjPath))
            {
                File.Delete(tempObjPath);
            }

            if (File.Exists(tempEntryObjPath))
            {
                File.Delete(tempEntryObjPath);
            }

            if (File.Exists(tempEntrySourcePath))
            {
                File.Delete(tempEntrySourcePath);
            }

            foreach (var tempNativeObject in tempNativeObjects)
            {
                if (File.Exists(tempNativeObject))
                {
                    File.Delete(tempNativeObject);
                }
            }

            foreach (var tempRuntimeObject in tempRuntimeObjects)
            {
                if (File.Exists(tempRuntimeObject))
                {
                    File.Delete(tempRuntimeObject);
                }
            }
        }
    }

    /// <summary>
    /// 运行 llc (LLVM static compiler)
    /// </summary>
    private CodeGenResult RunLlc(string irPath, string outputPath)
    {
        var llcPath = FindTool("llc");
        if (llcPath != null)
        {
            var relocationFlags = GetDefaultObjectRelocationFlags(_targetInfo, _linkMode);
            var arguments = new StringBuilder();
            arguments.Append($"-filetype=obj ");
            arguments.Append($"--mtriple={_targetInfo.Triple} ");
            foreach (var flag in relocationFlags.LlcFlags)
            {
                arguments.Append(flag).Append(' ');
            }

            // CPU 特性
            if (!string.IsNullOrEmpty(_targetInfo.Cpu))
            {
                arguments.Append($"-mcpu={_targetInfo.Cpu} ");
            }
            if (!string.IsNullOrEmpty(_targetInfo.Features))
            {
                arguments.Append($"-mattr={_targetInfo.Features} ");
            }

            // 优化级别
            arguments.Append($"-O{_optimizationLevel} ");

            // 输入输出
            arguments.Append($"-o \"{outputPath}\" ");
            arguments.Append($"\"{irPath}\"");

            return RunProcess(llcPath, arguments.ToString(), "object", "llc_compile_ir");
        }

        var clangPath = FindTool("clang");
        if (clangPath != null)
        {
            return RunClangCompileIr(clangPath, irPath, outputPath);
        }

        return new CodeGenResult
        {
            Success = false,
            ErrorMessage = DiagnosticMessages.LlvmToolsNotFound
        };
    }

    private CodeGenResult RunClangCompileIr(string clangPath, string irPath, string outputPath)
    {
        var relocationFlags = GetDefaultObjectRelocationFlags(_targetInfo, _linkMode);
        var arguments = new StringBuilder();
        arguments.Append($"-target {_targetInfo.Triple} ");
        arguments.Append("-c ");
        arguments.Append("-x ir ");
        arguments.Append($"-O{_optimizationLevel} ");
        foreach (var flag in relocationFlags.ClangFlags)
        {
            arguments.Append(flag).Append(' ');
        }

        // CPU tuning: clang on Windows MSVC uses -march, not -mcpu.
        // Skip for generic "x86-64" CPU to avoid "unsupported option" errors.
        if (!string.IsNullOrEmpty(_targetInfo.Cpu) &&
            !string.Equals(_targetInfo.Cpu, "native", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(_targetInfo.Cpu, "x86-64", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Append($"-mcpu={_targetInfo.Cpu} ");
        }
        else if (string.Equals(_targetInfo.Cpu, "native", StringComparison.OrdinalIgnoreCase))
        {
            // -mcpu=native is supported by clang on all platforms
            arguments.Append("-mcpu=native ");
        }

        // LTO for compilation step
        if (_enableLto)
        {
            arguments.Append("-flto ");
        }

        arguments.Append($"-o \"{outputPath}\" ");
        arguments.Append($"\"{irPath}\"");

        return RunProcess(clangPath, arguments.ToString(), "object", "clang_compile_ir");
    }

    private CodeGenResult CompileNativeSource(
        string sourcePath,
        string outputObjectPath,
        IReadOnlyList<string> includePaths)
    {
        if (!File.Exists(sourcePath))
        {
            return new CodeGenResult
            {
                Success = false,
                ErrorMessage = DiagnosticMessages.NativeFfiSourceFileNotFound(sourcePath)
            };
        }

        var clangPath = FindTool("clang");
        if (clangPath == null)
        {
            return new CodeGenResult
            {
                Success = false,
                ErrorMessage = DiagnosticMessages.ClangNotFoundForNativeFfi
            };
        }

        var nativeCacheKey = ComputeFileObjectCacheKey(
            "native-source-object-v1",
            sourcePath,
            outputObjectPath,
            includePaths.Concat(GetDefaultClangObjectCompileFlags()));
        var cachedNative = TryCopyCachedObject(nativeCacheKey, outputObjectPath, "native", $"object_cache.native.{Path.GetFileName(sourcePath)}");
        if (cachedNative != null)
        {
            return cachedNative;
        }

        var arguments = new StringBuilder();
        arguments.Append($"-target {_targetInfo.Triple} ");
        arguments.Append("-c ");
        arguments.Append($"-O{_optimizationLevel} ");
        foreach (var flag in GetDefaultClangObjectCompileFlags())
        {
            arguments.Append(flag).Append(' ');
        }

        foreach (var includePath in includePaths)
        {
            arguments.Append($"-I\"{includePath}\" ");
        }

        arguments.Append($"\"{sourcePath}\" ");
        arguments.Append($"-o \"{outputObjectPath}\"");

        var result = RunProcess(clangPath, arguments.ToString(), "native", $"compile_native_source.{Path.GetFileName(sourcePath)}");
        if (result.Success)
        {
            StoreObjectCache(nativeCacheKey, outputObjectPath, "native", $"object_cache.native.{Path.GetFileName(sourcePath)}");
        }

        return result;
    }

    private readonly record struct NativeCompileResult(string ObjectPath, CodeGenResult Result);

    /// <summary>
    /// 运行 clang 进行链接
    /// </summary>
    private CodeGenResult RunClang(
        string[] objectFiles,
        string outputPath,
        string[]? libraries,
        string[]? libraryPaths,
        string[]? linkerFlags)
    {
        var clangPath = FindTool("clang");
        if (clangPath == null)
        {
            return new CodeGenResult
            {
                Success = false,
                ErrorMessage = DiagnosticMessages.ClangNotFound
            };
        }

        var arguments = new List<string>();

        // 目标三元组
        arguments.Add("-target");
        arguments.Add(_targetInfo.Triple);

        // 优化级别
        arguments.Add($"-O{_optimizationLevel}");

        // LTO
        if (_enableLto)
        {
            arguments.Add("-flto");
        }

        // CPU tuning for linker (affects LTO codegen).
        // Skip generic "x86-64" CPU on clang (causes errors on MSVC target).
        if (!string.IsNullOrEmpty(_targetInfo.Cpu) &&
            !string.Equals(_targetInfo.Cpu, "x86-64", StringComparison.OrdinalIgnoreCase))
        {
            arguments.Add($"-mcpu={_targetInfo.Cpu}");
        }

        // 输入文件
        foreach (var objFile in objectFiles)
        {
            arguments.Add(objFile);
        }

        // 库文件
        if (libraryPaths != null)
        {
            foreach (var libraryPath in libraryPaths)
            {
                arguments.Add($"-L{libraryPath}");
            }
        }

        if (libraries != null)
        {
            foreach (var lib in libraries)
            {
                arguments.Add($"-l{lib}");
            }
        }

        foreach (var linkerFlag in GetDefaultExecutableLinkerFlags(_targetInfo, _linkMode, linkerFlags))
        {
            arguments.Add(linkerFlag);
        }

        if (linkerFlags != null)
        {
            foreach (var linkerFlag in linkerFlags)
            {
                if (!string.IsNullOrWhiteSpace(linkerFlag))
                {
                    arguments.Add(linkerFlag);
                }
            }
        }

        var extraLinkFlags = _extraLinkFlags ?? Environment.GetEnvironmentVariable(WellKnownStrings.EnvVars.ExtraLdFlags);
        if (!string.IsNullOrWhiteSpace(extraLinkFlags))
        {
            arguments.AddRange(SplitCommandLineFlags(extraLinkFlags));
        }

        // 输出文件
        arguments.Add("-o");
        arguments.Add(outputPath);

        // 添加运行时库路径
        var runtimeLibDir = GetRuntimeLibraryDir();
        if (Directory.Exists(runtimeLibDir))
        {
            arguments.Add($"-L{runtimeLibDir}");
        }

        return RunProcessWithResponseFileIfNeeded(clangPath, arguments, "link", "clang_link_executable");
    }

    internal static IReadOnlyList<string> GetDefaultExecutableLinkerFlags(
        TargetInfo targetInfo,
        NativeLinkMode linkMode,
        IReadOnlyList<string>? linkerFlags)
    {
        if (targetInfo.Os is not TargetOs.Linux || targetInfo.ObjectFormat is not TargetObjectFormat.Elf)
        {
            return [];
        }

        if (HasExplicitElfImageMode(linkerFlags))
        {
            return [];
        }

        return linkMode switch
        {
            NativeLinkMode.NonPieExecutable => ["-no-pie"],
            NativeLinkMode.PieExecutable => ["-pie"],
            _ => []
        };
    }

    internal static NativeObjectRelocationFlags GetDefaultObjectRelocationFlags(
        TargetInfo targetInfo,
        NativeLinkMode linkMode)
    {
        if (targetInfo.Os is not TargetOs.Linux || targetInfo.ObjectFormat is not TargetObjectFormat.Elf)
        {
            return NativeObjectRelocationFlags.Empty;
        }

        return linkMode switch
        {
            NativeLinkMode.NonPieExecutable => new NativeObjectRelocationFlags(["-relocation-model=static"], []),
            NativeLinkMode.PieExecutable => new NativeObjectRelocationFlags(["-relocation-model=pic"], ["-fPIE"]),
            _ => NativeObjectRelocationFlags.Empty
        };
    }

    private static bool HasExplicitElfImageMode(IReadOnlyList<string>? linkerFlags)
    {
        if (linkerFlags == null)
        {
            return false;
        }

        foreach (var linkerFlag in linkerFlags)
        {
            if (string.IsNullOrWhiteSpace(linkerFlag))
            {
                continue;
            }

            var flag = linkerFlag.Trim();
            if (flag is "-no-pie" or "-nopie" or "-pie" or "-shared" or "-static-pie")
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<string> GetDefaultClangObjectCompileFlags() =>
        GetDefaultObjectRelocationFlags(_targetInfo, _linkMode).ClangFlags;

    private CodeGenResult TryCompileEntryShim(LlvmModule module, string sourcePath, string outputObjectPath)
    {
        return TryCompileEntryShim(
            module.Functions.Select(static function => function.Name).Where(static name => !string.IsNullOrWhiteSpace(name)),
            sourcePath,
            outputObjectPath);
    }

    private CodeGenResult TryCompileEntryShim(
        IEnumerable<string> functionNames,
        string sourcePath,
        string outputObjectPath)
    {
        var names = functionNames.ToHashSet(StringComparer.Ordinal);
        if (names.Contains(WellKnownStrings.SpecialNames.Main))
        {
            return new CodeGenResult { Success = true };
        }

        if (!names.Contains(WellKnownStrings.Runtime.Main))
        {
            return new CodeGenResult
            {
                Success = false,
                ErrorMessage = DiagnosticMessages.ExecutableEntryNotFound
            };
        }

        var clangPath = FindTool("clang");
        if (clangPath == null)
        {
            return new CodeGenResult
            {
                Success = false,
                ErrorMessage = DiagnosticMessages.ClangNotFoundForEntryShim
            };
        }

        var entryCacheKey = ComputeObjectCacheKey(
            "entry-shim-object-v1",
            BuildEntryShimSource(),
            outputObjectPath,
            GetDefaultClangObjectCompileFlags());
        var cachedEntry = TryCopyCachedObject(entryCacheKey, outputObjectPath, "entry", "object_cache.entry_shim");
        if (cachedEntry != null)
        {
            File.WriteAllText(sourcePath, BuildEntryShimSource());
            return cachedEntry;
        }

        File.WriteAllText(sourcePath, BuildEntryShimSource());

        var arguments = new StringBuilder();
        arguments.Append($"-target {_targetInfo.Triple} ");
        arguments.Append("-c ");
        foreach (var flag in GetDefaultClangObjectCompileFlags())
        {
            arguments.Append(flag).Append(' ');
        }
        arguments.Append($"\"{sourcePath}\" ");
        arguments.Append($"-o \"{outputObjectPath}\"");

        var result = RunProcess(clangPath, arguments.ToString(), "entry", "compile_entry_shim");
        if (result.Success)
        {
            StoreObjectCache(entryCacheKey, outputObjectPath, "entry", "object_cache.entry_shim");
        }

        return result;
    }

    private IReadOnlyList<NativeCompileResult> CompileNativeSources(
        IReadOnlyList<string> nativeSources,
        IReadOnlyList<string> includePaths)
    {
        if (nativeSources.Count == 0)
        {
            return [];
        }

        var results = new NativeCompileResult[nativeSources.Count];
        Parallel.For(
            0,
            nativeSources.Count,
            CreateBoundedObjectCompileParallelOptions(),
            index =>
            {
                var nativeObjPath = CreateTemporaryPath("native_", _targetInfo.ObjectExtension);
                results[index] = new NativeCompileResult(
                    nativeObjPath,
                    CompileNativeSource(nativeSources[index], nativeObjPath, includePaths));
            });
        return results;
    }

    private static string BuildEntryShimSource()
    {
        return """
               #include <stdint.h>
               extern void eidos_command_line_init(int64_t argc, char** argv);
               extern int64_t eidos_main(int64_t argc);

               __attribute__((weak)) void eidos_module_init(void) {}

               int main(int argc, char** argv)
               {
                   eidos_command_line_init((int64_t)argc, argv);
                   eidos_module_init();
                   return (int)eidos_main((int64_t)argc);
               }
               """;
    }

    /// <summary>
    /// 查找 LLVM 工具
    /// </summary>
    private string? FindTool(string toolName)
    {
        var cacheKey = $"{_llvmToolsPath ?? "<path>"}|{toolName}|{Environment.GetEnvironmentVariable("PATH") ?? string.Empty}";
        lock (ToolCacheLock)
        {
            if (ToolPathCache.TryGetValue(cacheKey, out var cachedPath))
            {
                _profile?.Record("tool", $"find.{toolName}", toolName, TimeSpan.Zero, cachedPath != null, cacheHit: true);
                return cachedPath;
            }
        }

        var sw = Stopwatch.StartNew();
        string? resolvedPath = null;

        // 如果指定了工具路径
        if (_llvmToolsPath != null)
        {
            var path = Path.Combine(_llvmToolsPath, toolName);
            if (File.Exists(path))
            {
                resolvedPath = path;
            }
        }

        // 尝试在 PATH 中查找
        if (resolvedPath == null)
        {
            var pathVar = Environment.GetEnvironmentVariable("PATH");
            if (pathVar != null)
            {
                foreach (var dir in pathVar.Split(Path.PathSeparator))
                {
                    var path = Path.Combine(dir, toolName);
                    if (File.Exists(path))
                    {
                        resolvedPath = path;
                        break;
                    }

                    // Windows 可能需要 .exe 扩展名
                    if (OperatingSystem.IsWindows())
                    {
                        path = Path.Combine(dir, $"{toolName}.exe");
                        if (File.Exists(path))
                        {
                            resolvedPath = path;
                            break;
                        }
                    }
                }
            }
        }

        sw.Stop();
        lock (ToolCacheLock)
        {
            ToolPathCache[cacheKey] = resolvedPath;
        }

        _profile?.Record("tool", $"find.{toolName}", toolName, sw.Elapsed, resolvedPath != null);
        return resolvedPath;
    }

    /// <summary>
    /// 运行外部进程
    /// </summary>
    private CodeGenResult RunProcess(
        string executable,
        string arguments,
        string category = "external",
        string? name = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            var outputBuilder = new StringBuilder();
            var errorBuilder = new StringBuilder();

            process.OutputDataReceived += (s, e) =>
            {
                if (e.Data != null)
                    outputBuilder.AppendLine(e.Data);
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (e.Data != null)
                    errorBuilder.AppendLine(e.Data);
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            var result = new CodeGenResult
            {
                Success = process.ExitCode == 0,
                Output = outputBuilder.ToString(),
                ErrorMessage = errorBuilder.ToString(),
                ExitCode = process.ExitCode
            };
            sw.Stop();
            _profile?.Record(category, name ?? Path.GetFileNameWithoutExtension(executable), executable, sw.Elapsed, result.Success, result.ExitCode);
            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _profile?.Record(category, name ?? Path.GetFileNameWithoutExtension(executable), executable, sw.Elapsed, success: false);
            return new CodeGenResult
            {
                Success = false,
                ErrorMessage = DiagnosticMessages.FailedToRunProcess(executable, ex.Message)
            };
        }
    }

    private CodeGenResult RunProcessWithResponseFileIfNeeded(
        string executable,
        IReadOnlyList<string> arguments,
        string category,
        string name)
    {
        var commandLine = JoinCommandLine(arguments);
        if (commandLine.Length < 24_000)
        {
            return RunProcess(executable, commandLine, category, name);
        }

        var responsePath = CreateTemporaryPath("link_", ".rsp");
        try
        {
            File.WriteAllLines(responsePath, arguments.Select(QuoteResponseFileArgument));
            return RunProcess(executable, $"@\"{responsePath}\"", category, name);
        }
        finally
        {
            if (File.Exists(responsePath))
            {
                File.Delete(responsePath);
            }
        }
    }

    private static string JoinCommandLine(IEnumerable<string> arguments) =>
        string.Join(' ', arguments.Select(QuoteCommandLineArgument));

    private static string QuoteCommandLineArgument(string argument)
    {
        if (argument.Length == 0)
        {
            return "\"\"";
        }

        return argument.Any(static ch => char.IsWhiteSpace(ch) || ch == '"')
            ? $"\"{argument.Replace("\"", "\\\"")}\""
            : argument;
    }

    private static string QuoteResponseFileArgument(string argument)
    {
        var escaped = argument
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"");
        return escaped.Any(static ch => char.IsWhiteSpace(ch) || ch == '"')
            ? $"\"{escaped}\""
            : escaped;
    }

    private static IReadOnlyList<string> SplitCommandLineFlags(string flags)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < flags.Length; index++)
        {
            var ch = flags[index];
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private string CreateTemporaryPath(string stem, string extension)
    {
        Directory.CreateDirectory(_temporaryDirectory);
        return Path.Combine(
            _temporaryDirectory,
            $"{WellKnownStrings.Mangling.Prefix}{stem}{Guid.NewGuid():N}{extension}");
    }

    private string ComputeObjectCacheKey(
        string schema,
        string content,
        string outputPath,
        IEnumerable<string> extraIdentityParts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashText(hash, schema);
        AppendObjectCacheIdentity(hash, outputPath);
        AppendHashText(hash, content);
        foreach (var part in extraIdentityParts)
        {
            AppendHashText(hash, part);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private string ComputeObjectIdentityCacheKey(
        string schema,
        string outputPath,
        IEnumerable<string> extraIdentityParts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashText(hash, schema);
        AppendObjectCacheIdentity(hash, outputPath);
        foreach (var part in extraIdentityParts)
        {
            AppendHashText(hash, part);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private string ComputeStableObjectIdentityCacheKey(
        string schema,
        IEnumerable<string> extraIdentityParts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashText(hash, schema);
        AppendObjectTargetIdentity(hash);
        foreach (var part in extraIdentityParts)
        {
            AppendHashText(hash, part);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private string ComputeFileObjectCacheKey(
        string schema,
        string sourcePath,
        string outputPath,
        IEnumerable<string> extraIdentityParts)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashText(hash, schema);
        AppendObjectCacheIdentity(hash, outputPath);
        AppendHashText(hash, Path.GetFileName(sourcePath));
        using (var stream = File.OpenRead(sourcePath))
        {
            var buffer = new byte[8192];
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }
        }

        foreach (var part in extraIdentityParts)
        {
            AppendHashText(hash, part);
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private void AppendObjectCacheIdentity(IncrementalHash hash, string outputPath)
    {
        AppendObjectTargetIdentity(hash);
        AppendHashText(hash, Path.GetExtension(outputPath));
    }

    private void AppendObjectTargetIdentity(IncrementalHash hash)
    {
        AppendHashText(hash, _targetInfo.Triple);
        AppendHashText(hash, _targetInfo.ObjectExtension);
        AppendHashText(hash, _targetInfo.Cpu ?? string.Empty);
        AppendHashText(hash, _targetInfo.Features ?? string.Empty);
        AppendHashText(hash, _optimizationLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendHashText(hash, _enableLto.ToString());
        AppendHashText(hash, _linkMode.ToString());
        AppendHashText(hash, string.Join(" ", GetDefaultObjectRelocationFlags(_targetInfo, _linkMode).LlcFlags));
        AppendHashText(hash, string.Join(" ", GetDefaultClangObjectCompileFlags()));
        AppendHashText(hash, _extraCFlags ?? Environment.GetEnvironmentVariable(WellKnownStrings.EnvVars.ExtraCFlags) ?? string.Empty);
        AppendHashText(hash, FindTool("llc") ?? string.Empty);
        AppendHashText(hash, FindTool("clang") ?? string.Empty);
    }

    private CodeGenResult? TryCopyCachedObject(string cacheKey, string outputPath, string category, string name)
    {
        var cachedObjectPath = GetObjectCachePath(cacheKey);
        if (!File.Exists(cachedObjectPath))
        {
            _profile?.Record(category, name, "cache", TimeSpan.Zero, success: true, cacheHit: false);
            return null;
        }

        var sw = Stopwatch.StartNew();
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? Directory.GetCurrentDirectory());
        File.Copy(cachedObjectPath, outputPath, overwrite: true);
        sw.Stop();
        _profile?.Record(category, name, "cache", sw.Elapsed, success: true, cacheHit: true);
        return new CodeGenResult { Success = true, OutputPath = outputPath, CacheHit = true };
    }

    private void StoreObjectCache(string cacheKey, string objectPath, string category, string name)
    {
        if (!File.Exists(objectPath))
        {
            return;
        }

        var sw = Stopwatch.StartNew();
        var cachedObjectPath = GetObjectCachePath(cacheKey);
        var cacheDirectory = Path.GetDirectoryName(cachedObjectPath) ?? GetObjectCacheRoot();
        Directory.CreateDirectory(cacheDirectory);
        var lockPath = Path.Combine(cacheDirectory, $"{cacheKey}.lock");
        using var cacheLock = AcquireRuntimeCacheLock(lockPath);
        if (!File.Exists(cachedObjectPath))
        {
            var tempPath = Path.Combine(cacheDirectory, $"{cacheKey}.tmp-{Guid.NewGuid():N}{Path.GetExtension(cachedObjectPath)}");
            File.Copy(objectPath, tempPath, overwrite: true);
            File.Move(tempPath, cachedObjectPath, overwrite: false);
        }

        sw.Stop();
        _profile?.Record(category, name, "cache", sw.Elapsed, success: true, cacheHit: false);
    }

    private string GetObjectCachePath(string cacheKey)
    {
        return Path.Combine(GetObjectCacheRoot(), $"{cacheKey}{_targetInfo.ObjectExtension}");
    }

    private static string GetObjectCacheRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(Path.GetTempPath(), "Eidosc")
            : Path.Combine(localAppData, "Eidosc");
        return Path.Combine(root, "object-cache");
    }

    public static void ClearObjectCacheForProfile()
    {
        var root = GetObjectCacheRoot();
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private string GetRuntimeLibraryPath()
    {
        var cacheKey = BuildRuntimeResolutionCacheKey("library-path");
        if (TryGetRuntimeResolutionCache(cacheKey, "runtime_library_path_cache", out var cachedPath))
        {
            return cachedPath;
        }

        // 1. 检查实例配置或环境变量 EIDOS_RUNTIME_PATH
        var envPath = GetConfiguredRuntimePath();
        if (!string.IsNullOrEmpty(envPath))
        {
            // 如果是文件路径，直接返回
            if (File.Exists(envPath))
            {
                return StoreRuntimeResolutionCache(cacheKey, "runtime_library_path_cache", envPath);
            }
            // 如果是目录，构建完整路径
            if (Directory.Exists(envPath))
            {
                var envResolved = TryResolveRuntimeLibraryFromDirectory(envPath);
                if (envResolved != null)
                {
                    return StoreRuntimeResolutionCache(cacheKey, "runtime_library_path_cache", envResolved);
                }
            }
        }

        // 2. 检查默认位置
        var baseDir = AppDomain.CurrentDomain.BaseDirectory!;
        foreach (var dir in GetRuntimeDirectoryCandidates(baseDir))
        {
            var resolved = TryResolveRuntimeLibraryFromDirectory(dir);
            if (resolved != null)
            {
                return StoreRuntimeResolutionCache(cacheKey, "runtime_library_path_cache", resolved);
            }
        }

        // 3. 检查相对于可执行文件的路径
        var parentDir = Directory.GetParent(baseDir)?.FullName;
        if (parentDir != null)
        {
            foreach (var dir in GetRuntimeDirectoryCandidates(parentDir))
            {
                var resolved = TryResolveRuntimeLibraryFromDirectory(dir);
                if (resolved != null)
                {
                    return StoreRuntimeResolutionCache(cacheKey, "runtime_library_path_cache", resolved);
                }
            }
        }

        // 4. 递归检查工作目录上的 runtime/Runtime 目录
        var currentDir = Directory.GetCurrentDirectory();
        while (currentDir != null)
        {
            var resolved = TryResolveRuntimeLibraryInTree(currentDir);
            if (resolved != null)
            {
                return StoreRuntimeResolutionCache(cacheKey, "runtime_library_path_cache", resolved);
            }
            currentDir = Directory.GetParent(currentDir)?.FullName;
        }

        // 5. 返回默认路径（即使不存在，让后续代码报错）
        return Path.Combine(baseDir, "runtime", "libeidos_runtime.a");
    }

    private CodeGenResult TryResolveRuntimeLinkInputs(
        out IReadOnlyList<string> runtimeLinkInputPaths,
        List<string> temporaryRuntimeObjectPaths)
    {
        runtimeLinkInputPaths = [];

        var envPath = GetConfiguredRuntimePath();
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            if (File.Exists(envPath))
            {
                runtimeLinkInputPaths = [envPath];
                return new CodeGenResult { Success = true };
            }

            if (Directory.Exists(envPath))
            {
                var envRuntimeLib = TryResolveRuntimeLibraryFromDirectory(envPath);
                if (envRuntimeLib != null)
                {
                    runtimeLinkInputPaths = [envRuntimeLib];
                    return new CodeGenResult { Success = true };
                }

                var envRuntimeSource = TryResolveRuntimeSourceFromDirectory(envPath);
                if (envRuntimeSource != null)
                {
                    var compileResult = CompileRuntimeSourcesToObjects(
                        envRuntimeSource,
                        temporaryRuntimeObjectPaths,
                        out runtimeLinkInputPaths);
                    if (!compileResult.Success)
                    {
                        return new CodeGenResult
                        {
                            Success = false,
                            Output = compileResult.Output,
                            ExitCode = compileResult.ExitCode,
                            ErrorMessage = DiagnosticMessages.FailedToCompileRuntimeFromConfiguredPath(
                                envPath,
                                compileResult.ErrorMessage)
                        };
                    }

                    return new CodeGenResult { Success = true };
                }
            }
            else
            {
                return new CodeGenResult
                {
                    Success = false,
                    ErrorMessage = DiagnosticMessages.ConfiguredRuntimePathMissing(envPath)
                };
            }
        }

        var runtimeLibraryPath = GetRuntimeLibraryPath();
        if (File.Exists(runtimeLibraryPath))
        {
            runtimeLinkInputPaths = [runtimeLibraryPath];
            return new CodeGenResult { Success = true };
        }

        var runtimeSourcePath = TryResolveRuntimeSourcePath();
        if (runtimeSourcePath == null)
        {
            return new CodeGenResult
            {
                Success = false,
                ErrorMessage = DiagnosticMessages.RuntimeLinkInputNotFound(runtimeLibraryPath)
            };
        }

        var cachedRuntime = TryResolveCachedRuntimeLibrary(runtimeSourcePath);
        if (cachedRuntime.Result != null && !cachedRuntime.Result.Success)
        {
            return cachedRuntime.Result;
        }

        if (cachedRuntime.LibraryPath != null)
        {
            runtimeLinkInputPaths = [cachedRuntime.LibraryPath];
            return new CodeGenResult { Success = true };
        }

        var runtimeCompile = CompileRuntimeSourcesToObjects(
            runtimeSourcePath,
            temporaryRuntimeObjectPaths,
            out runtimeLinkInputPaths);
        if (!runtimeCompile.Success)
        {
            return new CodeGenResult
            {
                Success = false,
                Output = runtimeCompile.Output,
                ExitCode = runtimeCompile.ExitCode,
                ErrorMessage = DiagnosticMessages.FailedToCompileRuntimeSource(runtimeSourcePath, runtimeCompile.ErrorMessage)
            };
        }

        return new CodeGenResult { Success = true };
    }

    private (string? LibraryPath, CodeGenResult? Result) TryResolveCachedRuntimeLibrary(string runtimeSourcePath)
    {
        var llvmArPath = FindTool("llvm-ar");
        if (llvmArPath == null)
        {
            return (null, null);
        }

        var runtimeSources = GetRuntimeSources(runtimeSourcePath);
        if (runtimeSources.Length == 0)
        {
            return (null, null);
        }

        var cacheKey = ComputeRuntimeCacheKey(runtimeSources, llvmArPath);
        var cacheRoot = GetRuntimeCacheRoot();
        var cacheDirectory = Path.Combine(cacheRoot, cacheKey);
        var cachedLibraryPath = Path.Combine(cacheDirectory, "libeidos_runtime.a");
        if (File.Exists(cachedLibraryPath))
        {
            _profile?.Record("runtime", "runtime_archive_cache", "cache", TimeSpan.Zero, success: true, cacheHit: true);
            return (cachedLibraryPath, null);
        }

        Directory.CreateDirectory(cacheRoot);
        var lockPath = Path.Combine(cacheRoot, $"{cacheKey}.lock");
        using var cacheLock = AcquireRuntimeCacheLock(lockPath);
        if (File.Exists(cachedLibraryPath))
        {
            _profile?.Record("runtime", "runtime_archive_cache", "cache", TimeSpan.Zero, success: true, cacheHit: true);
            return (cachedLibraryPath, null);
        }

        var cacheSw = Stopwatch.StartNew();

        var buildDirectory = Path.Combine(cacheRoot, $"{cacheKey}.tmp-{Guid.NewGuid():N}");
        Directory.CreateDirectory(buildDirectory);

        try
        {
            var compileResults = CompileRuntimeArchiveSources(runtimeSources, buildDirectory);
            var failedCompile = compileResults.FirstOrDefault(static result => result.Result is { Success: false });
            if (failedCompile.Result is { Success: false } archiveFailure)
            {
                return (null, archiveFailure);
            }

            var objectPaths = compileResults.Select(static result => result.ObjectPath).ToArray();
            foreach (var objectPath in objectPaths)
            {
                if (!File.Exists(objectPath))
                {
                    return (null, new CodeGenResult
                    {
                        Success = false,
                        ErrorMessage = DiagnosticMessages.FailedToCompileRuntimeSource(
                            runtimeSourcePath,
                            $"expected runtime object was not produced: {objectPath}")
                    });
                }
            }

            var tempLibraryPath = Path.Combine(buildDirectory, "libeidos_runtime.a");
            var archiveResult = RunProcess(
                llvmArPath,
                $"rcs \"{tempLibraryPath}\" {string.Join(' ', objectPaths.Select(static path => $"\"{path}\""))}",
                "runtime",
                "archive_runtime");
            if (!archiveResult.Success)
            {
                return (null, archiveResult);
            }

            Directory.CreateDirectory(cacheDirectory);
            if (!File.Exists(cachedLibraryPath))
            {
                File.Move(tempLibraryPath, cachedLibraryPath, overwrite: false);
            }

            cacheSw.Stop();
            _profile?.Record("runtime", "runtime_archive_cache", "cache", cacheSw.Elapsed, success: true, cacheHit: false);
            return (cachedLibraryPath, null);
        }
        catch
        {
            cacheSw.Stop();
            _profile?.Record("runtime", "runtime_archive_cache", "cache", cacheSw.Elapsed, success: false, cacheHit: false);
            throw;
        }
        finally
        {
            if (Directory.Exists(buildDirectory))
            {
                Directory.Delete(buildDirectory, recursive: true);
            }
        }
    }

    private static FileStream AcquireRuntimeCacheLock(string lockPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath) ?? Path.GetTempPath());
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.DeleteOnClose);
            }
            catch (IOException)
            {
                Thread.Sleep(25);
            }
        }
    }

    private CodeGenResult CompileRuntimeSourcesToObjects(
        string runtimeSourcePath,
        List<string> temporaryRuntimeObjectPaths,
        out IReadOnlyList<string> runtimeObjectPaths)
    {
        runtimeObjectPaths = [];

        var runtimeSources = GetRuntimeSources(runtimeSourcePath);

        if (runtimeSources.Length == 0)
        {
            return CompileSingleRuntimeSource(runtimeSourcePath, temporaryRuntimeObjectPaths, out runtimeObjectPaths);
        }

        var compiledObjects = CompileRuntimeTempSources(runtimeSources);
        temporaryRuntimeObjectPaths.AddRange(compiledObjects.Select(static result => result.ObjectPath));
        var failed = compiledObjects.FirstOrDefault(static result => result.Result is { Success: false });
        if (failed.Result is { Success: false } compileFailure)
        {
            runtimeObjectPaths = compiledObjects
                .Where(static result => result.Result.Success)
                .Select(static result => result.ObjectPath)
                .ToArray();
            return compileFailure;
        }

        runtimeObjectPaths = compiledObjects.Select(static result => result.ObjectPath).ToArray();
        return new CodeGenResult { Success = true };
    }

    private IReadOnlyList<NativeCompileResult> CompileRuntimeArchiveSources(
        IReadOnlyList<string> runtimeSources,
        string buildDirectory)
    {
        var results = new NativeCompileResult[runtimeSources.Count];
        Parallel.For(
            0,
            runtimeSources.Count,
            CreateBoundedObjectCompileParallelOptions(),
            index =>
            {
                var source = runtimeSources[index];
                var objectPath = Path.Combine(
                    buildDirectory,
                    $"{Path.GetFileNameWithoutExtension(source)}{_targetInfo.ObjectExtension}");
                results[index] = new NativeCompileResult(
                    objectPath,
                    CompileRuntimeSourceToObject(source, objectPath));
            });

        return results;
    }

    private IReadOnlyList<NativeCompileResult> CompileRuntimeTempSources(IReadOnlyList<string> runtimeSources)
    {
        var results = new NativeCompileResult[runtimeSources.Count];
        Parallel.For(
            0,
            runtimeSources.Count,
            CreateBoundedObjectCompileParallelOptions(),
            index =>
            {
                var runtimeObjPath = CreateTemporaryPath("runtime_", _targetInfo.ObjectExtension);
                results[index] = new NativeCompileResult(
                    runtimeObjPath,
                    CompileRuntimeSourceToObject(runtimeSources[index], runtimeObjPath));
            });
        return results;
    }

    private ParallelOptions CreateBoundedObjectCompileParallelOptions() =>
        new() { MaxDegreeOfParallelism = _maxDegreeOfParallelism };

    private static string ShortenCacheKey(string cacheKey) =>
        cacheKey.Length <= 16
            ? cacheKey
            : cacheKey[..16];

    private string[] GetRuntimeSources(string runtimeSourcePath)
    {
        var cacheKey = Path.GetFullPath(runtimeSourcePath);
        lock (RuntimeSourceCacheLock)
        {
            if (RuntimeSourceListCache.TryGetValue(cacheKey, out var cachedSources))
            {
                _profile?.Record("runtime", "runtime_source_list_cache", "cache", TimeSpan.Zero, success: true, cacheHit: true);
                return cachedSources;
            }
        }

        var sw = Stopwatch.StartNew();
        var runtimeDirectory = Path.GetDirectoryName(runtimeSourcePath);
        if (string.IsNullOrWhiteSpace(runtimeDirectory) || !Directory.Exists(runtimeDirectory))
        {
            return StoreRuntimeSources(cacheKey, sw, File.Exists(runtimeSourcePath) ? [runtimeSourcePath] : []);
        }

        var sources = Directory.EnumerateFiles(runtimeDirectory, "*.c", SearchOption.TopDirectoryOnly)
            .OrderBy(static path => string.Equals(
                    Path.GetFileName(path),
                    WellKnownStrings.SpecialNames.MemoryRuntimeFile,
                    StringComparison.OrdinalIgnoreCase)
                ? 0
                : 1)
            .ThenBy(static path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return StoreRuntimeSources(cacheKey, sw, sources);
    }

    private string[] StoreRuntimeSources(string cacheKey, Stopwatch sw, string[] sources)
    {
        lock (RuntimeSourceCacheLock)
        {
            RuntimeSourceListCache[cacheKey] = sources;
        }

        sw.Stop();
        _profile?.Record("runtime", "runtime_source_list_cache", "cache", sw.Elapsed, success: true, cacheHit: false);
        return sources;
    }

    private string ComputeRuntimeCacheKey(IReadOnlyList<string> runtimeSources, string llvmArPath)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashText(hash, "eidos-runtime-cache-v1");
        AppendHashText(hash, _targetInfo.Triple);
        AppendHashText(hash, _targetInfo.ObjectExtension);
        AppendHashText(hash, _optimizationLevel.ToString(System.Globalization.CultureInfo.InvariantCulture));
        AppendHashText(hash, _linkMode.ToString());
        AppendHashText(hash, string.Join(" ", GetDefaultClangObjectCompileFlags()));
        AppendHashText(hash, _extraCFlags ?? Environment.GetEnvironmentVariable(WellKnownStrings.EnvVars.ExtraCFlags) ?? string.Empty);
        AppendHashText(hash, FindTool("clang") ?? string.Empty);
        AppendHashText(hash, llvmArPath);

        foreach (var source in runtimeSources)
        {
            AppendHashText(hash, Path.GetFileName(source));
            using var stream = File.OpenRead(source);
            var buffer = new byte[8192];
            while (true)
            {
                var read = stream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
            }
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static void AppendHashText(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        hash.AppendData(bytes);
        hash.AppendData([0]);
    }

    private static string GetRuntimeCacheRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = string.IsNullOrWhiteSpace(localAppData)
            ? Path.Combine(Path.GetTempPath(), "Eidosc")
            : Path.Combine(localAppData, "Eidosc");
        return Path.Combine(root, "runtime-cache");
    }

    private CodeGenResult CompileSingleRuntimeSource(
        string runtimeSourcePath,
        List<string> temporaryRuntimeObjectPaths,
        out IReadOnlyList<string> runtimeObjectPaths)
    {
        var runtimeObjPath = CreateTemporaryPath("runtime_", _targetInfo.ObjectExtension);
        temporaryRuntimeObjectPaths.Add(runtimeObjPath);
        var compileResult = CompileRuntimeSourceToObject(runtimeSourcePath, runtimeObjPath);
        if (!compileResult.Success)
        {
            runtimeObjectPaths = [];
            return compileResult;
        }

        runtimeObjectPaths = [runtimeObjPath];
        return new CodeGenResult { Success = true };
    }

    private CodeGenResult CompileRuntimeSourceToObject(string runtimeSourcePath, string outputObjectPath)
    {
        var clangPath = FindTool("clang");
        if (clangPath == null)
        {
            return new CodeGenResult
            {
                Success = false,
                ErrorMessage = DiagnosticMessages.ClangNotFoundForRuntime
            };
        }

        var extraCFlags = _extraCFlags ?? Environment.GetEnvironmentVariable(WellKnownStrings.EnvVars.ExtraCFlags);
        var arguments = new StringBuilder()
            .Append($"-target {_targetInfo.Triple} -O{_optimizationLevel} -c ");
        foreach (var flag in GetDefaultClangObjectCompileFlags())
        {
            arguments.Append(flag).Append(' ');
        }
        if (!string.IsNullOrWhiteSpace(extraCFlags))
        {
            arguments.Append(extraCFlags).Append(' ');
        }

        arguments.Append($"\"{runtimeSourcePath}\" -o \"{outputObjectPath}\"");
        return RunProcess(clangPath, arguments.ToString(), "runtime", $"compile_runtime_source.{Path.GetFileName(runtimeSourcePath)}");
    }

    private string GetRuntimeLibraryDir()
    {
        var cacheKey = BuildRuntimeResolutionCacheKey("library-dir");
        if (TryGetRuntimeResolutionCache(cacheKey, "runtime_library_dir_cache", out var cachedDir))
        {
            return cachedDir;
        }

        // 1. 检查实例配置或环境变量 EIDOS_RUNTIME_PATH
        var envPath = GetConfiguredRuntimePath();
        if (!string.IsNullOrEmpty(envPath))
        {
            // 如果是目录，直接返回
            if (Directory.Exists(envPath))
            {
                return StoreRuntimeResolutionCache(cacheKey, "runtime_library_dir_cache", envPath);
            }
            // 如果是文件路径，返回其目录
            if (File.Exists(envPath))
            {
                return StoreRuntimeResolutionCache(cacheKey, "runtime_library_dir_cache", Path.GetDirectoryName(envPath) ?? envPath);
            }
        }

        // 2. 优先返回可用运行时目录
        var baseDir = AppDomain.CurrentDomain.BaseDirectory!;
        foreach (var dir in GetRuntimeDirectoryCandidates(baseDir))
        {
            if (Directory.Exists(dir))
            {
                return StoreRuntimeResolutionCache(cacheKey, "runtime_library_dir_cache", dir);
            }
        }

        var currentDir = Directory.GetCurrentDirectory();
        while (currentDir != null)
        {
            foreach (var dir in GetRuntimeDirectoryCandidates(currentDir))
            {
                if (Directory.Exists(dir))
                {
                    return StoreRuntimeResolutionCache(cacheKey, "runtime_library_dir_cache", dir);
                }
            }
            currentDir = Directory.GetParent(currentDir)?.FullName;
        }

        return Path.Combine(baseDir, "runtime");
    }

    private string? GetConfiguredRuntimePath() =>
        !string.IsNullOrWhiteSpace(_runtimePath)
            ? _runtimePath
            : Environment.GetEnvironmentVariable(WellKnownStrings.EnvVars.RuntimePath);

    private static IEnumerable<string> GetRuntimeDirectoryCandidates(string root)
    {
        yield return Path.Combine(root, "runtime");
        yield return Path.Combine(root, "Runtime");
        // Solution root → Eidosc/src/Eidosc/Runtime/
        yield return Path.Combine(root, "src", "Eidosc", "Runtime");
        // Workspace root → Eidosc/src/Eidosc/Runtime/ (nested repo layout)
        yield return Path.Combine(root, "Eidosc", "src", "Eidosc", "Runtime");
    }

    private static string? TryResolveRuntimeLibraryInTree(string root)
    {
        foreach (var dir in GetRuntimeDirectoryCandidates(root))
        {
            var resolved = TryResolveRuntimeLibraryFromDirectory(dir);
            if (resolved != null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? TryResolveRuntimeLibraryFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var staticLibrary = Path.Combine(directory, "libeidos_runtime.a");
        if (File.Exists(staticLibrary))
        {
            return staticLibrary;
        }

        return null;
    }

    private string? TryResolveRuntimeSourcePath()
    {
        var cacheKey = BuildRuntimeResolutionCacheKey("source-path");
        if (TryGetRuntimeResolutionCache(cacheKey, "runtime_source_path_cache", out var cachedPath))
        {
            return cachedPath;
        }

        var baseDir = AppDomain.CurrentDomain.BaseDirectory!;
        foreach (var dir in GetRuntimeDirectoryCandidates(baseDir))
        {
            var source = TryResolveRuntimeSourceFromDirectory(dir);
            if (source != null)
            {
                return StoreRuntimeResolutionCache(cacheKey, "runtime_source_path_cache", source);
            }
        }

        var parentDir = Directory.GetParent(baseDir)?.FullName;
        if (parentDir != null)
        {
            foreach (var dir in GetRuntimeDirectoryCandidates(parentDir))
            {
                var source = TryResolveRuntimeSourceFromDirectory(dir);
                if (source != null)
                {
                    return StoreRuntimeResolutionCache(cacheKey, "runtime_source_path_cache", source);
                }
            }
        }

        var currentDir = Directory.GetCurrentDirectory();
        while (currentDir != null)
        {
            foreach (var dir in GetRuntimeDirectoryCandidates(currentDir))
            {
                var source = TryResolveRuntimeSourceFromDirectory(dir);
                if (source != null)
                {
                    return StoreRuntimeResolutionCache(cacheKey, "runtime_source_path_cache", source);
                }
            }

            currentDir = Directory.GetParent(currentDir)?.FullName;
        }

        return null;
    }

    private string BuildRuntimeResolutionCacheKey(string kind)
    {
        return string.Join(
            '|',
            kind,
            _runtimePath ?? Environment.GetEnvironmentVariable(WellKnownStrings.EnvVars.RuntimePath) ?? "",
            AppDomain.CurrentDomain.BaseDirectory ?? "",
            Directory.GetCurrentDirectory());
    }

    private bool TryGetRuntimeResolutionCache(string key, string name, out string path)
    {
        lock (RuntimeResolutionCacheLock)
        {
            if (RuntimeResolutionCache.TryGetValue(key, out path!))
            {
                _profile?.Record("runtime", name, "cache", TimeSpan.Zero, success: true, cacheHit: true);
                return true;
            }
        }

        path = "";
        return false;
    }

    private string StoreRuntimeResolutionCache(string key, string name, string path)
    {
        lock (RuntimeResolutionCacheLock)
        {
            RuntimeResolutionCache[key] = path;
        }

        _profile?.Record("runtime", name, "cache", TimeSpan.Zero, success: true, cacheHit: false);
        return path;
    }

    private static string? TryResolveRuntimeSourceFromDirectory(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return null;
        }

        var runtimeSource = Path.Combine(directory, WellKnownStrings.SpecialNames.MemoryRuntimeFile);
        if (File.Exists(runtimeSource))
        {
            return runtimeSource;
        }

        return null;
    }
}

/// <summary>
/// 代码生成结果
/// </summary>
public sealed class CodeGenResult
{
    /// <summary>
    /// 是否成功
    /// </summary>
    public bool Success { get; init; }

    /// <summary>
    /// 标准输出
    /// </summary>
    public string? Output { get; init; }

    /// <summary>
    /// 错误信息
    /// </summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// 退出代码
    /// </summary>
    public int ExitCode { get; init; }

    /// <summary>
    /// 输出文件路径（如果成功）
    /// </summary>
    public string? OutputPath { get; init; }

    public bool CacheHit { get; init; }
}
