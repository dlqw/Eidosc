using System.Security.Cryptography;
using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Expressions;

namespace Eidosc.Types;

internal static partial class MetaComptimeIntrinsics
{
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
                reason = $"named Meta argument '{argument.Name}' is missing a value";
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

    private static bool TryGetSingleCallArgument(
        CallExpr call,
        string domain,
        out EidosAstNode argument,
        out string reason)
    {
        argument = null!;
        if (call.PositionalArgs.Count + call.NamedArgs.Count != 1)
        {
            reason = $"{domain.ToLowerInvariant()} intrinsic expects exactly one argument";
            return false;
        }

        if (call.PositionalArgs.Count == 1)
        {
            argument = call.PositionalArgs[0];
            reason = string.Empty;
            return true;
        }

        var named = call.NamedArgs[0];
        if (named.Value == null)
        {
            reason = $"named {domain} argument '{named.Name}' is missing a value";
            return false;
        }

        argument = named.Value;
        reason = string.Empty;
        return true;
    }

    private static ComptimeMetaObjectValue Obj(
        string kind,
        params (string Name, ComptimeValue Value)[] properties) =>
        new(kind, properties.Select(static property =>
            new ComptimeNamedValue(property.Name, property.Value)).ToArray());

    private static ComptimeSequenceValue List(IEnumerable<ComptimeValue> values) =>
        new(ComptimeSequenceKind.List, values.ToArray());

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool TrySingle<T>(
        IReadOnlyList<ComptimeValue> arguments,
        out T value,
        out string reason)
        where T : ComptimeValue
    {
        if (arguments.Count == 1 && arguments[0] is T typed)
        {
            value = typed;
            reason = string.Empty;
            return true;
        }

        value = null!;
        reason = "expected one value of the required Meta schema type";
        return false;
    }

    private static bool Fail(string message, out ComptimeValue value, out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = message;
        return false;
    }

    private static void Replace(
        this List<(string Name, ComptimeValue Value)> properties,
        string name,
        ComptimeValue value)
    {
        var index = properties.FindIndex(property =>
            string.Equals(property.Name, name, StringComparison.Ordinal));
        if (index >= 0)
        {
            properties[index] = (name, value);
        }
        else
        {
            properties.Add((name, value));
        }
    }
}
