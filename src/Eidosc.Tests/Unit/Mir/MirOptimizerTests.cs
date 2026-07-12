using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public class MirOptimizerTests
{
    [Fact]
    public void Optimize_DefaultOptimizer_PreservesSpecializationFailures()
    {
        var failure = new MirSpecializationFailureInfo
        {
            Reason = "unresolved-types",
            TemplateKey = "sym:42",
            TemplateName = "id",
            SignatureKey = "0|",
            SignatureDisplay = "0|",
            PreviewName = "id__spec_deadbeef"
        };
        var module = new MirModule
        {
            Name = "Main",
            SpecializationFailures = [failure],
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Terminator = new MirReturn()
                        }
                    ],
                    EntryBlockId = new BlockId { Value = 1 }
                }
            ]
        };

        var optimized = MirOptimizer.CreateDefault().Optimize(module);

        Assert.Equal([failure], optimized.SpecializationFailures);
    }

    [Fact]
    public void Optimize_DefaultOptimizer_RemovesUnreachableBlocks()
    {
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Terminator = new MirGoto { Target = new BlockId { Value = 2 } }
        };

        var reachable = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Terminator = new MirReturn()
        };

        var dead = new MirBasicBlock
        {
            Id = new BlockId { Value = 3 },
            Terminator = new MirReturn()
        };

        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    EntryBlockId = entry.Id,
                    BasicBlocks = [entry, reachable, dead]
                }
            ]
        };

        var optimized = MirOptimizer.CreateDefault().Optimize(module);
        var func = Assert.Single(optimized.Functions);

        Assert.Equal(2, func.BasicBlocks.Count);
        Assert.DoesNotContain(func.BasicBlocks, block => block.Id == dead.Id);
    }

    [Fact]
    public void Optimize_DefaultOptimizer_RespectsEntryBlockId()
    {
        var deadFirst = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            Terminator = new MirReturn()
        };

        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            IsEntry = true,
            Terminator = new MirGoto { Target = new BlockId { Value = 3 } }
        };

        var reachable = new MirBasicBlock
        {
            Id = new BlockId { Value = 3 },
            Terminator = new MirReturn()
        };

        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    EntryBlockId = entry.Id,
                    BasicBlocks = [deadFirst, entry, reachable]
                }
            ]
        };

        var optimized = MirOptimizer.CreateDefault().Optimize(module);
        var func = Assert.Single(optimized.Functions);

        Assert.Equal(2, func.BasicBlocks.Count);
        Assert.DoesNotContain(func.BasicBlocks, block => block.Id == deadFirst.Id);
        Assert.Contains(func.BasicBlocks, block => block.Id == entry.Id);
        Assert.Contains(func.BasicBlocks, block => block.Id == reachable.Id);
    }

    [Fact]
    public void Optimize_DefaultOptimizer_KeepsValuesUsedByTerminators()
    {
        var parameter = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "conditionSource",
            IsParameter = true
        };
        var condition = new MirLocal
        {
            Id = new LocalId { Value = 2 },
            Name = "condition"
        };

        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCopy
                {
                    Target = LocalPlace(condition.Id),
                    Source = LocalPlace(parameter.Id)
                }
            ],
            Terminator = new MirSwitch
            {
                Discriminant = LocalPlace(condition.Id),
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant
                        {
                            Value = new MirConstantValue.BoolValue(true)
                        },
                        Target = new BlockId { Value = 2 }
                    }
                ],
                DefaultTarget = new BlockId { Value = 3 }
            }
        };
        var thenBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Terminator = new MirReturn()
        };
        var elseBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 3 },
            Terminator = new MirReturn()
        };

        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    EntryBlockId = entry.Id,
                    Locals = [parameter, condition],
                    BasicBlocks = [entry, thenBlock, elseBlock]
                }
            ]
        };

        var optimized = MirOptimizer.CreateDefault().Optimize(module);
        var func = Assert.Single(optimized.Functions);
        var optimizedEntry = func.BasicBlocks.Single(block => block.Id == entry.Id);

        Assert.Contains(optimizedEntry.Instructions, instruction => instruction is MirCopy);
    }

    [Fact]
    public void Optimize_DefaultOptimizer_RemovesMergeBlockAndMarksSelfTailCall()
    {
        var entryId = new BlockId { Value = 1 };
        var mergeId = new BlockId { Value = 2 };
        var functionSymbol = new SymbolId(42);
        var parameter = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "n",
            IsParameter = true
        };
        var result = new MirLocal
        {
            Id = new LocalId { Value = 2 },
            Name = "result"
        };
        var callResult = new MirLocal
        {
            Id = new LocalId { Value = 3 },
            Name = "callResult"
        };

        var entry = new MirBasicBlock
        {
            Id = entryId,
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = LocalPlace(callResult.Id),
                    Function = new MirFunctionRef
                    {
                        Name = "loop",
                        SymbolId = functionSymbol
                    },
                    Arguments = [LocalPlace(parameter.Id)]
                },
                new MirCopy
                {
                    Target = LocalPlace(result.Id),
                    Source = LocalPlace(callResult.Id)
                }
            ],
            Terminator = new MirGoto { Target = mergeId }
        };
        var merge = new MirBasicBlock
        {
            Id = mergeId,
            Terminator = new MirReturn { Value = LocalPlace(result.Id) }
        };

        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "loop",
                    SymbolId = functionSymbol,
                    EntryBlockId = entryId,
                    Locals = [parameter, result, callResult],
                    BasicBlocks = [entry, merge]
                }
            ]
        };

        var optimized = MirOptimizer.CreateDefault().Optimize(module);
        var func = Assert.Single(optimized.Functions);

        Assert.DoesNotContain(func.BasicBlocks, block => block.Id == mergeId);
        var optimizedEntry = func.BasicBlocks.Single(block => block.Id == entryId);
        Assert.DoesNotContain(optimizedEntry.Instructions, static instruction => instruction is MirCall);
        var jump = Assert.IsType<MirGoto>(optimizedEntry.Terminator);
        Assert.Equal(entryId, jump.Target);
    }

    [Fact]
    public void Optimize_DefaultOptimizer_PreservesModuleMetadata()
    {
        var module = new MirModule
        {
            Name = "Main",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [100] = "Tuple(T1,T2)"
            },
            TypeDescriptors = new Dictionary<int, TypeDescriptor>
            {
                [100] = new TypeDescriptor.Tuple([new TypeId(1), new TypeId(2)])
            },
            CStructAccessors = new Dictionary<string, CStructAccessorInfo>
            {
                ["point_x"] = new() { FieldOffset = 0, FieldTypeId = BaseTypes.IntId, IsGetter = true }
            },
            ConstructorLayouts = new Dictionary<int, List<ConstructorTypeLayout>>
            {
                [200] = [new ConstructorTypeLayout { TypeName = "Box", ConstructorName = "Box", FieldTypeIds = [new TypeId(BaseTypes.IntId)] }]
            },
            LinkLibraries = ["m"],
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    EntryBlockId = new BlockId { Value = 1 },
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Terminator = new MirReturn()
                        }
                    ]
                }
            ]
        };

        var optimized = MirOptimizer.CreateDefault().Optimize(module);

        Assert.Equal("Tuple(T1,T2)", optimized.DynamicTypeKeys[100]);
        Assert.Contains(100, optimized.TypeDescriptors.Keys);
        Assert.Equal(0, optimized.CStructAccessors["point_x"].FieldOffset);
        Assert.Single(optimized.ConstructorLayouts[200]);
        Assert.Equal("m", Assert.Single(optimized.LinkLibraries));
    }

    [Fact]
    public void Optimize_ConstantStringConcat_FoldsToSingleStringLiteral()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var result = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "result",
            TypeId = stringType
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirBinOp
                {
                    Target = LocalPlace(result.Id),
                    Operator = BinaryOp.Concat,
                    Left = new MirConstant
                    {
                        TypeId = stringType,
                        Value = new MirConstantValue.StringValue("hello ")
                    },
                    Right = new MirConstant
                    {
                        TypeId = stringType,
                        Value = new MirConstantValue.StringValue("world")
                    }
                }
            ],
            Terminator = new MirReturn { Value = LocalPlace(result.Id) }
        };
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = stringType,
                    EntryBlockId = block.Id,
                    Locals = [result],
                    BasicBlocks = [block]
                }
            ]
        };

        var optimized = MirOptimizer.CreateDefault().Optimize(module);

        var optimizedBlock = Assert.Single(Assert.Single(optimized.Functions).BasicBlocks);
        var assign = Assert.IsType<MirAssign>(Assert.Single(optimizedBlock.Instructions));
        var constant = Assert.IsType<MirConstant>(assign.Source);
        var stringValue = Assert.IsType<MirConstantValue.StringValue>(constant.Value);
        Assert.Equal("hello world", stringValue.Value);
    }

    [Fact]
    public void Optimize_ConstantFolding_InvalidatesTrackedConstantWhenLocalIsRedefinedByCopy()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var source = new MirLocal { Id = new LocalId { Value = 1 }, Name = "source", TypeId = intType, IsParameter = true };
        var target = new MirLocal { Id = new LocalId { Value = 2 }, Name = "target", TypeId = intType };
        var result = new MirLocal { Id = new LocalId { Value = 3 }, Name = "result", TypeId = intType };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirAssign
                {
                    Target = LocalPlace(target.Id, intType),
                    Source = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(1)
                    }
                },
                new MirCopy
                {
                    Target = LocalPlace(target.Id, intType),
                    Source = LocalPlace(source.Id, intType)
                },
                new MirAssign
                {
                    Target = LocalPlace(result.Id, intType),
                    Source = LocalPlace(target.Id, intType)
                }
            ],
            Terminator = new MirReturn { Value = LocalPlace(result.Id, intType) }
        };
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = intType,
                    EntryBlockId = block.Id,
                    Locals = [source, target, result],
                    BasicBlocks = [block]
                }
            ]
        };

        var optimized = new ConstantFolding().Run(module);
        var optimizedBlock = Assert.Single(Assert.Single(optimized.Functions).BasicBlocks);
        var finalAssign = Assert.IsType<MirAssign>(optimizedBlock.Instructions[2]);

        Assert.Equal(LocalPlace(target.Id, intType), finalAssign.Source);
    }

    [Fact]
    public void Optimize_ConstantFolding_InvalidatesTrackedConstantWhenLocalIsRedefinedByStore()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var value = new MirLocal { Id = new LocalId { Value = 1 }, Name = "value", TypeId = intType, IsParameter = true };
        var target = new MirLocal { Id = new LocalId { Value = 2 }, Name = "target", TypeId = intType };
        var result = new MirLocal { Id = new LocalId { Value = 3 }, Name = "result", TypeId = intType };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirAssign
                {
                    Target = LocalPlace(target.Id, intType),
                    Source = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(1)
                    }
                },
                new MirStore
                {
                    Target = LocalPlace(target.Id, intType),
                    Value = LocalPlace(value.Id, intType)
                },
                new MirAssign
                {
                    Target = LocalPlace(result.Id, intType),
                    Source = LocalPlace(target.Id, intType)
                }
            ],
            Terminator = new MirReturn { Value = LocalPlace(result.Id, intType) }
        };
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = intType,
                    EntryBlockId = block.Id,
                    Locals = [value, target, result],
                    BasicBlocks = [block]
                }
            ]
        };

        var optimized = new ConstantFolding().Run(module);
        var optimizedBlock = Assert.Single(Assert.Single(optimized.Functions).BasicBlocks);
        var finalAssign = Assert.IsType<MirAssign>(optimizedBlock.Instructions[2]);

        Assert.Equal(LocalPlace(target.Id, intType), finalAssign.Source);
    }

    [Fact]
    public void Optimize_ConstantFolding_InvalidatesTrackedConstantWhenBinaryFoldFails()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var target = new MirLocal { Id = new LocalId { Value = 1 }, Name = "target", TypeId = intType };
        var divisor = new MirLocal { Id = new LocalId { Value = 2 }, Name = "divisor", TypeId = intType, IsParameter = true };
        var result = new MirLocal { Id = new LocalId { Value = 3 }, Name = "result", TypeId = intType };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirAssign
                {
                    Target = LocalPlace(target.Id, intType),
                    Source = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(1)
                    }
                },
                new MirBinOp
                {
                    Target = LocalPlace(target.Id, intType),
                    Operator = BinaryOp.Div,
                    Left = LocalPlace(target.Id, intType),
                    Right = LocalPlace(divisor.Id, intType)
                },
                new MirAssign
                {
                    Target = LocalPlace(result.Id, intType),
                    Source = LocalPlace(target.Id, intType)
                }
            ],
            Terminator = new MirReturn { Value = LocalPlace(result.Id, intType) }
        };
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = intType,
                    EntryBlockId = block.Id,
                    Locals = [target, divisor, result],
                    BasicBlocks = [block]
                }
            ]
        };

        var optimized = new ConstantFolding().Run(module);
        var optimizedBlock = Assert.Single(Assert.Single(optimized.Functions).BasicBlocks);
        var finalAssign = Assert.IsType<MirAssign>(optimizedBlock.Instructions[2]);

        Assert.Equal(LocalPlace(target.Id, intType), finalAssign.Source);
    }

    [Fact]
    public void Optimize_DeadCodeElimination_DoesNotKeepPredecessorDefinitionForTerminatorUseAfterLocalRedefinition()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var source = new MirLocal { Id = new LocalId { Value = 1 }, Name = "source", TypeId = boolType, IsParameter = true };
        var condition = new MirLocal { Id = new LocalId { Value = 2 }, Name = "condition", TypeId = boolType };
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirAssign
                {
                    Target = LocalPlace(condition.Id, boolType),
                    Source = new MirConstant
                    {
                        TypeId = boolType,
                        Value = new MirConstantValue.BoolValue(false)
                    }
                }
            ],
            Terminator = new MirGoto { Target = new BlockId { Value = 2 } }
        };
        var switchBlock = new MirBasicBlock
        {
            Id = new BlockId { Value = 2 },
            Instructions =
            [
                new MirCopy
                {
                    Target = LocalPlace(condition.Id, boolType),
                    Source = LocalPlace(source.Id, boolType)
                }
            ],
            Terminator = new MirSwitch
            {
                Discriminant = LocalPlace(condition.Id, boolType),
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant
                        {
                            TypeId = boolType,
                            Value = new MirConstantValue.BoolValue(true)
                        },
                        Target = new BlockId { Value = 3 }
                    }
                ],
                DefaultTarget = new BlockId { Value = 4 }
            }
        };
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = intType,
                    EntryBlockId = entry.Id,
                    Locals = [source, condition],
                    BasicBlocks =
                    [
                        entry,
                        switchBlock,
                        new MirBasicBlock { Id = new BlockId { Value = 3 }, Terminator = new MirReturn() },
                        new MirBasicBlock { Id = new BlockId { Value = 4 }, Terminator = new MirReturn() }
                    ]
                }
            ]
        };

        var optimized = new DeadCodeElimination().Run(module);
        var optimizedEntry = Assert.Single(optimized.Functions).BasicBlocks.Single(block => block.Id == entry.Id);

        Assert.Empty(optimizedEntry.Instructions);
    }

    [Fact]
    public void Inlining_FunctionRefWithUnknownValidSymbol_DoesNotResolveCandidateByName()
    {
        var callee = BuildInlineIdentityFunction("helper", SymbolId.None);
        var caller = BuildInlineCaller(
            "caller_unknown_symbol",
            new MirFunctionRef
            {
                Name = "helper",
                SymbolId = new SymbolId(9001)
            });
        var module = new MirModule
        {
            Name = "Main",
            Functions = [callee, caller]
        };

        var optimized = new Inlining(maxInlineSize: 0).Run(module);

        var optimizedCaller = Assert.Single(optimized.Functions, function => function.Name == caller.Name);
        var instruction = Assert.Single(optimizedCaller.BasicBlocks.Single().Instructions);
        var call = Assert.IsType<MirCall>(instruction);
        var functionRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal(new SymbolId(9001), functionRef.SymbolId);
    }

    [Fact]
    public void Inlining_FunctionRefWithDifferentSymbol_ResolvesCandidateByStructuredFunctionIdentity()
    {
        const string qualifiedName = "Lib::helper";
        var callee = BuildInlineIdentityFunction(
            "helper",
            SymbolId.None,
            new FunctionId
            {
                Name = "helper",
                QualifiedName = qualifiedName
            });
        var caller = BuildInlineCaller(
            "caller_structured_identity",
            new MirFunctionRef
            {
                Name = "imported_helper",
                SymbolId = new SymbolId(9002),
                FunctionId = new FunctionId
                {
                    SymbolId = new SymbolId(9002),
                    Name = "helper",
                    QualifiedName = qualifiedName
                }
            });
        var module = new MirModule
        {
            Name = "Main",
            Functions = [callee, caller]
        };

        var optimized = new Inlining(maxInlineSize: 0).Run(module);

        var optimizedCaller = Assert.Single(optimized.Functions, function => function.Name == caller.Name);
        Assert.DoesNotContain(
            optimizedCaller.BasicBlocks.Single().Instructions,
            static instruction => instruction is MirCall);
        Assert.Contains(
            optimizedCaller.BasicBlocks.Single().Instructions,
            static instruction => instruction is MirAssign);
    }

    [Fact]
    public void DropInsertion_OpenDynamicTypeLocal_DoesNotInsertDrop()
    {
        var openTypeId = new TypeId(100);
        var local = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "value",
            TypeId = openTypeId,
            IsParameter = true
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Function = new MirFunctionRef { Name = "consume" },
                    Arguments = [LocalPlace(local.Id) with { TypeId = openTypeId }]
                }
            ],
            Terminator = new MirReturn()
        };
        var module = new MirModule
        {
            Name = "Main",
            DynamicTypeKeys = new Dictionary<int, string>
            {
                [openTypeId.Value] = "TyVar_1"
            },
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    EntryBlockId = block.Id,
                    Locals = [local],
                    BasicBlocks = [block]
                }
            ]
        };

        var optimized = new DropInsertionPass().Run(module);
        var optimizedFunc = Assert.Single(optimized.Functions);

        Assert.DoesNotContain(
            optimizedFunc.BasicBlocks.SelectMany(static basicBlock => basicBlock.Instructions),
            static instruction => instruction is MirDrop);
    }

    [Fact]
    public void DropInsertion_LastUse_InsertsDropAfterInstruction()
    {
        var local = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "text",
            TypeId = new TypeId(BaseTypes.StringId),
            IsParameter = true
        };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Function = new MirFunctionRef { Name = "consume" },
                    Arguments = [LocalPlace(local.Id) with { TypeId = local.TypeId }]
                }
            ],
            Terminator = new MirReturn()
        };
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    EntryBlockId = block.Id,
                    Locals = [local],
                    BasicBlocks = [block]
                }
            ]
        };

        var optimized = new DropInsertionPass().Run(module);
        var instructions = Assert.Single(optimized.Functions).BasicBlocks.Single().Instructions;

        Assert.IsType<MirCall>(instructions[0]);
        var drop = Assert.IsType<MirDrop>(instructions[1]);
        var dropPlace = Assert.IsType<MirPlace>(drop.Value);
        Assert.Equal(local.TypeId, dropPlace.TypeId);
    }

    [Fact]
    public void DropInsertion_BranchLastUse_InsertsDropBeforeTerminator()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var text = new MirLocal { Id = new LocalId { Value = 1 }, Name = "text", TypeId = stringType, IsParameter = true };
        var condition = new MirLocal { Id = new LocalId { Value = 2 }, Name = "condition", TypeId = boolType, IsParameter = true };
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Function = new MirFunctionRef { Name = "observe" },
                    Arguments = [LocalPlace(text.Id, stringType)]
                }
            ],
            Terminator = new MirSwitch
            {
                Discriminant = LocalPlace(condition.Id, boolType),
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant { TypeId = boolType, Value = new MirConstantValue.BoolValue(true) },
                        Target = new BlockId { Value = 2 }
                    }
                ],
                DefaultTarget = new BlockId { Value = 3 }
            }
        };
        var module = CreateDropInsertionModule([text, condition], [
            entry,
            new MirBasicBlock { Id = new BlockId { Value = 2 }, Terminator = new MirReturn() },
            new MirBasicBlock { Id = new BlockId { Value = 3 }, Terminator = new MirReturn() }
        ]);

        var optimized = new DropInsertionPass().Run(module);
        var instructions = Assert.Single(optimized.Functions).BasicBlocks.Single(block => block.Id == entry.Id).Instructions;

        Assert.IsType<MirCall>(instructions[0]);
        Assert.IsType<MirDrop>(instructions[1]);
    }

    [Fact]
    public void DropInsertion_EarlyReturnBranches_InsertDropOnEachPath()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var text = new MirLocal { Id = new LocalId { Value = 1 }, Name = "text", TypeId = stringType, IsParameter = true };
        var condition = new MirLocal { Id = new LocalId { Value = 2 }, Name = "condition", TypeId = boolType, IsParameter = true };
        var thenId = new BlockId { Value = 2 };
        var elseId = new BlockId { Value = 3 };
        var entry = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Terminator = new MirSwitch
            {
                Discriminant = LocalPlace(condition.Id, boolType),
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant { TypeId = boolType, Value = new MirConstantValue.BoolValue(true) },
                        Target = thenId
                    }
                ],
                DefaultTarget = elseId
            }
        };
        var thenBlock = CreateDropReturnBlock(thenId, text.Id, stringType);
        var elseBlock = CreateDropReturnBlock(elseId, text.Id, stringType);
        var module = CreateDropInsertionModule([text, condition], [entry, thenBlock, elseBlock]);

        var optimized = new DropInsertionPass().Run(module);
        var blocks = Assert.Single(optimized.Functions).BasicBlocks;

        Assert.Contains(blocks.Single(block => block.Id == thenId).Instructions, static instruction => instruction is MirDrop);
        Assert.Contains(blocks.Single(block => block.Id == elseId).Instructions, static instruction => instruction is MirDrop);
    }

    [Fact]
    public void DropInsertion_LoopBackEdge_KeepsValueLiveAcrossIteration()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var text = new MirLocal { Id = new LocalId { Value = 1 }, Name = "text", TypeId = stringType, IsParameter = true };
        var condition = new MirLocal { Id = new LocalId { Value = 2 }, Name = "condition", TypeId = boolType, IsParameter = true };
        var headerId = new BlockId { Value = 1 };
        var loopBodyId = new BlockId { Value = 2 };
        var exitId = new BlockId { Value = 3 };
        var header = new MirBasicBlock
        {
            Id = headerId,
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Function = new MirFunctionRef { Name = "observe" },
                    Arguments = [LocalPlace(text.Id, stringType)]
                }
            ],
            Terminator = new MirSwitch
            {
                Discriminant = LocalPlace(condition.Id, boolType),
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant { TypeId = boolType, Value = new MirConstantValue.BoolValue(true) },
                        Target = loopBodyId
                    }
                ],
                DefaultTarget = exitId
            }
        };
        var module = CreateDropInsertionModule([text, condition], [
            header,
            new MirBasicBlock { Id = loopBodyId, Terminator = new MirGoto { Target = headerId } },
            new MirBasicBlock { Id = exitId, Terminator = new MirReturn() }
        ]);

        var optimized = new DropInsertionPass().Run(module);
        var optimizedHeader = Assert.Single(optimized.Functions).BasicBlocks.Single(block => block.Id == headerId);

        Assert.DoesNotContain(optimizedHeader.Instructions, static instruction => instruction is MirDrop);
    }

    [Fact]
    public void DropInsertion_ClosureCallableLastUse_InsertsDrop()
    {
        var closureType = new TypeId(BaseTypes.ErasedCallableId);
        var closure = new MirLocal { Id = new LocalId { Value = 1 }, Name = "closure", TypeId = closureType, IsParameter = true };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Function = LocalPlace(closure.Id, closureType)
                }
            ],
            Terminator = new MirReturn()
        };
        var module = CreateDropInsertionModule([closure], [block]);

        var optimized = new DropInsertionPass().Run(module);
        var instructions = Assert.Single(optimized.Functions).BasicBlocks.Single().Instructions;

        Assert.IsType<MirCall>(instructions[0]);
        Assert.IsType<MirDrop>(instructions[1]);
    }

    [Fact]
    public void DropInsertion_PartialApplicationBoundArgument_InsertsDrop()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var closureType = new TypeId(BaseTypes.ErasedCallableId);
        var boundArgument = new MirLocal { Id = new LocalId { Value = 1 }, Name = "boundArgument", TypeId = stringType, IsParameter = true };
        var partial = new MirLocal { Id = new LocalId { Value = 2 }, Name = "partial", TypeId = closureType };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirCall
                {
                    Target = LocalPlace(partial.Id, closureType),
                    Function = new MirFunctionRef { Name = "__partial_apply" },
                    Arguments = [LocalPlace(boundArgument.Id, stringType)]
                }
            ],
            Terminator = new MirReturn()
        };
        var module = CreateDropInsertionModule([boundArgument, partial], [block]);

        var optimized = new DropInsertionPass().Run(module);
        var instructions = Assert.Single(optimized.Functions).BasicBlocks.Single().Instructions;

        Assert.IsType<MirCall>(instructions[0]);
        Assert.IsType<MirDrop>(instructions[1]);
    }

    [Fact]
    public void DropInsertion_MoveSource_DoesNotInsertExtraDrop()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var source = new MirLocal { Id = new LocalId { Value = 1 }, Name = "source", TypeId = stringType, IsParameter = true };
        var target = new MirLocal { Id = new LocalId { Value = 2 }, Name = "target", TypeId = stringType };
        var block = new MirBasicBlock
        {
            Id = new BlockId { Value = 1 },
            IsEntry = true,
            Instructions =
            [
                new MirMove
                {
                    Target = LocalPlace(target.Id, stringType),
                    Source = LocalPlace(source.Id, stringType)
                }
            ],
            Terminator = new MirReturn()
        };
        var module = CreateDropInsertionModule([source, target], [block]);

        var optimized = new DropInsertionPass().Run(module);

        Assert.DoesNotContain(
            Assert.Single(optimized.Functions).BasicBlocks.Single().Instructions,
            static instruction => instruction is MirDrop);
    }

    [Fact]
    public void DropInsertion_AfterTailCallOptimization_DoesNotInsertDropAfterTailCall()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var text = new MirLocal { Id = new LocalId { Value = 1 }, Name = "text", TypeId = stringType, IsParameter = true };
        var result = new MirLocal { Id = new LocalId { Value = 2 }, Name = "result", TypeId = stringType };
        var module = CreateTailCallDropModule(text, result, isTailCall: true);

        var optimized = new DropInsertionPass().Run(module);
        var instructions = Assert.Single(optimized.Functions).BasicBlocks.Single().Instructions;

        var call = Assert.IsType<MirCall>(Assert.Single(instructions));
        Assert.True(call.IsTailCall);
    }

    [Fact]
    public void DropInsertion_BeforeTailCallOptimization_DocumentsTailCallBlockedByDrop()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var text = new MirLocal { Id = new LocalId { Value = 1 }, Name = "text", TypeId = stringType, IsParameter = true };
        var result = new MirLocal { Id = new LocalId { Value = 2 }, Name = "result", TypeId = stringType };
        var module = CreateTailCallDropModule(text, result, isTailCall: false);

        var withDrops = new DropInsertionPass().Run(module);
        var optimized = new TailCallOptimization(convertSelfRecursionToLoop: false).Run(withDrops);
        var instructions = Assert.Single(optimized.Functions).BasicBlocks.Single().Instructions;

        var call = Assert.IsType<MirCall>(instructions[0]);
        Assert.False(call.IsTailCall);
        Assert.IsType<MirDrop>(instructions[1]);
    }

    private static MirPlace LocalPlace(LocalId localId)
    {
        return new MirPlace
        {
            Kind = PlaceKind.Local,
            Local = localId
        };
    }

    private static MirPlace LocalPlace(LocalId localId, TypeId typeId)
    {
        return LocalPlace(localId) with { TypeId = typeId };
    }

    private static MirBasicBlock CreateDropReturnBlock(BlockId blockId, LocalId localId, TypeId typeId)
    {
        return new MirBasicBlock
        {
            Id = blockId,
            Instructions =
            [
                new MirCall
                {
                    Function = new MirFunctionRef { Name = "observe" },
                    Arguments = [LocalPlace(localId, typeId)]
                }
            ],
            Terminator = new MirReturn()
        };
    }

    private static MirModule CreateDropInsertionModule(
        List<MirLocal> locals,
        List<MirBasicBlock> blocks)
    {
        return new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    EntryBlockId = blocks[0].Id,
                    Locals = locals,
                    BasicBlocks = blocks
                }
            ]
        };
    }

    private static MirModule CreateTailCallDropModule(MirLocal argument, MirLocal result, bool isTailCall)
    {
        return new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = result.TypeId,
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals = [argument, result],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions =
                            [
                                new MirCall
                                {
                                    Target = LocalPlace(result.Id, result.TypeId),
                                    Function = new MirFunctionRef { Name = "callee" },
                                    Arguments = [LocalPlace(argument.Id, argument.TypeId)],
                                    IsTailCall = isTailCall
                                }
                            ],
                            Terminator = new MirReturn { Value = LocalPlace(result.Id, result.TypeId) }
                        }
                    ]
                }
            ]
        };
    }

    private static MirFunc BuildInlineIdentityFunction(
        string name,
        SymbolId symbolId,
        FunctionId? functionId = null)
    {
        var parameter = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "value",
            IsParameter = true
        };
        return new MirFunc
        {
            Name = name,
            SymbolId = symbolId,
            FunctionId = functionId ?? new FunctionId { SymbolId = symbolId, Name = name },
            EntryBlockId = new BlockId { Value = 1 },
            Locals = [parameter],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = LocalPlace(parameter.Id) }
                }
            ]
        };
    }

    private static MirFunc BuildInlineCaller(string name, MirFunctionRef functionRef)
    {
        var argument = new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "argument",
            IsParameter = true
        };
        var result = new MirLocal
        {
            Id = new LocalId { Value = 2 },
            Name = "result"
        };
        return new MirFunc
        {
            Name = name,
            EntryBlockId = new BlockId { Value = 1 },
            Locals = [argument, result],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(result.Id),
                            Function = functionRef,
                            Arguments = [LocalPlace(argument.Id)]
                        }
                    ],
                    Terminator = new MirReturn { Value = LocalPlace(result.Id) }
                }
            ]
        };
    }
}
