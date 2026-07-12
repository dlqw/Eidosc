using System.Net;
using System.Text;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class GitHubReleaseClientTests
{
    [Fact]
    public async Task ResolveReleaseAsync_UsesEnvironmentStyleTokenWithoutPersistingIt()
    {
        HttpRequestMessage? captured = null;
        using var httpClient = new HttpClient(new StubHandler(request =>
        {
            captured = request;
            return JsonResponse("""
                {
                  "tag_name": "eidosc-v0.4.0-alpha.2",
                  "name": "Eidosc 0.4.0-alpha.2",
                  "draft": false,
                  "prerelease": true,
                  "published_at": "2026-07-12T00:00:00Z",
                  "assets": []
                }
                """);
        }));
        using var client = new GitHubReleaseClient("dlqw/Eidosc", httpClient, new FixedTokenProvider("test-token"));

        var release = await client.ResolveReleaseAsync(
            "0.4.0-alpha.2",
            ReleaseChannel.Preview,
            CancellationToken.None);

        Assert.Equal("eidosc-v0.4.0-alpha.2", release.TagName);
        Assert.NotNull(captured);
        Assert.Equal("Bearer", captured.Headers.Authorization?.Scheme);
        Assert.Equal("test-token", captured.Headers.Authorization?.Parameter);
        Assert.Null(httpClient.DefaultRequestHeaders.Authorization);
    }

    [Fact]
    public async Task ResolveReleaseAsync_MapsNotFoundWithoutResponseBodyOrStackLeak()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("sensitive provider response")
            }));
        using var client = new GitHubReleaseClient("dlqw/Eidosc", httpClient, new FixedTokenProvider(null));

        var exception = await Assert.ThrowsAsync<EidosupException>(() => client.ResolveReleaseAsync(
            "0.4.0-alpha.2",
            ReleaseChannel.Preview,
            CancellationToken.None));

        Assert.Equal(EidosupErrorCode.ReleaseNotFound, exception.Code);
        Assert.DoesNotContain("sensitive provider response", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveReleaseAsync_MapsRateLimitSeparatelyFromAccessDenied()
    {
        using var httpClient = new HttpClient(new StubHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            response.Headers.Add("X-RateLimit-Reset", "1783814400");
            return response;
        }));
        using var client = new GitHubReleaseClient("dlqw/Eidosc", httpClient, new FixedTokenProvider(null));

        var exception = await Assert.ThrowsAsync<EidosupException>(() => client.ResolveReleaseAsync(
            version: null,
            ReleaseChannel.Preview,
            CancellationToken.None));

        Assert.Equal(EidosupErrorCode.RateLimited, exception.Code);
        Assert.Contains("1783814400", exception.Hint, StringComparison.Ordinal);
    }

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class FixedTokenProvider(string? token) : IAccessTokenProvider
    {
        public string? GetToken() => token;
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responseFactory(request));
    }
}
