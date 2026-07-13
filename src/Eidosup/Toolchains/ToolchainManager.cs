using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Configuration;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.Proxies;

namespace Eidosup.Toolchains;

public sealed record ToolchainManagementOptions(
    string? Repository = null,
    string? InstallRoot = null,
    string? DownloadRoot = null);

public sealed record ToolchainInstallOutcome(
    ToolchainSpec Spec,
    EidosReleaseInfo Release,
    EidosReleaseAsset BundleAsset,
    EidosReleaseAsset ChecksumAsset,
    ToolInstallLayout Layout,
    VerifiedInstallResult? Install,
    ToolchainState? State,
    bool DryRun);

public enum ToolchainCheckStatus
{
    Missing,
    Current,
    UpdateAvailable,
    Conflict
}

public sealed record ToolchainCheckOutcome(
    ToolchainSpec Spec,
    ToolchainCheckStatus Status,
    string? InstalledVersion,
    string AvailableVersion,
    string AvailableTag);

public sealed record ToolchainUninstallOutcome(
    IReadOnlyList<string> ToolchainIds,
    IReadOnlyList<string> Selectors,
    bool DryRun);

public sealed record ToolchainRollbackOutcome(
    string Selector,
    string FromToolchainId,
    string ToToolchainId,
    ToolchainState State,
    bool DryRun);

public enum ToolchainUninstallCheckpoint
{
    TargetsMoved,
    StateCommitted
}

public interface IToolchainUninstallFaultInjector
{
    Task OnCheckpointAsync(
        ToolchainUninstallCheckpoint checkpoint,
        CancellationToken cancellationToken);
}

public sealed class NoToolchainUninstallFaultInjector : IToolchainUninstallFaultInjector
{
    public Task OnCheckpointAsync(
        ToolchainUninstallCheckpoint checkpoint,
        CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class ToolchainManager
{
    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly ReleaseAssetLocator _assetLocator;
    private readonly Func<string, IEidosReleaseSource>? _releaseSourceFactory;
    private readonly EidosupSettingsStore _settingsStore;
    private readonly DistributionSourceCatalogStore _sourceCatalogStore;
    private readonly ToolchainStateStore _stateStore;
    private readonly Func<VerifiedToolchainInstaller> _installerFactory;
    private readonly ToolchainResolver _resolver;
    private readonly IProxyProcessRunner _processRunner;
    private readonly IToolchainUninstallFaultInjector _uninstallFaultInjector;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _lockTimeout;

    public ToolchainManager(
        ReleaseAssetLocator? assetLocator = null,
        Func<string, IEidosReleaseSource>? releaseSourceFactory = null,
        EidosupSettingsStore? settingsStore = null,
        DistributionSourceCatalogStore? sourceCatalogStore = null,
        ToolchainStateStore? stateStore = null,
        Func<VerifiedToolchainInstaller>? installerFactory = null,
        ToolchainResolver? resolver = null,
        IProxyProcessRunner? processRunner = null,
        IToolchainUninstallFaultInjector? uninstallFaultInjector = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? lockTimeout = null)
    {
        _assetLocator = assetLocator ?? new ReleaseAssetLocator();
        _releaseSourceFactory = releaseSourceFactory;
        _settingsStore = settingsStore ?? new EidosupSettingsStore();
        _sourceCatalogStore = sourceCatalogStore ?? new DistributionSourceCatalogStore();
        _stateStore = stateStore ?? new ToolchainStateStore();
        _installerFactory = installerFactory ?? (() => new VerifiedToolchainInstaller());
        _resolver = resolver ?? new ToolchainResolver();
        _processRunner = processRunner ?? new ProxyProcessRunner();
        _uninstallFaultInjector = uninstallFaultInjector ?? new NoToolchainUninstallFaultInjector();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<ToolchainInstallOutcome> InstallAsync(
        ToolchainManagementOptions options,
        ToolchainSpec spec,
        bool force,
        bool dryRun,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(spec);
        if (spec.Kind == ToolchainSpecKind.Custom)
        {
            throw new EidosupException(
                EidosupErrorCode.InvalidArgument,
                EidosupExitCodes.InvalidArgument,
                $"Custom selector '{spec.Canonical}' cannot be downloaded.",
                "Use 'eidosup toolchain link <name> <path>' for a local compiler build.");
        }
        var hostPlatform = PlatformContext.Detect();
        var layout = ToolInstallLayout.Create(hostPlatform, options.InstallRoot, options.DownloadRoot);
        var settings = await _settingsStore.ReadAsync(layout, cancellationToken);
        var platform = PlatformContext.FromRid(spec.HostRid ?? settings.DefaultHost);
        var source = await ResolveSourceAsync(options, layout, cancellationToken);
        using var releaseSource = CreateReleaseSource(source, layout);
        var release = await releaseSource.ResolveReleaseAsync(
            spec.Version,
            spec.Channel ?? ReleaseChannel.Preview,
            cancellationToken);
        var bundleAsset = _assetLocator.ResolveEidoscBundleAsset(release, platform);
        var checksumAsset = _assetLocator.ResolveChecksumAsset(release);
        if (spec.Kind == ToolchainSpecKind.ExactVersion)
        {
            var existingState = await ReadExistingStateAsync(layout, cancellationToken);
            var existingSelector = existingState.Selectors.SingleOrDefault(selector =>
                string.Equals(selector.Selector, spec.Canonical, StringComparison.Ordinal));
            var existingToolchain = existingSelector == null
                ? null
                : existingState.Toolchains.Single(candidate =>
                    string.Equals(candidate.Id, existingSelector.ToolchainId, StringComparison.Ordinal));
            if (existingToolchain != null &&
                (!string.Equals(existingToolchain.Source, ReleaseSourceIdentity(release, source), StringComparison.Ordinal) ||
                 !string.Equals(existingToolchain.ReleaseTag, release.TagName, StringComparison.Ordinal)))
            {
                throw new EidosupException(
                    EidosupErrorCode.InstallConflict,
                    EidosupExitCodes.InstallConflict,
                    $"Exact-version selector '{spec.Canonical}' already identifies a different immutable toolchain source.",
                    "Use the original verified source for this version or publish a new Eidosc version; exact selectors never move between manifest identities.");
            }
        }

        if (dryRun)
        {
            return new ToolchainInstallOutcome(
                spec,
                release,
                bundleAsset,
                checksumAsset,
                layout,
                Install: null,
                State: null,
                DryRun: true);
        }

        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "management");
        await RecoverUninstallTransactionsAsync(layout, cancellationToken);
        using var installer = _installerFactory();
        var install = await installer.InstallAsync(
            new VerifiedInstallRequest(
                release,
                bundleAsset,
                checksumAsset,
                platform,
                layout,
                ReleaseSourceIdentity(release, source),
                force),
            progress,
            cancellationToken);
        ToolchainState state;
        try
        {
            state = await _stateStore.RegisterInstallAsync(
                layout,
                install.ToolchainDirectory,
                spec.Channel,
                spec.Canonical,
                cancellationToken);
        }
        catch (EidosupException exception) when (exception.Code == EidosupErrorCode.InstallConflict)
        {
            var currentState = await ToolchainStateStore.ReadAsync(layout, CancellationToken.None);
            var referenced = currentState.Selectors.Any(selector =>
                                 string.Equals(selector.ToolchainId, install.ToolchainId, StringComparison.Ordinal)) ||
                             currentState.Default != null &&
                             string.Equals(currentState.Default.ToolchainId, install.ToolchainId, StringComparison.Ordinal);
            if (!referenced)
            {
                DeleteDirectoryIfExists(install.ToolchainDirectory, layout.ToolchainsDirectory);
                await _stateStore.InitializeAsync(layout, CancellationToken.None);
            }

            throw;
        }
        return new ToolchainInstallOutcome(
            spec,
            release,
            bundleAsset,
            checksumAsset,
            layout,
            install,
            state,
            DryRun: false);
    }

    public async Task<IReadOnlyList<ToolchainInstallOutcome>> UpdateAsync(
        ToolchainManagementOptions options,
        IReadOnlyList<ToolchainSpec> requestedSpecs,
        bool dryRun,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requestedSpecs);
        var specs = requestedSpecs.Count == 0
            ? await GetInstalledChannelSpecsAsync(options, cancellationToken)
            : requestedSpecs;
        var results = new List<ToolchainInstallOutcome>(specs.Count);
        foreach (var spec in specs)
        {
            results.Add(await InstallAsync(
                options,
                spec,
                force: false,
                dryRun,
                progress,
                cancellationToken));
        }

        return results;
    }

    public async Task<IReadOnlyList<ToolchainCheckOutcome>> CheckAsync(
        ToolchainManagementOptions options,
        IReadOnlyList<ToolchainSpec> requestedSpecs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(requestedSpecs);
        var layout = CreateLayout(options);
        var state = await ReadExistingStateAsync(layout, cancellationToken);
        var specs = requestedSpecs.Count == 0
            ? state.Selectors
                .Where(static selector => selector.Kind == ToolchainSelectorKind.Channel)
                .Select(static selector => ToolchainSpec.Parse(selector.Selector))
                .OrderBy(static spec => spec.Canonical, StringComparer.Ordinal)
                .ToArray()
            : requestedSpecs;
        var results = new List<ToolchainCheckOutcome>(specs.Count);
        var source = await ResolveSourceAsync(options, layout, cancellationToken);
        using var releaseSource = CreateReleaseSource(source, layout);
        foreach (var spec in specs)
        {
            var release = await releaseSource.ResolveReleaseAsync(
                spec.Version,
                spec.Channel ?? ReleaseChannel.Preview,
                cancellationToken);
            var selector = state.Selectors.SingleOrDefault(candidate =>
                string.Equals(candidate.Selector, spec.Canonical, StringComparison.Ordinal));
            var installed = selector == null
                ? null
                : state.Toolchains.SingleOrDefault(candidate =>
                    string.Equals(candidate.Id, selector.ToolchainId, StringComparison.Ordinal));
            var sourceMatches = installed != null &&
                                string.Equals(installed.Source, ReleaseSourceIdentity(release, source), StringComparison.Ordinal);
            var status = installed == null
                ? ToolchainCheckStatus.Missing
                : spec.Kind == ToolchainSpecKind.ExactVersion && !sourceMatches
                    ? ToolchainCheckStatus.Conflict
                    : sourceMatches &&
                      string.Equals(installed.Version, release.NormalizedVersion, StringComparison.Ordinal) &&
                      string.Equals(installed.ReleaseTag, release.TagName, StringComparison.Ordinal)
                        ? ToolchainCheckStatus.Current
                        : ToolchainCheckStatus.UpdateAvailable;
            results.Add(new ToolchainCheckOutcome(
                spec,
                status,
                installed?.Version,
                release.NormalizedVersion,
                release.TagName));
        }

        return results;
    }

    public Task<ToolchainState> ListAsync(
        ToolchainManagementOptions options,
        CancellationToken cancellationToken) =>
        ReadExistingStateAsync(CreateLayout(options), cancellationToken);

    public async Task<ToolchainState> SetDefaultAsync(
        ToolchainManagementOptions options,
        ToolchainSpec? spec,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var layout = CreateLayout(options);
        var state = await ReadExistingStateAsync(layout, cancellationToken);
        if (spec != null)
        {
            EnsureInstalledSelector(state, spec.Canonical);
        }

        if (dryRun)
        {
            return state;
        }

        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "management");
        await RecoverUninstallTransactionsAsync(layout, cancellationToken);
        return await _stateStore.SetDefaultAsync(layout, spec?.Canonical, cancellationToken);
    }

    public async Task<ToolchainRollbackOutcome> RollbackAsync(
        ToolchainManagementOptions options,
        ToolchainSpec? requestedSpec,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var layout = CreateLayout(options);
        if (dryRun)
        {
            var state = await ReadExistingStateAsync(layout, cancellationToken);
            var spec = ValidateRollbackSpec(requestedSpec ?? ResolveDefaultRollbackSpec(state));
            var current = EnsureInstalledSelector(state, spec.Canonical);
            var target = FindRollbackTarget(state, spec.Canonical, current.ToolchainId);
            return new ToolchainRollbackOutcome(
                spec.Canonical,
                current.ToolchainId,
                target.ToolchainId,
                state,
                DryRun: true);
        }

        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "management");
        await RecoverUninstallTransactionsAsync(layout, cancellationToken);
        var lockedState = await ToolchainStateStore.ReadVerifiedAsync(layout, cancellationToken);
        var lockedSpec = ValidateRollbackSpec(requestedSpec ?? ResolveDefaultRollbackSpec(lockedState));
        var lockedCurrent = EnsureInstalledSelector(lockedState, lockedSpec.Canonical);
        var lockedTarget = FindRollbackTarget(
            lockedState,
            lockedSpec.Canonical,
            lockedCurrent.ToolchainId);
        var updated = await _stateStore.RollbackAsync(layout, lockedSpec.Canonical, cancellationToken);
        return new ToolchainRollbackOutcome(
            lockedSpec.Canonical,
            lockedCurrent.ToolchainId,
            lockedTarget.ToolchainId,
            updated,
            DryRun: false);
    }

    public async Task<ToolchainUninstallOutcome> UninstallAsync(
        ToolchainManagementOptions options,
        IReadOnlyList<ToolchainSpec> specs,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(specs);
        if (specs.Count == 0)
        {
            throw new ArgumentException("At least one toolchain specification is required.", nameof(specs));
        }

        if (specs.Any(static spec => spec.Kind == ToolchainSpecKind.Custom))
        {
            throw new EidosupException(
                EidosupErrorCode.InvalidArgument,
                EidosupExitCodes.InvalidArgument,
                "Custom toolchains are external read-only links and cannot be uninstalled.",
                "Use 'eidosup toolchain unlink <name>'; Eidosup never deletes the external build directory.");
        }

        var layout = CreateLayout(options);
        var initial = await ReadExistingStateAsync(layout, cancellationToken);
        var initialIds = ResolveUninstallIds(initial, specs);
        EnsureDefaultIsNotRemoved(initial, initialIds);
        if (dryRun)
        {
            return new ToolchainUninstallOutcome(
                initialIds,
                specs.Select(static spec => spec.Canonical).ToArray(),
                DryRun: true);
        }

        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "management");
        await RecoverUninstallTransactionsAsync(layout, cancellationToken);
        var state = await ToolchainStateStore.ReadVerifiedAsync(layout, cancellationToken);
        var toolchainIds = ResolveUninstallIds(state, specs);
        EnsureDefaultIsNotRemoved(state, toolchainIds);
        await CommitUninstallAsync(layout, toolchainIds, cancellationToken);
        return new ToolchainUninstallOutcome(
            toolchainIds,
            specs.Select(static spec => spec.Canonical).ToArray(),
            DryRun: false);
    }

    public Task<ResolvedToolchain> ResolveAsync(
        ToolchainManagementOptions options,
        string commandName,
        ToolchainSpec? spec,
        CancellationToken cancellationToken,
        string? workingDirectory = null) =>
        _resolver.ResolveAsync(
            CreateLayout(options),
            NormalizeCommandName(commandName),
            spec?.Canonical,
            cancellationToken,
            workingDirectory);

    public async Task<CustomToolchainState> LinkCustomAsync(
        ToolchainManagementOptions options,
        string name,
        string path,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var layout = CreateLayout(options);
        var custom = CustomToolchain.ValidateAndCreate(name, path, _clock());
        if (dryRun)
        {
            var state = await ReadExistingStateAsync(layout, cancellationToken);
            var existing = state.CustomToolchains.SingleOrDefault(candidate =>
                string.Equals(candidate.Name, custom.Name, StringComparison.Ordinal));
            if (existing != null && !ToolInstallLayout.PathEquals(existing.RootDirectory, custom.RootDirectory))
            {
                throw new EidosupException(
                    EidosupErrorCode.InstallConflict,
                    EidosupExitCodes.InstallConflict,
                    $"Custom toolchain '{custom.Name}' is already linked to '{existing.RootDirectory}'.",
                    "Unlink the existing custom toolchain before reusing its name.");
            }
        }
        else
        {
            await using var operationLock = await InstallOperationLock.AcquireAsync(
                layout.LockDirectory,
                _lockTimeout,
                cancellationToken,
                operationName: "management");
            await RecoverUninstallTransactionsAsync(layout, cancellationToken);
            await _stateStore.LinkCustomAsync(layout, custom, cancellationToken);
        }

        return custom;
    }

    public async Task<ToolchainState> UnlinkCustomAsync(
        ToolchainManagementOptions options,
        string name,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var layout = CreateLayout(options);
        var state = await ReadExistingStateAsync(layout, cancellationToken);
        _ = state.CustomToolchains.SingleOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
            ?? throw new EidosupException(
                EidosupErrorCode.ToolchainUnavailable,
                EidosupExitCodes.ToolchainUnavailable,
                $"Custom toolchain '{name}' is not linked.");
        var custom = state.CustomToolchains.Single(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
        if (state.Default != null && string.Equals(state.Default.ToolchainId, custom.ToolchainId, StringComparison.Ordinal))
        {
            throw new EidosupException(
                EidosupErrorCode.InstallConflict,
                EidosupExitCodes.InstallConflict,
                $"Custom toolchain '{name}' is active as the global default.",
                "Set another default or run 'eidosup default none' before unlinking it.");
        }
        if (dryRun)
        {
            return state;
        }

        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "management");
        await RecoverUninstallTransactionsAsync(layout, cancellationToken);
        return await _stateStore.UnlinkCustomAsync(layout, name, cancellationToken);
    }

    public async Task<ToolchainState> SetOverrideAsync(
        ToolchainManagementOptions options,
        ToolchainSpec spec,
        string directory,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var layout = CreateLayout(options);
        var state = await ReadExistingStateAsync(layout, cancellationToken);
        EnsureInstalledSelector(state, spec.Canonical);
        var fullPath = Path.GetFullPath(directory);
        if (!Directory.Exists(fullPath))
        {
            throw new DirectoryNotFoundException($"Override directory '{fullPath}' does not exist.");
        }

        if (dryRun)
        {
            return state;
        }

        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "management");
        return await _stateStore.SetOverrideAsync(layout, fullPath, spec.Canonical, cancellationToken);
    }

    public async Task<ToolchainState> RemoveOverridesAsync(
        ToolchainManagementOptions options,
        string? directory,
        bool nonexistentOnly,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        var layout = CreateLayout(options);
        var state = await ReadExistingStateAsync(layout, cancellationToken);
        if (dryRun)
        {
            if (!nonexistentOnly)
            {
                var canonicalDirectory = Path.GetFullPath(directory ?? Environment.CurrentDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (!state.Overrides.Any(candidate => ToolInstallLayout.PathEquals(candidate.Directory, canonicalDirectory)))
                {
                    throw new EidosupException(
                        EidosupErrorCode.ToolchainUnavailable,
                        EidosupExitCodes.ToolchainUnavailable,
                        $"No directory override exists for '{canonicalDirectory}'.",
                        "Use 'eidosup override list' to inspect configured overrides.");
                }
            }

            return state;
        }

        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "management");
        return await _stateStore.RemoveOverridesAsync(layout, directory, nonexistentOnly, cancellationToken);
    }

    public async Task<int> RunAsync(
        ToolchainManagementOptions options,
        ToolchainSpec spec,
        string commandName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveAsync(options, commandName, spec, cancellationToken);
        return await _processRunner.RunAsync(resolved, arguments, cancellationToken);
    }

    private async Task<IReadOnlyList<ToolchainSpec>> GetInstalledChannelSpecsAsync(
        ToolchainManagementOptions options,
        CancellationToken cancellationToken)
    {
        var state = await ReadExistingStateAsync(CreateLayout(options), cancellationToken);
        return state.Selectors
            .Where(static selector => selector.Kind == ToolchainSelectorKind.Channel)
            .Select(static selector => ToolchainSpec.Parse(selector.Selector))
            .OrderBy(static spec => spec.Canonical, StringComparer.Ordinal)
            .ToArray();
    }

    private async Task<ToolchainState> ReadExistingStateAsync(
        ToolInstallLayout layout,
        CancellationToken cancellationToken)
    {
        var statePath = Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName);
        if (!File.Exists(statePath))
        {
            return ToolchainState.Empty(_clock());
        }

        return await ToolchainStateStore.ReadVerifiedAsync(layout, cancellationToken);
    }

    private async Task CommitUninstallAsync(
        ToolInstallLayout layout,
        IReadOnlyList<string> toolchainIds,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(layout.TransactionDirectory);
        Directory.CreateDirectory(layout.BackupDirectory);
        var id = Guid.NewGuid().ToString("N");
        var entries = toolchainIds.Select((toolchainId, index) => new UninstallJournalEntry(
            toolchainId,
            layout.GetToolchainDirectory(toolchainId),
            Path.Combine(layout.BackupDirectory, $"uninstall-{id}-{index}"))).ToArray();
        foreach (var entry in entries)
        {
            if (!Directory.Exists(entry.TargetDirectory))
            {
                throw StateCorrupt(
                    $"Installed toolchain '{entry.ToolchainId}' is missing from disk.",
                    "Run eidosup doctor and restore or reinstall the toolchain before uninstalling it.");
            }
        }

        var journalPath = Path.Combine(layout.TransactionDirectory, $"uninstall-{id}.json");
        var journal = new UninstallJournal(
            UninstallJournal.CurrentSchema,
            id,
            UninstallJournalState.Started,
            entries,
            _clock());
        await WriteJournalAsync(journalPath, journal, cancellationToken);
        try
        {
            foreach (var entry in entries)
            {
                Directory.Move(entry.TargetDirectory, entry.BackupDirectory);
            }

            journal = journal with { State = UninstallJournalState.TargetsMoved };
            await WriteJournalAsync(journalPath, journal, cancellationToken);
            await _uninstallFaultInjector.OnCheckpointAsync(
                ToolchainUninstallCheckpoint.TargetsMoved,
                cancellationToken);
            var reconciled = await _stateStore.InitializeAsync(layout, cancellationToken);
            if (reconciled.Toolchains.Any(toolchain => toolchainIds.Contains(toolchain.Id, StringComparer.Ordinal)))
            {
                throw StateCorrupt(
                    "Toolchain state still contains an uninstalled toolchain after reconciliation.",
                    "Preserve the transaction journal and run eidosup doctor before retrying.");
            }

            journal = journal with { State = UninstallJournalState.StateCommitted };
            await WriteJournalAsync(journalPath, journal, cancellationToken);
            await _uninstallFaultInjector.OnCheckpointAsync(
                ToolchainUninstallCheckpoint.StateCommitted,
                cancellationToken);
            foreach (var entry in entries)
            {
                DeleteDirectoryIfExists(entry.BackupDirectory, layout.BackupDirectory);
            }

            File.Delete(journalPath);
        }
        catch
        {
            await RecoverUninstallJournalAsync(layout, journalPath, journal, CancellationToken.None);
            throw;
        }
    }

    private async Task RecoverUninstallTransactionsAsync(
        ToolInstallLayout layout,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(layout.TransactionDirectory))
        {
            return;
        }

        foreach (var journalPath in Directory.EnumerateFiles(
                     layout.TransactionDirectory,
                     "uninstall-*.json",
                     SearchOption.TopDirectoryOnly))
        {
            UninstallJournal journal;
            try
            {
                await using var stream = File.OpenRead(journalPath);
                journal = await JsonSerializer.DeserializeAsync<UninstallJournal>(
                              stream,
                              JournalJsonOptions,
                              cancellationToken)
                          ?? throw new JsonException("Empty uninstall journal.");
            }
            catch (JsonException exception)
            {
                throw new EidosupException(
                    EidosupErrorCode.InstallFailure,
                    EidosupExitCodes.InstallFailure,
                    $"Uninstall transaction journal '{journalPath}' is invalid.",
                    "Preserve the journal for inspection before retrying a toolchain operation.",
                    exception);
            }

            ValidateJournal(layout, journalPath, journal);
            await RecoverUninstallJournalAsync(layout, journalPath, journal, cancellationToken);
        }
    }

    private static async Task RecoverUninstallJournalAsync(
        ToolInstallLayout layout,
        string journalPath,
        UninstallJournal journal,
        CancellationToken cancellationToken)
    {
        ToolchainState? state = null;
        var statePath = Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName);
        if (File.Exists(statePath))
        {
            state = await ToolchainStateStore.ReadAsync(layout, cancellationToken);
        }

        foreach (var entry in journal.Entries)
        {
            var stateReferencesToolchain = state?.Toolchains.Any(toolchain =>
                string.Equals(toolchain.Id, entry.ToolchainId, StringComparison.Ordinal)) == true;
            if (stateReferencesToolchain)
            {
                if (Directory.Exists(entry.BackupDirectory))
                {
                    if (Directory.Exists(entry.TargetDirectory))
                    {
                        throw StateCorrupt(
                            $"Both target and rollback directories exist for '{entry.ToolchainId}'.",
                            $"Preserve '{entry.TargetDirectory}', '{entry.BackupDirectory}', and '{journalPath}' for inspection.");
                    }

                    Directory.Move(entry.BackupDirectory, entry.TargetDirectory);
                }
                else if (!Directory.Exists(entry.TargetDirectory))
                {
                    throw StateCorrupt(
                        $"Uninstall transaction '{journal.Id}' lost toolchain '{entry.ToolchainId}' before state commit.",
                        $"Preserve '{journalPath}' and restore the verified toolchain before continuing.");
                }
            }
            else
            {
                DeleteDirectoryIfExists(entry.BackupDirectory, layout.BackupDirectory);
            }
        }

        File.Delete(journalPath);
    }

    private static async Task WriteJournalAsync(
        string path,
        UninstallJournal journal,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 16 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, journal, JournalJsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void ValidateJournal(
        ToolInstallLayout layout,
        string journalPath,
        UninstallJournal journal)
    {
        if (journal.Schema != UninstallJournal.CurrentSchema ||
            !Guid.TryParseExact(journal.Id, "N", out _) ||
            !Enum.IsDefined(journal.State) ||
            journal.CreatedAt == default ||
            journal.Entries.Count == 0 ||
            !string.Equals(
                Path.GetFileName(journalPath),
                $"uninstall-{journal.Id}.json",
                StringComparison.Ordinal))
        {
            throw InvalidJournal(journalPath);
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < journal.Entries.Count; index++)
        {
            var entry = journal.Entries[index];
            if (!ToolchainIdentity.IsValidId(entry.ToolchainId) ||
                !ids.Add(entry.ToolchainId) ||
                !ToolInstallLayout.PathEquals(
                    entry.TargetDirectory,
                    layout.GetToolchainDirectory(entry.ToolchainId)) ||
                !ToolInstallLayout.PathEquals(
                    entry.BackupDirectory,
                    Path.Combine(layout.BackupDirectory, $"uninstall-{journal.Id}-{index}")))
            {
                throw InvalidJournal(journalPath);
            }
        }
    }

    private static IReadOnlyList<string> ResolveUninstallIds(
        ToolchainState state,
        IReadOnlyList<ToolchainSpec> specs)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var spec in specs)
        {
            var selector = EnsureInstalledSelector(state, spec.Canonical);
            ids.Add(selector.ToolchainId);
        }

        return ids.OrderBy(static id => id, StringComparer.Ordinal).ToArray();
    }

    private static void EnsureDefaultIsNotRemoved(
        ToolchainState state,
        IReadOnlyList<string> toolchainIds)
    {
        if (state.Default != null && toolchainIds.Contains(state.Default.ToolchainId, StringComparer.Ordinal))
        {
            throw new EidosupException(
                EidosupErrorCode.InstallConflict,
                EidosupExitCodes.InstallConflict,
                $"Toolchain '{state.Default.ToolchainId}' is active through default selector '{state.Default.Selector}'.",
                "Set another default or run 'eidosup default none' before uninstalling the active toolchain.");
        }
    }

    private static ToolchainSelectorState EnsureInstalledSelector(ToolchainState state, string selector) =>
        state.Selectors.SingleOrDefault(candidate =>
            string.Equals(candidate.Selector, selector, StringComparison.Ordinal))
        ?? throw new EidosupException(
            EidosupErrorCode.ToolchainUnavailable,
            EidosupExitCodes.ToolchainUnavailable,
            $"Toolchain selector '{selector}' is not installed.",
            $"Run 'eidosup toolchain install {selector}' before using it.");

    private static ToolchainSpec ResolveDefaultRollbackSpec(ToolchainState state)
    {
        if (state.Default == null)
        {
            throw new EidosupException(
                EidosupErrorCode.NoActiveToolchain,
                EidosupExitCodes.NoActiveToolchain,
                "No default Eidos toolchain is configured.",
                "Specify a channel explicitly or set a default before using rollback.");
        }

        return ToolchainSpec.Parse(state.Default.Selector);
    }

    private static ToolchainSpec ValidateRollbackSpec(ToolchainSpec spec)
    {
        if (spec.Kind != ToolchainSpecKind.Channel)
        {
            throw new EidosupException(
                EidosupErrorCode.InvalidArgument,
                EidosupExitCodes.InvalidArgument,
                $"Toolchain selector '{spec.Canonical}' is immutable and cannot be rolled back.",
                "Rollback accepts a movable channel selector such as 'stable' or 'preview'.");
        }

        return spec;
    }

    private static ToolchainActivationState FindRollbackTarget(
        ToolchainState state,
        string selector,
        string currentToolchainId)
    {
        var installedIds = state.Toolchains.Select(static toolchain => toolchain.Id)
            .ToHashSet(StringComparer.Ordinal);
        return state.ActivationHistory
            .Reverse()
            .FirstOrDefault(activation =>
                string.Equals(activation.Selector, selector, StringComparison.Ordinal) &&
                !string.Equals(activation.ToolchainId, currentToolchainId, StringComparison.Ordinal) &&
                installedIds.Contains(activation.ToolchainId))
            ?? throw new EidosupException(
                EidosupErrorCode.ToolchainUnavailable,
                EidosupExitCodes.ToolchainUnavailable,
                $"Toolchain selector '{selector}' has no installed rollback target.",
                "Install or retain an earlier verified channel toolchain before using rollback.");
    }

    private static ToolInstallLayout CreateLayout(ToolchainManagementOptions options) =>
        ToolInstallLayout.Create(PlatformContext.Detect(), options.InstallRoot, options.DownloadRoot);

    private async Task<IReadOnlyList<DistributionSourceDescriptor>> ResolveSourceAsync(
        ToolchainManagementOptions options,
        ToolInstallLayout layout,
        CancellationToken cancellationToken)
    {
        var configured = options.Repository;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = (await _settingsStore.ReadAsync(layout, cancellationToken)).Source;
        }

        return await _sourceCatalogStore.ResolveAsync(layout, configured, cancellationToken);
    }

    private IEidosReleaseSource CreateReleaseSource(
        IReadOnlyList<DistributionSourceDescriptor> sources,
        ToolInstallLayout layout) =>
        _releaseSourceFactory != null
            ? _releaseSourceFactory(sources.Count == 1 && sources[0].Kind == DistributionSourceKind.GitHub
                ? sources[0].Value
                : sources[0].Canonical)
            : ConfiguredReleaseSourceFactory.Create(sources, layout.StateDirectory);

    private static string ReleaseSourceIdentity(
        EidosReleaseInfo release,
        IReadOnlyList<DistributionSourceDescriptor> sources) =>
        release.SourceIdentity ?? (sources[0].Kind == DistributionSourceKind.GitHub ? sources[0].Value : sources[0].Canonical);

    private static string NormalizeCommandName(string commandName)
    {
        if (string.IsNullOrWhiteSpace(commandName) ||
            commandName.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException("The command must be a tool name, not a path.", nameof(commandName));
        }

        var fileName = commandName;
        if (OperatingSystem.IsWindows() && fileName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }

        return fileName.ToLowerInvariant();
    }

    private static void DeleteDirectoryIfExists(string path, string allowedParent)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        if (!ToolInstallLayout.IsWithin(allowedParent, path))
        {
            throw new InvalidOperationException($"Refusing to delete '{path}' outside '{allowedParent}'.");
        }

        Directory.Delete(path, recursive: true);
    }

    private static EidosupException InvalidJournal(string path) => new(
        EidosupErrorCode.InstallFailure,
        EidosupExitCodes.InstallFailure,
        $"Uninstall transaction journal '{path}' failed validation.",
        "Preserve the journal for inspection before retrying a toolchain operation.");

    private static EidosupException StateCorrupt(string message, string hint) => new(
        EidosupErrorCode.StateCorrupt,
        EidosupExitCodes.StateCorrupt,
        message,
        hint);

    private enum UninstallJournalState
    {
        Started,
        TargetsMoved,
        StateCommitted
    }

    private sealed record UninstallJournal(
        int Schema,
        string Id,
        UninstallJournalState State,
        IReadOnlyList<UninstallJournalEntry> Entries,
        DateTimeOffset CreatedAt)
    {
        public const int CurrentSchema = 1;
    }

    private sealed record UninstallJournalEntry(
        string ToolchainId,
        string TargetDirectory,
        string BackupDirectory);
}
