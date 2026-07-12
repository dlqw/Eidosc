using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

[Collection(EidosupEnvironmentTestCollection.Name)]
public sealed class DownloadManagerTests
{
    [Fact]
    public async Task DownloadArtifactAsync_AllowsOnlyConfiguredLoopbackTestOrigin()
    {
        const string variable = "EIDOSUP_TEST_RELEASE_SERVER";
        var previous = Environment.GetEnvironmentVariable(variable);
        using var temporary = new TemporaryDirectory();
        var payload = "candidate"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        try
        {
            Environment.SetEnvironmentVariable(variable, "http://127.0.0.1:43129/");
            using var httpClient = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            }));
            using var manager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);

            var result = await manager.DownloadArtifactAsync(
                new EidosReleaseAsset("asset.zip", "http://127.0.0.1:43129/assets/asset.zip", payload.Length),
                temporary.Path,
                hash,
                CancellationToken.None);

            Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path));
            await Assert.ThrowsAsync<ArgumentException>(() => manager.DownloadArtifactAsync(
                new EidosReleaseAsset("other.zip", "http://127.0.0.1:43130/assets/other.zip", payload.Length),
                temporary.Path,
                hash,
                CancellationToken.None));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, previous);
        }
    }

    [Fact]
    public async Task DownloadArtifactAsync_ResumesPartialAndThenUsesVerifiedCache()
    {
        using var temporary = new TemporaryDirectory();
        var payload = "abcdef"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var contentDirectory = Path.Combine(temporary.Path, hash[..2]);
        Directory.CreateDirectory(contentDirectory);
        await File.WriteAllBytesAsync(Path.Combine(contentDirectory, hash + ".partial"), payload[..3]);
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestCount++;
            Assert.Equal(3, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[3..])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        }));
        using var manager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        var asset = new EidosReleaseAsset("asset.zip", "https://example.invalid/asset.zip", payload.Length);

        var first = await manager.DownloadArtifactAsync(asset, temporary.Path, hash, CancellationToken.None);
        var second = await manager.DownloadArtifactAsync(asset, temporary.Path, hash, CancellationToken.None);

        Assert.True(first.Resumed);
        Assert.False(first.CacheHit);
        Assert.True(second.CacheHit);
        Assert.Equal(1, requestCount);
        Assert.Equal(payload, await File.ReadAllBytesAsync(first.Path));
    }

    [Fact]
    public async Task DownloadArtifactAsync_RejectsCorruptionBeforeCacheCommit()
    {
        using var temporary = new TemporaryDirectory();
        var expected = "abcdef"u8.ToArray();
        var corrupt = "abcdeg"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(expected)).ToLowerInvariant();
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(corrupt)
            };
        }));
        using var manager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        var asset = new EidosReleaseAsset("asset.zip", "https://example.invalid/asset.zip", corrupt.Length);

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            manager.DownloadArtifactAsync(asset, temporary.Path, hash, CancellationToken.None));

        Assert.Equal(EidosupErrorCode.IntegrityFailure, exception.Code);
        Assert.Equal(3, requestCount);
        Assert.Empty(Directory.EnumerateFiles(temporary.Path, hash, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DownloadArtifactAsync_DiscardsPollutedCacheBeforeUse()
    {
        using var temporary = new TemporaryDirectory();
        var payload = "verified"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var contentDirectory = Path.Combine(temporary.Path, hash[..2]);
        Directory.CreateDirectory(contentDirectory);
        await File.WriteAllTextAsync(Path.Combine(contentDirectory, hash), "polluted");
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
        }));
        using var manager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        var asset = new EidosReleaseAsset("asset.zip", "https://example.invalid/asset.zip", payload.Length);

        var result = await manager.DownloadArtifactAsync(asset, temporary.Path, hash, CancellationToken.None);

        Assert.False(result.CacheHit);
        Assert.Equal(1, requestCount);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path));
    }

    [Fact]
    public async Task DownloadArtifactAsync_RetriesInterruptedStreamFromPartialOffset()
    {
        using var temporary = new TemporaryDirectory();
        var payload = "abcdef"u8.ToArray();
        var hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            requestCount++;
            if (requestCount == 1)
            {
                var content = new StreamContent(new InterruptingStream(payload[..3]));
                content.Headers.ContentLength = payload.Length;
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
            }

            Assert.Equal(3, request.Headers.Range?.Ranges.Single().From);
            var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = new ByteArrayContent(payload[3..])
            };
            response.Content.Headers.ContentRange = new ContentRangeHeaderValue(3, 5, 6);
            return response;
        }));
        using var manager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);
        var asset = new EidosReleaseAsset("asset.zip", "https://example.invalid/asset.zip", payload.Length);

        var result = await manager.DownloadArtifactAsync(asset, temporary.Path, hash, CancellationToken.None);

        Assert.True(result.Resumed);
        Assert.Equal(2, requestCount);
        Assert.Equal(payload, await File.ReadAllBytesAsync(result.Path));
    }

    [Fact]
    public async Task DownloadChecksumManifestAsync_RetriesTransientServerFailure()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return requestCount == 1
                ? new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("abc")
                };
        }));
        using var manager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);

        var content = await manager.DownloadChecksumManifestAsync(
            new EidosReleaseAsset("SHA256SUMS", "https://example.invalid/SHA256SUMS", 3),
            CancellationToken.None);

        Assert.Equal("abc", content);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task DownloadArtifactAsync_RejectsMissingDeclaredSizeBeforeRequest()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            requestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }));
        using var manager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            manager.DownloadArtifactAsync(
                new EidosReleaseAsset("asset.zip", "https://example.invalid/asset.zip"),
                Path.GetTempPath(),
                new string('0', 64),
                CancellationToken.None));

        Assert.Equal(EidosupErrorCode.IntegrityFailure, exception.Code);
        Assert.Equal(0, requestCount);
    }

    [Fact]
    public async Task DownloadChecksumManifestAsync_ClassifiesProxyAuthenticationFailure()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ProxyAuthenticationRequired)));
        using var manager = new DownloadManager(httpClient, static (_, _) => Task.CompletedTask);

        var exception = await Assert.ThrowsAsync<EidosupException>(() =>
            manager.DownloadChecksumManifestAsync(
                new EidosReleaseAsset("SHA256SUMS", "https://example.invalid/SHA256SUMS", 100),
                CancellationToken.None));

        Assert.Equal(EidosupErrorCode.NetworkFailure, exception.Code);
        Assert.Contains("proxy", exception.Hint, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }

    private sealed class InterruptingStream(byte[] buffer) : MemoryStream(buffer)
    {
        private bool _interrupted;

        public override ValueTask<int> ReadAsync(Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (_interrupted)
            {
                throw new IOException("Injected connection interruption.");
            }

            _interrupted = true;
            return base.ReadAsync(destination, cancellationToken);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"eidosup-download-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
