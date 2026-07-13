using System.Text.Json;
using Eidosup.Diagnostics;
using Eidosup.Installation;
using Eidosup.Proxies;
using Eidosup.Toolchains;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class DoctorReporterTests
{
    [Fact]
    public async Task RunAsync_MissingEidoscReturnsUnhealthyExitCodeAndStableCheckId()
    {
        var environment = new FakeDoctorEnvironment();
        var reporter = CreateReporter(environment, DependencyHealth.Missing);
        using var writer = new StringWriter();

        var exitCode = await reporter.RunAsync(installRootOverride: null, json: false, writer);

        Assert.Equal(EidosupExitCodes.DoctorUnhealthy, exitCode);
        Assert.Contains("[FAIL] command.eidosc", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("Doctor result: action required", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_JsonSeparatesWarningsFromErrorsAndReturnsHealthyWhenEidoscExists()
    {
        var environment = new FakeDoctorEnvironment();
        var executableName = environment.DetectPlatform().ExecutableName;
        environment.Commands[executableName] = Path.Combine(Path.GetTempPath(), executableName);
        var reporter = CreateReporter(environment, DependencyHealth.Missing);
        using var writer = new StringWriter();

        var exitCode = await reporter.RunAsync(installRootOverride: null, json: true, writer);

        Assert.Equal(EidosupExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.Equal(1, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(document.RootElement.GetProperty("healthy").GetBoolean());
        Assert.Contains(
            document.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("id").GetString() == "command.clang" &&
                     check.GetProperty("status").GetString() == "warning");
    }

    [Fact]
    public async Task EvaluateAsync_ReportsInstalledVersionsInOrdinalOrder()
    {
        var environment = new FakeDoctorEnvironment();
        var executableName = environment.DetectPlatform().ExecutableName;
        var installRoot = Path.Combine(Path.GetTempPath(), "eidos-doctor-test");
        var toolchainsDirectory = Path.Combine(installRoot, "toolchains");
        var stableBin = Path.Combine(installRoot, "bin");
        var managerName = OperatingSystem.IsWindows() ? "eidosup.exe" : "eidosup";
        var shimPath = Path.Combine(stableBin, executableName);
        environment.Commands[executableName] = shimPath;
        var platform = environment.DetectPlatform();
        var alpha10 = ToolchainIdentity.Create(
            "0.4.0-alpha.10", platform.Rid, "test/source", "eidosc-v0.4.0-alpha.10",
            $"eidos-toolchain-v0.4.0-alpha.10-{platform.Rid}.json", new string('a', 64), ["eidosc-core"]).Id;
        var alpha2 = ToolchainIdentity.Create(
            "0.4.0-alpha.2", platform.Rid, "test/source", "eidosc-v0.4.0-alpha.2",
            $"eidos-toolchain-v0.4.0-alpha.2-{platform.Rid}.json", new string('b', 64), ["eidosc-core"]).Id;
        environment.ExistingDirectories.Add(installRoot);
        environment.ExistingDirectories.Add(toolchainsDirectory);
        environment.ExistingFiles.Add(shimPath);
        environment.ExistingFiles.Add(Path.Combine(stableBin, managerName));
        environment.ExistingFiles.Add(Path.Combine(stableBin, ShimInstaller.ManifestFileName));
        environment.Directories[toolchainsDirectory] =
        [
            Path.Combine(toolchainsDirectory, alpha10),
            Path.Combine(toolchainsDirectory, alpha2),
            Path.Combine(toolchainsDirectory, ".staging")
        ];
        var reporter = CreateReporter(
            environment,
            DependencyHealth.Compatible,
            CreateState(alpha10, alpha2));

        var report = await reporter.EvaluateAsync(installRoot);

        var check = Assert.Single(report.Checks, item => item.Id == "toolchains.installed");
        Assert.Equal(string.Join(", ", new[] { alpha10, alpha2 }.Order(StringComparer.Ordinal)), check.Detail);
        Assert.Equal(DoctorCheckStatus.Pass, Assert.Single(report.Checks, item => item.Id == "shims.installed").Status);
        Assert.Equal(DoctorCheckStatus.Pass, Assert.Single(report.Checks, item => item.Id == "shims.path").Status);
        Assert.Equal(DoctorCheckStatus.Pass, Assert.Single(report.Checks, item => item.Id == "toolchains.default").Status);
    }

    [Fact]
    public async Task EvaluateAsync_ReportsLegacyLayoutAsUnmanaged()
    {
        var environment = new FakeDoctorEnvironment();
        var executableName = environment.DetectPlatform().ExecutableName;
        environment.Commands[executableName] = Path.Combine(Path.GetTempPath(), executableName);
        var installRoot = Path.Combine(Path.GetTempPath(), "eidos-doctor-legacy-test");
        var toolchainsDirectory = Path.Combine(installRoot, "toolchains");
        environment.ExistingDirectories.Add(installRoot);
        environment.ExistingDirectories.Add(toolchainsDirectory);
        environment.ExistingDirectories.Add(Path.Combine(toolchainsDirectory, "eidosc"));
        var reporter = CreateReporter(environment, DependencyHealth.Compatible);

        var report = await reporter.EvaluateAsync(installRoot);

        var check = Assert.Single(report.Checks, item => item.Id == "toolchains.legacy-layout");
        Assert.Equal(DoctorCheckStatus.Warning, check.Status);
        Assert.Contains("not be activated", check.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EvaluateAsync_ReportsCorruptStateAsErrorWithoutRepairingIt()
    {
        var environment = new FakeDoctorEnvironment();
        var executableName = environment.DetectPlatform().ExecutableName;
        environment.Commands[executableName] = Path.Combine(Path.GetTempPath(), executableName);
        var installRoot = Path.Combine(Path.GetTempPath(), "eidos-doctor-corrupt-state");
        environment.ExistingDirectories.Add(installRoot);
        var reporter = new DoctorReporter(
            environment,
            new StaticDependencyProbe(CreateProbeResult(DependencyHealth.Compatible)),
            new ThrowingStateReader());

        var report = await reporter.EvaluateAsync(installRoot);

        var check = Assert.Single(report.Checks, item => item.Id == "toolchains.state");
        Assert.Equal(DoctorCheckStatus.Fail, check.Status);
        Assert.Equal(DoctorSeverity.Error, check.Severity);
        Assert.False(report.Healthy);
    }

    [Fact]
    public async Task EvaluateAsync_TreatsExplicitlyClearedDefaultAsInformationalWarning()
    {
        var environment = new FakeDoctorEnvironment();
        var platform = environment.DetectPlatform();
        var executableName = platform.ExecutableName;
        var installRoot = Path.Combine(Path.GetTempPath(), "eidos-doctor-default-none");
        var stableBin = Path.Combine(installRoot, "bin");
        var toolchainsDirectory = Path.Combine(installRoot, "toolchains");
        var shimPath = Path.Combine(stableBin, executableName);
        var managerName = platform.IsWindows ? "eidosup.exe" : "eidosup";
        var toolchainId = ToolchainIdentity.Create(
            "0.4.0-alpha.2",
            platform.Rid,
            "test/source",
            "eidosc-v0.4.0-alpha.2",
            $"eidos-toolchain-v0.4.0-alpha.2-{platform.Rid}.json",
            new string('a', 64),
            ["eidosc-core"]).Id;
        environment.Commands[executableName] = shimPath;
        environment.ExistingDirectories.Add(installRoot);
        environment.ExistingDirectories.Add(toolchainsDirectory);
        environment.ExistingFiles.Add(shimPath);
        environment.ExistingFiles.Add(Path.Combine(stableBin, managerName));
        environment.ExistingFiles.Add(Path.Combine(stableBin, ShimInstaller.ManifestFileName));
        var state = CreateState(toolchainId) with
        {
            Default = null,
            DefaultConfigured = true
        };
        var reporter = CreateReporter(environment, DependencyHealth.Compatible, state);

        var report = await reporter.EvaluateAsync(installRoot);

        var check = Assert.Single(report.Checks, item => item.Id == "toolchains.default");
        Assert.Equal(DoctorCheckStatus.Warning, check.Status);
        Assert.Equal(DoctorSeverity.Info, check.Severity);
        Assert.Contains("explicitly cleared", check.Summary, StringComparison.Ordinal);
        Assert.True(report.Healthy);
    }

    [Fact]
    public async Task EvaluateAsync_ReportsMissingLinkedCustomToolchainAsError()
    {
        var environment = new FakeDoctorEnvironment();
        var platform = environment.DetectPlatform();
        var installRoot = Path.Combine(Path.GetTempPath(), "eidos-doctor-custom-missing");
        var stableBin = Path.Combine(installRoot, "bin");
        var toolchainsDirectory = Path.Combine(installRoot, "toolchains");
        var shimPath = Path.Combine(stableBin, platform.ExecutableName);
        var managerName = platform.IsWindows ? "eidosup.exe" : "eidosup";
        var customRoot = Path.Combine(installRoot, "external-custom");
        var custom = new CustomToolchainState(
            "local",
            "custom:local",
            "custom-local",
            customRoot,
            Path.Combine(customRoot, platform.ExecutableName),
            Path.Combine(customRoot, "runtime"),
            DateTimeOffset.Parse("2026-07-12T00:00:00Z"));
        environment.Commands[platform.ExecutableName] = shimPath;
        environment.ExistingDirectories.Add(installRoot);
        environment.ExistingDirectories.Add(toolchainsDirectory);
        environment.ExistingFiles.Add(shimPath);
        environment.ExistingFiles.Add(Path.Combine(stableBin, managerName));
        environment.ExistingFiles.Add(Path.Combine(stableBin, ShimInstaller.ManifestFileName));
        var state = ToolchainState.Empty(custom.LinkedAt) with { CustomToolchains = [custom] };
        var reporter = CreateReporter(environment, DependencyHealth.Compatible, state);

        var report = await reporter.EvaluateAsync(installRoot);

        var check = Assert.Single(report.Checks, item => item.Id == "toolchains.custom");
        Assert.Equal(DoctorCheckStatus.Fail, check.Status);
        Assert.Equal(DoctorSeverity.Error, check.Severity);
        Assert.Contains("custom:local", check.Detail, StringComparison.Ordinal);
        Assert.False(report.Healthy);
    }

    [Fact]
    public async Task RunAsync_IncompatibleLlvmIsAnErrorLevelFailure()
    {
        var environment = new FakeDoctorEnvironment();
        var executableName = environment.DetectPlatform().ExecutableName;
        environment.Commands[executableName] = Path.Combine(Path.GetTempPath(), executableName);
        var reporter = CreateReporter(environment, DependencyHealth.Incompatible);
        using var writer = new StringWriter();

        var exitCode = await reporter.RunAsync(null, json: true, writer);

        Assert.Equal(EidosupExitCodes.DoctorUnhealthy, exitCode);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.Contains(
            document.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("id").GetString() == "dependency.llvm" &&
                     check.GetProperty("status").GetString() == "fail" &&
                     check.GetProperty("severity").GetString() == "error");
    }

    private static DoctorReporter CreateReporter(
        FakeDoctorEnvironment environment,
        DependencyHealth health,
        ToolchainState? state = null) =>
        new(
            environment,
            new StaticDependencyProbe(CreateProbeResult(health)),
            new StaticStateReader(state ?? ToolchainState.Empty(DateTimeOffset.Parse("2026-07-12T00:00:00Z"))));

    private static ToolchainState CreateState(params string[] toolchainIds)
    {
        var installedAt = DateTimeOffset.Parse("2026-07-12T00:00:00Z");
        var toolchains = toolchainIds.Select(id => new InstalledToolchainState(
            id,
            id.Contains("alpha.10", StringComparison.Ordinal) ? "0.4.0-alpha.10" : "0.4.0-alpha.2",
            PlatformContext.Detect().Rid,
            new string('a', 64),
            new string('b', 64),
            new string('c', 64),
            "eidos-toolchain.json",
            new string('d', 64),
            "test-release",
            "test/source",
            "minimal",
            [],
            [],
            [new InstalledComponent("eidosc-core", "eidosc-core", "0.4.0-alpha.2", true, null, [PlatformContext.Detect().ExecutableName])],
            [],
            [new InstalledArtifact("bundle.zip", new string('e', 64), 100)],
            installedAt)).ToArray();
        if (toolchains.Length == 0)
        {
            return ToolchainState.Empty(installedAt);
        }

        var selected = toolchains[0];
        var selector = new ToolchainSelectorState(
            "preview",
            ToolchainSelectorKind.Channel,
            selected.Id,
            installedAt);
        return ToolchainState.Empty(installedAt) with
        {
            Toolchains = toolchains,
            Selectors = [selector],
            Default = new ToolchainDefaultState(selector.Selector, selector.ToolchainId, installedAt),
            DefaultConfigured = true,
            ActivationHistory =
            [
                new ToolchainActivationState(
                    selector.Selector,
                    selector.ToolchainId,
                    ToolchainActivationReason.DefaultChanged,
                    installedAt)
            ]
        };
    }

    private static DependencyProbeResult CreateProbeResult(DependencyHealth health) => new(
        EidosDependencyRequirements.Llvm,
        health,
        health == DependencyHealth.Missing ? null : health == DependencyHealth.Compatible ? 22 : 23,
        health == DependencyHealth.Missing ? null : health == DependencyHealth.Compatible ? "22.0.0" : "23.0.0",
        health == DependencyHealth.Missing ? null : "/llvm/bin/clang",
        health == DependencyHealth.Missing ? null : "/llvm",
        new Dictionary<string, string>(),
        health == DependencyHealth.Missing ? ["clang", "llvm-ar"] : [],
        health switch
        {
            DependencyHealth.Compatible => "LLVM/Clang 22.0.0 is supported.",
            DependencyHealth.Missing => "Required LLVM commands are missing.",
            _ => "LLVM/Clang 23.0.0 is unsupported."
        });

    private sealed class StaticDependencyProbe(DependencyProbeResult result) : ILlvmDependencyProbe
    {
        public Task<DependencyProbeResult> ProbeAsync(CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class StaticStateReader(ToolchainState state) : IToolchainStateReader
    {
        public Task<ToolchainState> ReadAsync(
            PlatformContext platform,
            string installRoot,
            CancellationToken cancellationToken) => Task.FromResult(state);
    }

    private sealed class ThrowingStateReader : IToolchainStateReader
    {
        public Task<ToolchainState> ReadAsync(
            PlatformContext platform,
            string installRoot,
            CancellationToken cancellationToken) => throw new EidosupException(
                EidosupErrorCode.StateCorrupt,
                EidosupExitCodes.StateCorrupt,
                "Toolchain state is corrupt.",
                "Run setup to reconcile state.");
    }

    private sealed class FakeDoctorEnvironment : IDoctorEnvironment
    {
        public Dictionary<string, string> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ExistingDirectories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ExistingFiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string[]> Directories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public PlatformContext DetectPlatform() => PlatformContext.Detect();

        public string? FindCommand(string command) => Commands.GetValueOrDefault(command);

        public string? GetEnvironmentVariable(string name) => Variables.GetValueOrDefault(name);

        public string GetFullPath(string path) => path;

        public bool DirectoryExists(string path) => ExistingDirectories.Contains(path);

        public bool FileExists(string path) => ExistingFiles.Contains(path);

        public IEnumerable<string> EnumerateDirectories(string path) => Directories.GetValueOrDefault(path) ?? [];
    }
}
