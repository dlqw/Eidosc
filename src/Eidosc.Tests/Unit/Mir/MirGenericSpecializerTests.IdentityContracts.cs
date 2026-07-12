using Eidosc.Symbols;
using System.Text;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Tests.Unit.Mir;

public sealed partial class MirGenericSpecializerTests
{
    private static string BuildIdentityContract(MirModule module)
    {
        var specializationSymbols = BuildSpecializationSymbolMap(module);
        var builder = new StringBuilder();

        foreach (var function in module.Functions.OrderBy(function => FormatFunctionLabel(function, specializationSymbols)))
        {
            builder.Append("func ");
            builder.Append(FormatFunctionLabel(function, specializationSymbols));
            builder.Append(" symbol=");
            builder.Append(FormatSymbol(function.SymbolId, specializationSymbols));
            builder.Append(" fid=");
            builder.Append(FormatFunctionId(function.FunctionId, specializationSymbols));
            builder.AppendLine();

            foreach (var call in function.BasicBlocks
                         .OrderBy(block => block.Id.Value)
                         .SelectMany(block => block.Instructions.OfType<MirCall>()))
            {
                builder.Append("  call ");
                builder.Append(FormatOperand(call.Target, specializationSymbols));
                builder.Append(" -> ");
                builder.Append(FormatOperand(call.Function, specializationSymbols));
                if (call.Function is MirFunctionRef functionRef)
                {
                    builder.Append(" fid=");
                    builder.Append(FormatFunctionId(functionRef.FunctionId, specializationSymbols));
                }

                builder.Append(" args=[");
                builder.Append(string.Join(", ", call.Arguments.Select(argument => FormatOperand(argument, specializationSymbols))));
                builder.Append(']');
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static Dictionary<SymbolId, string> BuildSpecializationSymbolMap(MirModule module)
    {
        var result = new Dictionary<SymbolId, string>();
        var groups = module.Functions
            .Where(static function => function.SymbolId.IsValid &&
                                      function.Name.Contains("__spec_", StringComparison.Ordinal))
            .GroupBy(static function => function.Name[..function.Name.IndexOf("__spec_", StringComparison.Ordinal)])
            .OrderBy(static group => group.Key, StringComparer.Ordinal);

        foreach (var group in groups)
        {
            var ordinal = 1;
            foreach (var function in group.OrderBy(static function => function.Name, StringComparer.Ordinal))
            {
                result[function.SymbolId] = $"<spec:{group.Key}:{ordinal++}>";
            }
        }

        return result;
    }

    private static string FormatFunctionLabel(MirFunc function, IReadOnlyDictionary<SymbolId, string> specializationSymbols)
    {
        if (specializationSymbols.TryGetValue(function.SymbolId, out var specializationLabel))
        {
            return specializationLabel;
        }

        return string.IsNullOrWhiteSpace(function.Name) ? "<anonymous>" : function.Name;
    }

    private static string FormatOperand(
        MirOperand? operand,
        IReadOnlyDictionary<SymbolId, string> specializationSymbols)
    {
        return operand switch
        {
            MirFunctionRef functionRef => specializationSymbols.TryGetValue(functionRef.SymbolId, out var specializationLabel)
                ? specializationLabel
                : string.IsNullOrWhiteSpace(functionRef.Name)
                    ? FormatSymbol(functionRef.SymbolId, specializationSymbols)
                    : functionRef.Name,
            MirPlace { Kind: PlaceKind.Local } place => $"%{place.Local.Value}:{FormatType(place.TypeId)}",
            MirConstant constant => $"{FormatConstant(constant.Value)}:{FormatType(constant.TypeId)}",
            MirPoison poison => $"poison:{FormatType(poison.TypeId)}",
            null => "<null>",
            _ => operand.GetType().Name
        };
    }

    private static string FormatFunctionId(
        FunctionId? functionId,
        IReadOnlyDictionary<SymbolId, string> specializationSymbols)
    {
        if (functionId == null || !functionId.IsValid)
        {
            return "<none>";
        }

        if (functionId.SymbolId.IsValid)
        {
            return FormatSymbol(functionId.SymbolId, specializationSymbols);
        }

        if (!string.IsNullOrWhiteSpace(functionId.QualifiedName))
        {
            return $"qualified:{functionId.QualifiedName}";
        }

        if (!string.IsNullOrWhiteSpace(functionId.MangledName))
        {
            return $"mangled:{functionId.MangledName}";
        }

        return string.IsNullOrWhiteSpace(functionId.Name) ? "<none>" : $"name:{functionId.Name}";
    }

    private static string FormatSymbol(
        SymbolId symbolId,
        IReadOnlyDictionary<SymbolId, string> specializationSymbols)
    {
        if (specializationSymbols.TryGetValue(symbolId, out var specializationLabel))
        {
            return specializationLabel;
        }

        return symbolId.IsValid ? $"sym:{symbolId.Value}" : "<none>";
    }

    private static string FormatType(TypeId typeId) => typeId.IsValid ? $"T{typeId.Value}" : "T?";

    private static string FormatConstant(MirConstantValue value)
    {
        return value switch
        {
            MirConstantValue.IntValue intValue => intValue.Value.ToString(),
            MirConstantValue.BoolValue boolValue => boolValue.Value ? "true" : "false",
            MirConstantValue.StringValue stringValue => $"\"{stringValue.Value}\"",
            MirConstantValue.UnitValue => "()",
            _ => value.ToString() ?? "<constant>"
        };
    }
}
