using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eidosc.Diagnostic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.BuildSystem;

public sealed record EidosBuildStep(
    string Name,
    string Tool,
    IReadOnlyList<string> Arguments,
    IReadOnlyList<string> Inputs,
    IReadOnlyList<string> Outputs,
    IReadOnlyList<string> Dependencies);

public sealed record EidosBuildArtifact(
    string Kind,
    string Name,
    string Path,
    string Producer,
    string Target);

public sealed record EidosBuildGraph(
    int SchemaVersion,
    string HostTriple,
    string TargetTriple,
    string TargetName,
    IReadOnlyList<EidosBuildStep> Steps,
    IReadOnlyList<EidosBuildArtifact> Artifacts,
    string CanonicalHash)
{
    public const int CurrentSchemaVersion = 1;

    public string ToCanonicalJson()
    {
        var payload = new
        {
            schemaVersion = SchemaVersion,
            host = HostTriple,
            target = TargetTriple,
            targetName = TargetName,
            steps = Steps
                .OrderBy(static step => step.Name, StringComparer.Ordinal)
                .Select(static step => new
                {
                    name = step.Name,
                    tool = step.Tool,
                    arguments = step.Arguments,
                    inputs = step.Inputs.OrderBy(static value => value, StringComparer.Ordinal),
                    outputs = step.Outputs.OrderBy(static value => value, StringComparer.Ordinal),
                    dependencies = step.Dependencies.OrderBy(static value => value, StringComparer.Ordinal)
                }),
            artifacts = Artifacts
                .OrderBy(static artifact => artifact.Kind, StringComparer.Ordinal)
                .ThenBy(static artifact => artifact.Name, StringComparer.Ordinal)
                .ThenBy(static artifact => artifact.Path, StringComparer.Ordinal)
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }
}

internal static class EidosBuildGraphMaterializer
{
    public static bool TryMaterialize(
        ComptimeValue value,
        BuildComptimeContext context,
        string targetName,
        out EidosBuildGraph graph,
        out IReadOnlyList<Diagnostic.Diagnostic> diagnostics)
    {
        graph = new EidosBuildGraph(
            EidosBuildGraph.CurrentSchemaVersion,
            context.HostTriple,
            context.TargetTriple,
            targetName,
            [],
            [],
            string.Empty);
        var errors = new List<Diagnostic.Diagnostic>();
        if (value is not ComptimeMetaObjectValue { SchemaKind: "build.graph" } graphValue ||
            !TryGetList(graphValue, "steps", out var stepValues) ||
            !TryGetList(graphValue, "artifacts", out var artifactValues))
        {
            errors.Add(Error(
                "BuildGraph must be produced by Build::graph(Build::Emit, List[Build::Step], List[Build::Artifact]).",
                "E5002"));
            diagnostics = errors;
            return false;
        }

        var steps = new List<EidosBuildStep>(stepValues.Count);
        foreach (var stepValue in stepValues)
        {
            if (!TryMaterializeStep(stepValue, out var step, out var reason))
            {
                errors.Add(Error(reason, "E5003"));
                continue;
            }

            steps.Add(step);
        }

        var artifacts = new List<EidosBuildArtifact>(artifactValues.Count);
        foreach (var artifactValue in artifactValues)
        {
            if (!TryMaterializeArtifact(artifactValue, out var artifact, out var reason))
            {
                errors.Add(Error(reason, "E5003"));
                continue;
            }

            artifacts.Add(artifact);
        }

        if (errors.Count > 0)
        {
            diagnostics = errors;
            return false;
        }

        if (!EidosBuildGraphValidator.TryValidate(
                context,
                targetName,
                steps,
                artifacts,
                out var validatedSteps,
                out var validatedArtifacts,
                out var validationDiagnostics))
        {
            diagnostics = validationDiagnostics;
            return false;
        }

        var graphWithoutHash = new EidosBuildGraph(
            EidosBuildGraph.CurrentSchemaVersion,
            context.HostTriple,
            context.TargetTriple,
            targetName,
            validatedSteps,
            validatedArtifacts,
            string.Empty);
        var canonicalHash = HashText(graphWithoutHash.ToCanonicalJson());
        graph = graphWithoutHash with { CanonicalHash = canonicalHash };
        diagnostics = [];
        return true;
    }

    private static bool TryMaterializeStep(
        ComptimeValue value,
        out EidosBuildStep step,
        out string reason)
    {
        step = new EidosBuildStep("", "", [], [], [], []);
        if (value is not ComptimeMetaObjectValue { SchemaKind: "build.step.command" } command ||
            !TryGetString(command, "name", out var name) ||
            !TryGetString(command, "tool", out var tool) ||
            !TryGetStringList(command, "arguments", out var arguments) ||
            !TryGetStringList(command, "inputs", out var inputs) ||
            !TryGetStringList(command, "outputs", out var outputs) ||
            !TryGetStringList(command, "dependencies", out var dependencies))
        {
            reason = "Build graph contains a malformed Build::command value.";
            return false;
        }

        step = new EidosBuildStep(name, tool, arguments, inputs, outputs, dependencies);
        reason = string.Empty;
        return true;
    }

    private static bool TryMaterializeArtifact(
        ComptimeValue value,
        out EidosBuildArtifact artifact,
        out string reason)
    {
        artifact = new EidosBuildArtifact("", "", "", "", "");
        if (value is not ComptimeMetaObjectValue artifactValue ||
            artifactValue.SchemaKind is not ("build.artifact.generated-source" or "build.artifact.file") ||
            !TryGetString(artifactValue, "path", out var path) ||
            !TryGetString(artifactValue, "producer", out var producer) ||
            !TryGetString(artifactValue, "target", out var target))
        {
            reason = "Build graph contains a malformed Build artifact value.";
            return false;
        }

        var kind = artifactValue.SchemaKind == "build.artifact.generated-source"
            ? "generated-source"
            : "file";
        var name = kind == "generated-source"
            ? path
            : TryGetString(artifactValue, "name", out var declaredName)
                ? declaredName
                : string.Empty;
        artifact = new EidosBuildArtifact(kind, name, path, producer, target);
        reason = string.Empty;
        return true;
    }

    private static bool TryGetString(
        ComptimeMetaObjectValue value,
        string property,
        out string result)
    {
        result = string.Empty;
        if (!value.TryGet(property, out var propertyValue) || propertyValue is not ComptimeStringValue text)
        {
            return false;
        }

        result = text.Value;
        return true;
    }

    private static bool TryGetList(
        ComptimeMetaObjectValue value,
        string property,
        out IReadOnlyList<ComptimeValue> result)
    {
        result = [];
        if (!value.TryGet(property, out var propertyValue) ||
            propertyValue is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } list)
        {
            return false;
        }

        result = list.Elements;
        return true;
    }

    private static bool TryGetStringList(
        ComptimeMetaObjectValue value,
        string property,
        out IReadOnlyList<string> result)
    {
        result = [];
        if (!TryGetList(value, property, out var elements) ||
            elements.Any(static element => element is not ComptimeStringValue))
        {
            return false;
        }

        result = elements.Cast<ComptimeStringValue>().Select(static element => element.Value).ToArray();
        return true;
    }

    private static Diagnostic.Diagnostic Error(string message, string code) =>
        Diagnostic.Diagnostic.Error(message, code).WithLabel(SourceSpan.Empty, message);

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}

internal static class EidosBuildGraphValidator
{
    public static bool TryValidate(
        BuildComptimeContext context,
        string targetName,
        IReadOnlyList<EidosBuildStep> steps,
        IReadOnlyList<EidosBuildArtifact> artifacts,
        out IReadOnlyList<EidosBuildStep> validatedSteps,
        out IReadOnlyList<EidosBuildArtifact> validatedArtifacts,
        out IReadOnlyList<Diagnostic.Diagnostic> diagnostics)
    {
        var errors = new List<Diagnostic.Diagnostic>();
        var stepByName = new Dictionary<string, EidosBuildStep>(StringComparer.Ordinal);
        var outputProducer = new Dictionary<string, string>(context.PathComparer);
        var normalizedSteps = new List<EidosBuildStep>(steps.Count);

        foreach (var step in steps)
        {
            if (!IsBuildName(step.Name))
            {
                errors.Add(Error($"Invalid BuildGraph step name '{step.Name}'.", "E5003"));
                continue;
            }

            if (!stepByName.TryAdd(step.Name, step))
            {
                errors.Add(Error($"Duplicate BuildGraph step '{step.Name}'.", "E5004"));
                continue;
            }

            if (!context.TryGetTool(step.Tool, out _, out var toolReason))
            {
                errors.Add(Error(toolReason, "E5005"));
            }

            if (step.Outputs.Count == 0)
            {
                errors.Add(Error(
                    $"BuildGraph step '{step.Name}' must declare at least one output so its side effects are tracked.",
                    "E5006"));
            }

            var inputs = NormalizePaths(context, step.Name, "input", step.Inputs, errors);
            var outputs = NormalizePaths(context, step.Name, "output", step.Outputs, errors);
            var dependencies = NormalizeDistinctNames(step.Name, "dependency", step.Dependencies, errors);
            foreach (var output in outputs)
            {
                if (!IsWithinAnyOutputRoot(context, output))
                {
                    errors.Add(Error(
                        $"BuildGraph step '{step.Name}' output '{output}' is outside declared [build].outputRoots.",
                        "E5007"));
                }

                if (!outputProducer.TryAdd(output, step.Name))
                {
                    errors.Add(Error(
                        $"BuildGraph output '{output}' is produced by both '{outputProducer[output]}' and '{step.Name}'.",
                        "E5008"));
                }
            }

            normalizedSteps.Add(step with
            {
                Inputs = inputs,
                Outputs = outputs,
                Dependencies = dependencies
            });
        }

        stepByName = normalizedSteps.ToDictionary(static step => step.Name, StringComparer.Ordinal);
        foreach (var step in normalizedSteps)
        {
            foreach (var dependency in step.Dependencies)
            {
                if (string.Equals(dependency, step.Name, StringComparison.Ordinal))
                {
                    errors.Add(Error($"BuildGraph step '{step.Name}' cannot depend on itself.", "E5009"));
                }
                else if (!stepByName.ContainsKey(dependency))
                {
                    errors.Add(Error(
                        $"BuildGraph step '{step.Name}' depends on unknown step '{dependency}'.",
                        "E5009"));
                }
            }
        }

        if (!TryTopologicalOrder(normalizedSteps, out _, out var cycle))
        {
            errors.Add(Error($"BuildGraph dependency cycle detected: {string.Join(" -> ", cycle)}.", "E5010"));
        }

        var declaredInputs = context.DeclaredFiles
            .Select(static file => file.RelativePath)
            .ToHashSet(context.PathComparer);
        foreach (var step in normalizedSteps)
        {
            var ancestors = CollectAncestors(step.Name, stepByName);
            foreach (var input in step.Inputs)
            {
                if (declaredInputs.Contains(input))
                {
                    continue;
                }

                if (!outputProducer.TryGetValue(input, out var producer))
                {
                    errors.Add(Error(
                        $"BuildGraph step '{step.Name}' uses undeclared input '{input}'.",
                        "E5011"));
                    continue;
                }

                if (!ancestors.Contains(producer))
                {
                    errors.Add(Error(
                        $"BuildGraph step '{step.Name}' consumes '{input}' from '{producer}' without a dependency edge.",
                        "E5012"));
                }
            }
        }

        var normalizedArtifacts = new List<EidosBuildArtifact>(artifacts.Count);
        var artifactNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var artifact in artifacts)
        {
            if (string.IsNullOrWhiteSpace(artifact.Name) || !artifactNames.Add(artifact.Name))
            {
                errors.Add(Error($"Duplicate or empty BuildGraph artifact name '{artifact.Name}'.", "E5013"));
            }

            if (!TryNormalizePath(context, artifact.Path, out var path, out var pathReason))
            {
                errors.Add(Error($"Build artifact '{artifact.Name}' {pathReason}", "E5014"));
                continue;
            }

            if (!IsWithinAnyOutputRoot(context, path))
            {
                errors.Add(Error(
                    $"Build artifact '{artifact.Name}' path '{path}' is outside declared output roots.",
                    "E5014"));
            }

            if (!stepByName.TryGetValue(artifact.Producer, out var producer) ||
                !producer.Outputs.Contains(path, context.PathComparer))
            {
                errors.Add(Error(
                    $"Build artifact '{artifact.Name}' is not a declared output of producer '{artifact.Producer}'.",
                    "E5015"));
            }

            if (!string.Equals(artifact.Target, targetName, StringComparison.Ordinal))
            {
                errors.Add(Error(
                    $"Build artifact '{artifact.Name}' targets '{artifact.Target}', but the selected target is '{targetName}'.",
                    "E5016"));
            }

            if (artifact.Kind == "generated-source" &&
                !string.Equals(Path.GetExtension(path), ".eidos", StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(Error(
                    $"Generated source artifact '{artifact.Name}' must use the .eidos extension.",
                    "E5017"));
            }

            normalizedArtifacts.Add(artifact with { Path = path });
        }

        validatedSteps = normalizedSteps;
        validatedArtifacts = normalizedArtifacts;
        diagnostics = errors;
        return errors.Count == 0;
    }

    public static bool TryTopologicalOrder(
        IReadOnlyList<EidosBuildStep> steps,
        out IReadOnlyList<EidosBuildStep> order,
        out IReadOnlyList<string> cycle)
    {
        var byName = steps.ToDictionary(static step => step.Name, StringComparer.Ordinal);
        var indegree = steps.ToDictionary(static step => step.Name, static step => step.Dependencies.Count, StringComparer.Ordinal);
        var dependents = steps.ToDictionary(static step => step.Name, static _ => new List<string>(), StringComparer.Ordinal);
        foreach (var step in steps)
        {
            foreach (var dependency in step.Dependencies)
            {
                if (dependents.TryGetValue(dependency, out var values))
                {
                    values.Add(step.Name);
                }
            }
        }

        var ready = new SortedSet<string>(
            indegree.Where(static entry => entry.Value == 0).Select(static entry => entry.Key),
            StringComparer.Ordinal);
        var result = new List<EidosBuildStep>(steps.Count);
        while (ready.Count > 0)
        {
            var name = ready.Min!;
            ready.Remove(name);
            result.Add(byName[name]);
            foreach (var dependent in dependents[name].OrderBy(static value => value, StringComparer.Ordinal))
            {
                if (--indegree[dependent] == 0)
                {
                    ready.Add(dependent);
                }
            }
        }

        if (result.Count == steps.Count)
        {
            order = result;
            cycle = [];
            return true;
        }

        cycle = FindCycle(byName);
        order = [];
        return false;
    }

    private static IReadOnlyList<string> FindCycle(IReadOnlyDictionary<string, EidosBuildStep> steps)
    {
        var state = new Dictionary<string, int>(StringComparer.Ordinal);
        var stack = new List<string>();
        foreach (var name in steps.Keys.OrderBy(static value => value, StringComparer.Ordinal))
        {
            if (Visit(name, out var found))
            {
                return found;
            }
        }

        return steps.Keys.OrderBy(static value => value, StringComparer.Ordinal).ToArray();

        bool Visit(string name, out IReadOnlyList<string> found)
        {
            state.TryGetValue(name, out var currentState);
            if (currentState == 2)
            {
                found = [];
                return false;
            }

            if (currentState == 1)
            {
                var start = stack.IndexOf(name);
                found = [.. stack.Skip(Math.Max(0, start)), name];
                return true;
            }

            state[name] = 1;
            stack.Add(name);
            foreach (var dependency in steps[name].Dependencies.Where(steps.ContainsKey))
            {
                if (Visit(dependency, out found))
                {
                    return true;
                }
            }

            stack.RemoveAt(stack.Count - 1);
            state[name] = 2;
            found = [];
            return false;
        }
    }

    private static HashSet<string> CollectAncestors(
        string stepName,
        IReadOnlyDictionary<string, EidosBuildStep> steps)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(steps[stepName].Dependencies);
        while (pending.Count > 0)
        {
            var name = pending.Pop();
            if (!result.Add(name) || !steps.TryGetValue(name, out var dependency))
            {
                continue;
            }

            foreach (var parent in dependency.Dependencies)
            {
                pending.Push(parent);
            }
        }

        return result;
    }

    private static IReadOnlyList<string> NormalizePaths(
        BuildComptimeContext context,
        string stepName,
        string kind,
        IReadOnlyList<string> paths,
        ICollection<Diagnostic.Diagnostic> errors)
    {
        var result = new List<string>(paths.Count);
        var seen = new HashSet<string>(context.PathComparer);
        foreach (var path in paths)
        {
            if (!TryNormalizePath(context, path, out var normalized, out var reason))
            {
                errors.Add(Error($"BuildGraph step '{stepName}' {kind} {reason}", "E5007"));
                continue;
            }

            if (!seen.Add(normalized))
            {
                errors.Add(Error(
                    $"BuildGraph step '{stepName}' declares duplicate {kind} '{normalized}'.",
                    "E5008"));
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }

    private static IReadOnlyList<string> NormalizeDistinctNames(
        string stepName,
        string kind,
        IReadOnlyList<string> names,
        ICollection<Diagnostic.Diagnostic> errors)
    {
        var result = new List<string>(names.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rawName in names)
        {
            var name = rawName.Trim();
            if (!IsBuildName(name))
            {
                errors.Add(Error($"BuildGraph step '{stepName}' has invalid {kind} '{rawName}'.", "E5009"));
            }
            else if (!seen.Add(name))
            {
                errors.Add(Error($"BuildGraph step '{stepName}' has duplicate {kind} '{name}'.", "E5009"));
            }
            else
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static bool TryNormalizePath(
        BuildComptimeContext context,
        string path,
        out string normalized,
        out string reason)
    {
        normalized = string.Empty;
        if (!context.TryResolveProjectPath(path, out var fullPath, out reason))
        {
            return false;
        }

        normalized = context.ToRelativePath(fullPath);
        return true;
    }

    private static bool IsWithinAnyOutputRoot(BuildComptimeContext context, string relativePath)
    {
        if (!context.TryResolveProjectPath(relativePath, out var fullPath, out _))
        {
            return false;
        }

        return context.OutputRoots.Any(root => IsWithin(root, fullPath));
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static bool IsBuildName(string name) =>
        name.Length > 0 &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') &&
        name.Skip(1).All(static character =>
            char.IsAsciiLetterOrDigit(character) || character is '_' or '-');

    private static Diagnostic.Diagnostic Error(string message, string code) =>
        Diagnostic.Diagnostic.Error(message, code).WithLabel(SourceSpan.Empty, message);
}
