using Eidosc.Symbols;
using Eidosc;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;

using LifetimeId = Eidosc.Borrow.LifetimeId;

namespace Eidosc.Tests.Unit.Borrow;

public class LoanSignatureInfererTests
{
    [Fact]
    public void Infer_CallBorrowConstraints_DoNotRewriteOwnedSignature()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var calleeSymbol = new SymbolId(400);
        var callerSymbol = new SymbolId(401);

        var calleeLifetimeA = new LifetimeId { Value = 1 };
        var calleeLifetimeB = new LifetimeId { Value = 2 };

        var calleeSignature = new LoanSignature
        {
            FunctionName = "callee",
            FunctionSymbol = calleeSymbol,
            ParamRequirements =
            [
                new ParamBorrowRequirement
                {
                    ParamIndex = 0,
                    Name = "x",
                    Mode = ParamBorrowMode.BorrowShared,
                    Lifetime = calleeLifetimeA
                },
                new ParamBorrowRequirement
                {
                    ParamIndex = 1,
                    Name = "y",
                    Mode = ParamBorrowMode.BorrowShared,
                    Lifetime = calleeLifetimeB
                }
            ],
            ReturnConstraint = new ReturnBorrowConstraint
            {
                IsBorrow = false
            },
            LifetimeConstraints =
            [
                new LifetimeConstraint
                {
                    Sub = calleeLifetimeA,
                    Sup = calleeLifetimeB
                }
            ]
        };

        var cache = new LoanSignatureCache();
        cache.SetSignature(calleeSymbol, calleeSignature);

        var paramA = new LocalId { Value = 1 };
        var paramB = new LocalId { Value = 2 };

        var caller = new MirFunc
        {
            Name = "caller",
            SymbolId = callerSymbol,
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = paramA, Name = "a", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = paramB, Name = "b", TypeId = stringType, IsParameter = true }
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
                            Arguments =
                            [
                                new MirPlace { Kind = PlaceKind.Local, Local = paramA, TypeId = stringType },
                                new MirPlace { Kind = PlaceKind.Local, Local = paramB, TypeId = stringType }
                            ]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(caller, cache, new SymbolTable());
        var signature = inferer.Infer(includeCallConstraints: true, force: true);

        var param0 = Assert.Single(signature.ParamRequirements, r => r.ParamIndex == 0);
        var param1 = Assert.Single(signature.ParamRequirements, r => r.ParamIndex == 1);

        Assert.Equal(ParamBorrowMode.Own, param0.Mode);
        Assert.Equal(ParamBorrowMode.Own, param1.Mode);
        Assert.All(
            signature.OwnershipContract.Parameters,
            static parameter => Assert.Equal(OwnershipPassingKind.ByValue, parameter.Projection.Kind));

        Assert.DoesNotContain(
            signature.LifetimeConstraints,
            c => c.Sub.Equals(param0.Lifetime) && c.Sup.Equals(param1.Lifetime));
    }

    [Fact]
    public void Infer_UserTypeWithCopyImpl_ParamModeIsCopy()
    {
        var symbolTable = new SymbolTable();
        var copyTraitId = symbolTable.DeclareTrait("Copy", SourceSpan.Empty);
        var tokenTypeSymbolId = symbolTable.DeclareAdt("Token", SourceSpan.Empty);
        var tokenTypeSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(tokenTypeSymbolId));

        Assert.True(tokenTypeSymbol.TypeId.IsValid);
        symbolTable.DeclareImpl(copyTraitId, tokenTypeSymbol.TypeId, SourceSpan.Empty);

        var param = new LocalId { Value = 1 };
        var func = new MirFunc
        {
            Name = "copy_param",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = param,
                    Name = "x",
                    TypeId = tokenTypeSymbol.TypeId,
                    IsParameter = true
                }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(func, new LoanSignatureCache(), symbolTable);
        var signature = inferer.Infer(force: true);
        var requirement = Assert.Single(signature.ParamRequirements);

        Assert.Equal(ParamBorrowMode.Copy, requirement.Mode);
        Assert.Equal(
            OwnershipPassingKind.ByValue,
            signature.OwnershipContract.GetParameter(0).Projection.Kind);
    }

    [Fact]
    public void Infer_UserTypeWithoutCopyImpl_ParamModeFallsBackToOwn()
    {
        var symbolTable = new SymbolTable();
        var tokenTypeSymbolId = symbolTable.DeclareAdt("TokenNoCopy", SourceSpan.Empty);
        var tokenTypeSymbol = Assert.IsType<AdtSymbol>(symbolTable.GetSymbol(tokenTypeSymbolId));

        var param = new LocalId { Value = 1 };
        var func = new MirFunc
        {
            Name = "own_param",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = param,
                    Name = "x",
                    TypeId = tokenTypeSymbol.TypeId,
                    IsParameter = true
                }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(func, new LoanSignatureCache(), symbolTable);
        var signature = inferer.Infer(force: true);
        var requirement = Assert.Single(signature.ParamRequirements);

        Assert.Equal(ParamBorrowMode.Own, requirement.Mode);
        Assert.Equal(
            OwnershipPassingKind.ByValue,
            signature.OwnershipContract.GetParameter(0).Projection.Kind);
    }

    [Fact]
    public void Infer_RefTypedParam_ParamModeIsBorrowShared()
    {
        var refIntType = new TypeId(9101);
        var dynamicTypeKeys = new Dictionary<int, string>
        {
            [refIntType.Value] = $"Ref({BaseTypes.IntId})"
        };
        var param = new LocalId { Value = 1 };
        var func = new MirFunc
        {
            Name = "ref_param",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = param,
                    Name = "r",
                    TypeId = refIntType,
                    IsParameter = true
                }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(
            func,
            new LoanSignatureCache(),
            new SymbolTable(),
            dynamicTypeKeys,
            typeDescriptors: ParseTypeDescriptors(dynamicTypeKeys));
        var signature = inferer.Infer(force: true);
        var requirement = Assert.Single(signature.ParamRequirements);

        Assert.Equal(ParamBorrowMode.BorrowShared, requirement.Mode);
        Assert.True(requirement.Lifetime.IsValid);
        Assert.Equal(
            OwnershipPassingKind.SharedBorrow,
            signature.OwnershipContract.GetParameter(0).Projection.Kind);
        Assert.Empty(signature.ReturnConstraint.InternalNotes);
    }

    [Fact]
    public void Infer_MRefTypedParam_ParamModeIsBorrowMutable()
    {
        var mrefIntType = new TypeId(9102);
        var dynamicTypeKeys = new Dictionary<int, string>
        {
            [mrefIntType.Value] = $"MRef({BaseTypes.IntId})"
        };
        var param = new LocalId { Value = 1 };
        var func = new MirFunc
        {
            Name = "mref_param",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal
                {
                    Id = param,
                    Name = "target",
                    TypeId = mrefIntType,
                    IsParameter = true
                }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(
            func,
            new LoanSignatureCache(),
            new SymbolTable(),
            dynamicTypeKeys,
            typeDescriptors: ParseTypeDescriptors(dynamicTypeKeys));
        var signature = inferer.Infer(force: true);
        var requirement = Assert.Single(signature.ParamRequirements);

        Assert.Equal(ParamBorrowMode.BorrowMutable, requirement.Mode);
        Assert.True(requirement.Lifetime.IsValid);
        Assert.Equal(
            OwnershipPassingKind.MutableBorrow,
            signature.OwnershipContract.GetParameter(0).Projection.Kind);
        Assert.Empty(signature.ReturnConstraint.InternalNotes);
    }

    [Fact]
    public void Infer_ReturnByValueBranchJoin_DoesNotBindToParams()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var stringType = new TypeId(BaseTypes.StringId);
        var cond = new LocalId { Value = 1 };
        var paramA = new LocalId { Value = 2 };
        var paramB = new LocalId { Value = 3 };
        var temp = new LocalId { Value = 4 };
        var join = new BlockId { Value = 4 };

        var func = new MirFunc
        {
            Name = "branch_join_return",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = cond, Name = "cond", TypeId = intType, IsParameter = true },
                new MirLocal { Id = paramA, Name = "a", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = paramB, Name = "b", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = temp, Name = "t", TypeId = stringType }
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
                        new MirMove
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = paramA, TypeId = stringType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = temp, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirGoto { Target = join }
                },
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 3 },
                    Instructions =
                    [
                        new MirMove
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = paramB, TypeId = stringType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = temp, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirGoto { Target = join }
                },
                new MirBasicBlock
                {
                    Id = join,
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = temp, TypeId = stringType }
                    }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(func, new LoanSignatureCache(), new SymbolTable());
        var signature = inferer.Infer(force: true);

        Assert.False(signature.ReturnConstraint.IsBorrow);
        Assert.Empty(signature.ReturnConstraint.BoundToParams);
    }

    [Fact]
    public void Infer_ReturnValueLoadFromParam_DoesNotInventBorrowContract()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var param = new LocalId { Value = 1 };
        var temp = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "borrow_return",
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = param, Name = "x", TypeId = stringType, IsParameter = true },
                new MirLocal { Id = temp, Name = "t", TypeId = stringType }
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
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = param, TypeId = stringType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = temp, TypeId = stringType }
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = temp, TypeId = stringType }
                    }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(func, new LoanSignatureCache(), new SymbolTable());
        var signature = inferer.Infer(force: true);

        Assert.False(signature.ReturnConstraint.IsBorrow);
        Assert.Empty(signature.ReturnConstraint.BoundToParams);
        Assert.Equal(LoanInferenceConfidence.High, signature.ReturnConstraint.Confidence);
    }

    [Fact]
    public void Infer_ReturnDirectRefParam_BindsToSourceParam()
    {
        var refStringType = new TypeId(9001);
        var dynamicTypeKeys = new Dictionary<int, string>
        {
            [refStringType.Value] = $"Ref({BaseTypes.StringId})"
        };
        var param = new LocalId { Value = 1 };

        var func = new MirFunc
        {
            Name = "borrow_return_param",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = refStringType,
            Locals =
            [
                new MirLocal { Id = param, Name = "r", TypeId = refStringType, IsParameter = true }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = param, TypeId = refStringType }
                    }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(func, new LoanSignatureCache(), new SymbolTable(), dynamicTypeKeys);
        var signature = inferer.Infer(force: true);

        Assert.True(signature.ReturnConstraint.IsBorrow);
        Assert.Equal([0], signature.ReturnConstraint.BoundToParams);
        Assert.Equal(LoanInferenceConfidence.High, signature.ReturnConstraint.Confidence);
        Assert.Empty(signature.ReturnConstraint.InternalNotes);
        Assert.Empty(inferer.Diagnostics);
    }

    [Fact]
    public void Infer_ReturnBorrowFromFieldProjectionParam_BindsToSourceParam()
    {
        var boxType = new TypeId(9002);
        var refBoxType = new TypeId(9003);
        var refIntType = new TypeId(9004);
        var dynamicTypeKeys = new Dictionary<int, string>
        {
            [refBoxType.Value] = $"Ref({boxType.Value})",
            [refIntType.Value] = $"Ref({BaseTypes.IntId})"
        };
        var param = new LocalId { Value = 1 };

        var func = new MirFunc
        {
            Name = "borrow_return_field_projection",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = refIntType,
            Locals =
            [
                new MirLocal { Id = param, Name = "box", TypeId = refBoxType, IsParameter = true }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace
                        {
                            Kind = PlaceKind.Field,
                            Base = new MirPlace
                            {
                                Kind = PlaceKind.Deref,
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = param, TypeId = refBoxType },
                                TypeId = boxType
                            },
                            FieldName = "_0",
                            TypeId = refIntType
                        }
                    }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(func, new LoanSignatureCache(), new SymbolTable(), dynamicTypeKeys);
        var signature = inferer.Infer(force: true);

        Assert.True(signature.ReturnConstraint.IsBorrow);
        Assert.Equal([0], signature.ReturnConstraint.BoundToParams);
        Assert.Empty(inferer.Diagnostics);
    }

    [Fact]
    public void Infer_ReturnBorrowFromIndexProjectionParam_BindsToSourceParam()
    {
        var listType = new TypeId(9005);
        var mrefListType = new TypeId(9006);
        var mrefIntType = new TypeId(9007);
        var dynamicTypeKeys = new Dictionary<int, string>
        {
            [mrefListType.Value] = $"MRef({listType.Value})",
            [mrefIntType.Value] = $"MRef({BaseTypes.IntId})"
        };
        var param = new LocalId { Value = 1 };

        var func = new MirFunc
        {
            Name = "borrow_return_index_projection",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = mrefIntType,
            Locals =
            [
                new MirLocal { Id = param, Name = "xs", TypeId = mrefListType, IsParameter = true }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace
                        {
                            Kind = PlaceKind.Index,
                            Base = new MirPlace
                            {
                                Kind = PlaceKind.Deref,
                                Base = new MirPlace { Kind = PlaceKind.Local, Local = param, TypeId = mrefListType },
                                TypeId = listType
                            },
                            Index = new MirConstant
                            {
                                Value = new MirConstantValue.IntValue(0),
                                TypeId = new TypeId(BaseTypes.IntId)
                            },
                            IndexAccessKind = MirIndexAccessKind.RuntimeArray,
                            TypeId = mrefIntType
                        }
                    }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(func, new LoanSignatureCache(), new SymbolTable(), dynamicTypeKeys);
        var signature = inferer.Infer(force: true);

        Assert.True(signature.ReturnConstraint.IsBorrow);
        Assert.Equal([0], signature.ReturnConstraint.BoundToParams);
        Assert.Empty(inferer.Diagnostics);
    }

    [Fact]
    public void Infer_ReturnLocalAliasOfDirectRefParam_BindsToSourceParam()
    {
        var refIntType = new TypeId(9009);
        var dynamicTypeKeys = new Dictionary<int, string>
        {
            [refIntType.Value] = $"Ref({BaseTypes.IntId})"
        };
        var param = new LocalId { Value = 1 };
        var alias = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "borrow_return_param_alias",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = refIntType,
            Locals =
            [
                new MirLocal { Id = param, Name = "r", TypeId = refIntType, IsParameter = true },
                new MirLocal { Id = alias, Name = "alias", TypeId = refIntType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCopy
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = param, TypeId = refIntType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = alias, TypeId = refIntType }
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = alias, TypeId = refIntType }
                    }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(func, new LoanSignatureCache(), new SymbolTable(), dynamicTypeKeys);
        var signature = inferer.Infer(force: true);

        Assert.True(signature.ReturnConstraint.IsBorrow);
        Assert.Equal([0], signature.ReturnConstraint.BoundToParams);
        Assert.Empty(inferer.Diagnostics);
    }

    [Fact]
    public void Infer_ReturnOverwrittenAliasOfDirectRefParam_ReportsEscapeDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var refIntType = new TypeId(9010);
        var dynamicTypeKeys = new Dictionary<int, string>
        {
            [refIntType.Value] = $"Ref({BaseTypes.IntId})"
        };
        var param = new LocalId { Value = 1 };
        var local = new LocalId { Value = 2 };
        var alias = new LocalId { Value = 3 };

        var func = new MirFunc
        {
            Name = "borrow_return_overwritten_param_alias",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = refIntType,
            Locals =
            [
                new MirLocal { Id = param, Name = "r", TypeId = refIntType, IsParameter = true },
                new MirLocal { Id = local, Name = "x", TypeId = intType },
                new MirLocal { Id = alias, Name = "alias", TypeId = refIntType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCopy
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = param, TypeId = refIntType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = alias, TypeId = refIntType }
                        },
                        new MirLoad
                        {
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = local, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = alias, TypeId = refIntType },
                            CreatesBorrowAlias = true
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = alias, TypeId = refIntType }
                    }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(func, new LoanSignatureCache(), new SymbolTable(), dynamicTypeKeys);
        var signature = inferer.Infer(force: true);
        var diagnostic = Assert.Single(inferer.Diagnostics);

        Assert.True(signature.ReturnConstraint.IsBorrow);
        Assert.Equal(BorrowErrorKind.BorrowedWhileReturned, diagnostic.Kind);
        Assert.Contains("返回借用必须直接来自输入参数", diagnostic.Message);
    }

    [Fact]
    public void Infer_ReturnBorrowFromLocalTemp_ReportsEscapeDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var refIntType = new TypeId(9008);
        var dynamicTypeKeys = new Dictionary<int, string>
        {
            [refIntType.Value] = $"Ref({BaseTypes.IntId})"
        };
        var local = new LocalId { Value = 1 };
        var temp = new LocalId { Value = 2 };

        var func = new MirFunc
        {
            Name = "borrow_return_local_temp",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = refIntType,
            Locals =
            [
                new MirLocal { Id = local, Name = "x", TypeId = intType },
                new MirLocal { Id = temp, Name = "r", TypeId = refIntType }
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
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = local, TypeId = intType },
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = temp, TypeId = refIntType },
                            CreatesBorrowAlias = true
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = temp, TypeId = refIntType }
                    }
                }
            ]
        };

        var inferer = new LoanSignatureInferer(func, new LoanSignatureCache(), new SymbolTable(), dynamicTypeKeys);
        var signature = inferer.Infer(force: true);
        var diagnostic = Assert.Single(inferer.Diagnostics);

        Assert.True(signature.ReturnConstraint.IsBorrow);
        Assert.Equal(BorrowErrorKind.BorrowedWhileReturned, diagnostic.Kind);
        Assert.Contains("返回借用必须直接来自输入参数", diagnostic.Message);
    }

    private static IReadOnlyDictionary<int, TypeDescriptor> ParseTypeDescriptors(
        IReadOnlyDictionary<int, string> dynamicTypeKeys)
    {
        var descriptors = new Dictionary<int, TypeDescriptor>();
        foreach (var (typeId, typeKey) in dynamicTypeKeys)
        {
            Assert.True(TypeKeyParsing.TryParseTypeDescriptor(typeKey, out var descriptor));
            descriptors[typeId] = descriptor;
        }

        return descriptors;
    }
}
