using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Diagnostics;
using Eidosup.Distribution;

namespace Eidosup.Installation;

public interface IAccessTokenProvider
{
    string? GetToken();
}

public sealed class EnvironmentAccessTokenProvider : IAccessTokenProvider
{
    public string? GetToken() =>
        GetNonEmptyEnvironmentVariable("GITHUB_TOKEN") ?? GetNonEmptyEnvironmentVariable("GH_TOKEN");

    private static string? GetNonEmptyEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

public sealed class GitHubReleaseClient : IEidosReleaseSource, IDisposable
{
    private const int ReleasesPerPage = 100;
    private const int MaximumPages = 100;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly IAccessTokenProvider _tokenProvider;
    private readonly GitHubRepositoryId _repository;
    private readonly bool _ownsHttpClient;

    public GitHubReleaseClient(
        string repository = "dlqw/Eidosc",
        HttpClient? httpClient = null,
        IAccessTokenProvider? tokenProvider = null)
    {
        _repository = GitHubRepositoryId.Parse(repository);
        _httpClient = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(100)
        };
        _tokenProvider = tokenProvider ?? new EnvironmentAccessTokenProvider();
        _ownsHttpClient = httpClient == null;
    }

    public async Task<EidosReleaseInfo> ResolveReleaseAsync(
        string? version,
        ReleaseChannel channel,
        CancellationToken cancellationToken)
    {
        if (version != null)
        {
            var normalized = ReleaseAssetLocator.NormalizeVersion(version);
            var expectedVersion = SemanticVersion.Parse(normalized);
            var tag = $"{ReleaseAssetLocator.EidoscTagPrefix}{expectedVersion}";
            var release = await GetReleaseByTagAsync(tag, cancellationToken);
            return ReleaseSelector.ValidateExact(release, expectedVersion, _repository.ToString());
        }

        var releases = await GetReleasesAsync(cancellationToken);
        return ReleaseSelector.Select(releases, channel, _repository.ToString());
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<EidosReleaseInfo> GetReleaseByTagAsync(
        string tag,
        CancellationToken cancellationToken)
    {
        var uri = BuildApiUri(_repository, $"releases/tags/{Uri.EscapeDataString(tag)}");
        using var response = await SendAsync(uri, cancellationToken);
        await EnsureSuccessAsync(response, _repository, tag);
        var payload = await DeserializeAsync<GitHubReleasePayload>(response, $"release '{tag}'", cancellationToken);
        return payload.ToModel();
    }

    private async Task<IReadOnlyList<EidosReleaseInfo>> GetReleasesAsync(CancellationToken cancellationToken)
    {
        var releases = new List<EidosReleaseInfo>();
        for (var page = 1; page <= MaximumPages; page++)
        {
            var uri = BuildApiUri(_repository, $"releases?per_page={ReleasesPerPage}&page={page}");
            using var response = await SendAsync(uri, cancellationToken);
            await EnsureSuccessAsync(response, _repository, tag: null);
            var currentPage = await DeserializeAsync<List<GitHubReleasePayload>>(
                response,
                $"release page {page}",
                cancellationToken);
            releases.AddRange(currentPage.Select(static payload => payload.ToModel()));
            if (currentPage.Count < ReleasesPerPage)
            {
                return releases;
            }
        }

        throw new EidosupException(
            EidosupErrorCode.InvalidReleaseMetadata,
            EidosupExitCodes.InvalidRelease,
            $"Repository '{_repository}' returned more than {MaximumPages * ReleasesPerPage} releases.",
            "Use an exact --version or a distribution index with bounded channel metadata.");
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("eidosup", "0.1"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        var token = _tokenProvider.GetToken();
        if (!string.IsNullOrEmpty(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        try
        {
            return await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EidosupException(
                EidosupErrorCode.NetworkFailure,
                EidosupExitCodes.NetworkFailure,
                "The release source request timed out.",
                "Check the network connection and proxy, then retry the operation.",
                exception);
        }
    }

    private static async Task<T> DeserializeAsync<T>(
        HttpResponseMessage response,
        string payloadName,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken)
                ?? throw new JsonException("The response body was empty.");
        }
        catch (JsonException exception)
        {
            throw new EidosupException(
                EidosupErrorCode.InvalidReleaseMetadata,
                EidosupExitCodes.InvalidRelease,
                $"The release source returned an invalid {payloadName} payload.",
                "Retry the request or select a different trusted release source.",
                exception);
        }
    }

    private static Task EnsureSuccessAsync(
        HttpResponseMessage response,
        GitHubRepositoryId repository,
        string? tag)
    {
        if (response.IsSuccessStatusCode)
        {
            return Task.CompletedTask;
        }

        var source = $"GitHub repository '{repository}'";
        EidosupException exception = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new EidosupException(
                EidosupErrorCode.AuthenticationRequired,
                EidosupExitCodes.AuthenticationRequired,
                $"{source} requires authentication.",
                "Set GITHUB_TOKEN or GH_TOKEN for a private source; tokens are read from the environment and are not stored."),
            HttpStatusCode.Forbidden when IsRateLimited(response) => CreateRateLimitException(source, response),
            HttpStatusCode.TooManyRequests => CreateRateLimitException(source, response),
            HttpStatusCode.Forbidden => new EidosupException(
                EidosupErrorCode.AccessDenied,
                EidosupExitCodes.AccessDenied,
                $"Access to {source} was denied.",
                "Confirm repository access and the scopes of GITHUB_TOKEN or GH_TOKEN."),
            HttpStatusCode.NotFound => new EidosupException(
                EidosupErrorCode.ReleaseNotFound,
                EidosupExitCodes.ReleaseNotFound,
                tag == null
                    ? $"{source} was not found or is not visible to the current credentials."
                    : $"Release '{tag}' was not found in {source}.",
                tag == null
                    ? "Check the --repo value and provide GITHUB_TOKEN or GH_TOKEN only when the source is private."
                    : "Confirm the release is published; for a private source, provide GITHUB_TOKEN or GH_TOKEN with repository access."),
            _ => new EidosupException(
                EidosupErrorCode.NetworkFailure,
                EidosupExitCodes.NetworkFailure,
                $"{source} returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                "Retry later or select a different trusted release source.")
        };

        throw exception;
    }

    private static bool IsRateLimited(HttpResponseMessage response) =>
        response.Headers.RetryAfter != null ||
        response.Headers.TryGetValues("X-RateLimit-Remaining", out var values) && values.Contains("0");

    private static EidosupException CreateRateLimitException(string source, HttpResponseMessage response)
    {
        var resetHint = response.Headers.RetryAfter?.Delta is { } delay
            ? $" Retry after approximately {Math.Ceiling(delay.TotalSeconds)} seconds."
            : response.Headers.TryGetValues("X-RateLimit-Reset", out var values)
                ? $" The reported reset epoch is {values.FirstOrDefault()}."
                : string.Empty;
        return new EidosupException(
            EidosupErrorCode.RateLimited,
            EidosupExitCodes.RateLimited,
            $"{source} rate limit was exceeded.",
            $"Retry after the limit resets or provide GITHUB_TOKEN or GH_TOKEN for a higher authenticated limit.{resetHint}");
    }

    private static Uri BuildApiUri(GitHubRepositoryId repository, string suffix) =>
        new($"https://api.github.com/repos/{Uri.EscapeDataString(repository.Owner)}/{Uri.EscapeDataString(repository.Name)}/{suffix}");
}

public sealed record GitHubRepositoryId(string Owner, string Name)
{
    public static GitHubRepositoryId Parse(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new FormatException("The GitHub repository must use the form 'owner/name'.");
        }

        var parts = value.Split('/');
        if (parts.Length != 2 || !IsValidOwner(parts[0]) || !IsValidRepositoryName(parts[1]))
        {
            throw new FormatException($"GitHub repository '{value}' is invalid. Expected a valid 'owner/name'.");
        }

        return new GitHubRepositoryId(parts[0], parts[1]);
    }

    public override string ToString() => $"{Owner}/{Name}";

    private static bool IsValidOwner(string value) =>
        value.Length is > 0 and <= 39 &&
        value[0] != '-' && value[^1] != '-' &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character == '-');

    private static bool IsValidRepositoryName(string value) =>
        value.Length is > 0 and <= 100 &&
        value is not "." and not ".." &&
        value.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
}

internal sealed record GitHubReleasePayload(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool PreRelease,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubReleaseAssetPayload> Assets)
{
    public EidosReleaseInfo ToModel() => new(
        TagName,
        Name,
        Draft,
        PreRelease,
        PublishedAt,
        Assets.Select(static asset => new EidosReleaseAsset(asset.Name, asset.BrowserDownloadUrl, asset.Size)).ToArray());
}

internal sealed record GitHubReleaseAssetPayload(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
    [property: JsonPropertyName("size")] long? Size);
