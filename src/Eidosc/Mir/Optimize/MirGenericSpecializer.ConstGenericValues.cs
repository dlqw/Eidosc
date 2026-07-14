using System.Globalization;
using System.Text;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private static void RewriteConstGenericValues(
        IReadOnlyList<MirBasicBlock> blocks,
        IReadOnlyList<GenericValueArgumentDescriptor>? valueArguments)
    {
        if (valueArguments is not { Count: > 0 })
        {
            return;
        }

        var bindings = valueArguments
            .GroupBy(static argument => argument.ParameterIndex)
            .ToDictionary(static group => group.Key, static group => group.Last());
        foreach (var block in blocks)
        {
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                block.Instructions[instructionIndex] = RewriteConstGenericInstruction(
                    block.Instructions[instructionIndex],
                    bindings);
            }

            if (block.Terminator != null)
            {
                block.Terminator = RewriteConstGenericTerminator(block.Terminator, bindings);
            }
        }
    }

    private static MirInstruction RewriteConstGenericInstruction(
        MirInstruction instruction,
        IReadOnlyDictionary<int, GenericValueArgumentDescriptor> bindings)
    {
        return instruction switch
        {
            MirAssign assign => assign with
            {
                Target = RewriteConstGenericPlace(assign.Target, bindings),
                Source = RewriteConstGenericOperand(assign.Source, bindings)
            },
            MirCall call => call with
            {
                Target = call.Target == null ? null : RewriteConstGenericPlace(call.Target, bindings),
                Function = RewriteConstGenericOperand(call.Function, bindings),
                Arguments = call.Arguments
                    .Select(argument => RewriteConstGenericOperand(argument, bindings))
                    .ToList()
            },
            MirBinOp binOp => binOp with
            {
                Target = RewriteConstGenericOperand(binOp.Target, bindings),
                Left = RewriteConstGenericOperand(binOp.Left, bindings),
                Right = RewriteConstGenericOperand(binOp.Right, bindings)
            },
            MirUnaryOp unaryOp => unaryOp with
            {
                Target = RewriteConstGenericOperand(unaryOp.Target, bindings),
                Operand = RewriteConstGenericOperand(unaryOp.Operand, bindings)
            },
            MirLoad load => load with
            {
                Target = RewriteConstGenericPlace(load.Target, bindings),
                Source = RewriteConstGenericOperand(load.Source, bindings)
            },
            MirStore store => store with
            {
                Target = RewriteConstGenericPlace(store.Target, bindings),
                Value = RewriteConstGenericOperand(store.Value, bindings)
            },
            MirDrop drop => drop with { Value = RewriteConstGenericOperand(drop.Value, bindings) },
            MirCopy copy => copy with
            {
                Target = RewriteConstGenericPlace(copy.Target, bindings),
                Source = RewriteConstGenericPlace(copy.Source, bindings)
            },
            MirMove move => move with
            {
                Target = RewriteConstGenericPlace(move.Target, bindings),
                Source = RewriteConstGenericPlace(move.Source, bindings)
            },
            MirAlloc alloc => alloc with { Target = RewriteConstGenericPlace(alloc.Target, bindings) },
            _ => instruction
        };
    }

    private static MirTerminator RewriteConstGenericTerminator(
        MirTerminator terminator,
        IReadOnlyDictionary<int, GenericValueArgumentDescriptor> bindings)
    {
        return terminator switch
        {
            MirReturn ret => ret with
            {
                Value = ret.Value == null ? null : RewriteConstGenericOperand(ret.Value, bindings)
            },
            MirSwitch @switch => @switch with
            {
                Discriminant = RewriteConstGenericOperand(@switch.Discriminant, bindings)
            },
            _ => terminator
        };
    }

    private static MirOperand RewriteConstGenericOperand(
        MirOperand operand,
        IReadOnlyDictionary<int, GenericValueArgumentDescriptor> bindings)
    {
        return operand switch
        {
            MirConstGenericValue value when
                bindings.TryGetValue(value.ParameterIndex, out var argument) &&
                TryCreateConstGenericConstant(value, argument, out var constant) => constant,
            MirFunctionRef functionRef => functionRef with
            {
                ValueArguments = RewriteNestedValueArguments(functionRef.ValueArguments, bindings)
            },
            MirPlace place => RewriteConstGenericPlace(place, bindings),
            _ => operand
        };
    }

    private static IReadOnlyList<GenericValueArgumentDescriptor> RewriteNestedValueArguments(
        IReadOnlyList<GenericValueArgumentDescriptor> arguments,
        IReadOnlyDictionary<int, GenericValueArgumentDescriptor> bindings)
    {
        if (arguments.Count == 0)
        {
            return arguments;
        }

        var changed = false;
        var rewritten = new GenericValueArgumentDescriptor[arguments.Count];
        for (var index = 0; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            if (argument.ReferencedParameterIndex >= 0 &&
                bindings.TryGetValue(argument.ReferencedParameterIndex, out var boundArgument))
            {
                rewritten[index] = boundArgument with { ParameterIndex = argument.ParameterIndex };
                changed = true;
            }
            else
            {
                rewritten[index] = argument;
            }
        }

        return changed ? rewritten : arguments;
    }

    private static MirPlace RewriteConstGenericPlace(
        MirPlace place,
        IReadOnlyDictionary<int, GenericValueArgumentDescriptor> bindings)
    {
        return place with
        {
            Base = place.Base == null ? null : RewriteConstGenericPlace(place.Base, bindings),
            Index = place.Index == null ? null : RewriteConstGenericOperand(place.Index, bindings)
        };
    }

    private static bool TryCreateConstGenericConstant(
        MirConstGenericValue placeholder,
        GenericValueArgumentDescriptor argument,
        out MirConstant constant)
    {
        constant = default!;
        if (!argument.IsConcrete || !TryParseConstGenericConstant(argument.CanonicalText, out var value))
        {
            return false;
        }

        constant = new MirConstant
        {
            Span = placeholder.Span,
            TypeId = placeholder.TypeId.IsValid ? placeholder.TypeId : argument.TypeId,
            Value = value
        };
        return true;
    }

    private static bool TryParseConstGenericConstant(
        string canonicalText,
        out MirConstantValue value)
    {
        value = null!;
        var scalarText = StripTypedCanonicalPrefix(canonicalText);
        if (string.Equals(scalarText, "unit", StringComparison.Ordinal))
        {
            value = new MirConstantValue.UnitValue();
            return true;
        }

        if (string.Equals(scalarText, "bool:0", StringComparison.Ordinal) ||
            string.Equals(scalarText, "bool:1", StringComparison.Ordinal))
        {
            value = new MirConstantValue.BoolValue(scalarText[^1] == '1');
            return true;
        }

        if (scalarText.StartsWith("int:", StringComparison.Ordinal) &&
            long.TryParse(scalarText.AsSpan("int:".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var integer))
        {
            value = new MirConstantValue.IntValue(integer);
            return true;
        }

        if (scalarText.StartsWith("float:", StringComparison.Ordinal) &&
            double.TryParse(scalarText.AsSpan("float:".Length), NumberStyles.Float, CultureInfo.InvariantCulture, out var floating))
        {
            value = new MirConstantValue.FloatValue(floating);
            return true;
        }

        if (scalarText.StartsWith("char:", StringComparison.Ordinal) &&
            int.TryParse(scalarText.AsSpan("char:".Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var character) &&
            character is >= char.MinValue and <= char.MaxValue)
        {
            value = new MirConstantValue.CharValue((char)character);
            return true;
        }

        if (scalarText.StartsWith("string:", StringComparison.Ordinal) &&
            TryDecodeCanonicalText(scalarText["string:".Length..], out var text))
        {
            value = new MirConstantValue.StringValue(text);
            return true;
        }

        return false;
    }

    private static string StripTypedCanonicalPrefix(string canonicalText)
    {
        if (!canonicalText.StartsWith("typed:", StringComparison.Ordinal))
        {
            return canonicalText;
        }

        var typeSeparator = canonicalText.IndexOf(':', "typed:".Length);
        return typeSeparator >= 0 && typeSeparator + 1 < canonicalText.Length
            ? canonicalText[(typeSeparator + 1)..]
            : canonicalText;
    }

    private static bool TryDecodeCanonicalText(string encodedText, out string text)
    {
        try
        {
            text = Encoding.UTF8.GetString(Convert.FromHexString(encodedText));
            return true;
        }
        catch (FormatException)
        {
            text = "";
            return false;
        }
    }

    private static bool ContainsUnresolvedConstGenericValues(MirFunc function)
    {
        return function.BasicBlocks.Any(block =>
            block.Instructions.Any(InstructionContainsUnresolvedConstGenericValue) ||
            TerminatorContainsUnresolvedConstGenericValue(block.Terminator));
    }

    private static bool InstructionContainsUnresolvedConstGenericValue(MirInstruction instruction)
    {
        return instruction switch
        {
            MirAssign assign => OperandContainsUnresolvedConstGenericValue(assign.Target) ||
                                OperandContainsUnresolvedConstGenericValue(assign.Source),
            MirCall call => OperandContainsUnresolvedConstGenericValue(call.Target) ||
                            OperandContainsUnresolvedConstGenericValue(call.Function) ||
                            call.Arguments.Any(OperandContainsUnresolvedConstGenericValue),
            MirBinOp binOp => OperandContainsUnresolvedConstGenericValue(binOp.Target) ||
                              OperandContainsUnresolvedConstGenericValue(binOp.Left) ||
                              OperandContainsUnresolvedConstGenericValue(binOp.Right),
            MirUnaryOp unaryOp => OperandContainsUnresolvedConstGenericValue(unaryOp.Target) ||
                                  OperandContainsUnresolvedConstGenericValue(unaryOp.Operand),
            MirLoad load => OperandContainsUnresolvedConstGenericValue(load.Target) ||
                            OperandContainsUnresolvedConstGenericValue(load.Source),
            MirStore store => OperandContainsUnresolvedConstGenericValue(store.Target) ||
                              OperandContainsUnresolvedConstGenericValue(store.Value),
            MirDrop drop => OperandContainsUnresolvedConstGenericValue(drop.Value),
            MirCopy copy => OperandContainsUnresolvedConstGenericValue(copy.Target) ||
                            OperandContainsUnresolvedConstGenericValue(copy.Source),
            MirMove move => OperandContainsUnresolvedConstGenericValue(move.Target) ||
                            OperandContainsUnresolvedConstGenericValue(move.Source),
            MirAlloc alloc => OperandContainsUnresolvedConstGenericValue(alloc.Target),
            _ => false
        };
    }

    private static bool TerminatorContainsUnresolvedConstGenericValue(MirTerminator? terminator)
    {
        return terminator switch
        {
            MirReturn ret => OperandContainsUnresolvedConstGenericValue(ret.Value),
            MirSwitch @switch => OperandContainsUnresolvedConstGenericValue(@switch.Discriminant),
            _ => false
        };
    }

    private static bool OperandContainsUnresolvedConstGenericValue(MirOperand? operand)
    {
        return operand switch
        {
            null => false,
            MirConstGenericValue => true,
            MirFunctionRef functionRef => functionRef.ValueArguments.Any(static argument => !argument.IsConcrete),
            MirPlace place => OperandContainsUnresolvedConstGenericValue(place.Base) ||
                              OperandContainsUnresolvedConstGenericValue(place.Index),
            _ => false
        };
    }
}
