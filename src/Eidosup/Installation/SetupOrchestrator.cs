namespace Eidosup.Installation;

using Eidosup.Distribution;

public sealed class SetupOrchestrator
{
    private readonly ReleaseAssetLocator _assetLocator = new();
    private readonly EnvironmentConfigurator _environmentConfigurator = new();
    private readonly Func<string, IEidosReleaseSource> _releaseSourceFactory;
    private readonly LlvmDependencyCoordinator _dependencyCoordinator;

    public SetupOrchestrator(
        Func<string, IEidosReleaseSource>? releaseSourceFactory = null,
        LlvmDependencyCoordinator? dependencyCoordinator = null)
    {
        _releaseSourceFactory = releaseSourceFactory ?? (repository => new GitHubReleaseClient(repository));
        _dependencyCoordinator = dependencyCoordinator ?? new LlvmDependencyCoordinator();
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

            var bundleAsset = _assetLocator.ResolveEidoscBundleAsset(release, platform);
            var checksumAsset = _assetLocator.ResolveChecksumAsset(release);
            if (options.DryRun)
            {
                Console.WriteLine($"[dry-run] Would download and verify '{checksumAsset.Name}'.");
                Console.WriteLine($"[dry-run] Would install verified asset '{bundleAsset.Name}'.");
                Console.WriteLine($"[dry-run] Content cache: {layout.CacheDirectory}");
                Console.WriteLine($"[dry-run] Atomic install target: {layout.VersionDirectory}");
            }
            else
            {
                using var installer = new VerifiedToolchainInstaller();
                var result = await installer.InstallAsync(
                    new VerifiedInstallRequest(
                        release,
                        bundleAsset,
                        checksumAsset,
                        platform,
                        layout,
                        options.Repository,
                        options.Force),
                    new DownloadProgressReporter(),
                    cancellationToken);
                Console.WriteLine(result.Disposition switch
                {
                    InstallDisposition.AlreadyInstalled => $"Verified toolchain is already installed at '{result.VersionDirectory}'.",
                    InstallDisposition.Replaced => $"Replaced toolchain atomically at '{result.VersionDirectory}'.",
                    _ => $"Installed toolchain atomically at '{result.VersionDirectory}'."
                });
                Console.WriteLine($"Asset SHA-256: {result.AssetSha256}");
                if (result.Disposition != InstallDisposition.AlreadyInstalled)
                {
                    Console.WriteLine(result.CacheHit
                        ? "Artifact cache: verified hit."
                        : result.Resumed
                            ? "Artifact download: resumed and verified."
                            : "Artifact download: completed and verified.");
                }
            }

            Console.WriteLine($"eidosc target directory: {layout.VersionDirectory}");
        }

        var dependency = options.SkipClang
            ? null
            : await _dependencyCoordinator.ResolveAsync(
                platform,
                DependencyInstallPolicy.InstallMissing,
                options.DryRun,
                cancellationToken);
        var clangPath = dependency?.Probe.ClangPath ?? CommandProbe.TryFind("clang") ?? "clang";
        var llvmHome = dependency?.Probe.LlvmHome ?? ResolveLlvmHome(clangPath);
        Console.WriteLine(dependency == null
            ? $"LLVM dependency: skipped (clang candidate: {clangPath})."
            : dependency.Probe.IsCompatible
                ? $"LLVM dependency: {dependency.Probe.Detail}"
                : $"LLVM dependency: install planned via '{dependency.Plan!.ProviderId}'.");

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
            if (!string.IsNullOrWhiteSpace(llvmHome))
            {
                pathEntries.Add(Path.Combine(llvmHome!, "bin"));
            }

            var plan = new EnvironmentPlan(
                layout.RootDirectory,
                eidoscHome,
                Path.Combine(eidoscHome, "runtime"),
                llvmHome,
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

    private static string? ResolveLlvmHome(string clangPath)
    {
        var binDirectory = Path.GetDirectoryName(clangPath);
        return string.IsNullOrWhiteSpace(binDirectory)
            ? null
            : Directory.GetParent(binDirectory)?.FullName;
    }

    private sealed class DownloadProgressReporter : IProgress<DownloadProgress>
    {
        private long _lastReportedBytes;

        public void Report(DownloadProgress value)
        {
            var completed = value.TotalBytes is { } total && value.BytesReceived >= total;
            if (!completed && value.BytesReceived - _lastReportedBytes < 8L * 1024 * 1024)
            {
                return;
            }

            _lastReportedBytes = value.BytesReceived;
            var totalText = value.TotalBytes is { } length ? $"/{FormatBytes(length)}" : string.Empty;
            Console.WriteLine($"Downloading {value.AssetName}: {FormatBytes(value.BytesReceived)}{totalText}{(value.Resumed ? " (resumed)" : string.Empty)}");
        }

        private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):F1} MiB"
            : $"{bytes / 1024d:F1} KiB";
    }
}
