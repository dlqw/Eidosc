using Eidosup.Distribution;
using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ToolchainSpecTests
{
    [Theory]
    [InlineData("stable", ReleaseChannel.Stable)]
    [InlineData("PREVIEW", ReleaseChannel.Preview)]
    public void Parse_CanonicalizesSupportedChannels(string value, ReleaseChannel expected)
    {
        var spec = ToolchainSpec.Parse(value);

        Assert.Equal(ToolchainSpecKind.Channel, spec.Kind);
        Assert.Equal(expected, spec.Channel);
        Assert.Equal(expected.ToString().ToLowerInvariant(), spec.Canonical);
        Assert.Null(spec.Version);
    }

    [Theory]
    [InlineData("0.4.0-alpha.2")]
    [InlineData("v0.4.0-alpha.2")]
    [InlineData("eidosc-v0.4.0-alpha.2")]
    public void Parse_CanonicalizesExactVersions(string value)
    {
        var spec = ToolchainSpec.Parse(value);

        Assert.Equal(ToolchainSpecKind.ExactVersion, spec.Kind);
        Assert.Equal("0.4.0-alpha.2", spec.Canonical);
        Assert.Equal("0.4.0-alpha.2", spec.Version);
        Assert.Null(spec.Channel);
    }

    [Theory]
    [InlineData("nightly")]
    [InlineData("../preview")]
    [InlineData(" preview")]
    [InlineData("")]
    public void Parse_RejectsUnsupportedOrUnsafeSpecifications(string value)
    {
        Assert.ThrowsAny<FormatException>(() => ToolchainSpec.Parse(value));
    }
}
