namespace Eidosup.Installation;

public sealed record CacheCleanResult(long BytesBefore, long BytesAfter, int FilesRemoved, bool DryRun);

public sealed class ArtifactCacheManager
{
    private static readonly EnumerationOptions RecursiveEnumeration = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public CacheCleanResult Clean(ToolInstallLayout layout, long? maximumBytes, bool all, bool dryRun)
    {
        if (!all && maximumBytes is null)
        {
            throw new ArgumentException("Specify --all or --max-size.");
        }

        if (maximumBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }

        var offlineRoot = Path.Combine(layout.DownloadDirectory, "offline");
        var entries = EnumerateContentEntries(layout.CacheDirectory, includePartial: all)
            .Concat(EnumerateOfflineEntries(offlineRoot))
            .OrderBy(static entry => entry.LastUsedUtc)
            .ThenBy(static entry => entry.Path, StringComparer.Ordinal)
            .ToArray();
        var before = entries.Sum(static entry => entry.Size);
        var target = all ? 0 : maximumBytes!.Value;
        var remaining = before;
        var removed = 0;
        foreach (var entry in entries)
        {
            if (!all && remaining <= target)
            {
                break;
            }

            remaining -= entry.Size;
            removed += entry.FileCount;
            if (!dryRun)
            {
                DeleteEntry(entry);
            }
        }

        if (!dryRun)
        {
            DeleteEmptyDirectories(layout.CacheDirectory);
            DeleteEmptyDirectories(offlineRoot);
        }

        return new CacheCleanResult(before, remaining, removed, dryRun);
    }

    private static IEnumerable<CacheEntry> EnumerateContentEntries(string root, bool includePartial)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", RecursiveEnumeration))
        {
            var file = new FileInfo(path);
            if (!includePartial && file.Name.EndsWith(".partial", StringComparison.Ordinal))
            {
                continue;
            }

            yield return new CacheEntry(file.FullName, file.Length, 1, file.LastAccessTimeUtc, IsDirectory: false);
        }
    }

    private static IEnumerable<CacheEntry> EnumerateOfflineEntries(string root)
    {
        if (!Directory.Exists(root))
        {
            yield break;
        }

        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
        {
            var directory = new DirectoryInfo(path);
            var directoryLastAccess = directory.LastAccessTimeUtc;
            var files = Directory.EnumerateFiles(path, "*", RecursiveEnumeration)
                .Select(file => new FileInfo(file))
                .ToArray();
            yield return new CacheEntry(
                directory.FullName,
                files.Sum(static file => file.Length),
                files.Length,
                files.Select(static file => file.LastAccessTimeUtc)
                    .Append(directoryLastAccess)
                    .Max(),
                IsDirectory: true);
        }

        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
        {
            var file = new FileInfo(path);
            yield return new CacheEntry(file.FullName, file.Length, 1, file.LastAccessTimeUtc, IsDirectory: false);
        }
    }

    private static void DeleteEntry(CacheEntry entry)
    {
        if (entry.IsDirectory)
        {
            if (Directory.Exists(entry.Path))
            {
                Directory.Delete(entry.Path, recursive: true);
            }

            return;
        }

        File.Delete(entry.Path);
    }

    private static void DeleteEmptyDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (var directory in Directory.EnumerateDirectories(root, "*", RecursiveEnumeration)
                     .OrderByDescending(static path => path.Length))
        {
            if (!Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
    }

    private sealed record CacheEntry(
        string Path,
        long Size,
        int FileCount,
        DateTime LastUsedUtc,
        bool IsDirectory);
}
