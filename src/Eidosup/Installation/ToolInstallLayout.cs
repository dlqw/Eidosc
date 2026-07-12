namespace Eidosup.Installation;

public sealed record ToolInstallLayout(
    string RootDirectory,
    string DownloadDirectory,
    string CacheDirectory,
    string ToolchainsDirectory,
    string StateDirectory,
    string LockDirectory,
    string TransactionDirectory,
    string StagingDirectory,
    string BackupDirectory)
{
    public string BinDirectory => EnsureChild(RootDirectory, Path.Combine(RootDirectory, "bin"));

    public static ToolInstallLayout Create(
        PlatformContext platform,
        string? installRoot,
        string? downloadRoot)
    {
        var root = Path.GetFullPath(string.IsNullOrWhiteSpace(installRoot)
            ? GetDefaultInstallRoot(platform)
            : installRoot);
        var downloads = Path.GetFullPath(string.IsNullOrWhiteSpace(downloadRoot)
            ? Path.Combine(root, "downloads")
            : downloadRoot);
        var toolchains = EnsureChild(root, Path.Combine(root, "toolchains"));
        var state = EnsureChild(root, Path.Combine(root, "state"));
        return new ToolInstallLayout(
            root,
            downloads,
            Path.Combine(downloads, "sha256"),
            toolchains,
            state,
            EnsureChild(state, Path.Combine(state, "locks")),
            EnsureChild(state, Path.Combine(state, "transactions")),
            EnsureChild(toolchains, Path.Combine(toolchains, ".staging")),
            EnsureChild(toolchains, Path.Combine(toolchains, ".backup")));
    }

    public string GetToolchainDirectory(string toolchainId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolchainId);
        if (toolchainId.IndexOfAny([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar]) >= 0)
        {
            throw new ArgumentException("Toolchain ID must be a single path segment.", nameof(toolchainId));
        }

        return EnsureChild(ToolchainsDirectory, Path.Combine(ToolchainsDirectory, toolchainId));
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
        var configuredHome = Environment.GetEnvironmentVariable("EIDOS_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return configuredHome;
        }

        if (platform.IsWindows)
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "Programs", "Eidos");
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(home, ".local", "share", "eidos");
    }
}
