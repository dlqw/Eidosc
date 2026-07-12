using Eidosc.Mir;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed partial class MirValidatorTests
{
    [Fact]
    public void Validate_PoisonInstructionOperand_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = intType,
                    EntryBlockId = new BlockId { Value = 1 },
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
                                    Target = new MirPlace
                                    {
                                        Kind = PlaceKind.Local,
                                        Local = new LocalId { Value = 1 },
                                        TypeId = intType
                                    },
                                    Source = new MirPoison
                                    {
                                        TypeId = TypeId.None,
                                        Reason = "test poison"
                                    }
                                }
                            ],
                            Terminator = new MirReturn
                            {
                                Value = new MirPlace
                                {
                                    Kind = PlaceKind.Local,
                                    Local = new LocalId { Value = 1 },
                                    TypeId = intType
                                }
                            }
                        }
                    ]
                }
            ]
        };

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.PoisonOperandCode, diagnostic.Code);
        Assert.Contains(diagnostic.Notes, note => note.Contains("function: main", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("reason: test poison", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CleanModule_Succeeds()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = intType,
                    EntryBlockId = new BlockId { Value = 1 },
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Terminator = new MirReturn
                            {
                                Value = new MirConstant
                                {
                                    TypeId = intType,
                                    Value = new MirConstantValue.IntValue(0)
                                }
                            }
                        }
                    ]
                }
            ]
        };

        var validator = new MirValidator();

        Assert.True(validator.Validate(module));
        Assert.Empty(validator.Diagnostics);
    }

    [Fact]
    public void Validate_UnsupportedInstruction_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [new UnsupportedInstruction()],
            new MirReturn
            {
                Value = new MirConstant
                {
                    TypeId = intType,
                    Value = new MirConstantValue.IntValue(0)
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR instruction", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("function: main", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnsupportedTerminator_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            new UnsupportedTerminator());

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR terminator", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("function: main", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnsupportedBinOpTargetOperand_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var invalidTarget = new MirConstant
        {
            TypeId = intType,
            Value = new MirConstantValue.IntValue(0)
        };
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirBinOp
                {
                    Target = invalidTarget,
                    Operator = BinaryOp.Add,
                    Left = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(1)
                    },
                    Right = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(2)
                    }
                }
            ],
            new MirReturn
            {
                Value = new MirConstant
                {
                    TypeId = intType,
                    Value = new MirConstantValue.IntValue(0)
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR target operand", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("MirConstant", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnsupportedUnaryOpTargetOperand_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var invalidTarget = new MirConstant
        {
            TypeId = intType,
            Value = new MirConstantValue.IntValue(0)
        };
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirUnaryOp
                {
                    Target = invalidTarget,
                    Operator = UnaryOp.Neg,
                    Operand = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(1)
                    }
                }
            ],
            new MirReturn
            {
                Value = new MirConstant
                {
                    TypeId = intType,
                    Value = new MirConstantValue.IntValue(0)
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR target operand", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("MirConstant", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_BinOpWithNonLocalPlaceTarget_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirBinOp
                {
                    Target = CreateFieldPlace(intType),
                    Operator = BinaryOp.Add,
                    Left = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(1)
                    },
                    Right = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(2)
                    }
                }
            ],
            CreateReturnZero(intType));

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR binary operation target place", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Field", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnaryOpWithNonLocalPlaceTarget_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirUnaryOp
                {
                    Target = CreateFieldPlace(intType),
                    Operator = UnaryOp.Neg,
                    Operand = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(1)
                    }
                }
            ],
            CreateReturnZero(intType));

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR unary operation target place", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Field", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AssignWithNonLocalTarget_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirAssign
                {
                    Target = CreateFieldPlace(intType),
                    Source = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(1)
                    }
                }
            ],
            CreateReturnZero(intType));

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR assign target place", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Field", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnsupportedPlaceKind_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            new MirReturn
            {
                Value = new MirPlace
                {
                    Kind = (PlaceKind)999,
                    Local = new LocalId { Value = 1 },
                    TypeId = intType
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR place kind", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("999", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnsupportedIndexAccessKind_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            new MirReturn
            {
                Value = new MirPlace
                {
                    Kind = PlaceKind.Index,
                    IndexAccessKind = (MirIndexAccessKind)999,
                    Base = new MirPlace
                    {
                        Kind = PlaceKind.Local,
                        Local = new LocalId { Value = 1 },
                        TypeId = intType
                    },
                    Index = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(0)
                    },
                    TypeId = intType
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR index access kind", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("999", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_LocalPlaceWithoutLocalId_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            new MirReturn
            {
                Value = new MirPlace
                {
                    Kind = PlaceKind.Local,
                    Local = LocalId.None,
                    TypeId = intType
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR local place id", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("missing", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FieldPlaceWithoutBase_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            new MirReturn
            {
                Value = new MirPlace
                {
                    Kind = PlaceKind.Field,
                    FieldName = "0",
                    TypeId = intType
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR field place base", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("missing", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_IndexPlaceWithoutIndexOperand_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            new MirReturn
            {
                Value = new MirPlace
                {
                    Kind = PlaceKind.Index,
                    Base = new MirPlace
                    {
                        Kind = PlaceKind.Local,
                        Local = new LocalId { Value = 1 },
                        TypeId = intType
                    },
                    TypeId = intType
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR index place operand", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("missing", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_DerefPlaceWithoutBase_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            new MirReturn
            {
                Value = new MirPlace
                {
                    Kind = PlaceKind.Deref,
                    TypeId = intType
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR deref place base", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("missing", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_MissingTerminator_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "main",
                    ReturnType = intType,
                    EntryBlockId = new BlockId { Value = 1 },
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true
                        }
                    ]
                }
            ]
        };

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.MissingTerminatorCode, diagnostic.Code);
        Assert.Contains("without a terminator", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("function: main", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("must not synthesize default control flow", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_FunctionWithMissingEntryBlock_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            CreateReturnZero(intType));
        module.Functions[0] = new MirFunc
        {
            Name = "main",
            ReturnType = intType,
            EntryBlockId = new BlockId { Value = 99 },
            BasicBlocks = module.Functions[0].BasicBlocks
        };

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR entry block", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("bb99", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("target: bb99", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("invalid control-flow targets before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_GotoWithMissingTargetBlock_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            new MirGoto { Target = new BlockId { Value = 99 } });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR goto target block", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("bb99", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("mir: bb1:terminator", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("invalid control-flow targets before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_SwitchWithMissingBranchTarget_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            new MirSwitch
            {
                Discriminant = new MirConstant
                {
                    TypeId = intType,
                    Value = new MirConstantValue.IntValue(1)
                },
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(1)
                        },
                        Target = new BlockId { Value = 99 }
                    }
                ],
                DefaultTarget = new BlockId { Value = 1 }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR switch branch 0 target block", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("bb99", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("invalid control-flow targets before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_SwitchWithMissingDefaultTarget_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            new MirSwitch
            {
                Discriminant = new MirConstant
                {
                    TypeId = intType,
                    Value = new MirConstantValue.IntValue(1)
                },
                Branches =
                [
                    new MirSwitchBranch
                    {
                        Value = new MirConstant
                        {
                            TypeId = intType,
                            Value = new MirConstantValue.IntValue(1)
                        },
                        Target = new BlockId { Value = 1 }
                    }
                ],
                DefaultTarget = new BlockId { Value = 99 }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR switch default target block", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("bb99", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("invalid control-flow targets before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_UnknownValidTypeId_ReportsBackendBoundaryDiagnostic()
    {
        var unknownType = new TypeId(900_900);
        var module = CreateSingleBlockModule(
            unknownType,
            [],
            new MirReturn
            {
                Value = new MirConstant
                {
                    TypeId = unknownType,
                    Value = new MirConstantValue.IntValue(0)
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        Assert.Contains(
            validator.Diagnostics,
            diagnostic => diagnostic.Code == MirValidator.UnknownTypeIdCode &&
                          diagnostic.Message.Contains("Unknown MIR TypeId", StringComparison.Ordinal) &&
                          diagnostic.Notes.Contains("role: function return type"));
    }

    [Fact]
    public void Validate_MissingFunctionReturnType_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            TypeId.None,
            [],
            new MirReturn
            {
                Value = new MirConstant
                {
                    TypeId = intType,
                    Value = new MirConstantValue.IntValue(0)
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnknownTypeIdCode, diagnostic.Code);
        Assert.Contains("Missing MIR TypeId", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("role: function return type", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("concrete return type", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ConcreteOperandWithoutType_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            new MirReturn
            {
                Value = new MirConstant
                {
                    TypeId = TypeId.None,
                    Value = new MirConstantValue.IntValue(0)
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnknownTypeIdCode, diagnostic.Code);
        Assert.Contains("Missing MIR TypeId", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("role: terminator operand", StringComparison.Ordinal));
        Assert.Contains(
            diagnostic.Notes,
            note => note.Contains("generic/partial callable placeholders", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_ConcreteLocalWithoutType_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [],
            CreateReturnZero(intType));
        module.Functions[0].Locals.Add(new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "value",
            TypeId = TypeId.None
        });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnknownTypeIdCode, diagnostic.Code);
        Assert.Contains("Missing MIR TypeId", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("role: local 'value' type", StringComparison.Ordinal));
        Assert.Contains(
            diagnostic.Notes,
            note => note.Contains("generic/partial callable placeholders", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_GenericPartialCallablePlaceholders_AllowsTypeErasedMetadata()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var genericSymbol = new SymbolId { Value = 100 };
        var partialLocal = new LocalId { Value = 1 };
        var module = new MirModule
        {
            Name = "Main",
            Functions =
            [
                new MirFunc
                {
                    Name = "id",
                    SymbolId = genericSymbol,
                    GenericParameterCount = 1,
                    ReturnType = TypeId.None,
                    EntryBlockId = new BlockId { Value = 1 },
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Terminator = new MirUnreachable()
                        }
                    ]
                },
                new MirFunc
                {
                    Name = "main",
                    ReturnType = intType,
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal
                        {
                            Id = partialLocal,
                            Name = "partial",
                            TypeId = TypeId.None
                        }
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
                                    Target = new MirPlace
                                    {
                                        Kind = PlaceKind.Local,
                                        Local = partialLocal,
                                        TypeId = TypeId.None
                                    },
                                    Function = new MirFunctionRef
                                    {
                                        Name = "id",
                                        SymbolId = genericSymbol,
                                        TypeId = TypeId.None
                                    },
                                    Arguments = []
                                }
                            ],
                            Terminator = CreateReturnZero(intType)
                        }
                    ]
                }
            ]
        };

        var validator = new MirValidator();

        Assert.True(validator.Validate(module));
        Assert.Empty(validator.Diagnostics);
    }

    [Fact]
    public void Validate_TypeDescriptorBackedTypeId_Succeeds()
    {
        var tupleType = new TypeId(900_901);
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            tupleType,
            [],
            new MirReturn
            {
                Value = new MirPlace
                {
                    Kind = PlaceKind.Local,
                    Local = new LocalId { Value = 1 },
                    TypeId = tupleType
                }
            });
        module.TypeDescriptors[tupleType.Value] = new TypeDescriptor.Tuple([intType]);
        module.Functions[0].Locals.Add(new MirLocal
        {
            Id = new LocalId { Value = 1 },
            Name = "value",
            TypeId = tupleType
        });

        var validator = new MirValidator();

        Assert.True(validator.Validate(module));
        Assert.Empty(validator.Diagnostics);
    }

    [Fact]
    public void Validate_AllocWithoutType_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirAlloc
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Local,
                        Local = new LocalId { Value = 1 },
                        TypeId = intType
                    },
                    TypeId = TypeId.None
                }
            ],
            new MirReturn
            {
                Value = new MirConstant
                {
                    TypeId = intType,
                    Value = new MirConstantValue.IntValue(0)
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnknownTypeIdCode, diagnostic.Code);
        Assert.Contains("Missing MIR TypeId", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("role: alloc type", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("concrete allocation type", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_AllocWithNonLocalTarget_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirAlloc
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = new MirPlace
                        {
                            Kind = PlaceKind.Local,
                            Local = new LocalId { Value = 1 },
                            TypeId = intType
                        },
                        FieldName = "0",
                        TypeId = intType
                    },
                    TypeId = intType
                }
            ],
            new MirReturn
            {
                Value = new MirConstant
                {
                    TypeId = intType,
                    Value = new MirConstantValue.IntValue(0)
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR alloc target place", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Field", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_LoadWithNonLocalTarget_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirLoad
                {
                    Target = new MirPlace
                    {
                        Kind = PlaceKind.Field,
                        Base = new MirPlace
                        {
                            Kind = PlaceKind.Local,
                            Local = new LocalId { Value = 1 },
                            TypeId = intType
                        },
                        FieldName = "0",
                        TypeId = intType
                    },
                    Source = new MirConstant
                    {
                        TypeId = intType,
                        Value = new MirConstantValue.IntValue(1)
                    }
                }
            ],
            new MirReturn
            {
                Value = new MirConstant
                {
                    TypeId = intType,
                    Value = new MirConstantValue.IntValue(0)
                }
            });

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR load target place", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Field", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CallWithNonLocalTarget_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirCall
                {
                    Target = CreateFieldPlace(intType),
                    Function = new MirFunctionRef
                    {
                        Name = "callee",
                        FunctionId = new FunctionId
                        {
                            Name = "callee",
                            QualifiedName = "test:callee"
                        },
                        TypeId = intType
                    }
                }
            ],
            CreateReturnZero(intType));

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR call target place", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Field", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CopyWithNonLocalTarget_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirCopy
                {
                    Target = CreateFieldPlace(intType),
                    Source = CreateLocalPlace(2, intType)
                }
            ],
            CreateReturnZero(intType));

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR copy target place", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Field", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

    [Fact]
    public void Validate_CopyWithNonLocalSource_ReportsBackendBoundaryDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = CreateSingleBlockModule(
            intType,
            [
                new MirCopy
                {
                    Target = CreateLocalPlace(2, intType),
                    Source = CreateFieldPlace(intType)
                }
            ],
            CreateReturnZero(intType));

        var validator = new MirValidator();

        Assert.False(validator.Validate(module));
        var diagnostic = Assert.Single(validator.Diagnostics);
        Assert.Equal(MirValidator.UnsupportedMirNodeCode, diagnostic.Code);
        Assert.Contains("Unsupported MIR copy source place", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("Field", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("before LLVM lowering", StringComparison.Ordinal));
    }

}
