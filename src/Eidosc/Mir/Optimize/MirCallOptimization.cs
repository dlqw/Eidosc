using System.Globalization;

namespace Eidosc.Mir.Optimize;

internal static class MirCallOptimization
{
    public static IReadOnlyDictionary<string, int> BuildParameterCounts(MirModule module) =>
        module.Functions
            .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().Locals.Count(static local => local.IsParameter),
                StringComparer.Ordinal);

    public static bool TryGetReusableCall(
        MirCall call,
        FunctionOptimizationSummaryIndex? summaries,
        IReadOnlyDictionary<string, int> parameterCounts,
        out MirFunctionRef function,
        out MirPlace target)
    {
        function = null!;
        target = null!;
        if (call.Function is not MirFunctionRef functionRef ||
            call.Target is not { Kind: PlaceKind.Local } targetPlace ||
            summaries == null ||
            !summaries.TryGet(functionRef, out var summary) ||
            !summary.CanReuseCallResult ||
            !parameterCounts.TryGetValue(MirFunctionIdentity.GetStableKey(functionRef), out var parameterCount) ||
            call.Arguments.Count != parameterCount)
        {
            return false;
        }

        function = functionRef;
        target = targetPlace;
        return true;
    }

    public static bool TryCreateCallKey(
        MirFunctionRef function,
        IReadOnlyList<MirOperand> arguments,
        out string key,
        out HashSet<LocalId> localDependencies)
    {
        var parts = new string[arguments.Count];
        localDependencies = [];
        for (var i = 0; i < arguments.Count; i++)
        {
            if (!TryCreateOperandKey(arguments[i], out parts[i], localDependencies))
            {
                key = "";
                return false;
            }
        }

        key = $"{MirFunctionIdentity.GetStableKey(function)}({string.Join(';', parts)})";
        return true;
    }

    public static LocalId? GetDefinedLocal(MirInstruction instruction)
    {
        MirOperand? target = instruction switch
        {
            MirAssign assign => assign.Target,
            MirCaseInject injection => injection.Target,
            MirCall call => call.Target,
            MirBinOp binOp => binOp.Target,
            MirUnaryOp unaryOp => unaryOp.Target,
            MirLoad load => load.Target,
            MirStore store => store.Target,
            MirCopy copy => copy.Target,
            MirMove move => move.Target,
            MirAlloc alloc => alloc.Target,
            _ => null
        };

        return target is MirPlace { Kind: PlaceKind.Local } place
            ? place.Local
            : null;
    }

    public static bool TryCollectLocalDependencies(
        IReadOnlyList<MirOperand> operands,
        out HashSet<LocalId> dependencies)
    {
        dependencies = [];
        foreach (var operand in operands)
        {
            if (!TryCreateOperandKey(operand, out _, dependencies))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TryCreateOperandKey(
        MirOperand operand,
        out string key,
        HashSet<LocalId> dependencies)
    {
        switch (operand)
        {
            case MirPlace { Kind: PlaceKind.Local } place:
                dependencies.Add(place.Local);
                key = $"l:{place.Local.Value}:{place.TypeId.Value}";
                return true;
            case MirConstant constant:
                key = $"c:{constant.TypeId.Value}:{FormatConstant(constant.Value)}";
                return true;
            case MirFunctionRef function:
                key = $"f:{MirFunctionIdentity.GetStableKey(function)}";
                return true;
            default:
                key = "";
                return false;
        }
    }

    private static string FormatConstant(MirConstantValue value) => value switch
    {
        MirConstantValue.IntValue integer => $"i:{integer.Value}",
        MirConstantValue.FloatValue floating => $"f:{floating.Value.ToString("R", CultureInfo.InvariantCulture)}",
        MirConstantValue.StringValue text => $"s:{text.Value.Length}:{text.Value}",
        MirConstantValue.RawStringValue text => $"r:{text.Value.Length}:{text.Value}",
        MirConstantValue.CharValue character => $"h:{(int)character.Value}",
        MirConstantValue.BoolValue boolean => boolean.Value ? "b:1" : "b:0",
        MirConstantValue.UnitValue => "u",
        _ => value.ToString() ?? "<null>"
    };
}
