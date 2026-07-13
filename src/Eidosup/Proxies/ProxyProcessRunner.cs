using System.ComponentModel;
using System.Diagnostics;
using Eidosup.Diagnostics;

namespace Eidosup.Proxies;

public interface IProxyProcessRunner
{
    Task<int> RunAsync(
        ResolvedToolchain toolchain,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken);
}

public sealed class ProxyProcessRunner : IProxyProcessRunner
{
    public async Task<int> RunAsync(
        ResolvedToolchain toolchain,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(toolchain);
        ArgumentNullException.ThrowIfNull(arguments);

        var startInfo = CreateStartInfo(toolchain, arguments);

        try
        {
            using var process = new Process { StartInfo = startInfo };
            process.Start();
            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                throw;
            }

            return process.ExitCode;
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new EidosupException(
                EidosupErrorCode.ProxyFailure,
                EidosupExitCodes.ProxyFailure,
                $"The active eidosc command '{toolchain.CommandPath}' could not be started.",
                "Run eidosup doctor and reinstall the active toolchain if the command is missing or inaccessible.",
                exception);
        }
    }

    internal static ProcessStartInfo CreateStartInfo(
        ResolvedToolchain toolchain,
        IReadOnlyList<string> arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = toolchain.CommandPath,
            UseShellExecute = false,
            RedirectStandardInput = false,
            RedirectStandardOutput = false,
            RedirectStandardError = false,
            CreateNoWindow = false
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["EIDOS_HOME"] = toolchain.RootDirectory;
        startInfo.Environment["EIDOSC_HOME"] = toolchain.ToolchainDirectory;
        if (Directory.Exists(toolchain.RuntimePath))
        {
            startInfo.Environment["EIDOS_RUNTIME_PATH"] = toolchain.RuntimePath;
        }
        else
        {
            startInfo.Environment.Remove("EIDOS_RUNTIME_PATH");
        }
        startInfo.Environment["EIDOS_STDLIB_PATH"] = toolchain.StdlibPath;
        startInfo.Environment["EIDOS_TARGETS_PATH"] = Path.Combine(toolchain.ToolchainDirectory, "targets");
        return startInfo;
    }
}
