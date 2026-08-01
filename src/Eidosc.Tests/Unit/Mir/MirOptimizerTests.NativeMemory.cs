using Eidosc;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirOptimizerTests
{
    [Fact]
    public void RuntimeArrayFusion_SingletonAppendLast_BecomesConsumingPrepend()
    {
        var listType = new TypeId(6980);
        var intType = new TypeId(BaseTypes.IntId);
        var singleton = new MirLocal { Id = new LocalId { Value = 1 }, Name = "singleton", TypeId = listType };
        var left = new MirLocal { Id = new LocalId { Value = 2 }, Name = "left", TypeId = listType };
        var right = new MirLocal { Id = new LocalId { Value = 3 }, Name = "right", TypeId = listType, IsParameter = true };
        var result = new MirLocal { Id = new LocalId { Value = 4 }, Name = "result", TypeId = listType };
        var element = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(17) };
        var elementSize = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(8) };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = LocalPlace(singleton.Id, listType),
                    Function = MirRuntimeFunctions.CreateFunctionRef(
                        WellKnownStrings.InternalNames.ArrayNew,
                        listType,
                        SourceSpan.Empty),
                    Arguments =
                    [
                        new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(1) },
                        elementSize
                    ]
                },
                new MirStore
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = LocalPlace(singleton.Id, listType),
                        Index = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(0) },
                        TypeId = intType
                    },
                    Value = element
                },
                new MirMove
                {
                    Target = LocalPlace(left.Id, listType),
                    Source = LocalPlace(singleton.Id, listType)
                },
                new MirCall
                {
                    Target = LocalPlace(result.Id, listType),
                    Function = new MirFunctionRef
                    {
                        Name = "append_last",
                        TypeId = listType,
                        CompilerSemanticRole = CompilerSemanticRole.AppendLastAppend
                    },
                    Arguments = [LocalPlace(left.Id, listType), LocalPlace(right.Id, listType)]
                }
            ],
            Terminator = new MirReturn { Value = LocalPlace(result.Id, listType) }
        };
        var module = CreateDropInsertionModule([singleton, left, right, result], [block]);

        new RuntimeArrayFusionPass().Run(module);

        var fused = Assert.IsType<MirCall>(Assert.Single(block.Instructions));
        var functionRef = Assert.IsType<MirFunctionRef>(fused.Function);
        Assert.True(MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayPrepend));
        Assert.Equal(result.Id, fused.Target!.Local);
        Assert.Equal(right.Id, Assert.IsType<MirPlace>(fused.Arguments[0]).Local);
        Assert.Same(element, fused.Arguments[1]);
        Assert.Same(elementSize, fused.Arguments[2]);
    }

    [Fact]
    public void RuntimeArrayFusion_SingletonWithAdditionalUse_RemainsUnfused()
    {
        var listType = new TypeId(6981);
        var intType = new TypeId(BaseTypes.IntId);
        var singleton = new MirLocal { Id = new LocalId { Value = 1 }, Name = "singleton", TypeId = listType };
        var left = new MirLocal { Id = new LocalId { Value = 2 }, Name = "left", TypeId = listType };
        var right = new MirLocal { Id = new LocalId { Value = 3 }, Name = "right", TypeId = listType, IsParameter = true };
        var result = new MirLocal { Id = new LocalId { Value = 4 }, Name = "result", TypeId = listType };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = LocalPlace(singleton.Id, listType),
                    Function = MirRuntimeFunctions.CreateFunctionRef(
                        WellKnownStrings.InternalNames.ArrayNew,
                        listType,
                        SourceSpan.Empty),
                    Arguments =
                    [
                        new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(1) },
                        new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(8) }
                    ]
                },
                new MirStore
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Index,
                        Base = LocalPlace(singleton.Id, listType),
                        Index = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(0) },
                        TypeId = intType
                    },
                    Value = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(17) }
                },
                new MirCall
                {
                    Function = new MirFunctionRef { Name = "observe" },
                    Arguments = [LocalPlace(singleton.Id, listType)],
                    BorrowedArgumentIndices = new HashSet<int> { 0 }
                },
                new MirMove
                {
                    Target = LocalPlace(left.Id, listType),
                    Source = LocalPlace(singleton.Id, listType)
                },
                new MirCall
                {
                    Target = LocalPlace(result.Id, listType),
                    Function = new MirFunctionRef
                    {
                        Name = "append_last",
                        TypeId = listType,
                        CompilerSemanticRole = CompilerSemanticRole.AppendLastAppend
                    },
                    Arguments = [LocalPlace(left.Id, listType), LocalPlace(right.Id, listType)]
                }
            ],
            Terminator = new MirReturn { Value = LocalPlace(result.Id, listType) }
        };
        var module = CreateDropInsertionModule([singleton, left, right, result], [block]);

        new RuntimeArrayFusionPass().Run(module);

        Assert.Equal(5, block.Instructions.Count);
        var append = Assert.IsType<MirCall>(block.Instructions[^1]);
        Assert.Equal(CompilerSemanticRole.AppendLastAppend, Assert.IsType<MirFunctionRef>(append.Function).CompilerSemanticRole);
    }

    [Fact]
    public void RecordUpdateFusion_PreservesUnchangedFieldsAndKeepsOnlyReplacements()
    {
        var recordType = new TypeId(6990);
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var source = new MirLocal { Id = new LocalId { Value = 1 }, Name = "source", TypeId = recordType, IsParameter = true };
        var text = new MirLocal { Id = new LocalId { Value = 2 }, Name = "text", TypeId = stringType };
        var result = new MirLocal { Id = new LocalId { Value = 4 }, Name = "result", TypeId = recordType };
        var replacement = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(42) };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirLoad
                {
                    Target = LocalPlace(text.Id, stringType),
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = LocalPlace(source.Id, recordType),
                        FieldName = "_0",
                        TypeId = stringType
                    },
                    MovesOutOfSource = true,
                    CreatesBorrowAlias = false
                },
                new MirDrop { Value = LocalPlace(source.Id, recordType) },
                new MirCall
                {
                    Target = LocalPlace(result.Id, recordType),
                    Function = new MirFunctionRef
                    {
                        Name = "Record",
                        SymbolKind = Eidosc.Symbols.SymbolKind.Constructor,
                        TypeId = recordType
                    },
                    Arguments = [LocalPlace(text.Id, stringType), replacement]
                }
            ],
            Terminator = new MirReturn { Value = LocalPlace(result.Id, recordType) }
        };
        var module = CreateDropInsertionModule(
            [source, text, result],
            [block],
            new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [recordType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Record",
                        ConstructorName = "Record",
                        FieldTypeIds = [stringType, intType]
                    }
                ]
            });

        new RecordUpdateFusionPass().Run(module);

        var fused = Assert.IsType<MirCall>(Assert.Single(block.Instructions));
        Assert.NotNull(fused.RecordUpdate);
        Assert.Equal(source.Id, fused.RecordUpdate.Source.Local);
        Assert.Equal([1], fused.RecordUpdate.UpdatedFieldIndices);
        Assert.Equal(2, fused.Arguments.Count);
        Assert.Equal(source.Id, Assert.IsType<MirPlace>(fused.Arguments[0]).Local);
        Assert.Same(replacement, fused.Arguments[1]);
    }

    [Fact]
    public void RecordUpdateFusion_PreservedFieldWithAnotherUse_RemainsConstructorRebuild()
    {
        var recordType = new TypeId(6991);
        var stringType = new TypeId(BaseTypes.StringId);
        var source = new MirLocal { Id = new LocalId { Value = 1 }, Name = "source", TypeId = recordType, IsParameter = true };
        var field = new MirLocal { Id = new LocalId { Value = 2 }, Name = "field", TypeId = stringType };
        var result = new MirLocal { Id = new LocalId { Value = 3 }, Name = "result", TypeId = recordType };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirLoad
                {
                    Target = LocalPlace(field.Id, stringType),
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = LocalPlace(source.Id, recordType),
                        FieldName = "_0",
                        TypeId = stringType
                    }
                },
                new MirCall
                {
                    Function = new MirFunctionRef { Name = "observe" },
                    Arguments = [LocalPlace(field.Id, stringType)],
                    BorrowedArgumentIndices = new HashSet<int> { 0 }
                },
                new MirDrop { Value = LocalPlace(source.Id, recordType) },
                new MirCall
                {
                    Target = LocalPlace(result.Id, recordType),
                    Function = new MirFunctionRef
                    {
                        Name = "Record",
                        SymbolKind = Eidosc.Symbols.SymbolKind.Constructor,
                        TypeId = recordType
                    },
                    Arguments = [LocalPlace(field.Id, stringType)]
                }
            ],
            Terminator = new MirReturn { Value = LocalPlace(result.Id, recordType) }
        };
        var module = CreateDropInsertionModule(
            [source, field, result],
            [block],
            new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [recordType.Value] =
                [
                    new ConstructorTypeLayout
                    {
                        TypeName = "Record",
                        ConstructorName = "Record",
                        FieldTypeIds = [stringType]
                    }
                ]
            });

        new RecordUpdateFusionPass().Run(module);

        Assert.Null(Assert.IsType<MirCall>(block.Instructions[^1]).RecordUpdate);
        Assert.Contains(block.Instructions, instruction => instruction is MirDrop);
    }

    [Fact]
    public void PayloadlessAdtAnalysis_PayloadConstructorInjectedIntoAncestor_VetoesScalarTag()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var someType = new TypeId(7007);
        var optionType = new TypeId(7008);
        var constructed = new MirLocal { Id = new LocalId { Value = 1 }, Name = "some", TypeId = someType };
        var alias = new MirLocal { Id = new LocalId { Value = 2 }, Name = "alias", TypeId = someType };
        var injected = new MirLocal { Id = new LocalId { Value = 3 }, Name = "option", TypeId = optionType };
        var module = CreateDropInsertionModule(
            [constructed, alias, injected],
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(constructed.Id, someType),
                            Function = new MirFunctionRef
                            {
                                Name = "Some",
                                SymbolKind = Eidosc.Symbols.SymbolKind.Constructor,
                                TypeId = someType
                            },
                            Arguments =
                            [
                                new MirConstant
                                {
                                    TypeId = intType,
                                    Value = new MirConstantValue.IntValue(41)
                                }
                            ]
                        },
                        new MirLoad
                        {
                            Target = LocalPlace(alias.Id, someType),
                            Source = LocalPlace(constructed.Id, someType)
                        },
                        new MirCaseInject
                        {
                            Target = LocalPlace(injected.Id, optionType),
                            Operand = LocalPlace(alias.Id, someType),
                            SourceTypeId = someType,
                            TargetTypeId = optionType
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(injected.Id, optionType) }
                }
            ],
            new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [optionType.Value] =
                [
                    new ConstructorTypeLayout { TypeName = "Option", ConstructorName = "Some", FieldTypeIds = [] },
                    new ConstructorTypeLayout { TypeName = "Option", ConstructorName = "None", FieldTypeIds = [] }
                ]
            });

        var scalarTypes = PayloadlessAdtRepresentationAnalysis.Analyze(module);

        Assert.DoesNotContain(optionType.Value, scalarTypes);
    }

    [Fact]
    public void PayloadlessAdtAnalysis_PayloadlessCaseInjectedIntoAncestor_PromotesScalarTag()
    {
        var eastType = new TypeId(7009);
        var directionType = new TypeId(7010);
        var east = new MirLocal { Id = new LocalId { Value = 1 }, Name = "east", TypeId = eastType };
        var direction = new MirLocal { Id = new LocalId { Value = 2 }, Name = "direction", TypeId = directionType };
        var module = CreateDropInsertionModule(
            [east, direction],
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(east.Id, eastType),
                            Function = new MirFunctionRef
                            {
                                Name = "East",
                                SymbolKind = Eidosc.Symbols.SymbolKind.Constructor,
                                TypeId = eastType
                            }
                        },
                        new MirCaseInject
                        {
                            Target = LocalPlace(direction.Id, directionType),
                            Operand = LocalPlace(east.Id, eastType),
                            SourceTypeId = eastType,
                            TargetTypeId = directionType
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(direction.Id, directionType) }
                }
            ],
            new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [eastType.Value] =
                [
                    new ConstructorTypeLayout { TypeName = "Direction.East", ConstructorName = "East", FieldTypeIds = [] }
                ]
            });

        var scalarTypes = PayloadlessAdtRepresentationAnalysis.Analyze(module);

        Assert.Contains(eastType.Value, scalarTypes);
        Assert.Contains(directionType.Value, scalarTypes);
    }

    [Fact]
    public void DropInsertion_BodylessIntrinsicAndFfiDeclarations_PassThroughUnchanged()
    {
        var intrinsic = new MirFunc
        {
            Name = "intrinsic_decl",
            IntrinsicName = "intrinsic_decl",
            ReturnType = new TypeId(BaseTypes.IntId)
        };
        var ffi = new MirFunc
        {
            Name = "ffi_decl",
            IsExternal = true,
            ExternalSymbolName = "ffi_decl",
            ExternalLibrary = "fixture",
            ReturnType = new TypeId(BaseTypes.IntId)
        };
        var module = new MirModule { Name = "bodyless", Functions = [intrinsic, ffi] };

        var optimized = new DropInsertionPass().Run(module);

        Assert.Same(intrinsic, optimized.Functions[0]);
        Assert.Same(ffi, optimized.Functions[1]);
    }

    [Fact]
    public void Liveness_LoopLocalCopyAndMoveSourcesDefinedInBlock_AreNotLiveAcrossBackEdge()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var copySource = new MirLocal { Id = new LocalId { Value = 1 }, Name = "copy_source", TypeId = stringType };
        var copyTarget = new MirLocal { Id = new LocalId { Value = 2 }, Name = "copy_target", TypeId = stringType };
        var moveSource = new MirLocal { Id = new LocalId { Value = 3 }, Name = "move_source", TypeId = stringType };
        var moveTarget = new MirLocal { Id = new LocalId { Value = 4 }, Name = "move_target", TypeId = stringType };
        var loopId = new BlockId { Value = 1 };
        var block = new MirBasicBlock
        {
            Id = loopId,
            IsEntry = true,
            Instructions =
            [
                new MirAlloc { Target = LocalPlace(copySource.Id, stringType) },
                new MirCopy
                {
                    Target = LocalPlace(copyTarget.Id, stringType),
                    Source = LocalPlace(copySource.Id, stringType)
                },
                new MirDrop { Value = LocalPlace(copyTarget.Id, stringType) },
                new MirAlloc { Target = LocalPlace(moveSource.Id, stringType) },
                new MirMove
                {
                    Target = LocalPlace(moveTarget.Id, stringType),
                    Source = LocalPlace(moveSource.Id, stringType)
                },
                new MirDrop { Value = LocalPlace(moveTarget.Id, stringType) }
            ],
            Terminator = new MirGoto { Target = loopId }
        };
        var function = Assert.Single(CreateDropInsertionModule(
            [copySource, copyTarget, moveSource, moveTarget],
            [block]).Functions);
        var usage = new VariableUsageAnalyzer(function);
        usage.Analyze();
        var liveness = new LivenessAnalyzer(function, usage);

        liveness.Analyze();

        Assert.True(liveness.TryGetLiveOutSet(loopId, out var liveOut));
        Assert.DoesNotContain(copySource.Id, liveOut);
        Assert.DoesNotContain(moveSource.Id, liveOut);
    }

    [Fact]
    public void DropInsertion_BorrowedManagedProjection_KeepsBaseAliveUntilAliasLastUse()
    {
        var recordType = new TypeId(7010);
        var stringType = new TypeId(BaseTypes.StringId);
        var record = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "record",
            TypeId = recordType,
            IsParameter = true
        };
        var alias = new MirLocal
        {
            Id = new LocalId { Value = 2 },
            Name = "alias",
            TypeId = stringType
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirLoad
                {
                    Target = LocalPlace(alias.Id, stringType),
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = LocalPlace(record.Id, recordType),
                        FieldName = "text",
                        TypeId = stringType
                    },
                    CreatesBorrowAlias = true
                },
                new MirCall
                {
                    Function = new MirFunctionRef { Name = "observe" },
                    Arguments = [LocalPlace(alias.Id, stringType)],
                    BorrowedArgumentIndices = new HashSet<int> { 0 }
                }
            ],
            Terminator = new MirReturn()
        };

        var optimized = new DropInsertionPass().Run(
            CreateDropInsertionModule([record, alias], [block]));
        var instructions = Assert.Single(optimized.Functions).BasicBlocks.Single().Instructions;

        Assert.IsType<MirLoad>(instructions[0]);
        Assert.IsType<MirCall>(instructions[1]);
        var recordDrop = Assert.IsType<MirDrop>(instructions[2]);
        Assert.Equal(record.Id, Assert.IsType<MirPlace>(recordDrop.Value).Local);
    }

    [Fact]
    public void DropInsertion_LocalLoadAlias_DoesNotDropAliasOrOwnerBeforeLaterBaseUse()
    {
        var recordType = new TypeId(7011);
        var intType = new TypeId(BaseTypes.IntId);
        var owner = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "owner",
            TypeId = recordType,
            IsParameter = true
        };
        var alias = new MirLocal
        {
            Id = new LocalId { Value = 2 },
            Name = "alias",
            TypeId = recordType
        };
        var field = new MirLocal
        {
            Id = new LocalId { Value = 3 },
            Name = "field",
            TypeId = intType
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirLoad
                {
                    Target = LocalPlace(alias.Id, recordType),
                    Source = LocalPlace(owner.Id, recordType)
                },
                new MirCall
                {
                    Function = new MirFunctionRef { Name = "type_id" },
                    Arguments = [LocalPlace(alias.Id, recordType)],
                    BorrowedArgumentIndices = new HashSet<int> { 0 }
                },
                new MirLoad
                {
                    Target = LocalPlace(field.Id, intType),
                    Source = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = LocalPlace(owner.Id, recordType),
                        FieldName = "value",
                        TypeId = intType
                    }
                }
            ],
            Terminator = new MirReturn()
        };

        var optimized = new DropInsertionPass().Run(
            CreateDropInsertionModule([owner, alias, field], [block]));
        var instructions = Assert.Single(optimized.Functions).BasicBlocks.Single().Instructions;

        Assert.DoesNotContain(instructions, instruction =>
            instruction is MirDrop { Value: MirPlace { Local: var local } } && local == alias.Id);
        var ownerDropIndex = instructions.FindIndex(instruction =>
            instruction is MirDrop { Value: MirPlace { Local: var local } } && local == owner.Id);
        var fieldLoadIndex = instructions.FindIndex(instruction =>
            instruction is MirLoad { Target.Local: var local } && local == field.Id);
        Assert.True(ownerDropIndex > fieldLoadIndex);
    }

    [Fact]
    public void DropInsertion_LocalLoadWithDeadSource_DropsOnlyTheOwner()
    {
        var recordType = new TypeId(7012);
        var owner = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "owner",
            TypeId = recordType,
            IsParameter = true
        };
        var target = new MirLocal
        {
            Id = new LocalId { Value = 2 },
            Name = "target",
            TypeId = recordType
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirLoad
                {
                    Target = LocalPlace(target.Id, recordType),
                    Source = LocalPlace(owner.Id, recordType)
                },
                new MirCall
                {
                    Function = new MirFunctionRef { Name = "observe" },
                    Arguments = [LocalPlace(target.Id, recordType)],
                    BorrowedArgumentIndices = new HashSet<int> { 0 }
                }
            ],
            Terminator = new MirReturn()
        };

        var optimized = new DropInsertionPass().Run(
            CreateDropInsertionModule([owner, target], [block]));
        var instructions = Assert.Single(optimized.Functions).BasicBlocks.Single().Instructions;

        var load = Assert.IsType<MirLoad>(instructions[0]);
        Assert.True(load.CreatesBorrowAlias);
        Assert.DoesNotContain(instructions, instruction =>
            instruction is MirDrop { Value: MirPlace { Local: var local } } && local == target.Id);
        Assert.Contains(instructions, instruction =>
            instruction is MirDrop { Value: MirPlace { Local: var local } } && local == owner.Id);
    }

    [Fact]
    public void DropInsertion_ReturningCaseInjectedLocalAlias_TransfersUnderlyingOwner()
    {
        var concreteType = new TypeId(7013);
        var ancestorType = new TypeId(7014);
        var owner = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "owner",
            TypeId = concreteType
        };
        var alias = new MirLocal
        {
            Id = new LocalId { Value = 2 },
            Name = "alias",
            TypeId = concreteType
        };
        var injected = new MirLocal
        {
            Id = new LocalId { Value = 3 },
            Name = "injected",
            TypeId = ancestorType
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirAlloc { Target = LocalPlace(owner.Id, concreteType) },
                new MirLoad
                {
                    Target = LocalPlace(alias.Id, concreteType),
                    Source = LocalPlace(owner.Id, concreteType)
                },
                new MirCaseInject
                {
                    Target = LocalPlace(injected.Id, ancestorType),
                    Operand = LocalPlace(alias.Id, concreteType),
                    SourceTypeId = concreteType,
                    TargetTypeId = ancestorType
                }
            ],
            Terminator = new MirReturn { Value = LocalPlace(injected.Id, ancestorType) }
        };

        var optimized = new DropInsertionPass().Run(
            CreateDropInsertionModule([owner, alias, injected], [block]));
        var instructions = Assert.Single(optimized.Functions).BasicBlocks.Single().Instructions;

        Assert.DoesNotContain(instructions, instruction => instruction is MirDrop);
    }

    [Fact]
    public void CopyDropElision_AdjacentSourceDrop_BecomesMove()
    {
        var typeId = new TypeId(BaseTypes.StringId);
        var source = new LocalId { Value = 1 };
        var target = new LocalId { Value = 2 };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            Instructions =
            [
                new MirCopy
                {
                    Target = LocalPlace(target, typeId),
                    Source = LocalPlace(source, typeId)
                },
                new MirDrop { Value = LocalPlace(source, typeId) }
            ],
            Terminator = new MirReturn { Value = LocalPlace(target, typeId) }
        };
        var module = CreateDropInsertionModule(
            [
                new MirLocal { Id = source, Name = "source", TypeId = typeId },
                new MirLocal { Id = target, Name = "target", TypeId = typeId }
            ],
            [block]);

        var optimized = new CopyDropElisionPass().Run(module);

        var move = Assert.IsType<MirMove>(Assert.Single(optimized.Functions[0].BasicBlocks[0].Instructions));
        Assert.Equal(source, move.Source.Local);
        Assert.Equal(target, move.Target.Local);
    }
}
