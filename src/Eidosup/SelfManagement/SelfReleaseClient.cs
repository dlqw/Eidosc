using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosup.SelfManagement;

public sealed record SelfRelease(
    string Version,
    string Tag,
    EidosReleaseAsset Binary,
    EidosReleaseAsset Checksums);

public sealed class SelfReleaseClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly GitHubRepositoryId _repository;
    private readonly bool _ownsClient;

    public SelfReleaseClient(string repository = "dlqw/Eidosc", HttpClient? httpClient = null)
    {
        _repository = GitHubRepositoryId.Parse(repository);
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(100) };
        _ownsClient = httpClient == null;
    }

    public async Task<SelfRelease> ResolveLatestAsync(PlatformContext platform, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{_repository.Owner}/{_repository.Name}/releases?per_page=100");
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("eidosup", "0.3"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        AddToken(request);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new EidosupException(
                EidosupErrorCode.NetworkFailure,
                EidosupExitCodes.NetworkFailure,
                $"Eidosup self-update source returned HTTP {(int)response.StatusCode}.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var releases = await JsonSerializer.DeserializeAsync<List<SelfReleasePayload>>(stream, JsonOptions, cancellationToken)
                       ?? [];
        var candidate = releases
            .Where(static release => !release.Draft && release.TagName.StartsWith("eidosup-v", StringComparison.Ordinal))
            .Select(release => (Release: release, Version: TryParseVersion(release.TagName)))
            .Where(static item => item.Version != null)
            .OrderByDescending(static item => item.Version)
            .FirstOrDefault();
        if (candidate.Version == null)
        {
            throw new EidosupException(
                EidosupErrorCode.NoMatchingRelease,
                EidosupExitCodes.ReleaseNotFound,
                "No published Eidosup release was found for self-update.");
        }

        var extension = platform.IsWindows ? ".exe" : string.Empty;
        var binaryName = $"eidosup-v{candidate.Version}-{platform.Rid}{extension}";
        var binary = candidate.Release.Assets.SingleOrDefault(asset => string.Equals(asset.Name, binaryName, StringComparison.Ordinal))
                     ?? throw Missing(binaryName);
        var checksums = candidate.Release.Assets.SingleOrDefault(asset => string.Equals(asset.Name, "SHA256SUMS", StringComparison.Ordinal))
                        ?? throw Missing("SHA256SUMS");
        return new SelfRelease(
            candidate.Version.ToString(),
            candidate.Release.TagName,
            new EidosReleaseAsset(binary.Name, binary.BrowserDownloadUrl, binary.Size),
            new EidosReleaseAsset(checksums.Name, checksums.BrowserDownloadUrl, checksums.Size));
    }

    public static SemanticVersion CurrentVersion()
    {
        var informational = typeof(SelfReleaseClient).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var value = informational?.Split('+')[0] ?? "0.0.0";
        return SemanticVersion.Parse(value);
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }

    private static SemanticVersion? TryParseVersion(string tag) =>
        SemanticVersion.TryParse(tag["eidosup-v".Length..], out var version) ? version : null;

    private static EidosupException Missing(string name) => new(
        EidosupErrorCode.MissingReleaseAsset,
        EidosupExitCodes.MissingAsset,
        $"Eidosup release is missing required asset '{name}'.");

    private static void AddToken(HttpRequestMessage request)
    {
        var token = Environment.GetEnvironmentVariable("GITHUB_TOKEN") ?? Environment.GetEnvironmentVariable("GH_TOKEN");
        if (!string.IsNullOrWhiteSpace(token))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        }
    }

    private sealed record SelfReleasePayload(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("assets")] IReadOnlyList<SelfAssetPayload> Assets);

    private sealed record SelfAssetPayload(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string BrowserDownloadUrl,
        [property: JsonPropertyName("size")] long Size);
}
