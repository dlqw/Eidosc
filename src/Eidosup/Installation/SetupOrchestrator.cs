namespace Eidosup.Installation;

using Eidosup.Distribution;
using Eidosup.Proxies;
using Eidosup.Toolchains;

public sealed class SetupOrchestrator
{
    private readonly EnvironmentConfigurator _environmentConfigurator = new();
    private readonly LlvmDependencyCoordinator _dependencyCoordinator;
    private readonly IShimInstaller _shimInstaller;
    private readonly ToolchainResolver _toolchainResolver;
    private readonly ToolchainManager _toolchainManager;

    public SetupOrchestrator(
        Func<string, IEidosReleaseSource>? releaseSourceFactory = null,
        LlvmDependencyCoordinator? dependencyCoordinator = null,
        ToolchainStateStore? stateStore = null,
        Func<VerifiedToolchainInstaller>? installerFactory = null,
        IShimInstaller? shimInstaller = null,
        ToolchainResolver? toolchainResolver = null)
    {
        _dependencyCoordinator = dependencyCoordinator ?? new LlvmDependencyCoordinator();
        _shimInstaller = shimInstaller ?? new ShimInstaller();
        _toolchainResolver = toolchainResolver ?? new ToolchainResolver();
        _toolchainManager = new ToolchainManager(
            releaseSourceFactory: releaseSourceFactory,
            stateStore: stateStore,
            installerFactory: installerFactory,
            resolver: _toolchainResolver);
    }

    public async Task<int> RunAsync(SetupOptions options, CancellationToken cancellationToken)
    {
        var platform = PlatformContext.Detect();
        Console.WriteLine($"Detected platform: {platform.Rid}");
        var layout = ToolInstallLayout.Create(platform, options.InstallRoot, options.DownloadRoot);

        EidosReleaseInfo? release = null;
        string? toolchainDirectory = null;
        string? installedSelector = null;

        if (!options.SkipEidosc)
        {
            var spec = options.Version == null
                ? ToolchainSpec.Parse(options.Channel.ToString().ToLowerInvariant())
                : ToolchainSpec.Parse(options.Version);
            installedSelector = spec.Canonical;
            var outcome = await _toolchainManager.InstallAsync(
                new ToolchainManagementOptions(
                    options.Repository,
                    options.InstallRoot,
                    options.DownloadRoot),
                spec,
                options.Force,
                options.DryRun,
                new DownloadProgressReporter(),
                cancellationToken);
            release = outcome.Release;
            Console.WriteLine($"Resolved Eidos release: {release.TagName}");

            if (options.DryRun)
            {
                toolchainDirectory = layout.GetToolchainDirectory(
                    $"eidosc-{release.NormalizedVersion}-{platform.Rid}-[manifest-sha256]");
                Console.WriteLine($"[dry-run] Would download and verify '{outcome.ChecksumAsset.Name}'.");
                Console.WriteLine($"[dry-run] Would install verified asset '{outcome.BundleAsset.Name}'.");
                Console.WriteLine($"[dry-run] Content cache: {layout.CacheDirectory}");
                Console.WriteLine($"[dry-run] Immutable install target: {toolchainDirectory}");
                Console.WriteLine($"[dry-run] Would initialize and reconcile '{Path.Combine(layout.StateDirectory, ToolchainStateStore.FileName)}'.");
            }
            else
            {
                var result = outcome.Install!;
                Console.WriteLine(result.Disposition switch
                {
                    InstallDisposition.AlreadyInstalled => $"Verified toolchain is already installed at '{result.ToolchainDirectory}'.",
                    InstallDisposition.Replaced => $"Replaced toolchain atomically at '{result.ToolchainDirectory}'.",
                    _ => $"Installed immutable toolchain at '{result.ToolchainDirectory}'."
                });
                toolchainDirectory = result.ToolchainDirectory;
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
                installedSelector,
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
