using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Eidosup.Diagnostics;
using Eidosup.Installation;

namespace Eidosup.Distribution;

public sealed record OfflineBundleImportResult(string Id, string Directory, string Source);

public sealed class OfflineBundleManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow
    };

    public async Task<OfflineBundleImportResult> ImportAsync(
        ToolInstallLayout layout,
        string bundlePath,
        CancellationToken cancellationToken)
    {
        var sourcePath = Path.GetFullPath(bundlePath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Offline bundle ZIP does not exist.", sourcePath);
        }

        var imports = Path.Combine(layout.DownloadDirectory, "offline");
        Directory.CreateDirectory(imports);
        string archiveSha;
        await using (var archiveStream = File.OpenRead(sourcePath))
        {
            archiveSha = Convert.ToHexString(await SHA256.HashDataAsync(archiveStream, cancellationToken)).ToLowerInvariant();
        }

        var target = Path.Combine(imports, archiveSha);
        if (!Directory.Exists(target))
        {
            var staging = target + $".{Guid.NewGuid():N}.tmp";
            Directory.CreateDirectory(staging);
            try
            {
                await new SafeZipExtractor().ExtractAsync(sourcePath, staging, cancellationToken);
                await VerifyBundleAsync(layout, staging, $"offline:{target}", cancellationToken);
                Directory.Move(staging, target);
            }
            finally
            {
                if (Directory.Exists(staging))
                {
                    Directory.Delete(staging, recursive: true);
                }
            }
        }
        else
        {
            await VerifyBundleAsync(layout, target, $"offline:{target}", cancellationToken);
        }

        Touch(target);

        return new OfflineBundleImportResult(archiveSha, target, $"offline:{target}");
    }

    public Task ExportAsync(
        ToolInstallLayout layout,
        string id,
        string outputPath,
        CancellationToken cancellationToken)
    {
        if (!ChecksumManifest.IsSha256(id))
        {
            throw new FormatException("An imported offline bundle ID must be a lowercase SHA-256 digest.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var source = Path.Combine(layout.DownloadDirectory, "offline", id);
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"Imported offline bundle '{id}' does not exist.");
        }

        var output = Path.GetFullPath(outputPath);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var temporary = output + $".{Guid.NewGuid():N}.tmp";
        try
        {
            ZipFile.CreateFromDirectory(source, temporary, CompressionLevel.Optimal, includeBaseDirectory: false);
            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }

        return Task.CompletedTask;
    }

    private static async Task VerifyBundleAsync(
        ToolInstallLayout layout,
        string root,
        string sourceIdentity,
        CancellationToken cancellationToken)
    {
        var indexPath = Path.Combine(root, "index.json");
        if (!File.Exists(indexPath))
        {
            throw Invalid("Offline bundle does not contain index.json.");
        }
        if (new FileInfo(indexPath).Length > SignedIndexReleaseSource.MaximumIndexBytes)
        {
            throw Invalid($"Offline bundle index.json exceeds {SignedIndexReleaseSource.MaximumIndexBytes} bytes.");
        }

        await using var stream = File.OpenRead(indexPath);
        var envelope = await JsonSerializer.DeserializeAsync<SignedReleaseIndexEnvelope>(stream, JsonOptions, cancellationToken)
                       ?? throw Invalid("Offline bundle index.json is empty.");
        await new SignedReleaseIndexVerifier(new MetadataTrustStore(layout.StateDirectory))
            .VerifyAsync(sourceIdentity, envelope, cancellationToken);
        foreach (var asset in envelope.Payload.Releases.SelectMany(static release => release.Assets))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Uri.TryCreate(asset.Url, UriKind.Relative, out var relative) ||
                asset.Url.Contains('\\') || asset.Url.StartsWith('/') || asset.Url.Split('/').Any(static part => part is "" or "." or ".."))
            {
                throw Invalid($"Offline asset URL '{asset.Url}' is not a safe relative path.");
            }

            var path = Path.GetFullPath(Path.Combine(root, Uri.UnescapeDataString(relative.ToString()).Replace('/', Path.DirectorySeparatorChar)));
            if (!ToolInstallLayout.IsWithin(root, path) || !File.Exists(path) || new FileInfo(path).Length != asset.Size)
            {
                throw Invalid($"Offline asset '{asset.Name}' is missing or has an unexpected size.");
            }

            await using var assetStream = File.OpenRead(path);
            var digest = Convert.ToHexString(await SHA256.HashDataAsync(assetStream, cancellationToken)).ToLowerInvariant();
            if (!string.Equals(digest, asset.Sha256, StringComparison.Ordinal))
            {
                throw Invalid($"Offline asset '{asset.Name}' failed SHA-256 verification.");
            }
        }
    }

    private static EidosupException Invalid(string message) => new(
        EidosupErrorCode.InvalidReleaseMetadata,
        EidosupExitCodes.InvalidRelease,
        message,
        "Rebuild the complete offline bundle from signed metadata and verified assets.");

    private static void Touch(string path)
    {
        try
        {
            Directory.SetLastAccessTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
