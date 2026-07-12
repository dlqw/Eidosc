
using Eidosc.Symbols;

namespace Eidosc.Semantic;

internal enum ImportSuggestionTargetKind
{
    Any,
    TypeLike,
    Effect
}

internal static class ModuleImportSuggestionLocator
{
    public static IEnumerable<string> FindPrecompiledImportCandidateModules(string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var modulePath in PrecompiledModuleRegistry.GetAvailableModulePaths())
        {
            if (!PrecompiledModuleExportsContainName(modulePath, name) ||
                !seen.Add(modulePath))
            {
                continue;
            }

            yield return modulePath;
        }

        foreach (var root in EnumeratePrecompiledSourceRoots())
        {
            var moduleSources = BuildPrecompiledSourceMap(root);
            foreach (var (modulePath, source) in moduleSources)
            {
                if (!seen.Add(modulePath))
                {
                    continue;
                }

                var exports = PrecompiledModuleRegistry.ExtractExportsForTest(source, modulePath, moduleSources);
                if (ExportsContainName(exports, name))
                {
                    yield return modulePath;
                }
            }
        }
    }

    public static IEnumerable<string> FindWorkspaceImportCandidateModules(
        string currentInputFile,
        IReadOnlyList<string> importSearchRoots,
        string name)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var moduleSources = BuildWorkspaceModuleSourceMap(currentInputFile, importSearchRoots);
        foreach (var (modulePath, source) in moduleSources)
        {
            if (!WorkspaceModuleExportsContainName(modulePath, source, moduleSources, name) ||
                !seen.Add(modulePath))
            {
                continue;
            }

            yield return modulePath;
        }
    }

    public static IEnumerable<string> FindPrecompiledQualifiedImportCandidateModules(
        string moduleLeafName,
        IReadOnlyList<string> relativePath,
        ImportSuggestionTargetKind targetKind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var modulePath in PrecompiledModuleRegistry.GetAvailableModulePaths())
        {
            if (!string.Equals(GetModuleLeafName(modulePath), moduleLeafName, StringComparison.Ordinal) ||
                !PrecompiledModuleMatchesQualifiedImportCandidate(modulePath, relativePath, targetKind) ||
                !seen.Add(modulePath))
            {
                continue;
            }

            yield return modulePath;
        }

        foreach (var root in EnumeratePrecompiledSourceRoots())
        {
            var moduleSources = BuildPrecompiledSourceMap(root);
            foreach (var (modulePath, source) in moduleSources)
            {
                if (!string.Equals(GetModuleLeafName(modulePath), moduleLeafName, StringComparison.Ordinal) ||
                    !seen.Add(modulePath))
                {
                    continue;
                }

                if (!PrecompiledModuleMatchesQualifiedImportCandidate(
                        modulePath,
                        relativePath,
                        targetKind,
                        source,
                        moduleSources))
                {
                    continue;
                }

                yield return modulePath;
            }
        }
    }

    public static IEnumerable<string> FindWorkspaceQualifiedImportCandidateModules(
        string currentInputFile,
        IReadOnlyList<string> importSearchRoots,
        string moduleLeafName,
        IReadOnlyList<string> relativePath,
        ImportSuggestionTargetKind targetKind)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var moduleSources = BuildWorkspaceModuleSourceMap(currentInputFile, importSearchRoots);
        foreach (var (modulePath, source) in moduleSources)
        {
            if (!string.Equals(GetModuleLeafName(modulePath), moduleLeafName, StringComparison.Ordinal) ||
                !WorkspaceModuleMatchesQualifiedImportCandidate(modulePath, source, moduleSources, relativePath, targetKind) ||
                !seen.Add(modulePath))
            {
                continue;
            }

            yield return modulePath;
        }
    }

    private static bool PrecompiledModuleExportsContainName(string modulePath, string name)
    {
        return PrecompiledModuleRegistry.GetExports(modulePath).ContainsName(name);
    }

    private static bool ExportsContainName(PrecompiledModuleExports exports, string name)
    {
        return exports.ContainsName(name);
    }

    private static Dictionary<string, string> BuildWorkspaceModuleSourceMap(
        string currentInputFile,
        IReadOnlyList<string> importSearchRoots)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var precompiledRoots = EnumeratePrecompiledSourceRoots()
            .Select(Path.GetFullPath)
            .ToArray();

        foreach (var root in EnumerateWorkspaceSearchRoots(currentInputFile, importSearchRoots))
        {
            foreach (var filePath in Directory.EnumerateFiles(root, "*.eidos", SearchOption.AllDirectories))
            {
                var fullPath = Path.GetFullPath(filePath);
                if (!seenFiles.Add(fullPath) ||
                    string.Equals(fullPath, currentInputFile, StringComparison.OrdinalIgnoreCase) ||
                    IsUnderAnyRoot(fullPath, precompiledRoots))
                {
                    continue;
                }

                var modulePath = WorkspaceModuleLocator.TryGetModulePathFromRoot(root, fullPath);
                if (modulePath == null ||
                    result.ContainsKey(modulePath))
                {
                    continue;
                }

                result[modulePath] = File.ReadAllText(fullPath);
            }
        }

        return result;
    }

    private static IEnumerable<string> EnumerateWorkspaceSearchRoots(
        string currentInputFile,
        IReadOnlyList<string> importSearchRoots)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in WorkspaceModuleLocator.EnumerateImportSearchRoots(currentInputFile, importSearchRoots))
        {
            if (string.IsNullOrWhiteSpace(root) ||
                !Directory.Exists(root) ||
                !seen.Add(root))
            {
                continue;
            }

            yield return root;
        }
    }

    private static bool WorkspaceModuleExportsContainName(
        string modulePath,
        string source,
        IReadOnlyDictionary<string, string> moduleSources,
        string name)
    {
        var exports = PrecompiledModuleRegistry.ExtractExportsForTest(source, modulePath, moduleSources);
        return ExportsContainName(exports, name);
    }

    private static bool WorkspaceModuleMatchesQualifiedImportCandidate(
        string modulePath,
        string source,
        IReadOnlyDictionary<string, string> moduleSources,
        IReadOnlyList<string> relativePath,
        ImportSuggestionTargetKind targetKind)
    {
        if (relativePath.Count == 0)
        {
            return false;
        }

        var exports = PrecompiledModuleRegistry.ExtractExportsForTest(source, modulePath, moduleSources);
        var memberName = relativePath[0];
        if (relativePath.Count == 1)
        {
            return targetKind switch
            {
                ImportSuggestionTargetKind.TypeLike =>
                    exports.Types.Contains(memberName, StringComparer.Ordinal) ||
                    exports.Traits.Contains(memberName, StringComparer.Ordinal),
                ImportSuggestionTargetKind.Effect =>
                    exports.Effects.Contains(memberName, StringComparer.Ordinal),
                _ => ExportsContainName(exports, memberName)
            };
        }

        if (PrecompiledModuleRegistry.TryGetExportedModulePathForTest(
                source,
                modulePath,
                moduleSources,
                memberName,
                out var targetModulePath) &&
            moduleSources.TryGetValue(targetModulePath, out var targetSource))
        {
            return WorkspaceModuleMatchesQualifiedImportCandidate(
                targetModulePath,
                targetSource,
                moduleSources,
                relativePath.Skip(1).ToList(),
                targetKind);
        }

        if (targetKind != ImportSuggestionTargetKind.Any || relativePath.Count != 2)
        {
            return false;
        }

        return PrecompiledModuleRegistry.ExportedOwnerDefinesMemberForTest(
            source,
            memberName,
            relativePath[1],
            modulePath,
            moduleSources);
    }

    private static string GetModuleLeafName(string modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
        {
            return string.Empty;
        }

        var segments = modulePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }

    private static IEnumerable<string> EnumeratePrecompiledSourceRoots()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var basePath in EnumeratePrecompiledSearchBasePaths())
        {
            if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
            {
                continue;
            }

            var current = Path.GetFullPath(basePath);
            while (!string.IsNullOrWhiteSpace(current))
            {
                foreach (var candidate in GetPrecompiledSourceRootCandidates(current))
                {
                    if (Directory.Exists(candidate) && seen.Add(candidate))
                    {
                        yield return candidate;
                    }
                }

                var parent = Directory.GetParent(current)?.FullName;
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }
        }
    }

    private static IEnumerable<string> GetPrecompiledSourceRootCandidates(string current)
    {
        yield return Path.Combine(current, "src", "Eidosc", "Stdlib", "Precompiled");
        yield return Path.Combine(current, "src", "Eidosc", "Eidosc", "Stdlib", "Precompiled");
    }

    private static IEnumerable<string> EnumeratePrecompiledSearchBasePaths()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }

    private static string? TryGetModulePathFromPrecompiledFile(string rootDirectory, string filePath)
    {
        var relativePath = Path.GetRelativePath(rootDirectory, filePath);
        if (string.IsNullOrWhiteSpace(relativePath) ||
            relativePath.StartsWith(WellKnownStrings.Punctuation.DotDot, StringComparison.Ordinal))
        {
            return null;
        }

        var modulePath = Path.ChangeExtension(relativePath, null);
        return string.IsNullOrWhiteSpace(modulePath)
            ? null
            : modulePath.Replace('\\', '/');
    }

    private static Dictionary<string, string> BuildPrecompiledSourceMap(string rootDirectory)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var filePath in Directory.EnumerateFiles(rootDirectory, "*.eidos", SearchOption.AllDirectories))
        {
            var modulePath = TryGetModulePathFromPrecompiledFile(rootDirectory, filePath);
            if (modulePath == null)
            {
                continue;
            }

            result[modulePath] = File.ReadAllText(filePath);
        }

        return result;
    }

    private static bool PrecompiledModuleMatchesQualifiedImportCandidate(
        string modulePath,
        IReadOnlyList<string> relativePath,
        ImportSuggestionTargetKind targetKind)
    {
        return PrecompiledModuleMatchesQualifiedImportCandidate(
            modulePath,
            relativePath,
            targetKind,
            source: null,
            moduleSources: null);
    }

    private static bool PrecompiledModuleMatchesQualifiedImportCandidate(
        string modulePath,
        IReadOnlyList<string> relativePath,
        ImportSuggestionTargetKind targetKind,
        string? source,
        IReadOnlyDictionary<string, string>? moduleSources)
    {
        if (relativePath.Count == 0)
        {
            return false;
        }

        var exports = source == null || moduleSources == null
            ? PrecompiledModuleRegistry.GetExports(modulePath)
            : PrecompiledModuleRegistry.ExtractExportsForTest(source, modulePath, moduleSources);
        var memberName = relativePath[0];
        if (relativePath.Count == 1)
        {
            if (targetKind == ImportSuggestionTargetKind.TypeLike)
            {
                return exports.Types.Contains(memberName, StringComparer.Ordinal) ||
                       exports.Traits.Contains(memberName, StringComparer.Ordinal);
            }

            if (targetKind == ImportSuggestionTargetKind.Effect)
            {
                return exports.Effects.Contains(memberName, StringComparer.Ordinal);
            }

            if (ExportsContainName(exports, memberName))
            {
                return true;
            }

            return source == null || moduleSources == null
                ? PrecompiledModuleRegistry.ExportedOwnerDefinesMember(modulePath, GetModuleLeafName(modulePath), memberName)
                : PrecompiledModuleRegistry.ExportedOwnerDefinesMemberForTest(
                    source,
                    GetModuleLeafName(modulePath),
                    memberName,
                    modulePath,
                    moduleSources);
        }

        if ((source == null || moduleSources == null)
                ? PrecompiledModuleRegistry.TryGetExportedModulePath(modulePath, memberName, out var reexportedModulePath)
                : PrecompiledModuleRegistry.TryGetExportedModulePathForTest(
                    source,
                    modulePath,
                    moduleSources,
                    memberName,
                    out reexportedModulePath))
        {
            if (source != null &&
                moduleSources != null &&
                moduleSources.TryGetValue(reexportedModulePath, out var reexportedSource))
            {
                return PrecompiledModuleMatchesQualifiedImportCandidate(
                    reexportedModulePath,
                    relativePath.Skip(1).ToList(),
                    targetKind,
                    reexportedSource,
                    moduleSources);
            }

            return PrecompiledModuleMatchesQualifiedImportCandidate(
                reexportedModulePath,
                relativePath.Skip(1).ToList(),
                targetKind);
        }

        var nestedModulePath = $"{modulePath}/{memberName}";
        if (source != null &&
            moduleSources != null &&
            moduleSources.TryGetValue(nestedModulePath, out var nestedSource) &&
            PrecompiledModuleMatchesQualifiedImportCandidate(
                nestedModulePath,
                relativePath.Skip(1).ToList(),
                targetKind,
                nestedSource,
                moduleSources))
        {
            return true;
        }

        if (PrecompiledModuleRegistry.GetAvailableModulePaths().Contains(nestedModulePath, StringComparer.Ordinal) &&
            PrecompiledModuleMatchesQualifiedImportCandidate(
                nestedModulePath,
                relativePath.Skip(1).ToList(),
                targetKind))
        {
            return true;
        }

        if (targetKind != ImportSuggestionTargetKind.Any || relativePath.Count != 2)
        {
            return false;
        }

        return source == null || moduleSources == null
            ? PrecompiledModuleRegistry.ExportedOwnerDefinesMember(modulePath, memberName, relativePath[1])
            : PrecompiledModuleRegistry.ExportedOwnerDefinesMemberForTest(
                source,
                memberName,
                relativePath[1],
                modulePath,
                moduleSources);
    }

    private static bool IsUnderAnyRoot(string filePath, IReadOnlyList<string> rootDirectories)
    {
        foreach (var rootDirectory in rootDirectories)
        {
            if (IsUnderRoot(filePath, rootDirectory))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsUnderRoot(string filePath, string rootDirectory)
    {
        if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(rootDirectory))
        {
            return false;
        }

        var normalizedFilePath = Path.GetFullPath(filePath);
        var normalizedRoot = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;

        return normalizedFilePath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }
}
