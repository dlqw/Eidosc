using System.Text.Json;
using System.Security.Cryptography;
using Eidosup.Serialization;
using Eidosup.Diagnostics;
using Eidosup.Toolchains;

namespace Eidosup.Installation;

public sealed record InstallManifest(
    int Schema,
    string ToolchainId,
    string ManifestSha256,
    string ReleaseTag,
    string Version,
    string Rid,
    string Source,
    string AssetName,
    string AssetSha256,
    long AssetSize,
    DateTimeOffset InstalledAt,
    IReadOnlyList<InstalledFile> Files)
{
    public const int CurrentSchema = 2;
    public const string FileName = ".eidosup-install.json";

    private const string CompilerGrammarCachePath = "cache/grammar.bin";

    public async Task WriteAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, FileName);
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 16 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                this,
                EidosupJsonContext.Default.InstallManifest,
                cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, path, overwrite: true);
    }

    public static async Task<InstallManifest?> TryReadAsync(string directory, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var manifest = await JsonSerializer.DeserializeAsync(
                stream,
                EidosupJsonContext.Default.InstallManifest,
                cancellationToken);
            return manifest is { Schema: CurrentSchema } ? manifest : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public async Task<bool> VerifyAsync(
        string directory,
        string expectedSha256,
        CancellationToken cancellationToken,
        string? expectedRid = null,
        string? expectedVersion = null)
    {
        if (Schema != CurrentSchema ||
            !HasValidIdentity(directory) ||
            !string.Equals(AssetSha256, expectedSha256, StringComparison.Ordinal) ||
            expectedRid != null && !string.Equals(Rid, expectedRid, StringComparison.Ordinal) ||
            expectedVersion != null && !string.Equals(Version, expectedVersion, StringComparison.Ordinal) ||
            Files is not { Count: > 0 })
        {
            return false;
        }

        var root = Path.GetFullPath(directory);
        if (!Directory.Exists(root) ||
            (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            return false;
        }

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        var expectedFiles = new HashSet<string>(pathComparer);
        foreach (var file in Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file is null ||
                file.Size < 0 ||
                !ChecksumManifest.IsSha256(file.Sha256) ||
                !TryNormalizeRelativePath(file.Path, out var relativePath) ||
                !expectedFiles.Add(relativePath))
            {
                return false;
            }

            var path = Path.GetFullPath(Path.Combine(
                root,
                relativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!ToolInstallLayout.IsWithin(root, path) ||
                !File.Exists(path) ||
                (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return false;
            }

            var info = new FileInfo(path);
            if (info.Length != file.Size)
            {
                return false;
            }

            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 1024 * 1024,
                FileOptions.SequentialScan);
            var digest = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            cancellationToken.ThrowIfCancellationRequested();
            if (!string.Equals(digest, file.Sha256, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return TryCollectInstalledFiles(root, pathComparer, out var actualFiles) &&
               expectedFiles.SetEquals(actualFiles);
    }

    public bool HasValidIdentity(string directory)
    {
        try
        {
            var identity = ToolchainIdentity.Create(
                Version,
                Rid,
                Source,
                ReleaseTag,
                AssetName,
                AssetSha256,
                AssetSize);
            var directoryName = Path.GetFileName(
                Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return string.Equals(identity.Id, ToolchainId, StringComparison.Ordinal) &&
                   string.Equals(identity.ManifestSha256, ManifestSha256, StringComparison.Ordinal) &&
                   string.Equals(directoryName, ToolchainId, StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    private static bool TryNormalizeRelativePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(path) ||
            path.Contains('\\') ||
            path.Contains(':') ||
            path.IndexOf('\0') >= 0 ||
            path.StartsWith('/') ||
            path.EndsWith('/'))
        {
            return false;
        }

        var segments = path.Split('/');
        if (segments.Any(static segment =>
                string.IsNullOrWhiteSpace(segment) ||
                segment is "." or ".." ||
                segment.Any(char.IsControl)))
        {
            return false;
        }

        normalized = string.Join('/', segments);
        return !string.Equals(normalized, FileName, StringComparison.Ordinal);
    }

    private static bool TryCollectInstalledFiles(
        string root,
        StringComparer pathComparer,
        out HashSet<string> files)
    {
        files = new HashSet<string>(pathComparer);
        var directories = new Stack<string>();
        directories.Push(root);
        while (directories.TryPop(out var directory))
        {
            foreach (var childDirectory in Directory.EnumerateDirectories(directory))
            {
                if ((File.GetAttributes(childDirectory) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                directories.Push(childDirectory);
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                var relativePath = Path.GetRelativePath(root, file)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (string.Equals(relativePath, FileName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (string.Equals(relativePath, CompilerGrammarCachePath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!TryNormalizeRelativePath(relativePath, out var normalized) ||
                    !files.Add(normalized))
                {
                    return false;
                }
            }
        }

        return true;
    }

    public static EidosupException Conflict(string targetDirectory) => new(
        EidosupErrorCode.InstallConflict,
        EidosupExitCodes.InstallConflict,
        $"Install target '{targetDirectory}' already contains a different or unverifiable toolchain.",
        "Choose another version, remove the unmanaged directory, or use --force to replace it transactionally.");
}
