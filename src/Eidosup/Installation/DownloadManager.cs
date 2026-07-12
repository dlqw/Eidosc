using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Eidosup.Diagnostics;
using Eidosup.Distribution;

namespace Eidosup.Installation;

public sealed record DownloadProgress(string AssetName, long BytesReceived, long? TotalBytes, bool Resumed);

public sealed record DownloadResult(string Path, string Sha256, long Size, bool CacheHit, bool Resumed);

public sealed class DownloadManager : IDisposable
{
    private const int DefaultAttempts = 3;
    private const long DefaultMaximumArtifactBytes = 2L * 1024 * 1024 * 1024;
    private const int MaximumChecksumBytes = 1024 * 1024;

    private readonly HttpClient _httpClient;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly bool _ownsHttpClient;

    public DownloadManager(
        HttpClient? httpClient = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _delay = delay ?? Task.Delay;
        _ownsHttpClient = httpClient == null;
    }

    public async Task<string> DownloadChecksumManifestAsync(
        EidosReleaseAsset asset,
        CancellationToken cancellationToken)
    {
        ValidateAsset(asset, MaximumChecksumBytes);
        Exception? lastException = null;
        for (var attempt = 1; attempt <= DefaultAttempts; attempt++)
        {
            try
            {
                using var request = CreateRequest(asset.DownloadUrl);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                EnsureDownloadSuccess(response, asset.Name);
                if (response.Content.Headers.ContentLength is > MaximumChecksumBytes)
                {
                    throw IntegrityFailure($"Checksum asset '{asset.Name}' exceeds {MaximumChecksumBytes} bytes.");
                }

                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                using var buffer = new MemoryStream();
                try
                {
                    await CopyBoundedAsync(source, buffer, MaximumChecksumBytes, cancellationToken);
                }
                catch (IOException exception)
                {
                    throw new RetryableDownloadException("The checksum response stream was interrupted.", exception);
                }
                if (buffer.Length != asset.Size)
                {
                    throw new RetryableDownloadException(
                        $"Checksum asset '{asset.Name}' did not match its declared size.");
                }

                return new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                    .GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                lastException = exception;
                if (attempt < DefaultAttempts)
                {
                    await _delay(RetryDelay(attempt), cancellationToken);
                }
            }
            catch (DecoderFallbackException exception)
            {
                throw IntegrityFailure($"Checksum asset '{asset.Name}' is not valid UTF-8.", exception);
            }
        }

        throw NetworkFailure(asset.Name, lastException);
    }

    public async Task<DownloadResult> DownloadArtifactAsync(
        EidosReleaseAsset asset,
        string cacheDirectory,
        string expectedSha256,
        CancellationToken cancellationToken,
        IProgress<DownloadProgress>? progress = null,
        long maximumArtifactBytes = DefaultMaximumArtifactBytes)
    {
        ValidateAsset(asset, maximumArtifactBytes);
        if (!ChecksumManifest.IsSha256(expectedSha256))
        {
            throw new ArgumentException("Expected SHA-256 must contain exactly 64 hexadecimal characters.", nameof(expectedSha256));
        }

        expectedSha256 = expectedSha256.ToLowerInvariant();
        var contentDirectory = Path.Combine(cacheDirectory, expectedSha256[..2]);
        var finalPath = Path.Combine(contentDirectory, expectedSha256);
        var partialPath = finalPath + ".partial";
        Directory.CreateDirectory(contentDirectory);

        if (File.Exists(finalPath))
        {
            var cached = await VerifyFileAsync(finalPath, expectedSha256, asset.Size, cancellationToken);
            if (cached.Valid)
            {
                return new DownloadResult(finalPath, expectedSha256, cached.Size, CacheHit: true, Resumed: false);
            }

            File.Delete(finalPath);
        }

        Exception? lastException = null;
        var resumed = false;
        for (var attempt = 1; attempt <= DefaultAttempts; attempt++)
        {
            try
            {
                resumed |= await DownloadAttemptAsync(
                    asset,
                    partialPath,
                    maximumArtifactBytes,
                    progress,
                    cancellationToken);
                var verification = await VerifyFileAsync(partialPath, expectedSha256, asset.Size, cancellationToken);
                if (!verification.Valid)
                {
                    File.Delete(partialPath);
                    if (attempt < DefaultAttempts)
                    {
                        lastException = IntegrityFailure(
                            $"Downloaded asset '{asset.Name}' did not match its declared size or SHA-256 checksum.");
                        await _delay(RetryDelay(attempt), cancellationToken);
                        continue;
                    }

                    throw IntegrityFailure(
                        $"Downloaded asset '{asset.Name}' did not match its declared size or SHA-256 checksum.");
                }

                File.Move(partialPath, finalPath, overwrite: true);
                return new DownloadResult(finalPath, expectedSha256, verification.Size, CacheHit: false, resumed);
            }
            catch (EidosupException exception) when (exception.Code == EidosupErrorCode.IntegrityFailure)
            {
                File.Delete(partialPath);
                throw;
            }
            catch (Exception exception) when (IsTransient(exception, cancellationToken))
            {
                lastException = exception;
                if (attempt < DefaultAttempts)
                {
                    await _delay(RetryDelay(attempt), cancellationToken);
                }
            }
        }

        throw lastException is EidosupException known
            ? known
            : NetworkFailure(asset.Name, lastException);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<bool> DownloadAttemptAsync(
        EidosReleaseAsset asset,
        string partialPath,
        long maximumArtifactBytes,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var existingLength = File.Exists(partialPath) ? new FileInfo(partialPath).Length : 0;
        if (existingLength > maximumArtifactBytes || asset.Size is { } declaredSize && existingLength > declaredSize)
        {
            File.Delete(partialPath);
            existingLength = 0;
        }

        using var request = CreateRequest(asset.DownloadUrl);
        if (existingLength > 0)
        {
            request.Headers.Range = new RangeHeaderValue(existingLength, null);
        }

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existingLength > 0)
        {
            File.Delete(partialPath);
            throw new RetryableDownloadException("The server rejected the partial download range.");
        }

        EnsureDownloadSuccess(response, asset.Name);
        var append = existingLength > 0 && response.StatusCode == HttpStatusCode.PartialContent;
        if (append && response.Content.Headers.ContentRange?.From != existingLength)
        {
            File.Delete(partialPath);
            throw new RetryableDownloadException("The server returned an unexpected content range.");
        }

        if (!append)
        {
            existingLength = 0;
        }

        var responseLength = response.Content.Headers.ContentLength;
        if (responseLength is { } length && existingLength + length > maximumArtifactBytes)
        {
            throw IntegrityFailure($"Asset '{asset.Name}' exceeds the configured download size limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(
            partialPath,
            append ? FileMode.Append : FileMode.Create,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[128 * 1024];
        var total = existingLength;
        while (true)
        {
            int read;
            try
            {
                read = await source.ReadAsync(buffer, cancellationToken);
            }
            catch (IOException exception)
            {
                throw new RetryableDownloadException("The artifact response stream was interrupted.", exception);
            }
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > maximumArtifactBytes)
            {
                throw IntegrityFailure($"Asset '{asset.Name}' exceeds the configured download size limit.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            var totalLength = asset.Size ?? (responseLength is { } contentLength ? existingLength + contentLength : null);
            progress?.Report(new DownloadProgress(asset.Name, total, totalLength, append));
        }

        await destination.FlushAsync(cancellationToken);
        destination.Flush(flushToDisk: true);
        return append;
    }

    private static async Task<(bool Valid, long Size)> VerifyFileAsync(
        string path,
        string expectedSha256,
        long? expectedSize,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (expectedSize is { } size && info.Length != size)
        {
            return (false, info.Length);
        }

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var digest = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
        return (string.Equals(digest, expectedSha256, StringComparison.Ordinal), info.Length);
    }

    private static HttpRequestMessage CreateRequest(string downloadUrl)
    {
        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri) ||
            !ReleaseTransportPolicy.IsAllowedAssetUri(uri))
        {
            throw new ArgumentException(
                "Release asset URLs must use absolute HTTPS URLs (or the configured loopback release test server).",
                nameof(downloadUrl));
        }

        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("eidosup", "0.1"));
        return request;
    }

    private static void ValidateAsset(EidosReleaseAsset asset, long maximumBytes)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (asset.Size is null or < 0 || asset.Size > maximumBytes)
        {
            throw IntegrityFailure($"Asset '{asset.Name}' is missing a valid bounded size declaration.");
        }

        using var request = CreateRequest(asset.DownloadUrl);
    }

    private static void EnsureDownloadSuccess(HttpResponseMessage response, string assetName)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        if (response.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
        {
            throw new EidosupException(
                EidosupErrorCode.NetworkFailure,
                EidosupExitCodes.NetworkFailure,
                $"The proxy rejected asset '{assetName}' with HTTP 407 (ProxyAuthenticationRequired).",
                "Configure credentials for the active HTTP proxy, then retry the operation.");
        }

        if (response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int)response.StatusCode >= 500)
        {
            throw new RetryableDownloadException(
                $"Asset '{assetName}' returned HTTP {(int)response.StatusCode} ({response.StatusCode}).");
        }

        throw new EidosupException(
            EidosupErrorCode.NetworkFailure,
            EidosupExitCodes.NetworkFailure,
            $"Asset '{assetName}' returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
            "Confirm the release asset is public and retry the operation.");
    }

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        var total = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            total += read;
            if (total > maximumBytes)
            {
                throw IntegrityFailure($"Checksum asset exceeds {maximumBytes} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static bool IsTransient(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is HttpRequestException or RetryableDownloadException or TaskCanceledException;

    private static TimeSpan RetryDelay(int attempt) => TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt - 1));

    private static EidosupException IntegrityFailure(string message, Exception? innerException = null) => new(
        EidosupErrorCode.IntegrityFailure,
        EidosupExitCodes.IntegrityFailure,
        message,
        "Do not use the downloaded file; retry from a trusted release source.",
        innerException);

    private static EidosupException NetworkFailure(string assetName, Exception? innerException) => new(
        EidosupErrorCode.NetworkFailure,
        EidosupExitCodes.NetworkFailure,
        $"Downloading asset '{assetName}' failed after {DefaultAttempts} attempts.",
        "Check the network connection and proxy, then retry the operation.",
        innerException);

    private sealed class RetryableDownloadException : IOException
    {
        public RetryableDownloadException(string message, Exception? innerException = null)
            : base(message, innerException)
        {
        }
    }
}
