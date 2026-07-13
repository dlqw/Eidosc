namespace Eidosup.Installation;

public sealed class EnvironmentConfigurator
{
    public void Apply(EnvironmentPlan plan, bool dryRun)
    {
        if (OperatingSystem.IsWindows())
        {
            ApplyWindows(plan, dryRun);
            return;
        }

        ApplyUnix(plan, dryRun);
    }

    public void Remove(string eidosHome, string binDirectory, bool dryRun)
    {
        if (OperatingSystem.IsWindows())
        {
            RemoveWindows(eidosHome, binDirectory, dryRun);
            return;
        }

        if (dryRun)
        {
            return;
        }

        foreach (var profilePath in ProfileScriptWriter.GetDefaultUnixProfiles())
        {
            if (!File.Exists(profilePath))
            {
                continue;
            }

            var existing = File.ReadAllText(profilePath);
            var updated = ProfileScriptWriter.RemoveBlock(existing);
            if (!string.Equals(existing, updated, StringComparison.Ordinal))
            {
                File.WriteAllText(profilePath, updated);
            }
        }

        if (PathEquals(Environment.GetEnvironmentVariable("EIDOS_HOME"), eidosHome))
        {
            Environment.SetEnvironmentVariable("EIDOS_HOME", null, EnvironmentVariableTarget.Process);
        }

        RemoveProcessPath(binDirectory, ':');
        ClearLegacyVariables(EnvironmentVariableTarget.Process);
    }

    private static void ApplyWindows(EnvironmentPlan plan, bool dryRun)
    {
        if (dryRun)
        {
            Console.WriteLine("[dry-run] Would configure EIDOS_HOME, remove legacy version-bound variables, and update the user PATH with the stable Eidos bin directory.");
            return;
        }

        ApplyVariable("EIDOS_HOME", plan.EidosHome, EnvironmentVariableTarget.Process);
        ClearLegacyVariables(EnvironmentVariableTarget.Process);
        if (!string.IsNullOrWhiteSpace(plan.LlvmHome))
        {
            ApplyVariable("EIDOS_LLVM_HOME", plan.LlvmHome!, EnvironmentVariableTarget.Process);
        }

        var mergedProcessPath = MergePathEntries(Environment.GetEnvironmentVariable("PATH"), plan.PathEntries, ';');
        ApplyVariable("PATH", mergedProcessPath, EnvironmentVariableTarget.Process);

        ApplyVariable("EIDOS_HOME", plan.EidosHome, EnvironmentVariableTarget.User);
        ClearLegacyVariables(EnvironmentVariableTarget.User);
        if (!string.IsNullOrWhiteSpace(plan.LlvmHome))
        {
            ApplyVariable("EIDOS_LLVM_HOME", plan.LlvmHome!, EnvironmentVariableTarget.User);
        }

        var currentUserPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        var mergedUserPath = MergePathEntries(currentUserPath, plan.PathEntries, ';');
        ApplyVariable("PATH", mergedUserPath, EnvironmentVariableTarget.User);
    }

    private static void ApplyUnix(EnvironmentPlan plan, bool dryRun)
    {
        if (!dryRun)
        {
            ApplyVariable("EIDOS_HOME", plan.EidosHome, EnvironmentVariableTarget.Process);
            ClearLegacyVariables(EnvironmentVariableTarget.Process);
            if (!string.IsNullOrWhiteSpace(plan.LlvmHome))
            {
                ApplyVariable("EIDOS_LLVM_HOME", plan.LlvmHome!, EnvironmentVariableTarget.Process);
            }

            var mergedProcessPath = MergePathEntries(Environment.GetEnvironmentVariable("PATH"), plan.PathEntries, ':');
            ApplyVariable("PATH", mergedProcessPath, EnvironmentVariableTarget.Process);
        }
        var newBlock = ProfileScriptWriter.BuildUnixProfileBlock(plan);
        foreach (var profilePath in ProfileScriptWriter.GetDefaultUnixProfiles())
        {
            if (dryRun)
            {
                Console.WriteLine($"[dry-run] Would update shell profile: {profilePath}");
                continue;
            }

            var existing = File.Exists(profilePath) ? File.ReadAllText(profilePath) : string.Empty;
            var updated = ProfileScriptWriter.UpsertBlock(existing, newBlock);
            File.WriteAllText(profilePath, updated);
        }
    }

    public static string MergePathEntries(string? existingPath, IEnumerable<string> newEntries, char separator)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var entry in newEntries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (seen.Add(entry))
            {
                result.Add(entry);
            }
        }

        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            foreach (var entry in existingPath.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (seen.Add(entry))
                {
                    result.Add(entry);
                }
            }
        }

        return string.Join(separator, result);
    }

    private static void ApplyVariable(string name, string value, EnvironmentVariableTarget target)
    {
        Environment.SetEnvironmentVariable(name, value, target);
    }

    private static void ClearLegacyVariables(EnvironmentVariableTarget target)
    {
        Environment.SetEnvironmentVariable("EIDOSC_HOME", null, target);
        Environment.SetEnvironmentVariable("EIDOS_RUNTIME_PATH", null, target);
    }

    private static void RemoveWindows(string eidosHome, string binDirectory, bool dryRun)
    {
        if (dryRun)
        {
            return;
        }

        if (PathEquals(Environment.GetEnvironmentVariable("EIDOS_HOME"), eidosHome))
        {
            Environment.SetEnvironmentVariable("EIDOS_HOME", null, EnvironmentVariableTarget.Process);
        }

        RemoveProcessPath(binDirectory, ';');
        ClearLegacyVariables(EnvironmentVariableTarget.Process);
        if (PathEquals(Environment.GetEnvironmentVariable("EIDOS_HOME", EnvironmentVariableTarget.User), eidosHome))
        {
            Environment.SetEnvironmentVariable("EIDOS_HOME", null, EnvironmentVariableTarget.User);
        }

        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable(
            "PATH",
            RemovePathEntry(userPath, binDirectory, ';'),
            EnvironmentVariableTarget.User);
        ClearLegacyVariables(EnvironmentVariableTarget.User);
    }

    private static void RemoveProcessPath(string binDirectory, char separator) =>
        Environment.SetEnvironmentVariable(
            "PATH",
            RemovePathEntry(Environment.GetEnvironmentVariable("PATH"), binDirectory, separator),
            EnvironmentVariableTarget.Process);

    public static string RemovePathEntry(string? path, string entry, char separator) =>
        string.Join(
            separator,
            (path ?? string.Empty).Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(candidate => !PathEquals(candidate, entry)));

    private static bool PathEquals(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left))
        {
            return false;
        }

        try
        {
            return ToolInstallLayout.PathEquals(left, right);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }
}
