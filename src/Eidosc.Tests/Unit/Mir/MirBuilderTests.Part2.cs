using Eidosc.Symbols;
using Eidosc;
using Eidosc.Hir;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public partial class MirBuilderTests
{
    [Fact]
    public void Build_FunctionCall_WithResolverCopyType_ArgumentIsCopiedNotMoved()
    {
        var customType = new TypeId(9220);
        var calleeSymbol = new SymbolId(9221);
        var callerSymbol = new SymbolId(9222);
        var paramSymbol = new SymbolId(9223);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "callee",
                    SymbolId = calleeSymbol,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "x",
                            SymbolId = new SymbolId(9224),
                            TypeId = customType
                        }
                    ],
                    ReturnType = customType,
                    Body = new HirVar
                    {
                        Name = "x",
                        SymbolId = new SymbolId(9224),
                        TypeId = customType
                    }
                },
                new HirFunc
                {
                    Name = "caller",
                    SymbolId = callerSymbol,
                    ReturnType = customType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "p",
                            SymbolId = paramSymbol,
                            TypeId = customType
                        }
                    ],
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "callee",
                            SymbolId = calleeSymbol,
                            TypeId = customType
                        },
                        Arguments =
                        [
                            new HirVar
                            {
                                Name = "p",
                                SymbolId = paramSymbol,
                                TypeId = customType
                            }
                        ],
                        TypeId = customType
                    }
                }
            ]
        };

        var builder = new MirBuilder(typeId => typeId.Equals(customType));
        var mirModule = builder.Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var entry = Assert.Single(callerFunc.BasicBlocks, block => block.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        Assert.Single(call.Arguments);

        var sourceLocal = Assert.Single(callerFunc.Locals, local => local.IsParameter && local.Name == "p").Id;
        var sourceCopies = entry.Instructions
            .OfType<MirCopy>()
            .Where(copy => copy.Source.Local.Equals(sourceLocal))
            .ToList();
        Assert.NotEmpty(sourceCopies);
        Assert.DoesNotContain(
            entry.Instructions.OfType<MirMove>(),
            move => move.Source.Local.Equals(sourceLocal));
    }

    [Fact]
    public void Build_StdSeqHeadCall_FirstArgumentIsCopiedNotMoved()
    {
        var listType = new TypeId(9400);
        var optionType = new TypeId(9401);
        var headSymbol = new SymbolId(9402);
        var sourceSymbol = new SymbolId(9403);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "caller",
                    ReturnType = optionType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "xs",
                            SymbolId = sourceSymbol,
                            TypeId = listType
                        }
                    ],
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "Seq::head",
                            SymbolId = headSymbol,
                            TypeId = optionType
                        },
                        Arguments =
                        [
                            new HirVar
                            {
                                Name = "xs",
                                SymbolId = sourceSymbol,
                                TypeId = listType
                            }
                        ],
                        TypeId = optionType
                    }
                }
            ]
        };

        var effectMap = new ParameterEffectMap();
        effectMap.Add("Seq::head", headSymbol.Value, [ParameterEffect.Read]);

        var builder = new MirBuilder(null, parameterEffects: effectMap);
        var mirModule = builder.Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var entry = Assert.Single(callerFunc.BasicBlocks, block => block.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        var funcRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal("Seq::head", funcRef.Name);
        Assert.Single(call.Arguments);

        var sourceLocal = Assert.Single(callerFunc.Locals, local => local.IsParameter && local.Name == "xs").Id;
        var sourceCopies = entry.Instructions
            .OfType<MirCopy>()
            .Where(copy => copy.Source.Local.Equals(sourceLocal))
            .ToList();
        Assert.NotEmpty(sourceCopies);
        Assert.DoesNotContain(
            entry.Instructions.OfType<MirMove>(),
            move => move.Source.Local.Equals(sourceLocal));

        var firstArg = Assert.IsType<MirPlace>(call.Arguments[0]);
        Assert.Contains(sourceCopies, copy => copy.Target.Local.Equals(firstArg.Local));
    }

    [Fact]
    public void Build_StdResultOkCall_FirstArgumentIsCopiedNotMoved()
    {
        var resultType = new TypeId(9410);
        var optionType = new TypeId(9411);
        var okSymbol = new SymbolId(9412);
        var sourceSymbol = new SymbolId(9413);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "caller",
                    ReturnType = optionType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "res",
                            SymbolId = sourceSymbol,
                            TypeId = resultType
                        }
                    ],
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "Result::ok",
                            SymbolId = okSymbol,
                            TypeId = optionType
                        },
                        Arguments =
                        [
                            new HirVar
                            {
                                Name = "res",
                                SymbolId = sourceSymbol,
                                TypeId = resultType
                            }
                        ],
                        TypeId = optionType
                    }
                }
            ]
        };

        var effectMap = new ParameterEffectMap();
        effectMap.Add("Result::ok", okSymbol.Value, [ParameterEffect.Read]);

        var builder = new MirBuilder(null, parameterEffects: effectMap);
        var mirModule = builder.Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var entry = Assert.Single(callerFunc.BasicBlocks, block => block.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        var funcRef = Assert.IsType<MirFunctionRef>(call.Function);
        Assert.Equal("Result::ok", funcRef.Name);
        Assert.Single(call.Arguments);

        var sourceLocal = Assert.Single(callerFunc.Locals, local => local.IsParameter && local.Name == "res").Id;
        var sourceCopies = entry.Instructions
            .OfType<MirCopy>()
            .Where(copy => copy.Source.Local.Equals(sourceLocal))
            .ToList();
        Assert.NotEmpty(sourceCopies);
        Assert.DoesNotContain(
            entry.Instructions.OfType<MirMove>(),
            move => move.Source.Local.Equals(sourceLocal));

        var firstArg = Assert.IsType<MirPlace>(call.Arguments[0]);
        Assert.Contains(sourceCopies, copy => copy.Target.Local.Equals(firstArg.Local));
    }

    [Fact]
    public void Build_FunctionCall_WithoutResolverCopyType_ArgumentIsCopiedForUnknownCallee()
    {
        var customType = new TypeId(9230);
        var calleeSymbol = new SymbolId(9231);
        var callerSymbol = new SymbolId(9232);
        var paramSymbol = new SymbolId(9233);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "callee",
                    SymbolId = calleeSymbol,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "x",
                            SymbolId = new SymbolId(9234),
                            TypeId = customType
                        }
                    ],
                    ReturnType = customType,
                    Body = new HirVar
                    {
                        Name = "x",
                        SymbolId = new SymbolId(9234),
                        TypeId = customType
                    }
                },
                new HirFunc
                {
                    Name = "caller",
                    SymbolId = callerSymbol,
                    ReturnType = customType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "p",
                            SymbolId = paramSymbol,
                            TypeId = customType
                        }
                    ],
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "callee",
                            SymbolId = calleeSymbol,
                            TypeId = customType
                        },
                        Arguments =
                        [
                            new HirVar
                            {
                                Name = "p",
                                SymbolId = paramSymbol,
                                TypeId = customType
                            }
                        ],
                        TypeId = customType
                    }
                }
            ]
        };

        // Without effect info, unknown callees default to Read (MirCopy).
        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var callerFunc = Assert.Single(mirModule.Functions, function => function.Name == "caller");
        var entry = Assert.Single(callerFunc.BasicBlocks, block => block.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        Assert.Single(call.Arguments);

        var sourceLocal = Assert.Single(callerFunc.Locals, local => local.IsParameter && local.Name == "p").Id;
        var sourceCopies = entry.Instructions
            .OfType<MirCopy>()
            .Where(copy => copy.Source.Local.Equals(sourceLocal))
            .ToList();
        Assert.NotEmpty(sourceCopies);
        Assert.DoesNotContain(
            entry.Instructions.OfType<MirMove>(),
            move => move.Source.Local.Equals(sourceLocal));
    }

    [Fact]
    public void Build_StringEqualityBinOp_LowersToRuntimeStringEqualsCall()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var leftSymbol = new SymbolId(9212);
        var rightSymbol = new SymbolId(9213);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "eqText",
                    ReturnType = boolType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "a",
                            SymbolId = leftSymbol,
                            TypeId = stringType
                        },
                        new HirParam
                        {
                            Name = "b",
                            SymbolId = rightSymbol,
                            TypeId = stringType
                        }
                    ],
                    Body = new HirBinOp
                    {
                        Operator = Eidosc.Hir.BinaryOp.Eq,
                        Left = new HirVar
                        {
                            Name = "a",
                            SymbolId = leftSymbol,
                            TypeId = stringType
                        },
                        Right = new HirVar
                        {
                            Name = "b",
                            SymbolId = rightSymbol,
                            TypeId = stringType
                        },
                        TypeId = boolType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions, function => function.Name == "eqText");
        var entry = Assert.Single(func.BasicBlocks, block => block.IsEntry);
        var equalsCall = Assert.Single(
            entry.Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef { Name: "string_equals" });

        Assert.Equal(2, equalsCall.Arguments.Count);
        Assert.DoesNotContain(
            entry.Instructions.OfType<MirBinOp>(),
            operation => operation.Operator == Eidosc.Mir.BinaryOp.Eq);
    }

    [Fact]
    public void Build_StringLiteralPatternMatch_UsesRuntimeStringEqualsCall()
    {
        var stringType = new TypeId(BaseTypes.StringId);
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(9214);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "classifyText",
                    ReturnType = intType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "s",
                            SymbolId = paramSymbol,
                            TypeId = stringType
                        }
                    ],
                    Body = new HirMatch
                    {
                        Scrutinee = new HirVar
                        {
                            Name = "s",
                            SymbolId = paramSymbol,
                            TypeId = stringType
                        },
                        Branches =
                        [
                            new HirMatchBranch
                            {
                                Pattern = new HirLiteralPattern
                                {
                                    Value = "ok",
                                    TypeId = stringType
                                },
                                Body = new HirLiteral
                                {
                                    LiteralKind = LiteralKind.Int,
                                    Value = 1L,
                                    TypeId = intType
                                }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern
                                {
                                    IsWildcard = true,
                                    TypeId = stringType
                                },
                                Body = new HirLiteral
                                {
                                    LiteralKind = LiteralKind.Int,
                                    Value = 0L,
                                    TypeId = intType
                                }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions, function => function.Name == "classifyText");
        var calls = func.BasicBlocks
            .SelectMany(block => block.Instructions)
            .OfType<MirCall>()
            .Where(call => call.Function is MirFunctionRef { Name: "string_equals" })
            .ToList();

        Assert.NotEmpty(calls);
    }

    [Fact]
    public void Build_ConstructorCall_UsesFunctionRefOperandInsteadOfUninitializedLocal()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var fooType = new TypeId(9020);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "make",
                    ReturnType = fooType,
                    Body = new HirCall
                    {
                        Function = new HirVar
                        {
                            Name = "A",
                            SymbolId = SymbolId.None,
                            TypeId = TypeId.None
                        },
                        Convention = CallConvention.Constructor,
                        Arguments =
                        [
                            new HirLiteral
                            {
                                LiteralKind = LiteralKind.Int,
                                Value = 1L,
                                TypeId = intType
                            }
                        ],
                        TypeId = fooType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions, function => function.Name == "make");
        var entry = Assert.Single(func.BasicBlocks, block => block.IsEntry);
        var call = Assert.Single(entry.Instructions.OfType<MirCall>());
        var funcRef = Assert.IsType<MirFunctionRef>(call.Function);

        Assert.Equal("A", funcRef.Name);
        var target = call.Target;
        Assert.NotNull(target);
        Assert.Equal(fooType, target!.TypeId);
        Assert.DoesNotContain(
            builder.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true &&
                          diagnostic.Message.Contains("Unsupported"));
    }

    [Fact]
    public void Build_IfExpression_WritesMergedResultInBothBranches()
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
                    Name = "choose",
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
                        ElseBranch = new HirLiteral
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

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions);
        var ret = Assert.IsType<MirReturn>(Assert.Single(func.BasicBlocks, b => b.Terminator is MirReturn).Terminator);
        var retPlace = Assert.IsType<MirPlace>(ret.Value);

        var writes = func.BasicBlocks
            .SelectMany(block => block.Instructions)
            .Count(instr => WritesLocal(instr, retPlace.Local));

        Assert.Equal(2, writes);
    }

    [Fact]
    public void Build_MatchExpression_WritesMergedResultInAllBranches()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(300);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "classify",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "x",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirMatch
                    {
                        Scrutinee = new HirVar
                        {
                            Name = "x",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        },
                        Branches =
                        [
                            new HirMatchBranch
                            {
                                Pattern = new HirLiteralPattern { Value = 0, TypeId = intType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 10L, TypeId = intType }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern { IsWildcard = true, TypeId = intType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 20L, TypeId = intType }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions);
        var ret = Assert.IsType<MirReturn>(Assert.Single(func.BasicBlocks, b => b.Terminator is MirReturn).Terminator);
        var retPlace = Assert.IsType<MirPlace>(ret.Value);

        var writes = func.BasicBlocks
            .SelectMany(block => block.Instructions)
            .Count(instr => WritesLocal(instr, retPlace.Local));

        Assert.Equal(2, writes);
        Assert.DoesNotContain(
            func.BasicBlocks,
            block => block.Terminator is MirSwitch switchTerm &&
                     switchTerm.Discriminant is MirConstant
                     {
                         Value: MirConstantValue.BoolValue { Value: true }
                     });
    }

    [Fact]
    public void Build_NonExhaustiveMatchFallback_LowersToUnreachableWithoutPlaceholder()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(301);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "classify",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "x",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirMatch
                    {
                        Scrutinee = new HirVar
                        {
                            Name = "x",
                            SymbolId = paramSymbol,
                            TypeId = intType
                        },
                        Branches =
                        [
                            new HirMatchBranch
                            {
                                Pattern = new HirLiteralPattern { Value = 0, TypeId = intType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 10L, TypeId = intType }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions);
        var ret = Assert.IsType<MirReturn>(Assert.Single(func.BasicBlocks, b => b.Terminator is MirReturn).Terminator);
        var retPlace = Assert.IsType<MirPlace>(ret.Value);

        Assert.Contains(builder.Diagnostics, diagnostic => diagnostic.Code == "W5331");
        Assert.Contains(func.BasicBlocks, block => block.Terminator is MirUnreachable);
        Assert.Single(
            func.BasicBlocks.SelectMany(block => block.Instructions),
            instr => WritesLocal(instr, retPlace.Local));
        Assert.DoesNotContain(
            func.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirAssign>(),
            assign => assign.Target.Local.Equals(retPlace.Local) &&
                      assign.Source is MirConstant
                      {
                          Value: MirConstantValue.IntValue { Value: 0 }
                      });
    }


}
