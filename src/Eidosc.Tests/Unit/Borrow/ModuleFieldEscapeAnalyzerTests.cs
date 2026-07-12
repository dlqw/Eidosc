using Eidosc;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Types;

namespace Eidosc.Tests.Unit.Borrow;

public class ModuleFieldEscapeAnalyzerTests
{
    private static readonly TypeId IntType = new(BaseTypes.IntId);
    private static readonly TypeId StringType = new(BaseTypes.StringId);

    private static FieldEscapeSummary GetSummary(ModuleFieldEscapeAnalyzer analyzer, MirFunc function)
    {
        return Assert.IsType<FieldEscapeSummary>(
            analyzer.Summaries[MirFunctionIdentity.GetStableKey(function)]);
    }

    /// <summary>
    /// 叶子函数（不返回参数、不存储参数、不传递给其他函数）
    /// → 无参数逃逸摘要
    /// </summary>
    [Fact]
    public void LeafFunction_NoEscape()
    {
        var param0 = new LocalId { Value = 1 };

        var module = new MirModule
        {
            Name = "Test",
            Functions =
            [
                new MirFunc
                {
                    Name = "leaf",
                    SymbolId = new SymbolId(1),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = param0, Name = "x", TypeId = IntType, IsParameter = true }
                    ],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions = [],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleFieldEscapeAnalyzer(module);
        analyzer.Analyze();

        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.Empty(summary.ParamEscapes);
        Assert.False(summary.IsRecursive);
    }

    /// <summary>
    /// 函数返回参数 → 该参数 FullyEscapes
    /// </summary>
    [Fact]
    public void ReturnParam_FullyEscapes()
    {
        var param0 = new LocalId { Value = 1 };

        var module = new MirModule
        {
            Name = "Test",
            Functions =
            [
                new MirFunc
                {
                    Name = "ret_param",
                    SymbolId = new SymbolId(1),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = param0, Name = "x", TypeId = IntType, IsParameter = true }
                    ],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions = [],
                            Terminator = new MirReturn
                            {
                                Value = new MirPlace { Kind = PlaceKind.Local, Local = param0, TypeId = IntType }
                            }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleFieldEscapeAnalyzer(module);
        analyzer.Analyze();

        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.True(summary.ParamEscapes[0].FullyEscapes);
        Assert.False(summary.IsRecursive);
    }

    /// <summary>
    /// 函数通过 MirStore 存储参数 → 该参数 FullyEscapes
    /// </summary>
    [Fact]
    public void StoreParam_FullyEscapes()
    {
        var param0 = new LocalId { Value = 1 };
        var local1 = new LocalId { Value = 2 };

        var module = new MirModule
        {
            Name = "Test",
            Functions =
            [
                new MirFunc
                {
                    Name = "store_param",
                    SymbolId = new SymbolId(1),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = param0, Name = "x", TypeId = IntType, IsParameter = true },
                        new MirLocal { Id = local1, Name = "slot", TypeId = IntType }
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
                                    Target = new MirPlace { Kind = PlaceKind.Local, Local = local1, TypeId = IntType },
                                    Value = new MirPlace { Kind = PlaceKind.Local, Local = param0, TypeId = IntType }
                                }
                            ],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleFieldEscapeAnalyzer(module);
        analyzer.Analyze();

        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.True(summary.ParamEscapes[0].FullyEscapes);
        Assert.False(summary.IsRecursive);
    }

    /// <summary>
    /// 传递性：caller 调用 leaf（leaf 的参数不逃逸）→ caller 的参数也不逃逸
    /// </summary>
    [Fact]
    public void Transitive_NonEscapingCallee_CallerParamDoesNotEscape()
    {
        var leafParam0 = new LocalId { Value = 1 };
        var callerParam0 = new LocalId { Value = 1 };
        var callerResult = new LocalId { Value = 2 };

        var module = new MirModule
        {
            Name = "Test",
            Functions =
            [
                new MirFunc
                {
                    Name = "leaf",
                    SymbolId = new SymbolId(1),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = leafParam0, Name = "x", TypeId = IntType, IsParameter = true }
                    ],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions = [],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                },
                new MirFunc
                {
                    Name = "caller",
                    SymbolId = new SymbolId(2),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = callerParam0, Name = "a", TypeId = IntType, IsParameter = true },
                        new MirLocal { Id = callerResult, Name = "r", TypeId = IntType }
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
                                    Target = new MirPlace { Kind = PlaceKind.Local, Local = callerResult, TypeId = IntType },
                                    Function = new MirFunctionRef { SymbolId = new SymbolId(1), Name = "leaf", TypeId = IntType },
                                    Arguments =
                                    [
                                        new MirPlace { Kind = PlaceKind.Local, Local = callerParam0, TypeId = IntType }
                                    ]
                                }
                            ],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleFieldEscapeAnalyzer(module);
        analyzer.Analyze();

        var leafSummary = GetSummary(analyzer, module.Functions[0]);
        var callerSummary = GetSummary(analyzer, module.Functions[1]);
        Assert.Empty(leafSummary.ParamEscapes);
        Assert.Empty(callerSummary.ParamEscapes);
    }

    /// <summary>
    /// 自递归函数：所有参数保守标记为 FullyEscapes
    /// </summary>
    [Fact]
    public void SelfRecursive_AllParamsFullyEscape()
    {
        var param0 = new LocalId { Value = 1 };
        var result = new LocalId { Value = 2 };

        var module = new MirModule
        {
            Name = "Test",
            Functions =
            [
                new MirFunc
                {
                    Name = "recurse",
                    SymbolId = new SymbolId(1),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = param0, Name = "x", TypeId = IntType, IsParameter = true },
                        new MirLocal { Id = result, Name = "r", TypeId = IntType }
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
                                    Target = new MirPlace { Kind = PlaceKind.Local, Local = result, TypeId = IntType },
                                    Function = new MirFunctionRef { SymbolId = new SymbolId(1), Name = "recurse", TypeId = IntType },
                                    Arguments =
                                    [
                                        new MirPlace { Kind = PlaceKind.Local, Local = param0, TypeId = IntType }
                                    ]
                                }
                            ],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleFieldEscapeAnalyzer(module);
        analyzer.Analyze();

        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.True(summary.IsRecursive);
        Assert.True(summary.ParamEscapes[0].FullyEscapes);
    }

    /// <summary>
    /// 多参数函数：只返回部分参数，未返回的参数不逃逸
    /// </summary>
    [Fact]
    public void MultiParam_PartialEscape()
    {
        var param0 = new LocalId { Value = 1 };
        var param1 = new LocalId { Value = 2 };

        var module = new MirModule
        {
            Name = "Test",
            Functions =
            [
                new MirFunc
                {
                    Name = "partial",
                    SymbolId = new SymbolId(1),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = param0, Name = "a", TypeId = IntType, IsParameter = true },
                        new MirLocal { Id = param1, Name = "b", TypeId = IntType, IsParameter = true }
                    ],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 },
                            IsEntry = true,
                            Instructions = [],
                            Terminator = new MirReturn
                            {
                                Value = new MirPlace { Kind = PlaceKind.Local, Local = param0, TypeId = IntType }
                            }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleFieldEscapeAnalyzer(module);
        analyzer.Analyze();

        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.True(summary.ParamEscapes[0].FullyEscapes);
        Assert.False(summary.ParamEscapes.ContainsKey(1));
    }

    /// <summary>
    /// 调用未知/外部函数：所有传给该函数的参数保守标记为 FullyEscapes
    /// </summary>
    [Fact]
    public void UnknownFunctionCall_AllArgsFullyEscape()
    {
        var param0 = new LocalId { Value = 1 };
        var result = new LocalId { Value = 2 };

        var module = new MirModule
        {
            Name = "Test",
            Functions =
            [
                new MirFunc
                {
                    Name = "caller",
                    SymbolId = new SymbolId(1),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = param0, Name = "x", TypeId = IntType, IsParameter = true },
                        new MirLocal { Id = result, Name = "r", TypeId = IntType }
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
                                    Target = new MirPlace { Kind = PlaceKind.Local, Local = result, TypeId = IntType },
                                    Function = new MirFunctionRef { SymbolId = new SymbolId(99), Name = "external_fn", TypeId = IntType },
                                    Arguments =
                                    [
                                        new MirPlace { Kind = PlaceKind.Local, Local = param0, TypeId = IntType }
                                    ]
                                }
                            ],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleFieldEscapeAnalyzer(module);
        analyzer.Analyze();

        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.True(summary.ParamEscapes[0].FullyEscapes);
        Assert.False(summary.IsRecursive);
    }

    [Fact]
    public void ExternalFfiFunctionInModule_CallerParamFullyEscapes()
    {
        var externalParam = new LocalId { Value = 1 };
        var wrapperParam = new LocalId { Value = 1 };
        var wrapperResult = new LocalId { Value = 2 };
        var wrapperAlias = new LocalId { Value = 3 };

        var external = new MirFunc
        {
            Name = "external_accepts_closure",
            SymbolId = new SymbolId(99),
            IsExternal = true,
            ExternalSymbolName = "external_accepts_closure",
            Locals =
            [
                new MirLocal { Id = externalParam, Name = "callback", TypeId = StringType, IsParameter = true }
            ]
        };

        var wrapper = new MirFunc
        {
            Name = "wrapper",
            SymbolId = new SymbolId(1),
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = wrapperParam, Name = "callback", TypeId = StringType, IsParameter = true },
                new MirLocal { Id = wrapperResult, Name = "r", TypeId = IntType },
                new MirLocal { Id = wrapperAlias, Name = "callback_alias", TypeId = StringType }
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
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = wrapperAlias, TypeId = StringType },
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = wrapperParam, TypeId = StringType }
                        },
                        new MirCall
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = wrapperResult, TypeId = IntType },
                            Function = new MirFunctionRef
                            {
                                SymbolId = external.SymbolId,
                                Name = external.Name,
                                TypeId = IntType
                            },
                            Arguments =
                            [
                                new MirPlace { Kind = PlaceKind.Local, Local = wrapperAlias, TypeId = StringType }
                            ]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var module = new MirModule
        {
            Name = "Test",
            Functions = [external, wrapper]
        };

        var analyzer = new ModuleFieldEscapeAnalyzer(module);
        analyzer.Analyze();

        var summary = GetSummary(analyzer, wrapper);
        Assert.True(summary.ParamEscapes[0].FullyEscapes);
        Assert.False(analyzer.Summaries.ContainsKey(MirFunctionIdentity.GetStableKey(external)));
    }
}
