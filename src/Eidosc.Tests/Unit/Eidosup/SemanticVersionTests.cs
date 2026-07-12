using Eidosup.Distribution;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class EidosupSemanticVersionTests
{
    [Fact]
    public void Parse_AcceptsSemVerAndUnboundedNumericComponents()
    {
        var version = SemanticVersion.Parse("123456789012345678901234567890.2.3-alpha.1+build.7");

        Assert.Equal("123456789012345678901234567890", version.Major);
        Assert.Equal("alpha.1", version.PreRelease);
        Assert.Equal("build.7", version.BuildMetadata);
        Assert.Equal("123456789012345678901234567890.2.3-alpha.1+build.7", version.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-alpha.01")]
    [InlineData("1.2.3+build+")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3/escape")]
    public void Parse_RejectsInvalidSemVer(string input)
    {
        Assert.Throws<FormatException>(() => SemanticVersion.Parse(input));
    }

    [Fact]
    public void CompareTo_ImplementsOfficialPrereleasePrecedence()
    {
        var ordered = new[]
        {
            "1.0.0-alpha",
            "1.0.0-alpha.1",
            "1.0.0-alpha.beta",
            "1.0.0-beta",
            "1.0.0-beta.2",
            "1.0.0-beta.11",
            "1.0.0-rc.1",
            "1.0.0"
        }.Select(SemanticVersion.Parse).ToArray();

        for (var index = 1; index < ordered.Length; index++)
        {
            Assert.True(ordered[index - 1] < ordered[index]);
        }
    }

    [Fact]
    public void CompareTo_IgnoresBuildMetadataForPrecedence()
    {
        var first = SemanticVersion.Parse("1.2.3+first");
        var second = SemanticVersion.Parse("1.2.3+second");

        Assert.Equal(0, first.CompareTo(second));
        Assert.NotEqual(first, second);
    }
}
