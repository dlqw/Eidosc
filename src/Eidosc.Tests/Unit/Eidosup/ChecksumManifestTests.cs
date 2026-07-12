using Eidosup.Diagnostics;
using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ChecksumManifestTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    [Theory]
    [InlineData("  ")]
    [InlineData(" *")]
    public void Parse_AcceptsStandardTextAndBinaryMarkers(string separator)
    {
        var manifest = ChecksumManifest.Parse($"{Hash}{separator}eidosc-v0.4.0-alpha.2-win-x64.zip\n");

        Assert.Equal(Hash, manifest.GetRequiredChecksum("eidosc-v0.4.0-alpha.2-win-x64.zip"));
    }

    [Theory]
    [InlineData("not-a-checksum  asset.zip")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  ../asset.zip")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  dir/asset.zip")]
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  dir\\asset.zip")]
    public void Parse_RejectsMalformedOrUnsafeEntries(string content)
    {
        var exception = Assert.Throws<EidosupException>(() => ChecksumManifest.Parse(content));

        Assert.Equal(EidosupErrorCode.IntegrityFailure, exception.Code);
    }

    [Fact]
    public void Parse_RejectsDuplicateAssetNames()
    {
        var content = $"{Hash}  asset.zip\n{Hash}  asset.zip\n";

        Assert.Throws<EidosupException>(() => ChecksumManifest.Parse(content));
    }

    [Fact]
    public void GetRequiredChecksum_RejectsUncoveredAsset()
    {
        var manifest = ChecksumManifest.Parse($"{Hash}  other.zip\n");

        var exception = Assert.Throws<EidosupException>(() => manifest.GetRequiredChecksum("asset.zip"));
        Assert.Equal(EidosupExitCodes.IntegrityFailure, exception.ExitCode);
    }
}
