using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir;

/// <summary>
/// Owns compiler-generated RuntimeArray construction primitives and capacity planning.
/// </summary>
internal static class RuntimeSequenceBuildLowering
{
    public const int DefaultUnknownCapacity = 8;

    public static RuntimeSequenceCapacityEstimate EstimateComprehensionCapacity(
        IEnumerable<int?> sourceLengths,
        bool hasGuard)
    {
        var upperBound = 1L;
        var allSourceLengthsKnown = true;

        foreach (var sourceLength in sourceLengths)
        {
            if (!sourceLength.HasValue)
            {
                allSourceLengthsKnown = false;
                continue;
            }

            var length = Math.Max(sourceLength.Value, 0);
            upperBound = length == 0 || upperBound == 0
                ? 0
                : upperBound > int.MaxValue / (long)length
                    ? int.MaxValue
                    : Math.Min(int.MaxValue, upperBound * length);
        }

        int? boundedUpper = allSourceLengthsKnown ? (int)upperBound : null;
        return new RuntimeSequenceCapacityEstimate(
            boundedUpper ?? DefaultUnknownCapacity,
            boundedUpper,
            allSourceLengthsKnown && !hasGuard);
    }

    public static MirCall CreateArrayLengthCall(
        MirPlace target,
        MirPlace source,
        SourceSpan span) => new()
    {
        Target = target,
        Function = MirRuntimeFunctions.CreateFunctionRef(
            WellKnownStrings.InternalNames.ArrayLength,
            target.TypeId,
            span),
        Arguments = [source],
        BorrowedArgumentIndices = new HashSet<int> { 0 },
        Span = span
    };

    public static MirCall CreateArrayNewCall(
        MirPlace target,
        MirOperand capacity,
        MirOperand elementSize,
        SourceSpan span) => new()
    {
        Target = target,
        Function = MirRuntimeFunctions.CreateFunctionRef(
            WellKnownStrings.InternalNames.ArrayNew,
            target.TypeId,
            span),
        Arguments = [capacity, elementSize],
        Span = span
    };

    public static MirCall CreateArrayPushCall(
        MirPlace target,
        MirPlace array,
        MirOperand value,
        MirOperand elementSize,
        SourceSpan span) => new()
    {
        Target = target,
        Function = MirRuntimeFunctions.CreateFunctionRef(
            WellKnownStrings.InternalNames.ArrayPush,
            target.TypeId,
            span),
        Arguments = [array, value, elementSize],
        Span = span
    };

    public static MirConstant IntConstant(long value, SourceSpan span) => new()
    {
        Value = new MirConstantValue.IntValue(value),
        TypeId = new TypeId(BaseTypes.IntId),
        Span = span
    };
}

internal readonly record struct RuntimeSequenceCapacityEstimate(
    int InitialCapacity,
    int? UpperBound,
    bool IsExact);
