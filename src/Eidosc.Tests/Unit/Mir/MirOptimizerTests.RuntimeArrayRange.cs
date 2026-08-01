using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirOptimizerTests
{
    [Fact]
    public void RuntimeArrayRangeSpecialization_ReadOnlyTailAndShift_EliminatesSlice()
    {
        var fixture = CreateRangeFixture();

        new RuntimeArrayRangeSpecializationPass().Run(fixture.Module);

        Assert.DoesNotContain(
            fixture.CallerBlock.Instructions.OfType<MirCall>(),
            call => HasRuntimeIdentity(call, WellKnownStrings.InternalNames.ArraySlice));
        Assert.DoesNotContain(
            fixture.CallerBlock.Instructions.OfType<MirCall>(),
            call => HasRuntimeIdentity(call, WellKnownStrings.InternalNames.ArrayShiftPrepend));
        var update = Assert.Single(
            fixture.CallerBlock.Instructions.OfType<MirCall>(),
            call => HasRuntimeIdentity(call, WellKnownStrings.InternalNames.ArrayTailShiftPrepend));
        Assert.Equal(fixture.Source.Id, Assert.IsType<MirPlace>(update.Arguments[0]).Local);
        Assert.Equal(fixture.First.Id, Assert.IsType<MirPlace>(update.Arguments[1]).Local);
        Assert.Equal(fixture.Grow.Id, Assert.IsType<MirPlace>(update.Arguments[2]).Local);
        Assert.Equal(4, update.Arguments.Count);

        var rangeCall = Assert.Single(
            fixture.CallerBlock.Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    functionRef.Name.EndsWith("__range_0", StringComparison.Ordinal));
        Assert.Equal(fixture.Source.Id, Assert.IsType<MirPlace>(rangeCall.Arguments[0]).Local);
        Assert.Equal(4, rangeCall.Arguments.Count);
        Assert.Equal(new MirConstantValue.IntValue(1), Assert.IsType<MirConstant>(rangeCall.Arguments[2]).Value);
        Assert.Equal(new MirConstantValue.IntValue(0), Assert.IsType<MirConstant>(rangeCall.Arguments[3]).Value);

        var variant = Assert.Single(
            fixture.Module.Functions,
            function => function.Name.EndsWith("__range_0", StringComparison.Ordinal));
        Assert.Equal(4, variant.Locals.Count(static local => local.IsParameter));
        Assert.Contains(
            variant.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(),
            call => HasRuntimeIdentity(call, WellKnownStrings.InternalNames.ArrayRangeLength));
        var rangeGet = Assert.Single(
            variant.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(),
            call => HasRuntimeIdentity(call, WellKnownStrings.InternalNames.ArrayRangeGet));
        Assert.Equal(new LocalId { Value = 1 }, Assert.IsType<MirPlace>(rangeGet.Arguments[0]).Local);
        Assert.DoesNotContain(
            variant.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirLoad>(),
            load => load.Source is MirPlace
            {
                Kind: PlaceKind.Index,
                IndexAccessKind: MirIndexAccessKind.RuntimeArray
            });
        Assert.DoesNotContain(
            variant.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirLoad>(),
            load => load.Target is MirPlace { Local.Value: 3 } &&
                    load.Source is MirPlace { Kind: PlaceKind.Deref });
    }

    [Fact]
    public void RuntimeArrayRangeSpecialization_EscapingTail_KeepsMaterializedSlice()
    {
        var fixture = CreateRangeFixture(escapeRange: true);

        new RuntimeArrayRangeSpecializationPass().Run(fixture.Module);

        Assert.Contains(
            fixture.CallerBlock.Instructions.OfType<MirCall>(),
            call => HasRuntimeIdentity(call, WellKnownStrings.InternalNames.ArraySlice));
        Assert.Contains(
            fixture.CallerBlock.Instructions.OfType<MirCall>(),
            call => HasRuntimeIdentity(call, WellKnownStrings.InternalNames.ArrayShiftPrepend));
        Assert.DoesNotContain(
            fixture.Module.Functions,
            function => function.Name.EndsWith("__range_0", StringComparison.Ordinal));
    }

    private static RangeFixture CreateRangeFixture(bool escapeRange = false)
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var elementType = new TypeId(BaseTypes.StringId);
        var sequenceType = new TypeId(7300);
        var referenceType = new TypeId(7301);
        var consumer = CreateRangeConsumer(
            sequenceType,
            referenceType,
            elementType,
            intType,
            escapeRange);

        var source = RangeLocal(1, "source", sequenceType, parameter: true);
        var index = RangeLocal(2, "index", intType, parameter: true);
        var first = RangeLocal(3, "first", elementType, parameter: true);
        var second = RangeLocal(4, "second", elementType, parameter: true);
        var grow = RangeLocal(5, "grow", boolType, parameter: true);
        var slice = RangeLocal(6, "slice", sequenceType);
        var observed = RangeLocal(7, "observed", intType);
        var result = RangeLocal(8, "result", sequenceType);
        var one = RangeConstant(1, intType);
        var zero = RangeConstant(0, intType);
        var size = RangeConstant(8, intType);
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = RangePlace(slice),
                    Function = MirRuntimeFunctions.CreateFunctionRef(
                        WellKnownStrings.InternalNames.ArraySlice,
                        sequenceType,
                        SourceSpan.Empty),
                    Arguments = [RangePlace(source), one, zero]
                },
                new MirCall
                {
                    Target = RangePlace(observed),
                    Function = new MirFunctionRef { Name = consumer.Name, TypeId = intType },
                    Arguments = [RangePlace(slice), RangePlace(index)]
                },
                new MirCall
                {
                    Target = RangePlace(result),
                    Function = MirRuntimeFunctions.CreateFunctionRef(
                        WellKnownStrings.InternalNames.ArrayShiftPrepend,
                        sequenceType,
                        SourceSpan.Empty),
                    Arguments = [RangePlace(slice), RangePlace(first), RangePlace(second), RangePlace(grow), size]
                }
            ],
            Terminator = new MirReturn { Value = RangePlace(result) }
        };
        var caller = new MirFunc
        {
            Name = "range_caller",
            Locals = [source, index, first, second, grow, slice, observed, result],
            BasicBlocks = [block],
            EntryBlockId = block.Id,
            ReturnType = sequenceType
        };
        var module = new MirModule
        {
            Name = "range_fixture",
            Functions = [consumer, caller],
            TypeDescriptors =
            {
                [referenceType.Value] = new TypeDescriptor.Ref(sequenceType)
            }
        };
        return new RangeFixture(module, block, source, first, grow);
    }

    private static MirFunc CreateRangeConsumer(
        TypeId sequenceType,
        TypeId referenceType,
        TypeId elementType,
        TypeId intType,
        bool escapeRange)
    {
        var values = RangeLocal(1, "values", referenceType, parameter: true);
        var index = RangeLocal(2, "index", intType, parameter: true);
        var sequence = RangeLocal(3, "sequence", sequenceType);
        var length = RangeLocal(4, "length", intType);
        var element = RangeLocal(5, "element", elementType);
        var escaped = RangeLocal(6, "escaped", intType);
        var instructions = new List<MirInstruction>
        {
            new MirLoad
            {
                Target = RangePlace(sequence),
                Source = new MirPlace
                {
                    Kind = PlaceKind.Deref,
                    Base = RangePlace(values),
                    TypeId = sequenceType
                }
            },
            new MirCall
            {
                Target = RangePlace(length),
                Function = MirRuntimeFunctions.CreateFunctionRef(
                    WellKnownStrings.InternalNames.ArrayLength,
                    intType,
                    SourceSpan.Empty),
                Arguments = [RangePlace(values)]
            },
            new MirLoad
            {
                Target = RangePlace(element),
                Source = new MirPlace
                {
                    Kind = PlaceKind.Index,
                    Base = RangePlace(sequence),
                    Index = RangePlace(index),
                    IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                    TypeId = elementType
                },
                CreatesBorrowAlias = false
            }
        };
        if (escapeRange)
        {
            instructions.Add(new MirCall
            {
                Target = RangePlace(escaped),
                Function = new MirFunctionRef { Name = "unknown_consumer", TypeId = intType },
                Arguments = [RangePlace(sequence)]
            });
        }

        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions = instructions,
            Terminator = new MirReturn { Value = RangePlace(length) }
        };
        return new MirFunc
        {
            Name = "range_consumer",
            Locals = [values, index, sequence, length, element, escaped],
            BasicBlocks = [block],
            EntryBlockId = block.Id,
            ReturnType = intType
        };
    }

    private static bool HasRuntimeIdentity(MirCall call, string name) =>
        call.Function is MirFunctionRef functionRef && MirRuntimeFunctions.HasIdentity(functionRef, name);

    private static MirLocal RangeLocal(int id, string name, TypeId typeId, bool parameter = false) => new()
    {
        Id = new LocalId { Value = id },
        Name = name,
        TypeId = typeId,
        IsParameter = parameter
    };

    private static MirPlace RangePlace(MirLocal local) => new()
    {
        Kind = PlaceKind.Local,
        Local = local.Id,
        TypeId = local.TypeId
    };

    private static MirConstant RangeConstant(long value, TypeId typeId) => new()
    {
        TypeId = typeId,
        Value = new MirConstantValue.IntValue(value)
    };

    private sealed record RangeFixture(
        MirModule Module,
        MirBasicBlock CallerBlock,
        MirLocal Source,
        MirLocal First,
        MirLocal Grow);
}
