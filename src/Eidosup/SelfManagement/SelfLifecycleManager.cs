using System.Diagnostics;
using System.Text.Json;
using Eidosup.Diagnostics;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.Proxies;
using Eidosup.Toolchains;

namespace Eidosup.SelfManagement;

public enum SelfUpdateStatus
{
    Current,
    UpdateAvailable,
    Updated,
    Scheduled
}

public sealed record SelfUpdateResult(
    SelfUpdateStatus Status,
    string CurrentVersion,
    string AvailableVersion,
    string? TargetPath);

public sealed class SelfLifecycleManager
{
    public async Task<SelfUpdateResult> UpdateAsync(
        ToolInstallLayout layout,
        bool checkOnly,
        CancellationToken cancellationToken)
    {
        using var source = new SelfReleaseClient(
            Environment.GetEnvironmentVariable("EIDOSUP_SELF_REPOSITORY") ?? "dlqw/Eidosc");
        var platform = PlatformContext.Detect();
        var release = await source.ResolveLatestAsync(platform, cancellationToken);
        var current = SelfReleaseClient.CurrentVersion();
        var available = SemanticVersion.Parse(release.Version);
        if (available <= current)
        {
            return new SelfUpdateResult(SelfUpdateStatus.Current, current.ToString(), available.ToString(), null);
        }

        if (checkOnly)
        {
            return new SelfUpdateResult(SelfUpdateStatus.UpdateAvailable, current.ToString(), available.ToString(), null);
        }

        Directory.CreateDirectory(layout.CacheDirectory);
        using var downloader = new DownloadManager();
        var checksumText = await downloader.DownloadChecksumManifestAsync(release.Checksums, cancellationToken);
        var checksum = ChecksumManifest.Parse(checksumText).GetRequiredChecksum(release.Binary.Name);
        var downloaded = await downloader.DownloadArtifactAsync(
            release.Binary,
            layout.CacheDirectory,
            checksum,
            cancellationToken);
        var stagingDirectory = Path.Combine(layout.StateDirectory, "self-update");
        Directory.CreateDirectory(stagingDirectory);
        var extension = platform.IsWindows ? ".exe" : string.Empty;
        var staged = Path.Combine(stagingDirectory, $"eidosup-{release.Version}-{Guid.NewGuid():N}{extension}");
        File.Copy(downloaded.Path, staged, overwrite: false);
        if (!OperatingSystem.IsWindows())
        {
            SetExecutableMode(staged);
        }

        await VerifyCandidateAsync(staged, release.Version, cancellationToken);

        var target = Path.Combine(layout.BinDirectory, $"eidosup{extension}");
        if (platform.IsWindows && IsRunningTarget(target))
        {
            StartHelper(staged, "__self-replace", Environment.ProcessId.ToString(), layout.RootDirectory);
            return new SelfUpdateResult(SelfUpdateStatus.Scheduled, current.ToString(), available.ToString(), target);
        }

        await new ShimInstaller(staged).InstallAsync(layout, dryRun: false, cancellationToken);
        File.Delete(staged);
        return new SelfUpdateResult(SelfUpdateStatus.Updated, current.ToString(), available.ToString(), target);
    }

    public async Task<bool> UninstallAsync(
        ToolInstallLayout layout,
        bool keepToolchains,
        CancellationToken cancellationToken)
    {
        ValidateOwnedShims(layout);
        new EnvironmentConfigurator().Remove(layout.RootDirectory, layout.BinDirectory, dryRun: false);
        if (OperatingSystem.IsWindows() &&
            Environment.ProcessPath is { } processPath &&
            ToolInstallLayout.IsWithin(layout.RootDirectory, processPath))
        {
            var helper = Path.Combine(Path.GetTempPath(), $"eidosup-uninstall-{Guid.NewGuid():N}.exe");
            File.Copy(processPath, helper);
            StartHelper(helper, "__self-uninstall", Environment.ProcessId.ToString(), layout.RootDirectory, keepToolchains.ToString());
            return true;
        }

        await DeleteOwnedAsync(layout, keepToolchains, cancellationToken);
        return false;
    }

    public static async Task<int> RunReplacementHelperAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 2 || !int.TryParse(args[0], out var parentId))
        {
            return EidosupExitCodes.InvalidArgument;
        }

        await WaitForExitAsync(parentId, cancellationToken);
        var layout = ToolInstallLayout.Create(PlatformContext.Detect(), args[1], null);
        await new ShimInstaller(Environment.ProcessPath).InstallAsync(layout, dryRun: false, cancellationToken);
        return EidosupExitCodes.Success;
    }

    public static async Task<int> RunUninstallHelperAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length != 3 || !int.TryParse(args[0], out var parentId) || !bool.TryParse(args[2], out var keep))
        {
            return EidosupExitCodes.InvalidArgument;
        }

        await WaitForExitAsync(parentId, cancellationToken);
        var layout = ToolInstallLayout.Create(PlatformContext.Detect(), args[1], null);
        await DeleteOwnedAsync(layout, keep, cancellationToken);
        return EidosupExitCodes.Success;
    }

    public static void CleanupStagedFiles(ToolInstallLayout layout)
    {
        CleanupStagedFiles(layout, Environment.ProcessPath);
    }

    internal static void CleanupStagedFiles(ToolInstallLayout layout, string? currentProcessPath)
    {
        var directory = Path.Combine(layout.StateDirectory, "self-update");
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory))
        {
            if (currentProcessPath != null && ToolInstallLayout.PathEquals(path, currentProcessPath))
            {
                continue;
            }

            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    internal static async Task DeleteOwnedAsync(
        ToolInstallLayout layout,
        bool keepToolchains,
        CancellationToken cancellationToken)
    {
        ToolchainState? state = null;
        var statePath = Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName);
        if (!keepToolchains && File.Exists(statePath))
        {
            state = await ToolchainStateStore.ReadVerifiedAsync(layout, cancellationToken);
        }

        DeleteOwnedShims(layout);
        File.Delete(Path.Combine(layout.RootDirectory, Configuration.EidosupSettingsStore.FileName));
        if (!keepToolchains && state != null)
        {
            foreach (var toolchain in state.Toolchains)
            {
                var directory = layout.GetToolchainDirectory(toolchain.Id);
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }

            DeleteDirectory(layout.StagingDirectory);
            DeleteDirectory(layout.BackupDirectory);
        }

        DeleteDirectory(layout.DownloadDirectory);
        if (keepToolchains)
        {
            DeleteDirectory(layout.LockDirectory);
            DeleteDirectory(layout.TransactionDirectory);
            foreach (var trust in Directory.Exists(layout.StateDirectory)
                         ? Directory.EnumerateFiles(layout.StateDirectory, "metadata-trust-*.json")
                         : [])
            {
                File.Delete(trust);
            }
        }
        else
        {
            DeleteDirectory(layout.StateDirectory);
        }

        DeleteIfEmpty(layout.ToolchainsDirectory);
        DeleteIfEmpty(layout.RootDirectory);
    }

    private static void DeleteOwnedShims(ToolInstallLayout layout)
    {
        var owned = GetOwnedShims(layout);
        foreach (var path in owned.Paths)
        {
            File.Delete(path);
        }

        File.Delete(owned.ManifestPath);
        DeleteIfEmpty(layout.BinDirectory);
    }

    private static void ValidateOwnedShims(ToolInstallLayout layout)
    {
        _ = GetOwnedShims(layout);
    }

    private static OwnedShimSet GetOwnedShims(ToolInstallLayout layout)
    {
        var manifestPath = Path.Combine(layout.BinDirectory, ShimInstaller.ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new EidosupException(
                EidosupErrorCode.InstallFailure,
                EidosupExitCodes.InstallFailure,
                $"Shim ownership manifest '{manifestPath}' is missing.",
                "Eidosup will not delete unowned bin files; restore the manifest or remove the files manually.");
        }

        using var document = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = document.RootElement;
        if (!root.TryGetProperty("schema", out var schema) || schema.GetInt32() != ShimInstaller.ManifestSchema ||
            !root.TryGetProperty("managerFile", out var manager) ||
            !root.TryGetProperty("shimFile", out var shim) ||
            !root.TryGetProperty("sha256", out var sha256) ||
            !ChecksumManifest.IsSha256(sha256.GetString()))
        {
            throw new EidosupException(
                EidosupErrorCode.StateCorrupt,
                EidosupExitCodes.StateCorrupt,
                $"Shim ownership manifest '{manifestPath}' is invalid.");
        }

        var extension = OperatingSystem.IsWindows() ? ".exe" : string.Empty;
        if (!string.Equals(manager.GetString(), "eidosup" + extension, StringComparison.Ordinal) ||
            !string.Equals(shim.GetString(), "eidosc" + extension, StringComparison.Ordinal))
        {
            throw new EidosupException(
                EidosupErrorCode.StateCorrupt,
                EidosupExitCodes.StateCorrupt,
                "Shim ownership manifest contains unexpected manager or shim names.");
        }

        var paths = new List<string>(2);
        foreach (var name in new[] { manager.GetString(), shim.GetString() })
        {
            if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name)
            {
                throw new EidosupException(EidosupErrorCode.StateCorrupt, EidosupExitCodes.StateCorrupt, "Shim ownership path is invalid.");
            }

            var path = Path.Combine(layout.BinDirectory, name);
            if (!File.Exists(path) ||
                !string.Equals(
                    Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant(),
                    sha256.GetString(),
                    StringComparison.Ordinal))
            {
                throw new EidosupException(
                    EidosupErrorCode.StateCorrupt,
                    EidosupExitCodes.StateCorrupt,
                    $"Owned shim file '{path}' no longer matches its manifest digest.",
                    "Preserve the modified file for inspection and remove it manually if appropriate.");
            }

            paths.Add(path);
        }

        return new OwnedShimSet(manifestPath, paths);
    }

    private static async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (ArgumentException)
        {
        }
    }

    private static async Task VerifyCandidateAsync(
        string path,
        string expectedVersion,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--version");
        using var process = Process.Start(start)
                            ?? throw new EidosupException(
                                EidosupErrorCode.InstallFailure,
                                EidosupExitCodes.InstallFailure,
                                "The downloaded Eidosup candidate could not be started on this host.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var output = (await outputTask).Trim();
            _ = await errorTask;
            if (process.ExitCode != 0 ||
                !string.Equals(output.Split('+')[0], expectedVersion, StringComparison.Ordinal))
            {
                throw new EidosupException(
                    EidosupErrorCode.InstallFailure,
                    EidosupExitCodes.InstallFailure,
                    $"The downloaded Eidosup candidate reported '{output}' instead of '{expectedVersion}'.",
                    "The existing manager was preserved; use a correctly versioned release asset.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            throw new EidosupException(
                EidosupErrorCode.InstallFailure,
                EidosupExitCodes.InstallFailure,
                "The downloaded Eidosup candidate did not finish its version check within 30 seconds.");
        }
    }

    private static bool IsRunningTarget(string target) =>
        Environment.ProcessPath != null && ToolInstallLayout.PathEquals(Environment.ProcessPath, target);

    private static void StartHelper(string executable, params string[] arguments)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        _ = Process.Start(start) ?? throw new InvalidOperationException("Failed to start Eidosup lifecycle helper.");
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void DeleteIfEmpty(string path)
    {
        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
        {
            Directory.Delete(path);
        }
    }

    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    private static void SetExecutableMode(string path) =>
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                   UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                                   UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

    private sealed record OwnedShimSet(string ManifestPath, IReadOnlyList<string> Paths);
}
