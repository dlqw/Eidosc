using Eidosc;
using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirBuilderTests
{
    [Fact]
    public void Build_UnsupportedHirExpression_ReportsMirLoweringDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "unsupported",
                    ReturnType = intType,
                    Body = new UnsupportedHirExpr
                    {
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Single(mirModule.Functions);
        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5330" &&
                          diagnostic.Message?.Contains("UnsupportedHirExpr", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        Assert.IsType<MirPoison>(returnTerminator.Value);
    }

    [Fact]
    public void Build_HirErrorExpression_ReportsMirPoisonDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "hir_error",
                    ReturnType = intType,
                    Body = new HirError
                    {
                        TypeId = intType,
                        Reason = "synthetic recovery node"
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5331" &&
                          diagnostic.Message?.Contains("synthetic recovery node", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        var poison = Assert.IsType<MirPoison>(returnTerminator.Value);
        Assert.Equal("synthetic recovery node", poison.Reason);
    }

    [Fact]
    public void Build_UnsupportedHirStatement_PoisonPropagatesThroughBlock()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "unsupported_statement",
                    ReturnType = intType,
                    Body = new HirBlock
                    {
                        TypeId = intType,
                        Statements =
                        [
                            new UnsupportedHirStatement()
                        ],
                        Result = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        }
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5330" &&
                          diagnostic.Message?.Contains("UnsupportedHirStatement", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        var poison = Assert.IsType<MirPoison>(returnTerminator.Value);
        Assert.Contains("block contains poisoned statement", poison.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnsupportedHirDeclaration_PoisonPropagatesThroughBlock()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "unsupported_declaration",
                    ReturnType = intType,
                    Body = new HirBlock
                    {
                        TypeId = intType,
                        Statements =
                        [
                            new HirDeclStatement
                            {
                                Declaration = new UnsupportedHirDecl
                                {
                                    TypeId = intType
                                }
                            }
                        ],
                        Result = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        }
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5330" &&
                          diagnostic.Message?.Contains("UnsupportedHirDecl", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        var poison = Assert.IsType<MirPoison>(returnTerminator.Value);
        Assert.Contains("block contains poisoned statement", poison.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnresolvedHirVariable_ReturnsPoisonInsteadOfPlaceholder()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "unresolved_variable",
                    ReturnType = intType,
                    Body = new HirVar
                    {
                        Name = "missing",
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5001" &&
                          diagnostic.Message?.Contains("missing", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        var poison = Assert.IsType<MirPoison>(returnTerminator.Value);
        Assert.Contains("Unresolved variable 'missing'", poison.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            function.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirAssign>(),
            assign => assign.Source is MirConstant);
    }

    [Fact]
    public void Build_BreakOutsideLoop_ReturnsPoisonInsteadOfPlaceholder()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "break_outside_loop",
                    ReturnType = intType,
                    Body = new HirBreak
                    {
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5310" &&
                          diagnostic.Message?.Contains("break expression", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        var poison = Assert.IsType<MirPoison>(returnTerminator.Value);
        Assert.Equal("break outside loop", poison.Reason);
    }

    [Fact]
    public void Build_ContinueOutsideLoop_ReturnsPoisonInsteadOfPlaceholder()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "continue_outside_loop",
                    ReturnType = intType,
                    Body = new HirContinue
                    {
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5310" &&
                          diagnostic.Message?.Contains("continue expression", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        var poison = Assert.IsType<MirPoison>(returnTerminator.Value);
        Assert.Equal("continue outside loop", poison.Reason);
    }

    [Fact]
    public void Build_ReturnWithoutValueInNonUnitFunction_UsesPoisonReturnValue()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "missing_return_value",
                    ReturnType = intType,
                    Body = new HirReturn
                    {
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5330" &&
                          diagnostic.Message?.Contains("without a value", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        var entryReturn = Assert.IsType<MirReturn>(function.BasicBlocks[0].Terminator);
        var poison = Assert.IsType<MirPoison>(entryReturn.Value);
        Assert.Equal("missing non-Unit return value", poison.Reason);
    }

    [Fact]
    public void Build_IfWithoutElseInNonUnitExpression_UsesPoisonElseValue()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "missing_else",
                    ReturnType = intType,
                    Body = new HirIf
                    {
                        Condition = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Bool,
                            Value = true,
                            TypeId = boolType
                        },
                        ThenBranch = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        },
                        ElseBranch = null,
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5330" &&
                          diagnostic.Message?.Contains("without an else branch", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        Assert.Contains(
            function.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirAssign>(),
            assign => assign.Source is MirPoison { Reason: "missing non-Unit else branch" });
    }

    [Fact]
    public void Build_EmptyMatch_UsesPoisonInsteadOfPlaceholder()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "empty_match",
                    ReturnType = intType,
                    Body = new HirMatch
                    {
                        TypeId = intType,
                        Scrutinee = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        },
                        Branches = []
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5330" &&
                          diagnostic.Message?.Contains("without branches", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        Assert.Contains(
            function.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirAssign>(),
            assign => assign.Source is MirPoison { Reason: "empty match expression" });
    }

    [Fact]
    public void Build_CapturedLambdaWithMissingCapture_ReturnsPoisonInsteadOfPlaceholder()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var functionType = new TypeId(93001);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "bad_capture",
                    ReturnType = functionType,
                    Body = new HirLambda
                    {
                        TypeId = functionType,
                        ReturnType = intType,
                        Captures =
                        [
                            new HirCapture
                            {
                                Name = "missing",
                                TypeId = intType
                            }
                        ],
                        Body = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        }
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5320" &&
                          diagnostic.Message?.Contains("missing", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        var poison = Assert.IsType<MirPoison>(returnTerminator.Value);
        Assert.Equal("failed captured lambda lowering", poison.Reason);
    }

    [Fact]
    public void Build_RecursiveClosureGroupWithMissingSharedCapture_ReturnsPoisonInsteadOfPlaceholder()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var functionType = new TypeId(93002);
        var functionSymbol = new SymbolId(93003);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "bad_recursive_group_capture",
                    ReturnType = functionType,
                    Body = new HirBlock
                    {
                        TypeId = functionType,
                        Statements =
                        [
                            new HirDeclStatement
                            {
                                Declaration = new HirVal
                                {
                                    Name = "f",
                                    TypeId = functionType,
                                    Pattern = new HirVarPattern
                                    {
                                        Name = "f",
                                        SymbolId = functionSymbol,
                                        TypeId = functionType
                                    },
                                    Initializer = new HirLambda
                                    {
                                        TypeId = functionType,
                                        ReturnType = intType,
                                        Captures =
                                        [
                                            new HirCapture
                                            {
                                                Name = "missing",
                                                TypeId = intType
                                            }
                                        ],
                                        Body = new HirLiteral
                                        {
                                            LiteralKind = LiteralKind.Int,
                                            Value = 1L,
                                            TypeId = intType
                                        }
                                    }
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5320" &&
                          diagnostic.Message?.Contains("missing", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions, item => item.Name == "bad_recursive_group_capture");
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        var poison = Assert.IsType<MirPoison>(returnTerminator.Value);
        Assert.Equal("failed recursive closure group environment", poison.Reason);
    }

    [Fact]
    public void Build_UnsupportedHirUnaryOperator_ReportsMirLoweringDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "unsupported_unary",
                    ReturnType = intType,
                    Body = new HirUnaryOp
                    {
                        Operator = (Eidosc.Hir.UnaryOp)999,
                        Operand = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        },
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5330" &&
                          diagnostic.Message?.Contains("Unsupported HIR unary operator", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        var poison = Assert.IsType<MirPoison>(returnTerminator.Value);
        Assert.Contains("Unsupported HIR unary operator", poison.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_UnsupportedHirBinaryOperator_ReportsMirLoweringDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "unsupported_binary",
                    ReturnType = intType,
                    Body = new HirBinOp
                    {
                        Operator = (Eidosc.Hir.BinaryOp)999,
                        Left = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        },
                        Right = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 2L,
                            TypeId = intType
                        },
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5330" &&
                          diagnostic.Message?.Contains("Unsupported HIR binary operator", StringComparison.Ordinal) == true);
        var function = Assert.Single(mirModule.Functions);
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        var poison = Assert.IsType<MirPoison>(returnTerminator.Value);
        Assert.Contains("Unsupported HIR binary operator", poison.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(
            function.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirBinOp>(),
            instruction => instruction.Operator == Eidosc.Mir.BinaryOp.Add);
    }

    [Fact]
    public void Build_HirErrorPattern_ReportsPatternPoisonDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "hir_error_pattern",
                    ReturnType = intType,
                    Body = new HirMatch
                    {
                        TypeId = intType,
                        Scrutinee = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        },
                        Branches =
                        [
                            new HirMatchBranch
                            {
                                Pattern = new HirErrorPattern
                                {
                                    TypeId = intType,
                                    Reason = "synthetic pattern recovery"
                                },
                                Body = new HirLiteral
                                {
                                    LiteralKind = LiteralKind.Int,
                                    Value = 0L,
                                    TypeId = intType
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5332" &&
                          diagnostic.Message?.Contains("synthetic pattern recovery", StringComparison.Ordinal) == true);

        var function = Assert.Single(mirModule.Functions);
        Assert.Contains(
            function.BasicBlocks.Select(block => block.Terminator).OfType<MirSwitch>(),
            sw => sw.Discriminant is MirPoison { Reason: "synthetic pattern recovery" });
        Assert.Contains(
            function.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirAssign>(),
            assign => assign.Source is MirPoison { Reason: "match contains poisoned pattern" });
    }

    [Fact]
    public void Build_UnsupportedHirPattern_UsesPoisonInsteadOfAlwaysMatching()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "unsupported_pattern",
                    ReturnType = intType,
                    Body = new HirMatch
                    {
                        TypeId = intType,
                        Scrutinee = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        },
                        Branches =
                        [
                            new HirMatchBranch
                            {
                                Pattern = new UnsupportedHirPattern
                                {
                                    TypeId = intType
                                },
                                Body = new HirLiteral
                                {
                                    LiteralKind = LiteralKind.Int,
                                    Value = 0L,
                                    TypeId = intType
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5332" &&
                          diagnostic.Message?.Contains("UnsupportedHirPattern", StringComparison.Ordinal) == true);

        var function = Assert.Single(mirModule.Functions);
        Assert.Contains(
            function.BasicBlocks.Select(block => block.Terminator).OfType<MirSwitch>(),
            sw => sw.Discriminant is MirPoison { Reason: "Unsupported HIR pattern 'UnsupportedHirPattern'" });
        Assert.Contains(
            function.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirAssign>(),
            assign => assign.Source is MirPoison { Reason: "match contains poisoned pattern" });
    }

    [Fact]
    public void Build_PoisonPlaceOperand_ReportsTypeFallbackDiagnostic()
    {
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "poison_place_operand",
                    ReturnType = TypeId.None,
                    Body = new HirUnaryOp
                    {
                        Operator = Eidosc.Hir.UnaryOp.Deref,
                        Operand = new HirError
                        {
                            TypeId = TypeId.None,
                            Reason = "poison deref operand"
                        },
                        TypeId = TypeId.None
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5333" &&
                          diagnostic.Message?.Contains("operand is poison", StringComparison.Ordinal) == true);

        var function = Assert.Single(mirModule.Functions);
        Assert.Contains(
            function.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirAssign>(),
            assign => assign.Source is MirPoison { Reason: "missing MIR type for place operand" });
    }

    [Fact]
    public void Build_MissingPlaceOperandType_ReportsTypeFallbackDiagnostic()
    {
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "missing_place_operand_type",
                    ReturnType = TypeId.None,
                    Body = new HirUnaryOp
                    {
                        Operator = Eidosc.Hir.UnaryOp.Deref,
                        Operand = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = TypeId.None
                        },
                        TypeId = TypeId.None
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5333" &&
                          diagnostic.Message?.Contains("neither the operand nor fallback has a valid TypeId", StringComparison.Ordinal) == true);

        var function = Assert.Single(mirModule.Functions);
        Assert.Contains(
            function.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirAssign>(),
            assign => assign.Source is MirPoison { Reason: "missing MIR type for place operand" });
    }

    [Fact]
    public void Build_LocalInitializationWithMissingSourceType_ReportsTypeFallbackDiagnostic()
    {
        var sourceSymbol = new SymbolId(94001);
        var targetSymbol = new SymbolId(94002);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "missing_initialization_source_type",
                    ReturnType = TypeId.None,
                    Body = new HirBlock
                    {
                        TypeId = TypeId.None,
                        Statements =
                        [
                            CreateUntypedVal("source", sourceSymbol, new HirLiteral
                            {
                                LiteralKind = LiteralKind.Int,
                                Value = 1L,
                                TypeId = TypeId.None
                            }),
                            CreateUntypedVal("target", targetSymbol, new HirVar
                            {
                                Name = "source",
                                SymbolId = sourceSymbol,
                                TypeId = TypeId.None
                            })
                        ]
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5333" &&
                          diagnostic.Message?.Contains("initialization", StringComparison.Ordinal) == true);

        var function = Assert.Single(mirModule.Functions);
        Assert.Contains(
            function.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirAssign>(),
            assign => assign.Source is MirPoison { Reason: "missing MIR type for initialization" });
    }

    [Fact]
    public void Build_StoreWithMissingSourceType_ReportsTypeFallbackDiagnostic()
    {
        var sourceSymbol = new SymbolId(94003);
        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "missing_store_source_type",
                    ReturnType = TypeId.None,
                    Body = new HirBlock
                    {
                        TypeId = TypeId.None,
                        Statements =
                        [
                            CreateUntypedVal("source", sourceSymbol, new HirLiteral
                            {
                                LiteralKind = LiteralKind.Int,
                                Value = 1L,
                                TypeId = TypeId.None
                            }),
                            new HirAssignStatement
                            {
                                Target = new HirVar
                                {
                                    Name = "source",
                                    SymbolId = sourceSymbol,
                                    TypeId = TypeId.None
                                },
                                Value = new HirVar
                                {
                                    Name = "source",
                                    SymbolId = sourceSymbol,
                                    TypeId = TypeId.None
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        Assert.Contains(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5333" &&
                          diagnostic.Message?.Contains("store value", StringComparison.Ordinal) == true);

        var function = Assert.Single(mirModule.Functions);
        Assert.Contains(
            function.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirStore>(),
            store => store.Value is MirPoison { Reason: "missing MIR type for store value" });
    }

    [Fact]
    public void Build_LetTuplePattern_DestructuresInitializerIntoPatternBindings()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var aSymbol = new SymbolId(91001);
        var bSymbol = new SymbolId(91002);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "let_tuple",
                    ReturnType = intType,
                    Body = new HirBlock
                    {
                        TypeId = intType,
                        Statements =
                        [
                            new HirDeclStatement
                            {
                                Declaration = new HirVal
                                {
                                    Name = "$let",
                                    TypeId = TypeId.None,
                                    Pattern = new HirTuplePattern
                                    {
                                        Elements =
                                        [
                                            new HirVarPattern { Name = "a", SymbolId = aSymbol, TypeId = intType },
                                            new HirVarPattern { Name = "b", SymbolId = bSymbol, TypeId = intType }
                                        ]
                                    },
                                    Initializer = new HirTuple
                                    {
                                        TypeId = TypeId.None,
                                        Elements =
                                        [
                                            new HirLiteral { LiteralKind = LiteralKind.Int, Value = 2L, TypeId = intType },
                                            new HirLiteral { LiteralKind = LiteralKind.Int, Value = 3L, TypeId = intType }
                                        ]
                                    }
                                }
                            },
                            new HirExprStatement
                            {
                                Expression = new HirBinOp
                                {
                                    Operator = Eidosc.Hir.BinaryOp.Add,
                                    Left = new HirVar { Name = "a", SymbolId = aSymbol, TypeId = intType },
                                    Right = new HirVar { Name = "b", SymbolId = bSymbol, TypeId = intType },
                                    TypeId = intType
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var function = Assert.Single(mirModule.Functions, item => item.Name == "let_tuple");
        var entry = Assert.Single(function.BasicBlocks, block => block.IsEntry);

        var indexLoads = entry.Instructions
            .OfType<MirLoad>()
            .Where(load => load.Source is MirPlace { Kind: PlaceKind.Index })
            .ToList();
        Assert.True(indexLoads.Count >= 2);

        var add = Assert.Single(entry.Instructions.OfType<MirBinOp>());
        Assert.IsType<MirPlace>(add.Left);
        Assert.IsType<MirPlace>(add.Right);
        Assert.DoesNotContain(builder.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Build_LetRefMrefPatternBindings_EmitBorrowLoadsAndLocalBindingModes()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var xSymbol = new SymbolId(92001);
        var refSymbol = new SymbolId(92002);
        var mrefSymbol = new SymbolId(92003);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "binding_modes",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "x",
                            SymbolId = xSymbol,
                            TypeId = intType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirBlock
                    {
                        TypeId = intType,
                        Statements =
                        [
                            new HirDeclStatement
                            {
                                Declaration = new HirVal
                                {
                                    Name = "$let_ref",
                                    TypeId = intType,
                                    Pattern = new HirVarPattern
                                    {
                                        Name = "r",
                                        SymbolId = refSymbol,
                                        TypeId = intType,
                                        BindingMode = PatternBindingMode.SharedBorrow
                                    },
                                    Initializer = new HirVar
                                    {
                                        Name = "x",
                                        SymbolId = xSymbol,
                                        TypeId = intType
                                    }
                                }
                            },
                            new HirDeclStatement
                            {
                                Declaration = new HirVal
                                {
                                    Name = "$let_mref",
                                    TypeId = intType,
                                    Pattern = new HirVarPattern
                                    {
                                        Name = "m",
                                        SymbolId = mrefSymbol,
                                        TypeId = intType,
                                        BindingMode = PatternBindingMode.MutableBorrow
                                    },
                                    Initializer = new HirVar
                                    {
                                        Name = "x",
                                        SymbolId = xSymbol,
                                        TypeId = intType
                                    }
                                }
                            },
                            new HirExprStatement
                            {
                                Expression = new HirVar
                                {
                                    Name = "r",
                                    SymbolId = refSymbol,
                                    TypeId = intType
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var function = Assert.Single(mirModule.Functions, item => item.Name == "binding_modes");
        var entry = Assert.Single(function.BasicBlocks, block => block.IsEntry);

        var xLocal = Assert.Single(function.Locals, local => local.Name == "x");
        var refLocal = Assert.Single(function.Locals, local => local.Name == "r");
        var mrefLocal = Assert.Single(function.Locals, local => local.Name == "m");

        var borrowLoads = entry.Instructions
            .OfType<MirLoad>()
            .Where(load => load.Source is MirPlace { Kind: PlaceKind.Local, Local: var sourceLocal } &&
                           sourceLocal.Equals(xLocal.Id))
            .ToList();

        var refLoad = Assert.Single(
            borrowLoads,
            load => load.Target.Kind == PlaceKind.Local && load.Target.Local.Equals(refLocal.Id));
        var mrefLoad = Assert.Single(
            borrowLoads,
            load => load.Target.Kind == PlaceKind.Local && load.Target.Local.Equals(mrefLocal.Id));

        Assert.False(refLoad.IsMutableBorrow);
        Assert.True(mrefLoad.IsMutableBorrow);
        Assert.True(refLoad.CreatesBorrowAlias);
        Assert.True(mrefLoad.CreatesBorrowAlias);
        Assert.Equal(PatternBindingMode.SharedBorrow, refLocal.BindingMode);
        Assert.Equal(PatternBindingMode.MutableBorrow, mrefLocal.BindingMode);
        Assert.DoesNotContain(builder.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);
    }

    private static HirDeclStatement CreateUntypedVal(string name, SymbolId symbolId, HirNode initializer)
    {
        return new HirDeclStatement
        {
            Declaration = new HirVal
            {
                Name = name,
                SymbolId = symbolId,
                TypeId = TypeId.None,
                Pattern = new HirVarPattern
                {
                    Name = name,
                    SymbolId = symbolId,
                    TypeId = TypeId.None
                },
                Initializer = initializer
            }
        };
    }

    private static bool WritesLocal(MirInstruction instr, LocalId localId)
    {
        return instr switch
        {
            MirAssign assign when assign.Target.Kind == PlaceKind.Local && assign.Target.Local.Equals(localId) => true,
            MirMove move when move.Target?.Kind == PlaceKind.Local && move.Target.Local.Equals(localId) => true,
            MirCopy copy when copy.Target?.Kind == PlaceKind.Local && copy.Target.Local.Equals(localId) => true,
            MirStore store when store.Target?.Kind == PlaceKind.Local && store.Target.Local.Equals(localId) => true,
            _ => false
        };
    }

    private sealed record UnsupportedHirExpr : HirNode
    {
        public UnsupportedHirExpr() : base(HirKind.Expr) { }
    }

    private sealed record UnsupportedHirStatement : HirStatement;

    private sealed record UnsupportedHirPattern : HirPattern;

    private sealed record UnsupportedHirDecl : HirDecl
    {
        public UnsupportedHirDecl() : base(HirKind.Val) { }
    }

}
