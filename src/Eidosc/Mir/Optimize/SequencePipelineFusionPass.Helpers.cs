using Eidosc.Borrow;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

public sealed partial class SequencePipelineFusionPass
{
    private static bool TryGetSharedBorrowInnerType(MirModule module, TypeId typeId, out TypeId innerType)
    {
        innerType = TypeId.None;
        TypeDescriptor? descriptor = null;
        if (module.TypeDescriptors.TryGetValue(typeId.Value, out var direct))
        {
            descriptor = direct;
        }
        else if (module.DynamicTypeKeys.TryGetValue(typeId.Value, out var typeKey) &&
                 TypeKeyParsing.TryParseTypeDescriptor(typeKey, out var parsed))
        {
            descriptor = parsed;
        }

        if (descriptor is not TypeDescriptor.Ref { Inner: { IsValid: true } inner })
            return false;

        innerType = inner;
        return true;
    }

    private static bool TryCreateSequenceFunctionReference(
        MirFunctionRef sourceReference,
        CompilerSemanticRole targetRole,
        IReadOnlyDictionary<string, MirFunc> functionsByKey,
        out MirFunctionRef targetReference)
    {
        targetReference = null!;
        var sourceName = targetRole switch
        {
            CompilerSemanticRole.SequenceFind => "find",
            _ => ""
        };
        if (sourceName.Length == 0)
            return false;

        var expectedName = ReplaceFinalFunctionName(sourceReference.Name, "head", sourceName);
        var target = !string.Equals(expectedName, sourceReference.Name, StringComparison.Ordinal)
            ? functionsByKey.Values.FirstOrDefault(function =>
                string.Equals(function.Name, expectedName, StringComparison.Ordinal) &&
                string.Equals(function.FunctionId.Module, sourceReference.FunctionId.Module, StringComparison.Ordinal))
            : null;
        target ??= functionsByKey.Values.FirstOrDefault(function =>
            !string.Equals(function.Name, sourceReference.Name, StringComparison.Ordinal) &&
            string.Equals(function.SourceName, sourceName, StringComparison.Ordinal) &&
            string.Equals(function.FunctionId.Module, sourceReference.FunctionId.Module, StringComparison.Ordinal));
        if (target == null)
            return false;

        targetReference = sourceReference with
        {
            Name = target.Name,
            SymbolId = target.SymbolId,
            FunctionId = target.FunctionId,
            TypeId = TypeId.None,
            SignatureTypeId = TypeId.None,
            CompilerSemanticRole = targetRole
        };
        return true;
    }

    private static string ReplaceFinalFunctionName(string name, string source, string target)
    {
        if (!name.EndsWith(source, StringComparison.Ordinal))
            return name;

        var index = name.Length - source.Length;
        return index < 0 ? name : name[..index] + target + name[(index + source.Length)..];
    }

    private static MirSwitch BoolSwitch(
        MirOperand discriminant,
        BlockId trueTarget,
        BlockId falseTarget,
        SourceSpan span) => new()
    {
        Discriminant = discriminant,
        Branches =
        [
            new MirSwitchBranch
            {
                Value = new MirConstant
                {
                    Value = new MirConstantValue.BoolValue(true),
                    TypeId = new TypeId(BaseTypes.BoolId),
                    Span = span
                },
                Target = trueTarget
            }
        ],
        DefaultTarget = falseTarget,
        Span = span
    };

    private static MirConstant IntConstant(long value, SourceSpan span) => new()
    {
        Value = new MirConstantValue.IntValue(value),
        TypeId = new TypeId(BaseTypes.IntId),
        Span = span
    };

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        private NoopDisposable()
        {
        }

        public void Dispose()
        {
        }
    }
}
