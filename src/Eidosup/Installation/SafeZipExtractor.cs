using System.IO.Compression;
using System.Security.Cryptography;
using Eidosup.Diagnostics;

namespace Eidosup.Installation;

public sealed record InstalledFile(string Path, long Size, string Sha256);

public sealed record ArchiveExtractionResult(IReadOnlyList<InstalledFile> Files, long TotalBytes);

public sealed class SafeZipExtractor
{
    private const int MaximumEntries = 100_000;
    private const long MaximumTotalBytes = 4L * 1024 * 1024 * 1024;
    private const long MaximumFileBytes = 2L * 1024 * 1024 * 1024;
    private const double MaximumCompressionRatio = 1_000;
    private const int MaximumRelativePathCharacters = 1_024;
    private const int MaximumPathSegmentCharacters = 255;

    public async Task<ArchiveExtractionResult> ExtractAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(destinationRoot);
        var destinationPrefix = destinationRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var installedFiles = new List<InstalledFile>();
        var paths = new HashSet<string>(OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);
        long totalBytes = 0;

        await using var archiveStream = new FileStream(
            archivePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumEntries)
        {
            throw Unsafe($"Archive contains more than {MaximumEntries} entries.");
        }

        var entries = ValidateEntries(archive, destinationRoot, destinationPrefix, comparison, paths);
        foreach (var item in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = item.Entry;
            if (item.IsDirectory)
            {
                Directory.CreateDirectory(item.TargetPath);
                continue;
            }

            if (entry.Length < 0 || entry.Length > MaximumFileBytes)
            {
                throw Unsafe($"Archive entry '{entry.FullName}' exceeds the per-file size limit.");
            }

            if (entry.Length > MaximumTotalBytes - totalBytes)
            {
                throw Unsafe("Archive exceeds the total uncompressed size limit.");
            }
            totalBytes += entry.Length;

            if (entry.Length > 0 &&
                (entry.CompressedLength == 0 || (double)entry.Length / entry.CompressedLength > MaximumCompressionRatio))
            {
                throw Unsafe($"Archive entry '{entry.FullName}' exceeds the compression-ratio limit.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(item.TargetPath)!);
            await using var source = entry.Open();
            await using var destination = new FileStream(
                item.TargetPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[128 * 1024];
            long written = 0;
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                written = checked(written + read);
                if (written > entry.Length || written > MaximumFileBytes)
                {
                    throw Unsafe($"Archive entry '{entry.FullName}' expanded beyond its declared size.");
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            if (written != entry.Length)
            {
                throw Unsafe($"Archive entry '{entry.FullName}' did not match its declared size.");
            }

            await destination.FlushAsync(cancellationToken);
            destination.Flush(flushToDisk: true);
            installedFiles.Add(new InstalledFile(
                item.RelativePath.Replace(Path.DirectorySeparatorChar, '/'),
                written,
                Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant()));
        }

        return new ArchiveExtractionResult(
            installedFiles.OrderBy(static file => file.Path, StringComparer.Ordinal).ToArray(),
            totalBytes);
    }

    private static IReadOnlyList<ValidatedEntry> ValidateEntries(
        ZipArchive archive,
        string destinationRoot,
        string destinationPrefix,
        StringComparison comparison,
        HashSet<string> paths)
    {
        var entries = new List<ValidatedEntry>(archive.Entries.Count);
        var filePaths = new HashSet<string>(paths.Comparer);
        foreach (var entry in archive.Entries)
        {
            RejectSpecialEntry(entry);
            var relativePath = NormalizeEntryPath(entry.FullName);
            if (string.Equals(
                    relativePath,
                    InstallManifest.FileName,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw Unsafe($"Archive entry '{entry.FullName}' uses a reserved installation path.");
            }

            if (!paths.Add(relativePath))
            {
                throw Unsafe($"Archive contains duplicate path '{entry.FullName}'.");
            }

            var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, relativePath));
            if (!targetPath.StartsWith(destinationPrefix, comparison))
            {
                throw Unsafe($"Archive entry '{entry.FullName}' escapes the staging directory.");
            }

            var isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\') || entry.Name.Length == 0;
            if (!isDirectory)
            {
                filePaths.Add(relativePath);
            }

            entries.Add(new ValidatedEntry(entry, relativePath, targetPath, isDirectory));
        }

        foreach (var path in paths)
        {
            var parent = Path.GetDirectoryName(path);
            while (!string.IsNullOrEmpty(parent))
            {
                if (filePaths.Contains(parent))
                {
                    throw Unsafe($"Archive path '{path}' is nested below file '{parent}'.");
                }

                parent = Path.GetDirectoryName(parent);
            }
        }

        return entries;
    }

    private static string NormalizeEntryPath(string entryPath)
    {
        if (string.IsNullOrWhiteSpace(entryPath) || entryPath.IndexOf('\0') >= 0)
        {
            throw Unsafe("Archive contains an empty or invalid entry path.");
        }

        var normalized = entryPath.Replace('\\', '/');
        if (normalized.Length > MaximumRelativePathCharacters)
        {
            throw Unsafe($"Archive entry '{entryPath}' exceeds the path-length limit.");
        }

        if (normalized.StartsWith('/') || normalized.Contains(':'))
        {
            throw Unsafe($"Archive entry '{entryPath}' uses an absolute path.");
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(static segment =>
                segment is "." or ".." ||
                segment.Length > MaximumPathSegmentCharacters ||
                segment.Any(char.IsControl) ||
                OperatingSystem.IsWindows() && IsUnsafeWindowsSegment(segment)))
        {
            throw Unsafe($"Archive entry '{entryPath}' contains an unsafe path segment.");
        }

        return Path.Combine(segments);
    }

    private static bool IsUnsafeWindowsSegment(string segment)
    {
        if (segment.EndsWith('.') ||
            segment.EndsWith(' ') ||
            segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            return true;
        }

        var stem = segment.Split('.')[0];
        return stem.Equals("CON", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("PRN", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("AUX", StringComparison.OrdinalIgnoreCase) ||
               stem.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
               stem.Length == 4 &&
               (stem.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                stem.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
               stem[3] is >= '1' and <= '9';
    }

    private static void RejectSpecialEntry(ZipArchiveEntry entry)
    {
        const int UnixFileTypeMask = 0xF000;
        const int UnixDirectory = 0x4000;
        const int UnixRegularFile = 0x8000;
        const int UnixSymbolicLink = 0xA000;
        const int WindowsReparsePoint = 0x0400;
        var unixMode = entry.ExternalAttributes >> 16;
        if ((unixMode & UnixFileTypeMask) == UnixSymbolicLink)
        {
            throw Unsafe($"Archive entry '{entry.FullName}' is a symbolic link.");
        }

        var fileType = unixMode & UnixFileTypeMask;
        if (fileType is not 0 and not UnixDirectory and not UnixRegularFile ||
            (entry.ExternalAttributes & WindowsReparsePoint) != 0)
        {
            throw Unsafe($"Archive entry '{entry.FullName}' uses an unsupported special file type.");
        }
    }

    private static EidosupException Unsafe(string message) => new(
        EidosupErrorCode.UnsafeArchive,
        EidosupExitCodes.UnsafeArchive,
        message,
        "Do not install this archive; use a trusted release with a safe bundle layout.");

    private sealed record ValidatedEntry(
        ZipArchiveEntry Entry,
        string RelativePath,
        string TargetPath,
        bool IsDirectory);
}
