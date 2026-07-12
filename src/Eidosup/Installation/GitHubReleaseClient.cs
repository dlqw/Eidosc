using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eidosup.Installation;

public sealed class GitHubReleaseClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public GitHubReleaseClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _ownsHttpClient = httpClient == null;
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("eidosup", "1.0"));
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
    }

    public async Task<GitHubReleaseInfo> ResolveReleaseAsync(
        string repository,
        string? version,
        bool includePreRelease,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(version))
        {
            var normalized = ReleaseAssetLocator.NormalizeVersion(version);
            return await GetReleaseByTagAsync(
                repository,
                $"{ReleaseAssetLocator.EidoscTagPrefix}{normalized}",
                cancellationToken);
        }

        var releases = await GetReleasesAsync(repository, cancellationToken);
        var selected = releases.FirstOrDefault(release =>
                release.TagName.StartsWith(ReleaseAssetLocator.EidoscTagPrefix, StringComparison.Ordinal) &&
                !release.Draft &&
                (includePreRelease || !release.PreRelease))
            ?? throw new InvalidOperationException($"Repository '{repository}' has no release that matches the current filters.");
        return selected;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    private async Task<GitHubReleaseInfo> GetReleaseByTagAsync(string repository, string tag, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(BuildApiUri(repository, $"releases/tags/{tag}"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<GitHubReleaseInfo>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"GitHub returned an empty release payload for tag '{tag}'.");
    }

    private async Task<IReadOnlyList<GitHubReleaseInfo>> GetReleasesAsync(string repository, CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(BuildApiUri(repository, "releases?per_page=20"), cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<List<GitHubReleaseInfo>>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException($"GitHub returned an empty release list for repository '{repository}'.");
    }

    private static string BuildApiUri(string repository, string suffix) => $"https://api.github.com/repos/{repository}/{suffix}";
}

public sealed record GitHubReleaseInfo(
    [property: JsonPropertyName("tag_name")] string TagName,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("draft")] bool Draft,
    [property: JsonPropertyName("prerelease")] bool PreRelease,
    [property: JsonPropertyName("published_at")] DateTimeOffset? PublishedAt,
    [property: JsonPropertyName("assets")] IReadOnlyList<GitHubReleaseAsset> Assets)
{
    public string NormalizedVersion => ReleaseAssetLocator.NormalizeVersion(TagName);
}

public sealed record GitHubReleaseAsset(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl);
