using Eidosc;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Types;

namespace Eidosc.Tests.Unit.Borrow;

public class ModuleEscapeAnalyzerTests
{
    private static readonly TypeId IntType = new(BaseTypes.IntId);
    private static readonly TypeId StringType = new(BaseTypes.StringId);

    private static FunctionEscapeSummary GetSummary(ModuleEscapeAnalyzer analyzer, MirFunc function)
    {
        return Assert.IsType<FunctionEscapeSummary>(
            analyzer.Summaries[MirFunctionIdentity.GetStableKey(function)]);
    }

    /// <summary>
    /// 叶子函数（不返回参数、不存储参数、不传递给其他函数）→ 参数不逃逸
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

        var analyzer = new ModuleEscapeAnalyzer(module);
        analyzer.Analyze();

        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.Empty(summary.EscapingParams);
        Assert.False(summary.IsRecursive);
    }

    /// <summary>
    /// 函数返回参数 → 该参数逃逸
    /// </summary>
    [Fact]
    public void ReturnParam_Escapes()
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

        var analyzer = new ModuleEscapeAnalyzer(module);
        analyzer.Analyze();

        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.Contains(0, summary.EscapingParams);
    }

    /// <summary>
    /// 函数通过 MirStore 存储参数 → 参数逃逸
    /// </summary>
    [Fact]
    public void StoreParam_Escapes()
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

        var analyzer = new ModuleEscapeAnalyzer(module);
        analyzer.Analyze();

        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.Contains(0, summary.EscapingParams);
    }

    /// <summary>
    /// 传递性：A 调用 B，B 不让参数逃逸 → A 中传给 B 的参数不逃逸
    /// </summary>
    [Fact]
    public void Transitive_NonEscapingCallee_CallerParamDoesNotEscape()
    {
        var calleeParam = new LocalId { Value = 1 };
        var callerParam = new LocalId { Value = 1 };
        var callerResult = new LocalId { Value = 2 };

        var module = new MirModule
        {
            Name = "Test",
            Functions =
            [
                // callee: 叶子函数，不逃逸参数
                new MirFunc
                {
                    Name = "leaf",
                    SymbolId = new SymbolId(1),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = calleeParam, Name = "x", TypeId = IntType, IsParameter = true }
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
                // caller: 调用叶子函数，参数不逃逸
                new MirFunc
                {
                    Name = "caller",
                    SymbolId = new SymbolId(2),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = callerParam, Name = "a", TypeId = IntType, IsParameter = true },
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
                                        new MirPlace { Kind = PlaceKind.Local, Local = callerParam, TypeId = IntType }
                                    ]
                                }
                            ],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleEscapeAnalyzer(module);
        analyzer.Analyze();

        // callee 不逃逸参数
        var leafSummary = GetSummary(analyzer, module.Functions[0]);
        // caller 的参数也不逃逸（因为 callee 不逃逸）
        var callerSummary = GetSummary(analyzer, module.Functions[1]);
        Assert.Empty(leafSummary.EscapingParams);
        Assert.Empty(callerSummary.EscapingParams);
    }

    /// <summary>
    /// 传递性：A 调用 B，B 让参数逃逸（返回）→ A 中传给 B 的参数也逃逸
    /// </summary>
    [Fact]
    public void Transitive_EscapingCallee_CallerParamEscapes()
    {
        var calleeParam = new LocalId { Value = 1 };
        var callerParam = new LocalId { Value = 1 };
        var callerResult = new LocalId { Value = 2 };

        var module = new MirModule
        {
            Name = "Test",
            Functions =
            [
                // callee: 返回参数 → 参数逃逸
                new MirFunc
                {
                    Name = "returns_param",
                    SymbolId = new SymbolId(1),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = calleeParam, Name = "x", TypeId = IntType, IsParameter = true }
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
                                Value = new MirPlace { Kind = PlaceKind.Local, Local = calleeParam, TypeId = IntType }
                            }
                        }
                    ]
                },
                // caller: 调用返回参数的函数 → 参数逃逸
                new MirFunc
                {
                    Name = "caller",
                    SymbolId = new SymbolId(2),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = callerParam, Name = "a", TypeId = IntType, IsParameter = true },
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
                                    Function = new MirFunctionRef { SymbolId = new SymbolId(1), Name = "returns_param", TypeId = IntType },
                                    Arguments =
                                    [
                                        new MirPlace { Kind = PlaceKind.Local, Local = callerParam, TypeId = IntType }
                                    ]
                                }
                            ],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleEscapeAnalyzer(module);
        analyzer.Analyze();

        // callee 参数逃逸
        var calleeSummary = GetSummary(analyzer, module.Functions[0]);
        // caller 参数也逃逸（传递性）
        var callerSummary = GetSummary(analyzer, module.Functions[1]);
        Assert.Contains(0, calleeSummary.EscapingParams);
        Assert.Contains(0, callerSummary.EscapingParams);
    }

    /// <summary>
    /// 递归函数 → 所有参数保守逃逸
    /// </summary>
    [Fact]
    public void SelfRecursiveFunction_AllParamsEscape()
    {
        var param0 = new LocalId { Value = 1 };
        var param1 = new LocalId { Value = 2 };
        var result = new LocalId { Value = 3 };

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
                        new MirLocal { Id = param0, Name = "n", TypeId = IntType, IsParameter = true },
                        new MirLocal { Id = param1, Name = "acc", TypeId = IntType, IsParameter = true },
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
                                        new MirPlace { Kind = PlaceKind.Local, Local = param0, TypeId = IntType },
                                        new MirPlace { Kind = PlaceKind.Local, Local = param1, TypeId = IntType }
                                    ]
                                }
                            ],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleEscapeAnalyzer(module);
        analyzer.Analyze();

        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.True(summary.IsRecursive);
        Assert.Equal(2, summary.EscapingParams.Count);
        Assert.Contains(0, summary.EscapingParams);
        Assert.Contains(1, summary.EscapingParams);
    }

    /// <summary>
    /// 多参数函数：仅部分参数逃逸
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
                        new MirLocal { Id = param0, Name = "x", TypeId = IntType, IsParameter = true },
                        new MirLocal { Id = param1, Name = "y", TypeId = IntType, IsParameter = true }
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

        var analyzer = new ModuleEscapeAnalyzer(module);
        analyzer.Analyze();

        // 只逃逸 param0（被返回），param1 不逃逸
        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.Single(summary.EscapingParams);
        Assert.Contains(0, summary.EscapingParams);
    }

    /// <summary>
    /// 调用未知/外部函数 → 所有参数位置的参数保守逃逸
    /// </summary>
    [Fact]
    public void UnknownFunctionCall_AllArgsEscape()
    {
        var param0 = new LocalId { Value = 1 };
        var param1 = new LocalId { Value = 2 };
        var result = new LocalId { Value = 3 };

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
                        new MirLocal { Id = param0, Name = "a", TypeId = IntType, IsParameter = true },
                        new MirLocal { Id = param1, Name = "b", TypeId = IntType, IsParameter = true },
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
                                    Function = new MirFunctionRef { SymbolId = new SymbolId(999), Name = "external_func", TypeId = IntType },
                                    Arguments =
                                    [
                                        new MirPlace { Kind = PlaceKind.Local, Local = param0, TypeId = IntType },
                                        new MirPlace { Kind = PlaceKind.Local, Local = param1, TypeId = IntType }
                                    ]
                                }
                            ],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleEscapeAnalyzer(module);
        analyzer.Analyze();

        // 未知函数 → 两个参数都逃逸
        var summary = GetSummary(analyzer, module.Functions[0]);
        Assert.Equal(2, summary.EscapingParams.Count);
        Assert.Contains(0, summary.EscapingParams);
        Assert.Contains(1, summary.EscapingParams);
    }

    /// <summary>
    /// 三层调用链：C(叶子) ← B(不逃逸) ← A → A 的参数不逃逸
    /// </summary>
    [Fact]
    public void ThreeLevelChain_NoEscape()
    {
        var funcCParam = new LocalId { Value = 1 };
        var funcBParam = new LocalId { Value = 1 };
        var funcBResult = new LocalId { Value = 2 };
        var funcAParam = new LocalId { Value = 1 };
        var funcAResult = new LocalId { Value = 2 };

        var module = new MirModule
        {
            Name = "Test",
            Functions =
            [
                new MirFunc
                {
                    Name = "c",
                    SymbolId = new SymbolId(1),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals = [new MirLocal { Id = funcCParam, Name = "x", TypeId = IntType, IsParameter = true }],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 }, IsEntry = true,
                            Instructions = [],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                },
                new MirFunc
                {
                    Name = "b",
                    SymbolId = new SymbolId(2),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = funcBParam, Name = "y", TypeId = IntType, IsParameter = true },
                        new MirLocal { Id = funcBResult, Name = "r", TypeId = IntType }
                    ],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 }, IsEntry = true,
                            Instructions =
                            [
                                new MirCall
                                {
                                    Target = new MirPlace { Kind = PlaceKind.Local, Local = funcBResult, TypeId = IntType },
                                    Function = new MirFunctionRef { SymbolId = new SymbolId(1), Name = "c", TypeId = IntType },
                                    Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = funcBParam, TypeId = IntType }]
                                }
                            ],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                },
                new MirFunc
                {
                    Name = "a",
                    SymbolId = new SymbolId(3),
                    EntryBlockId = new BlockId { Value = 1 },
                    Locals =
                    [
                        new MirLocal { Id = funcAParam, Name = "z", TypeId = IntType, IsParameter = true },
                        new MirLocal { Id = funcAResult, Name = "r", TypeId = IntType }
                    ],
                    BasicBlocks =
                    [
                        new MirBasicBlock
                        {
                            Id = new BlockId { Value = 1 }, IsEntry = true,
                            Instructions =
                            [
                                new MirCall
                                {
                                    Target = new MirPlace { Kind = PlaceKind.Local, Local = funcAResult, TypeId = IntType },
                                    Function = new MirFunctionRef { SymbolId = new SymbolId(2), Name = "b", TypeId = IntType },
                                    Arguments = [new MirPlace { Kind = PlaceKind.Local, Local = funcAParam, TypeId = IntType }]
                                }
                            ],
                            Terminator = new MirReturn { Value = null }
                        }
                    ]
                }
            ]
        };

        var analyzer = new ModuleEscapeAnalyzer(module);
        analyzer.Analyze();

        var funcCSummary = GetSummary(analyzer, module.Functions[0]);
        var funcBSummary = GetSummary(analyzer, module.Functions[1]);
        var funcASummary = GetSummary(analyzer, module.Functions[2]);
        Assert.Empty(funcCSummary.EscapingParams);
        Assert.Empty(funcBSummary.EscapingParams);
        Assert.Empty(funcASummary.EscapingParams);
    }
}
