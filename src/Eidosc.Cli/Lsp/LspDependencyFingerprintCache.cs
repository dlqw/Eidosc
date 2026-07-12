using System.Globalization;
using System.Text;

namespace Eidosc.Cli.Lsp;

internal sealed class LspDependencyFingerprintCache : IDisposable
{
    private sealed record DirectoryCacheEntry(string Fingerprint, string[] Files, int Version);

    private readonly object _sync = new();
    private readonly Dictionary<string, DirectoryCacheEntry> _directoryCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _directoryVersions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, FileSystemWatcher> _watchers = new(StringComparer.OrdinalIgnoreCase);
    private int _directoryScanCount;
    private int _changeStamp;

    public int DirectoryScanCount => Volatile.Read(ref _directoryScanCount);
    public int ChangeStamp => Volatile.Read(ref _changeStamp);

    public string GetDirectoryFingerprint(string root, string baseDirectory)
    {
        var fullRoot = NormalizeRoot(root, baseDirectory);
        if (!Directory.Exists(fullRoot))
        {
            return $"missing-root:{fullRoot}{Environment.NewLine}";
        }

        int version;
        lock (_sync)
        {
            if (_directoryCache.TryGetValue(fullRoot, out var cached))
            {
                return cached.Fingerprint;
            }

            EnsureWatcherLocked(fullRoot);
            version = GetDirectoryVersionLocked(fullRoot);
        }

        var indexed = ComputeDirectoryIndex(fullRoot);

        lock (_sync)
        {
            var currentVersion = GetDirectoryVersionLocked(fullRoot);
            if (currentVersion == version)
            {
                _directoryCache[fullRoot] = new DirectoryCacheEntry(indexed.Fingerprint, indexed.Files, version);
            }
        }

        return indexed.Fingerprint;
    }

    public string[] GetIndexedFiles(string root, string baseDirectory)
    {
        var fullRoot = NormalizeRoot(root, baseDirectory);
        if (!Directory.Exists(fullRoot))
        {
            return [];
        }

        GetDirectoryFingerprint(fullRoot, Directory.GetCurrentDirectory());
        lock (_sync)
        {
            return _directoryCache.TryGetValue(fullRoot, out var cached)
                ? [.. cached.Files]
                : [];
        }
    }

    public void InvalidateDirectory(string root, string? baseDirectory = null)
    {
        var fullRoot = NormalizeRoot(root, baseDirectory ?? Directory.GetCurrentDirectory());
        lock (_sync)
        {
            InvalidateDirectoryLocked(fullRoot);
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            foreach (var watcher in _watchers.Values)
            {
                watcher.Dispose();
            }

            _watchers.Clear();
            _directoryCache.Clear();
            _directoryVersions.Clear();
        }
    }

    private (string Fingerprint, string[] Files) ComputeDirectoryIndex(string fullRoot)
    {
        Interlocked.Increment(ref _directoryScanCount);

        var builder = new StringBuilder();
        var files = Directory.EnumerateFiles(fullRoot, "*.eidos", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .OrderBy(static file => file, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        foreach (var file in files)
        {
            AppendFileFingerprint(builder, file);
        }

        return (builder.ToString(), files);
    }

    private void EnsureWatcherLocked(string fullRoot)
    {
        if (_watchers.ContainsKey(fullRoot))
        {
            return;
        }

        try
        {
            var watcher = new FileSystemWatcher(fullRoot, "*.eidos")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName |
                               NotifyFilters.DirectoryName |
                               NotifyFilters.LastWrite |
                               NotifyFilters.Size |
                               NotifyFilters.CreationTime,
                EnableRaisingEvents = true
            };
            watcher.Changed += (_, _) => InvalidateDirectory(fullRoot);
            watcher.Created += (_, _) => InvalidateDirectory(fullRoot);
            watcher.Deleted += (_, _) => InvalidateDirectory(fullRoot);
            watcher.Renamed += (_, _) => InvalidateDirectory(fullRoot);
            watcher.Error += (_, _) => InvalidateDirectory(fullRoot);
            _watchers[fullRoot] = watcher;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            InvalidateDirectoryLocked(fullRoot);
        }
    }

    private int GetDirectoryVersionLocked(string fullRoot)
    {
        return _directoryVersions.TryGetValue(fullRoot, out var version) ? version : 0;
    }

    private void InvalidateDirectoryLocked(string fullRoot)
    {
        _directoryCache.Remove(fullRoot);
        _directoryVersions[fullRoot] = GetDirectoryVersionLocked(fullRoot) + 1;
        unchecked
        {
            _changeStamp++;
        }
    }

    private static string NormalizeRoot(string root, string baseDirectory)
    {
        var fullRoot = Path.IsPathRooted(root)
            ? root
            : Path.GetFullPath(Path.Combine(baseDirectory, root));
        return Path.GetFullPath(fullRoot);
    }

    internal static void AppendFileFingerprint(StringBuilder builder, string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            if (!File.Exists(fullPath))
            {
                builder.Append("missing-file:");
                builder.AppendLine(fullPath);
                return;
            }

            var info = new FileInfo(fullPath);
            builder.Append("file:");
            builder.Append(fullPath);
            builder.Append(':');
            builder.Append(info.Length);
            builder.Append(':');
            builder.AppendLine(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            builder.Append("unreadable-file:");
            builder.Append(filePath);
            builder.Append(':');
            builder.AppendLine(ex.GetType().Name);
        }
    }
}
