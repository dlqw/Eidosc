namespace Eidosup.Distribution;

public sealed record EidosReleaseInfo(
    string TagName,
    string Name,
    bool Draft,
    bool PreRelease,
    DateTimeOffset? PublishedAt,
    IReadOnlyList<EidosReleaseAsset> Assets)
{
    public string NormalizedVersion => Installation.ReleaseAssetLocator.NormalizeVersion(TagName);
}

public sealed record EidosReleaseAsset(string Name, string DownloadUrl);
