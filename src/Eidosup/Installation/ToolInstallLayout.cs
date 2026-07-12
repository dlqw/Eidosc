namespace Eidosup.Installation;

public sealed record ToolInstallLayout(
    string RootDirectory,
    string DownloadDirectory,
    string CacheDirectory,
    string VersionsDirectory,
    string VersionDirectory,
    string RuntimeDirectory,
    string StateDirectory,
    string LockDirectory,
    string TransactionDirectory,
    string StagingDirectory,
    string BackupDirectory)
{
    public static ToolInstallLayout Create(
        PlatformContext platform,
        string version,
        string? installRoot,
        string? downloadRoot)
    {
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(installRoot)
            ? GetDefaultInstallRoot(platform)
            : installRoot);
        var downloads = Path.GetFullPath(string.IsNullOrWhiteSpace(downloadRoot)
            ? Path.Combine(root, "downloads")
            : downloadRoot);
        var versions = EnsureChild(root, Path.Combine(root, "toolchains", "eidosc"));
        var versionDirectory = EnsureChild(versions, Path.Combine(versions, version));
        var state = EnsureChild(root, Path.Combine(root, "state"));
        return new ToolInstallLayout(
            root,
            downloads,
            Path.Combine(downloads, "sha256"),
            versions,
            versionDirectory,
            EnsureChild(versionDirectory, Path.Combine(versionDirectory, "runtime")),
            state,
            EnsureChild(state, Path.Combine(state, "locks")),
            EnsureChild(state, Path.Combine(state, "transactions")),
            EnsureChild(versions, Path.Combine(versions, ".staging")),
            EnsureChild(versions, Path.Combine(versions, ".backup")));
    }

    public static bool IsWithin(string parent, string candidate)
    {
        var normalizedParent = Path.GetFullPath(parent)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var normalizedCandidate = Path.GetFullPath(candidate);
        return normalizedCandidate.StartsWith(
            normalizedParent,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public static bool PathEquals(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static string EnsureChild(string parent, string candidate)
    {
        var fullPath = Path.GetFullPath(candidate);
        if (!IsWithin(parent, fullPath))
        {
            throw new ArgumentException($"Path '{candidate}' escapes the managed root '{parent}'.");
        }

        return fullPath;
    }

    private static string GetDefaultInstallRoot(PlatformContext platform)
    {
        if (platform.IsWindows)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Programs", "Eidos");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "eidos");
    }
}
