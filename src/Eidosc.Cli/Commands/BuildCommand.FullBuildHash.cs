using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;
using Eidosc.Utils;
using System.Security.Cryptography;
using System.Text;

namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    internal static string ComputeFullBuildInputHash(
        ProjectCommandSourceInput sourceInput,
        ProjectCommandInputResolution inputResolution) =>
        ComputeFullBuildInputHash(sourceInput, inputResolution, out _, maxDegreeOfParallelism: 0);

    internal static string ComputeFullBuildInputHash(
        ProjectCommandSourceInput sourceInput,
        ProjectCommandInputResolution inputResolution,
        out FullBuildInputHashStats stats,
        int maxDegreeOfParallelism = 0)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHashPart(hash, "entry");
        AppendHashPart(hash, NormalizeHashPath(sourceInput.SourceFilePath));
        AppendHashPart(hash, sourceInput.SourceText);
        var sourceBytes = sourceInput.SourceText.Length;

        var projectPart = ReadProjectHashPart(inputResolution.ImportResolution.ProjectFilePath);
        if (projectPart != null)
        {
            AppendHashPart(hash, "project");
            AppendHashPart(hash, projectPart.NormalizedPath);
            AppendHashPart(hash, projectPart.Content);
            sourceBytes += projectPart.Content.Length;
        }

        var sourceParts = ReadSourceHashParts(
            sourceInput,
            inputResolution,
            out var sourceRootCount,
            maxDegreeOfParallelism);
        foreach (var sourcePart in sourceParts)
        {
            AppendHashPart(hash, "source");
            AppendHashPart(hash, sourcePart.NormalizedPath);
            AppendHashPart(hash, sourcePart.Content);
            sourceBytes += sourcePart.Content.Length;
        }

        stats = new FullBuildInputHashStats(
            sourceParts.Count + 1,
            sourceRootCount,
            sourceBytes,
            sourceParts.Count);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static FullBuildInputHashPart? ReadProjectHashPart(string? projectFilePath)
    {
        if (string.IsNullOrWhiteSpace(projectFilePath) || !File.Exists(projectFilePath))
        {
            return null;
        }

        return new FullBuildInputHashPart(
            Path.GetFullPath(projectFilePath),
            NormalizeHashPath(projectFilePath),
            File.ReadAllText(projectFilePath));
    }

    private static IReadOnlyList<FullBuildInputHashPart> ReadSourceHashParts(
        ProjectCommandSourceInput sourceInput,
        ProjectCommandInputResolution inputResolution,
        out int sourceRootCount,
        int maxDegreeOfParallelism)
    {
        var entryPath = Path.GetFullPath(sourceInput.SourceFilePath);
        var sourceRoots = EnumerateFullBuildInputRoots(inputResolution)
            .Where(Directory.Exists)
            .ToArray();
        sourceRootCount = sourceRoots.Length;
        var files = sourceRoots
            .SelectMany(static root => Directory.EnumerateFiles(root, "*.eidos", SearchOption.AllDirectories))
            .Select(Path.GetFullPath)
            .Where(path => !string.Equals(path, entryPath, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FullBuildInputHashFile(path, NormalizeHashPath(path)))
            .OrderBy(static file => file.NormalizedPath, StringComparer.Ordinal)
            .ToArray();
        if (files.Length == 0)
        {
            return [];
        }

        var parts = new FullBuildInputHashPart[files.Length];
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(
                ResolveMaxDegreeOfParallelism(maxDegreeOfParallelism),
                Math.Max(1, files.Length))
        };
        Parallel.ForEach(
            Enumerable.Range(0, files.Length),
            parallelOptions,
            index =>
            {
                var file = files[index];
                parts[index] = new FullBuildInputHashPart(
                    file.FullPath,
                    file.NormalizedPath,
                    File.ReadAllText(file.FullPath));
            });

        return parts;
    }

    private static IEnumerable<string> EnumerateFullBuildInputRoots(ProjectCommandInputResolution inputResolution)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                             inputResolution.ImportResolution.EffectiveSearchRoots)
        {
            if (!string.IsNullOrWhiteSpace(root) && seen.Add(Path.GetFullPath(root)))
            {
                yield return Path.GetFullPath(root);
            }
        }

        foreach (var roots in inputResolution.ProjectTarget?.PackageImportRoots.Values ?? Enumerable.Empty<string[]>())
        {
            foreach (var root in roots)
            {
                if (!string.IsNullOrWhiteSpace(root) && seen.Add(Path.GetFullPath(root)))
                {
                    yield return Path.GetFullPath(root);
                }
            }
        }
    }

    private static int ResolveMaxDegreeOfParallelism(int requested) =>
        requested > 0 ? requested : Math.Max(1, Environment.ProcessorCount);

    private sealed record FullBuildInputHashFile(
        string FullPath,
        string NormalizedPath);

    private sealed record FullBuildInputHashPart(
        string FullPath,
        string NormalizedPath,
        string Content);

    internal readonly record struct FullBuildInputHashStats(
        int SourceFileCount,
        int SourceRootCount,
        int SourceBytes,
        int ParallelReadCount);

    internal static IEnumerable<string> CreateFullBuildArtifactFlags(
        ProjectCommandInputResolution inputResolution,
        CompilationOptions compileOptions,
        BuildOptions options,
        int optimizationLevel,
        TargetInfo? targetInfo,
        string outputPath,
        bool includeOutputPath = true)
    {
        yield return $"target={options.Target}";
        yield return $"compilerBuild={CompilerBuildIdentity.Current}";
        yield return $"cliBuild={typeof(BuildCommand).Assembly.ManifestModule.ModuleVersionId:N}";
        yield return $"targetName={inputResolution.ProjectTarget?.TargetName ?? ""}";
        yield return $"entry={NormalizeHashPath(inputResolution.SourceFilePath)}";
        if (includeOutputPath)
        {
            yield return $"output={NormalizeHashPath(outputPath)}";
        }

        yield return $"stop={compileOptions.StopAtPhase?.ToString() ?? ""}";
        yield return $"noImplicitPrelude={compileOptions.NoImplicitPrelude}";
        yield return $"mirOpt={compileOptions.EnableMirOptimizations}";
        yield return $"optimizationLevel={optimizationLevel}";
        yield return $"lto={options.Lto}";
        yield return $"nativeCpu={options.NativeCpu}";
        yield return $"targetTriple={targetInfo?.Triple ?? compileOptions.LlvmTargetTriple ?? TargetInfo.Default.Triple}";
        yield return $"nativeLinkMode={compileOptions.NativeLinkMode}";
        yield return $"codegenMode={ResolveNativeCodegenMode(options.BuildMode, options.CodegenMode)}";
        yield return $"maxObjectGroups={ResolveMaxObjectGroups(options.BuildMode, options.CodegenMode, options.MaxObjectGroups)}";
        yield return $"stdlib={PrecompiledModuleRegistry.GetStdlibImageFingerprint()}";
        foreach (var runtimePart in CreateRuntimeBuildArtifactFlags())
        {
            yield return runtimePart;
        }

        foreach (var value in compileOptions.ConfigFfiLibraries)
        {
            yield return $"ffi.lib={value}";
        }

        foreach (var value in compileOptions.ConfigFfiLibraryPaths)
        {
            yield return $"ffi.libPath={NormalizeHashPath(value)}";
        }

        foreach (var value in compileOptions.ConfigFfiIncludePaths)
        {
            yield return $"ffi.include={NormalizeHashPath(value)}";
        }

        foreach (var value in compileOptions.ConfigFfiNativeSources)
        {
            yield return $"ffi.nativeSource={NormalizeHashPath(value)}";
            if (File.Exists(value))
            {
                yield return $"ffi.nativeSourceHash={NormalizeHashPath(value)}:{ModuleArtifactHash.ComputeSourceHash(File.ReadAllText(value))}";
            }
        }

        foreach (var value in compileOptions.ConfigFfiLinkerFlags)
        {
            yield return $"ffi.linkerFlag={value}";
        }
    }

    private static IEnumerable<string> CreateRuntimeBuildArtifactFlags()
    {
        yield return $"{WellKnownStrings.EnvVars.RuntimePath}={Environment.GetEnvironmentVariable(WellKnownStrings.EnvVars.RuntimePath) ?? ""}";
        yield return $"{WellKnownStrings.EnvVars.ExtraCFlags}={Environment.GetEnvironmentVariable(WellKnownStrings.EnvVars.ExtraCFlags) ?? ""}";
        yield return $"{WellKnownStrings.EnvVars.ExtraLdFlags}={Environment.GetEnvironmentVariable(WellKnownStrings.EnvVars.ExtraLdFlags) ?? ""}";

        var candidateRoots = new[]
            {
                Directory.GetCurrentDirectory(),
                AppContext.BaseDirectory,
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."))
            }
            .SelectMany(static root => new[]
            {
                Path.Combine(root, "runtime"),
                Path.Combine(root, "Runtime"),
                Path.Combine(root, "src", "Eidosc", "Runtime"),
                Path.Combine(root, "Eidosc", "src", "Eidosc", "Runtime")
            })
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var root in candidateRoots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            yield return $"runtime.root={NormalizeHashPath(root)}";
            foreach (var file in Directory.EnumerateFiles(root, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(static path =>
                             string.Equals(Path.GetExtension(path), ".c", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(Path.GetExtension(path), ".h", StringComparison.OrdinalIgnoreCase) ||
                             string.Equals(Path.GetFileName(path), "libeidos_runtime.a", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(NormalizeHashPath, StringComparer.Ordinal))
            {
                yield return $"runtime.file={NormalizeHashPath(file)}:{ComputeFileHash(file)}";
            }
        }
    }

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string CreateFullBuildModuleKey(ProjectCommandInputResolution inputResolution, CompileTarget target)
    {
        var projectPart = inputResolution.ProjectTarget != null
            ? $"{NormalizeHashPath(inputResolution.ProjectTarget.ProjectDirectory)}::{inputResolution.ProjectTarget.TargetName}"
            : NormalizeHashPath(inputResolution.SourceFilePath);
        return $"{target.ToString().ToLowerInvariant()}::{projectPart}";
    }

    private static void AppendHashPart(IncrementalHash hash, string value)
    {
        hash.AppendData(Encoding.UTF8.GetBytes(value));
        hash.AppendData([0]);
    }

    private static string NormalizeHashPath(string path)
    {
        return Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
    }
}
