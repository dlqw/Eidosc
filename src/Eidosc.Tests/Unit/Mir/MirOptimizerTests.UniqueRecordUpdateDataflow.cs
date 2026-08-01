using Eidosc;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirOptimizerTests
{
    [Fact]
    public void UniqueRecordUpdate_LoopCarriedOwner_CreatesSpecializedVariant()
    {
        var fixture = CreateUniqueUpdateFixture(7300, "loop");
        var boolType = new TypeId(BaseTypes.BoolId);
        var state = UniqueLocal(10, "state", fixture.RecordType);
        var next = UniqueLocal(11, "next", fixture.RecordType);
        var condition = UniqueLocal(12, "condition", boolType, parameter: true);
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions = [UniqueConstructor(state, fixture.RecordType)],
            Terminator = new MirGoto { Target = new BlockId { Value = 2 } }
        };
        var loop = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions =
            [
                UniqueCall(next, fixture.Function, state),
                new MirMove { Target = UniquePlace(state), Source = UniquePlace(next) }
            ],
            Terminator = new MirSwitch
            {
                Discriminant = UniquePlace(condition),
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant
                        {
                            TypeId = boolType,
                            Value = new MirConstantValue.BoolValue(true)
                        },
                        Target = loopId()
                    }
                ],
                DefaultTarget = new BlockId { Value = 3 }
            }
        };
        var exit = new MirBasicBlock
        {
            Id = new BlockId { Value = 3 },
            Terminator = new MirReturn { Value = UniquePlace(state) }
        };
        var caller = UniqueCaller("loop_caller", fixture.RecordType, [state, next, condition], [entry, loop, exit]);
        var module = new MirModule { Functions = [fixture.Function, caller] };

        new UniqueRecordUpdateSpecializationPass().Run(module);

        AssertUniqueCallWasSpecialized(module, fixture.Function, loop, 0);

        static BlockId loopId() => new() { Value = 2 };
    }

    [Fact]
    public void UniqueRecordUpdate_BranchMergeOwner_CreatesSpecializedVariant()
    {
        var fixture = CreateUniqueUpdateFixture(7310, "branch");
        var boolType = new TypeId(BaseTypes.BoolId);
        var condition = UniqueLocal(10, "condition", boolType, parameter: true);
        var left = UniqueLocal(11, "left", fixture.RecordType);
        var right = UniqueLocal(12, "right", fixture.RecordType);
        var merged = UniqueLocal(13, "merged", fixture.RecordType);
        var result = UniqueLocal(14, "result", fixture.RecordType);
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Terminator = new MirSwitch
            {
                Discriminant = UniquePlace(condition),
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant
                        {
                            TypeId = boolType,
                            Value = new MirConstantValue.BoolValue(true)
                        },
                        Target = new BlockId { Value = 2 }
                    }
                ],
                DefaultTarget = new BlockId { Value = 3 }
            }
        };
        var leftBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions =
            [
                UniqueConstructor(left, fixture.RecordType),
                new MirMove { Target = UniquePlace(merged), Source = UniquePlace(left) }
            ],
            Terminator = new MirGoto { Target = new BlockId { Value = 4 } }
        };
        var rightBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 3 },
            Instructions =
            [
                UniqueConstructor(right, fixture.RecordType),
                new MirMove { Target = UniquePlace(merged), Source = UniquePlace(right) }
            ],
            Terminator = new MirGoto { Target = new BlockId { Value = 4 } }
        };
        var merge = new MirBasicBlock
        {
            Id = new BlockId { Value = 4 },
            Instructions = [UniqueCall(result, fixture.Function, merged)],
            Terminator = new MirReturn { Value = UniquePlace(result) }
        };
        var caller = UniqueCaller(
            "branch_caller",
            fixture.RecordType,
            [condition, left, right, merged, result],
            [entry, leftBlock, rightBlock, merge]);
        var module = new MirModule { Functions = [fixture.Function, caller] };

        new UniqueRecordUpdateSpecializationPass().Run(module);

        AssertUniqueCallWasSpecialized(module, fixture.Function, merge, 0);
    }

    [Fact]
    public void UniqueRecordUpdate_AggregateSlotMove_CreatesSpecializedVariant()
    {
        var fixture = CreateUniqueUpdateFixture(7320, "aggregate");
        var aggregateType = new TypeId(7321);
        var created = UniqueLocal(10, "created", fixture.RecordType);
        var aggregate = UniqueLocal(11, "aggregate", aggregateType);
        var loaded = UniqueLocal(12, "loaded", fixture.RecordType);
        var result = UniqueLocal(13, "result", fixture.RecordType);
        var slot = new MirPlace
        {
            Kind = PlaceKind.Index,
            Base = UniquePlace(aggregate),
            Index = new MirConstant
            {
                TypeId = new TypeId(BaseTypes.IntId),
                Value = new MirConstantValue.IntValue(0)
            },
            IndexAccessKind = MirIndexAccessKind.Aggregate,
            TypeId = fixture.RecordType
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                UniqueConstructor(created, fixture.RecordType),
                new MirStore { Target = slot, Value = UniquePlace(created) },
                new MirLoad
                {
                    Target = UniquePlace(loaded),
                    Source = slot,
                    MovesOutOfSource = true,
                    CreatesBorrowAlias = false
                },
                UniqueCall(result, fixture.Function, loaded)
            ],
            Terminator = new MirReturn { Value = UniquePlace(result) }
        };
        var caller = UniqueCaller(
            "aggregate_caller",
            fixture.RecordType,
            [created, aggregate, loaded, result],
            [block]);
        var module = new MirModule { Functions = [fixture.Function, caller] };

        new UniqueRecordUpdateSpecializationPass().Run(module);

        AssertUniqueCallWasSpecialized(module, fixture.Function, block, 3);
    }

    [Fact]
    public void UniqueRecordUpdate_LiveBorrowAlias_KeepsGeneralCowCall()
    {
        var fixture = CreateUniqueUpdateFixture(7330, "borrow");
        var created = UniqueLocal(10, "created", fixture.RecordType);
        var alias = UniqueLocal(11, "alias", fixture.RecordType);
        var result = UniqueLocal(12, "result", fixture.RecordType);
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                UniqueConstructor(created, fixture.RecordType),
                new MirLoad
                {
                    Target = UniquePlace(alias),
                    Source = UniquePlace(created),
                    CreatesBorrowAlias = true
                },
                UniqueCall(result, fixture.Function, created)
            ],
            Terminator = new MirReturn { Value = UniquePlace(result) }
        };
        var caller = UniqueCaller("borrow_caller", fixture.RecordType, [created, alias, result], [block]);
        var module = new MirModule { Functions = [fixture.Function, caller] };

        new UniqueRecordUpdateSpecializationPass().Run(module);

        Assert.Equal(2, module.Functions.Count);
        var call = Assert.IsType<MirCall>(block.Instructions[2]);
        Assert.Equal(fixture.Function.FunctionId.StableIdentityKey,
            Assert.IsType<MirFunctionRef>(call.Function).FunctionId.StableIdentityKey);
    }

    [Fact]
    public void UniqueRecordUpdate_BodylessIntrinsicAndFfi_DoNotCreateVariants()
    {
        var recordType = new TypeId(7340);
        var intrinsic = new MirFunc
        {
            Name = "fixture_intrinsic",
            IntrinsicName = "fixture_intrinsic",
            ReturnType = recordType
        };
        var ffi = new MirFunc
        {
            Name = "fixture_ffi",
            IsExternal = true,
            ExternalSymbolName = "fixture_ffi",
            ReturnType = recordType
        };
        var module = new MirModule { Functions = [intrinsic, ffi] };

        new UniqueRecordUpdateSpecializationPass().Run(module);

        Assert.Equal(2, module.Functions.Count);
        Assert.DoesNotContain(module.Functions, function => function.Name.Contains("__unique_", StringComparison.Ordinal));
    }

    [Fact]
    public void UniqueRecordUpdate_RecursiveCall_ReusesSingleStableVariant()
    {
        var fixture = CreateUniqueUpdateFixture(7350, "recursive", recursive: true);
        var created = UniqueLocal(10, "created", fixture.RecordType);
        var result = UniqueLocal(11, "result", fixture.RecordType);
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                UniqueConstructor(created, fixture.RecordType),
                UniqueCall(result, fixture.Function, created)
            ],
            Terminator = new MirReturn { Value = UniquePlace(result) }
        };
        var caller = UniqueCaller("recursive_caller", fixture.RecordType, [created, result], [block]);
        var module = new MirModule { Functions = [fixture.Function, caller] };

        new UniqueRecordUpdateSpecializationPass().Run(module);

        var specialized = Assert.Single(
            module.Functions,
            function => function.Name.Contains("__unique_0", StringComparison.Ordinal));
        var recursiveCall = Assert.IsType<MirCall>(specialized.BasicBlocks[0].Instructions[1]);
        Assert.Equal(
            specialized.FunctionId.StableIdentityKey,
            Assert.IsType<MirFunctionRef>(recursiveCall.Function).FunctionId.StableIdentityKey);
        Assert.True(Assert.IsType<MirCall>(specialized.BasicBlocks[0].Instructions[0]).RecordUpdate!.IsKnownUnique);
    }

    [Fact]
    public void UniqueRecordUpdate_ManyAssumptionSets_RespectsVariantLimit()
    {
        var recordType = new TypeId(7360);
        var identity = new FunctionId
        {
            StableIdentityKey = "test:unique:variant-limit",
            Name = "update_variant_limit",
            QualifiedName = "test.update_variant_limit"
        };
        var parameters = Enumerable.Range(0, 9)
            .Select(index => UniqueLocal(index + 1, $"value_{index}", recordType, parameter: true))
            .ToList();
        var updated = UniqueLocal(20, "updated", recordType);
        var updateBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = UniquePlace(updated),
                    Function = new MirFunctionRef
                    {
                        Name = "Record",
                        SymbolKind = SymbolKind.Constructor,
                        TypeId = recordType
                    },
                    Arguments = [UniquePlace(parameters[0])],
                    RecordUpdate = new MirRecordUpdateInfo
                    {
                        Source = UniquePlace(parameters[0]),
                        UpdatedFieldIndices = [0]
                    }
                }
            ],
            Terminator = new MirReturn { Value = UniquePlace(updated) }
        };
        var update = new MirFunc
        {
            Name = identity.Name,
            FunctionId = identity,
            ReturnType = recordType,
            Locals = [.. parameters, updated],
            EntryBlockId = updateBlock.Id,
            BasicBlocks = [updateBlock]
        };
        var shared = UniqueLocal(1, "shared", recordType, parameter: true);
        var callerLocals = new List<MirLocal> { shared };
        var callerInstructions = new List<MirInstruction>();
        var nextLocalId = 2;
        for (var mask = 1; mask <= 257; mask++)
        {
            var arguments = new List<MirOperand>(parameters.Count);
            for (var parameterIndex = 0; parameterIndex < parameters.Count; parameterIndex++)
            {
                if ((mask & (1 << parameterIndex)) == 0)
                {
                    arguments.Add(UniquePlace(shared));
                    continue;
                }

                var owned = UniqueLocal(nextLocalId++, $"owned_{mask}_{parameterIndex}", recordType);
                callerLocals.Add(owned);
                callerInstructions.Add(UniqueConstructor(owned, recordType));
                arguments.Add(UniquePlace(owned));
            }

            var result = UniqueLocal(nextLocalId++, $"result_{mask}", recordType);
            callerLocals.Add(result);
            callerInstructions.Add(new MirCall
            {
                Target = UniquePlace(result),
                Function = new MirFunctionRef
                {
                    Name = update.Name,
                    FunctionId = update.FunctionId,
                    TypeId = recordType
                },
                Arguments = arguments
            });
        }

        var callerBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions = callerInstructions,
            Terminator = new MirReturn { Value = UniquePlace(shared) }
        };
        var caller = UniqueCaller("variant_limit_caller", recordType, callerLocals, [callerBlock]);
        var module = new MirModule { Functions = [update, caller] };

        new UniqueRecordUpdateSpecializationPass().Run(module);

        Assert.Equal(
            256,
            module.Functions.Count(function => function.Name.Contains("__unique_", StringComparison.Ordinal)));
        Assert.Contains(
            callerBlock.Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    functionRef.FunctionId.StableIdentityKey == identity.StableIdentityKey);
    }

    private static UniqueUpdateFixture CreateUniqueUpdateFixture(
        int typeId,
        string suffix,
        bool recursive = false)
    {
        var recordType = new TypeId(typeId);
        var identity = new FunctionId
        {
            StableIdentityKey = $"test:unique:{suffix}",
            Name = $"update_{suffix}",
            QualifiedName = $"test.update_{suffix}"
        };
        var source = UniqueLocal(1, "source", recordType, parameter: true);
        var updated = UniqueLocal(2, "updated", recordType);
        var recursiveResult = UniqueLocal(3, "recursive_result", recordType);
        var instructions = new List<MirInstruction>
        {
            new MirCall
            {
                Target = UniquePlace(updated),
                Function = new MirFunctionRef
                {
                    Name = "Record",
                    SymbolKind = SymbolKind.Constructor,
                    TypeId = recordType
                },
                Arguments = [UniquePlace(source)],
                RecordUpdate = new MirRecordUpdateInfo
                {
                    Source = UniquePlace(source),
                    UpdatedFieldIndices = [0]
                }
            }
        };
        if (recursive)
        {
            instructions.Add(new MirCall
            {
                Target = UniquePlace(recursiveResult),
                Function = new MirFunctionRef
                {
                    Name = identity.Name,
                    FunctionId = identity,
                    TypeId = recordType
                },
                Arguments = [UniquePlace(updated)]
            });
        }

        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions = instructions,
            Terminator = new MirReturn { Value = UniquePlace(recursive ? recursiveResult : updated) }
        };
        var function = new MirFunc
        {
            Name = identity.Name,
            SourceName = identity.Name,
            FunctionId = identity,
            ReturnType = recordType,
            Locals = [source, updated, recursiveResult],
            EntryBlockId = block.Id,
            BasicBlocks = [block]
        };
        return new UniqueUpdateFixture(recordType, function);
    }

    private static void AssertUniqueCallWasSpecialized(
        MirModule module,
        MirFunc original,
        MirBasicBlock block,
        int instructionIndex)
    {
        var specialized = Assert.Single(
            module.Functions,
            function => function.Name.Contains("__unique_0", StringComparison.Ordinal));
        Assert.True(Assert.IsType<MirCall>(specialized.BasicBlocks[0].Instructions[0]).RecordUpdate!.IsKnownUnique);
        var rewritten = Assert.IsType<MirCall>(block.Instructions[instructionIndex]);
        Assert.Equal(
            specialized.FunctionId.StableIdentityKey,
            Assert.IsType<MirFunctionRef>(rewritten.Function).FunctionId.StableIdentityKey);
        Assert.False(Assert.IsType<MirCall>(original.BasicBlocks[0].Instructions[0]).RecordUpdate!.IsKnownUnique);
    }

    private static MirFunc UniqueCaller(
        string name,
        TypeId returnType,
        List<MirLocal> locals,
        List<MirBasicBlock> blocks) => new()
        {
            Name = name,
            ReturnType = returnType,
            Locals = locals,
            EntryBlockId = blocks[0].Id,
            BasicBlocks = blocks
        };

    private static MirCall UniqueConstructor(MirLocal target, TypeId typeId) => new()
    {
        Target = UniquePlace(target),
        Function = new MirFunctionRef
        {
            Name = "Record",
            SymbolKind = SymbolKind.Constructor,
            TypeId = typeId
        }
    };

    private static MirCall UniqueCall(MirLocal target, MirFunc function, MirLocal argument) => new()
    {
        Target = UniquePlace(target),
        Function = new MirFunctionRef
        {
            Name = function.Name,
            FunctionId = function.FunctionId,
            TypeId = function.ReturnType
        },
        Arguments = [UniquePlace(argument)]
    };

    private static MirLocal UniqueLocal(
        int id,
        string name,
        TypeId typeId,
        bool parameter = false) => new()
        {
            Id = new LocalId { Value = id },
            Name = name,
            TypeId = typeId,
            IsParameter = parameter
        };

    private static MirPlace UniquePlace(MirLocal local) => new()
    {
        Kind = PlaceKind.Local,
        Local = local.Id,
        TypeId = local.TypeId
    };

    private sealed record UniqueUpdateFixture(TypeId RecordType, MirFunc Function);
}
