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
    public void Build_TupleExpression_LowersToAllocAndAggregateIndexedStores()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var aSymbol = new SymbolId(400);
        var bSymbol = new SymbolId(401);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "pair",
                    Parameters =
                    [
                        new HirParam { Name = "a", SymbolId = aSymbol, TypeId = intType },
                        new HirParam { Name = "b", SymbolId = bSymbol, TypeId = intType }
                    ],
                    ReturnType = intType,
                    Body = new HirTuple
                    {
                        Elements =
                        [
                            new HirVar { Name = "a", SymbolId = aSymbol, TypeId = intType },
                            new HirVar { Name = "b", SymbolId = bSymbol, TypeId = intType }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions);
        var entry = Assert.Single(func.BasicBlocks, b => b.IsEntry);
        var ret = Assert.IsType<MirReturn>(entry.Terminator);
        var retPlace = Assert.IsType<MirPlace>(ret.Value);
        var alloc = Assert.Single(entry.Instructions.OfType<MirAlloc>());
        Assert.Equal(retPlace.Local, alloc.Target.Local);

        var stores = entry.Instructions.OfType<MirStore>().ToList();
        Assert.Equal(2, stores.Count);
        Assert.All(stores, store =>
        {
            Assert.Equal(PlaceKind.Index, store.Target.Kind);
            Assert.NotNull(store.Target.Base);
            Assert.Equal(retPlace.Local, store.Target.Base!.Local);
            Assert.Equal(MirIndexAccessKind.Aggregate, store.Target.IndexAccessKind);
            var index = Assert.IsType<MirConstant>(store.Target.Index);
            Assert.IsType<MirConstantValue.IntValue>(index.Value);
        });
    }

    [Fact]
    public void Build_ListExpression_LowersToArrayNewAndStores()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var listType = new TypeId(520);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "mkList",
                    ReturnType = listType,
                    Body = new HirList
                    {
                        Elements =
                        [
                            new HirLiteral { LiteralKind = LiteralKind.Int, Value = 1L, TypeId = intType },
                            new HirLiteral { LiteralKind = LiteralKind.Int, Value = 2L, TypeId = intType }
                        ],
                        TypeId = listType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions);
        var entry = Assert.Single(func.BasicBlocks, b => b.IsEntry);

        Assert.Contains(
            entry.Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef { Name: "array_new" });
        var stores = entry.Instructions.OfType<MirStore>().ToList();
        Assert.Equal(2, stores.Count);
        Assert.All(stores, store =>
        {
            Assert.Equal(PlaceKind.Index, store.Target.Kind);
            Assert.Equal(MirIndexAccessKind.RuntimeArray, store.Target.IndexAccessKind);
        });
    }

    [Fact]
    public void Build_ListExpression_WithTupleElements_UsesAggregateElementSizeForArrayNew()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var tupleType = new TypeId(5210);
        var listType = new TypeId(5211);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "mkTupleList",
                    ReturnType = listType,
                    Body = new HirList
                    {
                        Elements =
                        [
                            new HirTuple
                            {
                                TypeId = tupleType,
                                Elements =
                                [
                                    new HirLiteral { LiteralKind = LiteralKind.Int, Value = 1L, TypeId = intType },
                                    new HirLiteral { LiteralKind = LiteralKind.Bool, Value = true, TypeId = boolType }
                                ]
                            }
                        ],
                        TypeId = listType
                    }
                }
            ]
        };

        var builder = new MirBuilder(
            null,
            null,
            new Dictionary<TypeId, string>
            {
                [tupleType] = $"Tuple(T{BaseTypes.IntId},T{BaseTypes.BoolId})"
            });
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions);
        var entry = Assert.Single(func.BasicBlocks, b => b.IsEntry);

        var arrayNew = Assert.Single(
            entry.Instructions.OfType<MirCall>(),
            call => call.Function is MirFunctionRef { Name: "array_new" });
        var elementSize = Assert.IsType<MirConstant>(arrayNew.Arguments[1]);
        var elementSizeValue = Assert.IsType<MirConstantValue.IntValue>(elementSize.Value);
        Assert.Equal(16, elementSizeValue.Value);
    }

    [Fact]
    public void Build_ListComprehension_StaticSource_LowersToLoopCfgWithoutFallbackWarning()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var listType = new TypeId(521);
        var xSymbol = new SymbolId(522);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "mkListComp",
                    ReturnType = listType,
                    Body = new HirListComprehension
                    {
                        TypeId = listType,
                        Output = new HirBinOp
                        {
                            Operator = Eidosc.Hir.BinaryOp.Mul,
                            Left = new HirVar
                            {
                                Name = "x",
                                SymbolId = xSymbol,
                                TypeId = intType
                            },
                            Right = new HirLiteral
                            {
                                LiteralKind = LiteralKind.Int,
                                Value = 2L,
                                TypeId = intType
                            },
                            TypeId = intType
                        },
                        Qualifiers =
                        [
                            new HirQualifier
                            {
                                Kind = HirQualifierKind.Generator,
                                GeneratorPattern = new HirVarPattern
                                {
                                    Name = "x",
                                    SymbolId = xSymbol,
                                    TypeId = intType
                                },
                                GeneratorSource = new HirList
                                {
                                    TypeId = listType,
                                    Elements =
                                    [
                                        new HirLiteral
                                        {
                                            LiteralKind = LiteralKind.Int,
                                            Value = 1L,
                                            TypeId = intType
                                        },
                                        new HirLiteral
                                        {
                                            LiteralKind = LiteralKind.Int,
                                            Value = 2L,
                                            TypeId = intType
                                        },
                                        new HirLiteral
                                        {
                                            LiteralKind = LiteralKind.Int,
                                            Value = 3L,
                                            TypeId = intType
                                        }
                                    ]
                                }
                            },
                            new HirQualifier
                            {
                                Kind = HirQualifierKind.Guard,
                                GuardExpression = new HirBinOp
                                {
                                    Operator = Eidosc.Hir.BinaryOp.Gt,
                                    Left = new HirVar
                                    {
                                        Name = "x",
                                        SymbolId = xSymbol,
                                        TypeId = intType
                                    },
                                    Right = new HirLiteral
                                    {
                                        LiteralKind = LiteralKind.Int,
                                        Value = 1L,
                                        TypeId = intType
                                    },
                                    TypeId = boolType
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions);
        var returnBlock = Assert.Single(func.BasicBlocks, block => block.Terminator is MirReturn);
        var ret = Assert.IsType<MirReturn>(returnBlock.Terminator);
        var resultPlace = Assert.IsType<MirPlace>(ret.Value);

        Assert.True(func.BasicBlocks.Count >= 5);
        Assert.Contains(
            func.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirBinOp>(),
            instr => instr.Operator == Eidosc.Mir.BinaryOp.Lt);
        Assert.Contains(func.BasicBlocks, block => block.Terminator is MirSwitch);
        Assert.Contains(
            func.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirLoad>(),
            load => load.Source is MirPlace { Kind: PlaceKind.Index, IndexAccessKind: MirIndexAccessKind.RuntimeArray });

        var appendCalls = func.BasicBlocks
            .SelectMany(block => block.Instructions)
            .OfType<MirCall>()
            .Where(call => call.Target is MirPlace target &&
                           target.Local.Equals(resultPlace.Local) &&
                           call.Function is MirFunctionRef { Name: "array_push" })
            .ToList();
        Assert.NotEmpty(appendCalls);

        Assert.DoesNotContain(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5101");
        Assert.DoesNotContain(
            builder.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true &&
                          diagnostic.Message?.Contains("HirListComprehension", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Build_ListComprehension_UnknownSourceLength_LowersWithRuntimeLengthCall()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var listType = new TypeId(523);
        var listParamSymbol = new SymbolId(524);
        var itemSymbol = new SymbolId(525);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "mkListCompFallback",
                    ReturnType = listType,
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "src",
                            SymbolId = listParamSymbol,
                            TypeId = listType
                        }
                    ],
                    Body = new HirListComprehension
                    {
                        TypeId = listType,
                        Output = new HirVar
                        {
                            Name = "x",
                            SymbolId = itemSymbol,
                            TypeId = intType
                        },
                        Qualifiers =
                        [
                            new HirQualifier
                            {
                                Kind = HirQualifierKind.Generator,
                                GeneratorPattern = new HirVarPattern
                                {
                                    Name = "x",
                                    SymbolId = itemSymbol,
                                    TypeId = intType
                                },
                                GeneratorSource = new HirVar
                                {
                                    Name = "src",
                                    SymbolId = listParamSymbol,
                                    TypeId = listType
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions);
        var returnBlock = Assert.Single(func.BasicBlocks, block => block.Terminator is MirReturn);
        var ret = Assert.IsType<MirReturn>(returnBlock.Terminator);
        Assert.IsType<MirPlace>(ret.Value);

        Assert.Contains(
            func.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirCall>(),
            call => call.Function is MirFunctionRef { Name: "array_length" });
        Assert.Contains(func.BasicBlocks, block => block.Terminator is MirSwitch);

        Assert.DoesNotContain(
            builder.Diagnostics,
            diagnostic => diagnostic.Code == "E5101");
        Assert.DoesNotContain(
            builder.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true &&
                          diagnostic.Message?.Contains("HirListComprehension", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Build_ListComprehension_UnsupportedPattern_ReportsExplicitMirDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var listType = new TypeId(526);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "mkListCompFallbackPattern",
                    ReturnType = listType,
                    Body = new HirListComprehension
                    {
                        TypeId = listType,
                        Output = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 1L,
                            TypeId = intType
                        },
                        Qualifiers =
                        [
                            new HirQualifier
                            {
                                Kind = HirQualifierKind.Generator,
                                GeneratorPattern = new HirTuplePattern
                                {
                                    Elements =
                                    [
                                        new HirVarPattern
                                        {
                                            Name = "x",
                                            TypeId = intType
                                        }
                                    ],
                                    TypeId = TypeId.None
                                },
                                GeneratorSource = new HirList
                                {
                                    TypeId = listType,
                                    Elements =
                                    [
                                        new HirLiteral
                                        {
                                            LiteralKind = LiteralKind.Int,
                                            Value = 1L,
                                            TypeId = intType
                                        }
                                    ]
                                }
                            }
                        ]
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);

        var diagnostic = Assert.Single(
            builder.Diagnostics,
            diagnostic => diagnostic.Level == Eidosc.Diagnostic.DiagnosticLevel.Error &&
                          diagnostic.Code == "E5101");
        Assert.Contains("not supported by MIR lowering", diagnostic.Message, StringComparison.Ordinal);
        Assert.Equal("mir", diagnostic.Metadata["phase"]);
        Assert.Equal("list-comprehension", diagnostic.Metadata["feature"]);
        Assert.Equal("generator", diagnostic.Metadata["qualifier"]);
        Assert.Equal(nameof(HirVarPattern), diagnostic.Metadata["supportedPattern"]);
        Assert.Equal(nameof(HirTuplePattern), diagnostic.Metadata["actualPattern"]);
        Assert.Equal("unsupported-generator-pattern", diagnostic.Metadata["reason"]);
        Assert.Contains(
            diagnostic.Helps,
            help => help.Contains("simple variable or wildcard binding", StringComparison.Ordinal));

        var function = Assert.Single(mirModule.Functions);
        var returnTerminator = Assert.IsType<MirReturn>(Assert.Single(function.BasicBlocks).Terminator);
        var poison = Assert.IsType<MirPoison>(returnTerminator.Value);
        Assert.Equal("unsupported seq comprehension generator pattern: HirTuplePattern", poison.Reason);
        Assert.DoesNotContain(
            builder.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true &&
                          diagnostic.Message?.Contains("HirListComprehension", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Build_HirReturn_LowersToMirReturnTerminator()
    {
        var intType = new TypeId(BaseTypes.IntId);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "id",
                    ReturnType = intType,
                    Body = new HirReturn
                    {
                        TypeId = intType,
                        Value = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 7L,
                            TypeId = intType
                        }
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions);
        var returns = func.BasicBlocks
            .Where(block => block.Terminator is MirReturn)
            .Select(block => (MirReturn)block.Terminator!)
            .ToList();

        Assert.NotEmpty(returns);
        Assert.Contains(
            returns,
            ret => ret.Value is MirConstant
            {
                Value: MirConstantValue.IntValue { Value: 7L }
            });
        Assert.Contains(func.BasicBlocks, block => block.Terminator is MirUnreachable);
        Assert.DoesNotContain(
            func.BasicBlocks.SelectMany(block => block.Instructions).OfType<MirAssign>(),
            assign => assign.Source is MirConstant
            {
                Value: MirConstantValue.IntValue { Value: 0 } or
                    MirConstantValue.UnitValue
            });
        Assert.DoesNotContain(
            builder.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true &&
                          diagnostic.Message?.Contains("HirReturn", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Build_LoopWithBreak_LowersToDedicatedExitBlock()
    {
        var unitType = new TypeId(BaseTypes.UnitId);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "main",
                    ReturnType = unitType,
                    Body = new HirLoop
                    {
                        TypeId = unitType,
                        Body = new HirBreak
                        {
                            TypeId = unitType
                        }
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions);
        var entry = Assert.Single(func.BasicBlocks, block => block.IsEntry);
        var entryGoto = Assert.IsType<MirGoto>(entry.Terminator);
        var header = Assert.Single(func.BasicBlocks, block => block.Id.Equals(entryGoto.Target));
        var headerGoto = Assert.IsType<MirGoto>(header.Terminator);
        var body = Assert.Single(func.BasicBlocks, block => block.Id.Equals(headerGoto.Target));
        var breakGoto = Assert.IsType<MirGoto>(body.Terminator);
        var exit = Assert.Single(func.BasicBlocks, block => block.Id.Equals(breakGoto.Target));

        Assert.IsType<MirReturn>(exit.Terminator);
        Assert.DoesNotContain(builder.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Build_LoopWithContinue_JumpsBackToHeader()
    {
        var unitType = new TypeId(BaseTypes.UnitId);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "spin",
                    ReturnType = unitType,
                    Body = new HirLoop
                    {
                        TypeId = unitType,
                        Body = new HirContinue
                        {
                            TypeId = unitType
                        }
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions);
        var entry = Assert.Single(func.BasicBlocks, block => block.IsEntry);
        var entryGoto = Assert.IsType<MirGoto>(entry.Terminator);
        var header = Assert.Single(func.BasicBlocks, block => block.Id.Equals(entryGoto.Target));

        Assert.Contains(
            func.BasicBlocks,
            block => !block.Id.Equals(header.Id) &&
                     block.Terminator is MirGoto jump &&
                     jump.Target.Equals(header.Id));
        Assert.DoesNotContain(builder.Diagnostics, diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void Build_LambdaExpression_GeneratesMirFunctionAndFunctionRef()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var lambdaParamSymbol = new SymbolId(610);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "make",
                    ReturnType = TypeId.None,
                    Body = new HirLambda
                    {
                        Parameters =
                        [
                            new HirParam
                            {
                                Name = "x",
                                SymbolId = lambdaParamSymbol,
                                TypeId = intType
                            }
                        ],
                        ReturnType = intType,
                        Body = new HirBinOp
                        {
                            Operator = Eidosc.Hir.BinaryOp.Add,
                            Left = new HirVar
                            {
                                Name = "x",
                                SymbolId = lambdaParamSymbol,
                                TypeId = intType
                            },
                            Right = new HirLiteral
                            {
                                LiteralKind = LiteralKind.Int,
                                Value = 1L,
                                TypeId = intType
                            },
                            TypeId = intType
                        }
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        Assert.Equal(2, mirModule.Functions.Count);

        var makeFunc = Assert.Single(mirModule.Functions, f => f.Name == "make");
        var makeEntry = Assert.Single(makeFunc.BasicBlocks, b => b.IsEntry);
        var makeRet = Assert.IsType<MirReturn>(makeEntry.Terminator);
        var funcRef = Assert.IsType<MirFunctionRef>(makeRet.Value);
        Assert.StartsWith("__lambda_", funcRef.Name, StringComparison.Ordinal);

        var lambdaFunc = Assert.Single(mirModule.Functions, f => f.Name == funcRef.Name);
        Assert.Equal(funcRef.FunctionId, lambdaFunc.FunctionId);
        var lambdaStableKey = MirFunctionIdentity.GetStableKey(lambdaFunc);
        Assert.Equal(MirFunctionIdentity.GetStableKey(funcRef), lambdaStableKey);
        Assert.False(lambdaFunc.FunctionId.SymbolId.IsValid);
        Assert.Contains("synthetic:", lambdaStableKey, StringComparison.Ordinal);
        Assert.Single(lambdaFunc.Locals, l => l.IsParameter && l.Name == "x");
        Assert.Contains(lambdaFunc.BasicBlocks.SelectMany(b => b.Instructions), instr => instr is MirBinOp);
    }

    [Fact]
    public void Build_IndexAccess_LowersToIndexedLoad()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var listType = new TypeId(620);
        var listSymbol = new SymbolId(621);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "readFirst",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "list",
                            SymbolId = listSymbol,
                            TypeId = listType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirIndexAccess
                    {
                        Target = new HirVar
                        {
                            Name = "list",
                            SymbolId = listSymbol,
                            TypeId = listType
                        },
                        Index = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 0L,
                            TypeId = intType
                        },
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions);
        var entry = Assert.Single(func.BasicBlocks, block => block.IsEntry);
        var load = Assert.Single(entry.Instructions.OfType<MirLoad>());
        var source = Assert.IsType<MirPlace>(load.Source);
        var ret = Assert.IsType<MirReturn>(entry.Terminator);
        var retPlace = Assert.IsType<MirPlace>(ret.Value);

        Assert.Empty(builder.Diagnostics);
        Assert.Equal(PlaceKind.Index, source.Kind);
        Assert.Equal(MirIndexAccessKind.Aggregate, source.IndexAccessKind);
        Assert.NotNull(source.Base);
        Assert.Equal(PlaceKind.Local, source.Base!.Kind);
        Assert.Equal(load.Target.Local, retPlace.Local);
    }

    [Fact]
    public void Build_IndexAccess_WithRuntimeArrayHint_UsesRuntimeArrayIndexKind()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var listType = new TypeId(623);
        var listSymbol = new SymbolId(624);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "readFirst",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "list",
                            SymbolId = listSymbol,
                            TypeId = listType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirIndexAccess
                    {
                        Target = new HirVar
                        {
                            Name = "list",
                            SymbolId = listSymbol,
                            TypeId = listType
                        },
                        Index = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 0L,
                            TypeId = intType
                        },
                        TargetKind = HirIndexAccessKind.RuntimeArray,
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions);
        var entry = Assert.Single(func.BasicBlocks, block => block.IsEntry);
        var load = Assert.Single(entry.Instructions.OfType<MirLoad>());
        var source = Assert.IsType<MirPlace>(load.Source);

        Assert.Empty(builder.Diagnostics);
        Assert.Equal(PlaceKind.Index, source.Kind);
        Assert.Equal(MirIndexAccessKind.RuntimeArray, source.IndexAccessKind);
    }

    [Fact]
    public void Build_IndexAccess_OnListLiteral_UsesRuntimeArrayIndexKind()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var listType = new TypeId(622);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "readFirst",
                    ReturnType = intType,
                    Body = new HirIndexAccess
                    {
                        Target = new HirList
                        {
                            Elements =
                            [
                                new HirLiteral { LiteralKind = LiteralKind.Int, Value = 1L, TypeId = intType },
                                new HirLiteral { LiteralKind = LiteralKind.Int, Value = 2L, TypeId = intType }
                            ],
                            TypeId = listType
                        },
                        Index = new HirLiteral
                        {
                            LiteralKind = LiteralKind.Int,
                            Value = 0L,
                            TypeId = intType
                        },
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions);
        var entry = Assert.Single(func.BasicBlocks, block => block.IsEntry);
        var load = Assert.Single(entry.Instructions.OfType<MirLoad>());
        var source = Assert.IsType<MirPlace>(load.Source);

        Assert.Empty(builder.Diagnostics);
        Assert.Equal(PlaceKind.Index, source.Kind);
        Assert.Equal(MirIndexAccessKind.RuntimeArray, source.IndexAccessKind);
    }

}
