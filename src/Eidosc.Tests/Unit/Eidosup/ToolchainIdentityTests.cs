using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ToolchainIdentityTests
{
    [Fact]
    public void Create_UsesDeterministicCanonicalManifestDigest()
    {
        var identity = ToolchainIdentity.Create(
            "0.4.0-alpha.2",
            "win-x64",
            "dlqw/Eidosc",
            "eidosc-v0.4.0-alpha.2",
            "eidosc-v0.4.0-alpha.2-win-x64.zip",
            new string('0', 64),
            123);

        Assert.Equal("1f8854665b63610bcc19c37b44e1f2a2111687670d50901fa76efd545ac4876c", identity.ManifestSha256);
        Assert.Equal(
            "eidosc-0.4.0-alpha.2-win-x64-1f8854665b63610bcc19c37b44e1f2a2111687670d50901fa76efd545ac4876c",
            identity.Id);
        Assert.True(ToolchainIdentity.IsValidId(identity.Id));
    }

    [Theory]
    [InlineData("Eidosc-0.4.0-alpha.2-win-x64-0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("eidosc-0.4.0-alpha.2-win-x86-0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("eidosc-0.4.0-alpha.2-win-x64-AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("eidosc-0.4.0-alpha.2-win-x64-short")]
    public void IsValidId_RejectsNonCanonicalIdentity(string value)
    {
        Assert.False(ToolchainIdentity.IsValidId(value));
    }
}
