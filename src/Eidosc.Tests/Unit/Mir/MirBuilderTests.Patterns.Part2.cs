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
    public void Build_ViewPatternMatch_WithCallOnGeneralExpression_ReusesSingleViewCallForScrutinee()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x
}

select_view :: Bool -> Int -> Int
{
    (b, value) => if b then { normalize(value) } else { normalize(value) }
}

classify :: Int -> Int
{
    x => match x
    {
        ((if true then { select_view } else { select_view })(true) -> 7) => 30,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_view_pattern_call_on_general_expr.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var classify = Assert.Single(mirModule.Functions, function => function.Name == "classify");
        var calls = classify.BasicBlocks
            .SelectMany(block => block.Instructions)
            .OfType<MirCall>()
            .ToList();

        var selectorCalls = calls
            .Where(call =>
                call.Arguments.Count >= 1 &&
                call.Arguments[0] is MirConstant
                {
                    Value: MirConstantValue.BoolValue { Value: true }
                })
            .ToList();
        var selectorCall = Assert.Single(selectorCalls);
        var selectorResult = Assert.IsType<MirPlace>(selectorCall.Target);

        var scrutineeCall = Assert.Single(
            calls,
            call => call.Function is MirPlace functionPlace &&
                    functionPlace.Local == selectorResult.Local);
        Assert.Contains(scrutineeCall.Arguments, argument => argument is MirPlace);
    }

    [Fact]
    public void Build_ViewPatternMatch_WithKeywordCommaGeneralExpression_FailsBeforeMir()
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
        view(if true then { normalize } else { normalize }, 7) => 30,
        _ => 0
    }
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_view_pattern_keyword_comma_general_expr.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.False(result.Success);
    }

    [Fact]
    public void Build_FunctionPatternBranchesWithGuard_LowersToMirGuardFlow()
    {
        const string source = """
abs :: Int -> Int
{
    n when n >= 0 => n,
    n => 0 - n
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mir_function_pattern_guard.eidos",
            StopAtPhase = CompilationPhase.Mir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        var mirModule = Assert.IsType<MirModule>(result.MirModule);
        var abs = Assert.Single(mirModule.Functions, function => function.Name == "abs");
        var binOps = abs.BasicBlocks
            .SelectMany(block => block.Instructions)
            .OfType<MirBinOp>()
            .ToList();

        Assert.Contains(binOps, operation => operation.Operator == Eidosc.Mir.BinaryOp.Ge);
        Assert.Contains(binOps, operation => operation.Operator == Eidosc.Mir.BinaryOp.Sub);
        Assert.Contains(abs.BasicBlocks, block => block.Terminator is MirSwitch);
    }

    [Fact]
    public void Build_CtorPatternMatch_BindsFieldPatternVariablesFromScrutinee()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var listType = new TypeId(920);
        var paramSymbol = new SymbolId(9200);
        var headSymbol = new SymbolId(9201);
        var tailSymbol = new SymbolId(9202);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirFunc
                {
                    Name = "headOrZero",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "list",
                            SymbolId = paramSymbol,
                            TypeId = listType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirMatch
                    {
                        Scrutinee = new HirVar
                        {
                            Name = "list",
                            SymbolId = paramSymbol,
                            TypeId = listType
                        },
                        Branches =
                        [
                            new HirMatchBranch
                            {
                                Pattern = new HirCtorPattern
                                {
                                    ConstructorName = "TokCons",
                                    TypeId = listType,
                                    Fields =
                                    [
                                        new HirFieldPattern
                                        {
                                            FieldName = "_0",
                                            Pattern = new HirVarPattern
                                            {
                                                Name = "head",
                                                SymbolId = headSymbol,
                                                TypeId = intType
                                            }
                                        },
                                        new HirFieldPattern
                                        {
                                            FieldName = "_1",
                                            Pattern = new HirVarPattern
                                            {
                                                Name = "tail",
                                                SymbolId = tailSymbol,
                                                TypeId = listType
                                            }
                                        }
                                    ]
                                },
                                Body = new HirVar
                                {
                                    Name = "head",
                                    SymbolId = headSymbol,
                                    TypeId = intType
                                }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern
                                {
                                    IsWildcard = true,
                                    TypeId = listType
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
        var func = Assert.Single(mirModule.Functions);
        var fieldLoads = func.BasicBlocks
            .SelectMany(block => block.Instructions)
            .OfType<MirLoad>()
            .Where(load => load.Source is MirPlace { Kind: PlaceKind.Field, FieldName: "_0" })
            .ToList();

        Assert.NotEmpty(fieldLoads);
        Assert.All(fieldLoads, load => Assert.False(load.CreatesBorrowAlias));
    }

    [Fact]
    public void Build_CtorPatternMatch_WithNamedFieldPattern_NormalizesFieldNameToOrdinal()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var optionType = new TypeId(930);
        var optionSymbol = new SymbolId(9300);
        var ctorSymbol = new SymbolId(9301);
        var matchParamSymbol = new SymbolId(9303);
        var boundValueSymbol = new SymbolId(9304);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirAdt
                {
                    Name = "Option",
                    SymbolId = optionSymbol,
                    Constructors =
                    [
                        new HirCtor
                        {
                            Name = "Some",
                            SymbolId = ctorSymbol,
                            Fields =
                            [
                                new HirField
                                {
                                    Name = "value",
                                    TypeId = intType
                                }
                            ]
                        }
                    ]
                },
                new HirFunc
                {
                    Name = "unwrapOrZero",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "opt",
                            SymbolId = matchParamSymbol,
                            TypeId = optionType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirMatch
                    {
                        Scrutinee = new HirVar
                        {
                            Name = "opt",
                            SymbolId = matchParamSymbol,
                            TypeId = optionType
                        },
                        Branches =
                        [
                            new HirMatchBranch
                            {
                                Pattern = new HirCtorPattern
                                {
                                    ConstructorName = "Some",
                                    ConstructorSymbolId = ctorSymbol,
                                    TypeId = optionType,
                                    Fields =
                                    [
                                        new HirFieldPattern
                                        {
                                            FieldName = "value",
                                            Pattern = new HirVarPattern
                                            {
                                                Name = "v",
                                                SymbolId = boundValueSymbol,
                                                TypeId = intType
                                            }
                                        }
                                    ]
                                },
                                Body = new HirVar
                                {
                                    Name = "v",
                                    SymbolId = boundValueSymbol,
                                    TypeId = intType
                                }
                            },
                            new HirMatchBranch
                            {
                                Pattern = new HirVarPattern { IsWildcard = true, TypeId = optionType },
                                Body = new HirLiteral { LiteralKind = LiteralKind.Int, Value = 0L, TypeId = intType }
                            }
                        ],
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions, item => item.Name == "unwrapOrZero");

        var normalizedFieldLoad = Assert.Single(
            func.BasicBlocks
                .SelectMany(block => block.Instructions)
                .OfType<MirLoad>(),
            load => load.Source is MirPlace { Kind: PlaceKind.Field, FieldName: "_0" });

        Assert.IsType<MirPlace>(normalizedFieldLoad.Target);
    }

    [Fact]
    public void Build_FieldAccess_WithUniqueNamedField_NormalizesFieldNameToOrdinal()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var optionType = new TypeId(931);
        var optionSymbol = new SymbolId(9310);
        var ctorSymbol = new SymbolId(9311);
        var paramSymbol = new SymbolId(9312);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirAdt
                {
                    Name = "Option",
                    SymbolId = optionSymbol,
                    Constructors =
                    [
                        new HirCtor
                        {
                            Name = "Some",
                            SymbolId = ctorSymbol,
                            Fields =
                            [
                                new HirField
                                {
                                    Name = "value",
                                    TypeId = intType
                                }
                            ]
                        }
                    ]
                },
                new HirFunc
                {
                    Name = "getValue",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "opt",
                            SymbolId = paramSymbol,
                            TypeId = optionType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirFieldAccess
                    {
                        Target = new HirVar
                        {
                            Name = "opt",
                            SymbolId = paramSymbol,
                            TypeId = optionType
                        },
                        FieldName = "value",
                        TypeId = intType
                    }
                }
            ]
        };

        var mirModule = new MirBuilder().Build(module);
        var func = Assert.Single(mirModule.Functions, item => item.Name == "getValue");
        var fieldLoad = Assert.Single(
            func.BasicBlocks
                .SelectMany(block => block.Instructions)
                .OfType<MirLoad>(),
            load => load.Source is MirPlace { Kind: PlaceKind.Field, FieldName: "_0" });

        Assert.IsType<MirPlace>(fieldLoad.Target);
    }

    [Fact]
    public void Build_FieldAccess_WithAmbiguousNamedFieldAcrossConstructors_ReportsDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var resultType = new TypeId(932);
        var resultSymbol = new SymbolId(9320);
        var okCtorSymbol = new SymbolId(9321);
        var errCtorSymbol = new SymbolId(9322);
        var paramSymbol = new SymbolId(9323);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirAdt
                {
                    Name = "Result",
                    SymbolId = resultSymbol,
                    TypeId = resultType,
                    Constructors =
                    [
                        new HirCtor
                        {
                            Name = "Ok",
                            SymbolId = okCtorSymbol,
                            Fields =
                            [
                                new HirField { Name = "value", TypeId = intType }
                            ]
                        },
                        new HirCtor
                        {
                            Name = "Err",
                            SymbolId = errCtorSymbol,
                            Fields =
                            [
                                new HirField { Name = "code", TypeId = intType },
                                new HirField { Name = "value", TypeId = intType }
                            ]
                        }
                    ]
                },
                new HirFunc
                {
                    Name = "read",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "res",
                            SymbolId = paramSymbol,
                            TypeId = resultType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirFieldAccess
                    {
                        Target = new HirVar
                        {
                            Name = "res",
                            SymbolId = paramSymbol,
                            TypeId = resultType
                        },
                        FieldName = "value",
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions, item => item.Name == "read");
        var fieldLoad = Assert.Single(
            func.BasicBlocks
                .SelectMany(block => block.Instructions)
                .OfType<MirLoad>());
        var sourceField = Assert.IsType<MirPlace>(fieldLoad.Source);

        Assert.Equal("value", sourceField.FieldName);
        Assert.Contains(builder.Diagnostics, diagnostic => diagnostic.Code == "E3205");
    }

    [Fact]
    public void Build_FieldAccess_WithConstructorSpecificField_ReportsDiagnostic()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var optionType = new TypeId(933);
        var optionSymbol = new SymbolId(9330);
        var someCtorSymbol = new SymbolId(9331);
        var noneCtorSymbol = new SymbolId(9332);
        var paramSymbol = new SymbolId(9333);

        var module = new HirModule
        {
            Name = "Main",
            Declarations =
            [
                new HirAdt
                {
                    Name = "Option",
                    SymbolId = optionSymbol,
                    TypeId = optionType,
                    Constructors =
                    [
                        new HirCtor
                        {
                            Name = "Some",
                            SymbolId = someCtorSymbol,
                            Fields =
                            [
                                new HirField { Name = "value", TypeId = intType }
                            ]
                        },
                        new HirCtor
                        {
                            Name = "None",
                            SymbolId = noneCtorSymbol
                        }
                    ]
                },
                new HirFunc
                {
                    Name = "read",
                    Parameters =
                    [
                        new HirParam
                        {
                            Name = "opt",
                            SymbolId = paramSymbol,
                            TypeId = optionType
                        }
                    ],
                    ReturnType = intType,
                    Body = new HirFieldAccess
                    {
                        Target = new HirVar
                        {
                            Name = "opt",
                            SymbolId = paramSymbol,
                            TypeId = optionType
                        },
                        FieldName = "value",
                        TypeId = intType
                    }
                }
            ]
        };

        var builder = new MirBuilder();
        var mirModule = builder.Build(module);
        var func = Assert.Single(mirModule.Functions, item => item.Name == "read");
        var fieldLoad = Assert.Single(
            func.BasicBlocks
                .SelectMany(block => block.Instructions)
                .OfType<MirLoad>());
        var sourceField = Assert.IsType<MirPlace>(fieldLoad.Source);

        Assert.Equal("value", sourceField.FieldName);
        Assert.Contains(builder.Diagnostics, diagnostic => diagnostic.Code == "E3204");
    }
}
