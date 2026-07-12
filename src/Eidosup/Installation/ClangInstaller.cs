namespace Eidosup.Installation;

public sealed class ClangInstaller
{
    private readonly ProcessRunner _runner = new();

    public async Task<ClangInstallationResult> EnsureInstalledAsync(PlatformContext platform, bool dryRun, CancellationToken cancellationToken)
    {
        var existingClang = FindClang();
        if (existingClang != null)
        {
            return CreateResult(existingClang);
        }

        if (platform.IsWindows)
        {
            await InstallOnWindowsAsync(dryRun, cancellationToken);
        }
        else if (platform.IsLinux)
        {
            await InstallOnLinuxAsync(dryRun, cancellationToken);
        }
        else if (platform.IsMacOs)
        {
            await InstallOnMacOsAsync(dryRun, cancellationToken);
        }
        else
        {
            throw new PlatformNotSupportedException("Unsupported OS for clang installation.");
        }

        if (dryRun)
        {
            return new ClangInstallationResult("clang", null);
        }

        var installedClang = FindClang()
            ?? throw new InvalidOperationException("clang installation completed but clang was still not found on PATH.");
        return CreateResult(installedClang);
    }

    private async Task InstallOnWindowsAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (CommandProbe.TryFind("winget") != null)
        {
            await _runner.RunAsync(
                "winget",
                "install -e --id LLVM.LLVM --accept-source-agreements --accept-package-agreements",
                dryRun,
                cancellationToken);
            return;
        }

        if (CommandProbe.TryFind("choco") != null)
        {
            await _runner.RunAsync("choco", "install llvm -y --no-progress", dryRun, cancellationToken);
            return;
        }

        if (CommandProbe.TryFind("scoop") != null)
        {
            await _runner.RunAsync("scoop", "install llvm", dryRun, cancellationToken);
            return;
        }

        throw new InvalidOperationException("No supported Windows package manager was found. Install winget, Chocolatey, or Scoop first.");
    }

    private async Task InstallOnLinuxAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (CommandProbe.TryFind("apt-get") != null)
        {
            await RunWithOptionalSudoAsync("apt-get", "update", dryRun, cancellationToken);
            await RunWithOptionalSudoAsync("apt-get", "install -y clang llvm lld", dryRun, cancellationToken);
            return;
        }

        if (CommandProbe.TryFind("dnf") != null)
        {
            await RunWithOptionalSudoAsync("dnf", "install -y clang llvm lld", dryRun, cancellationToken);
            return;
        }

        if (CommandProbe.TryFind("yum") != null)
        {
            await RunWithOptionalSudoAsync("yum", "install -y clang llvm lld", dryRun, cancellationToken);
            return;
        }

        if (CommandProbe.TryFind("pacman") != null)
        {
            await RunWithOptionalSudoAsync("pacman", "-Sy --noconfirm clang llvm lld", dryRun, cancellationToken);
            return;
        }

        if (CommandProbe.TryFind("zypper") != null)
        {
            await RunWithOptionalSudoAsync("zypper", "--non-interactive install clang llvm lld", dryRun, cancellationToken);
            return;
        }

        throw new InvalidOperationException("No supported Linux package manager was found for clang installation.");
    }

    private async Task InstallOnMacOsAsync(bool dryRun, CancellationToken cancellationToken)
    {
        if (CommandProbe.TryFind("brew") == null)
        {
            throw new InvalidOperationException("Homebrew is required on macOS to install clang/LLVM.");
        }

        await _runner.RunAsync("brew", "install llvm", dryRun, cancellationToken);
    }

    private async Task RunWithOptionalSudoAsync(string command, string arguments, bool dryRun, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows() && Environment.UserName != "root" && CommandProbe.TryFind("sudo") != null)
        {
            await _runner.RunAsync("sudo", $"{command} {arguments}", dryRun, cancellationToken);
            return;
        }

        await _runner.RunAsync(command, arguments, dryRun, cancellationToken);
    }

    private static string? FindClang()
    {
        return CommandProbe.TryFind("clang")
               ?? (OperatingSystem.IsWindows() ? TryFindKnownWindowsClangPath() : null)
               ?? (OperatingSystem.IsMacOS() ? TryFindKnownMacOsClangPath() : null);
    }

    private static string? TryFindKnownWindowsClangPath()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", "clang.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LLVM", "bin", "clang.exe")
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string? TryFindKnownMacOsClangPath()
    {
        foreach (var candidate in new[]
                 {
                     "/opt/homebrew/opt/llvm/bin/clang",
                     "/usr/local/opt/llvm/bin/clang"
                 })
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static ClangInstallationResult CreateResult(string clangPath)
    {
        var binDirectory = Path.GetDirectoryName(clangPath) ?? throw new InvalidOperationException("clang path has no parent directory.");
        var homeDirectory = Directory.GetParent(binDirectory)?.FullName;
        return new ClangInstallationResult(clangPath, homeDirectory);
    }
}

public sealed record ClangInstallationResult(string ClangPath, string? LlvmHome);
