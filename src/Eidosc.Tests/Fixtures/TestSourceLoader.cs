using System.Collections.Concurrent;
using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Fixtures;

/// <summary>
/// 加载测试源代码文件
/// </summary>
public static class TestSourceLoader
{
    private static readonly TestPathConfig PathConfig = TestPathConfig.Current;

    private static readonly string BasePath = FindBasePath();
    private static readonly ConcurrentDictionary<SourceCacheKey, string> SourceTextCache = new();
    private static long _sourceTextCacheHits;
    private static long _sourceTextCacheMisses;

    private static string FindBasePath()
    {
        var configuredBasePath = Environment.GetEnvironmentVariable("EIDOS_TEST_BASE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredBasePath))
        {
            var fullPath = Path.GetFullPath(configuredBasePath);
            if (!Directory.Exists(fullPath))
            {
                throw new InvalidOperationException($"Configured EIDOS_TEST_BASE_PATH does not exist: {fullPath}");
            }

            return fullPath;
        }

        var current = Directory.GetCurrentDirectory();
        while (current != null)
        {
            var fixtureRoot = CombinePath(current, PathConfig.FixtureSourceRootSegments);
            if (Directory.Exists(fixtureRoot) &&
                Directory.EnumerateFiles(fixtureRoot, "*.eidos", SearchOption.AllDirectories).Any())
                return current;
            current = Directory.GetParent(current)?.FullName;
        }
        throw new InvalidOperationException("Cannot find project root directory");
    }

    private static string CombinePath(string basePath, IReadOnlyList<string> relativeSegments)
    {
        var parts = new string[relativeSegments.Count + 1];
        parts[0] = basePath;
        for (var i = 0; i < relativeSegments.Count; i++)
        {
            parts[i + 1] = relativeSegments[i];
        }

        return Path.Combine(parts);
    }

    public static string Load(string relativePath)
    {
        var fullPath = Path.GetFullPath(Path.Combine(BasePath, relativePath));
        var fileInfo = new FileInfo(fullPath);
        if (!fileInfo.Exists)
            throw new FileNotFoundException($"Test source not found: {fullPath}");

        var cacheKey = new SourceCacheKey(
            fullPath,
            fileInfo.LastWriteTimeUtc.Ticks,
            fileInfo.Length);

        if (SourceTextCache.TryGetValue(cacheKey, out var cachedSource))
        {
            Interlocked.Increment(ref _sourceTextCacheHits);
            return cachedSource;
        }

        var source = File.ReadAllText(fullPath);
        SourceTextCache[cacheKey] = source;
        Interlocked.Increment(ref _sourceTextCacheMisses);
        return source;
    }

    public static string GetFullPath(string relativePath)
    {
        return Path.Combine(BasePath, relativePath);
    }

    public static string GetLanguageVersion(string relativeOrFullPath)
    {
        var fullPath = Path.IsPathRooted(relativeOrFullPath)
            ? relativeOrFullPath
            : GetFullPath(relativeOrFullPath);

        return EidosProjectConfigurationLoader.TryLoadNearest(fullPath)?.Configuration.LanguageVersion
            ?? EidosLanguageVersions.DefaultForExistingProjects;
    }

    public static TestSourceLoaderCacheSnapshot GetCacheSnapshot() =>
        new(
            Interlocked.Read(ref _sourceTextCacheHits),
            Interlocked.Read(ref _sourceTextCacheMisses),
            SourceTextCache.Count);

    private readonly record struct SourceCacheKey(
        string FullPath,
        long LastWriteTicks,
        long Length);
}

public readonly record struct TestSourceLoaderCacheSnapshot(
    long Hits,
    long Misses,
    int Entries);
