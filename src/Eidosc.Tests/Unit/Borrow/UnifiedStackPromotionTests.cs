using Eidosc.Symbols;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public class UnifiedStackPromotionTests
{
    private static readonly TypeId IntType = new(BaseTypes.IntId);
    private static readonly TypeId PairType = new(10_001);

    [Fact]
    public void Analyzer_KnownNonEscapingCallee_PromotesConstructorAllocation()
    {
        var target = new LocalId { Value = 1 };
        var result = new LocalId { Value = 2 };
        var callee = FunctionRef("inspect", new SymbolId(20));
        var function = CreateConstructorThenCallFunction(target, result, callee);
        var summaries = new Dictionary<string, FieldEscapeSummary>
        {
            [MirFunctionIdentity.GetStableKey(callee)] = new()
            {
                FunctionName = "inspect"
            }
        };

        var analyzer = new UnifiedStackPromotionAnalyzer(function, summaries);
        analyzer.Analyze();

        Assert.Contains(target, analyzer.Hints.PromotedLocals);
        var info = Assert.IsType<UnifiedStackAllocInfo>(analyzer.Hints.AllocInfoByLocal[target]);
        Assert.Equal(PromotableAllocationKind.AdtConstructor, info.Kind);
        Assert.Equal(2, info.FieldCount);
    }

    [Fact]
    public void Analyzer_KnownEscapingCallee_DoesNotPromoteConstructorAllocation()
    {
        var target = new LocalId { Value = 1 };
        var result = new LocalId { Value = 2 };
        var callee = FunctionRef("store", new SymbolId(21));
        var function = CreateConstructorThenCallFunction(target, result, callee);
        var summaries = new Dictionary<string, FieldEscapeSummary>
        {
            [MirFunctionIdentity.GetStableKey(callee)] = new()
            {
                FunctionName = "store",
                ParamEscapes =
                {
                    [0] = new ParamEscapeInfo { FullyEscapes = true }
                }
            }
        };

        var analyzer = new UnifiedStackPromotionAnalyzer(function, summaries);
        analyzer.Analyze();

        Assert.DoesNotContain(target, analyzer.Hints.PromotedLocals);
        Assert.False(analyzer.Hints.AllocInfoByLocal.ContainsKey(target));
    }

    [Fact]
    public void Analyzer_ClosurePassedThroughWrapperToExternalFfi_DoesNotPromoteClosureAllocation()
    {
        var externalParam = new LocalId { Value = 1 };
        var wrapperParam = new LocalId { Value = 1 };
        var wrapperResult = new LocalId { Value = 2 };
        var wrapperAlias = new LocalId { Value = 3 };
        var callerClosure = new LocalId { Value = 1 };
        var callerResult = new LocalId { Value = 2 };
        var callerCapture = new LocalId { Value = 3 };
        var lambdaCapture = new LocalId { Value = 1 };
        var lambdaArg = new LocalId { Value = 2 };

        var external = new MirFunc
        {
            Name = "external_accepts_closure",
            SymbolId = new SymbolId(30),
            IsExternal = true,
            ExternalSymbolName = "external_accepts_closure",
            Locals =
            [
                new MirLocal { Id = externalParam, Name = "callback", TypeId = PairType, IsParameter = true }
            ]
        };

        var wrapper = new MirFunc
        {
            Name = "wrapper",
            SymbolId = new SymbolId(31),
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = wrapperParam, Name = "callback", TypeId = PairType, IsParameter = true },
                new MirLocal { Id = wrapperResult, Name = "r", TypeId = IntType },
                new MirLocal { Id = wrapperAlias, Name = "callback_alias", TypeId = PairType }
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
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = wrapperAlias, TypeId = PairType },
                            Source = new MirPlace { Kind = PlaceKind.Local, Local = wrapperParam, TypeId = PairType }
                        },
                        new MirCall
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = wrapperResult, TypeId = IntType },
                            Function = FunctionRef(external.Name, external.SymbolId),
                            Arguments =
                            [
                                new MirPlace { Kind = PlaceKind.Local, Local = wrapperAlias, TypeId = PairType }
                            ]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var lambda = new MirFunc
        {
            Name = "lambda_1",
            SymbolId = new SymbolId(32),
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = lambdaCapture, Name = "captured", TypeId = IntType, IsParameter = true },
                new MirLocal { Id = lambdaArg, Name = "arg", TypeId = IntType, IsParameter = true }
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
        };

        var caller = new MirFunc
        {
            Name = "caller",
            SymbolId = new SymbolId(33),
            EntryBlockId = new BlockId { Value = 1 },
            Locals =
            [
                new MirLocal { Id = callerClosure, Name = "closure", TypeId = PairType },
                new MirLocal { Id = callerResult, Name = "r", TypeId = IntType },
                new MirLocal { Id = callerCapture, Name = "captured", TypeId = IntType }
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
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = callerClosure, TypeId = PairType },
                            Function = FunctionRef(lambda.Name, lambda.SymbolId),
                            Arguments =
                            [
                                new MirPlace { Kind = PlaceKind.Local, Local = callerCapture, TypeId = IntType }
                            ]
                        },
                        new MirCall
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = callerResult, TypeId = IntType },
                            Function = FunctionRef(wrapper.Name, wrapper.SymbolId),
                            Arguments =
                            [
                                new MirPlace { Kind = PlaceKind.Local, Local = callerClosure, TypeId = PairType }
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
            Functions = [external, wrapper, lambda, caller]
        };
        var fieldEscapeAnalyzer = new ModuleFieldEscapeAnalyzer(module);
        fieldEscapeAnalyzer.Analyze();

        var analyzer = new UnifiedStackPromotionAnalyzer(caller, fieldEscapeAnalyzer.Summaries, module);
        analyzer.Analyze();

        Assert.DoesNotContain(callerClosure, analyzer.Hints.PromotedLocals);
        Assert.False(analyzer.Hints.AllocInfoByLocal.ContainsKey(callerClosure));
    }

    [Fact]
    public void AnalysisContext_DuplicateStableKey_RemovesStaleLookup()
    {
        var first = new MirFunc { Name = "duplicate", SymbolId = new SymbolId(40) };
        var second = new MirFunc { Name = "duplicate", SymbolId = new SymbolId(40) };
        var module = new MirModule { Name = "Test", Functions = [first, second] };
        var context = new BorrowModuleAnalysisContext(module);
        var stableKey = MirFunctionIdentity.GetStableKey(first);

        Assert.DoesNotContain(stableKey, context.FunctionByStableKey.Keys);
        Assert.False(context.TryGetFunctionIndex(stableKey, out _));
    }

    [Fact]
    public void Analyzer_FunctionArgumentClosureReturnedByCallee_UsesHeapFallback()
    {
        var (module, caller, site) = CreateFunctionArgumentScenario(FunctionArgumentUse.Returned);
        var analyzer = new UnifiedStackPromotionAnalyzer(caller, new Dictionary<string, FieldEscapeSummary>(), module);

        analyzer.Analyze();

        Assert.Equal(1, analyzer.Stats.FunctionArgumentClosureCandidates);
        Assert.Equal(0, analyzer.Stats.PromotedFunctionArgumentClosures);
        Assert.DoesNotContain(site, analyzer.Hints.FunctionArgumentClosureSites);
    }

    [Fact]
    public void Analyzer_FunctionArgumentClosureStoredThroughUnknownMemory_UsesHeapFallback()
    {
        var (module, caller, site) = CreateFunctionArgumentScenario(FunctionArgumentUse.StoredThroughDeref);
        var analyzer = new UnifiedStackPromotionAnalyzer(caller, new Dictionary<string, FieldEscapeSummary>(), module);

        analyzer.Analyze();

        Assert.Equal(1, analyzer.Stats.FunctionArgumentClosureCandidates);
        Assert.Equal(0, analyzer.Stats.PromotedFunctionArgumentClosures);
        Assert.DoesNotContain(site, analyzer.Hints.FunctionArgumentClosureSites);
    }

    [Fact]
    public void Analyzer_FunctionArgumentClosurePassedToExternal_UsesHeapFallback()
    {
        var (module, caller, site) = CreateFunctionArgumentScenario(FunctionArgumentUse.External);
        var analyzer = new UnifiedStackPromotionAnalyzer(caller, new Dictionary<string, FieldEscapeSummary>(), module);

        analyzer.Analyze();

        Assert.Equal(0, analyzer.Stats.FunctionArgumentClosureCandidates);
        Assert.Equal(0, analyzer.Stats.PromotedFunctionArgumentClosures);
        Assert.DoesNotContain(site, analyzer.Hints.FunctionArgumentClosureSites);
    }

    [Fact]
    public void Analyzer_FunctionArgumentClosureCapturedByReturnedPartialApplication_UsesHeapFallback()
    {
        var (module, caller, site) = CreatePartialApplicationCaptureScenario();
        var analyzer = new UnifiedStackPromotionAnalyzer(caller, new Dictionary<string, FieldEscapeSummary>(), module);

        analyzer.Analyze();

        Assert.Equal(1, analyzer.Stats.FunctionArgumentClosureCandidates);
        Assert.Equal(0, analyzer.Stats.PromotedFunctionArgumentClosures);
        Assert.DoesNotContain(site, analyzer.Hints.FunctionArgumentClosureSites);
    }

    private static MirFunc CreateConstructorThenCallFunction(
        LocalId target,
        LocalId result,
        MirFunctionRef callee)
    {
        var entryBlock = new BlockId { Value = 1 };

        return new MirFunc
        {
            Name = "caller",
            SymbolId = new SymbolId(10),
            EntryBlockId = entryBlock,
            Locals =
            [
                new MirLocal { Id = target, Name = "pair", TypeId = PairType },
                new MirLocal { Id = result, Name = "result", TypeId = IntType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = entryBlock,
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = PairType },
                            Function = FunctionRef("MkPair", new SymbolId(11), SymbolKind.Constructor),
                            Arguments =
                            [
                                IntConstant(1),
                                IntConstant(2)
                            ]
                        },
                        new MirCall
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = result, TypeId = IntType },
                            Function = callee,
                            Arguments =
                            [
                                new MirPlace { Kind = PlaceKind.Local, Local = target, TypeId = PairType }
                            ]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };
    }

    private static MirConstant IntConstant(long value)
    {
        return new MirConstant
        {
            TypeId = IntType,
            Value = new MirConstantValue.IntValue(value)
        };
    }

    private static MirFunctionRef FunctionRef(
        string name,
        SymbolId symbolId,
        SymbolKind symbolKind = SymbolKind.Function)
    {
        return new MirFunctionRef
        {
            Name = name,
            SymbolId = symbolId,
            SymbolKind = symbolKind,
            TypeId = IntType
        };
    }

    private static (MirModule Module, MirFunc Caller, UnifiedFunctionArgumentClosureSite Site)
        CreateFunctionArgumentScenario(FunctionArgumentUse use)
    {
        var entryBlock = new BlockId { Value = 1 };
        var callback = new MirFunc
        {
            Name = "callback",
            SymbolId = new SymbolId(50),
            EntryBlockId = entryBlock,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = entryBlock,
                    IsEntry = true,
                    Instructions = [],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        var callbackParam = new LocalId { Value = 1 };
        var storagePointer = new LocalId { Value = 2 };
        var consumer = new MirFunc
        {
            Name = "consume_callback",
            SymbolId = new SymbolId(51),
            EntryBlockId = entryBlock,
            IsExternal = use == FunctionArgumentUse.External,
            ExternalSymbolName = use == FunctionArgumentUse.External ? "consume_callback" : null,
            Locals =
            [
                new MirLocal { Id = callbackParam, Name = "callback", TypeId = PairType, IsParameter = true },
                new MirLocal { Id = storagePointer, Name = "storage", TypeId = PairType }
            ],
            BasicBlocks = use == FunctionArgumentUse.External
                ? []
                :
                [
                    new MirBasicBlock
                    {
                        Id = entryBlock,
                        IsEntry = true,
                        Instructions = use == FunctionArgumentUse.StoredThroughDeref
                            ?
                            [
                                new MirStore
                                {
                                    Target = new MirPlace
                                    {
                                        Kind = PlaceKind.Deref,
                                        Base = new MirPlace
                                        {
                                            Kind = PlaceKind.Local,
                                            Local = storagePointer,
                                            TypeId = PairType
                                        },
                                        TypeId = PairType
                                    },
                                    Value = new MirPlace
                                    {
                                        Kind = PlaceKind.Local,
                                        Local = callbackParam,
                                        TypeId = PairType
                                    }
                                }
                            ]
                            : [],
                        Terminator = new MirReturn
                        {
                            Value = use == FunctionArgumentUse.Returned
                                ? new MirPlace
                                {
                                    Kind = PlaceKind.Local,
                                    Local = callbackParam,
                                    TypeId = PairType
                                }
                                : null
                        }
                    }
                ]
        };

        var callerResult = new LocalId { Value = 1 };
        var caller = new MirFunc
        {
            Name = "caller",
            SymbolId = new SymbolId(52),
            EntryBlockId = entryBlock,
            Locals =
            [
                new MirLocal { Id = callerResult, Name = "result", TypeId = IntType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = entryBlock,
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = callerResult, TypeId = IntType },
                            Function = FunctionRef(consumer.Name, consumer.SymbolId),
                            Arguments = [FunctionRef(callback.Name, callback.SymbolId)]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        return (
            new MirModule { Name = "Test", Functions = [callback, consumer, caller] },
            caller,
            new UnifiedFunctionArgumentClosureSite(entryBlock, 0, 0));
    }

    private enum FunctionArgumentUse
    {
        Returned,
        StoredThroughDeref,
        External
    }

    private static (MirModule Module, MirFunc Caller, UnifiedFunctionArgumentClosureSite Site)
        CreatePartialApplicationCaptureScenario()
    {
        var entryBlock = new BlockId { Value = 1 };
        var callback = new MirFunc
        {
            Name = "callback",
            SymbolId = new SymbolId(60),
            EntryBlockId = entryBlock,
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = entryBlock,
                    IsEntry = true,
                    Instructions = [],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };
        var helperFirst = new LocalId { Value = 1 };
        var helperSecond = new LocalId { Value = 2 };
        var helper = new MirFunc
        {
            Name = "helper",
            SymbolId = new SymbolId(61),
            EntryBlockId = entryBlock,
            Locals =
            [
                new MirLocal { Id = helperFirst, Name = "first", TypeId = PairType, IsParameter = true },
                new MirLocal { Id = helperSecond, Name = "second", TypeId = PairType, IsParameter = true }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = entryBlock,
                    IsEntry = true,
                    Instructions = [],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };
        var consumerCallback = new LocalId { Value = 1 };
        var partialClosure = new LocalId { Value = 2 };
        var consumer = new MirFunc
        {
            Name = "capture_partial",
            SymbolId = new SymbolId(62),
            EntryBlockId = entryBlock,
            Locals =
            [
                new MirLocal { Id = consumerCallback, Name = "callback", TypeId = PairType, IsParameter = true },
                new MirLocal { Id = partialClosure, Name = "partial", TypeId = PairType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = entryBlock,
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = partialClosure, TypeId = PairType },
                            Function = FunctionRef(helper.Name, helper.SymbolId),
                            Arguments =
                            [
                                new MirPlace
                                {
                                    Kind = PlaceKind.Local,
                                    Local = consumerCallback,
                                    TypeId = PairType
                                }
                            ]
                        }
                    ],
                    Terminator = new MirReturn
                    {
                        Value = new MirPlace { Kind = PlaceKind.Local, Local = partialClosure, TypeId = PairType }
                    }
                }
            ]
        };
        var callerResult = new LocalId { Value = 1 };
        var caller = new MirFunc
        {
            Name = "caller",
            SymbolId = new SymbolId(63),
            EntryBlockId = entryBlock,
            Locals = [new MirLocal { Id = callerResult, Name = "result", TypeId = PairType }],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = entryBlock,
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = new MirPlace { Kind = PlaceKind.Local, Local = callerResult, TypeId = PairType },
                            Function = FunctionRef(consumer.Name, consumer.SymbolId),
                            Arguments = [FunctionRef(callback.Name, callback.SymbolId)]
                        }
                    ],
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };

        return (
            new MirModule { Name = "Test", Functions = [callback, helper, consumer, caller] },
            caller,
            new UnifiedFunctionArgumentClosureSite(entryBlock, 0, 0));
    }
}
