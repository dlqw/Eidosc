using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class InliningTests
{
    private static readonly TypeId IntType = new(BaseTypes.IntId);
    private static readonly TypeId StringType = new(BaseTypes.StringId);

    [Fact]
    public void Run_ManagedByValueIdentity_KeepsCallUntilOwnershipProofIsAvailable()
    {
        var callee = BuildIdentityFunction("identity", new SymbolId(10), StringType);
        var caller = BuildCaller("caller", callee, StringType);
        var originalLocalCount = caller.Locals.Count;

        var optimized = RunInlining(new MirModule { Functions = [callee, caller] });
        var optimizedCaller = optimized.Functions.Single(function => function.Name == caller.Name);

        Assert.Equal(originalLocalCount, optimizedCaller.Locals.Count);
        Assert.IsType<MirCall>(Assert.Single(Assert.Single(optimizedCaller.BasicBlocks).Instructions));
    }

    [Fact]
    public void Run_MismatchedArgumentCount_KeepsCallWithoutAddingLocals()
    {
        var callee = BuildIdentityFunction("identity", new SymbolId(11), IntType);
        var caller = BuildCaller("caller", callee, IntType, includeArgument: false);
        var originalLocalCount = caller.Locals.Count;

        var optimized = RunInlining(new MirModule { Functions = [callee, caller] });
        var optimizedCaller = optimized.Functions.Single(function => function.Name == caller.Name);

        Assert.Equal(originalLocalCount, optimizedCaller.Locals.Count);
        Assert.IsType<MirCall>(Assert.Single(Assert.Single(optimizedCaller.BasicBlocks).Instructions));
    }

    [Fact]
    public void Run_NonReturnSingleBlockCandidate_KeepsCall()
    {
        var callee = BuildIdentityFunction(
            "diverge",
            new SymbolId(12),
            IntType,
            terminator: new MirUnreachable());
        var caller = BuildCaller("caller", callee, IntType);

        var optimized = RunInlining(new MirModule { Functions = [callee, caller] });
        var optimizedCaller = optimized.Functions.Single(function => function.Name == caller.Name);

        Assert.IsType<MirCall>(Assert.Single(Assert.Single(optimizedCaller.BasicBlocks).Instructions));
    }

    [Fact]
    public void Run_GenericCandidate_KeepsCall()
    {
        var callee = BuildIdentityFunction(
            "generic_identity",
            new SymbolId(13),
            IntType,
            genericParameterCount: 1);
        var caller = BuildCaller("caller", callee, IntType);

        AssertCallRemains(callee, caller);
    }

    [Fact]
    public void Run_RuntimeWordAbiCandidate_KeepsCall()
    {
        var callee = BuildIdentityFunction("runtime_helper", new SymbolId(14), IntType);
        callee.IsRuntimeWordAbi = true;
        var caller = BuildCaller("caller", callee, IntType);

        AssertCallRemains(callee, caller);
    }

    [Fact]
    public void Run_CallerOwnedAggregateAbiCandidate_KeepsCall()
    {
        var callee = BuildIdentityFunction("aggregate_helper", new SymbolId(15), StringType);
        callee.CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
        {
            OutReturnType = StringType,
            OutReturnLocals = new HashSet<LocalId> { callee.Locals[0].Id }
        };
        var caller = BuildCaller("caller", callee, StringType);

        AssertCallRemains(callee, caller);
    }

    [Fact]
    public void Run_UnitParameterCandidate_KeepsCallSugarBoundary()
    {
        var unitType = new TypeId(BaseTypes.UnitId);
        var callee = BuildIdentityFunction("unit_identity", new SymbolId(18), unitType);
        var caller = BuildCaller("caller", callee, unitType);

        AssertCallRemains(callee, caller);
    }

    [Fact]
    public void Run_UnitConstantArgument_KeepsCallSugarBoundaryWhenParameterTypeIsUnavailable()
    {
        var parameter = Local(1, "unit", TypeId.None, isParameter: true);
        var callee = new MirFunc
        {
            Name = "unit_call_sugar",
            SymbolId = new SymbolId(19),
            FunctionId = new FunctionId
            {
                SymbolId = new SymbolId(19),
                Name = "unit_call_sugar"
            },
            ReturnType = IntType,
            EntryBlockId = Block(1),
            Locals = [parameter],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Terminator = new MirReturn
                    {
                        Value = new MirConstant
                        {
                            TypeId = IntType,
                            Value = new MirConstantValue.IntValue(1)
                        }
                    }
                }
            ]
        };

        var result = Local(1, "result", IntType);
        var call = new MirCall
        {
            Target = Place(result.Id, IntType),
            Function = new MirFunctionRef
            {
                Name = callee.Name,
                SymbolId = callee.SymbolId,
                FunctionId = callee.FunctionId,
                TypeId = IntType
            },
            Arguments =
            [
                new MirConstant
                {
                    TypeId = new TypeId(BaseTypes.UnitId),
                    Value = new MirConstantValue.UnitValue()
                }
            ]
        };
        var caller = new MirFunc
        {
            Name = "caller",
            SymbolId = new SymbolId(119),
            ReturnType = IntType,
            EntryBlockId = Block(1),
            Locals = [result],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(1),
                    IsEntry = true,
                    Instructions = [call],
                    Terminator = new MirReturn { Value = Place(result.Id, IntType) }
                }
            ]
        };

        AssertCallRemains(callee, caller);
    }

    [Fact]
    public void Run_BorrowedArgumentCall_KeepsCallBoundary()
    {
        var callee = BuildIdentityFunction("borrowed_identity", new SymbolId(20), StringType);
        var caller = BuildCaller("caller", callee, StringType, borrowArgument: true);

        AssertCallRemains(callee, caller);
    }

    [Fact]
    public void Run_RewritePreservesCallerMetadata()
    {
        var callee = BuildIdentityFunction("identity", new SymbolId(16), IntType);
        var caller = BuildCaller("caller", callee, IntType);
        var ownershipContract = OwnershipContract.Create(
            new SymbolId(17),
            caller.Name,
            [("value", IntType)],
            IntType,
            typeDescriptors: null);
        var aggregateAbi = new MirCallerOwnedAggregateAbi
        {
            OutReturnType = new TypeId(9001),
            OutReturnLocals = new HashSet<LocalId> { caller.Locals[1].Id }
        };
        caller.OwnershipContract = ownershipContract;
        caller.CallerOwnedAggregateAbi = aggregateAbi;

        var optimized = RunInlining(new MirModule { Functions = [callee, caller] });
        var optimizedCaller = optimized.Functions.Single(function => function.Name == caller.Name);

        Assert.Same(ownershipContract, optimizedCaller.OwnershipContract);
        Assert.Same(aggregateAbi, optimizedCaller.CallerOwnedAggregateAbi);
    }

    [Fact]
    public void CreateDefault_RegistersInliningBeforeTailCallAndDropInsertion()
    {
        var passNames = MirOptimizer.CreateDefault().PassNames;

        var inliningIndex = passNames.IndexOf("Inlining");
        Assert.True(inliningIndex >= 0);
        Assert.True(inliningIndex < passNames.IndexOf("TailCallOptimization"));
        Assert.True(inliningIndex < passNames.IndexOf("DropInsertion"));
    }

    private static void AssertCallRemains(MirFunc callee, MirFunc caller)
    {
        var optimized = RunInlining(new MirModule { Functions = [callee, caller] });
        var optimizedCaller = optimized.Functions.Single(function => function.Name == caller.Name);

        Assert.IsType<MirCall>(Assert.Single(Assert.Single(optimizedCaller.BasicBlocks).Instructions));
    }

    private static MirModule RunInlining(MirModule module)
    {
        var summaries = module.Functions
            .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static _ => FunctionOptimizationSummary.Pure,
                StringComparer.Ordinal);
        var pass = new Inlining(maxInlineSize: 0);
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs =
            new FunctionOptimizationProofIndex(
                new FunctionOptimizationSummaryIndex(summaries),
                RecursiveCallAnalysis.Analyze(module));
        return pass.Run(module);
    }

    private static MirFunc BuildIdentityFunction(
        string name,
        SymbolId symbolId,
        TypeId type,
        MirTerminator? terminator = null,
        int genericParameterCount = 0)
    {
        var parameter = Local(1, "value", type, isParameter: true);
        var block = new MirBasicBlock
        {
            Id = Block(1),
            IsEntry = true,
            Terminator = terminator ?? new MirReturn { Value = Place(parameter.Id, type) }
        };
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = name },
            ReturnType = type,
            GenericParameterCount = genericParameterCount,
            GenericParameters = genericParameterCount == 0
                ? []
                : [new MirGenericParameter { Name = "T", ParameterIndex = 0 }],
            EntryBlockId = block.Id,
            Locals = [parameter],
            BasicBlocks = [block]
        };
    }

    private static MirFunc BuildCaller(
        string name,
        MirFunc callee,
        TypeId type,
        bool includeArgument = true,
        bool borrowArgument = false)
    {
        var argument = Local(1, "argument", type, isParameter: true);
        var result = Local(2, "result", type);
        var call = new MirCall
        {
            Target = Place(result.Id, type),
            Function = new MirFunctionRef
            {
                Name = callee.Name,
                SymbolId = callee.SymbolId,
                FunctionId = callee.FunctionId,
                TypeId = type
            },
            Arguments = includeArgument ? [Place(argument.Id, type)] : [],
            BorrowedArgumentIndices = borrowArgument
                ? new HashSet<int> { 0 }
                : []
        };
        var block = new MirBasicBlock
        {
            Id = Block(1),
            IsEntry = true,
            Instructions = [call],
            Terminator = new MirReturn { Value = Place(result.Id, type) }
        };
        return new MirFunc
        {
            Name = name,
            SymbolId = new SymbolId(100 + callee.SymbolId.Value),
            ReturnType = type,
            EntryBlockId = block.Id,
            Locals = [argument, result],
            BasicBlocks = [block]
        };
    }

    private static MirLocal Local(int id, string name, TypeId type, bool isParameter = false) => new()
    {
        Id = new LocalId { Value = id },
        Name = name,
        TypeId = type,
        IsParameter = isParameter
    };

    private static MirPlace Place(LocalId localId, TypeId type) => new()
    {
        Kind = PlaceKind.Local,
        Local = localId,
        TypeId = type
    };

    private static BlockId Block(int value) => new() { Value = value };
}
