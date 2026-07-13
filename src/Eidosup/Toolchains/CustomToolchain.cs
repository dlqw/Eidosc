using Eidosup.Diagnostics;
using Eidosup.Installation;

namespace Eidosup.Toolchains;

public static class CustomToolchain
{
    public static CustomToolchainState ValidateAndCreate(
        string name,
        string rootDirectory,
        DateTimeOffset linkedAt)
    {
        if (!IsValidName(name))
        {
            throw new FormatException(
                "A custom toolchain name must start with an ASCII letter or digit and contain only letters, digits, '.', '_' or '-'.");
        }

        var root = Path.GetFullPath(rootDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(root) || (File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
        {
            throw InvalidLayout(name, root, "the root directory is missing or is a filesystem link");
        }

        var platform = PlatformContext.Detect();
        var command = ResolveCommand(root, platform.ExecutableName);
        var runtime = Path.Combine(root, "runtime");
        if (command == null || !Directory.Exists(runtime) ||
            (File.GetAttributes(runtime) & FileAttributes.ReparsePoint) != 0 ||
            HasReparsePointBetween(root, command) ||
            HasReparsePointBetween(root, runtime))
        {
            throw InvalidLayout(name, root, "the required eidosc executable and runtime directory were not found");
        }

        return new CustomToolchainState(
            name,
            GetSelector(name),
            GetId(name),
            root,
            command,
            runtime,
            linkedAt);
    }

    public static bool IsValidName(string? name) =>
        !string.IsNullOrWhiteSpace(name) &&
        name.Length <= 64 &&
        char.IsAsciiLetterOrDigit(name[0]) &&
        name.All(static character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');

    public static string GetSelector(string name) => $"custom:{name}";

    public static string GetId(string name) => $"custom-{name}";

    public static bool IsValidId(string? id) =>
        id != null && id.StartsWith("custom-", StringComparison.Ordinal) && IsValidName(id["custom-".Length..]);

    private static string? ResolveCommand(string root, string executableName)
    {
        var direct = Path.Combine(root, executableName);
        if (IsRegularFile(direct))
        {
            return direct;
        }

        var nested = Path.Combine(root, "bin", executableName);
        return IsRegularFile(nested) ? nested : null;
    }

    private static bool IsRegularFile(string path) =>
        File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0;

    private static bool HasReparsePointBetween(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        var current = root;
        foreach (var segment in relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
        {
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static EidosupException InvalidLayout(string name, string root, string reason) => new(
        EidosupErrorCode.ToolchainUnavailable,
        EidosupExitCodes.ToolchainUnavailable,
        $"Custom toolchain '{name}' at '{root}' is invalid: {reason}.",
        "Publish or build a layout containing eidosc[.exe] (at the root or in bin/) and a regular runtime/ directory.");
}
