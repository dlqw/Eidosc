using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public sealed class ReuseOptimizationTests
{
    [Fact]
    public void ReusePreparation_RecordUpdate_MaterializesBorrowedManagedFieldBeforeDrop()
    {
        var recordType = new TypeId(9000);
        var fieldType = new TypeId(BaseTypes.StringId);
        var oldValue = LocalPlace(1, recordType);
        var fieldValue = LocalPlace(2, fieldType);
        var newValue = LocalPlace(3, recordType);
        var load = new MirLoad
        {
            Target = fieldValue,
            Source = new MirPlace
            {
                Kind = PlaceKind.Field,
                Base = oldValue,
                FieldName = "text",
                TypeId = fieldType
            },
            CreatesBorrowAlias = true
        };
        var call = ConstructorCall(newValue, fieldValue);
        var drop = new MirDrop { Value = oldValue };
        var block = Block(load, call, drop);

        new ReusePreparationPass().Run(Module(block, recordType, fieldType, recordType));

        var materializedLoad = Assert.IsType<MirLoad>(block.Instructions[0]);
        Assert.False(materializedLoad.CreatesBorrowAlias);
        Assert.Same(drop, block.Instructions[1]);
        Assert.Same(call, block.Instructions[2]);
    }

    [Fact]
    public void ReusePreparation_RecordMovedInPredecessor_DropsBeforeBranchConstructor()
    {
        var recordType = new TypeId(9010);
        var fieldType = new TypeId(BaseTypes.StringId);
        var parameter = LocalPlace(1, recordType);
        var owner = LocalPlace(2, recordType);
        var fieldValue = LocalPlace(3, fieldType);
        var newValue = LocalPlace(4, recordType);
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions = [new MirMove { Target = owner, Source = parameter }],
            Terminator = new MirGoto { Target = new BlockId { Value = 2 } }
        };
        var load = new MirLoad
        {
            Target = fieldValue,
            Source = Field(owner, "text", fieldType),
            CreatesBorrowAlias = true
        };
        var call = ConstructorCall(newValue, fieldValue);
        var drop = new MirDrop { Value = owner };
        var update = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions = [load, call, drop],
            Terminator = new MirReturn { Value = newValue }
        };
        var function = new MirFunc
        {
            Name = "predecessor_record_update",
            EntryBlockId = entry.Id,
            ReturnType = recordType,
            Locals =
            [
                new MirLocal { Id = parameter.Local, Name = "parameter", TypeId = recordType, IsParameter = true },
                new MirLocal { Id = owner.Local, Name = "owner", TypeId = recordType },
                new MirLocal { Id = fieldValue.Local, Name = "field", TypeId = fieldType },
                new MirLocal { Id = newValue.Local, Name = "new_value", TypeId = recordType }
            ],
            BasicBlocks = [entry, update]
        };
        var module = new MirModule { Name = "predecessor_record_update_test", Functions = [function] };

        new ReusePreparationPass().Run(module);
        new DestructiveProjectionMovePass().Run(module);
        var reuseAnalyzer = new ReuseAnalyzer(function);
        reuseAnalyzer.Analyze();

        var destructiveLoad = Assert.IsType<MirLoad>(update.Instructions[0]);
        Assert.False(destructiveLoad.CreatesBorrowAlias);
        Assert.True(destructiveLoad.MovesOutOfSource);
        Assert.Same(drop, update.Instructions[1]);
        Assert.Same(call, update.Instructions[2]);
        Assert.Equal(
            reuseAnalyzer.Hints.DropReuseSites[(update.Id, 1)],
            reuseAnalyzer.Hints.AllocReuseSites[(update.Id, 2)]);
    }

    [Fact]
    public void ReusePreparation_OwnerNotDefinedOnEveryIncomingPath_DoesNotMoveDrop()
    {
        var recordType = new TypeId(9011);
        var conditionType = new TypeId(BaseTypes.BoolId);
        var parameter = LocalPlace(1, recordType);
        var owner = LocalPlace(2, recordType);
        var condition = LocalPlace(3, conditionType);
        var newValue = LocalPlace(4, recordType);
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Terminator = new MirSwitch
            {
                Discriminant = condition,
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant
                        {
                            TypeId = conditionType,
                            Value = new MirConstantValue.BoolValue(true)
                        },
                        Target = new BlockId { Value = 2 }
                    }
                ],
                DefaultTarget = new BlockId { Value = 3 }
            }
        };
        var definingPath = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions = [new MirMove { Target = owner, Source = parameter }],
            Terminator = new MirGoto { Target = new BlockId { Value = 4 } }
        };
        var missingPath = new MirBasicBlock
        {
            Id = new BlockId { Value = 3 },
            Terminator = new MirGoto { Target = new BlockId { Value = 4 } }
        };
        var call = ConstructorCall(newValue);
        var drop = new MirDrop { Value = owner };
        var join = new MirBasicBlock
        {
            Id = new BlockId { Value = 4 },
            Instructions = [call, drop],
            Terminator = new MirReturn { Value = newValue }
        };
        var function = new MirFunc
        {
            Name = "conditional_record_update",
            EntryBlockId = entry.Id,
            ReturnType = recordType,
            Locals =
            [
                new MirLocal { Id = parameter.Local, Name = "parameter", TypeId = recordType, IsParameter = true },
                new MirLocal { Id = owner.Local, Name = "owner", TypeId = recordType },
                new MirLocal { Id = condition.Local, Name = "condition", TypeId = conditionType, IsParameter = true },
                new MirLocal { Id = newValue.Local, Name = "new_value", TypeId = recordType }
            ],
            BasicBlocks = [entry, definingPath, missingPath, join]
        };

        new ReusePreparationPass().Run(new MirModule
        {
            Name = "conditional_record_update_test",
            Functions = [function]
        });

        Assert.Same(call, join.Instructions[0]);
        Assert.Same(drop, join.Instructions[1]);
    }

    [Fact]
    public void ReusePreparation_MutableBorrowedField_DoesNotMoveDropBeforeBorrow()
    {
        var recordType = new TypeId(9001);
        var fieldType = new TypeId(BaseTypes.StringId);
        var oldValue = LocalPlace(1, recordType);
        var fieldValue = LocalPlace(2, fieldType);
        var newValue = LocalPlace(3, recordType);
        var load = new MirLoad
        {
            Target = fieldValue,
            Source = new MirPlace
            {
                Kind = PlaceKind.Field,
                Base = oldValue,
                FieldName = "text",
                TypeId = fieldType
            },
            IsMutableBorrow = true,
            CreatesBorrowAlias = true
        };
        var call = ConstructorCall(newValue, fieldValue);
        var drop = new MirDrop { Value = oldValue };
        var block = Block(load, call, drop);

        new ReusePreparationPass().Run(Module(block, recordType, fieldType, recordType));

        Assert.True(Assert.IsType<MirLoad>(block.Instructions[0]).CreatesBorrowAlias);
        Assert.Same(call, block.Instructions[1]);
        Assert.Same(drop, block.Instructions[2]);
    }

    [Fact]
    public void ReusePreparation_DropDefinedAfterConstructor_RemainsAfterInitialization()
    {
        var recordType = new TypeId(9003);
        var constructorResult = LocalPlace(1, recordType);
        var owner = LocalPlace(2, recordType);
        var constructor = ConstructorCall(constructorResult);
        var move = new MirMove { Target = owner, Source = constructorResult };
        var drop = new MirDrop { Value = owner };
        var block = Block(constructor, move, drop);

        new ReusePreparationPass().Run(Module(block, recordType, recordType));

        Assert.Same(constructor, block.Instructions[0]);
        Assert.Same(move, block.Instructions[1]);
        Assert.Same(drop, block.Instructions[2]);
    }

    [Fact]
    public void RecordUpdate_OwnedProjectedField_DropsAndReusesOriginalRecord()
    {
        var recordType = new TypeId(9003);
        var fieldType = new TypeId(BaseTypes.StringId);
        var oldValue = LocalPlace(1, recordType);
        var fieldValue = LocalPlace(2, fieldType);
        var newValue = LocalPlace(3, recordType);
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirLoad
                {
                    Target = fieldValue,
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = oldValue,
                        FieldName = "text",
                        TypeId = fieldType
                    },
                    CreatesBorrowAlias = true
                },
                ConstructorCall(newValue, fieldValue)
            ],
            Terminator = new MirReturn { Value = newValue }
        };
        var function = new MirFunc
        {
            Name = "record_update",
            EntryBlockId = block.Id,
            ReturnType = recordType,
            Locals =
            [
                new MirLocal
                {
                    Id = oldValue.Local,
                    Name = "old_value",
                    TypeId = recordType,
                    IsParameter = true
                },
                new MirLocal { Id = fieldValue.Local, Name = "field", TypeId = fieldType },
                new MirLocal { Id = newValue.Local, Name = "new_value", TypeId = recordType }
            ],
            BasicBlocks = [block]
        };
        var module = new MirModule { Name = "record_update_test", Functions = [function] };

        module = new DropInsertionPass().Run(module);
        function = Assert.Single(module.Functions);
        block = Assert.Single(function.BasicBlocks);

        Assert.IsType<MirLoad>(block.Instructions[0]);
        Assert.IsType<MirCall>(block.Instructions[1]);
        var insertedDrop = Assert.IsType<MirDrop>(block.Instructions[2]);
        Assert.Equal(oldValue.Local, Assert.IsType<MirPlace>(insertedDrop.Value).Local);

        new ReusePreparationPass().Run(module);
        new DestructiveProjectionMovePass().Run(module);
        var reuseAnalyzer = new ReuseAnalyzer(function);
        reuseAnalyzer.Analyze();

        var destructiveLoad = Assert.IsType<MirLoad>(block.Instructions[0]);
        Assert.False(destructiveLoad.CreatesBorrowAlias);
        Assert.True(destructiveLoad.MovesOutOfSource);
        Assert.Same(insertedDrop, block.Instructions[1]);
        Assert.IsType<MirCall>(block.Instructions[2]);
        Assert.Equal(reuseAnalyzer.Hints.DropReuseSites[(block.Id, 1)],
            reuseAnalyzer.Hints.AllocReuseSites[(block.Id, 2)]);
    }

    [Fact]
    public void ReuseAnalyzer_OnlyPublishesDropSlotsPairedInTheSameBlock()
    {
        var recordType = new TypeId(9002);
        var oldValue = LocalPlace(1, recordType);
        var newValue = LocalPlace(2, recordType);
        var pairedBlock = Block(
            new MirDrop { Value = oldValue },
            ConstructorCall(newValue));
        var unpairedBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions = [new MirDrop { Value = oldValue }],
            Terminator = new MirReturn()
        };
        var function = Assert.Single(Module(pairedBlock, recordType, recordType).Functions);
        function.BasicBlocks.Add(unpairedBlock);
        var analyzer = new ReuseAnalyzer(function);

        analyzer.Analyze();

        Assert.Single(analyzer.Hints.DropReuseSites);
        Assert.Single(analyzer.Hints.AllocReuseSites);
        Assert.Equal(
            analyzer.Hints.DropReuseSites[(pairedBlock.Id, 0)],
            analyzer.Hints.AllocReuseSites[(pairedBlock.Id, 1)]);
        Assert.DoesNotContain((unpairedBlock.Id, 0), analyzer.Hints.DropReuseSites.Keys);
    }

    [Fact]
    public void DestructiveProjectionMove_OwnedLoadBeforeOwnerDrop_MovesOutOfSlot()
    {
        var tupleType = new TypeId(9100);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, tupleType);
        var alias = LocalPlace(2, tupleType);
        var field = LocalPlace(3, fieldType);
        var load = new MirLoad
        {
            Target = field,
            Source = new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = alias,
                Index = new MirConstant
                {
                    Value = new MirConstantValue.IntValue(0),
                    TypeId = new TypeId(BaseTypes.IntId)
                },
                IndexAccessKind = MirIndexAccessKind.Aggregate,
                TypeId = fieldType
            },
            CreatesBorrowAlias = false
        };
        var block = Block(
            new MirLoad { Target = alias, Source = owner },
            load,
            new MirDrop { Value = owner });
        var module = Module(block, tupleType, tupleType, fieldType);

        new DestructiveProjectionMovePass().Run(module);

        var destructiveLoad = Assert.IsType<MirLoad>(block.Instructions[1]);
        Assert.True(destructiveLoad.MovesOutOfSource);
        var destructiveProjection = Assert.IsType<MirPlace>(destructiveLoad.Source);
        Assert.Equal(owner.Local, Assert.IsType<MirPlace>(destructiveProjection.Base).Local);
    }

    [Fact]
    public void DestructiveProjectionMove_MoveTargetOwnsIndependentStorage()
    {
        var tupleType = new TypeId(9101);
        var fieldType = new TypeId(BaseTypes.StringId);
        var source = LocalPlace(1, tupleType);
        var owner = LocalPlace(2, tupleType);
        var field = LocalPlace(3, fieldType);
        var block = Block(
            new MirMove { Target = owner, Source = source },
            OwnedProjectionLoad(field, Field(owner, "text", fieldType)),
            new MirDrop { Value = owner });

        new DestructiveProjectionMovePass().Run(Module(block, tupleType, tupleType, fieldType));

        var destructiveLoad = Assert.IsType<MirLoad>(block.Instructions[1]);
        Assert.True(destructiveLoad.MovesOutOfSource);
        var destructiveProjection = Assert.IsType<MirPlace>(destructiveLoad.Source);
        Assert.Equal(owner.Local, Assert.IsType<MirPlace>(destructiveProjection.Base).Local);
    }

    [Fact]
    public void DestructiveProjectionMove_OverwriteOfSameField_RemainsNonDestructive()
    {
        var recordType = new TypeId(9102);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, recordType);
        var loaded = LocalPlace(2, fieldType);
        var replacement = LocalPlace(3, fieldType);
        var load = OwnedProjectionLoad(loaded, Field(owner, "text", fieldType));
        var block = Block(
            load,
            new MirStore { Target = Field(owner, "text", fieldType), Value = replacement },
            new MirDrop { Value = owner });

        new DestructiveProjectionMovePass().Run(Module(block, recordType, fieldType, fieldType));

        Assert.False(Assert.IsType<MirLoad>(block.Instructions[0]).MovesOutOfSource);
    }

    [Fact]
    public void DestructiveProjectionMove_SecondReadOfSameField_MovesOnlyLastRead()
    {
        var recordType = new TypeId(9103);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, recordType);
        var first = OwnedProjectionLoad(LocalPlace(2, fieldType), Field(owner, "text", fieldType));
        var second = OwnedProjectionLoad(LocalPlace(3, fieldType), Field(owner, "text", fieldType));
        var block = Block(first, second, new MirDrop { Value = owner });

        new DestructiveProjectionMovePass().Run(Module(block, recordType, fieldType, fieldType));

        Assert.False(Assert.IsType<MirLoad>(block.Instructions[0]).MovesOutOfSource);
        Assert.True(Assert.IsType<MirLoad>(block.Instructions[1]).MovesOutOfSource);
    }

    [Fact]
    public void DestructiveProjectionMove_NestedProjection_MovesOutOfNestedSlot()
    {
        var outerType = new TypeId(9104);
        var innerType = new TypeId(9105);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, outerType);
        var nested = Field(Field(owner, "inner", innerType), "text", fieldType);
        var load = OwnedProjectionLoad(LocalPlace(2, fieldType), nested);
        var block = Block(load, new MirDrop { Value = owner });

        new DestructiveProjectionMovePass().Run(Module(block, outerType, fieldType));

        Assert.True(Assert.IsType<MirLoad>(block.Instructions[0]).MovesOutOfSource);
    }

    [Fact]
    public void DestructiveProjectionMove_AncestorReadBeforeDescendantRead_RemainsNonDestructive()
    {
        var outerType = new TypeId(9110);
        var innerType = new TypeId(9111);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, outerType);
        var inner = Field(owner, "inner", innerType);
        var ancestorLoad = OwnedProjectionLoad(LocalPlace(2, innerType), inner);
        var descendantLoad = OwnedProjectionLoad(
            LocalPlace(3, fieldType),
            Field(inner, "text", fieldType));
        var block = Block(ancestorLoad, descendantLoad, new MirDrop { Value = owner });

        new DestructiveProjectionMovePass().Run(Module(block, outerType, innerType, fieldType));

        Assert.False(Assert.IsType<MirLoad>(block.Instructions[0]).MovesOutOfSource);
        Assert.True(Assert.IsType<MirLoad>(block.Instructions[1]).MovesOutOfSource);
    }

    [Fact]
    public void DestructiveProjectionMove_DirectOwnerUseAfterProjection_RemainsNonDestructive()
    {
        var recordType = new TypeId(9112);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, recordType);
        var loaded = LocalPlace(2, fieldType);
        var alias = LocalPlace(3, recordType);
        var load = OwnedProjectionLoad(loaded, Field(owner, "text", fieldType));
        var block = Block(
            load,
            new MirCopy { Target = alias, Source = owner },
            new MirDrop { Value = owner });

        new DestructiveProjectionMovePass().Run(Module(block, recordType, fieldType, recordType));

        Assert.False(Assert.IsType<MirLoad>(block.Instructions[0]).MovesOutOfSource);
    }

    [Fact]
    public void DestructiveProjectionMove_OwnerReinitializedBeforeDrop_RemainsNonDestructive()
    {
        var recordType = new TypeId(9106);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, recordType);
        var replacement = LocalPlace(2, recordType);
        var loaded = LocalPlace(3, fieldType);
        var load = OwnedProjectionLoad(loaded, Field(owner, "text", fieldType));
        var block = Block(
            load,
            new MirAssign { Target = owner, Source = replacement },
            new MirDrop { Value = owner });

        new DestructiveProjectionMovePass().Run(Module(block, recordType, recordType, fieldType));

        Assert.False(Assert.IsType<MirLoad>(block.Instructions[0]).MovesOutOfSource);
    }

    [Fact]
    public void DestructiveProjectionMove_DynamicAggregateIndex_RemainsNonDestructive()
    {
        var tupleType = new TypeId(9107);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, tupleType);
        var index = LocalPlace(2, new TypeId(BaseTypes.IntId));
        var loaded = LocalPlace(3, fieldType);
        var load = OwnedProjectionLoad(
            loaded,
            new MirPlace
            {
                Kind = PlaceKind.Index,
                Base = owner,
                Index = index,
                IndexAccessKind = MirIndexAccessKind.Aggregate,
                TypeId = fieldType
            });
        var block = Block(load, new MirDrop { Value = owner });

        new DestructiveProjectionMovePass().Run(Module(block, tupleType, index.TypeId, fieldType));

        Assert.False(Assert.IsType<MirLoad>(block.Instructions[0]).MovesOutOfSource);
    }

    [Fact]
    public void DestructiveProjectionMove_DropOnEveryBranchExit_MovesOutOfSlot()
    {
        var recordType = new TypeId(9108);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, recordType);
        var loaded = LocalPlace(2, fieldType);
        var condition = LocalPlace(3, new TypeId(BaseTypes.BoolId));
        var load = OwnedProjectionLoad(loaded, Field(owner, "text", fieldType));
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions = [load],
            Terminator = new MirSwitch
            {
                Discriminant = condition,
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant
                        {
                            TypeId = condition.TypeId,
                            Value = new MirConstantValue.BoolValue(true)
                        },
                        Target = new BlockId { Value = 2 }
                    }
                ],
                DefaultTarget = new BlockId { Value = 3 }
            }
        };
        var module = Module(entry, recordType, fieldType, condition.TypeId);
        var function = Assert.Single(module.Functions);
        function.BasicBlocks.Add(new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions = [new MirDrop { Value = owner }],
            Terminator = new MirReturn()
        });
        function.BasicBlocks.Add(new MirBasicBlock
        {
            Id = new BlockId { Value = 3 },
            Instructions = [new MirDrop { Value = owner }],
            Terminator = new MirReturn()
        });

        new DestructiveProjectionMovePass().Run(module);

        Assert.True(Assert.IsType<MirLoad>(entry.Instructions[0]).MovesOutOfSource);
    }

    [Fact]
    public void DestructiveProjectionMove_BranchWithoutOwnerDrop_RemainsNonDestructive()
    {
        var recordType = new TypeId(9113);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, recordType);
        var loaded = LocalPlace(2, fieldType);
        var condition = LocalPlace(3, new TypeId(BaseTypes.BoolId));
        var load = OwnedProjectionLoad(loaded, Field(owner, "text", fieldType));
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions = [load],
            Terminator = new MirSwitch
            {
                Discriminant = condition,
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant
                        {
                            TypeId = condition.TypeId,
                            Value = new MirConstantValue.BoolValue(true)
                        },
                        Target = new BlockId { Value = 2 }
                    }
                ],
                DefaultTarget = new BlockId { Value = 3 }
            }
        };
        var module = Module(entry, recordType, fieldType, condition.TypeId);
        var function = Assert.Single(module.Functions);
        function.BasicBlocks.Add(new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions = [new MirDrop { Value = owner }],
            Terminator = new MirReturn()
        });
        function.BasicBlocks.Add(new MirBasicBlock
        {
            Id = new BlockId { Value = 3 },
            Terminator = new MirReturn()
        });

        new DestructiveProjectionMovePass().Run(module);

        Assert.False(Assert.IsType<MirLoad>(entry.Instructions[0]).MovesOutOfSource);
    }

    [Fact]
    public void DestructiveProjectionMove_DropAfterLoopExit_RemainsConservative()
    {
        var recordType = new TypeId(9109);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, recordType);
        var loaded = LocalPlace(2, fieldType);
        var condition = LocalPlace(3, new TypeId(BaseTypes.BoolId));
        var load = OwnedProjectionLoad(loaded, Field(owner, "text", fieldType));
        var loop = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions = [load],
            Terminator = new MirSwitch
            {
                Discriminant = condition,
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant
                        {
                            TypeId = condition.TypeId,
                            Value = new MirConstantValue.BoolValue(true)
                        },
                        Target = new BlockId { Value = 1 }
                    }
                ],
                DefaultTarget = new BlockId { Value = 2 }
            }
        };
        var module = Module(loop, recordType, fieldType, condition.TypeId);
        Assert.Single(module.Functions).BasicBlocks.Add(new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions = [new MirDrop { Value = owner }],
            Terminator = new MirReturn()
        });

        new DestructiveProjectionMovePass().Run(module);

        Assert.False(Assert.IsType<MirLoad>(loop.Instructions[0]).MovesOutOfSource);
    }

    [Fact]
    public void DestructiveProjectionMove_BorrowedLoad_RemainsNonDestructive()
    {
        var recordType = new TypeId(9101);
        var fieldType = new TypeId(BaseTypes.StringId);
        var owner = LocalPlace(1, recordType);
        var field = LocalPlace(2, fieldType);
        var load = new MirLoad
        {
            Target = field,
            Source = new MirPlace
            {
                Kind = PlaceKind.Field,
                Base = owner,
                FieldName = "text",
                TypeId = fieldType
            },
            CreatesBorrowAlias = true
        };
        var block = Block(load, new MirDrop { Value = owner });

        new DestructiveProjectionMovePass().Run(Module(block, recordType, fieldType));

        Assert.False(load.MovesOutOfSource);
    }

    private static MirModule Module(MirBasicBlock block, params TypeId[] types) => new()
    {
        Name = "reuse_test",
        Functions =
        [
            new MirFunc
            {
                Name = "update",
                EntryBlockId = block.Id,
                Locals = types
                    .SelectMany((type, index) => new[]
                    {
                        new MirLocal
                        {
                            Id = new LocalId { Value = index + 1 },
                            Name = $"value{index + 1}",
                            TypeId = type,
                            IsParameter = index == 0
                        }
                    })
                    .ToList(),
                BasicBlocks = [block]
            }
        ]
    };

    private static MirBasicBlock Block(params MirInstruction[] instructions) => new()
    {
        Id = new BlockId { Value = 1 },
        IsEntry = true,
        Instructions = [.. instructions],
        Terminator = new MirReturn()
    };

    private static MirCall ConstructorCall(MirPlace target, params MirOperand[] arguments) => new()
    {
        Target = target,
        Function = new MirFunctionRef
        {
            Name = "Record",
            SymbolId = new SymbolId(77),
            SymbolKind = SymbolKind.Constructor,
            TypeId = target.TypeId
        },
        Arguments = [.. arguments]
    };

    private static MirLoad OwnedProjectionLoad(MirPlace target, MirPlace source) => new()
    {
        Target = target,
        Source = source,
        CreatesBorrowAlias = false
    };

    private static MirPlace Field(MirPlace owner, string name, TypeId typeId) => new()
    {
        Kind = PlaceKind.Field,
        Base = owner,
        FieldName = name,
        TypeId = typeId
    };

    private static MirPlace LocalPlace(int id, TypeId typeId) => new()
    {
        Kind = PlaceKind.Local,
        Local = new LocalId { Value = id },
        TypeId = typeId
    };
}
