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
    public void BorrowChecker_SharedLoadWithoutReadCapability_ReportsReadCapabilityDenied()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var borrowLocal = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "cap_load_read_denied",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = borrowLocal, Name = "b", TypeId = intType }
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
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowLocal, TypeId = intType }
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

        var checker = new BorrowChecker(
            func,
            liveness,
            capabilitySnapshot: BorrowCapabilitySnapshot.Enforced());
        checker.Check();

        var denied = Assert.Single(checker.Diagnostics, d => d.Kind == BorrowErrorKind.ReadCapabilityDenied);
        Assert.Contains("命中路径:", denied.Hint ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("@borrow(read)", denied.Hint ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("source=none", denied.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void BorrowChecker_StoreWithoutWriteCapability_ReportsWriteCapabilityDenied()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };

        var func = new MirFunc
        {
            Name = "cap_store_write_denied",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true, IsMutable = true }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType }
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

        var checker = new BorrowChecker(
            func,
            liveness,
            capabilitySnapshot: BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read));
        checker.Check();

        Assert.Contains(checker.Diagnostics, d => d.Kind == BorrowErrorKind.WriteCapabilityDenied);
    }

    [Fact]
    public void BorrowChecker_MoveWithoutMoveCapability_ReportsMoveCapabilityDenied()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var tmp = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "cap_move_denied",
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
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(
            func,
            liveness,
            capabilitySnapshot: BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Write));
        checker.Check();

        Assert.Contains(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MoveCapabilityDenied);
    }

    [Fact]
    public void BorrowChecker_CallOwnArgWithoutMoveCapability_ReportsMoveCapabilityDenied()
    {
        var (func, signatureCache, symbolTable) = CreateCallArgCapabilityFixture(
            "call_cap_own_denied",
            new SymbolId(920),
            ParamBorrowMode.Own);

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(
            func,
            liveness,
            signatureCache,
            symbolTable,
            capabilitySnapshot: BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Write));
        checker.Check();

        Assert.Contains(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MoveCapabilityDenied);
    }

    [Fact]
    public void BorrowChecker_CallSharedArgWithoutReadCapability_ReportsReadCapabilityDenied()
    {
        var (func, signatureCache, symbolTable) = CreateCallArgCapabilityFixture(
            "call_cap_shared_denied",
            new SymbolId(921),
            ParamBorrowMode.BorrowShared);

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(
            func,
            liveness,
            signatureCache,
            symbolTable,
            capabilitySnapshot: BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Move));
        checker.Check();

        Assert.Contains(checker.Diagnostics, d => d.Kind == BorrowErrorKind.ReadCapabilityDenied);
    }

    [Fact]
    public void BorrowChecker_CallMutableArgWithoutWriteCapability_ReportsWriteCapabilityDenied()
    {
        var (func, signatureCache, symbolTable) = CreateCallArgCapabilityFixture(
            "call_cap_mut_denied",
            new SymbolId(922),
            ParamBorrowMode.BorrowMutable);

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(
            func,
            liveness,
            signatureCache,
            symbolTable,
            capabilitySnapshot: BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Move));
        checker.Check();

        Assert.Contains(checker.Diagnostics, d => d.Kind == BorrowErrorKind.WriteCapabilityDenied);
    }

    [Fact]
    public void BorrowChecker_CallOwnArgAfterCopyFromIndexedBorrowedValue_DoesNotReportNeedOwnershipButBorrowed()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(9231);
        var arr = new LocalId { Value = 1 };
        var idx = new LocalId { Value = 2 };
        var borrowed = new LocalId { Value = 3 };
        var ownedArg = new LocalId { Value = 4 };

        var func = new MirFunc
        {
            Name = "call_own_after_index_copy",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = arr, Name = "arr", TypeId = intType, IsParameter = true },
                new MirLocal { Id = idx, Name = "idx", TypeId = intType, IsParameter = true },
                new MirLocal { Id = borrowed, Name = "borrowed", TypeId = intType },
                new MirLocal { Id = ownedArg, Name = "ownedArg", TypeId = intType }
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
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = arr, TypeId = intType },
                                Index = new MirPlace { Kind = PlaceKind.Local, Local = idx, TypeId = intType },
                                TypeId = intType
                            },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = intType }
                        },
                        new MirCopy
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = ownedArg, TypeId = intType }
                        },
                        new MirCall
                        {
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "callee", TypeId = TypeId.None },
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = ownedArg, TypeId = intType }]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var signatureCache = new LoanSignatureCache();
        signatureCache.SetSignature(calleeSymbol, new LoanSignature
        {
            FunctionName = "callee",
            FunctionSymbol = calleeSymbol,
            ParamRequirements =
            [
                new ParamBorrowRequirement
                {
                    ParamIndex = 0,
                    Name = "x",
                    Mode = ParamBorrowMode.Own
                }
            ]
        });

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness, signatureCache, new SymbolTable());
        checker.Check();

        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MutateWhileBorrowed);
        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.LifetimeTooLong);
    }

    [Fact]
    public void BorrowChecker_CallOwnArgAfterCopyFromIndexedBorrowedString_RetainsAliasStateForCopiedTarget()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(9232);
        var arr = new LocalId { Value = 1 };
        var idx = new LocalId { Value = 2 };
        var borrowed = new LocalId { Value = 3 };
        var ownedArg = new LocalId { Value = 4 };

        var func = new MirFunc
        {
            Name = "call_own_after_index_copy_string",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = arr, Name = "arr", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = idx, Name = "idx", TypeId = intType, IsParameter = true },
                new MirLocal { Id = borrowed, Name = "borrowed", TypeId = stringType },
                new MirLocal { Id = ownedArg, Name = "ownedArg", TypeId = stringType }
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
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = arr, TypeId = stringType },
                                Index = new MirPlace { Kind = PlaceKind.Local, Local = idx, TypeId = intType },
                                TypeId = stringType
                            },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = stringType }
                        },
                        new MirCopy
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = stringType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = ownedArg, TypeId = stringType }
                        },
                        new MirCall
                        {
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "callee", TypeId = TypeId.None },
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = ownedArg, TypeId = stringType }]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var signatureCache = new LoanSignatureCache();
        signatureCache.SetSignature(calleeSymbol, new LoanSignature
        {
            FunctionName = "callee",
            FunctionSymbol = calleeSymbol,
            ParamRequirements =
            [
                new ParamBorrowRequirement
                {
                    ParamIndex = 0,
                    Name = "x",
                    Mode = ParamBorrowMode.Own
                }
            ]
        });

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness, signatureCache, new SymbolTable());
        checker.Check();

        var borrowsAfterCopy = checker.GetBorrowsAtPoint(new BlockId { Value = 1 }, 1);
        Assert.Contains(borrowsAfterCopy, borrow => borrow.Borrower.Equals(ownedArg));
    }

    [Fact]
    public void BorrowChecker_CallOwnArgAfterCopyFromIndexedBorrowedTraitCopyType_DoesNotReportMutateWhileBorrowed()
    {
        var symbolTable = new SymbolTable();
        var adtId = symbolTable.DeclareAdt("Boxed", SourceSpan.Empty);
        var boxedType = symbolTable.GetSymbol<AdtSymbol>(adtId)!.TypeId;
        var copyTrait = symbolTable.DeclareTrait("Copy", SourceSpan.Empty);
        symbolTable.DeclareImpl(copyTrait, boxedType, SourceSpan.Empty);

        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(9233);
        var arr = new LocalId { Value = 1 };
        var idx = new LocalId { Value = 2 };
        var borrowed = new LocalId { Value = 3 };
        var ownedArg = new LocalId { Value = 4 };

        var func = new MirFunc
        {
            Name = "call_own_after_index_copy_trait_copy",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = arr, Name = "arr", TypeId = boxedType, IsParameter = true },
                new MirLocal { Id = idx, Name = "idx", TypeId = intType, IsParameter = true },
                new MirLocal { Id = borrowed, Name = "borrowed", TypeId = boxedType },
                new MirLocal { Id = ownedArg, Name = "ownedArg", TypeId = boxedType }
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
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = arr, TypeId = boxedType },
                                Index = new MirPlace { Kind = PlaceKind.Local, Local = idx, TypeId = intType },
                                TypeId = boxedType
                            },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = boxedType }
                        },
                        new MirCopy
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = boxedType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = ownedArg, TypeId = boxedType }
                        },
                        new MirCall
                        {
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "callee", TypeId = TypeId.None },
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = ownedArg, TypeId = boxedType }]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var signatureCache = new LoanSignatureCache();
        signatureCache.SetSignature(calleeSymbol, new LoanSignature
        {
            FunctionName = "callee",
            FunctionSymbol = calleeSymbol,
            ParamRequirements =
            [
                new ParamBorrowRequirement
                {
                    ParamIndex = 0,
                    Name = "x",
                    Mode = ParamBorrowMode.Own
                }
            ]
        });

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness, signatureCache, symbolTable);
        checker.Check();

        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MutateWhileBorrowed);
        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.LifetimeTooLong);
    }

    [Fact]
    public void BorrowChecker_CallOwnArgAfterCopyFromFieldBorrowedString_RetainsAliasStateForCopiedTarget()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var calleeSymbol = new SymbolId(9234);
        var receiver = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };
        var ownedArg = new LocalId { Value = 3 };

        var func = new MirFunc
        {
            Name = "call_own_after_field_copy_string",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = receiver, Name = "receiver", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = borrowed, Name = "borrowed", TypeId = stringType },
                new MirLocal { Id = ownedArg, Name = "ownedArg", TypeId = stringType }
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
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = receiver, TypeId = stringType },
                                FieldName = "_0",
                                TypeId = stringType
                            },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = stringType }
                        },
                        new MirCopy
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = stringType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = ownedArg, TypeId = stringType }
                        },
                        new MirCall
                        {
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "callee", TypeId = TypeId.None },
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = ownedArg, TypeId = stringType }]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var signatureCache = new LoanSignatureCache();
        signatureCache.SetSignature(calleeSymbol, new LoanSignature
        {
            FunctionName = "callee",
            FunctionSymbol = calleeSymbol,
            ParamRequirements =
            [
                new ParamBorrowRequirement
                {
                    ParamIndex = 0,
                    Name = "x",
                    Mode = ParamBorrowMode.Own
                }
            ]
        });

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness, signatureCache, new SymbolTable());
        checker.Check();

        var borrowsAfterCopy = checker.GetBorrowsAtPoint(new BlockId { Value = 1 }, 1);
        Assert.Contains(borrowsAfterCopy, borrow => borrow.Borrower.Equals(ownedArg));
    }

    [Fact]
    public void BorrowChecker_CallOwnArgAfterCopyFromDerefBorrowedTraitCopyType_DoesNotReportMutateWhileBorrowed()
    {
        var symbolTable = new SymbolTable();
        var adtId = symbolTable.DeclareAdt("Packet", SourceSpan.Empty);
        var packetType = symbolTable.GetSymbol<AdtSymbol>(adtId)!.TypeId;
        var copyTrait = symbolTable.DeclareTrait("Copy", SourceSpan.Empty);
        symbolTable.DeclareImpl(copyTrait, packetType, SourceSpan.Empty);

        var calleeSymbol = new SymbolId(9235);
        var pointer = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };
        var ownedArg = new LocalId { Value = 3 };

        var func = new MirFunc
        {
            Name = "call_own_after_deref_copy_trait_copy",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = pointer, Name = "pointer", TypeId = packetType, IsParameter = true },
                new MirLocal { Id = borrowed, Name = "borrowed", TypeId = packetType },
                new MirLocal { Id = ownedArg, Name = "ownedArg", TypeId = packetType }
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
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = pointer, TypeId = packetType },
                                TypeId = packetType
                            },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = packetType }
                        },
                        new MirCopy
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = borrowed, TypeId = packetType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = ownedArg, TypeId = packetType }
                        },
                        new MirCall
                        {
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "callee", TypeId = TypeId.None },
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = ownedArg, TypeId = packetType }]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var signatureCache = new LoanSignatureCache();
        signatureCache.SetSignature(calleeSymbol, new LoanSignature
        {
            FunctionName = "callee",
            FunctionSymbol = calleeSymbol,
            ParamRequirements =
            [
                new ParamBorrowRequirement
                {
                    ParamIndex = 0,
                    Name = "x",
                    Mode = ParamBorrowMode.Own
                }
            ]
        });

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(func, liveness, signatureCache, symbolTable);
        checker.Check();

        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MutateWhileBorrowed);
        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.LifetimeTooLong);
    }

    [Fact]
    public void BorrowChecker_CallMutableDerefArgWithoutWriteCapability_ReportsWriteCapabilityDenied()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(924);
        var p = new LocalId { Value = 1 };

        var func = new MirFunc
        {
            Name = "call_cap_mut_deref_denied",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = p, Name = "p", TypeId = intType, IsParameter = true }
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
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "callee", TypeId = intType },
                            Arguments =
                            [
                                new MirPlace
                                {
                                    Kind = PlaceKind.Deref,
                                    Base = new MirPlace { Kind = PlaceKind.Local, Local = p, TypeId = intType },
                                    TypeId = intType
                                }
                            ]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var signatureCache = new LoanSignatureCache();
        signatureCache.SetSignature(calleeSymbol, new LoanSignature
        {
            FunctionName = "callee",
            FunctionSymbol = calleeSymbol,
            ParamRequirements =
            [
                new ParamBorrowRequirement
                {
                    ParamIndex = 0,
                    Name = "x",
                    Mode = ParamBorrowMode.BorrowMutable,
                    Lifetime = new BorrowLifetimeId { Value = 1 }
                }
            ]
        });

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(
            func,
            liveness,
            signatureCache,
            new SymbolTable(),
            capabilitySnapshot: BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Move));
        checker.Check();

        Assert.Contains(checker.Diagnostics, d => d.Kind == BorrowErrorKind.WriteCapabilityDenied);
    }

    [Fact]
    public void BorrowChecker_CallOwnDerefArgWithTargetMoveCapability_DoesNotReportMoveCapabilityDenied()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(926);
        var p = new LocalId { Value = 1 };

        var derefArg = new MirPlace
        {
            Kind = PlaceKind.Deref,
            Base = new MirPlace { Kind = PlaceKind.Local, Local = p, TypeId = intType },
            TypeId = intType
        };
        Assert.True(BorrowTarget.TryResolve(derefArg, out var derefTarget));

        var func = new MirFunc
        {
            Name = "call_cap_own_deref_target_move_granted",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = p, Name = "p", TypeId = intType, IsParameter = true }
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
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "callee", TypeId = intType },
                            Arguments = [derefArg]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var signatureCache = new LoanSignatureCache();
        signatureCache.SetSignature(calleeSymbol, new LoanSignature
        {
            FunctionName = "callee",
            FunctionSymbol = calleeSymbol,
            ParamRequirements =
            [
                new ParamBorrowRequirement
                {
                    ParamIndex = 0,
                    Name = "x",
                    Mode = ParamBorrowMode.Own
                }
            ]
        });

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var snapshot = BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Write);
        snapshot.GrantLocal(p, BorrowCapabilityKind.Read);
        snapshot.GrantTarget(derefTarget, BorrowCapabilityKind.Move);

        var checker = new BorrowChecker(
            func,
            liveness,
            signatureCache,
            new SymbolTable(),
            capabilitySnapshot: snapshot);
        checker.Check();

        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == BorrowErrorKind.MoveCapabilityDenied);

        var statesText = BorrowFormatter.FormatBorrowAliasStates(checker);
        Assert.Contains("// capability resolution order: target -> local -> global", statesText, StringComparison.Ordinal);
        Assert.Contains("// capability locals:", statesText);
        Assert.Contains($"//   %{p.Value}: read", statesText);
        Assert.Contains("// capability targets:", statesText);
        Assert.Contains($"//   {derefTarget.StableKey}: move [source=target]", statesText, StringComparison.Ordinal);
    }

    [Fact]
    public void BorrowChecker_CallReturnedMutableBorrowWithoutWriteCapability_ReportsWriteCapabilityDeniedAndSkipsBinding()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(925);
        var x = new LocalId { Value = 1 };
        var borrowResult = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "call_cap_return_mut_denied",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = borrowResult, Name = "b", TypeId = intType }
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
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = borrowResult, TypeId = intType },
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "borrower", TypeId = intType },
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = x, TypeId = intType }]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

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
                IsMutable = true,
                Lifetime = new BorrowLifetimeId { Value = 1 },
                BoundToParams = [0]
            }
        });

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var liveness = new LivenessAnalyzer(func, usage);
        liveness.Analyze();

        var checker = new BorrowChecker(
            func,
            liveness,
            signatureCache,
            new SymbolTable(),
            capabilitySnapshot: BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Move));
        checker.Check();

        var borrowsAfterCall = checker.GetBorrowsAtPoint(new BlockId { Value = 1 }, 0);

        Assert.Contains(checker.Diagnostics, d => d.Kind == BorrowErrorKind.WriteCapabilityDenied);
        Assert.DoesNotContain(borrowsAfterCall, borrow => borrow.Borrower.Equals(borrowResult) && borrow.Borrowee.Equals(x));
    }

    [Fact]
    public void AffineChecker_LoopBackedgeMissingOutState_DoesNotReportUseBeforeInit()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var i = new LocalId { Value = 1 };
        var cond = new LocalId { Value = 2 };
        var tmp = new LocalId { Value = 3 };
        var header = new BlockId { Value = 2 };
        var body = new BlockId { Value = 3 };
        var exit = new BlockId { Value = 4 };

        var func = new MirFunc
        {
            Name = "affine_loop_init",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = i, Name = "i", TypeId = intType, IsMutable = true },
                new MirLocal { Id = cond, Name = "cond", TypeId = boolType },
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
                        new MirAssign
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = i, TypeId = intType },
                            Source = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(0) }
                        }
                    ],
                    Terminator = new MirGoto { Target = header }
                },
                new MirBasicBlock
                {
                    Id = header,
                    Instructions =
                    [
                        new MirCopy
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = i, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType }
                        },
                        new MirBinOp
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = cond, TypeId = boolType },
                            Operator = BinaryOp.Lt,
                            Left = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType },
                            Right = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(3) }
                        }
                    ],
                    Terminator = new MirSwitch
                    {
                        Discriminant = new MirPlace { Kind = PlaceKind.Local, Local = cond, TypeId = boolType },
                        Branches =
                        [
                            new MirSwitchBranch
                            {
                                Value = new MirConstant
                                {
                                    TypeId = boolType,
                                    Value = new MirConstantValue.BoolValue(true)
                                },
                                Target = body
                            }
                        ],
                        DefaultTarget = exit
                    }
                },
                new MirBasicBlock
                {
                    Id = body,
                    Instructions =
                    [
                        new MirBinOp
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType },
                            Operator = BinaryOp.Add,
                            Left = new MirPlace { Kind = PlaceKind.Local, Local = i, TypeId = intType },
                            Right = new MirConstant { TypeId = intType, Value = new MirConstantValue.IntValue(1) }
                        },
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = i, TypeId = intType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = tmp, TypeId = intType }
                        }
                    ],
                    Terminator = new MirGoto { Target = header }
                },
                new MirBasicBlock
                {
                    Id = exit,
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = i, TypeId = intType }
                    }
                }
            ]
        };

        var usage = new VariableUsageAnalyzer(func);
        usage.Analyze();

        var checker = new AffineTypeChecker(func, usage);
        checker.Check();

        Assert.DoesNotContain(checker.Diagnostics, d => d.Kind == AffineErrorKind.UseBeforeInit);
    }

    private static (MirFunc Function, LoanSignatureCache SignatureCache, SymbolTable SymbolTable) CreateCallArgCapabilityFixture(
        string functionName,
        SymbolId calleeSymbol,
        ParamBorrowMode mode)
    {
        var intType = new TypeId(BaseTypes.IntId);
        var arg = new LocalId { Value = 1 };

        var function = new MirFunc
        {
            Name = functionName,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = arg, Name = "x", TypeId = intType, IsParameter = true }
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
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "callee", TypeId = intType },
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = arg, TypeId = intType }]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var signature = new LoanSignature
        {
            FunctionName = "callee",
            FunctionSymbol = calleeSymbol,
            ParamRequirements =
            [
                new ParamBorrowRequirement
                {
                    ParamIndex = 0,
                    Name = "x",
                    Mode = mode,
                    Lifetime = new BorrowLifetimeId { Value = 1 }
                }
            ]
        };

        var signatureCache = new LoanSignatureCache();
        signatureCache.SetSignature(calleeSymbol, signature);
        var symbolTable = new SymbolTable();

        return (function, signatureCache, symbolTable);
    }

}
