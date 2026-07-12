using Eidosc.Pipeline;
using System.Diagnostics;

namespace Eidosc.ProjectSystem;

public sealed class GitPackageFetcher
{
    public void Fetch(string url, string refSpec, string targetDir)
    {
        if (Directory.Exists(targetDir) && Directory.GetFiles(targetDir).Length > 0)
            return;

        var parentDir = Path.GetDirectoryName(targetDir);
        if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
            Directory.CreateDirectory(parentDir);

        if (!IsGitAvailable())
            throw new InvalidOperationException(PipelineMessages.GitNotInstalled);

        var tempDir = targetDir + ".tmp";
        try
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, true);

            RunGit(["clone", "--depth", "1", "--branch", refSpec, url, tempDir]);

            if (!Directory.Exists(tempDir))
                throw new InvalidOperationException(PipelineMessages.GitCloneFailed(url));

            Directory.Move(tempDir, targetDir);
        }
        catch
        {
            if (Directory.Exists(tempDir))
                try { Directory.Delete(tempDir, true); } catch { }

            if (Directory.Exists(targetDir))
                try { Directory.Delete(targetDir, true); } catch { }

            throw;
        }
    }

    public static string? GetCommitHash(string repoDir)
    {
        if (!Directory.Exists(repoDir)) return null;
        try
        {
            return RunGitCapture("rev-parse HEAD", repoDir)?.Trim();
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<string> ListRemoteTags(string url)
    {
        if (!IsGitAvailable())
            throw new InvalidOperationException(PipelineMessages.GitNotInstalled);

        var output = RunGitCapture(["ls-remote", "--tags", "--refs", url], workingDir: null) ?? "";
        var tags = new List<string>();
        foreach (var line in output.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;

            var tabIndex = trimmed.IndexOf('\t');
            if (tabIndex < 0)
                continue;

            const string prefix = "refs/tags/";
            var reference = trimmed[(tabIndex + 1)..];
            if (reference.StartsWith(prefix, StringComparison.Ordinal))
                tags.Add(reference[prefix.Length..]);
        }

        return tags;
    }

    public void CloneOrUpdate(string url, string targetDir)
    {
        if (!IsGitAvailable())
            throw new InvalidOperationException(PipelineMessages.GitNotInstalled);

        if (!Directory.Exists(targetDir) || Directory.GetFiles(targetDir).Length == 0)
        {
            var parentDir = Path.GetDirectoryName(targetDir);
            if (!string.IsNullOrEmpty(parentDir) && !Directory.Exists(parentDir))
                Directory.CreateDirectory(parentDir);

            var tempDir = targetDir + ".tmp";
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);

                RunGit(["clone", url, tempDir]);

                if (Directory.Exists(targetDir))
                    Directory.Delete(targetDir, true);

                Directory.Move(tempDir, targetDir);
            }
            catch
            {
                if (Directory.Exists(tempDir))
                    try { Directory.Delete(tempDir, true); } catch { }

                throw;
            }

            return;
        }

        RunGit(["pull", "--ff-only"], targetDir);
        RunGit(["fetch", "--tags", "--force"], targetDir);
    }

    private static bool IsGitAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null) return false;
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static void RunGit(IReadOnlyList<string> arguments, string? workingDir = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        if (workingDir != null)
            psi.WorkingDirectory = workingDir;

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException(PipelineMessages.FailedToStartGitProcess);

        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit(60000);

        if (process.ExitCode != 0)
            throw new InvalidOperationException(PipelineMessages.GitCommandFailed(string.Join(" ", arguments), process.ExitCode, stderr));
    }

    private static string? RunGitCapture(string arguments, string workingDir)
    {
        return RunGitCapture(arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries), workingDir);
    }

    private static string? RunGitCapture(IReadOnlyList<string> arguments, string? workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
            psi.ArgumentList.Add(argument);

        if (workingDir != null)
            psi.WorkingDirectory = workingDir;

        using var process = Process.Start(psi);
        if (process == null) return null;

        var stdout = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);

        return process.ExitCode == 0 ? stdout : null;
    }
}
