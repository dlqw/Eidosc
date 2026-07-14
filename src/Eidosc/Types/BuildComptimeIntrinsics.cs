using Eidosc.Ast.Expressions;

namespace Eidosc.Types;

internal static class BuildComptimeIntrinsics
{
    public static bool TryEvaluate(
        string name,
        CallExpr call,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        var build = context.Build;
        build?.Trace?.Record(
            build.TracePhase,
            ClassifyTraceKind(name),
            $"Build::{name}",
            "begin",
            $"arguments={call.PositionalArgs.Count}",
            call.Span,
            context.CallDepth);
        var result = TryEvaluateCore(name, call, context, out value, out reason);
        build?.Trace?.Record(
            build.TracePhase,
            ClassifyTraceKind(name),
            $"Build::{name}",
            result ? "success" : "failure",
            result ? value.CanonicalText : reason,
            call.Span,
            context.CallDepth);
        return result;
    }

    private static bool TryEvaluateCore(
        string name,
        CallExpr call,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        if (context.Build == null)
        {
            reason = $"Build::{name} requires the capability-constrained Build host; pure comptime cannot access BuildFs, BuildEnv, BuildProcess, or BuildEmit";
            return false;
        }

        if (!TryEvaluateArguments(call, context, out var arguments, out reason))
        {
            return false;
        }

        return name switch
        {
            "context" => TryContext(arguments, context.Build, out value, out reason),
            "fs" => TryCapability(arguments, context.Build, "build.context", "build.fs", out value, out reason),
            "env" => TryCapability(arguments, context.Build, "build.context", "build.env", out value, out reason),
            "process" => TryCapability(arguments, context.Build, "build.context", "build.process", out value, out reason),
            "emit" => TryCapability(arguments, context.Build, "build.context", "build.emit", out value, out reason),
            "host" => TryTriple(arguments, context.Build, "host", out value, out reason),
            "target" => TryTriple(arguments, context.Build, "target", out value, out reason),
            "readText" => TryReadText(arguments, context.Build, out value, out reason),
            "environment" => TryEnvironment(arguments, context.Build, out value, out reason),
            "command" => TryCommand(arguments, context.Build, out value, out reason),
            "generatedSource" => TryBuilder(
                "build.artifact.generated-source",
                arguments,
                "build.emit",
                ["path", "producer", "target"],
                out value,
                out reason),
            "artifact" => TryBuilder(
                "build.artifact.file",
                arguments,
                "build.emit",
                ["name", "path", "producer", "target"],
                out value,
                out reason),
            "graph" => TryBuilder(
                "build.graph",
                arguments,
                "build.emit",
                ["steps", "artifacts"],
                out value,
                out reason),
            _ => Fail($"unknown Build intrinsic '{name}'", out value, out reason)
        };
    }

    private static bool TryContext(
        IReadOnlyList<ComptimeValue> arguments,
        BuildComptimeContext build,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 0)
        {
            return Fail($"Build::context expects no arguments, got {arguments.Count}", out value, out reason);
        }

        value = Capability("build.context", build.CapabilityIdentity);
        reason = string.Empty;
        return true;
    }

    private static bool TryCapability(
        IReadOnlyList<ComptimeValue> arguments,
        BuildComptimeContext build,
        string expectedKind,
        string resultKind,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryRequireCapability(arguments, 0, expectedKind, build, out reason) || arguments.Count != 1)
        {
            value = ComptimeUnitValue.Instance;
            reason = arguments.Count == 1 ? reason : $"Build capability accessor expects one {expectedKind} value";
            return false;
        }

        value = Capability(resultKind, build.CapabilityIdentity);
        reason = string.Empty;
        return true;
    }

    private static bool TryTriple(
        IReadOnlyList<ComptimeValue> arguments,
        BuildComptimeContext build,
        string property,
        out ComptimeValue value,
        out string reason)
    {
        reason = string.Empty;
        if (arguments.Count != 1 ||
            !TryRequireCapability(arguments, 0, "build.context", build, out reason))
        {
            value = ComptimeUnitValue.Instance;
            reason = arguments.Count == 1 ? reason : $"Build::{property} expects one Build::Context";
            return false;
        }

        value = new ComptimeStringValue(property == "host" ? build.HostTriple : build.TargetTriple);
        reason = string.Empty;
        return true;
    }

    private static bool TryReadText(
        IReadOnlyList<ComptimeValue> arguments,
        BuildComptimeContext build,
        out ComptimeValue value,
        out string reason)
    {
        reason = string.Empty;
        if (arguments.Count != 2 ||
            !TryRequireCapability(arguments, 0, "build.fs", build, out reason) ||
            arguments[1] is not ComptimeStringValue path)
        {
            value = ComptimeUnitValue.Instance;
            reason = arguments.Count == 2 && arguments[1] is not ComptimeStringValue
                ? "Build::readText expects (Build::Fs, String)"
                : reason;
            return false;
        }

        if (!build.TryReadText(path.Value, out var text, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = new ComptimeStringValue(text);
        return true;
    }

    private static bool TryEnvironment(
        IReadOnlyList<ComptimeValue> arguments,
        BuildComptimeContext build,
        out ComptimeValue value,
        out string reason)
    {
        reason = string.Empty;
        if (arguments.Count != 2 ||
            !TryRequireCapability(arguments, 0, "build.env", build, out reason) ||
            arguments[1] is not ComptimeStringValue name)
        {
            value = ComptimeUnitValue.Instance;
            reason = arguments.Count == 2 && arguments[1] is not ComptimeStringValue
                ? "Build::environment expects (Build::Env, String)"
                : reason;
            return false;
        }

        if (!build.TryReadEnvironment(name.Value, out var environmentValue, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = new ComptimeStringValue(environmentValue);
        return true;
    }

    private static bool TryCommand(
        IReadOnlyList<ComptimeValue> arguments,
        BuildComptimeContext build,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        if (arguments.Count != 7 ||
            !TryRequireCapability(arguments, 0, "build.process", build, out reason) ||
            arguments[1] is not ComptimeStringValue name ||
            arguments[2] is not ComptimeStringValue tool ||
            !IsStringList(arguments[3]) ||
            !IsStringList(arguments[4]) ||
            !IsStringList(arguments[5]) ||
            !IsStringList(arguments[6]))
        {
            reason = arguments.Count == 7
                ? "Build::command expects (Build::Process, String, String, List[String], List[String], List[String], List[String])"
                : $"Build::command expects 7 arguments, got {arguments.Count}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name.Value))
        {
            reason = "Build::command step name cannot be empty";
            return false;
        }

        if (!build.TryGetTool(tool.Value, out _, out reason))
        {
            return false;
        }

        value = new ComptimeMetaObjectValue(
            "build.step.command",
            [
                new("name", name),
                new("tool", tool),
                new("arguments", arguments[3]),
                new("inputs", arguments[4]),
                new("outputs", arguments[5]),
                new("dependencies", arguments[6])
            ]);
        reason = string.Empty;
        return true;
    }

    private static bool TryBuilder(
        string kind,
        IReadOnlyList<ComptimeValue> arguments,
        string capabilityKind,
        IReadOnlyList<string> propertyNames,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        if (arguments.Count != propertyNames.Count + 1 ||
            arguments.Count == 0 ||
            arguments[0] is not ComptimeMetaObjectValue capability ||
            !string.Equals(capability.SchemaKind, capabilityKind, StringComparison.Ordinal))
        {
            reason = $"Build builder '{kind}' expects {propertyNames.Count + 1} arguments beginning with {capabilityKind}";
            return false;
        }

        value = new ComptimeMetaObjectValue(
            kind,
            arguments.Skip(1)
                .Select((argument, index) => new ComptimeNamedValue(propertyNames[index], argument))
                .ToArray());
        reason = string.Empty;
        return true;
    }

    private static bool TryRequireCapability(
        IReadOnlyList<ComptimeValue> arguments,
        int index,
        string expectedKind,
        BuildComptimeContext build,
        out string reason)
    {
        if (index >= arguments.Count ||
            arguments[index] is not ComptimeMetaObjectValue capability ||
            !string.Equals(capability.SchemaKind, expectedKind, StringComparison.Ordinal) ||
            !capability.TryGet("identity", out var identity) ||
            identity is not ComptimeStringValue identityString ||
            !string.Equals(identityString.Value, build.CapabilityIdentity, StringComparison.Ordinal))
        {
            reason = $"Build operation requires the active {expectedKind} capability";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool IsStringList(ComptimeValue value) =>
        value is ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } list &&
        list.Elements.All(static element => element is ComptimeStringValue);

    private static ComptimeMetaObjectValue Capability(string kind, string identity) => new(
        kind,
        [new ComptimeNamedValue("identity", new ComptimeStringValue(identity))]);

    private static string ClassifyTraceKind(string name) => name switch
    {
        "readText" => "filesystem",
        "environment" => "environment",
        "command" => "process",
        "generatedSource" or "artifact" or "graph" => "emit",
        _ => "capability"
    };

    private static bool TryEvaluateArguments(
        CallExpr call,
        ComptimeEvaluationContext context,
        out IReadOnlyList<ComptimeValue> arguments,
        out string reason)
    {
        var values = new List<ComptimeValue>(call.PositionalArgs.Count);
        foreach (var argument in call.PositionalArgs)
        {
            if (!ComptimeEvaluator.TryEvaluateNode(argument, context, out var argumentValue, out reason))
            {
                arguments = [];
                return false;
            }

            values.Add(argumentValue);
        }

        arguments = values;
        reason = string.Empty;
        return true;
    }

    private static bool Fail(string message, out ComptimeValue value, out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = message;
        return false;
    }
}
