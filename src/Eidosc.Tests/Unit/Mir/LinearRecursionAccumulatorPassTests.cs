using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public class LinearRecursionAccumulatorPassTests
{
    private static readonly TypeId IntType = new(1);

    [Fact]
    public void Run_FibShape_RewritesToAccumulatorLoop()
    {
        var entryId = new BlockId { Value = 1 };
        var baseId = new BlockId { Value = 2 };
        var recId = new BlockId { Value = 3 };
        var functionSymbol = new SymbolId(10);
        var n = Local(1, "n", isParameter: true);
        var cmp = Local(2, "cmp", type: new TypeId(BaseTypes.BoolId));
        var t1 = Local(3, "t1");
        var r1 = Local(4, "r1");
        var t2 = Local(5, "t2");
        var r2 = Local(6, "r2");
        var sum = Local(7, "sum");

        var nPlace = Place(n.Id);
        var entryBlock = new MirBasicBlock
        {
            Id = entryId,
            IsEntry = true,
            Instructions =
            [
                new MirBinOp
                {
                    Target = Place(cmp.Id, new TypeId(BaseTypes.BoolId)),
                    Operator = BinaryOp.Lt,
                    Left = nPlace,
                    Right = IntConst(2)
                }
            ],
            Terminator = new MirSwitch
            {
                Discriminant = Place(cmp.Id, new TypeId(BaseTypes.BoolId)),
                Branches = [Branch(true, baseId)],
                DefaultTarget = recId
            }
        };

        var baseBlock = new MirBasicBlock
        {
            Id = baseId,
            IsEntry = false,
            Instructions = [],
            Terminator = new MirReturn { Value = nPlace }
        };

        var recBlock = new MirBasicBlock
        {
            Id = recId,
            IsEntry = false,
            Instructions =
            [
                new MirBinOp { Target = Place(t1.Id), Operator = BinaryOp.Sub, Left = nPlace, Right = IntConst(1) },
                new MirCall
                {
                    Target = Place(r1.Id),
                    Function = FunctionRef("fib", functionSymbol),
                    Arguments = [Place(t1.Id)]
                },
                new MirBinOp { Target = Place(t2.Id), Operator = BinaryOp.Sub, Left = nPlace, Right = IntConst(2) },
                new MirCall
                {
                    Target = Place(r2.Id),
                    Function = FunctionRef("fib", functionSymbol),
                    Arguments = [Place(t2.Id)]
                },
                new MirBinOp
                {
                    Target = Place(sum.Id),
                    Operator = BinaryOp.Add,
                    Left = Place(r1.Id),
                    Right = Place(r2.Id)
                }
            ],
            Terminator = new MirReturn { Value = Place(sum.Id) }
        };

        var optimized = Optimize(new MirFunc
        {
            Name = "fib",
            SymbolId = functionSymbol,
            EntryBlockId = entryId,
            ReturnType = IntType,
            Locals = [n, cmp, t1, r1, t2, r2, sum],
            BasicBlocks = [entryBlock, baseBlock, recBlock]
        });

        // Recursion block replaced by init/loop/done; single tail call remains.
        Assert.NotSame(optimized, new MirFunc());
        Assert.Equal(5, optimized.BasicBlocks.Count);
        Assert.DoesNotContain(optimized.BasicBlocks, block => block.Id == recId);

        var calls = optimized.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>().ToArray();
        Assert.Single(calls);
        Assert.Equal("fib", ((MirFunctionRef)calls[0].Function).Name);

        // Parameter n is mutable; accumulator locals were added.
        Assert.True(optimized.Locals[0].IsMutable);
        Assert.Contains(optimized.Locals, static local => local.Name == "__fib_acc");
        Assert.Contains(optimized.Locals, static local => local.Name == "__fib_tmp");

        // Entry still guards n < 2 -> base; default now flows to init.
        var entry = optimized.BasicBlocks.Single(static block => block.IsEntry);
        var entrySwitch = Assert.IsType<MirSwitch>(entry.Terminator);
        Assert.Equal(baseId, entrySwitch.Branches[0].Target);
        Assert.NotNull(entrySwitch.DefaultTarget);
        Assert.NotEqual(recId, entrySwitch.DefaultTarget.Value);

        // Loop self-edge exists (backedge) after the init block.
        var init = optimized.BasicBlocks.Single(block => block.Id == entrySwitch.DefaultTarget.Value);
        var initGoto = Assert.IsType<MirGoto>(init.Terminator);
        var loop = optimized.BasicBlocks.Single(block => block.Id == initGoto.Target);
        var loopSwitch = Assert.IsType<MirSwitch>(loop.Terminator);
        Assert.True(loopSwitch.DefaultTarget.HasValue);
        Assert.Equal(loop.Id, loopSwitch.DefaultTarget.GetValueOrDefault());
    }

    [Fact]
    public void Run_FibShapeWithoutTrustedSummary_ReturnsUnchanged()
    {
        var (func, _) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: true);

        Assert.Same(func, Optimize(func, includeTrustedSummary: false));
    }

    [Fact]
    public void Run_FibShapeWithDeclaredEffect_ReturnsUnchanged()
    {
        var (func, _) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: true);
        var effects = new EffectRow([new EffectTag(new SymbolId(30), "ExternalState")]);

        Assert.Same(func, Optimize(func, functionEffects: effects));
    }

    [Fact]
    public void Run_AdditionalPureCall_ReturnsUnchanged()
    {
        var (func, recBlock) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: true);
        var helperParameter = Local(20, "value", isParameter: true);
        var helper = new MirFunc
        {
            Name = "observe",
            SymbolId = new SymbolId(20),
            EntryBlockId = Block(20),
            ReturnType = IntType,
            Locals = [helperParameter],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = Block(20),
                    IsEntry = true,
                    Terminator = new MirReturn { Value = Place(helperParameter.Id) }
                }
            ]
        };
        var observed = Local(8, "observed");
        func.Locals.Add(observed);
        recBlock.Instructions.Insert(0, new MirCall
        {
            Target = Place(observed.Id),
            Function = FunctionRef(helper.Name, helper.SymbolId),
            Arguments = [Place(func.Locals.Single(static local => local.IsParameter).Id)]
        });

        Assert.Same(func, Optimize(func, additionalFunctions: [helper]));
    }

    [Fact]
    public void Run_AdditionalStore_ReturnsUnchanged()
    {
        var (func, recBlock) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: true);
        var parameter = func.Locals.Single(static local => local.IsParameter);
        recBlock.Instructions.Insert(0, new MirStore
        {
            Target = Place(parameter.Id),
            Value = Place(parameter.Id)
        });

        Assert.Same(func, Optimize(func));
    }

    [Fact]
    public void Run_AdditionalDrop_ReturnsUnchanged()
    {
        var (func, recBlock) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: true);
        var parameter = func.Locals.Single(static local => local.IsParameter);
        recBlock.Instructions.Insert(0, new MirDrop { Value = Place(parameter.Id) });

        Assert.Same(func, Optimize(func));
    }

    [Fact]
    public void Run_AdditionalArithmetic_ReturnsUnchanged()
    {
        var (func, recBlock) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: true);
        var extra = Local(8, "extra");
        func.Locals.Add(extra);
        recBlock.Instructions.Insert(0, new MirBinOp
        {
            Target = Place(extra.Id),
            Operator = BinaryOp.Mul,
            Left = Place(func.Locals.Single(static local => local.IsParameter).Id),
            Right = IntConst(2)
        });

        Assert.Same(func, Optimize(func));
    }

    [Fact]
    public void Run_CallBeforeArgumentDefinition_ReturnsUnchanged()
    {
        var (func, recBlock) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: true);
        var firstSubtraction = recBlock.Instructions[0];
        recBlock.Instructions.RemoveAt(0);
        recBlock.Instructions.Insert(1, firstSubtraction);

        Assert.Same(func, Optimize(func));
    }

    [Fact]
    public void Run_SameNameCallWithoutStructuredIdentity_ReturnsUnchanged()
    {
        var (func, recBlock) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: true);
        var callIndex = recBlock.Instructions.FindIndex(static instruction => instruction is MirCall);
        var call = (MirCall)recBlock.Instructions[callIndex];
        recBlock.Instructions[callIndex] = call with
        {
            Function = FunctionRef(func.Name, SymbolId.None)
        };

        Assert.Same(func, Optimize(func));
    }

    [Fact]
    public void Run_NonCanonicalIntegerType_ReturnsUnchanged()
    {
        var (func, _) = BuildFibLike(
            1,
            2,
            BinaryOp.Add,
            baseReturnsParam: true,
            valueType: new TypeId(BaseTypes.Int64Id));

        Assert.Same(func, Optimize(func));
    }

    [Fact]
    public void Run_RewritePreservesFunctionMetadata()
    {
        var (func, _) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: true);
        var ownershipContract = OwnershipContract.Create(
            func.SymbolId,
            func.Name,
            [("n", IntType)],
            IntType,
            typeDescriptors: null);
        var aggregateAbi = new MirCallerOwnedAggregateAbi
        {
            OutReturnType = new TypeId(9001),
            OutReturnLocals = new HashSet<LocalId> { func.Locals[0].Id }
        };
        func.OwnershipContract = ownershipContract;
        func.CallerOwnedAggregateAbi = aggregateAbi;

        var optimized = Optimize(func);

        Assert.Same(ownershipContract, optimized.OwnershipContract);
        Assert.Same(aggregateAbi, optimized.CallerOwnedAggregateAbi);
    }

    [Fact]
    public void Run_OptimizedAndOriginalFunctionsAgreeAcrossRepresentativeInputs()
    {
        var (original, _) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: true);
        var optimized = Optimize(original);

        for (var input = -5; input <= 20; input++)
        {
            Assert.Equal(Evaluate(original, input), Evaluate(optimized, input));
        }
    }

    [Fact]
    public void AccumulatorIdentity_PreservesUncheckedIntOverflow()
    {
        var fibonacci = new long[201];
        fibonacci[1] = 1;
        for (var n = 2; n < fibonacci.Length; n++)
        {
            fibonacci[n] = unchecked(fibonacci[n - 1] + fibonacci[n - 2]);
        }

        for (var n = 0; n < fibonacci.Length; n++)
        {
            long accumulator = 0;
            var cursor = n;
            while (cursor >= 2)
            {
                accumulator = unchecked(accumulator + fibonacci[cursor - 1]);
                cursor -= 2;
            }

            Assert.Equal(fibonacci[n], unchecked(accumulator + cursor));
        }
    }

    [Theory]
    [InlineData(1, 3)] // offsets not 1/2
    [InlineData(2, 4)]
    public void Run_NonFibonacciOffsets_ReturnsUnchanged(int offsetA, int offsetB)
    {
        var (func, _) = BuildFibLike(offsetA, offsetB, BinaryOp.Add, baseReturnsParam: true);
        var optimized = Optimize(func);
        Assert.Same(func, optimized);
    }

    [Fact]
    public void Run_MultiplyCombination_ReturnsUnchanged()
    {
        var (func, _) = BuildFibLike(1, 2, BinaryOp.Mul, baseReturnsParam: true);
        Assert.Same(func, Optimize(func));
    }

    [Fact]
    public void Run_BaseCaseNotReturningParam_ReturnsUnchanged()
    {
        var (func, _) = BuildFibLike(1, 2, BinaryOp.Add, baseReturnsParam: false);
        Assert.Same(func, Optimize(func));
    }

    [Fact]
    public void Run_TwoParameters_ReturnsUnchanged()
    {
        var entryId = new BlockId { Value = 1 };
        var n = Local(1, "n", isParameter: true);
        var m = Local(2, "m", isParameter: true);
        var func = new MirFunc
        {
            Name = "fib",
            SymbolId = new SymbolId(10),
            EntryBlockId = entryId,
            ReturnType = IntType,
            Locals = [n, m],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = entryId,
                    IsEntry = true,
                    Instructions = [],
                    Terminator = new MirReturn { Value = Place(n.Id) }
                }
            ]
        };

        Assert.Same(func, Optimize(func));
    }

    private static (MirFunc Func, MirBasicBlock RecBlock) BuildFibLike(
        int offsetA,
        int offsetB,
        BinaryOp combineOp,
        bool baseReturnsParam,
        TypeId? valueType = null)
    {
        var type = valueType ?? IntType;
        var entryId = new BlockId { Value = 1 };
        var baseId = new BlockId { Value = 2 };
        var recId = new BlockId { Value = 3 };
        var functionSymbol = new SymbolId(10);
        var n = Local(1, "n", isParameter: true, type: type);
        var cmp = Local(2, "cmp", type: new TypeId(BaseTypes.BoolId));
        var t1 = Local(3, "t1", type: type);
        var r1 = Local(4, "r1", type: type);
        var t2 = Local(5, "t2", type: type);
        var r2 = Local(6, "r2", type: type);
        var sum = Local(7, "sum", type: type);

        var nPlace = Place(n.Id, type);
        var entryBlock = new MirBasicBlock
        {
            Id = entryId,
            IsEntry = true,
            Instructions =
            [
                new MirBinOp
                {
                    Target = Place(cmp.Id, new TypeId(BaseTypes.BoolId)),
                    Operator = BinaryOp.Lt,
                    Left = nPlace,
                    Right = IntConst(2, type)
                }
            ],
            Terminator = new MirSwitch
            {
                Discriminant = Place(cmp.Id, new TypeId(BaseTypes.BoolId)),
                Branches = [Branch(true, baseId)],
                DefaultTarget = recId
            }
        };

        var baseBlock = new MirBasicBlock
        {
            Id = baseId,
            IsEntry = false,
            Instructions = [],
            Terminator = new MirReturn { Value = baseReturnsParam ? nPlace : IntConst(0, type) }
        };

        var recBlock = new MirBasicBlock
        {
            Id = recId,
            IsEntry = false,
            Instructions =
            [
                new MirBinOp { Target = Place(t1.Id, type), Operator = BinaryOp.Sub, Left = nPlace, Right = IntConst(offsetA, type) },
                new MirCall
                {
                    Target = Place(r1.Id, type),
                    Function = FunctionRef("fib", functionSymbol),
                    Arguments = [Place(t1.Id, type)]
                },
                new MirBinOp { Target = Place(t2.Id, type), Operator = BinaryOp.Sub, Left = nPlace, Right = IntConst(offsetB, type) },
                new MirCall
                {
                    Target = Place(r2.Id, type),
                    Function = FunctionRef("fib", functionSymbol),
                    Arguments = [Place(t2.Id, type)]
                },
                new MirBinOp
                {
                    Target = Place(sum.Id, type),
                    Operator = combineOp,
                    Left = Place(r1.Id, type),
                    Right = Place(r2.Id, type)
                }
            ],
            Terminator = new MirReturn { Value = Place(sum.Id, type) }
        };

        var func = new MirFunc
        {
            Name = "fib",
            SymbolId = functionSymbol,
            EntryBlockId = entryId,
            ReturnType = type,
            Locals = [n, cmp, t1, r1, t2, r2, sum],
            BasicBlocks = [entryBlock, baseBlock, recBlock]
        };

        return (func, recBlock);
    }

    private static MirFunc Optimize(
        MirFunc func,
        bool includeTrustedSummary = true,
        IReadOnlyList<MirFunc>? additionalFunctions = null,
        EffectRow? functionEffects = null)
    {
        var functions = new List<MirFunc> { func };
        if (additionalFunctions != null)
        {
            functions.AddRange(additionalFunctions);
        }

        Dictionary<SymbolId, FunctionEffectSummary>? summaries = null;
        if (includeTrustedSummary)
        {
            summaries = functions
                .Where(static function => function.SymbolId.IsValid)
                .ToDictionary(
                    static function => function.SymbolId,
                    static _ => new FunctionEffectSummary(EffectRow.Pure, EffectRow.Pure));
            if (functionEffects != null)
            {
                summaries[func.SymbolId] = new FunctionEffectSummary(functionEffects, functionEffects);
            }
        }

        var optimizer = new MirOptimizer(effectSummaries: summaries);
        optimizer.RegisterPass(new LinearRecursionAccumulatorPass());
        return optimizer.Optimize(new MirModule { Functions = functions }).Functions[0];
    }

    private static MirLocal Local(
        int id,
        string name,
        bool isParameter = false,
        TypeId? type = null)
    {
        return new MirLocal
        {
            Id = new LocalId { Value = id },
            Name = name,
            TypeId = type ?? IntType,
            IsMutable = false,
            IsParameter = isParameter,
            Span = default
        };
    }

    private static MirPlace Place(LocalId localId, TypeId? type = null)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = localId,
            TypeId = type ?? IntType,
            Span = default
        };
    }

    private static MirConstant IntConst(long value, TypeId? type = null)
    {
        return new MirConstant
        {
            Value = new MirConstantValue.IntValue(value),
            TypeId = type ?? IntType,
            Span = default
        };
    }

    private static MirSwitchBranch Branch(bool value, BlockId target)
    {
        return new MirSwitchBranch
        {
            Value = new MirConstant
            {
                Value = new MirConstantValue.BoolValue(value),
                TypeId = new TypeId(BaseTypes.BoolId),
                Span = default
            },
            Target = target
        };
    }

    private static MirFunctionRef FunctionRef(string name, SymbolId symbolId)
    {
        return new MirFunctionRef
        {
            Name = name,
            SymbolId = symbolId,
            Span = default
        };
    }

    private static BlockId Block(int value) => new() { Value = value };

    private static long Evaluate(MirFunc function, long argument)
    {
        var steps = 0;
        return Evaluate(function, argument, ref steps);
    }

    private static long Evaluate(MirFunc function, long argument, ref int sharedSteps)
    {
        var steps = sharedSteps;
        var values = new Dictionary<LocalId, long>
        {
            [function.Locals.Single(static local => local.IsParameter).Id] = argument
        };
        var blockId = function.EntryBlockId;

        while (steps++ < 1_000_000)
        {
            var block = function.BasicBlocks.Single(candidate => candidate.Id == blockId);
            foreach (var instruction in block.Instructions)
            {
                switch (instruction)
                {
                    case MirAssign assign:
                        values[assign.Target.Local] = Read(assign.Source, values);
                        break;
                    case MirCopy copy:
                        values[copy.Target.Local] = Read(copy.Source, values);
                        break;
                    case MirBinOp binOp:
                        values[((MirPlace)binOp.Target).Local] = binOp.Operator switch
                        {
                            BinaryOp.Add => unchecked(Read(binOp.Left, values) + Read(binOp.Right, values)),
                            BinaryOp.Sub => unchecked(Read(binOp.Left, values) - Read(binOp.Right, values)),
                            BinaryOp.Lt => Read(binOp.Left, values) < Read(binOp.Right, values) ? 1 : 0,
                            _ => throw new InvalidOperationException($"unsupported test operation {binOp.Operator}")
                        };
                        break;
                    case MirCall call:
                        var nestedSteps = steps;
                        values[call.Target!.Local] = Evaluate(function, Read(call.Arguments[0], values), ref nestedSteps);
                        steps = nestedSteps;
                        break;
                    default:
                        throw new InvalidOperationException($"unsupported test instruction {instruction.GetType().Name}");
                }
            }

            switch (block.Terminator)
            {
                case MirReturn ret:
                    sharedSteps = steps;
                    return Read(ret.Value!, values);
                case MirGoto jump:
                    blockId = jump.Target;
                    break;
                case MirSwitch branch:
                    var discriminant = Read(branch.Discriminant, values);
                    blockId = branch.Branches
                        .FirstOrDefault(candidate => Read(candidate.Value, values) == discriminant)?.Target
                        ?? branch.DefaultTarget!.Value;
                    break;
                default:
                    throw new InvalidOperationException("unsupported test terminator");
            }
        }

        throw new InvalidOperationException("test MIR evaluator exceeded its step budget");
    }

    private static long Read(MirOperand operand, IReadOnlyDictionary<LocalId, long> values)
    {
        return operand switch
        {
            MirPlace place => values[place.Local],
            MirConstant { Value: MirConstantValue.IntValue integer } => integer.Value,
            MirConstant { Value: MirConstantValue.BoolValue boolean } => boolean.Value ? 1 : 0,
            _ => throw new InvalidOperationException($"unsupported test operand {operand.GetType().Name}")
        };
    }
}
