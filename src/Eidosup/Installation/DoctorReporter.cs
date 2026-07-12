using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Diagnostics;

namespace Eidosup.Installation;

public enum DoctorCheckStatus
{
    Pass,
    Warning,
    Fail
}

public enum DoctorSeverity
{
    Info,
    Warning,
    Error
}

public sealed record DoctorCheck(
    string Id,
    DoctorCheckStatus Status,
    DoctorSeverity Severity,
    string Summary,
    string? Detail = null,
    string? Remediation = null);

public sealed record DoctorReport(string Platform, IReadOnlyList<DoctorCheck> Checks)
{
    public bool Healthy => Checks.All(static check => check.Status != DoctorCheckStatus.Fail || check.Severity != DoctorSeverity.Error);
}

public interface IDoctorEnvironment
{
    PlatformContext DetectPlatform();

    string? FindCommand(string command);

    string? GetEnvironmentVariable(string name);

    string GetFullPath(string path);

    bool DirectoryExists(string path);

    IEnumerable<string> EnumerateDirectories(string path);
}

public sealed class SystemDoctorEnvironment : IDoctorEnvironment
{
    public PlatformContext DetectPlatform() => PlatformContext.Detect();

    public string? FindCommand(string command) => CommandProbe.TryFind(command);

    public string? GetEnvironmentVariable(string name) => Environment.GetEnvironmentVariable(name);

    public string GetFullPath(string path) => Path.GetFullPath(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public IEnumerable<string> EnumerateDirectories(string path) => Directory.EnumerateDirectories(path);
}

public sealed class DoctorReporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly IDoctorEnvironment _environment;
    private readonly ILlvmDependencyProbe _dependencyProbe;

    public DoctorReporter(
        IDoctorEnvironment? environment = null,
        ILlvmDependencyProbe? dependencyProbe = null)
    {
        _environment = environment ?? new SystemDoctorEnvironment();
        _dependencyProbe = dependencyProbe ?? new LlvmDependencyProbe();
    }

    public async Task<int> RunAsync(
        string? installRootOverride,
        bool json,
        TextWriter? writer = null,
        CancellationToken cancellationToken = default)
    {
        writer ??= Console.Out;
        var report = await EvaluateAsync(installRootOverride, cancellationToken);
        if (json)
        {
            writer.WriteLine(JsonSerializer.Serialize(report, JsonOptions));
        }
        else
        {
            WriteHumanReadable(report, writer);
        }

        return report.Healthy ? EidosupExitCodes.Success : EidosupExitCodes.DoctorUnhealthy;
    }

    public async Task<DoctorReport> EvaluateAsync(
        string? installRootOverride,
        CancellationToken cancellationToken = default)
    {
        var platform = _environment.DetectPlatform();
        var checks = new List<DoctorCheck>
        {
            new(
                "platform.supported",
                DoctorCheckStatus.Pass,
                DoctorSeverity.Info,
                $"Host platform '{platform.Rid}' is supported.")
        };

        AddCommandCheck(
            checks,
            "command.eidosc",
            platform.ExecutableName,
            DoctorSeverity.Error,
            "Run 'eidosup setup' to install a verified Eidosc toolchain.");
        AddCommandCheck(
            checks,
            "command.clang",
            platform.IsWindows ? "clang.exe" : "clang",
            DoctorSeverity.Warning,
            "Install a supported LLVM/Clang toolchain or run the dependency setup explicitly.");
        AddCommandCheck(
            checks,
            "command.llc",
            platform.IsWindows ? "llc.exe" : "llc",
            DoctorSeverity.Warning,
            "Install the LLVM command-line tools and ensure their bin directory is on PATH.");
        AddLlvmDependencyCheck(checks, await _dependencyProbe.ProbeAsync(cancellationToken));

        foreach (var variable in new[] { "EIDOS_HOME", "EIDOSC_HOME", "EIDOS_RUNTIME_PATH", "EIDOS_LLVM_HOME" })
        {
            var value = _environment.GetEnvironmentVariable(variable);
            checks.Add(string.IsNullOrWhiteSpace(value)
                ? new DoctorCheck(
                    $"environment.{variable.ToLowerInvariant()}",
                    DoctorCheckStatus.Warning,
                    DoctorSeverity.Info,
                    $"{variable} is not set.",
                    Remediation: "Run 'eidosup setup' without --skip-env after installing the toolchain.")
                : new DoctorCheck(
                    $"environment.{variable.ToLowerInvariant()}",
                    DoctorCheckStatus.Pass,
                    DoctorSeverity.Info,
                    $"{variable} is set.",
                    value));
        }

        AddInstallRootChecks(checks, installRootOverride);
        return new DoctorReport(platform.Rid, checks);
    }

    private static void AddLlvmDependencyCheck(
        ICollection<DoctorCheck> checks,
        DependencyProbeResult probe)
    {
        checks.Add(probe.Health switch
        {
            DependencyHealth.Compatible => new DoctorCheck(
                "dependency.llvm",
                DoctorCheckStatus.Pass,
                DoctorSeverity.Info,
                probe.Detail,
                probe.ClangPath),
            DependencyHealth.Missing => new DoctorCheck(
                "dependency.llvm",
                DoctorCheckStatus.Warning,
                DoctorSeverity.Warning,
                probe.Detail,
                Remediation: "Run 'eidosup setup' to install the supported LLVM dependency explicitly."),
            _ => new DoctorCheck(
                "dependency.llvm",
                DoctorCheckStatus.Fail,
                DoctorSeverity.Error,
                probe.Detail,
                probe.ClangPath,
                $"Install LLVM/Clang major version {probe.Requirement.MinimumMajorVersion}-{probe.Requirement.MaximumMajorVersion} and retry.")
        });
    }

    private void AddCommandCheck(
        ICollection<DoctorCheck> checks,
        string id,
        string command,
        DoctorSeverity missingSeverity,
        string remediation)
    {
        var path = _environment.FindCommand(command);
        checks.Add(path == null
            ? new DoctorCheck(
                id,
                missingSeverity == DoctorSeverity.Error ? DoctorCheckStatus.Fail : DoctorCheckStatus.Warning,
                missingSeverity,
                $"Command '{command}' was not found on PATH.",
                Remediation: remediation)
            : new DoctorCheck(id, DoctorCheckStatus.Pass, DoctorSeverity.Info, $"Command '{command}' is available.", path));
    }

    private void AddInstallRootChecks(ICollection<DoctorCheck> checks, string? installRootOverride)
    {
        var configuredRoot = string.IsNullOrWhiteSpace(installRootOverride)
            ? _environment.GetEnvironmentVariable("EIDOS_HOME")
            : installRootOverride;
        if (string.IsNullOrWhiteSpace(configuredRoot))
        {
            checks.Add(new DoctorCheck(
                "install.root",
                DoctorCheckStatus.Warning,
                DoctorSeverity.Info,
                "No install root is configured.",
                Remediation: "Run 'eidosup setup' or pass --install-root to inspect a specific location."));
            return;
        }

        var installRoot = _environment.GetFullPath(configuredRoot);
        if (!_environment.DirectoryExists(installRoot))
        {
            checks.Add(new DoctorCheck(
                "install.root",
                DoctorCheckStatus.Warning,
                DoctorSeverity.Warning,
                "The configured install root does not exist.",
                installRoot,
                "Run 'eidosup setup' or correct EIDOS_HOME."));
            return;
        }

        checks.Add(new DoctorCheck(
            "install.root",
            DoctorCheckStatus.Pass,
            DoctorSeverity.Info,
            "The install root exists.",
            installRoot));
        var toolchainsDirectory = Path.Combine(installRoot, "toolchains", "eidosc");
        var versions = _environment.DirectoryExists(toolchainsDirectory)
            ? _environment.EnumerateDirectories(toolchainsDirectory)
                .Select(Path.GetFileName)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Order(StringComparer.Ordinal)
                .ToArray()
            : [];
        checks.Add(versions.Length == 0
            ? new DoctorCheck(
                "toolchains.installed",
                DoctorCheckStatus.Warning,
                DoctorSeverity.Warning,
                "No installed Eidosc version directories were found.",
                toolchainsDirectory,
                "Run 'eidosup setup' to install a verified release.")
            : new DoctorCheck(
                "toolchains.installed",
                DoctorCheckStatus.Pass,
                DoctorSeverity.Info,
                $"Found {versions.Length} installed Eidosc version director{(versions.Length == 1 ? "y" : "ies")}.",
                string.Join(", ", versions)));
    }

    private static void WriteHumanReadable(DoctorReport report, TextWriter writer)
    {
        writer.WriteLine($"Platform: {report.Platform}");
        foreach (var check in report.Checks)
        {
            writer.WriteLine($"[{check.Status.ToString().ToUpperInvariant()}] {check.Id}: {check.Summary}");
            if (!string.IsNullOrWhiteSpace(check.Detail))
            {
                writer.WriteLine($"  detail: {check.Detail}");
            }

            if (!string.IsNullOrWhiteSpace(check.Remediation))
            {
                writer.WriteLine($"  fix: {check.Remediation}");
            }
        }

        writer.WriteLine(report.Healthy ? "Doctor result: healthy" : "Doctor result: action required");
    }
}
