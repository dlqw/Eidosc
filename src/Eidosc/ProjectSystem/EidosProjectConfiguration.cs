using Eidosc.Pipeline;
using System.Runtime.InteropServices;
using Tomlyn;

namespace Eidosc.ProjectSystem;

public sealed record EidosProjectTargetConfiguration
{
    public string Name { get; init; } = "";
    public string Entry { get; init; } = "";
    public string? Kind { get; init; }
    public string[] Dependencies { get; init; } = [];
    public string[] ProjectDependencies { get; init; } = [];
}

public sealed record EidosProjectDependencyConfiguration
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string? Target { get; init; }
}

public sealed record EidosFfiConfiguration
{
    public string[] Libraries { get; init; } = [];
    public string[] LibraryPaths { get; init; } = [];
    public string[] IncludePaths { get; init; } = [];
    public string[] NativeSources { get; init; } = [];
    public string[] LinkerFlags { get; init; } = [];
    public Dictionary<string, string[]>? Platform { get; init; }
}

public sealed record EidosBuildToolConfiguration
{
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
}

public sealed record EidosBuildConfiguration
{
    public string Program { get; init; } = "";
    public string[] FileInputs { get; init; } = [];
    public string[] Environment { get; init; } = [];
    public string[] OutputRoots { get; init; } = [];
    public EidosBuildToolConfiguration[] Tools { get; init; } = [];
}

public sealed record EidosProjectConfiguration
{
    public int ManifestSchema { get; init; } = 3;
    public string LanguageVersion { get; init; } = EidosLanguageVersions.DefaultForExistingProjects;
    public PackageMetadata? Package { get; init; }
    public string[] SourceRoots { get; init; } = [];
    public string[] ImportRoots { get; init; } = [];
    public string? DefaultTarget { get; init; }
    public string? NativeLinkMode { get; init; }
    public EidosProjectTargetConfiguration[] Targets { get; init; } = [];
    public EidosProjectDependencyConfiguration[] Dependencies { get; init; } = [];
    public Dictionary<string, DependencySpec>? VersionedDependencies { get; init; }
    public bool NoImplicitStdlib { get; init; }
    public EidosBuildConfiguration? Build { get; init; }
    public EidosFfiConfiguration? Ffi { get; init; }
}

public sealed record LoadedEidosProjectConfiguration(
    string FilePath,
    string ProjectDirectory,
    EidosProjectConfiguration Configuration);

public sealed record ProjectImportSearchResolution(
    string[] SourceSearchRoots,
    string[] ImportSearchRoots,
    string[] EffectiveSearchRoots,
    string? ProjectFilePath,
    bool UsesExplicitImportRoots);

public static class EidosProjectConfigurationLoader
{
    public const string DefaultFileName = "eidos.toml";

    public static ProjectImportSearchResolution ResolveImportSearchRoots(
        string inputFilePath,
        IReadOnlyList<string>? explicitImportRoots = null)
    {
        var normalizedExplicitRoots = NormalizeImportRoots(
            explicitImportRoots,
            Directory.GetCurrentDirectory());

        if (normalizedExplicitRoots.Length > 0)
        {
            LoadedEidosProjectConfiguration? projectConfiguration = null;
            try
            {
                projectConfiguration = TryLoadNearest(inputFilePath);
            }
            catch (InvalidOperationException)
            {
                // 显式 import roots 允许绕过损坏的项目配置。
            }

            var sourceRoots = projectConfiguration?.Configuration.SourceRoots ?? [];
            return new ProjectImportSearchResolution(
                sourceRoots,
                normalizedExplicitRoots,
                CombineSearchRoots(sourceRoots, normalizedExplicitRoots),
                projectConfiguration?.FilePath,
                UsesExplicitImportRoots: true);
        }

        var discoveredProjectConfiguration = TryLoadNearest(inputFilePath);
        var sourceSearchRoots = discoveredProjectConfiguration?.Configuration.SourceRoots ?? [];
        var importSearchRoots = discoveredProjectConfiguration?.Configuration.ImportRoots ?? [];

        return new ProjectImportSearchResolution(
            sourceSearchRoots,
            importSearchRoots,
            CombineSearchRoots(sourceSearchRoots, importSearchRoots),
            discoveredProjectConfiguration?.FilePath,
            UsesExplicitImportRoots: false);
    }

    public static LoadedEidosProjectConfiguration LoadFromPath(string inputPath)
    {
        var normalizedProjectFilePath = NormalizeProjectFilePath(inputPath, Directory.GetCurrentDirectory());
        if (!File.Exists(normalizedProjectFilePath))
        {
            throw new InvalidOperationException(PipelineMessages.ProjectConfigNotFound(normalizedProjectFilePath));
        }

        return LoadFromFile(normalizedProjectFilePath);
    }

    public static LoadedEidosProjectConfiguration? TryLoadFromPath(string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return null;
        }

        string normalizedProjectFilePath;
        try
        {
            normalizedProjectFilePath = NormalizeProjectFilePath(inputPath, Directory.GetCurrentDirectory());
        }
        catch
        {
            return null;
        }

        if (!File.Exists(normalizedProjectFilePath))
        {
            return null;
        }

        return LoadFromFile(normalizedProjectFilePath);
    }

    public static LoadedEidosProjectConfiguration? TryLoadNearest(string inputFilePath)
    {
        var current = TryGetSearchStartDirectory(inputFilePath);
        while (!string.IsNullOrWhiteSpace(current))
        {
            var candidate = Path.Combine(current, DefaultFileName);
            if (File.Exists(candidate))
            {
                return LoadFromFile(candidate);
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return null;
    }

    private static LoadedEidosProjectConfiguration LoadFromFile(string filePath)
    {
        try
        {
            var normalizedPath = Path.GetFullPath(filePath);
            var configDocument = EidosProjectManifestDocument.Load(normalizedPath);
            var manifestSchema = configDocument.ManifestSchema ?? 3;
            if (manifestSchema != 3)
            {
                throw new InvalidOperationException(
                    $"Unsupported manifest schema '{manifestSchema}'. Run 'eidosc migrate manifest' before building.");
            }

            var baseDirectory = Path.GetDirectoryName(normalizedPath) ?? Directory.GetCurrentDirectory();
            var manifestSourceRoots = configDocument.SourceRoots is { Length: > 0 }
                ? configDocument.SourceRoots
                : ["src"];
            var sourceRoots = NormalizeImportRoots(manifestSourceRoots, baseDirectory);
            var importRoots = NormalizeImportRoots(configDocument.ImportRoots, baseDirectory);

            var (legacyDeps, versionedDeps) = ResolveDependencies(configDocument, baseDirectory);

            PackageMetadata? package = null;
            if (configDocument.Package != null)
            {
                if (string.IsNullOrWhiteSpace(configDocument.Package.Version) ||
                    !SemanticVersion.TryParse(configDocument.Package.Version, out var ver) ||
                    ver == null)
                {
                    throw new InvalidOperationException(
                        $"Package '{configDocument.Package.Name ?? "<unnamed>"}' must declare a valid SemVer 2.0.0 version.");
                }

                package = new PackageMetadata
                {
                    Name = configDocument.Package.Name ?? "",
                    Version = ver,
                    Description = configDocument.Package.Description,
                    Authors = configDocument.Package.Authors?.ToList() ?? [],
                    License = configDocument.Package.License,
                    Keywords = configDocument.Package.Keywords?.ToList() ?? []
                };
            }

            EidosFfiConfiguration? ffiConfig = null;
            if (configDocument.Ffi != null)
            {
                var platformLibs = ResolvePlatformLibraries(configDocument.Ffi.Platform);
                var allLibs = (configDocument.Ffi.Libraries ?? []).Concat(platformLibs).Distinct().ToArray();
                ffiConfig = new EidosFfiConfiguration
                {
                    Libraries = allLibs,
                    LibraryPaths = NormalizeImportRoots(configDocument.Ffi.LibraryPaths, baseDirectory),
                    IncludePaths = NormalizeImportRoots(configDocument.Ffi.IncludePaths, baseDirectory),
                    NativeSources = NormalizeFileLikePaths(configDocument.Ffi.NativeSources, baseDirectory),
                    LinkerFlags = NormalizeReferenceNames(configDocument.Ffi.LinkerFlags),
                    Platform = configDocument.Ffi.Platform
                };
            }

            var buildConfig = ResolveBuildConfiguration(configDocument.Build, baseDirectory);

            var targets = ResolveTargets(configDocument.Targets, baseDirectory);
            var defaultTarget = NormalizeOptionalValue(configDocument.DefaultTarget)
                ?? (targets.Length == 1 ? targets[0].Name : null);

            return new LoadedEidosProjectConfiguration(
                normalizedPath,
                baseDirectory,
                new EidosProjectConfiguration
                {
                    ManifestSchema = manifestSchema,
                    LanguageVersion = EidosLanguageVersions.Normalize(
                        configDocument.Language?.Version,
                        EidosLanguageVersions.DefaultForExistingProjects),
                    Package = package,
                    SourceRoots = sourceRoots,
                    ImportRoots = importRoots,
                    DefaultTarget = defaultTarget,
                    NativeLinkMode = NormalizeOptionalValue(configDocument.NativeLinkMode),
                    Targets = targets,
                    Dependencies = legacyDeps,
                    VersionedDependencies = versionedDeps,
                    NoImplicitStdlib = configDocument.NoImplicitStdlib ?? false,
                    Build = buildConfig,
                    Ffi = ffiConfig
                });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TomlException)
        {
            throw new InvalidOperationException(
                PipelineMessages.FailedToLoadProjectConfig(filePath, ex.Message),
                ex);
        }
    }

    private static EidosBuildConfiguration? ResolveBuildConfiguration(
        EidosProjectBuildManifestDocument? build,
        string projectDirectory)
    {
        if (build == null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(build.Program))
        {
            throw new InvalidOperationException("[build].program must name an Eidos build program.");
        }

        var program = ResolveContainedBuildPath(build.Program, projectDirectory, "build program");
        if (!string.Equals(Path.GetExtension(program), ".eidos", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("[build].program must reference a .eidos file.");
        }

        var fileInputs = ResolveDistinctContainedBuildPaths(
            build.FileInputs,
            projectDirectory,
            "build file input");
        var outputRoots = ResolveDistinctContainedBuildPaths(
            build.OutputRoots is { Length: > 0 } ? build.OutputRoots : ["build"],
            projectDirectory,
            "build output root");

        foreach (var outputRoot in outputRoots)
        {
            if (PathsEqual(outputRoot, projectDirectory))
            {
                throw new InvalidOperationException("A build output root cannot be the project root.");
            }

            if (PathsOverlap(program, outputRoot))
            {
                throw new InvalidOperationException(
                    "The build program and build output roots must be disjoint.");
            }

            if (fileInputs.Any(input => PathsOverlap(input, outputRoot)))
            {
                throw new InvalidOperationException(
                    "Build file inputs and build output roots must be disjoint.");
            }
        }

        var environment = NormalizeBuildEnvironment(build.Environment);
        var tools = NormalizeBuildTools(build.Tools, projectDirectory);
        return new EidosBuildConfiguration
        {
            Program = program,
            FileInputs = fileInputs,
            Environment = environment,
            OutputRoots = outputRoots,
            Tools = tools
        };
    }

    private static string[] ResolveDistinctContainedBuildPaths(
        IReadOnlyList<string>? paths,
        string projectDirectory,
        string description)
    {
        if (paths == null || paths.Count == 0)
        {
            return [];
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var result = new List<string>(paths.Count);
        var seen = new HashSet<string>(comparer);
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new InvalidOperationException($"A declared {description} cannot be empty.");
            }

            var resolved = ResolveContainedBuildPath(path, projectDirectory, description);
            if (!seen.Add(resolved))
            {
                throw new InvalidOperationException($"Duplicate {description} '{path}'.");
            }

            result.Add(resolved);
        }

        return result.ToArray();
    }

    private static string ResolveContainedBuildPath(
        string path,
        string projectDirectory,
        string description)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(path, projectDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new InvalidOperationException($"Invalid {description} path '{path}': {ex.Message}", ex);
        }

        var relative = Path.GetRelativePath(projectDirectory, fullPath);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Declared {description} '{path}' escapes the project root '{projectDirectory}'.");
        }

        return fullPath;
    }

    private static string[] NormalizeBuildEnvironment(IReadOnlyList<string>? names)
    {
        if (names == null || names.Count == 0)
        {
            return [];
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var seen = new HashSet<string>(comparer);
        var result = new List<string>(names.Count);
        foreach (var rawName in names)
        {
            var name = rawName?.Trim() ?? string.Empty;
            if (!IsValidEnvironmentName(name))
            {
                throw new InvalidOperationException($"Invalid build environment variable name '{rawName}'.");
            }

            if (!seen.Add(name))
            {
                throw new InvalidOperationException($"Duplicate build environment variable '{name}'.");
            }

            result.Add(name);
        }

        return result.ToArray();
    }

    private static EidosBuildToolConfiguration[] NormalizeBuildTools(
        IReadOnlyList<EidosProjectBuildToolManifestDocument>? tools,
        string projectDirectory)
    {
        if (tools == null || tools.Count == 0)
        {
            return [];
        }

        var result = new List<EidosBuildToolConfiguration>(tools.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            var name = tool.Name?.Trim() ?? string.Empty;
            if (!IsBuildName(name))
            {
                throw new InvalidOperationException($"Invalid registered build tool name '{tool.Name}'.");
            }

            if (!seen.Add(name))
            {
                throw new InvalidOperationException($"Duplicate registered build tool '{name}'.");
            }

            if (string.IsNullOrWhiteSpace(tool.Path))
            {
                throw new InvalidOperationException($"Registered build tool '{name}' must declare a path.");
            }

            string toolPath;
            try
            {
                toolPath = Path.GetFullPath(tool.Path, projectDirectory);
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidOperationException(
                    $"Invalid registered build tool path '{tool.Path}': {ex.Message}",
                    ex);
            }

            result.Add(new EidosBuildToolConfiguration { Name = name, Path = toolPath });
        }

        return result.ToArray();
    }

    private static bool IsValidEnvironmentName(string name) =>
        name.Length > 0 &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.Skip(1).All(static character => char.IsAsciiLetterOrDigit(character) || character == '_');

    private static bool IsBuildName(string name) =>
        name.Length > 0 &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.Skip(1).All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool PathsOverlap(string left, string right) =>
        IsWithin(left, right) || IsWithin(right, left);

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string[] ResolvePlatformLibraries(Dictionary<string, string[]>? platform)
    {
        if (platform == null || platform.Count == 0)
            return [];

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var key = isWindows ? "windows"
                : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
                : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macos"
                : null;

        if (key != null && platform.TryGetValue(key, out var libs))
            return libs;

        if (!isWindows && platform.TryGetValue("unix", out var unixLibs))
            return unixLibs;

        return [];
    }

    private static (EidosProjectDependencyConfiguration[] Legacy, Dictionary<string, DependencySpec>? Versioned)
        ResolveDependencies(EidosProjectManifestDocument doc, string baseDirectory)
    {
        if (doc.Dependencies == null || doc.Dependencies.Count == 0)
        {
            return ([], null);
        }

        var projectDependencies = new List<EidosProjectDependencyConfiguration>();
        var versioned = new Dictionary<string, DependencySpec>(StringComparer.Ordinal);
        foreach (var (name, spec) in doc.Dependencies)
        {
            versioned[name] = new DependencySpec
            {
                Path = spec.Path,
                Git = spec.Git,
                Tag = spec.Tag,
                Branch = spec.Branch,
                Commit = spec.Commit,
                Version = spec.Version,
                Target = spec.Target
            };

            if (!string.IsNullOrWhiteSpace(spec.Path))
            {
                projectDependencies.Add(new EidosProjectDependencyConfiguration
                {
                    Name = NormalizeRequiredLikeValue(name),
                    Path = NormalizeProjectFilePath(spec.Path, baseDirectory),
                    Target = NormalizeOptionalValue(spec.Target)
                });
            }
        }

        return (projectDependencies.ToArray(), versioned);
    }

    internal static string[] NormalizeImportRoots(
        IReadOnlyList<string>? importRoots,
        string baseDirectory)
    {
        if (importRoots == null || importRoots.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(importRoots.Count);

        foreach (var importRoot in importRoots)
        {
            if (string.IsNullOrWhiteSpace(importRoot))
            {
                continue;
            }

            string normalizedPath;
            try
            {
                normalizedPath = Path.IsPathRooted(importRoot)
                    ? Path.GetFullPath(importRoot)
                    : Path.GetFullPath(Path.Combine(baseDirectory, importRoot));
            }
            catch
            {
                continue;
            }

            if (!seen.Add(normalizedPath))
            {
                continue;
            }

            normalized.Add(normalizedPath);
        }

        return normalized.ToArray();
    }

    private static EidosProjectTargetConfiguration[] NormalizeTargets(
        IReadOnlyList<EidosProjectTargetManifestDocument>? targets,
        string baseDirectory)
    {
        if (targets == null || targets.Count == 0)
        {
            return [];
        }

        return targets.Select(target => new EidosProjectTargetConfiguration
        {
            Name = NormalizeRequiredLikeValue(target.Name),
            Entry = NormalizeFileLikePath(target.Entry, baseDirectory),
            Kind = NormalizeOptionalValue(target.Kind),
            Dependencies = NormalizeReferenceNames(target.Dependencies),
            ProjectDependencies = NormalizeReferenceNames(target.ProjectDependencies)
        }).ToArray();
    }

    private static EidosProjectTargetConfiguration[] ResolveTargets(
        IReadOnlyList<EidosProjectTargetManifestDocument>? targets,
        string baseDirectory)
    {
        if (targets is { Count: > 0 })
        {
            return NormalizeTargets(targets, baseDirectory);
        }

        var mainEntry = Path.Combine(baseDirectory, "src", "Main.eidos");
        if (File.Exists(mainEntry))
        {
            return
            [
                new EidosProjectTargetConfiguration
                {
                    Name = "main",
                    Entry = Path.GetFullPath(mainEntry),
                    Kind = "executable"
                }
            ];
        }

        var libEntry = Path.Combine(baseDirectory, "src", "Lib.eidos");
        if (File.Exists(libEntry))
        {
            return
            [
                new EidosProjectTargetConfiguration
                {
                    Name = "lib",
                    Entry = Path.GetFullPath(libEntry),
                    Kind = "library"
                }
            ];
        }

        return [];
    }

    private static string[] NormalizeReferenceNames(IReadOnlyList<string>? references)
    {
        if (references == null || references.Count == 0)
        {
            return [];
        }

        return references
            .Select(NormalizeRequiredLikeValue)
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .ToArray();
    }

    private static string[] NormalizeFileLikePaths(IReadOnlyList<string>? paths, string baseDirectory)
    {
        if (paths == null || paths.Count == 0)
        {
            return [];
        }

        return paths
            .Select(path => NormalizeFileLikePath(path, baseDirectory))
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string NormalizeFileLikePath(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        return NormalizeAbsolutePath(path, baseDirectory);
    }

    internal static string NormalizeProjectFilePath(string? path, string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "";
        }

        var normalizedPath = NormalizeAbsolutePath(path, baseDirectory);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            return "";
        }

        if (Directory.Exists(normalizedPath))
        {
            return Path.Combine(normalizedPath, DefaultFileName);
        }

        if (string.Equals(Path.GetFileName(normalizedPath), DefaultFileName, StringComparison.OrdinalIgnoreCase) ||
            Path.HasExtension(normalizedPath))
        {
            return normalizedPath;
        }

        return Path.Combine(normalizedPath, DefaultFileName);
    }

    private static string NormalizeAbsolutePath(string path, string baseDirectory)
    {
        try
        {
            return Path.IsPathRooted(path)
                ? Path.GetFullPath(path)
                : Path.GetFullPath(Path.Combine(baseDirectory, path));
        }
        catch
        {
            return "";
        }
    }

    private static string NormalizeRequiredLikeValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? ""
            : value.Trim();
    }

    private static string? NormalizeOptionalValue(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    internal static string[] CombineSearchRoots(
        IReadOnlyList<string>? sourceRoots,
        IReadOnlyList<string>? importRoots)
    {
        var combined = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AppendRoots(sourceRoots);
        AppendRoots(importRoots);

        return combined.ToArray();

        void AppendRoots(IReadOnlyList<string>? roots)
        {
            if (roots == null)
            {
                return;
            }

            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !seen.Add(root))
                {
                    continue;
                }

                combined.Add(root);
            }
        }
    }

    private static string? TryGetSearchStartDirectory(string inputFilePath)
    {
        if (string.IsNullOrWhiteSpace(inputFilePath))
        {
            return null;
        }

        try
        {
            var normalizedPath = Path.GetFullPath(inputFilePath);
            if (Directory.Exists(normalizedPath))
            {
                return normalizedPath;
            }

            return Path.GetDirectoryName(normalizedPath);
        }
        catch
        {
            return null;
        }
    }

}
