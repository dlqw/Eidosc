using Eidosc.CodeGen;
using Xunit;

namespace Eidosc.Tests.Unit.CodeGen;

public sealed class TargetInfoTests
{
    [Theory]
    [InlineData("x86_64-pc-linux-gnu", "x86_64-pc-linux-gnu")]
    [InlineData("x86_64-pc-windows-msvc", "x86_64-pc-windows-msvc")]
    [InlineData("x86_64-apple-macosx10.15", "x86_64-apple-macosx10.15")]
    [InlineData("aarch64-unknown-linux-gnu", "aarch64-unknown-linux-gnu")]
    [InlineData("arm64-apple-macosx11", "arm64-apple-macosx11")]
    public void Parse_FullTriple_ReturnsExpectedTarget(string input, string expectedTriple)
    {
        var target = TargetInfo.Parse(input);
        Assert.Equal(expectedTriple, target.Triple);
    }

    [Theory]
    [InlineData("x64-linux", "x86_64-pc-linux-gnu")]
    [InlineData("windows-x64", "x86_64-pc-windows-msvc")]
    [InlineData("aarch64-linux", "aarch64-unknown-linux-gnu")]
    public void Parse_Alias_ReturnsCanonicalTarget(string input, string expectedTriple)
    {
        var target = TargetInfo.Parse(input);
        Assert.Equal(expectedTriple, target.Triple);
    }

    [Fact]
    public void TryParse_UnknownTarget_ReturnsFalse()
    {
        var success = TargetInfo.TryParse("mips64-unknown-linux-gnu", out _);
        Assert.False(success);
    }
}
