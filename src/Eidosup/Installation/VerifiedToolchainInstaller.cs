using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Toolchains;

namespace Eidosup.Installation;

public enum InstallDisposition
{
    Installed,
    Replaced,
    AlreadyInstalled
}

public sealed record VerifiedInstallRequest(
    EidosReleaseInfo Release,
    EidosReleaseAsset BundleAsset,
    EidosReleaseAsset ChecksumAsset,
    PlatformContext Platform,
    ToolInstallLayout Layout,
    string Source,
    bool Force);

public sealed record VerifiedInstallResult(
    InstallDisposition Disposition,
    string ToolchainId,
    string ToolchainDirectory,
    string AssetSha256,
    long AssetSize,
    bool CacheHit,
    bool Resumed);

public enum InstallCheckpoint
{
    Staged,
    PreviousMoved,
    TargetCommitted
}

public interface IInstallFaultInjector
{
    Task OnCheckpointAsync(InstallCheckpoint checkpoint, CancellationToken cancellationToken);
}

public sealed class NoInstallFaultInjector : IInstallFaultInjector
{
    public Task OnCheckpointAsync(InstallCheckpoint checkpoint, CancellationToken cancellationToken) =>
        Task.CompletedTask;
}

public sealed class VerifiedToolchainInstaller : IDisposable
{
    private static readonly JsonSerializerOptions JournalJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly DownloadManager _downloadManager;
    private readonly SafeZipExtractor _extractor;
    private readonly IInstallFaultInjector _faultInjector;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _lockTimeout;
    private readonly bool _ownsDownloadManager;

    public VerifiedToolchainInstaller(
        DownloadManager? downloadManager = null,
        SafeZipExtractor? extractor = null,
        IInstallFaultInjector? faultInjector = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? lockTimeout = null)
    {
        _downloadManager = downloadManager ?? new DownloadManager();
        _extractor = extractor ?? new SafeZipExtractor();
        _faultInjector = faultInjector ?? new NoInstallFaultInjector();
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(30);
        _ownsDownloadManager = downloadManager == null;
    }

    public async Task<VerifiedInstallResult> InstallAsync(
        VerifiedInstallRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        Directory.CreateDirectory(request.Layout.CacheDirectory);
        Directory.CreateDirectory(request.Layout.ToolchainsDirectory);
        Directory.CreateDirectory(request.Layout.TransactionDirectory);
        Directory.CreateDirectory(request.Layout.StagingDirectory);
        Directory.CreateDirectory(request.Layout.BackupDirectory);

        await using var operationLock = await InstallOperationLock.AcquireAsync(
            request.Layout.LockDirectory,
            _lockTimeout,
            cancellationToken);
        await RecoverAsync(request.Layout, cancellationToken);

        var checksumText = await _downloadManager.DownloadChecksumManifestAsync(
            request.ChecksumAsset,
            cancellationToken);
        var expectedSha256 = ChecksumManifest.Parse(checksumText)
            .GetRequiredChecksum(request.BundleAsset.Name);
        var identity = ToolchainIdentity.Create(
            request.Release.NormalizedVersion,
            request.Platform.Rid,
            request.Source,
            request.Release.TagName,
            request.BundleAsset.Name,
            expectedSha256,
            request.BundleAsset.Size ?? throw new EidosupException(
                EidosupErrorCode.InvalidReleaseMetadata,
                EidosupExitCodes.InvalidRelease,
                $"Release asset '{request.BundleAsset.Name}' does not declare its size.",
                "Publish release metadata with a positive asset size before installing it."));
        var toolchainDirectory = request.Layout.GetToolchainDirectory(identity.Id);

        var existingManifest = await InstallManifest.TryReadAsync(
            toolchainDirectory,
            cancellationToken);
        var installedExecutable = Path.Combine(
            toolchainDirectory,
            request.Platform.ExecutableName);
        if (!request.Force &&
            existingManifest != null &&
            File.Exists(installedExecutable) &&
            await existingManifest.VerifyAsync(
                toolchainDirectory,
                expectedSha256,
                cancellationToken,
                request.Platform.Rid,
                request.Release.NormalizedVersion))
        {
            return new VerifiedInstallResult(
                InstallDisposition.AlreadyInstalled,
                identity.Id,
                toolchainDirectory,
                expectedSha256,
                existingManifest.AssetSize,
                CacheHit: false,
                Resumed: false);
        }

        if (!request.Force && Directory.Exists(toolchainDirectory))
        {
            throw InstallManifest.Conflict(toolchainDirectory);
        }

        var hadPreviousTarget = Directory.Exists(toolchainDirectory);
        var download = await _downloadManager.DownloadArtifactAsync(
            request.BundleAsset,
            request.Layout.CacheDirectory,
            expectedSha256,
            cancellationToken,
            progress);
        var transactionId = Guid.NewGuid().ToString("N");
        var stageDirectory = Path.Combine(request.Layout.StagingDirectory, $"install-{transactionId}");
        var backupDirectory = Path.Combine(request.Layout.BackupDirectory, $"install-{transactionId}");
        var journalPath = Path.Combine(request.Layout.TransactionDirectory, $"install-{transactionId}.json");
        var journal = new InstallJournal(
            InstallJournal.CurrentSchema,
            transactionId,
            InstallJournalState.Started,
            toolchainDirectory,
            stageDirectory,
            backupDirectory,
            expectedSha256,
            request.Platform.Rid,
            request.Release.NormalizedVersion,
            identity.Id,
            hadPreviousTarget);
        await WriteJournalAsync(journalPath, journal, cancellationToken);

        try
        {
            Directory.CreateDirectory(stageDirectory);
            var extraction = await _extractor.ExtractAsync(download.Path, stageDirectory, cancellationToken);
            var executablePath = Path.Combine(stageDirectory, request.Platform.ExecutableName);
            if (!File.Exists(executablePath))
            {
                throw new EidosupException(
                    EidosupErrorCode.InstallFailure,
                    EidosupExitCodes.InstallFailure,
                    $"Verified bundle '{request.BundleAsset.Name}' does not contain '{request.Platform.ExecutableName}'.",
                    "Publish a bundle with the expected host executable at the archive root.");
            }

            EnsureExecutablePermission(executablePath);
            var manifest = new InstallManifest(
                InstallManifest.CurrentSchema,
                identity.Id,
                identity.ManifestSha256,
                request.Release.TagName,
                request.Release.NormalizedVersion,
                request.Platform.Rid,
                request.Source,
                request.BundleAsset.Name,
                expectedSha256,
                download.Size,
                _clock(),
                extraction.Files);
            await manifest.WriteAsync(stageDirectory, cancellationToken);
            journal = journal with { State = InstallJournalState.Staged };
            await WriteJournalAsync(journalPath, journal, cancellationToken);
            await _faultInjector.OnCheckpointAsync(InstallCheckpoint.Staged, cancellationToken);

            var replaced = Directory.Exists(toolchainDirectory);
            if (replaced)
            {
                Directory.Move(toolchainDirectory, backupDirectory);
            }

            journal = journal with { State = InstallJournalState.PreviousMoved };
            await WriteJournalAsync(journalPath, journal, cancellationToken);
            await _faultInjector.OnCheckpointAsync(InstallCheckpoint.PreviousMoved, cancellationToken);

            Directory.Move(stageDirectory, toolchainDirectory);
            journal = journal with { State = InstallJournalState.TargetCommitted };
            await WriteJournalAsync(journalPath, journal, cancellationToken);
            await _faultInjector.OnCheckpointAsync(InstallCheckpoint.TargetCommitted, cancellationToken);

            DeleteDirectoryIfExists(backupDirectory, request.Layout.BackupDirectory);
            File.Delete(journalPath);
            return new VerifiedInstallResult(
                replaced ? InstallDisposition.Replaced : InstallDisposition.Installed,
                identity.Id,
                toolchainDirectory,
                expectedSha256,
                download.Size,
                download.CacheHit,
                download.Resumed);
        }
        catch (Exception exception)
        {
            if (journal.State == InstallJournalState.TargetCommitted)
            {
                var committedManifest = await InstallManifest.TryReadAsync(
                    toolchainDirectory,
                    CancellationToken.None);
                if (committedManifest != null &&
                    await committedManifest.VerifyAsync(
                        toolchainDirectory,
                        expectedSha256,
                        CancellationToken.None,
                        request.Platform.Rid,
                        request.Release.NormalizedVersion))
                {
                    throw new EidosupException(
                        EidosupErrorCode.InstallFailure,
                        EidosupExitCodes.InstallFailure,
                        "The verified toolchain was committed, but transaction cleanup did not finish.",
                        $"Retry the same install to recover journal '{journalPath}' and finish cleanup.",
                        exception);
                }
            }

            try
            {
                await RollBackAsync(journal, journalPath, request.Layout);
            }
            catch (Exception rollbackException)
            {
                throw new EidosupException(
                    EidosupErrorCode.InstallFailure,
                    EidosupExitCodes.InstallFailure,
                    "Toolchain installation failed and automatic rollback could not complete.",
                    $"Run 'eidosup doctor --verbose' and preserve transaction journal '{journalPath}'.",
                    new AggregateException(exception, rollbackException));
            }

            throw;
        }
    }

    public async Task RecoverAsync(ToolInstallLayout layout, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(layout.TransactionDirectory))
        {
            return;
        }

        foreach (var journalPath in Directory.EnumerateFiles(
                     layout.TransactionDirectory,
                     "install-*.json",
                     SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallJournal journal;
            try
            {
                await using var stream = File.OpenRead(journalPath);
                journal = await JsonSerializer.DeserializeAsync<InstallJournal>(
                              stream,
                              JournalJsonOptions,
                              cancellationToken)
                          ?? throw new JsonException("Empty transaction journal.");
            }
            catch (JsonException exception)
            {
                throw new EidosupException(
                    EidosupErrorCode.InstallFailure,
                    EidosupExitCodes.InstallFailure,
                    $"Transaction journal '{journalPath}' is invalid.",
                    "Preserve the journal and inspect it before retrying installation.",
                    exception);
            }

            ValidateJournal(layout, journal, journalPath);
            var targetManifest = await InstallManifest.TryReadAsync(journal.TargetDirectory, cancellationToken);
            var targetValid = targetManifest != null &&
                              await targetManifest.VerifyAsync(
                                  journal.TargetDirectory,
                                  journal.ExpectedSha256,
                                  cancellationToken,
                                  journal.Rid,
                                  expectedVersion: journal.Version);
            if (targetValid)
            {
                DeleteDirectoryIfExists(journal.StageDirectory, layout.StagingDirectory);
                DeleteDirectoryIfExists(journal.BackupDirectory, layout.BackupDirectory);
                File.Delete(journalPath);
                continue;
            }

            if (Directory.Exists(journal.BackupDirectory))
            {
                DeleteDirectoryIfExists(journal.TargetDirectory, layout.ToolchainsDirectory);
                Directory.Move(journal.BackupDirectory, journal.TargetDirectory);
                DeleteDirectoryIfExists(journal.StageDirectory, layout.StagingDirectory);
                File.Delete(journalPath);
                continue;
            }

            if (journal.State is InstallJournalState.Started or InstallJournalState.Staged)
            {
                DeleteDirectoryIfExists(journal.StageDirectory, layout.StagingDirectory);
                File.Delete(journalPath);
                continue;
            }

            if (journal.HadPreviousTarget)
            {
                throw new EidosupException(
                    EidosupErrorCode.InstallFailure,
                    EidosupExitCodes.InstallFailure,
                    $"Transaction '{journal.Id}' lost its required rollback copy.",
                    $"Preserve '{journal.TargetDirectory}' and '{journalPath}' for manual recovery.");
            }

            if (Directory.Exists(journal.TargetDirectory))
            {
                DeleteDirectoryIfExists(journal.TargetDirectory, layout.ToolchainsDirectory);
            }

            DeleteDirectoryIfExists(journal.StageDirectory, layout.StagingDirectory);
            File.Delete(journalPath);
        }
    }

    public void Dispose()
    {
        if (_ownsDownloadManager)
        {
            _downloadManager.Dispose();
        }
    }

    private static Task RollBackAsync(
        InstallJournal journal,
        string journalPath,
        ToolInstallLayout layout)
    {
        if (Directory.Exists(journal.BackupDirectory))
        {
            DeleteDirectoryIfExists(journal.TargetDirectory, layout.ToolchainsDirectory);
            Directory.Move(journal.BackupDirectory, journal.TargetDirectory);
        }
        else if (journal.State == InstallJournalState.TargetCommitted)
        {
            DeleteDirectoryIfExists(journal.TargetDirectory, layout.ToolchainsDirectory);
        }

        DeleteDirectoryIfExists(journal.StageDirectory, layout.StagingDirectory);
        File.Delete(journalPath);
        return Task.CompletedTask;
    }

    private static async Task WriteJournalAsync(
        string journalPath,
        InstallJournal journal,
        CancellationToken cancellationToken)
    {
        var temporaryPath = journalPath + $".{Guid.NewGuid():N}.tmp";
        await using (var stream = new FileStream(
                         temporaryPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 16 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, journal, JournalJsonOptions, cancellationToken);
            await stream.FlushAsync(cancellationToken);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporaryPath, journalPath, overwrite: true);
    }

    private static void ValidateJournal(ToolInstallLayout layout, InstallJournal journal, string journalPath)
    {
        var valid = false;
        try
        {
            var targetParent = Path.GetDirectoryName(Path.GetFullPath(journal.TargetDirectory));
            var targetName = Path.GetFileName(
                journal.TargetDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            valid = journal.Schema == InstallJournal.CurrentSchema &&
                    Guid.TryParseExact(journal.Id, "N", out _) &&
                    Enum.IsDefined(journal.State) &&
                    ChecksumManifest.IsSha256(journal.ExpectedSha256) &&
                    PlatformContext.IsSupportedRid(journal.Rid) &&
                    SemanticVersion.TryParse(journal.Version, out var targetVersion) &&
                    targetVersion != null &&
                    string.Equals(targetVersion.ToString(), journal.Version, StringComparison.Ordinal) &&
                    ToolchainIdentity.IsValidId(journal.ToolchainId) &&
                    string.Equals(targetName, journal.ToolchainId, StringComparison.Ordinal) &&
                    targetParent != null &&
                    ToolInstallLayout.PathEquals(targetParent, layout.ToolchainsDirectory) &&
                    !ToolInstallLayout.PathEquals(journal.TargetDirectory, layout.StagingDirectory) &&
                    !ToolInstallLayout.PathEquals(journal.TargetDirectory, layout.BackupDirectory) &&
                    ToolInstallLayout.PathEquals(
                        journal.StageDirectory,
                        Path.Combine(layout.StagingDirectory, $"install-{journal.Id}")) &&
                    ToolInstallLayout.PathEquals(
                        journal.BackupDirectory,
                        Path.Combine(layout.BackupDirectory, $"install-{journal.Id}")) &&
                    ToolInstallLayout.PathEquals(
                        journalPath,
                        Path.Combine(layout.TransactionDirectory, $"install-{journal.Id}.json"));
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            valid = false;
        }

        if (!valid)
        {
            throw new EidosupException(
                EidosupErrorCode.InstallFailure,
                EidosupExitCodes.InstallFailure,
                $"Transaction journal '{journalPath}' contains unsupported or unsafe paths.",
                "Do not edit or execute paths from this journal; preserve it for manual inspection.");
        }
    }

    private static void DeleteDirectoryIfExists(string path, string allowedParent)
    {
        if (!ToolInstallLayout.IsWithin(allowedParent, path))
        {
            throw new EidosupException(
                EidosupErrorCode.InstallFailure,
                EidosupExitCodes.InstallFailure,
                $"Refusing to delete path '{path}' outside managed directory '{allowedParent}'.");
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void EnsureExecutablePermission(string executablePath)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        File.SetUnixFileMode(
            executablePath,
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute |
            UnixFileMode.GroupRead |
            UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead |
            UnixFileMode.OtherExecute);
    }

    private enum InstallJournalState
    {
        Started,
        Staged,
        PreviousMoved,
        TargetCommitted
    }

    private sealed record InstallJournal(
        int Schema,
        string Id,
        InstallJournalState State,
        string TargetDirectory,
        string StageDirectory,
        string BackupDirectory,
        string ExpectedSha256,
        string Rid,
        string Version,
        string ToolchainId,
        bool HadPreviousTarget = false)
    {
        public const int CurrentSchema = 2;
    }
}
