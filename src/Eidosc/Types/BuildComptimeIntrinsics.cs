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
            $"build.{name}",
            "begin",
            $"arguments={call.PositionalArgs.Count}",
            call.Span,
            context.CallDepth);
        var result = TryEvaluateCore(name, call, context, out value, out reason);
        build?.Trace?.Record(
            build.TracePhase,
            ClassifyTraceKind(name),
            $"build.{name}",
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
            reason = $"build.{name} requires the capability-constrained build host; pure comptime cannot access build capabilities";
            return false;
        }

        if (!TryEvaluateArguments(call, context, out var arguments, out reason))
        {
            return false;
        }

        return name switch
        {
            "session" => TrySession(arguments, context.Build, out value, out reason),
            "fs" => TryCapability(arguments, context.Build, "build.session", "build.fs", out value, out reason),
            "env" => TryCapability(arguments, context.Build, "build.session", "build.env", out value, out reason),
            "process" => TryCapability(arguments, context.Build, "build.session", "build.process", out value, out reason),
            "emit" => TryCapability(arguments, context.Build, "build.session", "build.emit", out value, out reason),
            "network" => TryCapability(arguments, context.Build, "build.session", "build.network", out value, out reason),
            "host" => TryTriple(arguments, context.Build, "host", out value, out reason),
            "target" => TryTriple(arguments, context.Build, "target", out value, out reason),
            "read_text" => TryReadText(arguments, context.Build, out value, out reason),
            "environment" => TryEnvironment(arguments, context.Build, out value, out reason),
            "command" => TryCommand(arguments, context.Build, out value, out reason),
            "generated_source" => TryBuilder(
                "build.artifact.generated-source",
                arguments,
                "build.emit",
                context.Build,
                ["path", "producer", "target"],
                out value,
                out reason),
            "generated_module" => TryGeneratedModule(arguments, context.Build, out value, out reason),
            "artifact" => TryBuilder(
                "build.artifact.file",
                arguments,
                "build.emit",
                context.Build,
                ["name", "path", "producer", "target"],
                out value,
                out reason),
            "content_addressed_artifact" => TryBuilder(
                "build.artifact.content-addressed",
                arguments,
                "build.emit",
                context.Build,
                ["name", "path", "producer", "target", "sha256"],
                out value,
                out reason),
            "fetch" => TryFetch(arguments, context.Build, out value, out reason),
            "graph" => TryBuilder(
                "build.graph",
                arguments,
                "build.emit",
                context.Build,
                ["steps", "artifacts"],
                out value,
                out reason),
            _ => Fail($"unknown build intrinsic '{name}'", out value, out reason)
        };
    }

    private static bool TrySession(
        IReadOnlyList<ComptimeValue> arguments,
        BuildComptimeContext build,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 0)
        {
            return Fail($"build.session expects no arguments, got {arguments.Count}", out value, out reason);
        }

        value = Capability("build.session", build.CapabilityIdentity);
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
            !TryRequireCapability(arguments, 0, "build.session", build, out reason))
        {
            value = ComptimeUnitValue.Instance;
            reason = arguments.Count == 1 ? reason : $"build.{property} expects one build.Session";
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
                ? "build.read_text expects (build.Fs, String)"
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
                ? "build.environment expects (build.Env, String)"
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
                ? "build.command expects (build.Process, String, String, List[String], List[String], List[String], List[String])"
                : $"build.command expects 7 arguments, got {arguments.Count}";
            return false;
        }

        if (string.IsNullOrWhiteSpace(name.Value))
        {
            reason = "build.command step name cannot be empty";
            return false;
        }

        if (!build.TryGetHostTool(tool.Value, out _, out reason))
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

    private static bool TryGeneratedModule(
        IReadOnlyList<ComptimeValue> arguments,
        BuildComptimeContext build,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        if (arguments.Count != 4 ||
            !TryRequireCapability(arguments, 0, "build.emit", build, out reason) ||
            arguments[1] is not ComptimeStringValue moduleName ||
            arguments[2] is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } items ||
            items.Elements.Any(static item => item is not ComptimeSyntaxValue { Category: Eidosc.Syntax.SyntaxCategory.Item }) ||
            arguments[3] is not ComptimeStringValue target)
        {
            reason = "build.generated_module expects (build.Emit, String, List[meta.Syntax[meta.Item]], String)";
            return false;
        }

        if (!IsModulePath(moduleName.Value))
        {
            reason = $"build.generated_module module path '{moduleName.Value}' must use lower_snake_case segments";
            return false;
        }

        value = new ComptimeMetaObjectValue(
            "build.artifact.generated-module",
            [
                new("module", moduleName),
                new("syntax", items),
                new("target", target)
            ]);
        reason = string.Empty;
        return true;
    }

    private static bool TryFetch(
        IReadOnlyList<ComptimeValue> arguments,
        BuildComptimeContext build,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        if (arguments.Count != 3 ||
            !TryRequireCapability(arguments, 0, "build.network", build, out reason) ||
            arguments[1] is not ComptimeStringValue url ||
            !TryReadSha256(arguments[2], out var digest))
        {
            reason = "build.fetch expects (build.Network, String, build.Sha256)";
            return false;
        }

        if (!build.TryAuthorizeNetworkFetch(url.Value, digest, out reason))
        {
            return false;
        }

        value = new ComptimeMetaObjectValue(
            "build.artifact.fetch",
            [
                new("url", url),
                new("sha256", new ComptimeStringValue(digest))
            ]);
        reason = string.Empty;
        return true;
    }

    private static bool TryBuilder(
        string kind,
        IReadOnlyList<ComptimeValue> arguments,
        string capabilityKind,
        BuildComptimeContext build,
        IReadOnlyList<string> propertyNames,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        if (arguments.Count != propertyNames.Count + 1 ||
            !TryRequireCapability(arguments, 0, capabilityKind, build, out _))
        {
            reason = $"build builder '{kind}' expects {propertyNames.Count + 1} arguments beginning with {capabilityKind}";
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
            reason = $"build operation requires the active {expectedKind} capability";
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
        "read_text" => "filesystem",
        "environment" => "environment",
        "command" => "process",
        "fetch" => "network",
        "generated_source" or "generated_module" or "artifact" or
            "content_addressed_artifact" or "graph" => "emit",
        _ => "capability"
    };

    private static bool TryReadSha256(ComptimeValue value, out string digest)
    {
        digest = string.Empty;
        if (value is not ComptimeAdtValue
            {
                ConstructorName: var constructorName,
                PositionalValues: [ComptimeStringValue digestString]
            } ||
            !constructorName.EndsWith("Sha256", StringComparison.Ordinal) ||
            !IsSha256(digestString.Value))
        {
            return false;
        }

        digest = digestString.Value;
        return true;
    }

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool IsModulePath(string value) =>
        value.Split('.', StringSplitOptions.None) is { Length: > 0 } segments &&
        segments.All(static segment =>
            segment.Length > 0 &&
            (segment[0] is >= 'a' and <= 'z' || segment[0] == '_') &&
            segment.Skip(1).All(static character =>
                character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_'));

    private static bool TryEvaluateArguments(
        CallExpr call,
        ComptimeEvaluationContext context,
        out IReadOnlyList<ComptimeValue> arguments,
        out string reason)
    {
        var values = new List<ComptimeValue>(call.PositionalArgs.Count + call.NamedArgs.Count);
        foreach (var argument in call.PositionalArgs)
        {
            if (!ComptimeEvaluator.TryEvaluateNode(argument, context, out var argumentValue, out reason))
            {
                arguments = [];
                return false;
            }

            values.Add(argumentValue);
        }

        foreach (var argument in call.NamedArgs)
        {
            if (argument.Value == null)
            {
                arguments = [];
                reason = $"named Build argument '{argument.Name}' is missing a value";
                return false;
            }
            if (!ComptimeEvaluator.TryEvaluateNode(argument.Value, context, out var argumentValue, out reason))
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
