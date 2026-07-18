using Eidosc.Symbols;
using Eidosc.ProjectSystem;
using Eidosc.Pipeline;
namespace Eidosc.Semantic;

internal sealed record ResolvedWorkspaceModuleFile(
    string FilePath,
    string RootDirectory,
    string ModulePath);

internal static class WorkspaceModuleLocator
{
    public static ResolvedWorkspaceModuleFile? ResolveImportModule(
        string entryFilePath,
        IReadOnlyList<string> modulePath,
        IReadOnlyList<string>? explicitImportRoots = null)
    {
        return ResolveImportModuleCandidates(entryFilePath, modulePath, explicitImportRoots).FirstOrDefault();
    }

    public static IReadOnlyList<ResolvedWorkspaceModuleFile> ResolveImportModuleCandidates(
        string entryFilePath,
        IReadOnlyList<string> modulePath,
        IReadOnlyList<string>? explicitImportRoots = null)
    {
        if (modulePath.Count == 0)
        {
            return [];
        }

        var normalizedModulePath = string.Join(WellKnownStrings.Operators.Divide, modulePath.Where(segment => !string.IsNullOrWhiteSpace(segment)));
        if (string.IsNullOrWhiteSpace(normalizedModulePath))
        {
            return [];
        }

        var candidates = new List<ResolvedWorkspaceModuleFile>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootDirectory in EnumerateImportSearchRoots(entryFilePath, explicitImportRoots))
        {
            var resolved = ResolveImportModuleFileFromRoot(rootDirectory, modulePath);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                continue;
            }

            if (seenFiles.Add(resolved))
            {
                candidates.Add(new ResolvedWorkspaceModuleFile(
                    resolved,
                    rootDirectory,
                    normalizedModulePath));
            }
        }

        return candidates;
    }

    public static ResolvedWorkspaceModuleFile? ResolveImportModuleFromRoots(
        IReadOnlyList<string> modulePath,
        IReadOnlyList<string> roots)
    {
        return ResolveImportModuleCandidatesFromRoots(modulePath, roots).FirstOrDefault();
    }

    public static IReadOnlyList<ResolvedWorkspaceModuleFile> ResolveImportModuleCandidatesFromRoots(
        IReadOnlyList<string> modulePath,
        IReadOnlyList<string> roots)
    {
        if (modulePath.Count == 0)
        {
            return [];
        }

        var normalizedModulePath = string.Join(WellKnownStrings.Operators.Divide, modulePath.Where(segment => !string.IsNullOrWhiteSpace(segment)));
        if (string.IsNullOrWhiteSpace(normalizedModulePath))
        {
            return [];
        }

        var candidates = new List<ResolvedWorkspaceModuleFile>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rootDirectory in roots)
        {
            var resolved = ResolveImportModuleFileFromRoot(rootDirectory, modulePath);
            if (string.IsNullOrWhiteSpace(resolved))
            {
                continue;
            }

            if (seenFiles.Add(resolved))
            {
                candidates.Add(new ResolvedWorkspaceModuleFile(
                    resolved,
                    rootDirectory,
                    normalizedModulePath));
            }
        }

        return candidates;
    }

    public static string? ResolveImportModuleFile(
        string entryFilePath,
        IReadOnlyList<string> modulePath,
        IReadOnlyList<string>? explicitImportRoots = null)
    {
        return ResolveImportModule(entryFilePath, modulePath, explicitImportRoots)?.FilePath;
    }

    public static IEnumerable<string> EnumerateImportSearchRoots(
        string entryFilePath,
        IReadOnlyList<string>? explicitImportRoots = null)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasEntryDirectory = TryGetEntryDirectory(entryFilePath, out var entryDirectory);
        if (hasEntryDirectory && seen.Add(entryDirectory))
        {
            yield return entryDirectory;
        }

        var importResolution = EidosProjectConfigurationLoader.ResolveImportSearchRoots(
            entryFilePath,
            explicitImportRoots);
        if (importResolution.UsesExplicitImportRoots ||
            !string.IsNullOrWhiteSpace(importResolution.ProjectFilePath))
        {
            foreach (var root in importResolution.EffectiveSearchRoots)
            {
                if (!seen.Add(root))
                {
                    continue;
                }

                yield return root;
            }

            yield break;
        }

        if (!hasEntryDirectory)
        {
            yield break;
        }

        var preferredAncestorRoot = FindPreferredAncestorImportRoot(entryDirectory);
        if (!string.IsNullOrWhiteSpace(preferredAncestorRoot) &&
            seen.Add(preferredAncestorRoot))
        {
            yield return preferredAncestorRoot;
        }
    }

    private static string? ResolveImportModuleFileFromRoot(string rootDirectory, IReadOnlyList<string> modulePath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || modulePath.Count == 0)
        {
            return null;
        }

        var relativePath = Path.Combine(modulePath
            .Select(ManifestNamingRules.NormalizeDependencyAlias)
            .ToArray());
        var directFile = Path.Combine(rootDirectory, $"{relativePath}.eidos");
        if (File.Exists(directFile))
        {
            return Path.GetFullPath(directFile);
        }

        var moduleDirectoryFile = Path.Combine(rootDirectory, relativePath, "mod.eidos");
        if (File.Exists(moduleDirectoryFile))
        {
            return Path.GetFullPath(moduleDirectoryFile);
        }

        return null;
    }

    public static string? TryGetModulePathFromRoot(string rootDirectory, string filePath)
    {
        if (string.IsNullOrWhiteSpace(rootDirectory) || string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var relativePath = Path.GetRelativePath(rootDirectory, filePath);
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.StartsWith(WellKnownStrings.Punctuation.DotDot, StringComparison.Ordinal))
        {
            return null;
        }

        var normalized = relativePath.Replace('\\', '/');
        if (normalized.EndsWith("/mod.eidos", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^"/mod.eidos".Length];
        }
        else if (normalized.EndsWith(".eidos", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[..^".eidos".Length];
        }

        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        return string.Join(
            WellKnownStrings.Operators.Divide,
            normalized
                .Split(WellKnownStrings.Operators.Divide, StringSplitOptions.RemoveEmptyEntries)
                .Select(ManifestNamingRules.NormalizeModulePathSegment));
    }

    private static bool TryGetEntryDirectory(string entryFilePath, out string entryDirectory)
    {
        entryDirectory = string.Empty;
        if (string.IsNullOrWhiteSpace(entryFilePath))
        {
            return false;
        }

        try
        {
            var normalizedFilePath = Path.GetFullPath(entryFilePath);
            var directory = Path.GetDirectoryName(normalizedFilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                return false;
            }

            entryDirectory = directory;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? FindPreferredAncestorImportRoot(string entryDirectory)
    {
        string? workspaceRootFallback = null;
        var current = Directory.GetParent(entryDirectory)?.FullName;

        while (!string.IsNullOrWhiteSpace(current))
        {
            if (IsSourceRootDirectory(current))
            {
                return current;
            }

            if (workspaceRootFallback == null && IsWorkspaceRootDirectory(current))
            {
                workspaceRootFallback = current;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                break;
            }

            current = parent;
        }

        return workspaceRootFallback;
    }

    private static bool IsSourceRootDirectory(string directory)
    {
        var name = Path.GetFileName(directory);
        return string.Equals(name, "src", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "source", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkspaceRootDirectory(string directory)
    {
        return Directory.Exists(Path.Combine(directory, ".git")) ||
               Directory.EnumerateFiles(directory, "*.sln", SearchOption.TopDirectoryOnly).Any();
    }
}

