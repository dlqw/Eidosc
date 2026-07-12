namespace Eidosup.Installation;

using Eidosup.Distribution;

public sealed class SetupOrchestrator
{
    private readonly ReleaseAssetLocator _assetLocator = new();
    private readonly ArchiveInstaller _archiveInstaller = new();
    private readonly ClangInstaller _clangInstaller = new();
    private readonly EnvironmentConfigurator _environmentConfigurator = new();
    private readonly Func<string, IEidosReleaseSource> _releaseSourceFactory;

    public SetupOrchestrator(Func<string, IEidosReleaseSource>? releaseSourceFactory = null)
    {
        _releaseSourceFactory = releaseSourceFactory ?? (repository => new GitHubReleaseClient(repository));
    }

    public async Task<int> RunAsync(SetupOptions options, CancellationToken cancellationToken)
    {
        var platform = PlatformContext.Detect();
        Console.WriteLine($"Detected platform: {platform.Rid}");

        EidosReleaseInfo? release = null;
        string? versionDirectory = null;

        if (!options.SkipEidosc)
        {
            using var releaseSource = _releaseSourceFactory(options.Repository);
            release = await releaseSource.ResolveReleaseAsync(options.Version, options.Channel, cancellationToken);
            Console.WriteLine($"Resolved Eidos release: {release.TagName}");

            var layout = ToolInstallLayout.Create(platform, release.NormalizedVersion, options.InstallRoot, options.DownloadRoot);
            versionDirectory = layout.VersionDirectory;

            var asset = _assetLocator.ResolveEidoscBundleAsset(release, platform);
            var archivePath = await _archiveInstaller.DownloadAsync(
                asset.DownloadUrl,
                layout.DownloadDirectory,
                asset.Name,
                options.DryRun,
                cancellationToken);
            Console.WriteLine(options.DryRun
                ? $"[dry-run] Would extract '{archivePath}' to '{layout.VersionDirectory}'."
                : $"Installing eidosc into '{layout.VersionDirectory}'.");
            _archiveInstaller.ExtractZip(archivePath, layout.VersionDirectory, overwrite: options.Force, options.DryRun);

            if (!options.DryRun)
            {
                var executablePath = Path.Combine(layout.VersionDirectory, platform.ExecutableName);
                if (!File.Exists(executablePath))
                {
                    throw new InvalidOperationException($"Installed bundle is missing '{platform.ExecutableName}'.");
                }
            }

            Console.WriteLine($"eidosc target directory: {layout.VersionDirectory}");
        }

        var clang = options.SkipClang
            ? CreateSkippedClangResult()
            : await _clangInstaller.EnsureInstalledAsync(platform, options.DryRun, cancellationToken);

        Console.WriteLine($"clang: {clang.ClangPath}");

        if (!options.SkipEnvironmentConfiguration)
        {
            var eidoscHome = versionDirectory ?? ResolveExistingEidoscHome(platform);
            if (string.IsNullOrWhiteSpace(eidoscHome))
            {
                throw new InvalidOperationException("Environment configuration requires an installed eidosc. Run setup without --skip-eidosc first, or set EIDOSC_HOME.");
            }

            var layout = ToolInstallLayout.Create(
                platform,
                release?.NormalizedVersion ?? ReleaseAssetLocator.NormalizeVersion(options.Version ?? Path.GetFileName(eidoscHome.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))),
                options.InstallRoot,
                options.DownloadRoot);
            var pathEntries = new List<string> { eidoscHome };
            if (!string.IsNullOrWhiteSpace(clang.LlvmHome))
            {
                pathEntries.Add(Path.Combine(clang.LlvmHome!, "bin"));
            }

            var plan = new EnvironmentPlan(
                layout.RootDirectory,
                eidoscHome,
                Path.Combine(eidoscHome, "runtime"),
                clang.LlvmHome,
                pathEntries);

            _environmentConfigurator.Apply(plan, options.DryRun);
            Console.WriteLine(options.DryRun
                ? "[dry-run] Environment configuration planned."
                : "Environment variables and PATH have been configured.");
        }

        Console.WriteLine("Setup completed.");
        return 0;
    }

    private static string? ResolveExistingEidoscHome(PlatformContext platform)
    {
        var configured = Environment.GetEnvironmentVariable("EIDOSC_HOME");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var commandPath = CommandProbe.TryFind(platform.ExecutableName);
        return string.IsNullOrWhiteSpace(commandPath) ? null : Path.GetDirectoryName(commandPath);
    }

    private static ClangInstallationResult CreateSkippedClangResult()
    {
        var clangPath = CommandProbe.TryFind("clang") ?? "clang";
        var llvmHome = Path.GetDirectoryName(clangPath) is { } binDir ? Directory.GetParent(binDir)?.FullName : null;
        return new ClangInstallationResult(clangPath, llvmHome);
    }
}
