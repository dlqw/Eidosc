using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;

namespace Eidosup.Toolchains;

public sealed class ToolchainStateStore
{
    public const string FileName = "toolchains.json";
    public const string BackupFileName = "toolchains.json.bak";
    public const string CorruptFileName = "toolchains.json.corrupt";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };

    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _lockTimeout;

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
        return await LoadReconcileAndWriteAsync(layout, selectors: null, cancellationToken);
    }

    public async Task<ToolchainState> RegisterInstallAsync(
        ToolInstallLayout layout,
        string toolchainDirectory,
        ReleaseChannel? requestedChannel,
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
                manifest.AssetSha256,
                cancellationToken,
                manifest.Rid,
                manifest.Version))
        {
            throw StateCorrupt(
                $"Installed toolchain '{toolchainDirectory}' failed manifest verification.",
                "Reinstall the toolchain; Eidosup will not register modified or incomplete files.");
        }

        var selectors = new List<ToolchainSelectorState>
        {
            new(manifest.Version, ToolchainSelectorKind.ExactVersion, manifest.ToolchainId, manifest.InstalledAt)
        };
        if (requestedChannel is { } channel)
        {
            if (!Enum.IsDefined(channel))
            {
                throw new ArgumentOutOfRangeException(nameof(requestedChannel), channel, "Unknown release channel.");
            }

            selectors.Add(new ToolchainSelectorState(
                channel.ToString().ToLowerInvariant(),
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
        return await LoadReconcileAndWriteAsync(layout, selectors, cancellationToken);
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
        if (!state.Toolchains.SequenceEqual(scan.Toolchains) ||
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
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(layout.StateDirectory, FileName);
        var backupPath = Path.Combine(layout.StateDirectory, BackupFileName);
        var primary = await TryLoadAsync(path, cancellationToken);
        if (primary.Status == StateLoadStatus.Unsupported)
        {
            throw Unsupported(path, primary.Schema);
        }

        var mustWrite = primary.Status != StateLoadStatus.Valid;
        var backupResult = await TryLoadAsync(backupPath, cancellationToken);
        if (backupResult.Status == StateLoadStatus.Unsupported)
        {
            throw Unsupported(backupPath, backupResult.Schema);
        }

        var basis = primary.State ?? backupResult.State;

        var scan = await ScanToolchainsAsync(layout, cancellationToken);
        var now = _clock();
        var reconciled = Reconcile(basis ?? ToolchainState.Empty(now), scan, selectors, now);
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
        DateTimeOffset now)
    {
        var installedIds = scan.Toolchains.Select(static toolchain => toolchain.Id)
            .ToHashSet(StringComparer.Ordinal);
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

                selectors[selector.Selector] = selector;
            }
        }

        var orderedSelectors = selectors.Values.OrderBy(static selector => selector.Selector, StringComparer.Ordinal).ToArray();
        var defaultState = basis.Default;
        if (defaultState != null &&
            (!installedIds.Contains(defaultState.ToolchainId) ||
             !selectors.TryGetValue(defaultState.Selector, out var selected) ||
             !string.Equals(selected.ToolchainId, defaultState.ToolchainId, StringComparison.Ordinal)))
        {
            defaultState = null;
        }

        return new ToolchainState(
            ToolchainState.CurrentSchema,
            basis.Revision,
            now,
            scan.Toolchains,
            orderedSelectors,
            defaultState,
            basis.ActivationHistory,
            basis.Transactions,
            scan.UnmanagedDirectories);
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
                    manifest.AssetSha256,
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
                manifest.ManifestSha256,
                installManifestSha256,
                manifest.ReleaseTag,
                manifest.Source,
                manifest.AssetName,
                manifest.AssetSha256,
                manifest.AssetSize,
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

            if (schema != ToolchainState.CurrentSchema)
            {
                return new StateLoadResult(StateLoadStatus.Unsupported, null, schema);
            }

            var state = document.RootElement.Deserialize<ToolchainState>(JsonOptions);
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
            state.UnmanagedDirectories == null)
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
                !ToolchainIdentity.IsCanonicalSha256(toolchain.ManifestSha256) ||
                !ToolchainIdentity.IsCanonicalSha256(toolchain.InstallManifestSha256) ||
                !ToolchainIdentity.IsCanonicalSha256(toolchain.AssetSha256) ||
                toolchain.AssetSize <= 0 ||
                toolchain.InstalledAt == default ||
                string.IsNullOrWhiteSpace(toolchain.ReleaseTag) ||
                string.IsNullOrWhiteSpace(toolchain.Source) ||
                string.IsNullOrWhiteSpace(toolchain.AssetName) ||
                !toolchainIds.Add(toolchain.Id))
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
                !ToolchainIdentity.IsValidId(activation.ToolchainId) ||
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
                transaction.ToolchainId != null && !ToolchainIdentity.IsValidId(transaction.ToolchainId) ||
                !IsSafeName(transaction.JournalFile) ||
                transaction.StartedAt == default ||
                transaction.UpdatedAt < transaction.StartedAt))
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
        left.Toolchains.SequenceEqual(right.Toolchains) &&
        left.Selectors.SequenceEqual(right.Selectors) &&
        Equals(left.Default, right.Default) &&
        left.ActivationHistory.SequenceEqual(right.ActivationHistory) &&
        left.Transactions.SequenceEqual(right.Transactions) &&
        left.UnmanagedDirectories.SequenceEqual(right.UnmanagedDirectories);

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
