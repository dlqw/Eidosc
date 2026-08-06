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
    public void Run_ExactArityDirectCallToNamedTwoParameterFunction_Inlines()
    {
        var callee = BuildTwoParameterFunction("select_left", new SymbolId(21));
        var caller = BuildTwoArgumentCaller("caller", callee);

        var optimized = RunInlining(new MirModule { Functions = [callee, caller] });
        var optimizedCaller = optimized.Functions.Single(function => function.Name == caller.Name);
        var instructions = Assert.Single(optimizedCaller.BasicBlocks).Instructions;

        Assert.DoesNotContain(instructions, static instruction => instruction is MirCall);
        Assert.Equal(3, instructions.Count);
        Assert.Equal(caller.Locals.Count + callee.Locals.Count, optimizedCaller.Locals.Count);
    }

    [Fact]
    public void Run_PartialApplicationOfNamedTwoParameterFunction_KeepsCallBoundary()
    {
        var callee = BuildTwoParameterFunction("select_left", new SymbolId(22));
        var caller = BuildTwoArgumentCaller("caller", callee, argumentCount: 1);

        AssertCallRemains(callee, caller);
    }

    [Fact]
    public void Run_IndirectTwoParameterCall_KeepsClosureBoundary()
    {
        var callee = BuildTwoParameterFunction("select_left", new SymbolId(23));
        var caller = BuildTwoArgumentCaller("caller", callee, useDirectReference: false);

        AssertCallRemains(callee, caller);
    }

    [Fact]
    public void Run_ExactArityDirectCall_ScalarizesCurriedAggregateProtocol()
    {
        var tupleType = new TypeId(9000);
        var symbolId = new SymbolId(24);
        var left = Local(1, "left", IntType, isParameter: true);
        var right = Local(2, "right", IntType, isParameter: true);
        var aggregate = Local(3, "aggregate", tupleType);
        var leftCopy = Local(4, "left_copy", IntType);
        var rightCopy = Local(5, "right_copy", IntType);
        var aggregateAlias = Local(6, "aggregate_alias", tupleType);
        var loadedLeft = Local(7, "loaded_left", IntType);
        var loadedRight = Local(8, "loaded_right", IntType);
        var result = Local(9, "result", IntType);
        var block = new MirBasicBlock
        {
            Id = Block(1),
            IsEntry = true,
            Instructions =
            [
                new MirAlloc { Target = Place(aggregate.Id, tupleType), TypeId = tupleType },
                new MirCopy { Target = Place(leftCopy.Id, IntType), Source = Place(left.Id, IntType) },
                new MirStore { Target = Index(aggregate.Id, 0, tupleType), Value = Place(leftCopy.Id, IntType) },
                new MirCopy { Target = Place(rightCopy.Id, IntType), Source = Place(right.Id, IntType) },
                new MirStore { Target = Index(aggregate.Id, 1, tupleType), Value = Place(rightCopy.Id, IntType) },
                new MirCopy { Target = Place(aggregateAlias.Id, tupleType), Source = Place(aggregate.Id, tupleType) },
                new MirLoad { Target = Place(loadedLeft.Id, IntType), Source = Index(aggregateAlias.Id, 0, tupleType) },
                new MirLoad { Target = Place(loadedRight.Id, IntType), Source = Index(aggregateAlias.Id, 1, tupleType) },
                new MirDrop { Value = Place(aggregateAlias.Id, tupleType) },
                new MirBinOp
                {
                    Target = Place(result.Id, IntType),
                    Operator = BinaryOp.Add,
                    Left = Place(loadedLeft.Id, IntType),
                    Right = Place(loadedRight.Id, IntType)
                }
            ],
            Terminator = new MirReturn { Value = Place(result.Id, IntType) }
        };
        var callee = new MirFunc
        {
            Name = "aggregate_step",
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = "aggregate_step" },
            ReturnType = IntType,
            EntryBlockId = block.Id,
            Locals = [left, right, aggregate, leftCopy, rightCopy, aggregateAlias, loadedLeft, loadedRight, result],
            BasicBlocks = [block]
        };
        var caller = BuildTwoArgumentCaller("caller", callee);

        var optimized = RunInlining(
            new MirModule
            {
                Functions = [callee, caller],
                CopyLikeTypeIds = [tupleType.Value]
            });
        var optimizedCaller = optimized.Functions.Single(function => function.Name == caller.Name);

        Assert.DoesNotContain(
            optimizedCaller.BasicBlocks.SelectMany(static block => block.Instructions),
            static instruction => instruction is MirAlloc);
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
        var pass = new Inlining(maxInlineSize: 30);
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

    private static MirFunc BuildTwoParameterFunction(string name, SymbolId symbolId)
    {
        var left = Local(1, "left", IntType, isParameter: true);
        var right = Local(2, "right", IntType, isParameter: true);
        var block = new MirBasicBlock
        {
            Id = Block(1),
            IsEntry = true,
            Terminator = new MirReturn { Value = Place(left.Id, IntType) }
        };
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = new FunctionId { SymbolId = symbolId, Name = name },
            ReturnType = IntType,
            EntryBlockId = block.Id,
            Locals = [left, right],
            BasicBlocks = [block]
        };
    }

    private static MirFunc BuildTwoArgumentCaller(
        string name,
        MirFunc callee,
        int argumentCount = 2,
        bool useDirectReference = true)
    {
        var left = Local(1, "left", IntType, isParameter: true);
        var right = Local(2, "right", IntType, isParameter: true);
        var result = Local(3, "result", IntType);
        MirOperand function = useDirectReference
            ? new MirFunctionRef
            {
                Name = callee.Name,
                SymbolId = callee.SymbolId,
                FunctionId = callee.FunctionId,
                TypeId = IntType
            }
            : new MirConstant
            {
                TypeId = IntType,
                Value = new MirConstantValue.StringValue(callee.Name)
            };
        var arguments = new List<MirOperand> { Place(left.Id, IntType), Place(right.Id, IntType) };
        var call = new MirCall
        {
            Target = Place(result.Id, IntType),
            Function = function,
            Arguments = arguments.Take(argumentCount).ToList()
        };
        var block = new MirBasicBlock
        {
            Id = Block(1),
            IsEntry = true,
            Instructions = [call],
            Terminator = new MirReturn { Value = Place(result.Id, IntType) }
        };
        return new MirFunc
        {
            Name = name,
            SymbolId = new SymbolId(100 + callee.SymbolId.Value),
            ReturnType = IntType,
            EntryBlockId = block.Id,
            Locals = [left, right, result],
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

    private static MirPlace Index(LocalId localId, long index, TypeId aggregateType) => new()
    {
        Kind = PlaceKind.Index,
        Base = Place(localId, aggregateType),
        Index = new MirConstant
        {
            TypeId = IntType,
            Value = new MirConstantValue.IntValue(index)
        },
        TypeId = IntType
    };

    private static BlockId Block(int value) => new() { Value = value };
}
