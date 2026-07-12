using System.Security.Cryptography;
using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace Eidosc.ProjectSystem;

public sealed class PackageIndexResolver
{
    public const string DefaultIndexUrl = "https://github.com/eidos-lang/package-index.git";
    public const string IndexUrlEnvironmentVariable = "EIDOS_PACKAGE_INDEX_URL";

    private readonly string _indexUrl;
    private readonly string _cacheDirectory;
    private Dictionary<string, PackageIndexEntry>? _packages;

    public PackageIndexResolver(string? indexUrl = null)
    {
        _indexUrl = string.IsNullOrWhiteSpace(indexUrl)
            ? Environment.GetEnvironmentVariable(IndexUrlEnvironmentVariable) ?? DefaultIndexUrl
            : indexUrl;
        _cacheDirectory = GetIndexCachePath(_indexUrl);
    }

    public string IndexUrl => _indexUrl;

    public RegistryPackageResolution Resolve(string name, string versionRangeSpec)
    {
        var packages = LoadPackages();
        if (!packages.TryGetValue(name, out var entry))
            throw new InvalidOperationException($"Package index '{_indexUrl}' does not contain package '{name}'.");

        if (string.IsNullOrWhiteSpace(entry.Repo))
            throw new InvalidOperationException($"Package index entry '{name}' must declare repo.");

        var range = VersionRange.Parse(versionRangeSpec);
        var includePreRelease = VersionRangeIncludesPreRelease(versionRangeSpec);
        var candidates = GitPackageFetcher.ListRemoteTags(entry.Repo)
            .Select(ParseTag)
            .Where(candidate => candidate != null)
            .Select(candidate => candidate!)
            .Where(candidate => range.Contains(candidate.Version))
            .Where(candidate => includePreRelease || !candidate.Version.IsPreRelease)
            .OrderByDescending(candidate => candidate.Version)
            .ToList();

        var selected = candidates.FirstOrDefault();
        if (selected == null)
            throw new InvalidOperationException(
                $"Package '{name}' has no git tag satisfying version range '{versionRangeSpec}'.");

        return new RegistryPackageResolution(
            name,
            _indexUrl,
            entry.Repo,
            entry.DefaultTarget,
            selected.Tag,
            selected.Version);
    }

    public IReadOnlyDictionary<string, PackageIndexEntry> LoadPackages()
    {
        if (_packages != null)
            return _packages;

        var fetcher = new GitPackageFetcher();
        fetcher.CloneOrUpdate(_indexUrl, _cacheDirectory);

        var indexFile = FindIndexFile(_cacheDirectory)
            ?? throw new InvalidOperationException(
                $"Package index '{_indexUrl}' does not contain packages.toml or index.toml.");

        var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(indexFile));
        if (model == null ||
            !model.TryGetValue("packages", out var packagesValue) ||
            packagesValue is not TomlTable packagesTable)
        {
            throw new InvalidOperationException($"Package index '{indexFile}' must contain a [packages] table.");
        }

        var packages = new Dictionary<string, PackageIndexEntry>(StringComparer.Ordinal);
        foreach (var (name, value) in packagesTable)
        {
            if (value is not TomlTable packageTable)
                throw new InvalidOperationException($"Package index entry '{name}' must be a TOML table.");

            var repo = GetString(packageTable, "repo");
            var defaultTarget = GetString(packageTable, "defaultTarget");
            packages.Add(name, new PackageIndexEntry(repo, defaultTarget));
        }

        _packages = packages;
        return packages;
    }

    private static TagCandidate? ParseTag(string tag)
    {
        var versionText = tag.StartsWith('v') ? tag[1..] : tag;
        return SemanticVersion.TryParse(versionText, out var version) && version != null
            ? new TagCandidate(tag, version)
            : null;
    }

    private static bool VersionRangeIncludesPreRelease(string spec)
    {
        return spec.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Any(part =>
            {
                var candidate = part.TrimStart('^', '~', '>', '<', '=');
                return candidate.Contains('-', StringComparison.Ordinal);
            });
    }

    private static string? FindIndexFile(string directory)
    {
        foreach (var fileName in new[] { "packages.toml", "index.toml", "eidos-index.toml" })
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    private static string? GetString(TomlTable table, string key)
    {
        return table.TryGetValue(key, out var value) && value is string text
            ? text
            : null;
    }

    private static string GetIndexCachePath(string indexUrl)
    {
        var homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var cacheBase = Path.Combine(homeDir, ".eidosc", "index");
        return Path.Combine(cacheBase, ComputeHash(indexUrl));
    }

    private static string ComputeHash(string value)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..16];
    }

    private sealed record TagCandidate(string Tag, SemanticVersion Version);
}

public sealed record PackageIndexEntry(string? Repo, string? DefaultTarget);

public sealed record RegistryPackageResolution(
    string Name,
    string IndexUrl,
    string GitUrl,
    string? DefaultTarget,
    string Tag,
    SemanticVersion Version);
