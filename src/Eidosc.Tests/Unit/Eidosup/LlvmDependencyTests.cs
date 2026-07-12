using Eidosup.Diagnostics;
using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class LlvmDependencyTests
{
    [Theory]
    [InlineData("clang version 18.1.8", 18)]
    [InlineData("Apple clang version 22.0.0 (build)", 22)]
    public async Task ProbeAsync_AcceptsSupportedVersionAndRequiredCommands(string output, int expectedMajor)
    {
        var commands = RequiredCommands();
        var probe = new LlvmDependencyProbe(
            commands.GetValueOrDefault,
            (_, _, _) => Task.FromResult(new CommandCaptureResult(0, output, string.Empty)));

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DependencyHealth.Compatible, result.Health);
        Assert.Equal(expectedMajor, result.DetectedMajorVersion);
        Assert.Empty(result.MissingCommands);
    }

    [Fact]
    public async Task ProbeAsync_ReportsMissingRequiredCommandAfterVerifyingClang()
    {
        var captureCalled = false;
        var probe = new LlvmDependencyProbe(
            command => command == "clang" ? "/llvm/bin/clang" : null,
            (_, _, _) =>
            {
                captureCalled = true;
                return Task.FromResult(new CommandCaptureResult(0, "clang version 22", string.Empty));
            });

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DependencyHealth.Missing, result.Health);
        Assert.Equal(["llvm-ar"], result.MissingCommands);
        Assert.True(captureCalled);
    }

    [Fact]
    public async Task ProbeAsync_DoesNotTreatIncompatibleClangAsMerelyMissingAnotherCommand()
    {
        var probe = new LlvmDependencyProbe(
            command => command == "clang" ? "/llvm/bin/clang" : null,
            (_, _, _) => Task.FromResult(new CommandCaptureResult(0, "clang version 23.0.0", string.Empty)));

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DependencyHealth.Incompatible, result.Health);
        Assert.Equal(["llvm-ar"], result.MissingCommands);
    }

    [Theory]
    [InlineData("clang version 17.0.6")]
    [InlineData("clang version 23.0.0")]
    [InlineData("not a clang version")]
    public async Task ProbeAsync_RejectsUnsupportedOrUnverifiableVersion(string output)
    {
        var commands = RequiredCommands();
        var probe = new LlvmDependencyProbe(
            commands.GetValueOrDefault,
            (_, _, _) => Task.FromResult(new CommandCaptureResult(0, output, string.Empty)));

        var result = await probe.ProbeAsync(CancellationToken.None);

        Assert.Equal(DependencyHealth.Incompatible, result.Health);
    }

    [Fact]
    public void Provider_CreatesExactNonInteractiveAptPlanWithSudo()
    {
        var available = new HashSet<string>(["apt-get", "sudo"], StringComparer.Ordinal);
        var provider = new SystemLlvmDependencyProvider(
            command => available.Contains(command) ? $"/usr/bin/{command}" : null,
            isElevated: static () => false);

        var plan = provider.CreateInstallPlan(new PlatformContext("linux-x64", "eidosc", false, true, false));

        Assert.Equal("sudo-apt-get", plan.ProviderId);
        Assert.Collection(
            plan.Operations,
            operation => Assert.Equal(("sudo", "-n apt-get update", true),
                (operation.FileName, operation.Arguments, operation.RequiresElevation)),
            operation => Assert.Equal(("sudo", "-n apt-get install -y clang llvm lld", true),
                (operation.FileName, operation.Arguments, operation.RequiresElevation)));
    }

    [Fact]
    public void Provider_PrefersWingetAndIncludesNonInteractiveConsentFlags()
    {
        var provider = new SystemLlvmDependencyProvider(
            command => command is "winget" or "choco" ? command : null);

        var plan = provider.CreateInstallPlan(new PlatformContext("win-x64", "eidosc.exe", true, false, false));

        var operation = Assert.Single(plan.Operations);
        Assert.Equal("winget", plan.ProviderId);
        Assert.Contains("--accept-source-agreements", operation.Arguments, StringComparison.Ordinal);
        Assert.Contains("--accept-package-agreements", operation.Arguments, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_RejectsUnelevatedLinuxWithoutSudo()
    {
        var provider = new SystemLlvmDependencyProvider(
            command => command == "dnf" ? "/usr/bin/dnf" : null,
            isElevated: static () => false);

        var exception = Assert.Throws<EidosupException>(() =>
            provider.CreateInstallPlan(new PlatformContext("linux-x64", "eidosc", false, true, false)));

        Assert.Equal(EidosupErrorCode.DependencyProviderUnavailable, exception.Code);
    }

    [Fact]
    public async Task Coordinator_DiagnoseOnlyReturnsPlanWithoutExecuting()
    {
        var runner = new RecordingRunner();
        var coordinator = new LlvmDependencyCoordinator(
            new SequenceProbe(MissingResult()),
            new StaticProvider(),
            runner);

        var result = await coordinator.ResolveAsync(
            PlatformContext.Detect(),
            DependencyInstallPolicy.DiagnoseOnly,
            dryRun: false,
            CancellationToken.None);

        Assert.True(result.PlannedOnly);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task Coordinator_InstallExecutesPlanAndRequiresCompatibleReprobe()
    {
        var runner = new RecordingRunner();
        var coordinator = new LlvmDependencyCoordinator(
            new SequenceProbe(MissingResult(), CompatibleResult()),
            new StaticProvider(),
            runner);

        var result = await coordinator.ResolveAsync(
            PlatformContext.Detect(),
            DependencyInstallPolicy.InstallMissing,
            dryRun: false,
            CancellationToken.None);

        Assert.True(result.Installed);
        Assert.Single(runner.Commands);
    }

    [Fact]
    public async Task Coordinator_DryRunEmitsExactPlanWithoutReprobeOrExecution()
    {
        var runner = new RecordingRunner();
        var coordinator = new LlvmDependencyCoordinator(
            new SequenceProbe(MissingResult()),
            new StaticProvider(),
            runner);

        var result = await coordinator.ResolveAsync(
            PlatformContext.Detect(),
            DependencyInstallPolicy.InstallMissing,
            dryRun: true,
            CancellationToken.None);

        Assert.True(result.PlannedOnly);
        var command = Assert.Single(runner.Commands);
        Assert.Equal(("provider", "install llvm", true), command);
    }

    [Fact]
    public async Task Coordinator_RejectsIncompatibleVersionWithoutCallingProvider()
    {
        var provider = new StaticProvider { ThrowIfCalled = true };
        var coordinator = new LlvmDependencyCoordinator(
            new SequenceProbe(IncompatibleResult()),
            provider,
            new RecordingRunner());

        var exception = await Assert.ThrowsAsync<EidosupException>(() => coordinator.ResolveAsync(
            PlatformContext.Detect(),
            DependencyInstallPolicy.InstallMissing,
            dryRun: false,
            CancellationToken.None));

        Assert.Equal(EidosupErrorCode.DependencyIncompatible, exception.Code);
    }

    private static Dictionary<string, string> RequiredCommands() => new(StringComparer.Ordinal)
    {
        ["clang"] = "/llvm/bin/clang",
        ["llvm-ar"] = "/llvm/bin/llvm-ar"
    };

    private static DependencyProbeResult MissingResult() => Result(DependencyHealth.Missing, null, ["clang", "llvm-ar"]);

    private static DependencyProbeResult CompatibleResult() => Result(DependencyHealth.Compatible, 22, []);

    private static DependencyProbeResult IncompatibleResult() => Result(DependencyHealth.Incompatible, 23, []);

    private static DependencyProbeResult Result(DependencyHealth health, int? major, string[] missing) => new(
        EidosDependencyRequirements.Llvm,
        health,
        major,
        major?.ToString(),
        health == DependencyHealth.Missing ? null : "/llvm/bin/clang",
        health == DependencyHealth.Missing ? null : "/llvm",
        new Dictionary<string, string>(),
        missing,
        health.ToString());

    private sealed class SequenceProbe(params DependencyProbeResult[] results) : ILlvmDependencyProbe
    {
        private int _index;

        public Task<DependencyProbeResult> ProbeAsync(CancellationToken cancellationToken) =>
            Task.FromResult(results[Math.Min(_index++, results.Length - 1)]);
    }

    private sealed class StaticProvider : IDependencyProvider
    {
        public bool ThrowIfCalled { get; init; }

        public DependencyInstallPlan CreateInstallPlan(PlatformContext platform)
        {
            if (ThrowIfCalled)
            {
                throw new InvalidOperationException("Provider should not be called.");
            }

            return new DependencyInstallPlan(
                "llvm",
                "test",
                [new DependencyInstallOperation("provider", "install llvm", false)]);
        }
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<(string FileName, string Arguments, bool DryRun)> Commands { get; } = [];

        public Task RunAsync(string fileName, string arguments, bool dryRun, CancellationToken cancellationToken)
        {
            Commands.Add((fileName, arguments, dryRun));
            return Task.CompletedTask;
        }
    }
}
