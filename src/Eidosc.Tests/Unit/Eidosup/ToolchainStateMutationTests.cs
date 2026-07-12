using Eidosup.Distribution;
using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ToolchainStateMutationTests
{
    private const string FirstHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
    private const string SecondHash = "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    [Fact]
    public async Task SetDefaultAsync_SwitchesToInstalledExactSelectorAndCanClearIt()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var second = await fixture.CreateToolchainAsync("0.4.0-alpha.3", SecondHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, first.Directory, ReleaseChannel.Preview, CancellationToken.None);
        await store.RegisterInstallAsync(fixture.Layout, second.Directory, requestedChannel: null, CancellationToken.None);

        var selected = await store.SetDefaultAsync(fixture.Layout, "0.4.0-alpha.3", CancellationToken.None);
        var cleared = await store.SetDefaultAsync(fixture.Layout, selector: null, CancellationToken.None);

        Assert.Equal("0.4.0-alpha.3", selected.Default?.Selector);
        Assert.Equal(second.Manifest.ToolchainId, selected.Default?.ToolchainId);
        Assert.Equal(ToolchainActivationReason.DefaultChanged, selected.ActivationHistory[^1].Reason);
        Assert.Null(cleared.Default);
        Assert.True(cleared.Revision > selected.Revision);
    }

    [Fact]
    public async Task SetDefaultAsync_ExplicitClearSurvivesLaterInstall()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var second = await fixture.CreateToolchainAsync("0.4.0-alpha.3", SecondHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, first.Directory, ReleaseChannel.Preview, CancellationToken.None);
        await store.SetDefaultAsync(fixture.Layout, selector: null, CancellationToken.None);

        var state = await store.RegisterInstallAsync(
            fixture.Layout,
            second.Directory,
            ReleaseChannel.Preview,
            CancellationToken.None);

        Assert.True(state.DefaultConfigured);
        Assert.Null(state.Default);
        Assert.Equal(second.Manifest.ToolchainId, state.Selectors.Single(selector => selector.Selector == "preview").ToolchainId);
    }

    [Fact]
    public async Task SetDefaultAsync_RepeatingSameSelectionIsIdempotent()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        var initial = await store.RegisterInstallAsync(
            fixture.Layout,
            first.Directory,
            ReleaseChannel.Preview,
            CancellationToken.None);

        var repeated = await store.SetDefaultAsync(fixture.Layout, "preview", CancellationToken.None);

        Assert.Equal(initial.Revision, repeated.Revision);
        Assert.Equal(initial.Default, repeated.Default);
        Assert.Equal(initial.ActivationHistory, repeated.ActivationHistory);
    }

    [Fact]
    public async Task RollbackAsync_MovesChannelBetweenRetainedVerifiedInstallations()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var second = await fixture.CreateToolchainAsync("0.4.0-alpha.3", SecondHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, first.Directory, ReleaseChannel.Preview, CancellationToken.None);
        await store.RegisterInstallAsync(fixture.Layout, second.Directory, ReleaseChannel.Preview, CancellationToken.None);

        var rolledBack = await store.RollbackAsync(fixture.Layout, "preview", CancellationToken.None);
        var rolledForward = await store.RollbackAsync(fixture.Layout, "preview", CancellationToken.None);

        Assert.Equal(first.Manifest.ToolchainId, rolledBack.Default?.ToolchainId);
        Assert.Equal(first.Manifest.ToolchainId, rolledBack.Selectors.Single(selector => selector.Selector == "preview").ToolchainId);
        Assert.Equal(ToolchainActivationReason.Rollback, rolledBack.ActivationHistory[^1].Reason);
        Assert.Equal(second.Manifest.ToolchainId, rolledForward.Default?.ToolchainId);
        Assert.Equal(ToolchainActivationReason.Rollback, rolledForward.ActivationHistory[^1].Reason);
    }

    [Fact]
    public async Task RegisterInstallAsync_TracksChannelHistoryWhenAnotherSelectorIsDefault()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var second = await fixture.CreateToolchainAsync("0.4.0-alpha.3", SecondHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, first.Directory, ReleaseChannel.Preview, CancellationToken.None);
        await store.SetDefaultAsync(fixture.Layout, "0.4.0-alpha.2", CancellationToken.None);

        var updated = await store.RegisterInstallAsync(
            fixture.Layout,
            second.Directory,
            ReleaseChannel.Preview,
            CancellationToken.None);
        var rolledBack = await store.RollbackAsync(fixture.Layout, "preview", CancellationToken.None);

        Assert.Equal("0.4.0-alpha.2", updated.Default?.Selector);
        Assert.Equal(second.Manifest.ToolchainId, updated.Selectors.Single(selector => selector.Selector == "preview").ToolchainId);
        Assert.Contains(updated.ActivationHistory, activation =>
            activation.Selector == "preview" &&
            activation.ToolchainId == second.Manifest.ToolchainId &&
            activation.Reason == ToolchainActivationReason.ChannelUpdated);
        Assert.Equal(first.Manifest.ToolchainId, rolledBack.Selectors.Single(selector => selector.Selector == "preview").ToolchainId);
        Assert.Equal("0.4.0-alpha.2", rolledBack.Default?.Selector);
    }

    [Fact]
    public async Task ConcurrentDefaultMutations_KeepStateReadableAndMonotonic()
    {
        using var fixture = new EidosupToolchainTestFixture();
        var first = await fixture.CreateToolchainAsync("0.4.0-alpha.2", FirstHash);
        var second = await fixture.CreateToolchainAsync("0.4.0-alpha.3", SecondHash);
        var store = new ToolchainStateStore(() => EidosupToolchainTestFixture.FixedTime);
        await store.RegisterInstallAsync(fixture.Layout, first.Directory, ReleaseChannel.Preview, CancellationToken.None);
        await store.RegisterInstallAsync(fixture.Layout, second.Directory, requestedChannel: null, CancellationToken.None);

        await Task.WhenAll(
            store.SetDefaultAsync(fixture.Layout, "0.4.0-alpha.2", CancellationToken.None),
            store.SetDefaultAsync(fixture.Layout, "0.4.0-alpha.3", CancellationToken.None));
        var state = await ToolchainStateStore.ReadVerifiedAsync(fixture.Layout, CancellationToken.None);

        Assert.Contains(state.Default?.Selector, new[] { "0.4.0-alpha.2", "0.4.0-alpha.3" });
        Assert.Equal(2, state.Toolchains.Count);
        Assert.True(state.Revision >= 4);
    }
}
