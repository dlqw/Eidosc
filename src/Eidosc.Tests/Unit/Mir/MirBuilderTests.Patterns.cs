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
    public void Build_CtorPatternMatch_LowersDiscriminantToRuntimeTypeIdCall()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var adtType = new TypeId(910);
        var paramSymbol = new SymbolId(9100);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "isSome",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "v",
                            SymbolId = paramSymbol,
                            TypeId = adtType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirMatch
                    {
                        Scrutinee = new HirVar
                        {
                            Name = "v",
                            SymbolId = paramSymbol,
                            TypeId = adtType
                        },
                        Branches =
                        [
                            new HirMatchBranch
                            {
                                Pattern = new HirCtorPattern
                                {
                                    ConstructorName = "Some",
                                    TypeId = adtType
                                },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 1L, TypeId = intType }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern { IsWildcard = true, TypeId = adtType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 0L, TypeId = intType }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions);
        var entry = Assert.Single(func.BasicBlocks, block => block.IsEntry);
        var switchTerm = Assert.IsType<MirSwitch>(entry.Terminator);

        var typeIdCall = Assert.Single(
            entry.Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef { Name: "type_id" });
        Assert.IsType<MirPlace>(typeIdCall.Target);

        var ctorTagCompare = Assert.Single(
            entry.Instructions.OfType<MirBinOp>(),
            operation =>
                operation.Operator == Eidosc.Mir.BinaryOp.Eq &&
                operation.Right is MirConstant { Value: MirConstantValue.IntValue });

        var switchDiscriminant = Assert.IsType<MirPlace>(switchTerm.Discriminant);
        var compareTarget = Assert.IsType<MirPlace>(ctorTagCompare.Target);
        Assert.Contains(
            entry.Instructions.OfType<MirCopy>(),
            copy => copy.Source.Local.Equals(compareTarget.Local) &&
                    copy.Target.Local.Equals(switchDiscriminant.Local));

        var ctorTag = Assert.IsType<MirConstant>(ctorTagCompare.Right);
        var ctorTagValue = Assert.IsType<MirConstantValue.IntValue>(ctorTag.Value);
        Assert.Equal(AdtConstructorTypeId.Compute("Some"), ctorTagValue.Value);

        var ctorBranch = Assert.Single(switchTerm.Branches);
        var branchValue = Assert.IsType<MirConstantValue.BoolValue>(ctorBranch.Value.Value);
        Assert.True(branchValue.Value);
        Assert.True(switchTerm.DefaultTarget.HasValue);
    }

    [Fact]
    public void Build_MatchGuard_FallsThroughToNextBranchWhenGuardIsFalse()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(9400);
        var bindingSymbol = new SymbolId(9401);

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
                                Pattern = new HirVarPattern
                                {
                                    Name = "n",
                                    SymbolId = bindingSymbol,
                                    TypeId = intType
                                },
                                Guard = new HirBinOp
                                {
                                    Operator = Eidosc.Hir.BinaryOp.Gt,
                                    Left = new HirVar
                                    {
                                        Name = "n",
                                        SymbolId = bindingSymbol,
                                        TypeId = intType
                                    },
                                    Right = new HirLiteral
                                    {
                                        LiteralKind = LiteralKind.Int,
                                        Value = 0L,
                                        TypeId = intType
                                    },
                                    TypeId = new TypeId(BaseTypes.BoolId)
                                },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 1L, TypeId = intType }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern { IsWildcard = true, TypeId = intType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 0L, TypeId = intType }
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

        Assert.Contains(
            func.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirBinOp>(),
            operation => operation.Operator == Eidosc.Mir.BinaryOp.Gt);

        Assert.Contains(
            func.BasicBlocks,
            block => block.Terminator is MirSwitch sw &&
                     sw.Branches.Count == 1 &&
                     sw.DefaultTarget.HasValue);
    }

    [Fact]
    public void Build_MatchPatternGuardBinding_LowersGuardFlowAndBindsPatternVariable()
    {
        const string source = """
OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x => match x
    {
        _ when Some(n) <- x => n,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_match_pattern_guard_binding.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var classify = Assert.Single(mirModule.Functions, function => function.Name == "classify");
        var instructions = classify.BasicBlocks.SelectMany(block => block.Instructions).ToList();

        Assert.Contains(
            instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef { Name: "type_id" });
        Assert.Contains(
            instructions.OfType<MirLoad>(),
            load => load.Source is MirPlace { Kind: PlaceKind.Field, FieldName: "_0" });
        Assert.Contains(
            classify.BasicBlocks,
            block => block.Terminator is MirSwitch sw &&
                     sw.Branches.Count == 1 &&
                     sw.DefaultTarget.HasValue);
    }

    [Fact]
    public void Build_FunctionPatternBranchWithPatternGuardBinding_LowersToMirGuardFlow()
    {
        const string source = """
OptionI :: type {
    Some(Int) | None
}

classify :: OptionI -> Int
{
    x when Some(n) <- x => n,
    _ => 0
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_function_pattern_guard_binding.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);

        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var classify = Assert.Single(mirModule.Functions, function => function.Name == "classify");
        var instructions = classify.BasicBlocks.SelectMany(block => block.Instructions).ToList();

        Assert.Contains(
            instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef { Name: "type_id" });
        Assert.Contains(
            instructions.OfType<MirLoad>(),
            load => load.Source is MirPlace { Kind: PlaceKind.Field, FieldName: "_0" });
    }

    [Fact]
    public void Build_TupleLiteralPatternMatch_EmitsElementComparisons()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var tupleType = new TypeId(950);
        var paramSymbol = new SymbolId(9500);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "isPair",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "pair",
                            SymbolId = paramSymbol,
                            TypeId = tupleType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirMatch
                    {
                        Scrutinee = new HirVar
                        {
                            Name = "pair",
                            SymbolId = paramSymbol,
                            TypeId = tupleType
                        },
                        Branches =
                        [
                            new HirMatchBranch
                            {
                                Pattern = new HirTuplePattern
                                {
                                    TypeId = tupleType,
                                    Elements =
                                    [
                                        new HirLiteralPattern { Value = 1L, TypeId = intType },
                                        new HirLiteralPattern { Value = 2L, TypeId = intType }
                                    ]
                                },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 1L, TypeId = intType }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern { IsWildcard = true, TypeId = tupleType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 0L, TypeId = intType }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions);
        var comparisons = func.BasicBlocks
            .SelectMany(block => block.Instructions)
            .OfType<MirBinOp>()
            .Where(operation => operation.Operator == Eidosc.Mir.BinaryOp.Eq && operation.Right is MirConstant)
            .ToList();

        Assert.Contains(comparisons, comparison =>
            comparison.Right is MirConstant
            {
                Value: MirConstantValue.IntValue { Value: 1 }
            });
        Assert.Contains(comparisons, comparison =>
            comparison.Right is MirConstant
            {
                Value: MirConstantValue.IntValue { Value: 2 }
            });
    }

    [Fact]
    public void Build_OrPatternMatch_LowersWithShortCircuitControlFlow()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(9510);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "isOneOrTwo",
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
                                Pattern = new HirOrPattern
                                {
                                    Left = new HirLiteralPattern
                                    {
                                        Value = 1L,
                                        TypeId = intType
                                    },
                                    Right = new HirLiteralPattern
                                    {
                                        Value = 2L,
                                        TypeId = intType
                                    },
                                    TypeId = intType
                                },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 1L, TypeId = intType }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern { IsWildcard = true, TypeId = intType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 0L, TypeId = intType }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions);
        var switches = func.BasicBlocks
            .Where(block => block.Terminator is MirSwitch)
            .Select(block => Assert.IsType<MirSwitch>(block.Terminator))
            .ToList();

        Assert.True(switches.Count >= 2);
    }

    [Fact]
    public void Build_AndPatternMatch_LowersWithShortCircuitControlFlow()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(9512);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "isTwoInRange",
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
                                Pattern = new HirAndPattern
                                {
                                    Left = new HirRangePattern
                                    {
                                        Start = new HirLiteralPattern { Value = 1L, TypeId = intType },
                                        End = new HirLiteralPattern { Value = 3L, TypeId = intType },
                                        TypeId = intType
                                    },
                                    Right = new HirLiteralPattern
                                    {
                                        Value = 2L,
                                        TypeId = intType
                                    },
                                    TypeId = intType
                                },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 1L, TypeId = intType }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern { IsWildcard = true, TypeId = intType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 0L, TypeId = intType }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions);
        var switches = func.BasicBlocks
            .Where(block => block.Terminator is MirSwitch)
            .Select(block => Assert.IsType<MirSwitch>(block.Terminator))
            .ToList();

        Assert.True(switches.Count >= 2);
    }

    [Fact]
    public void Build_NotPatternMatch_EmitsLogicalNotCondition()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(9513);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "isNotZero",
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
                                Pattern = new HirNotPattern
                                {
                                    InnerPattern = new HirLiteralPattern
                                    {
                                        Value = 0L,
                                        TypeId = intType
                                    },
                                    TypeId = intType
                                },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 1L, TypeId = intType }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern { IsWildcard = true, TypeId = intType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 0L, TypeId = intType }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions);
        Assert.Contains(
            func.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirUnaryOp>(),
            instruction => instruction.Operator == Eidosc.Mir.UnaryOp.Not);
    }

    [Fact]
    public void Build_AndPatternMatch_WithTupleBindings_BindsBothConjunctVariables()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var tupleType = new TypeId(9521);
        var paramSymbol = new SymbolId(9522);
        var leftSymbol = new SymbolId(9523);
        var rightSymbol = new SymbolId(9524);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "sumTuple",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "pair",
                            SymbolId = paramSymbol,
                            TypeId = tupleType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirMatch
                    {
                        Scrutinee = new HirVar
                        {
                            Name = "pair",
                            SymbolId = paramSymbol,
                            TypeId = tupleType
                        },
                        Branches =
                        [
                            new HirMatchBranch
                            {
                                Pattern = new HirAndPattern
                                {
                                    Left = new HirTuplePattern
                                    {
                                        Elements =
                                        [
                                            new HirVarPattern
                                            {
                                                Name = "a",
                                                SymbolId = leftSymbol,
                                                TypeId = intType
                                            },
                                            new HirVarPattern
                                            {
                                                IsWildcard = true,
                                                TypeId = intType
                                            }
                                        ],
                                        TypeId = tupleType
                                    },
                                    Right = new HirTuplePattern
                                    {
                                        Elements =
                                        [
                                            new HirVarPattern
                                            {
                                                IsWildcard = true,
                                                TypeId = intType
                                            },
                                            new HirVarPattern
                                            {
                                                Name = "b",
                                                SymbolId = rightSymbol,
                                                TypeId = intType
                                            }
                                        ],
                                        TypeId = tupleType
                                    },
                                    TypeId = tupleType
                                },
                                Body = new HirBinOp
                                {
                                    Operator = Eidosc.Hir.BinaryOp.Add,
                                    Left = new HirVar
                                    {
                                        Name = "a",
                                        SymbolId = leftSymbol,
                                        TypeId = intType
                                    },
                                    Right = new HirVar
                                    {
                                        Name = "b",
                                        SymbolId = rightSymbol,
                                        TypeId = intType
                                    },
                                    TypeId = intType
                                }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern { IsWildcard = true, TypeId = tupleType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 0L, TypeId = intType }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions, function => function.Name == "sumTuple");
        var indexLoads = func.BasicBlocks
            .SelectMany(block => block.Instructions)
            .OfType<MirLoad>()
            .Where(load => load.Source is MirPlace { Kind: PlaceKind.Index })
            .ToList();

        Assert.Contains(indexLoads, load =>
            load.Source is MirPlace
            {
                Index: MirConstant { Value: MirConstantValue.IntValue { Value: 0 } }
            });
        Assert.Contains(indexLoads, load =>
            load.Source is MirPlace
            {
                Index: MirConstant { Value: MirConstantValue.IntValue { Value: 1 } }
            });
    }

    [Fact]
    public void Build_OrPatternMatch_WithSharedBinding_ReusesPatternBoundSymbolLocal()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(9515);
        var bindingSymbol = new SymbolId(9516);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "reuseBinding",
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
                                Pattern = new HirOrPattern
                                {
                                    Left = new HirAsPattern
                                    {
                                        InnerPattern = new HirLiteralPattern { Value = 1L, TypeId = intType },
                                        Name = "n",
                                        SymbolId = bindingSymbol,
                                        TypeId = intType
                                    },
                                    Right = new HirAsPattern
                                    {
                                        InnerPattern = new HirLiteralPattern { Value = 2L, TypeId = intType },
                                        Name = "n",
                                        SymbolId = bindingSymbol,
                                        TypeId = intType
                                    },
                                    TypeId = intType
                                },
                                Body = new HirVar
                                {
                                    Name = "n",
                                    SymbolId = bindingSymbol,
                                    TypeId = intType
                                }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern { IsWildcard = true, TypeId = intType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 0L, TypeId = intType }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions, function => function.Name == "reuseBinding");
        Assert.DoesNotContain(func.Locals, local => !local.TypeId.IsValid);
    }

    [Fact]
    public void Build_RangePatternMatch_EmitsLowerAndUpperBoundChecks()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var paramSymbol = new SymbolId(9520);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "inRange",
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
                                Pattern = new HirRangePattern
                                {
                                    Start = new HirLiteralPattern { Value = 1L, TypeId = intType },
                                    End = new HirLiteralPattern { Value = 3L, TypeId = intType },
                                    TypeId = intType
                                },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 1L, TypeId = intType }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern { IsWildcard = true, TypeId = intType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 0L, TypeId = intType }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions);
        var comparisons = func.BasicBlocks
            .SelectMany(block => block.Instructions)
            .OfType<MirBinOp>()
            .Select(instruction => instruction.Operator)
            .ToList();

        Assert.Contains(Eidosc.Mir.BinaryOp.Ge, comparisons);
        Assert.Contains(Eidosc.Mir.BinaryOp.Le, comparisons);
    }

    [Fact]
    public void Build_ListPatternMatch_WithRestBinding_EmitsLengthChecksAndTailMaterialization()
    {
        const string source = """
head_or_zero :: Int -> Int
{
    _ => match [1, 2, 3]
    {
        [head, ..tail] => head,
        [] => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_list_pattern_rest.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var func = Assert.Single(mirModule.Functions, function => function.Name == "head_or_zero");

        var instructions = func.BasicBlocks.SelectMany(block => block.Instructions).ToList();
        var arrayLengthCalls = instructions
            .OfType<MirCall>()
            .Where(call => call.Function is MirFunctionRef { Name: "array_length" })
            .ToList();
        Assert.NotEmpty(arrayLengthCalls);

        var runtimeArrayLoads = instructions
            .OfType<MirLoad>()
            .Where(load => load.Source is MirPlace { Kind: PlaceKind.Index, IndexAccessKind: MirIndexAccessKind.RuntimeArray })
            .ToList();
        Assert.NotEmpty(runtimeArrayLoads);

        var arrayPushCalls = instructions
            .OfType<MirCall>()
            .Where(call => call.Function is MirFunctionRef { Name: "array_push" })
            .ToList();
        Assert.NotEmpty(arrayPushCalls);
    }

    [Fact]
    public void Build_ViewPatternMatch_ReusesSingleViewCallForConditionAndBinding()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var normalizeSymbol = new SymbolId(9530);
        var paramSymbol = new SymbolId(9531);
        var bindingSymbol = new SymbolId(9532);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "normalize",
                    SymbolId = normalizeSymbol,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "value",
                            SymbolId = new SymbolId(9533),
                            TypeId = intType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirLiteral
                    {
                        LiteralKind = LiteralKind.Int,
                        Value = 0L,
                        TypeId = intType
                    }
                },
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
                                Pattern = new HirViewPattern
                                {
                                    View = new HirVar
                                    {
                                        Name = "normalize",
                                        SymbolId = normalizeSymbol,
                                        TypeId = intType
                                    },
                                    InnerPattern = new HirVarPattern
                                    {
                                        Name = "n",
                                        SymbolId = bindingSymbol,
                                        TypeId = intType
                                    },
                                    TypeId = intType
                                },
                                Body = new HirVar
                                {
                                    Name = "n",
                                    SymbolId = bindingSymbol,
                                    TypeId = intType
                                }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern
                                {
                                    IsWildcard = true,
                                    TypeId = intType
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
        var classify = Assert.Single(mirModule.Functions, function => function.Name == "classify");
        var normalizeCalls = classify.BasicBlocks
            .SelectMany(block => block.Instructions)
            .OfType<MirCall>()
            .Where(call => call.Function is MirFunctionRef { Name: "normalize" })
            .ToList();

        Assert.Single(normalizeCalls);
    }

    [Fact]
    public void Build_ViewPatternMatch_UsesViewResultTypeIdWhenInnerPatternTypeIsMissing()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var normalizeSymbol = new SymbolId(9630);
        var paramSymbol = new SymbolId(9631);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "normalize",
                    SymbolId = normalizeSymbol,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "value",
                            SymbolId = new SymbolId(9632),
                            TypeId = intType
                        }
                    ],
                    ReturnType = boolType,
                    Body = new HirLiteral
                    {
                        LiteralKind = LiteralKind.Bool,
                        Value = true,
                        TypeId = boolType
                    }
                },
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
                                Pattern = new HirViewPattern
                                {
                                    View = new HirVar
                                    {
                                        Name = "normalize",
                                        SymbolId = normalizeSymbol,
                                        TypeId = boolType
                                    },
                                    ViewResultTypeId = boolType,
                                    InnerPattern = new HirVarPattern
                                    {
                                        IsWildcard = true,
                                        TypeId = TypeId.None
                                    },
                                    TypeId = intType
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
                                    TypeId = intType
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
        var classify = Assert.Single(mirModule.Functions, function => function.Name == "classify");
        var normalizeCall = Assert.Single(
            classify.BasicBlocks
                .SelectMany(block => block.Instructions)
                .OfType<MirCall>(),
            call => call.Function is MirFunctionRef { Name: "normalize" });

        var callTarget = Assert.IsType<MirPlace>(normalizeCall.Target);
        Assert.Equal(boolType, callTarget.TypeId);
    }

    [Fact]
    public void Build_ViewPatternMatch_WithGeneralExpression_ReusesSingleCallForConditionAndBinding()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        (if true then { normalize } else { normalize } -> 7) => 30,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_view_pattern_general_expr.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var classify = Assert.Single(mirModule.Functions, function => function.Name == "classify");
        var viewCalls = classify.BasicBlocks
            .SelectMany(block => block.Instructions)
            .OfType<MirCall>()
            .Where(call => call.Function is MirPlace { Kind: PlaceKind.Local })
            .ToList();

        Assert.Single(viewCalls);
    }

}
