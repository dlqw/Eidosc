using Eidosc.Symbols;
using Eidosc;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

using BorrowLifetimeId = Eidosc.Borrow.LifetimeId;

namespace Eidosc.Tests.Unit.Borrow;

public partial class BorrowDataflowTests
{
    [Fact]
    public void AffineChecker_MoveFieldThenReadAggregate_ReportsPartialMove()
    {
        var aggregateType = new TypeId(9000);
        var fieldType = new TypeId(BaseTypes.StringId);
        var value = new LocalId { Value = 1 };
        var movedField = new LocalId { Value = 2 };
        var readBack = new LocalId { Value = 3 };
        var field = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = new MirPlace { Kind = PlaceKind.Local, Local = value, TypeId = aggregateType },
            FieldName = "name",
            TypeId = fieldType
        };

        var function = new MirFunc
        {
            Name = "partial_move",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = value, Name = "value", TypeId = aggregateType, IsParameter = true },
                new MirLocal { Id = movedField, Name = "moved_field", TypeId = fieldType },
                new MirLocal { Id = readBack, Name = "read_back", TypeId = aggregateType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirMove
                        {
                            Source = field,
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = movedField, TypeId = fieldType }
                        },
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = value, TypeId = aggregateType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = readBack, TypeId = aggregateType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(function);
        usage.Analyze();
        var checker = new AffineTypeChecker(function, usage);
        checker.Check();

        Assert.Contains(checker.Diagnostics, diagnostic => diagnostic.Kind == AffineErrorKind.UseAfterPartialMove);
    }

    [Fact]
    public void AffineChecker_ReinitializeMovedField_AllowsAggregateRead()
    {
        var aggregateType = new TypeId(9001);
        var fieldType = new TypeId(BaseTypes.StringId);
        var value = new LocalId { Value = 1 };
        var movedField = new LocalId { Value = 2 };
        var readBack = new LocalId { Value = 3 };
        var field = new MirPlace
        {
            Kind = PlaceKind.Field,
            Base = new MirPlace { Kind = PlaceKind.Local, Local = value, TypeId = aggregateType },
            FieldName = "name",
            TypeId = fieldType
        };

        var function = new MirFunc
        {
            Name = "reinitialize_partial_move",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = value, Name = "value", TypeId = aggregateType, IsParameter = true },
                new MirLocal { Id = movedField, Name = "moved_field", TypeId = fieldType },
                new MirLocal { Id = readBack, Name = "read_back", TypeId = aggregateType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirMove
                        {
                            Source = field,
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = movedField, TypeId = fieldType }
                        },
                        new MirStore
                        {
                            Target = field,
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = movedField, TypeId = fieldType }
                        },
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = value, TypeId = aggregateType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = readBack, TypeId = aggregateType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(function);
        usage.Analyze();
        var checker = new AffineTypeChecker(function, usage);
        checker.Check();

        Assert.DoesNotContain(checker.Diagnostics, diagnostic => diagnostic.Kind == AffineErrorKind.UseAfterPartialMove);
    }

    [Fact]
    public void AffineChecker_MoveInPredecessor_ReportsUseAfterMoveInSuccessor()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var tmp = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "f",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = tmp, Name = "tmp", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirMove
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType }
                        }
                    ],
                    Terminator = new MirGoto { Target = new BlockId { Value = 2 } }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType }
                    }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var checker = new AffineTypeChecker(func, usage);
        checker.Check();

        Assert.Contains(checker.Diagnostics, d => d.Kind == AffineErrorKind.UseAfterMove);
    }

    [Fact]
    public void BorrowChecker_BorrowInPredecessor_ConflictsWithMutationInSuccessor()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var tmp = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "g",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = tmp, Name = "tmp", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType }
                        }
                    ],
                    Terminator = new MirGoto { Target = new BlockId { Value = 2 } }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    Instructions =
                    [
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType }
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType }
                    }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        Assert.Contains(
            checker.Diagnostics,
            d => d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed ||
                 d.Kind == BorrowErrorKind.MultipleMutableBorrows);
    }

    [Fact]
    public void BorrowChecker_MoveBorrowHandle_PreservesBorrowConflict()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var x = new LocalId { Value = 1 };
        var b = new LocalId { Value = 2 };
        var c = new LocalId { Value = 3 };

        var func = new MirFunc
        {
            Name = "h",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = b, Name = "b", TypeId = stringType },
                new MirLocal { Id = c, Name = "c", TypeId = stringType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = stringType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = b, TypeId = stringType }
                        },
                        new MirMove
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = b, TypeId = stringType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = c, TypeId = stringType }
                        },
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = stringType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = c, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        Assert.Contains(
            checker.Diagnostics,
            d => d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed);
    }

    [Fact]
    public void BorrowChecker_FieldSensitiveBorrow_BorrowFieldThenWriteSiblingField_DoesNotConflict()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "field_sensitive_no_conflict",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = borrowed, Name = "borrowed", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace
                            {
                                Kind = PlaceKind.Field,
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                                FieldName = "_0",
                                TypeId = intType
                            },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = intType }
                        },
                        new MirStore
                        {
                            Target = new MirPlace
                            {
                                Kind = PlaceKind.Field,
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                                FieldName = "_1",
                                TypeId = intType
                            },
                            Value = new MirConstant
                            {
                                TypeId = intType,
                                Value = new MirConstantValue.IntValue(1)
                            }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed);
        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MultipleMutableBorrows);
    }

    [Fact]
    public void BorrowChecker_FieldSensitiveBorrow_BorrowFieldThenWriteSameField_ReportsConflict()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "field_sensitive_conflict",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = borrowed, Name = "borrowed", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace
                            {
                                Kind = PlaceKind.Field,
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                                FieldName = "_0",
                                TypeId = intType
                            },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = intType }
                        },
                        new MirStore
                        {
                            Target = new MirPlace
                            {
                                Kind = PlaceKind.Field,
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                                FieldName = "_0",
                                TypeId = intType
                            },
                            Value = new MirConstant
                            {
                                TypeId = intType,
                                Value = new MirConstantValue.IntValue(1)
                            }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        Assert.Contains(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed);
    }

    [Fact]
    public void BorrowChecker_IndexSensitiveBorrow_ConstIndexThenWriteOtherConstIndex_DoesNotConflict()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "index_sensitive_const_no_conflict",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = borrowed, Name = "borrowed", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace
                            {
                                Kind = PlaceKind.Index,
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                                Index = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(0) },
                                TypeId = intType
                            },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = intType }
                        },
                        new MirStore
                        {
                            Target = new MirPlace
                            {
                                Kind = PlaceKind.Index,
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                                Index = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(1) },
                                TypeId = intType
                            },
                            Value = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(1) }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed);
        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MultipleMutableBorrows);
    }

    [Fact]
    public void BorrowChecker_IndexSensitiveBorrow_SymbolicIndicesRemainConservative()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var i = new LocalId { Value = 2 };
        var j = new LocalId { Value = 3 };
        var borrowed = new LocalId { Value = 4 };

        var func = new MirFunc
        {
            Name = "index_sensitive_symbolic_conservative",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = i, Name = "i", TypeId = intType, IsParameter = true },
                new MirLocal { Id = j, Name = "j", TypeId = intType, IsParameter = true },
                new MirLocal { Id = borrowed, Name = "borrowed", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace
                            {
                                Kind = PlaceKind.Index,
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                                Index = new MirPlace { Kind = PlaceKind.Local, Local = i, TypeId = intType },
                                TypeId = intType
                            },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = intType }
                        },
                        new MirStore
                        {
                            Target = new MirPlace
                            {
                                Kind = PlaceKind.Index,
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                                Index = new MirPlace { Kind = PlaceKind.Local, Local = j, TypeId = intType },
                                TypeId = intType
                            },
                            Value = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(1) }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        Assert.Contains(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed);
    }

    [Fact]
    public void BorrowChecker_DerefBorrow_WritePointerLocal_DoesNotConflict()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var ptr = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "deref_domain_split",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = ptr, Name = "ptr", TypeId = intType, IsParameter = true },
                new MirLocal { Id = borrowed, Name = "borrowed", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace
                            {
                                Kind = PlaceKind.Deref,
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = ptr, TypeId = intType },
                                TypeId = intType
                            },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = intType }
                        },
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = ptr, TypeId = intType },
                            Value = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(1) }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed);
        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MultipleMutableBorrows);
    }

    [Fact]
    public void BorrowChecker_OutOfOrderBlocks_StillPropagatesBorrowConflict()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var tmp = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "out_of_order",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = tmp, Name = "tmp", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirGoto { Target = new BlockId { Value = 2 } }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 3 },
                    Instructions =
                    [
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType }
                        }
                    ],
                    Terminator = new MirGoto { Target = new BlockId { Value = 3 } }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        Assert.Contains(
            checker.Diagnostics,
            d => d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed);
    }

    [Fact]
    public void BorrowChecker_MergeBorrowHandlePaths_PropagatesAllAliasesAfterMove()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var cond = new LocalId { Value = 1 };
        var x = new LocalId { Value = 2 };
        var y = new LocalId { Value = 3 };
        var b = new LocalId { Value = 4 };
        var c = new LocalId { Value = 5 };
        var joinBlock = new BlockId { Value = 4 };

        var func = new MirFunc
        {
            Name = "merge_aliases",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = cond, Name = "cond", TypeId = intType, IsParameter = true },
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = y, Name = "y", TypeId = intType, IsParameter = true },
                new MirLocal { Id = b, Name = "b", TypeId = intType },
                new MirLocal { Id = c, Name = "c", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirSwitch
                    {
                        Discriminant = new MirPlace { Kind = PlaceKind.Local, Local = cond, TypeId = intType },
                        Branches =
                        [
                            new MirSwitchBranch
                            {
                                Value = new MirConstant
                                {
                                    Value = new MirConstantValue.IntValue(0),
                                    TypeId = intType
                                },
                                Target = new BlockId { Value = 2 }
                            }
                        ],
                        DefaultTarget = new BlockId { Value = 3 }
                    }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = b, TypeId = intType }
                        }
                    ],
                    Terminator = new MirGoto { Target = joinBlock }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 3 },
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = y, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = b, TypeId = intType }
                        }
                    ],
                    Terminator = new MirGoto { Target = joinBlock }
                },
                new MirBasicBlock
                {
                    Id = joinBlock,
                    Instructions =
                    [
                        new MirMove
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = b, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = c, TypeId = intType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        var borrowsAfterMove = checker.GetBorrowsAtPoint(joinBlock, 0);

        Assert.Contains(borrowsAfterMove, borrow => borrow.Borrower.Equals(c) && borrow.Borrowee.Equals(x));
        Assert.Contains(borrowsAfterMove, borrow => borrow.Borrower.Equals(c) && borrow.Borrowee.Equals(y));
        Assert.DoesNotContain(borrowsAfterMove, borrow => borrow.Borrower.Equals(b));
    }

    [Fact]
    public void BorrowChecker_CallReturningBorrow_ConflictsWithMutation()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var calleeSymbol = new SymbolId(410);
        var x = new LocalId { Value = 1 };
        var borrowResult = new LocalId { Value = 2 };

        var signatureCache = new LoanSignatureCache();
        signatureCache.SetSignature(calleeSymbol, new LoanSignature
        {
            FunctionName = "borrower",
            FunctionSymbol = calleeSymbol,
            ParamRequirements =
            [
                new ParamBorrowRequirement
                {
                    ParamIndex = 0,
                    Name = "x",
                    Mode = ParamBorrowMode.BorrowShared,
                    Lifetime = new BorrowLifetimeId { Value = 1 }
                }
            ],
            ReturnConstraint = new ReturnBorrowConstraint
            {
                IsBorrow = true,
                IsMutable = false,
                Lifetime = new BorrowLifetimeId { Value = 1 },
                BoundToParams = [0]
            }
        });

        var func = new MirFunc
        {
            Name = "caller",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = borrowResult, Name = "b", TypeId = stringType }
            ],
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
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowResult, TypeId = stringType },
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "borrower", TypeId = stringType },
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = stringType }]
                        },
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = stringType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = borrowResult, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness, signatureCache, new SymbolTable());
        checker.Check();

        var afterCall = checker.GetBorrowsAtPoint(new BlockId { Value = 1 }, 0);

        Assert.Contains(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed);
        Assert.Contains(afterCall, borrow => borrow.Borrower.Equals(borrowResult) && borrow.Borrowee.Equals(x));
        Assert.Contains(afterCall, borrow => borrow.OriginSummary.Contains("call @borrower arg[0]"));
    }

    [Fact]
    public void BorrowChecker_AliasDebugOutput_IncludesCrossBlockOrigins()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var b = new LocalId { Value = 2 };
        var c = new LocalId { Value = 3 };

        var func = new MirFunc
        {
            Name = "debug_alias",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = b, Name = "b", TypeId = intType },
                new MirLocal { Id = c, Name = "c", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = b, TypeId = intType }
                        }
                    ],
                    Terminator = new MirGoto { Target = new BlockId { Value = 2 } }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    Instructions =
                    [
                        new MirMove
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = b, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = c, TypeId = intType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        var debugText = BorrowFormatter.FormatBorrowAliasStates(checker);

        Assert.Contains("bb2:0", debugText);
        Assert.Contains("origin=load %1 -> %2 @ bb1:0", debugText);
        Assert.Contains("trace: load %1 -> %2 @ bb1:0 => move %2 -> %3 @ bb2:0", debugText);
    }

    [Fact]
    public void BorrowChecker_ConflictDiagnostics_IncludeAliasTraceIdLinkage()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var tmp = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "borrow_trace_id_link",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = tmp, Name = "tmp", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType }
                        },
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        var conflict = Assert.Single(checker.Diagnostics, d =>
            d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed &&
            !string.IsNullOrEmpty(d.RelatedAliasTraceId));

        Assert.Contains(conflict.RelatedAliasTraceId!, conflict.Hint ?? string.Empty);

        var statesText = BorrowFormatter.FormatBorrowAliasStates(checker);
        Assert.Contains($"id={conflict.RelatedAliasTraceId}", statesText);

        var errorsText = BorrowFormatter.FormatBorrowErrors(checker);
        Assert.Contains($"alias trace id: {conflict.RelatedAliasTraceId}", errorsText);
        Assert.Contains($"lookup: search \"id={conflict.RelatedAliasTraceId}\"", errorsText);
    }

    [Fact]
    public void AffineChecker_UseAfterMove_PropagatesUseAndMoveSpan()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var tmp = new LocalId { Value = 2 };
        var moveSpan = SpanAt(4);
        var useSpan = SpanAt(18);

        var func = new MirFunc
        {
            Name = "affine_span",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = tmp, Name = "tmp", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirMove
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType, Span = moveSpan },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType, Span = moveSpan },
                            Span = moveSpan
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType, Span = useSpan },
                        Span = useSpan
                    }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var checker = new AffineTypeChecker(func, usage);
        checker.Check();

        var diagnostic = Assert.Single(checker.Diagnostics, d => d.Kind == AffineErrorKind.UseAfterMove);
        Assert.Equal(useSpan.Location.Position, diagnostic.Span.Location.Position);
        Assert.True(diagnostic.RelatedSpan.HasValue);
        Assert.Equal(moveSpan.Location.Position, diagnostic.RelatedSpan.Value.Location.Position);
    }

    [Fact]
    public void BorrowChecker_MutateConflict_PropagatesPrimaryAndRelatedSpan()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var tmp = new LocalId { Value = 2 };
        var borrowSpan = SpanAt(8);
        var storeSpan = SpanAt(24);

        var func = new MirFunc
        {
            Name = "borrow_span_conflict",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = tmp, Name = "tmp", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType, Span = borrowSpan },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType, Span = borrowSpan },
                            Span = borrowSpan
                        },
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType, Span = storeSpan },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType, Span = storeSpan },
                            Span = storeSpan
                        }
                    ],
                    Terminator = new MirReturn { Value = null, Span = storeSpan }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        var diagnostic = Assert.Single(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed);
        Assert.Equal(storeSpan.Location.Position, diagnostic.Span.Location.Position);
        Assert.True(diagnostic.RelatedSpan.HasValue);
        Assert.Equal(borrowSpan.Location.Position, diagnostic.RelatedSpan.Value.Location.Position);
    }

    [Fact]
    public void BorrowChecker_ReturnWithActiveBorrow_UsesReturnAndBorrowSpan()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };
        var borrowSpan = SpanAt(6);
        var returnSpan = SpanAt(28);

        var func = new MirFunc
        {
            Name = "borrow_return_span",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = borrowed, Name = "borrowed", TypeId = intType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType, Span = borrowSpan },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = intType, Span = borrowSpan },
                            Span = borrowSpan
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType, Span = returnSpan },
                        Span = returnSpan
                    }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness);
        checker.Check();

        var diagnostic = Assert.Single(checker.Diagnostics, d => d.Kind == BorrowErrorKind.BorrowedWhileReturned);
        Assert.Equal(returnSpan.Location.Position, diagnostic.Span.Location.Position);
        Assert.True(diagnostic.RelatedSpan.HasValue);
        Assert.Equal(borrowSpan.Location.Position, diagnostic.RelatedSpan.Value.Location.Position);
    }

    [Fact]
    public void BorrowChecker_CallReturnedBorrow_DropThenMutate_DoesNotReportConflict()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var calleeSymbol = new SymbolId(411);
        var x = new LocalId { Value = 1 };
        var borrowResult = new LocalId { Value = 2 };

        var signatureCache = new LoanSignatureCache();
        signatureCache.SetSignature(calleeSymbol, new LoanSignature
        {
            FunctionName = "borrower",
            FunctionSymbol = calleeSymbol,
            ParamRequirements =
            [
                new ParamBorrowRequirement
                {
                    ParamIndex = 0,
                    Name = "x",
                    Mode = ParamBorrowMode.BorrowShared,
                    Lifetime = new BorrowLifetimeId { Value = 1 }
                }
            ],
            ReturnConstraint = new ReturnBorrowConstraint
            {
                IsBorrow = true,
                IsMutable = false,
                Lifetime = new BorrowLifetimeId { Value = 1 },
                BoundToParams = [0]
            }
        });

        var func = new MirFunc
        {
            Name = "caller_drop_then_store",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = borrowResult, Name = "b", TypeId = stringType }
            ],
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
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowResult, TypeId = stringType },
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "borrower", TypeId = stringType },
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = stringType }]
                        },
                        new MirDrop
                        {
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = borrowResult, TypeId = stringType }
                        },
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = stringType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness, signatureCache, new SymbolTable());
        checker.Check();

        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MutableWhileImmutableBorrowed);
    }


    private static SourceSpan SpanAt(int position, int length = 1)
    {
        return new SourceSpan(new SourceLocation(position, 0, position), length);
    }
}
