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
}
