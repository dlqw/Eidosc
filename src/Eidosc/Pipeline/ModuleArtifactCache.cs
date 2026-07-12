using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eidosc.Pipeline;

/// <summary>
/// Content-addressed module artifact cache with atomic writes and cross-process mutation locking.
/// </summary>
public sealed class ModuleArtifactCache
{
    private const string LockFileName = ".cache.lock";
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    private readonly string _cacheDirectory;
    private readonly object _sync = new();
    private readonly Dictionary<string, CacheEntry> _entries = new(StringComparer.Ordinal);
    private bool _fingerprintsLoaded;

    public ModuleArtifactCache(string cacheDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cacheDirectory);
        _cacheDirectory = Path.GetFullPath(cacheDirectory);
    }

    public string CacheDirectory => _cacheDirectory;

    public bool TryGetFingerprint(string modulePath, out ModuleFingerprint? fingerprint)
    {
        lock (_sync)
        {
            EnsureFingerprintsLoaded();
            if (_entries.TryGetValue(NormalizePath(modulePath), out var entry))
            {
                fingerprint = entry.Fingerprint;
                return true;
            }
        }

        fingerprint = null;
        return false;
    }

    public bool IsUpToDate(string modulePath, string currentSourceText)
    {
        lock (_sync)
        {
            EnsureFingerprintsLoaded();
            return _entries.TryGetValue(NormalizePath(modulePath), out var entry) &&
                   entry.Fingerprint.MatchesSource(currentSourceText);
        }
    }

    public void Update(string modulePath, string sourceText, List<string>? dependencies = null)
    {
        lock (_sync)
        {
            using var processLock = AcquireProcessLock();
            var key = NormalizePath(modulePath);
            var fingerprint = ModuleFingerprint.Compute(sourceText, dependencies);
            _entries[key] = new CacheEntry { ModulePath = modulePath, Fingerprint = fingerprint };
            SaveEntry(key);
        }
    }

    public bool TryGetArtifact(ModuleArtifactKey key, string kind, out ModuleArtifactManifest? manifest)
    {
        lock (_sync)
        {
            return TryLoadArtifactManifest(key, kind, out manifest);
        }
    }

    public ModuleArtifactManifest StoreArtifact(
        ModuleArtifactKey key,
        string kind,
        string payloadExtension,
        string payload)
    {
        lock (_sync)
        {
            using var processLock = AcquireProcessLock();
            Directory.CreateDirectory(_cacheDirectory);
            var payloadPath = GetPayloadPath(key, kind, payloadExtension);
            WriteTextAtomic(payloadPath, payload);
            return StoreArtifactManifest(key, kind, payloadPath);
        }
    }

    public ModuleArtifactManifest StoreArtifactJson<T>(ModuleArtifactKey key, string kind, T payload) =>
        ArtifactSnapshotStore.StoreJson(this, key, kind, payload);

    public ModuleArtifactManifest StoreArtifactFile(
        ModuleArtifactKey key,
        string kind,
        string payloadExtension,
        string sourceFilePath)
    {
        lock (_sync)
        {
            if (!File.Exists(sourceFilePath))
            {
                throw new FileNotFoundException("Artifact payload source file does not exist.", sourceFilePath);
            }

            using var processLock = AcquireProcessLock();
            Directory.CreateDirectory(_cacheDirectory);
            var payloadPath = GetPayloadPath(key, kind, payloadExtension);
            CopyFileAtomic(sourceFilePath, payloadPath);
            return StoreArtifactManifest(key, kind, payloadPath);
        }
    }

    public bool IsArtifactUpToDate(ModuleArtifactKey key, string kind) =>
        TryGetArtifact(key, kind, out var manifest) &&
        manifest != null &&
        ValidatePayloadFile(manifest);

    public bool TryReadArtifactText(ModuleArtifactKey key, string kind, out string payload)
    {
        payload = "";
        lock (_sync)
        {
            try
            {
                if (!TryLoadArtifactManifest(key, kind, out var manifest) ||
                    manifest == null ||
                    !ValidatePayloadFile(manifest))
                {
                    return false;
                }

                payload = File.ReadAllText(manifest.PayloadPath);
                return true;
            }
            catch (IOException)
            {
                payload = "";
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                payload = "";
                return false;
            }
        }
    }

    public bool TryReadArtifactJson<T>(ModuleArtifactKey key, string kind, out T? payload) =>
        ArtifactSnapshotStore.TryLoadJson(this, key, kind, out payload);

    public void Invalidate(string modulePath)
    {
        lock (_sync)
        {
            using var processLock = AcquireProcessLock();
            var key = NormalizePath(modulePath);
            _entries.Remove(key);
            DeleteFileIfExists(GetCacheFilePath(key));
        }
    }

    public void InvalidateAll() => Clear();

    public ModuleArtifactCacheClearResult Clear()
    {
        lock (_sync)
        {
            using var processLock = AcquireProcessLock();
            _entries.Clear();
            _fingerprintsLoaded = false;
            if (!Directory.Exists(_cacheDirectory))
            {
                return new ModuleArtifactCacheClearResult(0, 0);
            }

            var deletedFiles = 0;
            long deletedBytes = 0;
            foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFileName(file), LockFileName, StringComparison.Ordinal))
                {
                    continue;
                }

                var length = TryGetFileLength(file);
                File.Delete(file);
                deletedFiles++;
                deletedBytes += length;
            }

            return new ModuleArtifactCacheClearResult(deletedFiles, deletedBytes);
        }
    }

    public ModuleArtifactCacheStatus GetStatus()
    {
        lock (_sync)
        {
            using var processLock = AcquireProcessLock();
            return GetStatusCore();
        }
    }

    public ModuleArtifactCachePruneResult Prune(long maxBytes)
    {
        if (maxBytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        }

        lock (_sync)
        {
            using var processLock = AcquireProcessLock();
            var before = GetStatusCore();
            var manifests = LoadManifestFiles()
                .Where(static item => ValidatePayloadFile(item.Manifest))
                .OrderBy(static item => IsLatestKey(item.Manifest.Key) ? 1 : 0)
                .ThenBy(static item => item.Manifest.TimestampUtc)
                .ThenBy(static item => item.ManifestPath, StringComparer.Ordinal)
                .ToArray();
            var referencedPayloads = manifests
                .GroupBy(static item => item.Manifest.PayloadPath, PathComparer)
                .ToDictionary(static group => group.Key, static group => group.Count(), PathComparer);
            var currentBytes = before.TotalBytes;
            var deletedFiles = 0;
            long deletedBytes = 0;

            foreach (var file in EnumerateGarbageFiles(manifests))
            {
                deletedBytes += DeleteFileAndMeasure(file, ref deletedFiles);
            }

            currentBytes = GetStatusCore().TotalBytes;
            foreach (var item in manifests)
            {
                if (currentBytes <= maxBytes)
                {
                    break;
                }

                deletedBytes += DeleteFileAndMeasure(item.ManifestPath, ref deletedFiles);
                if (referencedPayloads.TryGetValue(item.Manifest.PayloadPath, out var references))
                {
                    references--;
                    referencedPayloads[item.Manifest.PayloadPath] = references;
                    if (references == 0)
                    {
                        deletedBytes += DeleteFileAndMeasure(item.Manifest.PayloadPath, ref deletedFiles);
                    }
                }

                currentBytes = GetStatusCore().TotalBytes;
            }

            if (currentBytes > maxBytes && Directory.Exists(_cacheDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(
                             _cacheDirectory,
                             "*",
                             SearchOption.TopDirectoryOnly)
                             .Where(static file =>
                                 !string.Equals(Path.GetFileName(file), LockFileName, StringComparison.Ordinal))
                             .OrderBy(TryGetLastWriteTimeUtc)
                             .ThenBy(static file => file, StringComparer.Ordinal))
                {
                    if (currentBytes <= maxBytes)
                    {
                        break;
                    }

                    deletedBytes += DeleteFileAndMeasure(file, ref deletedFiles);
                    currentBytes = GetStatusCore().TotalBytes;
                }
            }

            if (deletedFiles > 0)
            {
                _entries.Clear();
                _fingerprintsLoaded = false;
            }

            var after = GetStatusCore();
            return new ModuleArtifactCachePruneResult(
                before.TotalBytes,
                after.TotalBytes,
                deletedFiles,
                deletedBytes);
        }
    }

    private ModuleArtifactManifest StoreArtifactManifest(ModuleArtifactKey key, string kind, string payloadPath)
    {
        var manifest = new ModuleArtifactManifest
        {
            Key = key,
            Kind = kind,
            PayloadPath = Path.GetFullPath(payloadPath),
            PayloadLength = TryGetFileLength(payloadPath),
            PayloadHash = ComputeFileHash(payloadPath),
            TimestampUtc = DateTime.UtcNow
        };
        WriteTextAtomic(GetArtifactManifestPath(key, kind), JsonSerializer.Serialize(manifest, JsonOptions));
        return manifest;
    }

    private bool TryLoadArtifactManifest(
        ModuleArtifactKey key,
        string kind,
        out ModuleArtifactManifest? manifest)
    {
        manifest = null;
        var manifestPath = GetArtifactManifestPath(key, kind);
        try
        {
            if (!File.Exists(manifestPath))
            {
                return false;
            }

            var loaded = JsonSerializer.Deserialize<ModuleArtifactManifest>(
                File.ReadAllText(manifestPath),
                JsonOptions);
            if (loaded == null ||
                !string.Equals(loaded.SchemaVersion, ModuleArtifactManifest.CurrentSchemaVersion, StringComparison.Ordinal) ||
                !string.Equals(loaded.Key.StableHash(), key.StableHash(), StringComparison.Ordinal) ||
                !string.Equals(NormalizeArtifactKind(loaded.Kind), NormalizeArtifactKind(kind), StringComparison.Ordinal) ||
                !IsPathWithinCache(loaded.PayloadPath))
            {
                return false;
            }

            manifest = loaded;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool ValidatePayloadFile(ModuleArtifactManifest manifest)
    {
        try
        {
            if (!File.Exists(manifest.PayloadPath) ||
                manifest.PayloadLength < 0 ||
                string.IsNullOrWhiteSpace(manifest.PayloadHash) ||
                TryGetFileLength(manifest.PayloadPath) != manifest.PayloadLength)
            {
                return false;
            }

            return string.Equals(
                ComputeFileHash(manifest.PayloadPath),
                manifest.PayloadHash,
                StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void EnsureFingerprintsLoaded()
    {
        if (_fingerprintsLoaded)
        {
            return;
        }

        _fingerprintsLoaded = true;
        if (!Directory.Exists(_cacheDirectory))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(_cacheDirectory, "*.cache.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var entry = JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(file), JsonOptions);
                if (entry != null && !string.IsNullOrEmpty(entry.ModulePath))
                {
                    _entries[NormalizePath(entry.ModulePath)] = entry;
                }
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private void SaveEntry(string key)
    {
        Directory.CreateDirectory(_cacheDirectory);
        WriteTextAtomic(GetCacheFilePath(key), JsonSerializer.Serialize(_entries[key], JsonOptions));
    }

    private ModuleArtifactCacheStatus GetStatusCore()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return ModuleArtifactCacheStatus.Empty(_cacheDirectory);
        }

        var files = Directory.EnumerateFiles(_cacheDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(static file => !string.Equals(Path.GetFileName(file), LockFileName, StringComparison.Ordinal))
            .ToArray();
        var manifests = LoadManifestFiles()
            .Where(static item => ValidatePayloadFile(item.Manifest))
            .ToArray();
        var referencedPayloads = manifests
            .Select(static item => Path.GetFullPath(item.Manifest.PayloadPath))
            .ToHashSet(PathComparer);
        var payloadFiles = files.Where(IsPayloadFile).ToArray();
        var orphanPayloads = payloadFiles.Count(file => !referencedPayloads.Contains(Path.GetFullPath(file)));
        var timestamps = manifests.Select(static item => item.Manifest.TimestampUtc).ToArray();

        return new ModuleArtifactCacheStatus(
            _cacheDirectory,
            files.Length,
            manifests.Length,
            payloadFiles.Length,
            files.Count(static file => file.EndsWith(".cache.json", StringComparison.OrdinalIgnoreCase)),
            orphanPayloads,
            files.Sum(TryGetFileLength),
            timestamps.Length == 0 ? null : timestamps.Min(),
            timestamps.Length == 0 ? null : timestamps.Max());
    }

    private IEnumerable<ManifestFile> LoadManifestFiles()
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            yield break;
        }

        foreach (var manifestPath in Directory.EnumerateFiles(
                     _cacheDirectory,
                     "*.artifact.json",
                     SearchOption.TopDirectoryOnly))
        {
            ModuleArtifactManifest? manifest = null;
            try
            {
                manifest = JsonSerializer.Deserialize<ModuleArtifactManifest>(
                    File.ReadAllText(manifestPath),
                    JsonOptions);
            }
            catch (JsonException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            if (manifest != null &&
                string.Equals(manifest.SchemaVersion, ModuleArtifactManifest.CurrentSchemaVersion, StringComparison.Ordinal) &&
                IsPathWithinCache(manifest.PayloadPath))
            {
                yield return new ManifestFile(manifestPath, manifest);
            }
        }
    }

    private IEnumerable<string> EnumerateGarbageFiles(IReadOnlyList<ManifestFile> validManifests)
    {
        if (!Directory.Exists(_cacheDirectory))
        {
            return [];
        }

        var validManifestPaths = validManifests
            .Select(static item => Path.GetFullPath(item.ManifestPath))
            .ToHashSet(PathComparer);
        var referencedPayloads = validManifests
            .Select(static item => Path.GetFullPath(item.Manifest.PayloadPath))
            .ToHashSet(PathComparer);
        return Directory.EnumerateFiles(_cacheDirectory, "*", SearchOption.TopDirectoryOnly)
            .Where(file => IsTemporaryFile(file) ||
                           (file.EndsWith(".artifact.json", StringComparison.OrdinalIgnoreCase) &&
                            !validManifestPaths.Contains(Path.GetFullPath(file))) ||
                           (IsPayloadFile(file) && !referencedPayloads.Contains(Path.GetFullPath(file))))
            .ToArray();
    }

    private FileStream AcquireProcessLock()
    {
        Directory.CreateDirectory(_cacheDirectory);
        var lockPath = Path.Combine(_cacheDirectory, LockFileName);
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.WriteThrough);
            }
            catch (IOException) when (stopwatch.Elapsed < LockTimeout)
            {
                Thread.Sleep(10);
            }
        }
    }

    private string GetPayloadPath(ModuleArtifactKey key, string kind, string payloadExtension) =>
        Path.Combine(
            _cacheDirectory,
            $"{key.StableHash()}.{NormalizeArtifactKind(kind)}{payloadExtension}");

    private string GetCacheFilePath(string key) =>
        Path.Combine(_cacheDirectory, $"{ModuleArtifactHash.ComputeTextHash(key)}.cache.json");

    private string GetArtifactManifestPath(ModuleArtifactKey key, string kind) =>
        Path.Combine(
            _cacheDirectory,
            $"{key.StableHash()}.{NormalizeArtifactKind(kind)}.artifact.json");

    private bool IsPathWithinCache(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var fullPath = Path.GetFullPath(path);
            var relative = Path.GetRelativePath(_cacheDirectory, fullPath);
            return !Path.IsPathRooted(relative) &&
                   relative != ".." &&
                   !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
                   !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    private static bool IsLatestKey(ModuleArtifactKey key) =>
        string.Equals(key.SourceHash, "latest", StringComparison.Ordinal) ||
        string.Equals(key.DependencySignatureHash, "latest", StringComparison.Ordinal);

    private static bool IsPayloadFile(string file) =>
        !file.EndsWith(".artifact.json", StringComparison.OrdinalIgnoreCase) &&
        !file.EndsWith(".cache.json", StringComparison.OrdinalIgnoreCase) &&
        !string.Equals(Path.GetFileName(file), LockFileName, StringComparison.Ordinal) &&
        !IsTemporaryFile(file);

    private static bool IsTemporaryFile(string file) =>
        Path.GetFileName(file).Contains(".tmp-", StringComparison.Ordinal);

    private static long DeleteFileAndMeasure(string path, ref int deletedFiles)
    {
        if (!File.Exists(path))
        {
            return 0;
        }

        var length = TryGetFileLength(path);
        File.Delete(path);
        deletedFiles++;
        return length;
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void WriteTextAtomic(string destinationPath, string content)
    {
        var tempPath = CreateTemporaryPath(destinationPath);
        try
        {
            using (var stream = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan | FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(content);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            ReplaceFileAtomic(tempPath, destinationPath);
        }
        finally
        {
            DeleteFileIfExists(tempPath);
        }
    }

    private static void CopyFileAtomic(string sourcePath, string destinationPath)
    {
        var tempPath = CreateTemporaryPath(destinationPath);
        try
        {
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var destination = new FileStream(
                       tempPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       bufferSize: 64 * 1024,
                       FileOptions.SequentialScan | FileOptions.WriteThrough))
            {
                source.CopyTo(destination);
                destination.Flush(flushToDisk: true);
            }

            ReplaceFileAtomic(tempPath, destinationPath);
        }
        finally
        {
            DeleteFileIfExists(tempPath);
        }
    }

    private static void ReplaceFileAtomic(string tempPath, string destinationPath)
    {
        var stopwatch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                File.Move(tempPath, destinationPath, overwrite: true);
                return;
            }
            catch (IOException) when (stopwatch.Elapsed < TimeSpan.FromSeconds(1))
            {
                Thread.Sleep(10);
            }
        }
    }

    private static string CreateTemporaryPath(string destinationPath) =>
        $"{destinationPath}.tmp-{Environment.ProcessId}-{Guid.NewGuid():N}";

    private static string ComputeFileHash(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static long TryGetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (IOException)
        {
            return 0;
        }
        catch (UnauthorizedAccessException)
        {
            return 0;
        }
    }

    private static DateTime TryGetLastWriteTimeUtc(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    private static string NormalizeArtifactKind(string kind)
    {
        var builder = new StringBuilder(kind.Length);
        foreach (var ch in kind)
        {
            builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
        }

        return builder.Length == 0 ? "artifact" : builder.ToString();
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();
        }
        catch
        {
            return path.Replace('\\', '/').ToLowerInvariant();
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private sealed record ManifestFile(string ManifestPath, ModuleArtifactManifest Manifest);

    private sealed class CacheEntry
    {
        public string ModulePath { get; set; } = "";
        public ModuleFingerprint Fingerprint { get; set; } = null!;
    }
}

public sealed record ModuleArtifactCacheStatus(
    string CacheDirectory,
    int TotalFiles,
    int ArtifactManifests,
    int PayloadFiles,
    int FingerprintEntries,
    int OrphanPayloadFiles,
    long TotalBytes,
    DateTime? OldestArtifactUtc,
    DateTime? NewestArtifactUtc)
{
    public static ModuleArtifactCacheStatus Empty(string cacheDirectory) =>
        new(cacheDirectory, 0, 0, 0, 0, 0, 0, null, null);
}

public sealed record ModuleArtifactCacheClearResult(int DeletedFiles, long DeletedBytes);

public sealed record ModuleArtifactCachePruneResult(
    long BytesBefore,
    long BytesAfter,
    int DeletedFiles,
    long DeletedBytes);
