using Eidosup.Installation;
using Eidosup.Distribution;

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
        var release = new EidosReleaseInfo(
            "eidosc-v0.3.3-alpha.1",
            "0.3.3-alpha.1",
            false,
            true,
            DateTimeOffset.UtcNow,
            [
                new EidosReleaseAsset("eidosc-v0.3.3-alpha.1-linux-x64.zip", "https://example.invalid/linux.zip"),
                new EidosReleaseAsset("eidosc-v0.3.3-alpha.1-win-x64.zip", "https://example.invalid/win.zip")
            ]);
        var platform = new PlatformContext("win-x64", "eidosc.exe", true, false, false);

        var asset = locator.ResolveEidoscBundleAsset(release, platform);

        Assert.Equal("eidosc-v0.3.3-alpha.1-win-x64.zip", asset.Name);
    }

    [Theory]
    [InlineData("0.4.0-alpha.2", "0.4.0-alpha.2")]
    [InlineData("v0.4.0-alpha.2", "0.4.0-alpha.2")]
    [InlineData("eidosc-v0.4.0-alpha.2", "0.4.0-alpha.2")]
    public void NormalizeVersion_AcceptsOnlyDocumentedPrefixes(string input, string expected)
    {
        Assert.Equal(expected, ReleaseAssetLocator.NormalizeVersion(input));
    }

    [Theory]
    [InlineData("V0.4.0-alpha.2")]
    [InlineData("eidosc-0.4.0-alpha.2")]
    [InlineData("0.4/escape")]
    public void NormalizeVersion_RejectsInvalidOrUnsafeInput(string input)
    {
        Assert.Throws<FormatException>(() => ReleaseAssetLocator.NormalizeVersion(input));
    }
}
