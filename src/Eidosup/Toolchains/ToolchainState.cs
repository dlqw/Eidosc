namespace Eidosup.Toolchains;

public sealed record ToolchainState(
    int Schema,
    long Revision,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<InstalledToolchainState> Toolchains,
    IReadOnlyList<ToolchainSelectorState> Selectors,
    ToolchainDefaultState? Default,
    bool DefaultConfigured,
    IReadOnlyList<ToolchainActivationState> ActivationHistory,
    IReadOnlyList<ToolchainTransactionState> Transactions,
    IReadOnlyList<UnmanagedToolchainState> UnmanagedDirectories,
    IReadOnlyList<CustomToolchainState> CustomToolchains,
    IReadOnlyList<ToolchainOverrideState> Overrides)
{
    public const int CurrentSchema = 2;

    public static ToolchainState Empty(DateTimeOffset updatedAt) => new(
        CurrentSchema,
        Revision: 1,
        updatedAt,
        [],
        [],
        Default: null,
        DefaultConfigured: false,
        [],
        [],
        [],
        [],
        []);
}

public sealed record CustomToolchainState(
    string Name,
    string Selector,
    string ToolchainId,
    string RootDirectory,
    string CommandPath,
    string RuntimePath,
    DateTimeOffset LinkedAt);

public sealed record ToolchainOverrideState(
    string Directory,
    string Selector,
    DateTimeOffset SetAt);

public sealed record InstalledToolchainState(
    string Id,
    string Version,
    string Rid,
    string ManifestSha256,
    string InstallManifestSha256,
    string ReleaseTag,
    string Source,
    string AssetName,
    string AssetSha256,
    long AssetSize,
    DateTimeOffset InstalledAt);

public enum ToolchainSelectorKind
{
    ExactVersion,
    Channel,
    Custom
}

public sealed record ToolchainSelectorState(
    string Selector,
    ToolchainSelectorKind Kind,
    string ToolchainId,
    DateTimeOffset ResolvedAt);

public sealed record ToolchainDefaultState(
    string Selector,
    string ToolchainId,
    DateTimeOffset SetAt);

public enum ToolchainActivationReason
{
    DefaultChanged,
    SelectorChanged,
    ChannelUpdated,
    Rollback,
    ProjectOverride
}

public sealed record ToolchainActivationState(
    string Selector,
    string ToolchainId,
    ToolchainActivationReason Reason,
    DateTimeOffset ActivatedAt);

public enum ToolchainTransactionKind
{
    Install,
    Update,
    Uninstall,
    DefaultChange,
    Rollback,
    CustomLink,
    OverrideChange
}

public enum ToolchainTransactionStatus
{
    Started,
    Prepared,
    Committed,
    RolledBack
}

public sealed record ToolchainTransactionState(
    string Id,
    ToolchainTransactionKind Kind,
    ToolchainTransactionStatus Status,
    string? ToolchainId,
    string JournalFile,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt);

public enum UnmanagedToolchainReason
{
    LegacyLayout,
    MissingManifest,
    UnsupportedManifest,
    InvalidManifest
}

public sealed record UnmanagedToolchainState(
    string DirectoryName,
    UnmanagedToolchainReason Reason,
    string Guidance);

internal sealed record ToolchainStateV1(
    int Schema,
    long Revision,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<InstalledToolchainState> Toolchains,
    IReadOnlyList<ToolchainSelectorState> Selectors,
    ToolchainDefaultState? Default,
    bool DefaultConfigured,
    IReadOnlyList<ToolchainActivationState> ActivationHistory,
    IReadOnlyList<ToolchainTransactionState> Transactions,
    IReadOnlyList<UnmanagedToolchainState> UnmanagedDirectories);
