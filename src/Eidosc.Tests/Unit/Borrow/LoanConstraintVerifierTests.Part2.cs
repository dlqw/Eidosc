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
    public void VerifyFunction_CallOwnArgAfterCopyFromIndexedBorrowedTraitCopyType_DoesNotReportNeedOwnershipButBorrowed()
    {
        var symbolTable = new SymbolTable();
        var adtId = symbolTable.DeclareAdt("Boxed", Eidosc.Utils.SourceSpan.Empty);
        var boxedType = symbolTable.GetSymbol<AdtSymbol>(adtId)!.TypeId;
        var copyTrait = symbolTable.DeclareTrait("Copy", Eidosc.Utils.SourceSpan.Empty);
        symbolTable.DeclareImpl(copyTrait, boxedType, Eidosc.Utils.SourceSpan.Empty);

        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(9333);
        var arr = new LocalId { Value = 1 };
        var idx = new LocalId { Value = 2 };
        var borrowed = new LocalId { Value = 3 };
        var ownedArg = new LocalId { Value = 4 };

        var function = new MirFunc
        {
            Name = "loan_call_own_after_index_copy_trait_copy",
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

        var verifier = new LoanConstraintVerifier(signatureCache, symbolTable);
        var results = verifier.VerifyFunction(function);

        Assert.DoesNotContain(results, result => result.Violation == LoanConstraintViolation.NeedOwnershipButBorrowed);
        Assert.DoesNotContain(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

    [Fact]
    public void VerifyFunction_CallOwnArgAfterCopyFromFieldBorrowedString_ReportsNeedOwnershipButBorrowed()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var calleeSymbol = new SymbolId(9334);
        var receiver = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };
        var ownedArg = new LocalId { Value = 3 };

        var function = new MirFunc
        {
            Name = "loan_call_own_after_field_copy_string",
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

        var verifier = new LoanConstraintVerifier(signatureCache, new SymbolTable());
        var results = verifier.VerifyFunction(function);

        Assert.Contains(results, result => result.Violation == LoanConstraintViolation.NeedOwnershipButBorrowed);
        Assert.Contains(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

    [Fact]
    public void VerifyFunction_CallOwnArgAfterCopyFromDerefBorrowedTraitCopyType_DoesNotReportNeedOwnershipButBorrowed()
    {
        var symbolTable = new SymbolTable();
        var adtId = symbolTable.DeclareAdt("Packet", Eidosc.Utils.SourceSpan.Empty);
        var packetType = symbolTable.GetSymbol<AdtSymbol>(adtId)!.TypeId;
        var copyTrait = symbolTable.DeclareTrait("Copy", Eidosc.Utils.SourceSpan.Empty);
        symbolTable.DeclareImpl(copyTrait, packetType, Eidosc.Utils.SourceSpan.Empty);

        var calleeSymbol = new SymbolId(9335);
        var pointer = new LocalId { Value = 1 };
        var borrowed = new LocalId { Value = 2 };
        var ownedArg = new LocalId { Value = 3 };

        var function = new MirFunc
        {
            Name = "loan_call_own_after_deref_copy_trait_copy",
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

        var verifier = new LoanConstraintVerifier(signatureCache, symbolTable);
        var results = verifier.VerifyFunction(function);

        Assert.DoesNotContain(results, result => result.Violation == LoanConstraintViolation.NeedOwnershipButBorrowed);
        Assert.DoesNotContain(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.MutateWhileBorrowed);
    }

    [Fact]
    public void VerifyFunction_CallMutableDerefArgWithoutWriteCapability_ReportsWriteCapabilityDenied()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(934);
        var p = new LocalId { Value = 1 };

        var func = new MirFunc
        {
            Name = "loan_call_cap_mut_deref_denied",
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
                    Lifetime = new LifetimeId { Value = 1 }
                }
            ]
        });

        var verifier = new LoanConstraintVerifier(
            signatureCache,
            new SymbolTable(),
            BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Move));
        var results = verifier.VerifyFunction(func);

        Assert.Contains(results, result => result.Violation == LoanConstraintViolation.WriteCapabilityDenied);
        Assert.Contains(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.WriteCapabilityDenied);
    }

    [Fact]
    public void VerifyFunction_CallOwnDerefArgWithTargetMoveCapability_DoesNotReportMoveCapabilityDenied()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(936);
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
            Name = "loan_call_cap_own_deref_target_move_granted",
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

        var snapshot = BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Write);
        snapshot.GrantLocal(p, BorrowCapabilityKind.Read);
        snapshot.GrantTarget(derefTarget, BorrowCapabilityKind.Move);

        var verifier = new LoanConstraintVerifier(
            signatureCache,
            new SymbolTable(),
            snapshot);
        var results = verifier.VerifyFunction(func);

        Assert.DoesNotContain(results, result => result.Violation == LoanConstraintViolation.MoveCapabilityDenied);
        Assert.DoesNotContain(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.MoveCapabilityDenied);

        var statesText = BorrowFormatter.FormatLoanConstraintStates(verifier);
        Assert.Contains("// capability resolution order: target -> local -> global", statesText, StringComparison.Ordinal);
        Assert.Contains("// capability locals:", statesText);
        Assert.Contains($"//   %{p.Value}: read", statesText);
        Assert.Contains("// capability targets:", statesText);
        Assert.Contains($"//   {derefTarget.StableKey}: move [source=target]", statesText, StringComparison.Ordinal);
    }

    [Fact]
    public void VerifyFunction_CallReturnedMutableBorrowWithoutWriteCapability_ReportsWriteCapabilityDenied()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var calleeSymbol = new SymbolId(935);
        var x = new LocalId { Value = 1 };
        var borrowResult = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "loan_call_cap_return_mut_denied",
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
                    Lifetime = new LifetimeId { Value = 1 }
                }
            ],
            ReturnConstraint = new ReturnBorrowConstraint
            {
                IsBorrow = true,
                IsMutable = true,
                Lifetime = new LifetimeId { Value = 1 },
                BoundToParams = [0]
            }
        });

        var verifier = new LoanConstraintVerifier(
            signatureCache,
            new SymbolTable(),
            BorrowCapabilitySnapshot.Enforced(BorrowCapabilityKind.Read, BorrowCapabilityKind.Move));
        var results = verifier.VerifyFunction(func);

        Assert.Contains(results, result => result.Violation == LoanConstraintViolation.WriteCapabilityDenied);
        Assert.Contains(verifier.Diagnostics, diagnostic => diagnostic.Kind == BorrowErrorKind.WriteCapabilityDenied);
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
                    Lifetime = new LifetimeId { Value = 1 }
                }
            ]
        };

        var signatureCache = new LoanSignatureCache();
        signatureCache.SetSignature(calleeSymbol, signature);
        var symbolTable = new SymbolTable();

        return (function, signatureCache, symbolTable);
    }
}
