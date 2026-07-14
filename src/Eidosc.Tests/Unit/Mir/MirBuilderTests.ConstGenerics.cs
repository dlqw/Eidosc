using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirBuilderTests
{
    [Fact]
    public void Pipeline_ValueOnlySpecializations_HaveDistinctIdentitiesAndReifiedBodies()
    {
        const string source = """
constant[comptime N: Int, comptime T: Type] :: T -> Int
{
    _ => N
}

four :: Unit -> Int
{
    _ => constant[4, Unit](())
}

five :: Unit -> Int
{
    _ => constant[5, Unit](())
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_const_generic_specialization.eidos",
            StopAtPhase = CompilationPhase.Llvm,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        var module = Assert.IsType<MirModule>(result.MirModule);
        var specializations = module.Functions
            .Where(static function => function.Name.StartsWith("constant__spec_", StringComparison.Ordinal))
            .ToList();

        Assert.Equal(2, specializations.Count);
        Assert.Equal(2, specializations.Select(static function => function.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.All(specializations, static function =>
        {
            Assert.Equal(0, function.GenericParameterCount);
            Assert.Empty(function.GenericParameters);
            Assert.DoesNotContain(
                function.BasicBlocks.SelectMany(static block => block.Instructions),
                static instruction => InstructionContainsConstGenericValue(instruction));
        });

        var returnedValues = specializations
            .SelectMany(static function => function.BasicBlocks)
            .Select(static block => block.Terminator)
            .OfType<MirReturn>()
            .Select(static returnTerminator => Assert.IsType<MirConstant>(returnTerminator.Value))
            .Select(static constant => Assert.IsType<MirConstantValue.IntValue>(constant.Value).Value)
            .OrderBy(static value => value)
            .ToArray();

        Assert.Equal([4L, 5L], returnedValues);
    }

    [Fact]
    public void Pipeline_SingleValueGenericApplication_ReifiesConstParameterInSpecializedBody()
    {
        const string source = """
constant[comptime N: Int] :: Unit -> Int
{
    _ => N
}

use :: Unit -> Int
{
    _ => constant[4](())
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_single_const_generic_specialization.eidos",
            StopAtPhase = CompilationPhase.Mir,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        var module = Assert.IsType<MirModule>(result.MirModule);
        var specialization = Assert.Single(
            module.Functions,
            static function => function.Name.StartsWith("constant__spec_", StringComparison.Ordinal));
        var returned = Assert.Single(
            specialization.BasicBlocks
                .Select(static block => block.Terminator)
                .OfType<MirReturn>());
        var constant = Assert.IsType<MirConstant>(returned.Value);
        Assert.Equal(4, Assert.IsType<MirConstantValue.IntValue>(constant.Value).Value);
    }

    [Fact]
    public void Pipeline_NestedConstGenericCall_PropagatesOuterValueIntoInnerSpecialization()
    {
        const string source = """
constant[comptime N: Int] :: Unit -> Int
{
    _ => N
}

forward[comptime N: Int] :: Unit -> Int
{
    _ => constant[N](())
}

use :: Unit -> Int
{
    _ => forward[4](())
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_nested_const_generic_specialization.eidos",
            StopAtPhase = CompilationPhase.Mir,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}")));
        var module = Assert.IsType<MirModule>(result.MirModule);
        var constantSpecialization = Assert.Single(
            module.Functions,
            static function => function.Name.StartsWith("constant__spec_", StringComparison.Ordinal));
        var returned = Assert.Single(
            constantSpecialization.BasicBlocks
                .Select(static block => block.Terminator)
                .OfType<MirReturn>());
        var constant = Assert.IsType<MirConstant>(returned.Value);
        Assert.Equal(4, Assert.IsType<MirConstantValue.IntValue>(constant.Value).Value);
        Assert.DoesNotContain(
            module.Functions.SelectMany(static function => function.BasicBlocks),
            static block => block.Instructions.Any(InstructionContainsConstGenericValue));
    }

    private static bool InstructionContainsConstGenericValue(MirInstruction instruction)
    {
        return instruction switch
        {
            MirAssign assign => ContainsConstGenericValue(assign.Target) || ContainsConstGenericValue(assign.Source),
            MirCall call => ContainsConstGenericValue(call.Target) ||
                            ContainsConstGenericValue(call.Function) ||
                            call.Arguments.Any(ContainsConstGenericValue),
            MirBinOp binOp => ContainsConstGenericValue(binOp.Target) ||
                              ContainsConstGenericValue(binOp.Left) ||
                              ContainsConstGenericValue(binOp.Right),
            MirUnaryOp unaryOp => ContainsConstGenericValue(unaryOp.Target) ||
                                  ContainsConstGenericValue(unaryOp.Operand),
            MirLoad load => ContainsConstGenericValue(load.Target) || ContainsConstGenericValue(load.Source),
            MirStore store => ContainsConstGenericValue(store.Target) || ContainsConstGenericValue(store.Value),
            MirDrop drop => ContainsConstGenericValue(drop.Value),
            MirCopy copy => ContainsConstGenericValue(copy.Target) || ContainsConstGenericValue(copy.Source),
            MirMove move => ContainsConstGenericValue(move.Target) || ContainsConstGenericValue(move.Source),
            MirAlloc alloc => ContainsConstGenericValue(alloc.Target),
            _ => false
        };
    }

    private static bool ContainsConstGenericValue(MirOperand? operand)
    {
        return operand switch
        {
            MirConstGenericValue => true,
            MirPlace place => ContainsConstGenericValue(place.Base) || ContainsConstGenericValue(place.Index),
            _ => false
        };
    }
}
