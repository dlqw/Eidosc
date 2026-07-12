namespace Eidosup.Installation;

public sealed record ToolInstallLayout(
    string RootDirectory,
    string DownloadDirectory,
    string VersionsDirectory,
    string VersionDirectory,
    string RuntimeDirectory)
{
    public static ToolInstallLayout Create(PlatformContext platform, string version, string? installRoot, string? downloadRoot)
    {
        var root = string.IsNullOrWhiteSpace(installRoot)
            ? GetDefaultInstallRoot(platform)
            : Path.GetFullPath(installRoot);
        var downloads = string.IsNullOrWhiteSpace(downloadRoot)
            ? Path.Combine(root, "downloads")
            : Path.GetFullPath(downloadRoot);
        var versions = Path.Combine(root, "toolchains", "eidosc");
        var versionDirectory = Path.Combine(versions, version);
        var runtimeDirectory = Path.Combine(versionDirectory, "runtime");
        return new ToolInstallLayout(root, downloads, versions, versionDirectory, runtimeDirectory);
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
