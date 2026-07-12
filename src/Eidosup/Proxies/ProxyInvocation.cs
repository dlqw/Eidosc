using Eidosup.Diagnostics;
using Eidosup.Installation;

namespace Eidosup.Proxies;

public sealed record ProxyInvocation(string CommandName, string RootDirectory)
{
    public static bool TryCreate(string? processPath, out ProxyInvocation? invocation)
    {
        invocation = null;
        if (string.IsNullOrWhiteSpace(processPath))
        {
            return false;
        }

        var fileName = Path.GetFileName(processPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var expectedName = OperatingSystem.IsWindows() ? "eidosc.exe" : "eidosc";
        if (!string.Equals(fileName, expectedName, comparison))
        {
            return false;
        }

        var fullPath = Path.GetFullPath(processPath);
        var binDirectory = Path.GetDirectoryName(fullPath);
        var rootDirectory = binDirectory == null ? null : Directory.GetParent(binDirectory)?.FullName;
        if (binDirectory == null ||
            rootDirectory == null ||
            !string.Equals(Path.GetFileName(binDirectory), "bin", comparison) ||
            !ToolInstallLayout.IsWithin(rootDirectory, fullPath))
        {
            throw new EidosupException(
                EidosupErrorCode.ProxyFailure,
                EidosupExitCodes.ProxyFailure,
                $"The eidosc shim path '{processPath}' is outside the managed '<EIDOS_HOME>/bin' layout.",
                "Run eidosup setup to reinstall the stable shim in the managed bin directory.");
        }

        invocation = new ProxyInvocation("eidosc", Path.GetFullPath(rootDirectory));
        return true;
    }
}
