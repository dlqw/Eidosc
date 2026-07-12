using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ReleaseAssetLocatorTests
{
    [Fact]
    public void GetEidoscBundleAssetName_UsesNormalizedVersionAndRid()
    {
        var locator = new ReleaseAssetLocator();
        var platform = new PlatformContext("win-x64", "eidosc.exe", true, false, false);

        var assetName = locator.GetEidoscBundleAssetName("eidosc-v0.3.3-alpha.1", platform);

        Assert.Equal("eidosc-v0.3.3-alpha.1-win-x64.zip", assetName);
    }

    [Fact]
    public void ResolveEidoscBundleAsset_ReturnsMatchingAsset()
    {
        var locator = new ReleaseAssetLocator();
        var release = new GitHubReleaseInfo(
            "eidosc-v0.3.3-alpha.1",
            "0.3.3-alpha.1",
            false,
            true,
            DateTimeOffset.UtcNow,
            [
                new GitHubReleaseAsset("eidosc-v0.3.3-alpha.1-linux-x64.zip", "https://example.invalid/linux.zip"),
                new GitHubReleaseAsset("eidosc-v0.3.3-alpha.1-win-x64.zip", "https://example.invalid/win.zip")
            ]);
        var platform = new PlatformContext("win-x64", "eidosc.exe", true, false, false);

        var asset = locator.ResolveEidoscBundleAsset(release, platform);

        Assert.Equal("eidosc-v0.3.3-alpha.1-win-x64.zip", asset.Name);
    }
}
