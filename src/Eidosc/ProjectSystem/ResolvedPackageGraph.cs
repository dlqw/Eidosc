using Eidosc.Pipeline;

namespace Eidosc.ProjectSystem;

public sealed record ResolvedPackage
{
    public string Name { get; init; } = "";
    public DependencySourceKind Source { get; init; }
    public string? ResolvedPath { get; init; }
    public string? GitUrl { get; init; }
    public string? RegistryName { get; init; }
    public string? RegistryIndexUrl { get; init; }
    public string? Commit { get; init; }
    public string? Tag { get; init; }
    public string? Branch { get; init; }
    public string? Version { get; init; }
    public string? ContentHash { get; init; }
    public string[] SourceRoots { get; init; } = [];
    public string[] ImportRoots { get; init; } = [];
    public EidosFfiConfiguration? Ffi { get; init; }
}

public sealed record ResolvedPackageGraph
{
    public Dictionary<string, ResolvedPackage> Packages { get; init; } = new(StringComparer.Ordinal);

    public string[] GetAllSearchRoots()
    {
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in Packages.Values)
        {
            foreach (var root in pkg.SourceRoots.Concat(pkg.ImportRoots))
            {
                if (seen.Add(root))
                    roots.Add(root);
            }
        }

        return roots.ToArray();
    }

    public EidosFfiConfiguration? GetCombinedFfiConfiguration()
    {
        var libraries = new List<string>();
        var libraryPaths = new List<string>();
        var includePaths = new List<string>();
        var nativeSources = new List<string>();
        var linkerFlags = new List<string>();

        foreach (var package in Packages.Values)
        {
            var ffi = package.Ffi;
            if (ffi == null)
                continue;

            libraries.AddRange(ffi.Libraries);
            libraryPaths.AddRange(ffi.LibraryPaths);
            includePaths.AddRange(ffi.IncludePaths);
            nativeSources.AddRange(ffi.NativeSources);
            linkerFlags.AddRange(ffi.LinkerFlags);
        }

        return libraries.Count == 0 &&
               libraryPaths.Count == 0 &&
               includePaths.Count == 0 &&
               nativeSources.Count == 0 &&
               linkerFlags.Count == 0
            ? null
            : new EidosFfiConfiguration
            {
                Libraries = DistinctOrdinal(libraries),
                LibraryPaths = DistinctPath(libraryPaths),
                IncludePaths = DistinctPath(includePaths),
                NativeSources = DistinctPath(nativeSources),
                LinkerFlags = DistinctOrdinal(linkerFlags)
            };
    }

    private static string[] DistinctOrdinal(IEnumerable<string> values) =>
        values.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static string[] DistinctPath(IEnumerable<string> values) =>
        values.Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
}
