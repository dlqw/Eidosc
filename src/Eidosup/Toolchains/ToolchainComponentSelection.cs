using Eidosup.Diagnostics;
using Eidosup.Distribution;

namespace Eidosup.Toolchains;

public enum ToolchainProfile
{
    Minimal,
    Default,
    Complete
}

public sealed record ToolchainInstallSelection(
    ToolchainProfile Profile,
    IReadOnlyList<string> Components,
    IReadOnlyList<string> Targets)
{
    public static ToolchainInstallSelection Default { get; } = new(
        ToolchainProfile.Default,
        [],
        []);
}

public sealed record ToolchainComponentPlan(
    ToolchainProfile Profile,
    IReadOnlyList<ToolchainComponentDefinition> Components,
    IReadOnlyList<ToolchainTargetDefinition> Targets,
    IReadOnlyList<string> ExplicitComponents,
    IReadOnlyList<string> ExplicitTargets)
{
    public IReadOnlyList<string> ComponentIds => Components.Select(static component => component.Id).ToArray();
}

public static class ToolchainComponentSolver
{
    public static ToolchainComponentPlan ResolveInitial(
        ToolchainDistributionManifest manifest,
        ToolchainInstallSelection selection)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(selection);
        var selected = manifest.GetProfile(selection.Profile).Components.ToHashSet(StringComparer.Ordinal);
        var explicitComponents = new HashSet<string>(StringComparer.Ordinal);
        foreach (var requested in selection.Components)
        {
            var component = ResolveComponentRequest(manifest, requested);
            selected.Add(component);
            if (!manifest.GetProfile(selection.Profile).Components.Contains(component, StringComparer.Ordinal))
            {
                explicitComponents.Add(component);
            }
        }

        var targets = ResolveTargets(manifest, selection.Targets);
        foreach (var target in targets)
        {
            selected.Add(target.Component);
        }

        return CreatePlan(
            manifest,
            selection.Profile,
            selected,
            targets,
            explicitComponents,
            targets.Select(static target => target.Name).ToHashSet(StringComparer.Ordinal));
    }

    public static ToolchainComponentPlan Add(
        ToolchainDistributionManifest manifest,
        ToolchainProfile profile,
        IReadOnlyCollection<string> installedComponentIds,
        IReadOnlyCollection<string> installedExplicitComponents,
        IReadOnlyCollection<string> installedExplicitTargets,
        IReadOnlyList<string> requestedComponents,
        IReadOnlyList<string> requestedTargets)
    {
        var explicitComponents = installedExplicitComponents.ToHashSet(StringComparer.Ordinal);
        foreach (var requested in requestedComponents)
        {
            explicitComponents.Add(ResolveComponentRequest(manifest, requested));
        }

        var targets = ResolveTargets(manifest, requestedTargets);
        var explicitTargets = installedExplicitTargets.Concat(targets.Select(static target => target.Name))
            .ToHashSet(StringComparer.Ordinal);
        var selected = manifest.GetProfile(profile).Components
            .Concat(explicitComponents)
            .Concat(manifest.Targets.Where(target => explicitTargets.Contains(target.Name)).Select(static target => target.Component))
            .ToHashSet(StringComparer.Ordinal);
        return CreatePlan(
            manifest,
            profile,
            selected,
            manifest.Targets.Where(target => selected.Contains(target.Component)).ToArray(),
            explicitComponents,
            explicitTargets);
    }

    public static ToolchainComponentPlan Remove(
        ToolchainDistributionManifest manifest,
        ToolchainProfile profile,
        IReadOnlyCollection<string> installedComponentIds,
        IReadOnlyCollection<string> installedExplicitComponents,
        IReadOnlyCollection<string> installedExplicitTargets,
        IReadOnlyList<string> requestedComponents,
        IReadOnlyList<string> requestedTargets)
    {
        var componentRemovals = requestedComponents
            .Select(requested => ResolveComponentRequest(manifest, requested))
            .ToHashSet(StringComparer.Ordinal);
        var targetRemovals = ResolveTargets(manifest, requestedTargets)
            .Select(static target => target.Name)
            .ToHashSet(StringComparer.Ordinal);
        var removals = componentRemovals.Concat(targetRemovals).ToArray();
        if (removals.Length == 0)
        {
            throw new ArgumentException("At least one component or target must be requested for removal.");
        }

        var profileComponents = manifest.GetProfile(profile).Components.ToHashSet(StringComparer.Ordinal);
        var requiredRemoval = componentRemovals.FirstOrDefault(profileComponents.Contains);
        if (requiredRemoval != null)
        {
            throw new EidosupException(
                EidosupErrorCode.InstallConflict,
                EidosupExitCodes.InstallConflict,
                $"Profile component '{requiredRemoval}' cannot be removed while profile '{profile.ToString().ToLowerInvariant()}' is active.",
                "Select a smaller profile first, or keep every component required by the active profile.");
        }

        var explicitComponents = installedExplicitComponents.Except(componentRemovals, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var explicitTargets = installedExplicitTargets.Except(targetRemovals, StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);
        var selected = profileComponents.Concat(explicitComponents)
            .Concat(manifest.Targets.Where(target => explicitTargets.Contains(target.Name)).Select(static target => target.Component))
            .ToHashSet(StringComparer.Ordinal);
        var plan = CreatePlan(
            manifest,
            profile,
            selected,
            manifest.Targets.Where(target => selected.Contains(target.Component)).ToArray(),
            explicitComponents,
            explicitTargets);
        var stillSelected = componentRemovals.FirstOrDefault(plan.ComponentIds.Contains);
        if (stillSelected != null)
        {
            throw new EidosupException(
                EidosupErrorCode.InstallConflict,
                EidosupExitCodes.InstallConflict,
                $"Component '{stillSelected}' is still required by another selected component or target.",
                "Remove the dependent explicit component or target in the same operation.");
        }

        return plan;
    }

    public static ToolchainComponentPlan SetProfile(
        ToolchainDistributionManifest manifest,
        ToolchainProfile profile,
        IReadOnlyCollection<string> explicitComponents,
        IReadOnlyCollection<string> explicitTargets) => Add(
        manifest,
        profile,
        [],
        explicitComponents,
        explicitTargets,
        [],
        []);

    public static ToolchainProfile ParseProfile(string value)
    {
        if (Enum.TryParse<ToolchainProfile>(value, ignoreCase: true, out var profile) && Enum.IsDefined(profile))
        {
            return profile;
        }

        throw new FormatException($"Unsupported toolchain profile '{value}'. Expected minimal, default, or complete.");
    }

    private static ToolchainComponentPlan CreatePlan(
        ToolchainDistributionManifest manifest,
        ToolchainProfile profile,
        ISet<string> selected,
        IReadOnlyList<ToolchainTargetDefinition> targets,
        IReadOnlySet<string> explicitComponents,
        IReadOnlySet<string> explicitTargets)
    {
        AddDependencies(manifest, selected);
        var ordered = manifest.Components.Where(component => selected.Contains(component.Id)).ToArray();
        foreach (var component in ordered)
        {
            var conflict = component.Conflicts.FirstOrDefault(selected.Contains);
            if (conflict != null)
            {
                throw new EidosupException(
                    EidosupErrorCode.InstallConflict,
                    EidosupExitCodes.InstallConflict,
                    $"Component '{component.Id}' conflicts with '{conflict}'.",
                    "Choose a profile and explicit component set without a signed manifest conflict.");
            }
        }

        return new ToolchainComponentPlan(
            profile,
            ordered,
            targets.Concat(manifest.Targets.Where(target => selected.Contains(target.Component)))
                .DistinctBy(static target => target.Name, StringComparer.Ordinal)
                .OrderBy(static target => target.Name, StringComparer.Ordinal)
                .ToArray(),
            explicitComponents.Order(StringComparer.Ordinal).ToArray(),
            explicitTargets.Order(StringComparer.Ordinal).ToArray());
    }

    private static void AddDependencies(ToolchainDistributionManifest manifest, ISet<string> selected)
    {
        var queue = new Queue<string>(selected);
        while (queue.TryDequeue(out var id))
        {
            var component = manifest.GetComponent(id);
            foreach (var dependency in component.Dependencies)
            {
                if (selected.Add(dependency))
                {
                    queue.Enqueue(dependency);
                }
            }
        }
    }

    private static string ResolveComponentRequest(ToolchainDistributionManifest manifest, string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || !string.Equals(requested, requested.Trim(), StringComparison.Ordinal))
        {
            throw new FormatException("Component names cannot be empty or contain surrounding whitespace.");
        }

        var exact = manifest.Components.SingleOrDefault(component =>
            string.Equals(component.Id, requested, StringComparison.Ordinal));
        if (exact != null)
        {
            return exact.Id;
        }

        var matches = manifest.Components.Where(component =>
                string.Equals(component.Name, requested, StringComparison.Ordinal) && component.Target == null)
            .ToArray();
        return matches.Length switch
        {
            1 => matches[0].Id,
            0 => throw new EidosupException(
                EidosupErrorCode.ToolchainUnavailable,
                EidosupExitCodes.ToolchainUnavailable,
                $"Component '{requested}' is not available for toolchain '{manifest.Toolchain}'."),
            _ => throw new EidosupException(
                EidosupErrorCode.InvalidReleaseMetadata,
                EidosupExitCodes.InvalidRelease,
                $"Component name '{requested}' is ambiguous in toolchain '{manifest.Toolchain}'.")
        };
    }

    private static IReadOnlyList<ToolchainTargetDefinition> ResolveTargets(
        ToolchainDistributionManifest manifest,
        IReadOnlyList<string> requestedTargets)
    {
        var targets = new List<ToolchainTargetDefinition>(requestedTargets.Count);
        foreach (var requested in requestedTargets.Distinct(StringComparer.Ordinal))
        {
            var target = manifest.Targets.SingleOrDefault(candidate =>
                             string.Equals(candidate.Name, requested, StringComparison.Ordinal))
                         ?? throw new EidosupException(
                             EidosupErrorCode.ToolchainUnavailable,
                             EidosupExitCodes.ToolchainUnavailable,
                             $"Target '{requested}' is not available for toolchain '{manifest.Toolchain}'.",
                             "List targets for another toolchain version or choose a target published in its signed manifest.");
            targets.Add(target);
        }

        return targets;
    }
}
