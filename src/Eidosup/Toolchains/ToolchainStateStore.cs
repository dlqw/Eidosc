using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.Serialization;

namespace Eidosup.Toolchains;

public sealed class ToolchainStateStore
{
    public const string FileName = "toolchains.json";
    public const string BackupFileName = "toolchains.json.bak";
    public const string CorruptFileName = "toolchains.json.corrupt";

    private static readonly JsonSerializerOptions JsonOptions = new(EidosupJsonContext.Default.Options)
    {
        TypeInfoResolver = EidosupJsonContext.Default,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _lockTimeout;

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows()
        ? StringComparison.OrdinalIgnoreCase
        : StringComparison.Ordinal;

    public ToolchainStateStore(Func<DateTimeOffset>? clock = null, TimeSpan? lockTimeout = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30);
    }

    public async Task<ToolchainState> InitializeAsync(
        ToolInstallLayout layout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Directory.CreateDirectory(layout.StateDirectory);
        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "state");
        return await LoadReconcileAndWriteAsync(
            layout,
            selectors: null,
            activateSelector: null,
            cancellationToken);
    }

    public async Task<ToolchainState> RegisterInstallAsync(
        ToolInstallLayout layout,
        string toolchainDirectory,
        ReleaseChannel? requestedChannel,
        CancellationToken cancellationToken) =>
        await RegisterInstallAsync(
            layout,
            toolchainDirectory,
            requestedChannel,
            requestedSelector: null,
            cancellationToken);

    public async Task<ToolchainState> RegisterInstallAsync(
        ToolInstallLayout layout,
        string toolchainDirectory,
        ReleaseChannel? requestedChannel,
        string? requestedSelector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        if (!ToolInstallLayout.IsWithin(layout.ToolchainsDirectory, toolchainDirectory))
        {
            throw new ArgumentException("Toolchain directory is outside the managed toolchain root.", nameof(toolchainDirectory));
        }

        var manifest = await InstallManifest.TryReadAsync(toolchainDirectory, cancellationToken)
                       ?? throw StateCorrupt(
                           $"Installed toolchain '{toolchainDirectory}' has no supported install manifest.",
                           "Reinstall the toolchain from a verified release before registering it.");
        if (!await manifest.VerifyAsync(
                toolchainDirectory,
                manifest.DistributionManifestSha256,
                cancellationToken,
                manifest.Rid,
                manifest.Version))
        {
            throw StateCorrupt(
                $"Installed toolchain '{toolchainDirectory}' failed manifest verification.",
                "Reinstall the toolchain; Eidosup will not register modified or incomplete files.");
        }

        var requestedHost = requestedSelector == null ? null : ToolchainSpec.Parse(requestedSelector).HostRid;
        var exactSelector = requestedChannel == null && requestedSelector != null
            ? requestedSelector
            : requestedHost == null ? manifest.Version : $"{manifest.Version}@{requestedHost}";
        var selectors = new List<ToolchainSelectorState>
        {
            new(exactSelector, ToolchainSelectorKind.ExactVersion, manifest.ToolchainId, manifest.InstalledAt)
        };
        if (requestedChannel is { } channel)
        {
            if (!Enum.IsDefined(channel))
            {
                throw new ArgumentOutOfRangeException(nameof(requestedChannel), channel, "Unknown release channel.");
            }

            selectors.Add(new ToolchainSelectorState(
                requestedSelector ?? channel.ToString().ToLowerInvariant(),
                ToolchainSelectorKind.Channel,
                manifest.ToolchainId,
                manifest.InstalledAt));
        }

        Directory.CreateDirectory(layout.StateDirectory);
        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "state");
        var activateSelector = requestedSelector ?? (requestedChannel is { } selectedChannel
            ? selectedChannel.ToString().ToLowerInvariant()
            : manifest.Version);
        return await LoadReconcileAndWriteAsync(
            layout,
            selectors,
            activateSelector,
            cancellationToken);
    }

    public async Task<ToolchainState> SetDefaultAsync(
        ToolInstallLayout layout,
        string? selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        Directory.CreateDirectory(layout.StateDirectory);
        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "state");
        var state = await LoadReconcileAndWriteAsync(
            layout,
            selectors: null,
            activateSelector: null,
            cancellationToken);
        var now = _clock();
        ToolchainDefaultState? updatedDefault;
        var history = state.ActivationHistory.ToList();
        if (selector == null)
        {
            updatedDefault = null;
        }
        else
        {
            var selected = state.Selectors.SingleOrDefault(candidate =>
                               string.Equals(candidate.Selector, selector, StringComparison.Ordinal))
                           ?? throw ToolchainUnavailable(
                               $"Toolchain selector '{selector}' is not installed.",
                               "Install the requested toolchain before setting it as the default.");
            var changed = state.Default == null ||
                          !string.Equals(state.Default.Selector, selected.Selector, StringComparison.Ordinal) ||
                          !string.Equals(state.Default.ToolchainId, selected.ToolchainId, StringComparison.Ordinal);
            updatedDefault = changed
                ? new ToolchainDefaultState(selected.Selector, selected.ToolchainId, now)
                : state.Default;
            if (changed)
            {
                history.Add(new ToolchainActivationState(
                    selected.Selector,
                    selected.ToolchainId,
                    ToolchainActivationReason.DefaultChanged,
                    now));
            }
        }

        var updated = state with
        {
            Default = updatedDefault,
            DefaultConfigured = true,
            ActivationHistory = history
        };
        return await PersistMutationAsync(layout, state, updated, cancellationToken);
    }

    public async Task<ToolchainState> LinkCustomAsync(
        ToolInstallLayout layout,
        CustomToolchainState customToolchain,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(customToolchain);
        Directory.CreateDirectory(layout.StateDirectory);
        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "state");
        var state = await LoadReconcileAndWriteAsync(layout, null, null, cancellationToken);
        var existing = state.CustomToolchains.SingleOrDefault(candidate =>
            string.Equals(candidate.Name, customToolchain.Name, StringComparison.Ordinal));
        if (existing != null && !ToolInstallLayout.PathEquals(existing.RootDirectory, customToolchain.RootDirectory))
        {
            throw new EidosupException(
                EidosupErrorCode.InstallConflict,
                EidosupExitCodes.InstallConflict,
                $"Custom toolchain '{customToolchain.Name}' is already linked to '{existing.RootDirectory}'.",
                "Unlink the existing custom toolchain before reusing its name.");
        }

        var customs = state.CustomToolchains
            .Where(candidate => !string.Equals(candidate.Name, customToolchain.Name, StringComparison.Ordinal))
            .Append(customToolchain)
            .OrderBy(static candidate => candidate.Name, StringComparer.Ordinal)
            .ToArray();
        var selectors = state.Selectors
            .Where(candidate => !string.Equals(candidate.Selector, customToolchain.Selector, StringComparison.Ordinal))
            .Append(new ToolchainSelectorState(
                customToolchain.Selector,
                ToolchainSelectorKind.Custom,
                customToolchain.ToolchainId,
                customToolchain.LinkedAt))
            .OrderBy(static candidate => candidate.Selector, StringComparer.Ordinal)
            .ToArray();
        return await PersistMutationAsync(
            layout,
            state,
            state with { CustomToolchains = customs, Selectors = selectors },
            cancellationToken);
    }

    public async Task<ToolchainState> UnlinkCustomAsync(
        ToolInstallLayout layout,
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Directory.CreateDirectory(layout.StateDirectory);
        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "state");
        var state = await LoadReconcileAndWriteAsync(layout, null, null, cancellationToken);
        var custom = state.CustomToolchains.SingleOrDefault(candidate =>
                         string.Equals(candidate.Name, name, StringComparison.Ordinal))
                     ?? throw ToolchainUnavailable(
                         $"Custom toolchain '{name}' is not linked.",
                         "Link it before attempting to unlink it.");
        if (state.Default != null && string.Equals(state.Default.ToolchainId, custom.ToolchainId, StringComparison.Ordinal))
        {
            throw new EidosupException(
                EidosupErrorCode.InstallConflict,
                EidosupExitCodes.InstallConflict,
                $"Custom toolchain '{name}' is active as the global default.",
                "Set another default or run 'eidosup default none' before unlinking it.");
        }

        var updated = state with
        {
            CustomToolchains = state.CustomToolchains.Where(candidate => candidate != custom).ToArray(),
            Selectors = state.Selectors.Where(candidate =>
                !string.Equals(candidate.Selector, custom.Selector, StringComparison.Ordinal)).ToArray(),
            Overrides = state.Overrides.Where(candidate =>
                !string.Equals(candidate.Selector, custom.Selector, StringComparison.Ordinal)).ToArray()
        };
        return await PersistMutationAsync(layout, state, updated, cancellationToken);
    }

    public async Task<ToolchainState> SetOverrideAsync(
        ToolInstallLayout layout,
        string directory,
        string selector,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(layout.StateDirectory);
        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "state");
        var state = await LoadReconcileAndWriteAsync(layout, null, null, cancellationToken);
        _ = state.Selectors.SingleOrDefault(candidate => string.Equals(candidate.Selector, selector, StringComparison.Ordinal))
            ?? throw ToolchainUnavailable(
                $"Toolchain selector '{selector}' is not installed or linked.",
                $"Install or link '{selector}' before setting a directory override.");
        var canonicalDirectory = CanonicalizeOverrideDirectory(directory);
        var overrides = state.Overrides
            .Where(candidate => !PathEquals(candidate.Directory, canonicalDirectory))
            .Append(new ToolchainOverrideState(canonicalDirectory, selector, _clock()))
            .OrderBy(static candidate => candidate.Directory, PathComparer)
            .ToArray();
        return await PersistMutationAsync(layout, state, state with { Overrides = overrides }, cancellationToken);
    }

    public async Task<ToolchainState> RemoveOverridesAsync(
        ToolInstallLayout layout,
        string? directory,
        bool nonexistentOnly,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(layout.StateDirectory);
        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "state");
        var state = await LoadReconcileAndWriteAsync(layout, null, null, cancellationToken);
        ToolchainOverrideState[] overrides;
        if (nonexistentOnly)
        {
            overrides = state.Overrides.Where(candidate => Directory.Exists(candidate.Directory)).ToArray();
        }
        else
        {
            var canonicalDirectory = CanonicalizeOverrideDirectory(directory ?? Environment.CurrentDirectory);
            overrides = state.Overrides.Where(candidate => !PathEquals(candidate.Directory, canonicalDirectory)).ToArray();
            if (overrides.Length == state.Overrides.Count)
            {
                throw ToolchainUnavailable(
                    $"No directory override exists for '{canonicalDirectory}'.",
                    "Use 'eidosup override list' to inspect configured overrides.");
            }
        }

        return await PersistMutationAsync(layout, state, state with { Overrides = overrides }, cancellationToken);
    }

    public async Task<ToolchainState> RollbackAsync(
        ToolInstallLayout layout,
        string selector,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);
        Directory.CreateDirectory(layout.StateDirectory);
        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            _lockTimeout,
            cancellationToken,
            operationName: "state");
        var state = await LoadReconcileAndWriteAsync(
            layout,
            selectors: null,
            activateSelector: null,
            cancellationToken);
        var current = state.Selectors.SingleOrDefault(candidate =>
                          string.Equals(candidate.Selector, selector, StringComparison.Ordinal))
                      ?? throw ToolchainUnavailable(
                          $"Toolchain selector '{selector}' is not installed.",
                          "Install the channel before attempting to roll it back.");
        if (current.Kind != ToolchainSelectorKind.Channel)
        {
            throw new EidosupException(
                EidosupErrorCode.InvalidArgument,
                EidosupExitCodes.InvalidArgument,
                $"Toolchain selector '{selector}' is immutable and cannot be rolled back.",
                "Rollback accepts a movable channel selector such as 'stable' or 'preview'.");
        }

        var installedIds = state.Toolchains.Select(static toolchain => toolchain.Id)
            .ToHashSet(StringComparer.Ordinal);
        var currentToolchain = state.Toolchains.Single(toolchain =>
            string.Equals(toolchain.Id, current.ToolchainId, StringComparison.Ordinal));
        var previous = state.ActivationHistory
            .Reverse()
            .FirstOrDefault(activation =>
                string.Equals(activation.Selector, selector, StringComparison.Ordinal) &&
                !string.Equals(activation.ToolchainId, current.ToolchainId, StringComparison.Ordinal) &&
                installedIds.Contains(activation.ToolchainId) &&
                !CanSwitchComposition(state.Toolchains, currentToolchain.Id, activation.ToolchainId));
        if (previous == null)
        {
            throw ToolchainUnavailable(
                $"Toolchain selector '{selector}' has no installed rollback target.",
                "Install or retain an earlier verified channel toolchain before using rollback.");
        }

        var now = _clock();
        var selectors = state.Selectors
            .Select(candidate => string.Equals(candidate.Selector, selector, StringComparison.Ordinal)
                ? candidate with { ToolchainId = previous.ToolchainId, ResolvedAt = now }
                : candidate)
            .ToArray();
        var defaultState = state.Default != null &&
                           string.Equals(state.Default.Selector, selector, StringComparison.Ordinal)
            ? state.Default with { ToolchainId = previous.ToolchainId, SetAt = now }
            : state.Default;
        var history = state.ActivationHistory.Append(new ToolchainActivationState(
            selector,
            previous.ToolchainId,
            ToolchainActivationReason.Rollback,
            now)).ToArray();
        var updated = state with
        {
            Selectors = selectors,
            Default = defaultState,
            ActivationHistory = history
        };
        return await PersistMutationAsync(layout, state, updated, cancellationToken);
    }

    public static async Task<ToolchainState> ReadAsync(
        ToolInstallLayout layout,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(layout.StateDirectory, FileName);
        var result = await TryLoadAsync(path, cancellationToken);
        return result.Status switch
        {
            StateLoadStatus.Valid => result.State!,
            StateLoadStatus.Unsupported => throw Unsupported(path, result.Schema),
            StateLoadStatus.Missing => throw StateCorrupt(
                $"Toolchain state '{path}' does not exist.",
                "Run setup or another state-initializing Eidosup command."),
            _ => throw StateCorrupt(
                $"Toolchain state '{path}' is invalid.",
                "Run a state-initializing command to rebuild installed toolchains from verified manifests.")
        };
    }

    public static async Task<ToolchainState> ReadVerifiedAsync(
        ToolInstallLayout layout,
        CancellationToken cancellationToken)
    {
        var state = await ReadAsync(layout, cancellationToken);
        var scan = await ScanToolchainsAsync(layout, cancellationToken);
        if (!InstalledToolchainsEqual(state.Toolchains, scan.Toolchains) ||
            !state.UnmanagedDirectories.SequenceEqual(scan.UnmanagedDirectories))
        {
            throw StateCorrupt(
                $"Toolchain state '{Path.Combine(layout.StateDirectory, FileName)}' does not match verified install manifests.",
                "Run setup or another state-initializing command to reconcile state before activating a toolchain.");
        }

        return state;
    }

    private async Task<ToolchainState> LoadReconcileAndWriteAsync(
        ToolInstallLayout layout,
        IReadOnlyList<ToolchainSelectorState>? selectors,
        string? activateSelector,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(layout.StateDirectory, FileName);
        var backupPath = Path.Combine(layout.StateDirectory, BackupFileName);
        var primary = await TryLoadAsync(path, cancellationToken);
        if (primary.Status == StateLoadStatus.Unsupported)
        {
            throw Unsupported(path, primary.Schema);
        }

        var mustWrite = primary.Status != StateLoadStatus.Valid || primary.Schema != ToolchainState.CurrentSchema;
        var backupResult = await TryLoadAsync(backupPath, cancellationToken);
        if (backupResult.Status == StateLoadStatus.Unsupported)
        {
            throw Unsupported(backupPath, backupResult.Schema);
        }

        var basis = primary.State ?? backupResult.State;

        var scan = await ScanToolchainsAsync(layout, cancellationToken);
        var now = _clock();
        var reconciled = Reconcile(
            basis ?? ToolchainState.Empty(now),
            scan,
            selectors,
            activateSelector,
            now);
        if (basis == null || !StateContentEquals(basis, reconciled))
        {
            mustWrite = true;
        }

        if (mustWrite)
        {
            var revision = basis == null ? 1 : checked(basis.Revision + 1);
            reconciled = reconciled with { Revision = revision, UpdatedAt = now };
            if (primary.Status == StateLoadStatus.Corrupt && File.Exists(path))
            {
                File.Move(path, Path.Combine(layout.StateDirectory, CorruptFileName), overwrite: true);
            }

            if (backupResult.Status == StateLoadStatus.Corrupt && File.Exists(backupPath))
            {
                File.Move(backupPath, backupPath + ".corrupt", overwrite: true);
            }

            await WriteAtomicAsync(
                path,
                backupPath,
                reconciled,
                replacePrimary: primary.Status == StateLoadStatus.Valid,
                cancellationToken);
        }
        else
        {
            reconciled = basis!;
        }

        CleanupTemporaryFiles(layout.StateDirectory);
        return reconciled;
    }

    private static ToolchainState Reconcile(
        ToolchainState basis,
        ToolchainScan scan,
        IReadOnlyList<ToolchainSelectorState>? selectorUpdates,
        string? activateSelector,
        DateTimeOffset now)
    {
        var customToolchains = basis.CustomToolchains
            .OrderBy(static candidate => candidate.Name, StringComparer.Ordinal)
            .ToArray();
        var installedIds = scan.Toolchains.Select(static toolchain => toolchain.Id)
            .Concat(customToolchains.Select(static toolchain => toolchain.ToolchainId))
            .ToHashSet(StringComparer.Ordinal);
        var previousSelectors = basis.Selectors
            .Where(selector => installedIds.Contains(selector.ToolchainId))
            .ToDictionary(static selector => selector.Selector, StringComparer.Ordinal);
        var selectors = basis.Selectors
            .Where(selector => installedIds.Contains(selector.ToolchainId))
            .ToDictionary(static selector => selector.Selector, StringComparer.Ordinal);
        if (selectorUpdates != null)
        {
            foreach (var selector in selectorUpdates)
            {
                if (!installedIds.Contains(selector.ToolchainId))
                {
                    throw StateCorrupt(
                        $"Selector '{selector.Selector}' refers to unverified toolchain '{selector.ToolchainId}'.",
                        "Reinstall and verify the toolchain before updating selectors.");
                }

                if (selectors.TryGetValue(selector.Selector, out var existingSelector) &&
                    existingSelector.Kind == ToolchainSelectorKind.ExactVersion &&
                    selector.Kind == ToolchainSelectorKind.ExactVersion &&
                    !string.Equals(existingSelector.ToolchainId, selector.ToolchainId, StringComparison.Ordinal) &&
                    !CanSwitchComposition(scan.Toolchains, existingSelector.ToolchainId, selector.ToolchainId))
                {
                    throw new EidosupException(
                        EidosupErrorCode.InstallConflict,
                        EidosupExitCodes.InstallConflict,
                        $"Exact-version selector '{selector.Selector}' already identifies a different immutable toolchain.",
                        "Use the original verified source for this version or publish a new Eidosc version; exact selectors never move between manifest identities.");
                }

                selectors[selector.Selector] = selector;
            }
        }

        var orderedSelectors = selectors.Values.OrderBy(static selector => selector.Selector, StringComparer.Ordinal).ToArray();
        var previousDefault = basis.Default;
        var defaultConfigured = basis.DefaultConfigured || previousDefault != null;
        var defaultState = previousDefault;
        if (defaultState != null &&
            (!installedIds.Contains(defaultState.ToolchainId) ||
             !selectors.TryGetValue(defaultState.Selector, out var selected) ||
             !string.Equals(selected.ToolchainId, defaultState.ToolchainId, StringComparison.Ordinal)))
        {
            defaultState = null;
        }

        var activationHistory = basis.ActivationHistory.ToList();
        if (activateSelector != null &&
            selectors.TryGetValue(activateSelector, out var activatedSelector))
        {
            var selectorChanged = previousSelectors.TryGetValue(activateSelector, out var previousSelector) &&
                                  !string.Equals(previousSelector.ToolchainId, activatedSelector.ToolchainId, StringComparison.Ordinal);
            if (defaultState == null &&
                (!defaultConfigured ||
                 previousDefault != null &&
                 string.Equals(previousDefault.Selector, activateSelector, StringComparison.Ordinal)))
            {
                defaultState = new ToolchainDefaultState(
                    activateSelector,
                    activatedSelector.ToolchainId,
                    now);
                defaultConfigured = true;
                AddActivation(
                    activationHistory,
                    activateSelector,
                    activatedSelector.ToolchainId,
                    previousDefault != null &&
                    string.Equals(previousDefault.Selector, activateSelector, StringComparison.Ordinal) &&
                    activatedSelector.Kind == ToolchainSelectorKind.Channel
                        ? ToolchainActivationReason.ChannelUpdated
                        : ToolchainActivationReason.DefaultChanged,
                    now);
            }
            else if (defaultState != null &&
                     string.Equals(defaultState.Selector, activateSelector, StringComparison.Ordinal) &&
                     !string.Equals(defaultState.ToolchainId, activatedSelector.ToolchainId, StringComparison.Ordinal))
            {
                defaultState = new ToolchainDefaultState(
                    activateSelector,
                    activatedSelector.ToolchainId,
                    now);
                AddActivation(
                    activationHistory,
                    activateSelector,
                    activatedSelector.ToolchainId,
                    activatedSelector.Kind == ToolchainSelectorKind.Channel
                        ? ToolchainActivationReason.ChannelUpdated
                        : ToolchainActivationReason.DefaultChanged,
                    now);
            }
            else if (activatedSelector.Kind == ToolchainSelectorKind.Channel &&
                     (!previousSelectors.ContainsKey(activateSelector) || selectorChanged))
            {
                if (selectorChanged && previousSelector != null)
                {
                    AddActivation(
                        activationHistory,
                        activateSelector,
                        previousSelector.ToolchainId,
                        ToolchainActivationReason.SelectorChanged,
                        previousSelector.ResolvedAt);
                }

                AddActivation(
                    activationHistory,
                    activateSelector,
                    activatedSelector.ToolchainId,
                    selectorChanged
                        ? ToolchainActivationReason.ChannelUpdated
                        : ToolchainActivationReason.SelectorChanged,
                    now);
            }
        }

        return new ToolchainState(
            ToolchainState.CurrentSchema,
            basis.Revision,
            now,
            scan.Toolchains,
            orderedSelectors,
            defaultState,
            defaultConfigured,
            activationHistory,
            basis.Transactions,
            scan.UnmanagedDirectories,
            customToolchains,
            basis.Overrides
                .Where(overrideState => selectors.ContainsKey(overrideState.Selector))
                .OrderBy(static overrideState => overrideState.Directory, PathComparer)
                .ToArray());
    }

    private static bool CanSwitchComposition(
        IReadOnlyList<InstalledToolchainState> toolchains,
        string currentId,
        string replacementId)
    {
        var current = toolchains.SingleOrDefault(toolchain => string.Equals(toolchain.Id, currentId, StringComparison.Ordinal));
        var replacement = toolchains.SingleOrDefault(toolchain => string.Equals(toolchain.Id, replacementId, StringComparison.Ordinal));
        return current != null && replacement != null &&
               string.Equals(current.Version, replacement.Version, StringComparison.Ordinal) &&
               string.Equals(current.Rid, replacement.Rid, StringComparison.Ordinal) &&
               string.Equals(current.Source, replacement.Source, StringComparison.Ordinal) &&
               string.Equals(current.ReleaseTag, replacement.ReleaseTag, StringComparison.Ordinal) &&
               string.Equals(current.DistributionManifestName, replacement.DistributionManifestName, StringComparison.Ordinal) &&
               string.Equals(
                   current.DistributionManifestSha256,
                   replacement.DistributionManifestSha256,
                   StringComparison.Ordinal);
    }

    private static void AddActivation(
        List<ToolchainActivationState> history,
        string selector,
        string toolchainId,
        ToolchainActivationReason reason,
        DateTimeOffset activatedAt)
    {
        var last = history.LastOrDefault(activation =>
            string.Equals(activation.Selector, selector, StringComparison.Ordinal));
        if (last != null && string.Equals(last.ToolchainId, toolchainId, StringComparison.Ordinal))
        {
            return;
        }

        history.Add(new ToolchainActivationState(selector, toolchainId, reason, activatedAt));
    }

    private async Task<ToolchainState> PersistMutationAsync(
        ToolInstallLayout layout,
        ToolchainState original,
        ToolchainState updated,
        CancellationToken cancellationToken)
    {
        if (StateContentEquals(original, updated))
        {
            return original;
        }

        var now = _clock();
        updated = updated with
        {
            Revision = checked(original.Revision + 1),
            UpdatedAt = now
        };
        await WriteAtomicAsync(
            Path.Combine(layout.StateDirectory, FileName),
            Path.Combine(layout.StateDirectory, BackupFileName),
            updated,
            replacePrimary: true,
            cancellationToken);
        CleanupTemporaryFiles(layout.StateDirectory);
        return updated;
    }

    private static async Task<ToolchainScan> ScanToolchainsAsync(
        ToolInstallLayout layout,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(layout.ToolchainsDirectory))
        {
            return new ToolchainScan([], []);
        }

        var toolchains = new List<InstalledToolchainState>();
        var unmanaged = new List<UnmanagedToolchainState>();
        foreach (var directory in Directory.EnumerateDirectories(layout.ToolchainsDirectory)
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var directoryName = Path.GetFileName(directory);
            if (directoryName is ".staging" or ".backup")
            {
                continue;
            }

            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                unmanaged.Add(Unmanaged(directoryName, UnmanagedToolchainReason.InvalidManifest));
                continue;
            }

            var manifestPath = Path.Combine(directory, InstallManifest.FileName);
            if (!File.Exists(manifestPath))
            {
                var legacy = string.Equals(directoryName, "eidosc", StringComparison.Ordinal) ||
                             SemanticVersion.TryParse(directoryName, out _);
                unmanaged.Add(Unmanaged(
                    directoryName,
                    legacy ? UnmanagedToolchainReason.LegacyLayout : UnmanagedToolchainReason.MissingManifest));
                continue;
            }

            var manifestSchema = await ReadManifestSchemaAsync(manifestPath, cancellationToken);
            if (manifestSchema != InstallManifest.CurrentSchema)
            {
                var legacy = SemanticVersion.TryParse(directoryName, out _);
                unmanaged.Add(Unmanaged(
                    directoryName,
                    legacy
                        ? UnmanagedToolchainReason.LegacyLayout
                        : manifestSchema is > 0
                            ? UnmanagedToolchainReason.UnsupportedManifest
                            : UnmanagedToolchainReason.InvalidManifest));
                continue;
            }

            var manifest = await InstallManifest.TryReadAsync(directory, cancellationToken);
            if (manifest == null ||
                !await manifest.VerifyAsync(
                    directory,
                    manifest.DistributionManifestSha256,
                    cancellationToken,
                    manifest.Rid,
                    manifest.Version))
            {
                unmanaged.Add(Unmanaged(directoryName, UnmanagedToolchainReason.InvalidManifest));
                continue;
            }

            await using var stream = new FileStream(
                manifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var installManifestSha256 = Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken))
                .ToLowerInvariant();
            toolchains.Add(new InstalledToolchainState(
                manifest.ToolchainId,
                manifest.Version,
                manifest.Rid,
                manifest.IdentitySha256,
                manifest.CompositionSha256,
                installManifestSha256,
                manifest.DistributionManifestName,
                manifest.DistributionManifestSha256,
                manifest.ReleaseTag,
                manifest.Source,
                manifest.Profile,
                manifest.ExplicitComponents,
                manifest.ExplicitTargets,
                manifest.Components,
                manifest.Targets,
                manifest.Artifacts,
                manifest.InstalledAt));
        }

        return new ToolchainScan(
            toolchains.OrderBy(static toolchain => toolchain.Id, StringComparer.Ordinal).ToArray(),
            unmanaged.OrderBy(static entry => entry.DirectoryName, StringComparer.Ordinal).ToArray());
    }

    private static UnmanagedToolchainState Unmanaged(string directoryName, UnmanagedToolchainReason reason) => new(
        directoryName,
        reason,
        reason == UnmanagedToolchainReason.LegacyLayout
            ? "This pre-state-layout directory is not trusted. Reinstall the toolchain, then remove this directory manually after confirming it is no longer needed."
            : "This directory is not a verified Eidosup toolchain. Preserve it for inspection or remove it manually; Eidosup will not activate it.");

    private static async Task<int?> ReadManifestSchemaAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("schema", out var schema) &&
                   schema.TryGetInt32(out var value)
                ? value
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<StateLoadResult> TryLoadAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return new StateLoadResult(StateLoadStatus.Missing, null, null);
        }

        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 32 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schema", out var schemaProperty) ||
                !schemaProperty.TryGetInt32(out var schema))
            {
                return new StateLoadResult(StateLoadStatus.Corrupt, null, null);
            }

            if (schema is not (1 or 2 or ToolchainState.CurrentSchema))
            {
                return new StateLoadResult(StateLoadStatus.Unsupported, null, schema);
            }

            var state = schema switch
            {
                ToolchainState.CurrentSchema => document.RootElement.Deserialize<ToolchainState>(JsonOptions),
                2 => MigrateV2(document.RootElement.Deserialize<ToolchainStateV2>(JsonOptions)),
                _ => MigrateV1(document.RootElement.Deserialize<ToolchainStateV1>(JsonOptions))
            };
            return state != null && Validate(state)
                ? new StateLoadResult(StateLoadStatus.Valid, state, schema)
                : new StateLoadResult(StateLoadStatus.Corrupt, null, schema);
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            return new StateLoadResult(StateLoadStatus.Corrupt, null, null);
        }
    }

    private static bool Validate(ToolchainState state)
    {
        if (state.Schema != ToolchainState.CurrentSchema ||
            state.Revision < 1 ||
            state.UpdatedAt == default ||
            state.Toolchains == null ||
            state.Selectors == null ||
            state.ActivationHistory == null ||
            state.Transactions == null ||
            state.UnmanagedDirectories == null ||
            state.CustomToolchains == null ||
            state.Overrides == null)
        {
            return false;
        }

        var toolchainIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var toolchain in state.Toolchains)
        {
            if (toolchain == null ||
                !ToolchainIdentity.IsValidId(toolchain.Id) ||
                !SemanticVersion.TryParse(toolchain.Version, out var version) ||
                version == null ||
                !string.Equals(version.ToString(), toolchain.Version, StringComparison.Ordinal) ||
                !PlatformContext.IsSupportedRid(toolchain.Rid) ||
                !ToolchainIdentity.IsCanonicalSha256(toolchain.IdentitySha256) ||
                !ToolchainIdentity.IsCanonicalSha256(toolchain.CompositionSha256) ||
                !ToolchainIdentity.IsCanonicalSha256(toolchain.InstallManifestSha256) ||
                !ToolchainIdentity.IsCanonicalSha256(toolchain.DistributionManifestSha256) ||
                toolchain.InstalledAt == default ||
                string.IsNullOrWhiteSpace(toolchain.ReleaseTag) ||
                string.IsNullOrWhiteSpace(toolchain.Source) ||
                Path.GetFileName(toolchain.DistributionManifestName) != toolchain.DistributionManifestName ||
                !Enum.TryParse<ToolchainProfile>(toolchain.Profile, ignoreCase: true, out var profile) ||
                !Enum.IsDefined(profile) ||
                toolchain.ExplicitComponents == null ||
                toolchain.ExplicitTargets == null ||
                toolchain.Components is not { Count: > 0 } ||
                toolchain.Targets == null ||
                toolchain.Artifacts is not { Count: > 0 } ||
                !toolchainIds.Add(toolchain.Id))
            {
                return false;
            }

            if (toolchain.Components.Any(static component =>
                    component == null ||
                    string.IsNullOrWhiteSpace(component.Id) ||
                    component.Files is not { Count: > 0 }) ||
                toolchain.Components.Select(static component => component.Id).Distinct(StringComparer.Ordinal).Count() !=
                toolchain.Components.Count ||
                toolchain.ExplicitComponents.Distinct(StringComparer.Ordinal).Count() != toolchain.ExplicitComponents.Count ||
                toolchain.ExplicitTargets.Distinct(StringComparer.Ordinal).Count() != toolchain.ExplicitTargets.Count ||
                toolchain.Targets.Distinct(StringComparer.Ordinal).Count() != toolchain.Targets.Count ||
                toolchain.Artifacts.Any(static artifact =>
                    artifact == null ||
                    Path.GetFileName(artifact.Name) != artifact.Name ||
                    artifact.Size <= 0 ||
                    !ToolchainIdentity.IsCanonicalSha256(artifact.Sha256)) ||
                toolchain.Artifacts.Select(static artifact => artifact.Name).Distinct(StringComparer.Ordinal).Count() !=
                toolchain.Artifacts.Count)
            {
                return false;
            }
        }

        var customNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var custom in state.CustomToolchains)
        {
            if (custom == null ||
                !CustomToolchain.IsValidName(custom.Name) ||
                !string.Equals(custom.Selector, CustomToolchain.GetSelector(custom.Name), StringComparison.Ordinal) ||
                !string.Equals(custom.ToolchainId, CustomToolchain.GetId(custom.Name), StringComparison.Ordinal) ||
                !Path.IsPathFullyQualified(custom.RootDirectory) ||
                !Path.IsPathFullyQualified(custom.CommandPath) ||
                !Path.IsPathFullyQualified(custom.RuntimePath) ||
                !ToolInstallLayout.IsWithin(custom.RootDirectory, custom.CommandPath) ||
                !ToolInstallLayout.IsWithin(custom.RootDirectory, custom.RuntimePath) ||
                custom.LinkedAt == default ||
                !customNames.Add(custom.Name) ||
                !toolchainIds.Add(custom.ToolchainId))
            {
                return false;
            }
        }

        var selectorNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var selector in state.Selectors)
        {
            if (selector == null ||
                !IsSafeName(selector.Selector) ||
                !Enum.IsDefined(selector.Kind) ||
                !toolchainIds.Contains(selector.ToolchainId) ||
                selector.ResolvedAt == default ||
                !selectorNames.Add(selector.Selector))
            {
                return false;
            }
        }

        if (state.Default is { } defaultState &&
            (!selectorNames.Contains(defaultState.Selector) ||
             !toolchainIds.Contains(defaultState.ToolchainId) ||
             defaultState.SetAt == default ||
             !state.Selectors.Any(selector =>
                 string.Equals(selector.Selector, defaultState.Selector, StringComparison.Ordinal) &&
                 string.Equals(selector.ToolchainId, defaultState.ToolchainId, StringComparison.Ordinal))))
        {
            return false;
        }

        if (state.ActivationHistory.Any(activation =>
                activation == null ||
                !IsSafeName(activation.Selector) ||
                !toolchainIds.Contains(activation.ToolchainId) &&
                !ToolchainIdentity.IsValidId(activation.ToolchainId) &&
                !CustomToolchain.IsValidId(activation.ToolchainId) ||
                !Enum.IsDefined(activation.Reason) ||
                activation.ActivatedAt == default))
        {
            return false;
        }

        if (state.Transactions.Any(transaction =>
                transaction == null ||
                !Guid.TryParseExact(transaction.Id, "N", out _) ||
                !Enum.IsDefined(transaction.Kind) ||
                !Enum.IsDefined(transaction.Status) ||
                transaction.ToolchainId != null &&
                !ToolchainIdentity.IsValidId(transaction.ToolchainId) &&
                !CustomToolchain.IsValidId(transaction.ToolchainId) ||
                !IsSafeName(transaction.JournalFile) ||
                transaction.StartedAt == default ||
                transaction.UpdatedAt < transaction.StartedAt))
        {
            return false;
        }

        var overrideDirectories = new HashSet<string>(PathComparer);
        if (state.Overrides.Any(overrideState =>
                overrideState == null ||
                !Path.IsPathFullyQualified(overrideState.Directory) ||
                !selectorNames.Contains(overrideState.Selector) ||
                overrideState.SetAt == default ||
                !overrideDirectories.Add(overrideState.Directory)))
        {
            return false;
        }

        var unmanagedNames = new HashSet<string>(StringComparer.Ordinal);
        return state.UnmanagedDirectories.All(unmanaged =>
            unmanaged != null &&
            IsSafeName(unmanaged.DirectoryName) &&
            Enum.IsDefined(unmanaged.Reason) &&
            !string.IsNullOrWhiteSpace(unmanaged.Guidance) &&
            unmanagedNames.Add(unmanaged.DirectoryName));
    }

    private static bool IsSafeName(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) < 0 &&
        value.All(static character => !char.IsControl(character));

    private static bool StateContentEquals(ToolchainState left, ToolchainState right) =>
        InstalledToolchainsEqual(left.Toolchains, right.Toolchains) &&
        left.Selectors.SequenceEqual(right.Selectors) &&
        Equals(left.Default, right.Default) &&
        left.DefaultConfigured == right.DefaultConfigured &&
        left.ActivationHistory.SequenceEqual(right.ActivationHistory) &&
        left.Transactions.SequenceEqual(right.Transactions) &&
        left.UnmanagedDirectories.SequenceEqual(right.UnmanagedDirectories) &&
        left.CustomToolchains.SequenceEqual(right.CustomToolchains) &&
        left.Overrides.SequenceEqual(right.Overrides);

    private static bool InstalledToolchainsEqual(
        IReadOnlyList<InstalledToolchainState> left,
        IReadOnlyList<InstalledToolchainState> right) =>
        left.Count == right.Count && left.Zip(right).All(pair => InstalledToolchainEquals(pair.First, pair.Second));

    private static bool InstalledToolchainEquals(
        InstalledToolchainState left,
        InstalledToolchainState right) =>
        string.Equals(left.Id, right.Id, StringComparison.Ordinal) &&
        string.Equals(left.Version, right.Version, StringComparison.Ordinal) &&
        string.Equals(left.Rid, right.Rid, StringComparison.Ordinal) &&
        string.Equals(left.IdentitySha256, right.IdentitySha256, StringComparison.Ordinal) &&
        string.Equals(left.CompositionSha256, right.CompositionSha256, StringComparison.Ordinal) &&
        string.Equals(left.InstallManifestSha256, right.InstallManifestSha256, StringComparison.Ordinal) &&
        string.Equals(left.DistributionManifestName, right.DistributionManifestName, StringComparison.Ordinal) &&
        string.Equals(left.DistributionManifestSha256, right.DistributionManifestSha256, StringComparison.Ordinal) &&
        string.Equals(left.ReleaseTag, right.ReleaseTag, StringComparison.Ordinal) &&
        string.Equals(left.Source, right.Source, StringComparison.Ordinal) &&
        string.Equals(left.Profile, right.Profile, StringComparison.Ordinal) &&
        left.ExplicitComponents.SequenceEqual(right.ExplicitComponents, StringComparer.Ordinal) &&
        left.ExplicitTargets.SequenceEqual(right.ExplicitTargets, StringComparer.Ordinal) &&
        ComponentsEqual(left.Components, right.Components) &&
        left.Targets.SequenceEqual(right.Targets, StringComparer.Ordinal) &&
        left.Artifacts.SequenceEqual(right.Artifacts) &&
        left.InstalledAt == right.InstalledAt;

    private static bool ComponentsEqual(
        IReadOnlyList<InstalledComponent> left,
        IReadOnlyList<InstalledComponent> right) =>
        left.Count == right.Count && left.Zip(right).All(pair =>
            string.Equals(pair.First.Id, pair.Second.Id, StringComparison.Ordinal) &&
            string.Equals(pair.First.Name, pair.Second.Name, StringComparison.Ordinal) &&
            string.Equals(pair.First.Version, pair.Second.Version, StringComparison.Ordinal) &&
            pair.First.Required == pair.Second.Required &&
            string.Equals(pair.First.Target, pair.Second.Target, StringComparison.Ordinal) &&
            pair.First.Files.SequenceEqual(pair.Second.Files, StringComparer.Ordinal));

    private static ToolchainState? MigrateV1(ToolchainStateV1? state) => state == null
        ? null
        : new ToolchainState(
            ToolchainState.CurrentSchema,
            state.Revision,
            state.UpdatedAt,
            [],
            [],
            null,
            DefaultConfigured: false,
            [],
            [],
            state.UnmanagedDirectories,
            [],
            []);

    private static ToolchainState? MigrateV2(ToolchainStateV2? state)
    {
        if (state == null)
        {
            return null;
        }

        var customIds = state.CustomToolchains.Select(static custom => custom.ToolchainId)
            .ToHashSet(StringComparer.Ordinal);
        var selectors = state.Selectors.Where(selector =>
            selector.Kind == ToolchainSelectorKind.Custom && customIds.Contains(selector.ToolchainId)).ToArray();
        var defaultState = state.Default != null && customIds.Contains(state.Default.ToolchainId)
            ? state.Default
            : null;
        return new ToolchainState(
            ToolchainState.CurrentSchema,
            state.Revision,
            state.UpdatedAt,
            [],
            selectors,
            defaultState,
            state.DefaultConfigured && defaultState != null,
            state.ActivationHistory.Where(activation => customIds.Contains(activation.ToolchainId)).ToArray(),
            [],
            state.UnmanagedDirectories,
            state.CustomToolchains,
            state.Overrides);
    }

    private static string CanonicalizeOverrideDirectory(string directory) =>
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathEquals(string left, string right) =>
        string.Equals(left, right, PathComparison);

    private static async Task WriteAtomicAsync(
        string path,
        string backupPath,
        ToolchainState state,
        bool replacePrimary,
        CancellationToken cancellationToken)
    {
        var temporaryPath = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 32 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, state, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            if (replacePrimary)
            {
                File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
            }
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void CleanupTemporaryFiles(string stateDirectory)
    {
        foreach (var path in Directory.EnumerateFiles(stateDirectory, $"{FileName}.*.tmp"))
        {
            File.Delete(path);
        }
    }

    private static EidosupException Unsupported(string path, int? schema) => new(
        EidosupErrorCode.StateUnsupported,
        EidosupExitCodes.StateUnsupported,
        $"Toolchain state '{path}' uses unsupported schema '{schema?.ToString() ?? "unknown"}'.",
        "Upgrade Eidosup to a version that supports this schema. The state file was not modified.");

    private static EidosupException StateCorrupt(string message, string hint) => new(
        EidosupErrorCode.StateCorrupt,
        EidosupExitCodes.StateCorrupt,
        message,
        hint);

    private static EidosupException ToolchainUnavailable(string message, string hint) => new(
        EidosupErrorCode.ToolchainUnavailable,
        EidosupExitCodes.ToolchainUnavailable,
        message,
        hint);

    private enum StateLoadStatus
    {
        Missing,
        Valid,
        Corrupt,
        Unsupported
    }

    private sealed record StateLoadResult(StateLoadStatus Status, ToolchainState? State, int? Schema);

    private sealed record ToolchainScan(
        IReadOnlyList<InstalledToolchainState> Toolchains,
        IReadOnlyList<UnmanagedToolchainState> UnmanagedDirectories);
}
