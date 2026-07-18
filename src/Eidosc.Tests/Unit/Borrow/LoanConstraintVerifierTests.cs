using Eidosc.Symbols;
using Eidosc;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
using Xunit;

using LifetimeId = Eidosc.Borrow.LifetimeId;

namespace Eidosc.Tests.Unit.Borrow;

public partial class LoanConstraintVerifierTests
{
    [Fact]
    public void VerifyFunction_CallWithOwnedParamTwice_ReportsUseAfterMove()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var calleeSymbol = new SymbolId(300);

        var calleeParam = new LocalId { Value = 1 };
        var calleeTmp = new LocalId { Value = 2 };

        var callee = new MirFunc
        {
            Name = "callee",
            SymbolId = calleeSymbol,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = calleeParam, Name = "x", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = calleeTmp, Name = "tmp", TypeId = stringType }
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
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = calleeParam, TypeId = stringType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = calleeTmp, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = calleeTmp, TypeId = stringType }
                    }
                }
            ]
        };

        var callerParam = new LocalId { Value = 1 };

        var caller = new MirFunc
        {
            Name = "caller",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = callerParam, Name = "a", TypeId = stringType, IsParameter = true }
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
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "callee", TypeId = stringType },
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = callerParam, TypeId = stringType }]
                        },
                        new MirCall
                        {
                            Function = new MirFunctionRef { SymbolId = calleeSymbol, Name = "callee", TypeId = stringType },
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = callerParam, TypeId = stringType }]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var symbolTable = new SymbolTable();
        var signatureCache = new LoanSignatureCache();
        var inferer = new LoanSignatureInferer(callee, signatureCache, symbolTable);
        inferer.Infer();

        var verifier = new LoanConstraintVerifier(signatureCache, symbolTable);
        var results = verifier.VerifyFunction(caller);

        Assert.Contains(results, r => r.Violation == LoanConstraintViolation.UseAfterMove);
        Assert.Contains(verifier.Diagnostics, d => d.Kind == BorrowErrorKind.UseAfterMove);
    }

    [Fact]
    public void VerifyFunction_BorrowedReturnBlocksMutation()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var calleeSymbol = new SymbolId(310);

        var calleeSignature = new LoanSignature
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
                    Lifetime = new LifetimeId { Value = 1 }
                }
            ],
            ReturnConstraint = new ReturnBorrowConstraint
            {
                IsBorrow = true,
                IsMutable = false,
                Lifetime = new LifetimeId { Value = 1 },
                BoundToParams = [0]
            }
        };

        var cache = new LoanSignatureCache();
        cache.SetSignature(calleeSymbol, calleeSignature);

        var target = new LocalId { Value = 1 };
        var borrowResult = new LocalId { Value = 2 };

        var caller = new MirFunc
        {
            Name = "caller",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = target, Name = "x", TypeId = stringType, IsParameter = true },
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
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = stringType }]
                        },
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = stringType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = borrowResult, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var verifier = new LoanConstraintVerifier(cache, new SymbolTable());
        var results = verifier.VerifyFunction(caller);

        Assert.Contains(results, r => r.Violation == LoanConstraintViolation.MutateWhileBorrowed);
        Assert.Contains(verifier.Diagnostics, d => d.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

    [Fact]
    public void VerifyFunction_AliasDebugOutput_IncludesCrossBlockOriginsAndMovedLocals()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var b = new LocalId { Value = 2 };
        var c = new LocalId { Value = 3 };

        var func = new MirFunc
        {
            Name = "loan_debug",
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

        var verifier = new LoanConstraintVerifier(new LoanSignatureCache(), new SymbolTable());
        verifier.VerifyFunction(func);

        var debugText = BorrowFormatter.FormatLoanConstraintStates(verifier);

        Assert.Contains("bb2:0", debugText);
        Assert.Contains("origin=load %1 -> %2 @ bb1:0", debugText);
        Assert.Contains("trace: load %1 -> %2 @ bb1:0 => move %2 -> %3 @ bb2:0", debugText);
        Assert.Contains("moved: %2", debugText);
    }

    [Fact]
    public void VerifyFunction_FieldSensitiveBorrow_BorrowFieldThenWriteSiblingField_DoesNotReportMutateWhileBorrowed()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "loan_field_sensitive_no_conflict",
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

        var verifier = new LoanConstraintVerifier(new LoanSignatureCache(), new SymbolTable());
        var results = verifier.VerifyFunction(func);

        Assert.DoesNotContain(results, r => r.Violation == LoanConstraintViolation.MutateWhileBorrowed);
        Assert.DoesNotContain(verifier.Diagnostics, d => d.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

    [Fact]
    public void VerifyFunction_FieldSensitiveBorrow_BorrowFieldThenWriteSameField_ReportsMutateWhileBorrowed()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "loan_field_sensitive_conflict",
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

        var verifier = new LoanConstraintVerifier(new LoanSignatureCache(), new SymbolTable());
        var results = verifier.VerifyFunction(func);

        Assert.Contains(results, r => r.Violation == LoanConstraintViolation.MutateWhileBorrowed);
        Assert.Contains(verifier.Diagnostics, d => d.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

    [Fact]
    public void VerifyFunction_IndexSensitiveBorrow_ConstIndexThenWriteOtherConstIndex_DoesNotReportMutateWhileBorrowed()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "loan_index_sensitive_const_no_conflict",
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

        var verifier = new LoanConstraintVerifier(new LoanSignatureCache(), new SymbolTable());
        var results = verifier.VerifyFunction(func);

        Assert.DoesNotContain(results, r => r.Violation == LoanConstraintViolation.MutateWhileBorrowed);
        Assert.DoesNotContain(verifier.Diagnostics, d => d.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

    [Fact]
    public void VerifyFunction_IndexSensitiveBorrow_SymbolicIndicesRemainConservative()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var i = new LocalId { Value = 2 };
        var j = new LocalId { Value = 3 };
        var borrowed = new LocalId { Value = 4 };

        var func = new MirFunc
        {
            Name = "loan_index_sensitive_symbolic_conservative",
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

        var verifier = new LoanConstraintVerifier(new LoanSignatureCache(), new SymbolTable());
        var results = verifier.VerifyFunction(func);

        Assert.Contains(results, r => r.Violation == LoanConstraintViolation.MutateWhileBorrowed);
        Assert.Contains(verifier.Diagnostics, d => d.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

    [Fact]
    public void VerifyFunction_DerefBorrow_WritePointerLocal_DoesNotReportMutateWhileBorrowed()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var ptr = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "loan_deref_domain_split",
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

        var verifier = new LoanConstraintVerifier(new LoanSignatureCache(), new SymbolTable());
        var results = verifier.VerifyFunction(func);

        Assert.DoesNotContain(results, r => r.Violation == LoanConstraintViolation.MutateWhileBorrowed);
        Assert.DoesNotContain(verifier.Diagnostics, d => d.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

    [Fact]
    public void VerifyFunction_ConflictDiagnostics_IncludeAliasTraceIdLinkage()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var calleeSymbol = new SymbolId(311);

        var calleeSignature = new LoanSignature
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
                    Lifetime = new LifetimeId { Value = 1 }
                }
            ],
            ReturnConstraint = new ReturnBorrowConstraint
            {
                IsBorrow = true,
                IsMutable = false,
                Lifetime = new LifetimeId { Value = 1 },
                BoundToParams = [0]
            }
        };

        var cache = new LoanSignatureCache();
        cache.SetSignature(calleeSymbol, calleeSignature);

        var target = new LocalId { Value = 1 };
        var borrowResult = new LocalId { Value = 2 };

        var caller = new MirFunc
        {
            Name = "caller_trace_id_link",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = target, Name = "x", TypeId = stringType, IsParameter = true },
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
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = stringType }]
                        },
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = stringType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = borrowResult, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var verifier = new LoanConstraintVerifier(cache, new SymbolTable());
        verifier.VerifyFunction(caller);

        var conflict = Assert.Single(verifier.Diagnostics, d =>
            d.Kind == BorrowErrorKind.MutateWhileBorrowed &&
            !string.IsNullOrEmpty(d.RelatedAliasTraceId));

        Assert.Contains(conflict.RelatedAliasTraceId!, conflict.Hint ?? string.Empty);

        var statesText = BorrowFormatter.FormatLoanConstraintStates(verifier);
        Assert.Contains($"id={conflict.RelatedAliasTraceId}", statesText);

        var errorsText = BorrowFormatter.FormatLoanConstraintErrors(verifier);
        Assert.Contains($"alias trace id: {conflict.RelatedAliasTraceId}", errorsText);
        Assert.Contains($"lookup: search \"id={conflict.RelatedAliasTraceId}\"", errorsText);
    }

    [Fact]
    public void VerifyFunction_ReturnedBorrowDropThenMutate_DoesNotReportMutateWhileBorrowed()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var calleeSymbol = new SymbolId(320);

        var calleeSignature = new LoanSignature
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
                    Lifetime = new LifetimeId { Value = 1 }
                }
            ],
            ReturnConstraint = new ReturnBorrowConstraint
            {
                IsBorrow = true,
                IsMutable = false,
                Lifetime = new LifetimeId { Value = 1 },
                BoundToParams = [0]
            }
        };

        var cache = new LoanSignatureCache();
        cache.SetSignature(calleeSymbol, calleeSignature);

        var target = new LocalId { Value = 1 };
        var borrowResult = new LocalId { Value = 2 };

        var caller = new MirFunc
        {
            Name = "caller_drop_then_store",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = target, Name = "x", TypeId = stringType, IsParameter = true },
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
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = stringType }]
                        },
                        new MirDrop
                        {
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = borrowResult, TypeId = stringType }
                        },
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = stringType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var verifier = new LoanConstraintVerifier(cache, new SymbolTable());
        var results = verifier.VerifyFunction(caller);

        Assert.DoesNotContain(results, r => r.Violation == LoanConstraintViolation.MutateWhileBorrowed);
        Assert.DoesNotContain(verifier.Diagnostics, d => d.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

    [Fact]
    public void VerifyFunction_ReturnedBorrowOnlyDroppedOnOneBranch_JoinStillReportsConflict()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(321);

        var calleeSignature = new LoanSignature
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
                    Lifetime = new LifetimeId { Value = 1 }
                }
            ],
            ReturnConstraint = new ReturnBorrowConstraint
            {
                IsBorrow = true,
                IsMutable = false,
                Lifetime = new LifetimeId { Value = 1 },
                BoundToParams = [0]
            }
        };

        var cache = new LoanSignatureCache();
        cache.SetSignature(calleeSymbol, calleeSignature);

        var cond = new LocalId { Value = 1 };
        var target = new LocalId { Value = 2 };
        var borrowResult = new LocalId { Value = 3 };

        var join = new BlockId { Value = 4 };

        var caller = new MirFunc
        {
            Name = "caller_branch_drop",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = cond, Name = "cond", TypeId = intType, IsParameter = true },
                new MirLocal { Id = target, Name = "x", TypeId = stringType, IsParameter = true },
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
                            Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = stringType }]
                        }
                    ],
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
                        new MirDrop
                        {
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = borrowResult, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirGoto { Target = join }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 3 },
                    Terminator = new MirGoto { Target = join }
                },
                new MirBasicBlock
                {
                    Id = join,
                    Instructions =
                    [
                        new MirStore
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = stringType },
                            Value = new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var verifier = new LoanConstraintVerifier(cache, new SymbolTable());
        var results = verifier.VerifyFunction(caller);

        Assert.Contains(results, r => r.Violation == LoanConstraintViolation.MutateWhileBorrowed);
        Assert.Contains(verifier.Diagnostics, d => d.Kind == BorrowErrorKind.MutateWhileBorrowed && d.Location.Block.Equals(join));
    }

    [Fact]
    public void VerifyFunction_SharedLoadWithoutReadCapability_ReportsReadCapabilityDenied()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var b = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "loan_cap_read_denied",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = x, Name = "x", TypeId = intType, IsParameter = true },
                new MirLocal { Id = b, Name = "b", TypeId = intType }
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
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var verifier = new LoanConstraintVerifier(
            new LoanSignatureCache(),
            new SymbolTable(),
            BorrowCapabilitySnapshot.Enforced());
        var results = verifier.VerifyFunction(func);

        Assert.Contains(results, result => result.Violation == LoanConstraintViolation.ReadCapabilityDenied);
        var denied = Assert.Single(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.ReadCapabilityDenied);
        Assert.Contains("命中路径:", denied.Hint ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("@borrow(read)", denied.Hint ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("source=none", denied.Hint ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyFunction_StoreWithoutWriteCapability_ReportsWriteCapabilityDenied()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };

        var func = new MirFunc
        {
            Name = "loan_cap_write_denied",
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

        var verifier = new LoanConstraintVerifier(
            new LoanSignatureCache(),
            new SymbolTable(),
            BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read));
        var results = verifier.VerifyFunction(func);

        Assert.Contains(results, result => result.Violation == LoanConstraintViolation.WriteCapabilityDenied);
        Assert.Contains(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.WriteCapabilityDenied);
    }

    [Fact]
    public void VerifyFunction_MoveWithoutMoveCapability_ReportsMoveCapabilityDenied()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var x = new LocalId { Value = 1 };
        var tmp = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "loan_cap_move_denied",
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

        var verifier = new LoanConstraintVerifier(
            new LoanSignatureCache(),
            new SymbolTable(),
            BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Write));
        var results = verifier.VerifyFunction(func);

        Assert.Contains(results, result => result.Violation == LoanConstraintViolation.MoveCapabilityDenied);
        Assert.Contains(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.MoveCapabilityDenied);
    }

    [Fact]
    public void VerifyFunction_CallOwnArgWithoutMoveCapability_ReportsMoveCapabilityDenied()
    {
        var (func, signatureCache, symbolTable) = CreateCallArgCapabilityFixture(
            "loan_call_cap_own_denied",
            new SymbolId(930),
            ParamBorrowMode.Own);

        var verifier = new LoanConstraintVerifier(
            signatureCache,
            symbolTable,
            BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Write));
        var results = verifier.VerifyFunction(func);

        Assert.Contains(results, result => result.Violation == LoanConstraintViolation.MoveCapabilityDenied);
        Assert.Contains(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.MoveCapabilityDenied);
    }

    [Fact]
    public void VerifyFunction_CallSharedArgWithoutReadCapability_ReportsReadCapabilityDenied()
    {
        var (func, signatureCache, symbolTable) = CreateCallArgCapabilityFixture(
            "loan_call_cap_shared_denied",
            new SymbolId(931),
            ParamBorrowMode.BorrowShared);

        var verifier = new LoanConstraintVerifier(
            signatureCache,
            symbolTable,
            BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Move));
        var results = verifier.VerifyFunction(func);

        Assert.Contains(results, result => result.Violation == LoanConstraintViolation.ReadCapabilityDenied);
        Assert.Contains(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.ReadCapabilityDenied);
    }

    [Fact]
    public void VerifyFunction_CallMutableArgWithoutWriteCapability_ReportsWriteCapabilityDenied()
    {
        var (func, signatureCache, symbolTable) = CreateCallArgCapabilityFixture(
            "loan_call_cap_mut_denied",
            new SymbolId(932),
            ParamBorrowMode.BorrowMutable);

        var verifier = new LoanConstraintVerifier(
            signatureCache,
            symbolTable,
            BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Move));
        var results = verifier.VerifyFunction(func);

        Assert.Contains(results, result => result.Violation == LoanConstraintViolation.WriteCapabilityDenied);
        Assert.Contains(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.WriteCapabilityDenied);
    }

    [Fact]
    public void VerifyFunction_CallOwnArgAfterCopyFromIndexedBorrowedValue_DoesNotReportNeedOwnershipButBorrowed()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(9331);
        var arr = new LocalId { Value = 1 };
        var idx = new LocalId { Value = 2 };
        var borrowed = new LocalId { Value = 3 };
        var ownedArg = new LocalId { Value = 4 };

        var function = new MirFunc
        {
            Name = "loan_call_own_after_index_copy",
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

        var verifier = new LoanConstraintVerifier(signatureCache, new SymbolTable());
        var results = verifier.VerifyFunction(function);

        Assert.DoesNotContain(results, result => result.Violation == LoanConstraintViolation.NeedOwnershipButBorrowed);
        Assert.DoesNotContain(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

    [Fact]
    public void VerifyFunction_CallOwnArgAfterCopyFromIndexedBorrowedString_ReportsNeedOwnershipButBorrowed()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(9332);
        var arr = new LocalId { Value = 1 };
        var idx = new LocalId { Value = 2 };
        var borrowed = new LocalId { Value = 3 };
        var ownedArg = new LocalId { Value = 4 };

        var function = new MirFunc
        {
            Name = "loan_call_own_after_index_copy_string",
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

        var verifier = new LoanConstraintVerifier(signatureCache, new SymbolTable());
        var results = verifier.VerifyFunction(function);

        Assert.Contains(results, result => result.Violation == LoanConstraintViolation.NeedOwnershipButBorrowed);
        Assert.Contains(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

}
