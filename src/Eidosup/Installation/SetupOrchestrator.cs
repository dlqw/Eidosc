namespace Eidosup.Installation;

using Eidosup.Distribution;
using Eidosup.Proxies;
using Eidosup.Toolchains;

public sealed class SetupOrchestrator
{
    private readonly ReleaseAssetLocator _assetLocator = new();
    private readonly EnvironmentConfigurator _environmentConfigurator = new();
    private readonly Func<string, IEidosReleaseSource> _releaseSourceFactory;
    private readonly LlvmDependencyCoordinator _dependencyCoordinator;
    private readonly ToolchainStateStore _stateStore;
    private readonly Func<VerifiedToolchainInstaller> _installerFactory;
    private readonly IShimInstaller _shimInstaller;
    private readonly ToolchainResolver _toolchainResolver;

    public SetupOrchestrator(
        Func<string, IEidosReleaseSource>? releaseSourceFactory = null,
        LlvmDependencyCoordinator? dependencyCoordinator = null,
        ToolchainStateStore? stateStore = null,
        Func<VerifiedToolchainInstaller>? installerFactory = null,
        IShimInstaller? shimInstaller = null,
        ToolchainResolver? toolchainResolver = null)
    {
        _releaseSourceFactory = releaseSourceFactory ?? (repository => new GitHubReleaseClient(repository));
        _dependencyCoordinator = dependencyCoordinator ?? new LlvmDependencyCoordinator();
        _stateStore = stateStore ?? new ToolchainStateStore();
        _installerFactory = installerFactory ?? (() => new VerifiedToolchainInstaller());
        _shimInstaller = shimInstaller ?? new ShimInstaller();
        _toolchainResolver = toolchainResolver ?? new ToolchainResolver();
    }

    public async Task<int> RunAsync(SetupOptions options, CancellationToken cancellationToken)
    {
        var platform = PlatformContext.Detect();
        Console.WriteLine($"Detected platform: {platform.Rid}");
        var layout = ToolInstallLayout.Create(platform, options.InstallRoot, options.DownloadRoot);

        EidosReleaseInfo? release = null;
        string? toolchainDirectory = null;

        if (!options.SkipEidosc)
        {
            using var releaseSource = _releaseSourceFactory(options.Repository);
            release = await releaseSource.ResolveReleaseAsync(options.Version, options.Channel, cancellationToken);
            Console.WriteLine($"Resolved Eidos release: {release.TagName}");

            var bundleAsset = _assetLocator.ResolveEidoscBundleAsset(release, platform);
            var checksumAsset = _assetLocator.ResolveChecksumAsset(release);
            if (options.DryRun)
            {
                toolchainDirectory = layout.GetToolchainDirectory(
                    $"eidosc-{release.NormalizedVersion}-{platform.Rid}-[manifest-sha256]");
                Console.WriteLine($"[dry-run] Would download and verify '{checksumAsset.Name}'.");
                Console.WriteLine($"[dry-run] Would install verified asset '{bundleAsset.Name}'.");
                Console.WriteLine($"[dry-run] Content cache: {layout.CacheDirectory}");
                Console.WriteLine($"[dry-run] Immutable install target: {toolchainDirectory}");
                Console.WriteLine($"[dry-run] Would initialize and reconcile '{Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName)}'.");
            }
            else
            {
                using var installer = _installerFactory();
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
                    InstallDisposition.AlreadyInstalled => $"Verified toolchain is already installed at '{result.ToolchainDirectory}'.",
                    InstallDisposition.Replaced => $"Replaced toolchain atomically at '{result.ToolchainDirectory}'.",
                    _ => $"Installed immutable toolchain at '{result.ToolchainDirectory}'."
                });
                toolchainDirectory = result.ToolchainDirectory;
                var requestedChannel = options.Version == null
                    ? options.Channel
                    : (ReleaseChannel?)null;
                await _stateStore.RegisterInstallAsync(
                    layout,
                    result.ToolchainDirectory,
                    requestedChannel,
                    cancellationToken);
                Console.WriteLine($"Toolchain state: {Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName)}");
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

            Console.WriteLine($"Toolchain ID: {(options.DryRun ? "resolved after checksum verification" : Path.GetFileName(toolchainDirectory))}");
            Console.WriteLine($"eidosc target directory: {toolchainDirectory}");
        }

        if (!options.DryRun)
        {
            await _toolchainResolver.ResolveAsync(
                layout,
                "eidosc",
                selector: null,
                cancellationToken);
        }

        var shim = await _shimInstaller.InstallAsync(layout, options.DryRun, cancellationToken);
        Console.WriteLine(options.DryRun
            ? $"[dry-run] Would install stable commands '{shim.ManagerPath}' and '{shim.ShimPath}'."
            : shim.Changed
                ? $"Stable commands installed in '{layout.BinDirectory}' ({shim.Materialization.ToString().ToLowerInvariant()})."
                : $"Stable commands are already current in '{layout.BinDirectory}'.");

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
            var pathEntries = new List<string> { layout.BinDirectory };
            if (!string.IsNullOrWhiteSpace(llvmHome))
            {
                pathEntries.Add(Path.Combine(llvmHome!, "bin"));
            }

            var plan = new EnvironmentPlan(
                layout.RootDirectory,
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
