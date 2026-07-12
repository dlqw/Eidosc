using System.Text.Json;
using Eidosup.Diagnostics;
using Eidosup.Installation;

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
        environment.Commands[executableName] = Path.Combine(Path.GetTempPath(), executableName);
        var installRoot = Path.Combine(Path.GetTempPath(), "eidos-doctor-test");
        var toolchainsDirectory = Path.Combine(installRoot, "toolchains", "eidosc");
        environment.ExistingDirectories.Add(installRoot);
        environment.ExistingDirectories.Add(toolchainsDirectory);
        environment.Directories[toolchainsDirectory] =
        [
            Path.Combine(toolchainsDirectory, "0.4.0-alpha.10"),
            Path.Combine(toolchainsDirectory, "0.4.0-alpha.2")
        ];
        var reporter = CreateReporter(environment, DependencyHealth.Compatible);

        var report = await reporter.EvaluateAsync(installRoot);

        var check = Assert.Single(report.Checks, item => item.Id == "toolchains.installed");
        Assert.Equal("0.4.0-alpha.10, 0.4.0-alpha.2", check.Detail);
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

    private static DoctorReporter CreateReporter(FakeDoctorEnvironment environment, DependencyHealth health) =>
        new(environment, new StaticDependencyProbe(CreateProbeResult(health)));

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

    private sealed class FakeDoctorEnvironment : IDoctorEnvironment
    {
        public Dictionary<string, string> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);

        public HashSet<string> ExistingDirectories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, string[]> Directories { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public PlatformContext DetectPlatform() => PlatformContext.Detect();

        public string? FindCommand(string command) => Commands.GetValueOrDefault(command);

        public string? GetEnvironmentVariable(string name) => Variables.GetValueOrDefault(name);

        public string GetFullPath(string path) => path;

        public bool DirectoryExists(string path) => ExistingDirectories.Contains(path);

        public IEnumerable<string> EnumerateDirectories(string path) => Directories.GetValueOrDefault(path) ?? [];
    }
}
