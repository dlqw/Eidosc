using System.Text.Json;
using Eidosup.Diagnostics;
using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class DoctorReporterTests
{
    [Fact]
    public void Run_MissingEidoscReturnsUnhealthyExitCodeAndStableCheckId()
    {
        var environment = new FakeDoctorEnvironment();
        var reporter = new DoctorReporter(environment);
        using var writer = new StringWriter();

        var exitCode = reporter.Run(installRootOverride: null, json: false, writer);

        Assert.Equal(EidosupExitCodes.DoctorUnhealthy, exitCode);
        Assert.Contains("[FAIL] command.eidosc", writer.ToString(), StringComparison.Ordinal);
        Assert.Contains("Doctor result: action required", writer.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_JsonSeparatesWarningsFromErrorsAndReturnsHealthyWhenEidoscExists()
    {
        var environment = new FakeDoctorEnvironment();
        var executableName = environment.DetectPlatform().ExecutableName;
        environment.Commands[executableName] = Path.Combine(Path.GetTempPath(), executableName);
        var reporter = new DoctorReporter(environment);
        using var writer = new StringWriter();

        var exitCode = reporter.Run(installRootOverride: null, json: true, writer);

        Assert.Equal(EidosupExitCodes.Success, exitCode);
        using var document = JsonDocument.Parse(writer.ToString());
        Assert.True(document.RootElement.GetProperty("healthy").GetBoolean());
        Assert.Contains(
            document.RootElement.GetProperty("checks").EnumerateArray(),
            check => check.GetProperty("id").GetString() == "command.clang" &&
                     check.GetProperty("status").GetString() == "warning");
    }

    [Fact]
    public void Evaluate_ReportsInstalledVersionsInOrdinalOrder()
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
        var reporter = new DoctorReporter(environment);

        var report = reporter.Evaluate(installRoot);

        var check = Assert.Single(report.Checks, item => item.Id == "toolchains.installed");
        Assert.Equal("0.4.0-alpha.10, 0.4.0-alpha.2", check.Detail);
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
