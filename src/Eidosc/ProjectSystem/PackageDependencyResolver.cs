using Eidosc.Symbols;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using System.Security.Cryptography;
using System.Text;
namespace Eidosc.ProjectSystem;

public sealed class PackageDependencyResolver
{
    private readonly string _projectDir;
    private readonly string[] _importRoots;

    public PackageDependencyResolver(string projectDir, string[]? importRoots = null)
    {
        _projectDir = projectDir;
        _importRoots = importRoots ?? [];
    }

    public ResolvedPackageGraph Resolve(EidosProjectConfiguration config, EidosLockFile? lockFile = null)
    {
        var graph = new ResolvedPackageGraph();
        var packages = new Dictionary<string, ResolvedPackage>(StringComparer.Ordinal);

        var deps = config.VersionedDependencies;
        if (deps == null || deps.Count == 0)
        {
            if (!config.NoImplicitStdlib)
                AddEmbeddedStdlib(packages);

            return new ResolvedPackageGraph { Packages = packages };
        }

        foreach (var (name, spec) in deps)
        {
            var locked = lockFile?.Packages.GetValueOrDefault(name);

            try
            {
                var resolved = spec.SourceKind switch
                {
                    DependencySourceKind.Path => ResolvePath(name, spec, locked),
                    DependencySourceKind.Git => ResolveGit(name, spec, locked),
                    DependencySourceKind.Version => ResolveVersion(name, spec, locked),
                    _ => throw new InvalidOperationException(PipelineMessages.UnknownDependencySource(name))
                };
                packages[name] = resolved;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    PipelineMessages.FailedToResolveDependency(name, ex.Message), ex);
            }
        }

        if (!config.NoImplicitStdlib && !packages.ContainsKey(WellKnownStrings.Std.Module))
            AddEmbeddedStdlib(packages);

        return new ResolvedPackageGraph { Packages = packages };
    }

    private ResolvedPackage ResolvePath(string name, DependencySpec spec, LockedPackage? locked)
    {
        var dependencyPath = spec.Path
            ?? throw new InvalidOperationException(PipelineMessages.PathDependencyMissingPath(name));
        var resolvedPath = Path.IsPathRooted(dependencyPath)
            ? dependencyPath
            : Path.GetFullPath(Path.Combine(_projectDir, dependencyPath));

        if (!Directory.Exists(resolvedPath))
            throw new InvalidOperationException(PipelineMessages.PathDependencyDirectoryNotFound(resolvedPath));

        var configFile = Path.Combine(resolvedPath, EidosProjectConfigurationLoader.DefaultFileName);
        string[] sourceRoots = [resolvedPath];
        string[] importRoots = [];
        string? contentHash = null;
        EidosFfiConfiguration? ffi = null;

        if (File.Exists(configFile))
        {
            var loaded = EidosProjectConfigurationLoader.LoadFromPath(configFile);
            sourceRoots = NormalizeRoots(loaded.Configuration.SourceRoots, resolvedPath);
            importRoots = NormalizeRoots(loaded.Configuration.ImportRoots, resolvedPath);
            ffi = loaded.Configuration.Ffi;

            if (locked?.ContentHash != null)
                contentHash = ContentHash.ComputeForDirectory(resolvedPath, loaded.Configuration.SourceRoots.Select(r =>
                    Path.IsPathRooted(r) ? r : Path.GetRelativePath(resolvedPath, r)).ToArray());
        }

        return new ResolvedPackage
        {
            Name = name,
            Source = DependencySourceKind.Path,
            ResolvedPath = resolvedPath,
            ContentHash = contentHash ?? (Directory.Exists(resolvedPath)
                ? ContentHash.ComputeForDirectory(resolvedPath) : null),
            SourceRoots = sourceRoots,
            ImportRoots = importRoots,
            Ffi = ffi
        };
    }

    private ResolvedPackage ResolveGit(string name, DependencySpec spec, LockedPackage? locked)
    {
        var gitUrl = spec.Git
            ?? throw new InvalidOperationException(PipelineMessages.GitDependencyMissingUrl(name));

        var refSpec = spec.Tag ?? spec.Branch ?? spec.Commit ?? "main";
        var cacheDir = GetGitCachePath(gitUrl, refSpec);

        if (!Directory.Exists(cacheDir))
        {
            var fetcher = new GitPackageFetcher();
            fetcher.Fetch(gitUrl, refSpec, cacheDir);
        }

        var loadedPackage = LoadPackageFromCache(cacheDir);
        var actualCommit = GitPackageFetcher.GetCommitHash(cacheDir);

        return new ResolvedPackage
        {
            Name = name,
            Source = DependencySourceKind.Git,
            ResolvedPath = cacheDir,
            GitUrl = gitUrl,
            Commit = actualCommit ?? spec.Commit,
            Tag = spec.Tag,
            Branch = spec.Branch,
            ContentHash = loadedPackage.ContentHash,
            SourceRoots = loadedPackage.SourceRoots,
            ImportRoots = loadedPackage.ImportRoots,
            Ffi = loadedPackage.Ffi
        };
    }

    private ResolvedPackage ResolveVersion(string name, DependencySpec spec, LockedPackage? locked)
    {
        if (name == WellKnownStrings.Std.Module)
        {
            var stdlibVersion = PrecompiledModuleRegistry.StdlibVersion;
            return new ResolvedPackage
            {
                Name = WellKnownStrings.Std.Module,
                Source = DependencySourceKind.Version,
                Version = stdlibVersion?.ToString() ?? "0.1.0"
            };
        }

        var versionRangeSpec = spec.Version ?? "*";
        var range = VersionRange.Parse(versionRangeSpec);
        if (TryResolveLockedRegistryPackage(name, range, locked, out var resolved))
            return resolved;

        var registryResolution = new PackageIndexResolver().Resolve(name, versionRangeSpec);
        return ResolveRegistryGitPackage(name, registryResolution);
    }

    private static bool TryResolveLockedRegistryPackage(
        string name,
        VersionRange range,
        LockedPackage? locked,
        out ResolvedPackage resolved)
    {
        resolved = default!;
        if (locked == null ||
            !string.Equals(locked.Source, "registry", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(locked.Git) ||
            string.IsNullOrWhiteSpace(locked.Tag) ||
            string.IsNullOrWhiteSpace(locked.Version) ||
            !SemanticVersion.TryParse(locked.Version, out var lockedVersion) ||
            lockedVersion == null ||
            !range.Contains(lockedVersion))
        {
            return false;
        }

        var cacheDir = GetGitCachePath(locked.Git, locked.Tag);
        if (!Directory.Exists(cacheDir))
        {
            var fetcher = new GitPackageFetcher();
            fetcher.Fetch(locked.Git, locked.Tag, cacheDir);
        }

        var loadedPackage = LoadPackageFromCache(cacheDir);
        var actualCommit = GitPackageFetcher.GetCommitHash(cacheDir);
        resolved = new ResolvedPackage
        {
            Name = name,
            Source = DependencySourceKind.Registry,
            ResolvedPath = cacheDir,
            RegistryName = locked.RegistryName ?? name,
            RegistryIndexUrl = locked.RegistryIndex,
            GitUrl = locked.Git,
            Commit = actualCommit ?? locked.Commit,
            Tag = locked.Tag,
            Version = lockedVersion.ToString(),
            ContentHash = loadedPackage.ContentHash,
            SourceRoots = loadedPackage.SourceRoots,
            ImportRoots = loadedPackage.ImportRoots,
            Ffi = loadedPackage.Ffi
        };
        return true;
    }

    private static ResolvedPackage ResolveRegistryGitPackage(string name, RegistryPackageResolution registryResolution)
    {
        var cacheDir = GetGitCachePath(registryResolution.GitUrl, registryResolution.Tag);
        if (!Directory.Exists(cacheDir))
        {
            var fetcher = new GitPackageFetcher();
            fetcher.Fetch(registryResolution.GitUrl, registryResolution.Tag, cacheDir);
        }

        var loadedPackage = LoadPackageFromCache(cacheDir);
        var actualCommit = GitPackageFetcher.GetCommitHash(cacheDir);
        return new ResolvedPackage
        {
            Name = name,
            Source = DependencySourceKind.Registry,
            ResolvedPath = cacheDir,
            RegistryName = registryResolution.Name,
            RegistryIndexUrl = registryResolution.IndexUrl,
            GitUrl = registryResolution.GitUrl,
            Commit = actualCommit,
            Tag = registryResolution.Tag,
            Version = registryResolution.Version.ToString(),
            ContentHash = loadedPackage.ContentHash,
            SourceRoots = loadedPackage.SourceRoots,
            ImportRoots = loadedPackage.ImportRoots,
            Ffi = loadedPackage.Ffi
        };
    }

    private static LoadedPackageCache LoadPackageFromCache(string cacheDir)
    {
        string[] sourceRoots = [cacheDir];
        string[] importRoots = [];
        string? contentHash = null;
        EidosFfiConfiguration? ffi = null;

        var configFile = Path.Combine(cacheDir, EidosProjectConfigurationLoader.DefaultFileName);
        if (File.Exists(configFile))
        {
            var loaded = EidosProjectConfigurationLoader.LoadFromPath(configFile);
            sourceRoots = NormalizeRoots(loaded.Configuration.SourceRoots, cacheDir);
            importRoots = NormalizeRoots(loaded.Configuration.ImportRoots, cacheDir);
            ffi = loaded.Configuration.Ffi;
            contentHash = ContentHash.ComputeForDirectory(cacheDir);
        }

        return new LoadedPackageCache(sourceRoots, importRoots, contentHash, ffi);
    }

    private static void AddEmbeddedStdlib(Dictionary<string, ResolvedPackage> packages)
    {
        var version = PrecompiledModuleRegistry.StdlibVersion;
        packages[WellKnownStrings.Std.Module] = new ResolvedPackage
        {
            Name = WellKnownStrings.Std.Module,
            Source = DependencySourceKind.Version,
            Version = version?.ToString() ?? "0.1.0"
        };
    }

    private static string GetGitCachePath(string gitUrl, string refSpec)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheBase = Path.Combine(homeDir, ".eidosc", "packages");
        var label = gitUrl
            .Replace("https://", "", StringComparison.OrdinalIgnoreCase)
            .Replace("http://", "", StringComparison.OrdinalIgnoreCase)
            .Replace(".git", "", StringComparison.OrdinalIgnoreCase);
        label = string.Join("_", label.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (label.Length > 48)
            label = label[..48];

        var safeRef = string.Join("_", refSpec.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries));
        if (safeRef.Length > 48)
            safeRef = safeRef[..48];

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{gitUrl}\n{refSpec}")))
            .ToLowerInvariant()[..16];

        return Path.Combine(cacheBase, $"{label}_{hash}", safeRef);
    }

    private static string[] NormalizeRoots(string[] roots, string baseDir)
    {
        if (roots == null || roots.Length == 0) return [baseDir];

        return roots
            .Select(r => Path.IsPathRooted(r) ? r : Path.GetFullPath(Path.Combine(baseDir, r)))
            .Where(Directory.Exists)
            .ToArray();
    }

    private sealed record LoadedPackageCache(
        string[] SourceRoots,
        string[] ImportRoots,
        string? ContentHash,
        EidosFfiConfiguration? Ffi);
}
