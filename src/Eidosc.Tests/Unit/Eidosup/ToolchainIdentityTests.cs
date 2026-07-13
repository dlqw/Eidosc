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
            "eidos-toolchain-v0.4.0-alpha.2-win-x64.json",
            new string('0', 64),
            ["eidos-std", "eidosc-core"]);

        Assert.Equal("c3332e74c60320b1d87c1b680009d1b52feee50f44700ae10188dc57bd0aee93", identity.CompositionSha256);
        Assert.Equal(
            $"eidosc-0.4.0-alpha.2-win-x64-{identity.IdentitySha256}",
            identity.Id);
        Assert.True(ToolchainIdentity.IsValidId(identity.Id));
    }

    [Fact]
    public void Create_DistinguishesProfileAndExplicitSelectionIntentForSameFiles()
    {
        var minimal = Create(ToolchainProfile.Minimal, ["eidos-docs"], []);
        var complete = Create(ToolchainProfile.Complete, ["eidos-docs"], []);
        var target = Create(ToolchainProfile.Minimal, ["eidos-docs"], ["linux-arm64"]);

        Assert.NotEqual(minimal.CompositionSha256, complete.CompositionSha256);
        Assert.NotEqual(minimal.CompositionSha256, target.CompositionSha256);
        Assert.NotEqual(minimal.Id, complete.Id);
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

    private static ToolchainIdentity Create(
        ToolchainProfile profile,
        IReadOnlyList<string> explicitComponents,
        IReadOnlyList<string> explicitTargets) => ToolchainIdentity.Create(
        "0.4.0-alpha.2",
        "win-x64",
        "dlqw/Eidosc",
        "eidosc-v0.4.0-alpha.2",
        "eidos-toolchain-v0.4.0-alpha.2-win-x64.json",
        new string('0', 64),
        ["eidos-std", "eidosc-core", "eidos-docs"],
        profile,
        explicitComponents,
        explicitTargets);
}
