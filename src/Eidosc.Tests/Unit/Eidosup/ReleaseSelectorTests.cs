using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ReleaseSelectorTests
{
    [Theory]
    [InlineData("stable", ReleaseChannel.Stable)]
    [InlineData("STABLE", ReleaseChannel.Stable)]
    [InlineData("preview", ReleaseChannel.Preview)]
    public void ReleaseChannelParser_AcceptsDocumentedChannels(string input, ReleaseChannel expected)
    {
        Assert.Equal(expected, ReleaseChannelParser.Parse(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("nightly")]
    [InlineData(" preview")]
    public void ReleaseChannelParser_RejectsUnknownOrAmbiguousChannels(string input)
    {
        Assert.Throws<FormatException>(() => ReleaseChannelParser.Parse(input));
    }

    [Fact]
    public void Select_PreviewUsesHighestSemanticVersionRegardlessOfInputOrder()
    {
        var releases = new[]
        {
            Release("eidosc-v0.4.0-alpha.2", preRelease: true),
            Release("eidosc-v0.3.9", preRelease: false),
            Release("eidosc-v0.4.0-alpha.10", preRelease: true),
            Release("other-v9.0.0", preRelease: false)
        };

        var selected = ReleaseSelector.Select(releases, ReleaseChannel.Preview, "dlqw/Eidosc");

        Assert.Equal("eidosc-v0.4.0-alpha.10", selected.TagName);
    }

    [Fact]
    public void Select_StableExcludesPrereleasesAndDrafts()
    {
        var releases = new[]
        {
            Release("eidosc-v2.0.0", preRelease: false, draft: true),
            Release("eidosc-v1.1.0-alpha.1", preRelease: true),
            Release("eidosc-v1.0.1", preRelease: false)
        };

        var selected = ReleaseSelector.Select(releases, ReleaseChannel.Stable, "dlqw/Eidosc");

        Assert.Equal("eidosc-v1.0.1", selected.TagName);
    }

    [Fact]
    public void Select_IgnoresMalformedAndInconsistentReleaseMetadata()
    {
        var releases = new[]
        {
            Release("eidosc-v1.0", preRelease: false),
            Release("eidosc-v1.1.0-alpha.1", preRelease: false),
            Release("eidosc-v1.0.0", preRelease: false)
        };

        var selected = ReleaseSelector.Select(releases, ReleaseChannel.Preview, "dlqw/Eidosc");

        Assert.Equal("eidosc-v1.0.0", selected.TagName);
    }

    [Fact]
    public void Select_WithoutMatchingReleaseReturnsStableError()
    {
        var exception = Assert.Throws<EidosupException>(() =>
            ReleaseSelector.Select([], ReleaseChannel.Stable, "dlqw/Eidosc"));

        Assert.Equal(EidosupErrorCode.NoMatchingRelease, exception.Code);
        Assert.Equal(EidosupExitCodes.ReleaseNotFound, exception.ExitCode);
    }

    [Theory]
    [InlineData("dlqw/Eidosc")]
    [InlineData("owner-name/repo.name")]
    public void RepositoryId_AcceptsSafeOwnerAndRepository(string input)
    {
        Assert.Equal(input, GitHubRepositoryId.Parse(input).ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("owner")]
    [InlineData("owner/repo/extra")]
    [InlineData("owner/../repo")]
    [InlineData("_owner/repo")]
    [InlineData("owner-/repo")]
    [InlineData("https://github.com/owner/repo")]
    [InlineData("owner/repo?x=1")]
    public void RepositoryId_RejectsUnsafeOrAmbiguousInput(string input)
    {
        Assert.Throws<FormatException>(() => GitHubRepositoryId.Parse(input));
    }

    private static EidosReleaseInfo Release(string tag, bool preRelease, bool draft = false) =>
        new(tag, tag, draft, preRelease, DateTimeOffset.Parse("2026-07-12T00:00:00Z"), []);
}
