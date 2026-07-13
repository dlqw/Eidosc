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
    LoadedToolchainDistribution Distribution,
    ToolchainComponentPlan Plan,
    PlatformContext Platform,
    ToolInstallLayout Layout,
    string Source,
    bool Force);

public sealed record VerifiedInstallResult(
    InstallDisposition Disposition,
    string ToolchainId,
    string ToolchainDirectory,
    string DistributionManifestSha256,
    long ArtifactBytes,
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

        ValidateRequest(request);
        var identity = ToolchainIdentity.Create(
            request.Release.NormalizedVersion,
            request.Platform.Rid,
            request.Source,
            request.Release.TagName,
            request.Distribution.ManifestAsset.Name,
            request.Distribution.ManifestSha256,
            request.Plan.ComponentIds,
            request.Plan.Profile,
            request.Plan.ExplicitComponents,
            request.Plan.ExplicitTargets);
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
                request.Distribution.ManifestSha256,
                cancellationToken,
                request.Platform.Rid,
                request.Release.NormalizedVersion))
        {
            return new VerifiedInstallResult(
                InstallDisposition.AlreadyInstalled,
                identity.Id,
                toolchainDirectory,
                request.Distribution.ManifestSha256,
                existingManifest.Artifacts.Sum(static artifact => artifact.Size),
                CacheHit: false,
                Resumed: false);
        }

        if (!request.Force && Directory.Exists(toolchainDirectory))
        {
            throw InstallManifest.Conflict(toolchainDirectory);
        }

        var hadPreviousTarget = Directory.Exists(toolchainDirectory);
        var downloads = await DownloadArtifactsAsync(request, progress, cancellationToken);
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
            request.Distribution.ManifestSha256,
            request.Platform.Rid,
            request.Release.NormalizedVersion,
            identity.Id,
            hadPreviousTarget);
        await WriteJournalAsync(journalPath, journal, cancellationToken);

        try
        {
            Directory.CreateDirectory(stageDirectory);
            var installedFiles = await MaterializeComponentsAsync(
                request,
                downloads,
                stageDirectory,
                cancellationToken);
            var executablePath = Path.Combine(stageDirectory, request.Platform.ExecutableName);
            if (!File.Exists(executablePath))
            {
                throw new EidosupException(
                    EidosupErrorCode.InstallFailure,
                    EidosupExitCodes.InstallFailure,
                    $"Selected component composition does not contain '{request.Platform.ExecutableName}'.",
                    "Publish eidosc-core with the expected host executable and include it in every profile.");
            }

            EnsureExecutablePermission(executablePath);
            var components = request.Plan.Components.Select(component => new InstalledComponent(
                component.Id,
                component.Name,
                component.Version,
                component.Required,
                component.Target,
                component.Files.Select(static file => file.Path).ToArray())).ToArray();
            var artifacts = request.Plan.Components.Select(static component => component.Artifact)
                .DistinctBy(static artifact => artifact.Name, StringComparer.Ordinal)
                .Select(static artifact => new InstalledArtifact(artifact.Name, artifact.Sha256, artifact.Size))
                .ToArray();
            var manifest = new InstallManifest(
                InstallManifest.CurrentSchema,
                identity.Id,
                identity.IdentitySha256,
                identity.CompositionSha256,
                request.Distribution.ManifestAsset.Name,
                request.Distribution.ManifestSha256,
                request.Release.TagName,
                request.Release.NormalizedVersion,
                request.Platform.Rid,
                request.Source,
                request.Plan.Profile.ToString().ToLowerInvariant(),
                request.Plan.ExplicitComponents,
                request.Plan.ExplicitTargets,
                components,
                request.Plan.Targets.Select(static target => target.Name).ToArray(),
                artifacts,
                _clock(),
                installedFiles);
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
                request.Distribution.ManifestSha256,
                downloads.Sum(static download => download.Result.Size),
                downloads.All(static download => download.Result.CacheHit),
                downloads.Any(static download => download.Result.Resumed));
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
                        request.Distribution.ManifestSha256,
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

    private async Task<IReadOnlyList<DownloadedArtifact>> DownloadArtifactsAsync(
        VerifiedInstallRequest request,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        var releaseAssets = request.Release.Assets.ToDictionary(static asset => asset.Name, StringComparer.Ordinal);
        var artifacts = request.Plan.Components.Select(static component => component.Artifact)
            .DistinctBy(static artifact => artifact.Name, StringComparer.Ordinal)
            .ToArray();
        var downloads = new List<DownloadedArtifact>(artifacts.Length);
        foreach (var artifact in artifacts)
        {
            var releaseAsset = releaseAssets[artifact.Name];
            var result = await _downloadManager.DownloadArtifactAsync(
                releaseAsset,
                request.Layout.CacheDirectory,
                artifact.Sha256,
                cancellationToken,
                progress);
            downloads.Add(new DownloadedArtifact(artifact, result));
        }

        return downloads;
    }

    private async Task<IReadOnlyList<InstalledFile>> MaterializeComponentsAsync(
        VerifiedInstallRequest request,
        IReadOnlyList<DownloadedArtifact> downloads,
        string stageDirectory,
        CancellationToken cancellationToken)
    {
        var installed = new List<InstalledFile>();
        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        for (var index = 0; index < downloads.Count; index++)
        {
            var download = downloads[index];
            var extractionDirectory = Path.Combine(stageDirectory, $".artifact-{index}");
            Directory.CreateDirectory(extractionDirectory);
            var extraction = await _extractor.ExtractAsync(
                download.Result.Path,
                extractionDirectory,
                cancellationToken);
            var extracted = extraction.Files.ToDictionary(static file => file.Path, pathComparer);
            foreach (var component in request.Plan.Components.Where(component =>
                         string.Equals(component.Artifact.Name, download.Artifact.Name, StringComparison.Ordinal)))
            {
                foreach (var file in component.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!extracted.TryGetValue(file.Path, out var extractedFile) ||
                        extractedFile.Size != file.Size ||
                        !string.Equals(extractedFile.Sha256, file.Sha256, StringComparison.Ordinal))
                    {
                        throw new EidosupException(
                            EidosupErrorCode.IntegrityFailure,
                            EidosupExitCodes.IntegrityFailure,
                            $"Component '{component.Id}' file '{file.Path}' does not match its signed manifest.",
                            "Reject the release and regenerate its component file ownership metadata from the exact artifact.");
                    }

                    var sourcePath = Path.GetFullPath(Path.Combine(
                        extractionDirectory,
                        file.Path.Replace('/', Path.DirectorySeparatorChar)));
                    var destinationPath = Path.GetFullPath(Path.Combine(
                        stageDirectory,
                        file.Path.Replace('/', Path.DirectorySeparatorChar)));
                    if (!ToolInstallLayout.IsWithin(extractionDirectory, sourcePath) ||
                        !ToolInstallLayout.IsWithin(stageDirectory, destinationPath))
                    {
                        throw new EidosupException(
                            EidosupErrorCode.UnsafeArchive,
                            EidosupExitCodes.UnsafeArchive,
                            $"Component file '{file.Path}' escapes a managed staging directory.");
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                    File.Copy(sourcePath, destinationPath, overwrite: false);
                    SetInstalledFileMode(destinationPath, file.Executable);
                    installed.Add(new InstalledFile(file.Path, file.Size, file.Sha256, file.Executable));
                }
            }

            Directory.Delete(extractionDirectory, recursive: true);
        }

        return installed;
    }

    private static void ValidateRequest(VerifiedInstallRequest request)
    {
        var manifest = request.Distribution.Manifest;
        manifest.Validate(request.Release.NormalizedVersion, request.Platform.Rid);
        if (!string.Equals(manifest.Eidosc.Version, request.Release.NormalizedVersion, StringComparison.Ordinal) ||
            !string.Equals(manifest.Host, request.Platform.Rid, StringComparison.Ordinal) ||
            request.Plan.Components is not { Count: > 0 } ||
            request.Plan.ComponentIds.Distinct(StringComparer.Ordinal).Count() != request.Plan.ComponentIds.Count)
        {
            throw InvalidRelease("The component install plan does not match the selected release or host.");
        }

        var selected = request.Plan.ComponentIds.ToHashSet(StringComparer.Ordinal);
        var profile = manifest.GetProfile(request.Plan.Profile).Components;
        var required = manifest.Components.Where(static component => component.Required)
            .Select(static component => component.Id);
        if (profile.Concat(required).Any(component => !selected.Contains(component)))
        {
            throw InvalidRelease("The component install plan omits a profile or required component.");
        }

        foreach (var component in request.Plan.Components)
        {
            if (!Equals(component, manifest.GetComponent(component.Id)) ||
                component.Dependencies.Any(dependency => !selected.Contains(dependency)) ||
                component.Conflicts.Any(selected.Contains))
            {
                throw InvalidRelease($"Component install plan for '{component.Id}' violates signed metadata.");
            }
        }

        var targetNames = request.Plan.Targets.Select(static target => target.Name).ToHashSet(StringComparer.Ordinal);
        if (request.Plan.Targets.Any(target =>
                !Equals(target, manifest.GetTarget(target.Name)) ||
                !selected.Contains(target.Component)) ||
            manifest.Targets.Any(target => selected.Contains(target.Component) && !targetNames.Contains(target.Name)))
        {
            throw InvalidRelease("The target install plan does not match selected runtime components.");
        }

        var releaseAssets = new Dictionary<string, EidosReleaseAsset>(StringComparer.Ordinal);
        if (request.Release.Assets.Any(asset => asset == null || !releaseAssets.TryAdd(asset.Name, asset)))
        {
            throw InvalidRelease("Release metadata contains null or duplicate assets.");
        }

        foreach (var artifact in request.Plan.Components.Select(static component => component.Artifact)
                     .DistinctBy(static artifact => artifact.Name, StringComparer.Ordinal))
        {
            if (!releaseAssets.TryGetValue(artifact.Name, out var releaseAsset) ||
                releaseAsset.Size != artifact.Size ||
                releaseAsset.Sha256 != null && !string.Equals(releaseAsset.Sha256, artifact.Sha256, StringComparison.Ordinal))
            {
                throw InvalidRelease($"Component artifact '{artifact.Name}' is not bound to the selected release.");
            }
        }
    }

    private static void SetInstalledFileMode(string destinationPath, bool executable)
    {
        if (!OperatingSystem.IsWindows())
        {
            var mode = UnixFileMode.UserRead |
                       UnixFileMode.UserWrite |
                       UnixFileMode.GroupRead |
                       UnixFileMode.OtherRead;
            if (executable)
            {
                mode |= UnixFileMode.UserExecute |
                        UnixFileMode.GroupExecute |
                        UnixFileMode.OtherExecute;
            }

            File.SetUnixFileMode(destinationPath, mode);
        }
    }

    private static EidosupException InvalidRelease(string message) => new(
        EidosupErrorCode.InvalidReleaseMetadata,
        EidosupExitCodes.InvalidRelease,
        message,
        "Re-resolve the component plan from the exact signed distribution manifest before installing.");

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

    private sealed record DownloadedArtifact(
        ToolchainComponentArtifact Artifact,
        DownloadResult Result);

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
