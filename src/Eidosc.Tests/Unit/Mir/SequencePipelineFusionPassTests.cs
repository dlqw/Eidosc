using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Borrow;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed class SequencePipelineFusionPassTests
{
    [Theory]
    [InlineData(CompilerSemanticRole.SequenceFind)]
    [InlineData(CompilerSemanticRole.SequenceAny)]
    [InlineData(CompilerSemanticRole.SequenceAll)]
    [InlineData(CompilerSemanticRole.SequenceCount)]
    [InlineData(CompilerSemanticRole.SequenceForEach)]
    public void Run_DirectTerminalIsRepresentedByUnifiedSinkPlan(CompilerSemanticRole role)
    {
        var sequenceType = new TypeId(7040);
        var optionType = new TypeId(7041);
        var refElementType = new TypeId(7042);
        var resultType = role switch
        {
            CompilerSemanticRole.SequenceFind => optionType,
            CompilerSemanticRole.SequenceCount => new TypeId(BaseTypes.IntId),
            CompilerSemanticRole.SequenceForEach => new TypeId(BaseTypes.UnitId),
            _ => new TypeId(BaseTypes.BoolId)
        };
        var source = Local(1, sequenceType);
        var result = Local(2, resultType);
        var callback = Function("predicate");
        var sinkName = role.ToString().Replace("Sequence", "", StringComparison.Ordinal).ToLowerInvariant();
        var sink = Function($"__eidos_prelude_core__Seq__{sinkName}") with
        {
            CompilerSemanticRole = role,
            FunctionId = new FunctionId { Module = "Seq", Name = sinkName }
        };
        var callbackFunction = new MirFunc
        {
            Name = callback.Name,
            FunctionId = callback.FunctionId,
            ReturnType = role == CompilerSemanticRole.SequenceForEach
                ? new TypeId(BaseTypes.UnitId)
                : new TypeId(BaseTypes.BoolId),
            Locals =
            [
                Decl(
                    Local(
                        10,
                        role == CompilerSemanticRole.SequenceFind
                            ? refElementType
                            : new TypeId(BaseTypes.IntId)),
                    "element",
                    isParameter: true)
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = BoolConstant(true) }
                }
            ]
        };
        var main = new MirFunc
        {
            Name = "main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = result.TypeId,
            Locals = [Decl(source, "source"), Decl(result, "result")],
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
                            Target = result,
                            Function = sink,
                            Arguments = [source, callback]
                        }
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "direct_any",
            Functions = [main, callbackFunction],
            TypeDescriptors =
            {
                [refElementType.Value] = new TypeDescriptor.Ref(new TypeId(BaseTypes.IntId))
            }
        };
        var pass = new SequencePipelineFusionPass();
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs = new FunctionOptimizationProofIndex(
            new FunctionOptimizationSummaryIndex(module.Functions.ToDictionary(
                MirFunctionIdentity.GetStableKey,
                static _ => FunctionOptimizationSummary.Pure,
                StringComparer.Ordinal)),
            RecursiveCallAnalysis.Analyze(module));

        var resultModule = pass.Run(module);

        Assert.NotSame(module, resultModule);
        var plan = Assert.Single(pass.DiscoveredSinkPlans);
        switch (role)
        {
            case CompilerSemanticRole.SequenceFind:
                Assert.IsType<SequenceFindSinkPlan>(plan.Sink);
                break;
            case CompilerSemanticRole.SequenceAny:
                Assert.IsType<SequenceAnySinkPlan>(plan.Sink);
                break;
            case CompilerSemanticRole.SequenceAll:
                Assert.IsType<SequenceAllSinkPlan>(plan.Sink);
                break;
            case CompilerSemanticRole.SequenceCount:
                Assert.IsType<SequenceCountSinkPlan>(plan.Sink);
                break;
            case CompilerSemanticRole.SequenceForEach:
                Assert.IsType<SequenceForEachSinkPlan>(plan.Sink);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(role), role, null);
        }
        Assert.Empty(plan.Stages);
        Assert.Equal(source.Local, plan.Source.Place.Local);
        Assert.Equal(1, pass.Stats.SinkPlansDiscovered);
        Assert.Equal(1, pass.Stats.SinkPlansLowered);
        Assert.Equal(1, pass.Stats.SourceLoopsEmitted);
        Assert.NotEmpty(Assert.Single(resultModule.Functions, candidate => candidate.Name == "main").BasicBlocks);
    }

    [Fact]
    public void Run_FilterThenHead_RewritesToCanonicalFindWhenPredicateIsPure()
    {
        var sequenceType = new TypeId(7050);
        var optionType = new TypeId(7051);
        var refElementType = new TypeId(7052);
        var boolType = new TypeId(BaseTypes.BoolId);
        var source = Local(1, sequenceType);
        var filtered = Local(2, sequenceType);
        var result = Local(3, optionType);
        var predicate = Function("predicate");
        var filter = Function("__eidos_prelude_core__Seq__filter") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceFilter,
            FunctionId = new FunctionId { Module = "Seq", Name = "filter" }
        };
        var head = Function("__eidos_prelude_core__Seq__head") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceHead,
            FunctionId = new FunctionId { Module = "Seq", Name = "head" }
        };
        var find = new MirFunc
        {
            Name = "__eidos_prelude_core__Seq__find",
            SourceName = "find",
            FunctionId = new FunctionId { Module = "Seq", Name = "find" },
            ReturnType = optionType,
            BasicBlocks = []
        };
        var predicateFunction = new MirFunc
        {
            Name = predicate.Name,
            FunctionId = predicate.FunctionId,
            ReturnType = boolType,
            Locals = [Decl(Local(10, refElementType), "element", isParameter: true)],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = BoolConstant(true) }
                }
            ]
        };
        var function = new MirFunc
        {
            Name = "main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = optionType,
            Locals = [Decl(source, "source"), Decl(filtered, "filtered"), Decl(result, "result")],
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
                            Target = filtered,
                            Function = filter,
                            Arguments = [source, predicate]
                        },
                        new MirCall
                        {
                            Target = result,
                            Function = head,
                            Arguments = [filtered]
                        }
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "filter_head",
            Functions = [function, predicateFunction, find],
            TypeDescriptors =
            {
                [refElementType.Value] = new TypeDescriptor.Ref(new TypeId(7053))
            }
        };
        var summaries = module.Functions
            .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static _ => FunctionOptimizationSummary.Pure, StringComparer.Ordinal);
        var pass = new SequencePipelineFusionPass();
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs = new FunctionOptimizationProofIndex(
            new FunctionOptimizationSummaryIndex(summaries),
            RecursiveCallAnalysis.Analyze(module));

        var resultModule = pass.Run(module);

        var main = Assert.Single(resultModule.Functions, candidate => candidate.Name == "main");
        var rewritten = Assert.IsType<MirCall>(Assert.Single(main.BasicBlocks).Instructions[0]);
        Assert.Equal(CompilerSemanticRole.SequenceFind, ((MirFunctionRef)rewritten.Function).CompilerSemanticRole);
        Assert.Equal(find.Name, ((MirFunctionRef)rewritten.Function).Name);
        Assert.Equal(1, pass.Stats.FilterHeadPipelines);
        Assert.Equal(1, pass.Stats.PipelinesFormed);
        Assert.Equal(1, pass.Stats.IntermediatesElided);
    }

    [Theory]
    [InlineData("effect")]
    [InlineData("panic")]
    [InlineData("diverge")]
    public void Run_FilterThenHead_WhenPredicateProofIsObservable_FallsBack(string proof)
    {
        var summary = proof switch
        {
            "effect" => FunctionOptimizationSummary.Pure with
            {
                Effects = new EffectRow([new EffectTag(new SymbolId(9100), "io")])
            },
            "panic" => FunctionOptimizationSummary.Pure with { MayPanic = true },
            "diverge" => FunctionOptimizationSummary.Pure with { MayDiverge = true },
            _ => throw new ArgumentOutOfRangeException(nameof(proof))
        };
        var fixture = CreateFilterHeadFixture(summary);
        var pass = fixture.Pass;

        var result = pass.Run(fixture.Module);

        Assert.Same(fixture.Module, result);
        Assert.Equal(0, pass.Stats.FilterHeadPipelines);
        Assert.Equal(2, fixture.Main.BasicBlocks[0].Instructions.Count);
        if (proof == "effect")
            Assert.Equal(1, pass.Stats.FallbackEffect);
        else
            Assert.Equal(1, pass.Stats.FallbackPanicOrDivergence);
    }

    [Fact]
    public void Run_FilterThenHead_WithSpecializedHeadName_ResolvesFindTarget()
    {
        var fixture = CreateFilterHeadFixture(FunctionOptimizationSummary.Pure);
        var headCall = Assert.IsType<MirCall>(fixture.Main.BasicBlocks[0].Instructions[1]);
        fixture.Main.BasicBlocks[0].Instructions[1] = new MirCall
        {
            Target = headCall.Target,
            Function = Assert.IsType<MirFunctionRef>(headCall.Function) with
            {
                Name = "__eidos_prelude_core__Seq__head__spec_deadbeef",
                FunctionId = new FunctionId { Module = "Seq", Name = "head" }
            },
            Arguments = headCall.Arguments,
            Span = headCall.Span
        };

        var result = fixture.Pass.Run(fixture.Module);

        var rewritten = Assert.IsType<MirCall>(Assert.Single(fixture.Main.BasicBlocks).Instructions[0]);
        var function = Assert.IsType<MirFunctionRef>(rewritten.Function);
        Assert.Equal(CompilerSemanticRole.SequenceFind, function.CompilerSemanticRole);
        Assert.Equal("__eidos_prelude_core__Seq__find", function.Name);
        Assert.Equal(2, rewritten.Arguments.Count);
        Assert.NotSame(fixture.Module, result);
    }

    [Fact]
    public void Run_FilterTakeThenHead_WithPositiveConstant_RewritesToFind()
    {
        var fixture = CreateFilterHeadFixture(FunctionOptimizationSummary.Pure);
        var block = fixture.Main.BasicBlocks[0];
        var filtered = Assert.IsType<MirPlace>(Assert.IsType<MirCall>(block.Instructions[0]).Target);
        var taken = Local(4, filtered.TypeId);
        fixture.Main.Locals.Add(Decl(taken, "taken"));
        var headCall = Assert.IsType<MirCall>(block.Instructions[1]);
        var take = Function("__eidos_prelude_core__Seq__take") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceTake,
            FunctionId = new FunctionId { Module = "Seq", Name = "take" }
        };
        block.Instructions.Insert(
            1,
            new MirCall
            {
                Target = taken,
                Function = take,
                Arguments = [filtered, Constant(2)]
            });
        block.Instructions[2] = headCall with { Arguments = [taken] };

        var result = fixture.Pass.Run(fixture.Module);

        Assert.NotSame(fixture.Module, result);
        var rewritten = Assert.IsType<MirCall>(Assert.Single(block.Instructions));
        Assert.Equal(CompilerSemanticRole.SequenceFind, ((MirFunctionRef)rewritten.Function).CompilerSemanticRole);
        Assert.Equal(fixture.Source.Local, Assert.IsType<MirPlace>(rewritten.Arguments[0]).Local);
        Assert.Equal(2, rewritten.Arguments.Count);
        Assert.Equal(1, fixture.Pass.Stats.FilterHeadPipelines);
        Assert.Equal(2, fixture.Pass.Stats.IntermediatesElided);
    }

    [Fact]
    public void Run_FilterThenHead_WhenSourceHasSecondUse_FallsBack()
    {
        var fixture = CreateFilterHeadFixture(FunctionOptimizationSummary.Pure);
        fixture.Main.BasicBlocks[0].Instructions.Add(new MirCall
        {
            Function = Function("unknown_consumer"),
            Arguments = [fixture.Source]
        });
        var pass = fixture.Pass;

        var result = pass.Run(fixture.Module);

        Assert.Same(fixture.Module, result);
        Assert.Equal(0, pass.Stats.FilterHeadPipelines);
        Assert.Equal(1, pass.Stats.FallbackMultiUse);
    }

    [Fact]
    public void Run_FilterThenHead_WhenPredicateParameterIsNotSharedBorrow_FallsBack()
    {
        var fixture = CreateFilterHeadFixture(
            FunctionOptimizationSummary.Pure,
            new TypeId(BaseTypes.IntId));
        var pass = fixture.Pass;

        var result = pass.Run(fixture.Module);

        Assert.Same(fixture.Module, result);
        Assert.Equal(0, pass.Stats.FilterHeadPipelines);
        Assert.Equal(1, pass.Stats.FallbackUnknownCallback);
    }

    [Fact]
    public void Run_TakeThenHead_WithPositiveConstant_RewritesToHead()
    {
        var fixture = CreateTakeHeadFixture(2);

        var result = fixture.Pass.Run(fixture.Module);

        Assert.NotSame(fixture.Module, result);
        Assert.Equal(1, fixture.Pass.Stats.TakeHeadPipelines);
        Assert.Equal(1, fixture.Pass.Stats.PipelinesFormed);
        var call = Assert.IsType<MirCall>(Assert.Single(fixture.Main.BasicBlocks[0].Instructions));
        Assert.Equal(CompilerSemanticRole.SequenceHead, ((MirFunctionRef)call.Function).CompilerSemanticRole);
        Assert.Equal(fixture.Source.Local, Assert.IsType<MirPlace>(call.Arguments[0]).Local);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Run_TakeThenHead_WhenBoundIsNotPositive_FallsBack(long bound)
    {
        var fixture = CreateTakeHeadFixture(bound);

        var result = fixture.Pass.Run(fixture.Module);

        Assert.Same(fixture.Module, result);
        Assert.Equal(0, fixture.Pass.Stats.TakeHeadPipelines);
        Assert.Equal(2, fixture.Main.BasicBlocks[0].Instructions.Count);
    }

    [Fact]
    public void Run_TakeThenTake_WithPositiveConstants_ComposesBounds()
    {
        var fixture = CreateTakeTakeFixture(8, 3);

        var result = fixture.Pass.Run(fixture.Module);

        Assert.NotSame(fixture.Module, result);
        Assert.Equal(1, fixture.Pass.Stats.TakeTakePipelines);
        Assert.Equal(1, fixture.Pass.Stats.IntermediatesElided);
        var call = Assert.IsType<MirCall>(Assert.Single(fixture.Main.BasicBlocks[0].Instructions));
        Assert.Equal(CompilerSemanticRole.SequenceTake, ((MirFunctionRef)call.Function).CompilerSemanticRole);
        Assert.Equal(fixture.Source.Local, Assert.IsType<MirPlace>(call.Arguments[0]).Local);
        Assert.Equal(3, Assert.IsType<MirConstant>(call.Arguments[1]).Value is MirConstantValue.IntValue value
            ? value.Value
            : -1);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(-1, 3)]
    public void Run_TakeThenTake_WhenAnyBoundIsNotPositive_FallsBack(long first, long second)
    {
        var fixture = CreateTakeTakeFixture(first, second);

        var result = fixture.Pass.Run(fixture.Module);

        Assert.Same(fixture.Module, result);
        Assert.Equal(0, fixture.Pass.Stats.TakeTakePipelines);
        Assert.Equal(2, fixture.Main.BasicBlocks[0].Instructions.Count);
    }

    [Fact]
    public void Run_DropThenDrop_WithPositiveConstants_ComposesBounds()
    {
        var fixture = CreateDropDropFixture(5, 7);

        var result = fixture.Pass.Run(fixture.Module);

        Assert.NotSame(fixture.Module, result);
        Assert.Equal(1, fixture.Pass.Stats.DropDropPipelines);
        var call = Assert.IsType<MirCall>(Assert.Single(fixture.Main.BasicBlocks[0].Instructions));
        Assert.Equal(CompilerSemanticRole.SequenceDrop, ((MirFunctionRef)call.Function).CompilerSemanticRole);
        Assert.Equal(fixture.Source.Local, Assert.IsType<MirPlace>(call.Arguments[0]).Local);
        Assert.Equal(12, Assert.IsType<MirConstant>(call.Arguments[1]).Value is MirConstantValue.IntValue value
            ? value.Value
            : -1);
    }

    [Theory]
    [InlineData(0, 3)]
    [InlineData(3, 0)]
    [InlineData(-1, 3)]
    [InlineData(long.MaxValue, 1)]
    public void Run_DropThenDrop_WhenBoundsCannotCompose_FallsBack(long first, long second)
    {
        var fixture = CreateDropDropFixture(first, second);

        var result = fixture.Pass.Run(fixture.Module);

        Assert.Same(fixture.Module, result);
        Assert.Equal(0, fixture.Pass.Stats.DropDropPipelines);
        Assert.Equal(2, fixture.Main.BasicBlocks[0].Instructions.Count);
    }

    [Fact]
    public void Run_DirectFoldOverCopyValues_LowersToSingleLoop()
    {
        var sequenceType = new TypeId(7001);
        var intType = new TypeId(BaseTypes.IntId);
        var source = Local(1, sequenceType);
        var folded = Local(2, intType);
        var reducer = Function("reducer");
        var reducerLeft = Local(10, intType);
        var reducerRight = Local(11, intType);
        var reducerResult = Local(12, intType);

        var function = new MirFunc
        {
            Name = "main",
            ReturnType = intType,
            Locals = [Decl(source, "source"), Decl(folded, "folded")],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        RoleCall(
                            folded,
                            CompilerSemanticRole.SequenceFoldLeft,
                            source,
                            Constant(0),
                            reducer)
                    ],
                    Terminator = new MirReturn { Value = folded }
                }
            ]
        };
        var reducerFunction = new MirFunc
        {
            Name = "reducer",
            FunctionId = reducer.FunctionId,
            ReturnType = intType,
            Locals =
            [
                Decl(reducerLeft, "left", isParameter: true),
                Decl(reducerRight, "right", isParameter: true),
                Decl(reducerResult, "result")
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirBinOp
                        {
                            Target = reducerResult,
                            Operator = BinaryOp.Add,
                            Left = reducerLeft,
                            Right = reducerRight
                        }
                    ],
                    Terminator = new MirReturn { Value = reducerResult }
                }
            ]
        };
        var module = new MirModule { Name = "direct_fold", Functions = [function, reducerFunction] };
        var pass = new SequencePipelineFusionPass();

        var result = pass.Run(module);

        Assert.NotSame(module, result);
        Assert.Equal(1, pass.Stats.DirectFoldsLowered);
        Assert.Equal(0, pass.Stats.PipelinesFormed);
        Assert.Equal(1, pass.Stats.SourceLoopsEmitted);
        Assert.Equal(0, pass.Stats.MapFoldPipelines);
        Assert.DoesNotContain(
            function.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(),
            static call => call.Function is MirFunctionRef
            {
                CompilerSemanticRole: CompilerSemanticRole.SequenceFoldLeft
            });
        Assert.Contains(function.Locals, static local => local.Name == "__sequence_fold_index");
    }

    [Fact]
    public void Run_DirectFoldWithNonCopyAccumulator_FallsBackWithoutMutation()
    {
        var sequenceType = new TypeId(7001);
        var aggregateType = new TypeId(7002);
        var intType = new TypeId(BaseTypes.IntId);
        var source = Local(1, sequenceType);
        var initial = Local(2, aggregateType);
        var folded = Local(3, aggregateType);
        var reducer = Function("aggregate_reducer");
        var reducerAccumulator = Local(10, aggregateType);
        var reducerElement = Local(11, intType);

        var function = new MirFunc
        {
            Name = "main",
            ReturnType = aggregateType,
            Locals =
            [
                Decl(source, "source"),
                Decl(initial, "initial"),
                Decl(folded, "folded")
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        RoleCall(
                            folded,
                            CompilerSemanticRole.SequenceFoldLeft,
                            source,
                            initial,
                            reducer)
                    ],
                    Terminator = new MirReturn { Value = folded }
                }
            ]
        };
        var reducerFunction = new MirFunc
        {
            Name = "aggregate_reducer",
            FunctionId = reducer.FunctionId,
            ReturnType = aggregateType,
            Locals =
            [
                Decl(reducerAccumulator, "accumulator", isParameter: true),
                Decl(reducerElement, "element", isParameter: true)
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = reducerAccumulator }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "direct_fold_non_copy",
            Functions = [function, reducerFunction]
        };
        var pass = new SequencePipelineFusionPass();

        var result = pass.Run(module);

        Assert.Same(module, result);
        Assert.Equal(0, pass.Stats.DirectFoldsLowered);
        Assert.Equal(1, pass.Stats.FallbackOwnership);
        Assert.Single(function.BasicBlocks[0].Instructions);
    }

    [Fact]
    public void Run_MapIntermediateWithSecondReader_FallsBackWithoutMutation()
    {
        var sequenceType = new TypeId(7001);
        var intType = new TypeId(BaseTypes.IntId);
        var source = Local(1, sequenceType);
        var mapped = Local(2, sequenceType);
        var filtered = Local(3, sequenceType);
        var folded = Local(4, intType);
        var secondReader = Local(5, sequenceType);
        var mapper = Function("mapper");
        var predicate = Function("predicate");
        var reducer = Function("reducer");

        var function = new MirFunc
        {
            Name = "main",
            ReturnType = intType,
            Locals =
            [
                Decl(source, "source"),
                Decl(mapped, "mapped"),
                Decl(filtered, "filtered"),
                Decl(folded, "folded"),
                Decl(secondReader, "second_reader")
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        RoleCall(mapped, CompilerSemanticRole.SequenceMap, source, mapper),
                        RoleCall(filtered, CompilerSemanticRole.SequenceFilter, mapped, predicate),
                        RoleCall(
                            folded,
                            CompilerSemanticRole.SequenceFoldLeft,
                            filtered,
                            Constant(0),
                            reducer),
                        new MirCopy { Target = secondReader, Source = mapped }
                    ],
                    Terminator = new MirReturn { Value = folded }
                }
            ]
        };
        var module = new MirModule { Name = "multi_use", Functions = [function] };
        var pass = new SequencePipelineFusionPass();

        var result = pass.Run(module);

        Assert.Same(module, result);
        Assert.Equal(0, pass.Stats.PipelinesFormed);
        Assert.Equal(1, pass.Stats.FallbackMultiUse);
        Assert.Equal(4, function.BasicBlocks[0].Instructions.Count);
    }

    private static MirCall RoleCall(
        MirPlace target,
        CompilerSemanticRole role,
        params MirOperand[] arguments) => new()
    {
        Target = target,
        Function = Function(role.ToString()) with { CompilerSemanticRole = role },
        Arguments = [.. arguments]
    };

    private static MirFunctionRef Function(string name) => new()
    {
        Name = name,
        SymbolKind = SymbolKind.Function,
        FunctionId = new FunctionId
        {
            Name = name,
            QualifiedName = $"test:{name}",
            Module = "test",
            Kind = SymbolKind.Function
        }
    };

    private static MirPlace Local(int id, TypeId type) => new()
    {
        Kind = PlaceKind.Local,
        Local = new LocalId { Value = id },
        TypeId = type
    };

    [Fact]
    public void Run_TakeDropReverseAny_LowersOneInternalViewLoop()
    {
        var fixture = CreateViewSinkFixture(dynamicTakeBound: false);

        var result = fixture.Pass.Run(fixture.Module);

        Assert.NotSame(fixture.Module, result);
        var plan = Assert.Single(fixture.Pass.DiscoveredSinkPlans);
        Assert.Collection(
            plan.Stages,
            stage => Assert.IsType<SequenceTakeViewStagePlan>(stage),
            stage => Assert.IsType<SequenceDropViewStagePlan>(stage),
            stage => Assert.IsType<SequenceReverseStagePlan>(stage));
        Assert.True(plan.StoragePlan.UseInternalView);
        Assert.Equal(3, fixture.Pass.Stats.IntermediatesElided);
        Assert.DoesNotContain(
            fixture.Main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    (functionRef.Name.Contains("__Seq__take", StringComparison.Ordinal) ||
                     functionRef.Name.Contains("__Seq__drop", StringComparison.Ordinal) ||
                     functionRef.Name.Contains("__Seq__reverse", StringComparison.Ordinal) ||
                     functionRef.Name.Contains("__Seq__any", StringComparison.Ordinal)));
        var length = Assert.Single(
            fixture.Main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    MirRuntimeFunctions.HasIdentity(
                        functionRef,
                        WellKnownStrings.InternalNames.ArrayLength));
        Assert.Equal(fixture.Source.Local, Assert.IsType<MirPlace>(length.Arguments[0]).Local);
        Assert.Contains(
            fixture.Main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirBinOp>(),
            operation => operation.Operator == BinaryOp.Sub &&
                         operation.Target is MirPlace { Kind: PlaceKind.Local });
    }

    [Fact]
    public void Run_DynamicTakeBeforeAny_KeepsMaterializedTakeAndLowersSinkOnly()
    {
        var fixture = CreateViewSinkFixture(dynamicTakeBound: true);

        fixture.Pass.Run(fixture.Module);

        var plan = Assert.Single(fixture.Pass.DiscoveredSinkPlans);
        Assert.Empty(plan.Stages);
        Assert.False(plan.StoragePlan.UseInternalView);
        Assert.Contains(
            fixture.Main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(),
            call => call.Function is MirFunctionRef functionRef &&
                    functionRef.Name.Contains("__Seq__take", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_ZipWithThenFold_LowersBoundedDualSourceLoop()
    {
        var sequenceType = new TypeId(7110);
        var intType = new TypeId(BaseTypes.IntId);
        var left = Local(1, sequenceType);
        var right = Local(2, sequenceType);
        var zipped = Local(3, sequenceType);
        var result = Local(4, intType);
        var combiner = Function("zip_combiner");
        var reducer = Function("zip_reducer");
        var zipWith = Function("__eidos_prelude_core__Seq__zip_with") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceZipWith,
            FunctionId = new FunctionId { Module = "Seq", Name = "zip_with" }
        };
        var fold = Function("__eidos_prelude_core__Seq__fold_left") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceFoldLeft,
            FunctionId = new FunctionId { Module = "Seq", Name = "fold_left" }
        };
        var combinerFunction = new MirFunc
        {
            Name = combiner.Name,
            FunctionId = combiner.FunctionId,
            ReturnType = intType,
            Locals = [Decl(Local(10, intType), "left", true), Decl(Local(11, intType), "right", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 2 }, IsEntry = true, Terminator = new MirReturn { Value = Local(10, intType) } }]
        };
        var reducerFunction = new MirFunc
        {
            Name = reducer.Name,
            FunctionId = reducer.FunctionId,
            ReturnType = intType,
            Locals = [Decl(Local(12, intType), "acc", true), Decl(Local(13, intType), "value", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 3 }, IsEntry = true, Terminator = new MirReturn { Value = Local(12, intType) } }]
        };
        var main = new MirFunc
        {
            Name = "zip_main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = intType,
            Locals = [Decl(left, "left"), Decl(right, "right"), Decl(zipped, "zipped"), Decl(result, "result")],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall { Target = zipped, Function = zipWith, Arguments = [left, right, combiner] },
                        new MirCall { Target = result, Function = fold, Arguments = [zipped, Constant(0), reducer] }
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
        var module = new MirModule { Name = "zip_fold", Functions = [main, combinerFunction, reducerFunction] };
        var pass = new SequencePipelineFusionPass();
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs = new FunctionOptimizationProofIndex(
            new FunctionOptimizationSummaryIndex(module.Functions.ToDictionary(
                MirFunctionIdentity.GetStableKey,
                static _ => FunctionOptimizationSummary.Pure,
                StringComparer.Ordinal)),
            RecursiveCallAnalysis.Analyze(module));

        var resultModule = pass.Run(module);

        Assert.NotSame(module, resultModule);
        Assert.Equal(1, pass.Stats.ZipWithFoldPipelines);
        Assert.Contains(main.BasicBlocks, block => block.Instructions.OfType<MirCall>().Any(call =>
            call.Function is MirFunctionRef functionRef &&
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayLength)));
        Assert.DoesNotContain(main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(), call =>
            call.Function is MirFunctionRef functionRef &&
            (functionRef.CompilerSemanticRole == CompilerSemanticRole.SequenceZipWith ||
             functionRef.CompilerSemanticRole == CompilerSemanticRole.SequenceFoldLeft));
    }

    [Fact]
    public void Run_Partition_LowersOnePredicateAndTwoCollectors()
    {
        var sequenceType = new TypeId(7120);
        var refType = new TypeId(7121);
        var tupleType = new TypeId(7122);
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var source = Local(1, sequenceType);
        var result = Local(2, tupleType);
        var predicate = Function("partition_predicate");
        var partition = Function("__eidos_prelude_core__Seq__partition") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequencePartition,
            FunctionId = new FunctionId { Module = "Seq", Name = "partition" }
        };
        var predicateFunction = new MirFunc
        {
            Name = predicate.Name,
            FunctionId = predicate.FunctionId,
            ReturnType = boolType,
            Locals = [Decl(Local(10, refType), "value", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 2 }, IsEntry = true, Terminator = new MirReturn { Value = BoolConstant(true) } }]
        };
        var main = new MirFunc
        {
            Name = "partition_main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = tupleType,
            Locals = [Decl(source, "source"), Decl(result, "result")],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions = [new MirCall { Target = result, Function = partition, Arguments = [source, predicate] }],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "partition",
            Functions = [main, predicateFunction],
            TypeDescriptors =
            {
                [refType.Value] = new TypeDescriptor.Ref(intType),
                [tupleType.Value] = new TypeDescriptor.Tuple([sequenceType, sequenceType])
            }
        };
        var pass = new SequencePipelineFusionPass();
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs = new FunctionOptimizationProofIndex(
            new FunctionOptimizationSummaryIndex(module.Functions.ToDictionary(
                MirFunctionIdentity.GetStableKey,
                static _ => FunctionOptimizationSummary.Pure,
                StringComparer.Ordinal)),
            RecursiveCallAnalysis.Analyze(module));

        var resultModule = pass.Run(module);

        Assert.NotSame(module, resultModule);
        Assert.Equal(1, pass.Stats.PartitionSinksLowered);
        Assert.Contains(main.BasicBlocks.SelectMany(static block => block.Instructions), instruction => instruction is MirAlloc);
        Assert.Equal(2, main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>().Count(call =>
            call.Function is MirFunctionRef functionRef &&
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayNew)));
    }

    [Fact]
    public void Run_Partition_NonCopyRequiresOwnershipSnapshotAndMovesFromSource()
    {
        var elementType = new TypeId(7160);
        var sequenceType = new TypeId(7161);
        var tupleType = new TypeId(7162);
        var refType = new TypeId(7163);
        var boolType = new TypeId(BaseTypes.BoolId);
        var source = Local(1, sequenceType);
        var result = Local(2, tupleType);
        var predicate = Function("partition_move_predicate");
        var partition = Function("__eidos_prelude_core__Seq__partition") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequencePartition,
            FunctionId = new FunctionId { Module = "Seq", Name = "partition" }
        };
        var predicateFunction = new MirFunc
        {
            Name = predicate.Name,
            FunctionId = predicate.FunctionId,
            ReturnType = boolType,
            Locals = [Decl(Local(10, refType), "value", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 2 }, IsEntry = true, Terminator = new MirReturn { Value = BoolConstant(true) } }]
        };
        var main = new MirFunc
        {
            Name = "partition_move_main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = tupleType,
            Locals = [Decl(source, "source", true), Decl(result, "result")],
            BasicBlocks = [new MirBasicBlock
            {
                Id = new BlockId { Value = 1 },
                IsEntry = true,
                Instructions = [new MirCall { Target = result, Function = partition, Arguments = [source, predicate] }],
                Terminator = new MirReturn { Value = result }
            }]
        };
        var module = new MirModule
        {
            Name = "partition_move_only",
            Functions = [main, predicateFunction],
            CopyLikeTypeIds = [refType.Value],
            TypeDescriptors =
            {
                [sequenceType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromTypeId(new TypeId(7164)), [elementType]),
                [tupleType.Value] = new TypeDescriptor.Tuple([sequenceType, sequenceType]),
                [refType.Value] = new TypeDescriptor.Ref(elementType)
            },
            TypeConstructors = [new MirTypeConstructorInfo { Name = WellKnownStrings.BuiltinTypes.Seq, TypeId = new TypeId(7164) }]
        };
        var usage = new VariableUsageAnalyzer(main);
        usage.Analyze();
        var cfg = new ControlFlowGraph(main);
        var liveness = new LivenessAnalyzer(main, usage, cfg);
        liveness.Analyze();
        var checker = new BorrowChecker(main, liveness, capturePointStates: true, cfg: cfg);
        checker.Check();
        var perceus = new PerceusAnalyzer(main, liveness, usage);
        perceus.Analyze();
        var reuse = new ReuseAnalyzer(main, perceus.Hints);
        reuse.Analyze();
        var verifier = new LoanConstraintVerifier(new LoanSignatureCache(), new SymbolTable(), capturePointStates: true);
        var snapshot = OwnershipAnalysisSnapshot.Build(main, cfg, usage, liveness, checker, verifier, perceus, reuse, []);
        var pass = new SequencePipelineFusionPass();
        ((IOwnershipAnalysisSnapshotConsumer)pass).OwnershipSnapshots = new Dictionary<string, OwnershipAnalysisSnapshot>
        {
            [MirFunctionIdentity.GetStableKey(main)] = snapshot
        };

        pass.Run(module);

        Assert.Equal(1, pass.Stats.PartitionSinksLowered);
        Assert.Contains(main.BasicBlocks.SelectMany(static block => block.Instructions), instruction =>
            instruction is MirLoad { MovesOutOfSource: true });
    }

    [Fact]
    public void Run_FlatMapThenCount_LowersNestedLoopsAndDropsInnerSequence()
    {
        var outerSequenceType = new TypeId(7130);
        var innerSequenceType = new TypeId(7131);
        var seqConstructorType = new TypeId(7132);
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var source = Local(1, outerSequenceType);
        var flattened = Local(2, innerSequenceType);
        var result = Local(3, intType);
        var mapper = Function("flat_mapper");
        var predicate = Function("flat_predicate");
        var flatMap = Function("__eidos_prelude_core__Seq__flat_map") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceFlatMap,
            FunctionId = new FunctionId { Module = "Seq", Name = "flat_map" }
        };
        var count = Function("__eidos_prelude_core__Seq__count") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceCount,
            FunctionId = new FunctionId { Module = "Seq", Name = "count" }
        };
        var mapperFunction = new MirFunc
        {
            Name = mapper.Name,
            FunctionId = mapper.FunctionId,
            ReturnType = innerSequenceType,
            Locals = [Decl(Local(10, intType), "value", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 2 }, IsEntry = true, Terminator = new MirReturn { Value = Local(10, intType) } }]
        };
        var predicateFunction = new MirFunc
        {
            Name = predicate.Name,
            FunctionId = predicate.FunctionId,
            ReturnType = boolType,
            Locals = [Decl(Local(11, intType), "value", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 3 }, IsEntry = true, Terminator = new MirReturn { Value = BoolConstant(true) } }]
        };
        var main = new MirFunc
        {
            Name = "flat_map_count_main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = intType,
            Locals = [Decl(source, "source"), Decl(flattened, "flattened"), Decl(result, "result")],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall { Target = flattened, Function = flatMap, Arguments = [source, mapper] },
                        new MirCall { Target = result, Function = count, Arguments = [flattened, predicate] }
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "flat_map_count",
            Functions = [main, mapperFunction, predicateFunction],
            TypeConstructors = [new MirTypeConstructorInfo { Name = WellKnownStrings.BuiltinTypes.Seq, TypeId = seqConstructorType }],
            TypeDescriptors =
            {
                [innerSequenceType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromTypeId(seqConstructorType), [intType])
            }
        };
        var pass = new SequencePipelineFusionPass();
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs = new FunctionOptimizationProofIndex(
            new FunctionOptimizationSummaryIndex(module.Functions.ToDictionary(
                MirFunctionIdentity.GetStableKey,
                static _ => FunctionOptimizationSummary.Pure,
                StringComparer.Ordinal)),
            RecursiveCallAnalysis.Analyze(module));

        pass.Run(module);

        Assert.Equal(1, pass.Stats.FlatMapCountPipelines);
        Assert.Equal(1, pass.Stats.PipelinesFormed);
        Assert.Contains(main.BasicBlocks, block => block.Instructions.OfType<MirDrop>().Any(drop =>
            drop.Value is MirPlace { Kind: PlaceKind.Local, Local: var local } && local.Value > flattened.Local.Value));
        Assert.DoesNotContain(main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(), call =>
            call.Function is MirFunctionRef functionRef &&
            (functionRef.CompilerSemanticRole == CompilerSemanticRole.SequenceFlatMap ||
             functionRef.CompilerSemanticRole == CompilerSemanticRole.SequenceCount));
    }

    [Fact]
    public void Run_FlatMapThenFold_LowersNestedLoopsAndDropsInnerSequence()
    {
        var sequenceType = new TypeId(7180);
        var seqConstructorType = new TypeId(7181);
        var intType = new TypeId(BaseTypes.IntId);
        var source = Local(1, sequenceType);
        var flattened = Local(2, sequenceType);
        var result = Local(3, intType);
        var mapper = Function("flat_mapper_fold");
        var reducer = Function("flat_reducer");
        var flatMap = Function("__eidos_prelude_core__Seq__flat_map") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceFlatMap,
            FunctionId = new FunctionId { Module = "Seq", Name = "flat_map" }
        };
        var fold = Function("__eidos_prelude_core__Seq__fold_left") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceFoldLeft,
            FunctionId = new FunctionId { Module = "Seq", Name = "fold_left" }
        };
        var mapperFunction = new MirFunc
        {
            Name = mapper.Name,
            FunctionId = mapper.FunctionId,
            ReturnType = sequenceType,
            Locals = [Decl(Local(10, intType), "value", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 2 }, IsEntry = true, Terminator = new MirReturn { Value = Local(10, intType) } }]
        };
        var reducerFunction = new MirFunc
        {
            Name = reducer.Name,
            FunctionId = reducer.FunctionId,
            ReturnType = intType,
            Locals = [Decl(Local(11, intType), "acc", true), Decl(Local(12, intType), "value", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 3 }, IsEntry = true, Terminator = new MirReturn { Value = Local(11, intType) } }]
        };
        var main = new MirFunc
        {
            Name = "flat_map_fold_main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = intType,
            Locals = [Decl(source, "source"), Decl(flattened, "flattened"), Decl(result, "result")],
            BasicBlocks = [new MirBasicBlock
            {
                Id = new BlockId { Value = 1 },
                IsEntry = true,
                Instructions =
                [
                    new MirCall { Target = flattened, Function = flatMap, Arguments = [source, mapper] },
                    new MirCall { Target = result, Function = fold, Arguments = [flattened, new MirConstant { Value = new MirConstantValue.IntValue(0), TypeId = intType }, reducer] }
                ],
                Terminator = new MirReturn { Value = result }
            }]
        };
        var module = new MirModule
        {
            Name = "flat_map_fold",
            Functions = [main, mapperFunction, reducerFunction],
            TypeConstructors = [new MirTypeConstructorInfo { Name = WellKnownStrings.BuiltinTypes.Seq, TypeId = seqConstructorType }],
            TypeDescriptors = { [sequenceType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromTypeId(seqConstructorType), [intType]) }
        };
        var pass = new SequencePipelineFusionPass();
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs = new FunctionOptimizationProofIndex(
            new FunctionOptimizationSummaryIndex(module.Functions.ToDictionary(
                MirFunctionIdentity.GetStableKey,
                static _ => FunctionOptimizationSummary.Pure,
                StringComparer.Ordinal)),
            RecursiveCallAnalysis.Analyze(module));

        pass.Run(module);

        Assert.Equal(1, pass.Stats.FlatMapFoldPipelines);
        Assert.Equal(2, pass.Stats.SourceLoopsEmitted);
        Assert.Contains(main.BasicBlocks.SelectMany(static block => block.Instructions), instruction => instruction is MirDrop);
        Assert.DoesNotContain(main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(), call =>
            call.Function is MirFunctionRef functionRef &&
                functionRef.CompilerSemanticRole is CompilerSemanticRole.SequenceFlatMap or CompilerSemanticRole.SequenceFoldLeft);
    }

    [Fact]
    public void Run_FlatMapThenCollect_LowersNestedLoopsAndMaterializesOneCollector()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var sequenceType = new TypeId(7185);
        var seqConstructorType = new TypeId(7186);
        var source = Local(1, sequenceType);
        var flattened = Local(2, sequenceType);
        var result = Local(3, sequenceType);
        var mapper = Function("flat_mapper_collect");
        var flatMap = Function("__eidos_prelude_core__Seq__flat_map") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceFlatMap,
            FunctionId = new FunctionId { Module = "Seq", Name = "flat_map" }
        };
        var mapperFunction = new MirFunc
        {
            Name = mapper.Name,
            FunctionId = mapper.FunctionId,
            ReturnType = sequenceType,
            Locals = [Decl(Local(10, intType), "value", true)],
            BasicBlocks = [new MirBasicBlock
            {
                Id = new BlockId { Value = 2 },
                IsEntry = true,
                Terminator = new MirReturn { Value = Local(10, intType) }
            }]
        };
        var main = new MirFunc
        {
            Name = "flat_map_collect_main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = sequenceType,
            Locals = [Decl(source, "source"), Decl(flattened, "flattened"), Decl(result, "result")],
            BasicBlocks = [new MirBasicBlock
            {
                Id = new BlockId { Value = 1 },
                IsEntry = true,
                Instructions =
                [
                    new MirCall { Target = flattened, Function = flatMap, Arguments = [source, mapper] },
                    new MirMove { Target = result, Source = flattened }
                ],
                Terminator = new MirReturn { Value = result }
            }]
        };
        var module = new MirModule
        {
            Name = "flat_map_collect",
            Functions = [main, mapperFunction],
            TypeConstructors = [new MirTypeConstructorInfo { Name = WellKnownStrings.BuiltinTypes.Seq, TypeId = seqConstructorType }],
            TypeDescriptors = { [sequenceType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromTypeId(seqConstructorType), [intType]) }
        };
        var pass = new SequencePipelineFusionPass();
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs = new FunctionOptimizationProofIndex(
            new FunctionOptimizationSummaryIndex(module.Functions.ToDictionary(
                MirFunctionIdentity.GetStableKey,
                static _ => FunctionOptimizationSummary.Pure,
                StringComparer.Ordinal)),
            RecursiveCallAnalysis.Analyze(module));

        pass.Run(module);

        Assert.Equal(1, pass.Stats.FlatMapCollectPipelines);
        Assert.Equal(2, pass.Stats.SourceLoopsEmitted);
        Assert.DoesNotContain(main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(), call =>
            call.Function is MirFunctionRef functionRef && functionRef.CompilerSemanticRole == CompilerSemanticRole.SequenceFlatMap);
        Assert.Contains(main.BasicBlocks.SelectMany(static block => block.Instructions), instruction =>
            instruction is MirCall call && call.Function is MirFunctionRef functionRef &&
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayPush));
    }

    [Theory]
    [InlineData(CompilerSemanticRole.SequenceFind)]
    [InlineData(CompilerSemanticRole.SequenceAny)]
    [InlineData(CompilerSemanticRole.SequenceAll)]
    [InlineData(CompilerSemanticRole.SequenceForEach)]
    public void Run_FlatMapThenDirectSink_LowersNestedLoops(CompilerSemanticRole sinkRole)
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var unitType = new TypeId(BaseTypes.UnitId);
        var outerSequenceType = new TypeId(7150);
        var innerSequenceType = new TypeId(7151);
        var seqConstructorType = new TypeId(7152);
        var optionType = new TypeId(7153);
        var refType = new TypeId(7154);
        var source = Local(1, outerSequenceType);
        var flattened = Local(2, innerSequenceType);
        var resultType = sinkRole == CompilerSemanticRole.SequenceFind
            ? optionType
            : sinkRole == CompilerSemanticRole.SequenceForEach ? unitType : boolType;
        var result = Local(3, resultType);
        var mapper = Function("flat_mapper_direct");
        var predicate = Function("flat_predicate_direct");
        var flatMap = Function("__eidos_prelude_core__Seq__flat_map") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceFlatMap,
            FunctionId = new FunctionId { Module = "Seq", Name = "flat_map" }
        };
        var sinkName = sinkRole.ToString().Replace("Sequence", "", StringComparison.Ordinal).ToLowerInvariant();
        var sink = Function($"__eidos_prelude_core__Seq__{sinkName}") with
        {
            CompilerSemanticRole = sinkRole,
            FunctionId = new FunctionId { Module = "Seq", Name = sinkName }
        };
        var mapperFunction = new MirFunc
        {
            Name = mapper.Name,
            FunctionId = mapper.FunctionId,
            ReturnType = innerSequenceType,
            Locals = [Decl(Local(10, intType), "value", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 2 }, IsEntry = true, Terminator = new MirReturn { Value = Local(10, intType) } }]
        };
        var predicateParameterType = sinkRole == CompilerSemanticRole.SequenceFind ? refType : intType;
        var predicateFunction = new MirFunc
        {
            Name = predicate.Name,
            FunctionId = predicate.FunctionId,
            ReturnType = sinkRole == CompilerSemanticRole.SequenceForEach ? unitType : boolType,
            Locals = [Decl(Local(11, predicateParameterType), "value", true)],
            BasicBlocks = [new MirBasicBlock
            {
                Id = new BlockId { Value = 3 },
                IsEntry = true,
                Terminator = sinkRole == CompilerSemanticRole.SequenceForEach
                    ? new MirReturn { Value = new MirConstant { Value = new MirConstantValue.UnitValue(), TypeId = unitType } }
                    : new MirReturn { Value = BoolConstant(true) }
            }]
        };
        var main = new MirFunc
        {
            Name = "flat_map_direct_main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = resultType,
            Locals = [Decl(source, "source"), Decl(flattened, "flattened"), Decl(result, "result")],
            BasicBlocks = [new MirBasicBlock
            {
                Id = new BlockId { Value = 1 },
                IsEntry = true,
                Instructions =
                [
                    new MirCall { Target = flattened, Function = flatMap, Arguments = [source, mapper] },
                    new MirCall { Target = result, Function = sink, Arguments = [flattened, predicate] }
                ],
                Terminator = new MirReturn { Value = result }
            }]
        };
        var module = new MirModule
        {
            Name = "flat_map_direct_sink",
            Functions = [main, mapperFunction, predicateFunction],
            TypeConstructors = [new MirTypeConstructorInfo { Name = WellKnownStrings.BuiltinTypes.Seq, TypeId = seqConstructorType }],
            TypeDescriptors =
            {
                [innerSequenceType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromTypeId(seqConstructorType), [intType]),
                [refType.Value] = new TypeDescriptor.Ref(intType)
            }
        };

        var pass = new SequencePipelineFusionPass();
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs = new FunctionOptimizationProofIndex(
            new FunctionOptimizationSummaryIndex(module.Functions.ToDictionary(
                MirFunctionIdentity.GetStableKey,
                static _ => FunctionOptimizationSummary.Pure,
                StringComparer.Ordinal)),
            RecursiveCallAnalysis.Analyze(module));
        pass.Run(module);

        Assert.Equal(1, pass.Stats.FlatMapDirectSinkPipelines);
        Assert.Equal(1, pass.Stats.PipelinesFormed);
        Assert.Equal(2, pass.Stats.SourceLoopsEmitted);
        Assert.DoesNotContain(main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(), call =>
            call.Function is MirFunctionRef functionRef &&
            functionRef.CompilerSemanticRole is CompilerSemanticRole.SequenceFlatMap or
                CompilerSemanticRole.SequenceFind or CompilerSemanticRole.SequenceAny or
                CompilerSemanticRole.SequenceAll or CompilerSemanticRole.SequenceForEach);
    }

    [Fact]
    public void Run_FlatMapThenFind_AllowsNonCopyInnerElementOnBorrowedPredicateRoute()
    {
        var outerSequenceType = new TypeId(7170);
        var innerSequenceType = new TypeId(7171);
        var customElementType = new TypeId(7172);
        var seqConstructorType = new TypeId(7173);
        var optionType = new TypeId(7174);
        var refType = new TypeId(7175);
        var boolType = new TypeId(BaseTypes.BoolId);
        var source = Local(1, outerSequenceType);
        var flattened = Local(2, innerSequenceType);
        var result = Local(3, optionType);
        var mapper = Function("flat_mapper_move_find");
        var predicate = Function("flat_predicate_move_find");
        var flatMap = Function("__eidos_prelude_core__Seq__flat_map") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceFlatMap,
            FunctionId = new FunctionId { Module = "Seq", Name = "flat_map" }
        };
        var find = Function("__eidos_prelude_core__Seq__find") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceFind,
            FunctionId = new FunctionId { Module = "Seq", Name = "find" }
        };
        var mapperFunction = new MirFunc
        {
            Name = mapper.Name,
            FunctionId = mapper.FunctionId,
            ReturnType = innerSequenceType,
            Locals = [Decl(Local(10, new TypeId(BaseTypes.IntId)), "value", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 2 }, IsEntry = true, Terminator = new MirReturn { Value = Local(10, new TypeId(BaseTypes.IntId)) } }]
        };
        var predicateFunction = new MirFunc
        {
            Name = predicate.Name,
            FunctionId = predicate.FunctionId,
            ReturnType = boolType,
            Locals = [Decl(Local(11, refType), "value", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 3 }, IsEntry = true, Terminator = new MirReturn { Value = BoolConstant(true) } }]
        };
        var main = new MirFunc
        {
            Name = "flat_map_find_move_main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = optionType,
            Locals = [Decl(source, "source"), Decl(flattened, "flattened"), Decl(result, "result")],
            BasicBlocks = [new MirBasicBlock
            {
                Id = new BlockId { Value = 1 },
                IsEntry = true,
                Instructions =
                [
                    new MirCall { Target = flattened, Function = flatMap, Arguments = [source, mapper] },
                    new MirCall { Target = result, Function = find, Arguments = [flattened, predicate] }
                ],
                Terminator = new MirReturn { Value = result }
            }]
        };
        var module = new MirModule
        {
            Name = "flat_map_find_move",
            Functions = [main, mapperFunction, predicateFunction],
            TypeConstructors = [new MirTypeConstructorInfo { Name = WellKnownStrings.BuiltinTypes.Seq, TypeId = seqConstructorType }],
            TypeDescriptors =
            {
                [innerSequenceType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromTypeId(seqConstructorType), [customElementType]),
                [refType.Value] = new TypeDescriptor.Ref(customElementType)
            }
        };
        var pass = new SequencePipelineFusionPass();
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs = new FunctionOptimizationProofIndex(
            new FunctionOptimizationSummaryIndex(module.Functions.ToDictionary(
                MirFunctionIdentity.GetStableKey,
                static _ => FunctionOptimizationSummary.Pure,
                StringComparer.Ordinal)),
            RecursiveCallAnalysis.Analyze(module));

        pass.Run(module);

        Assert.Equal(1, pass.Stats.FlatMapDirectSinkPipelines);
        Assert.Contains(main.BasicBlocks.SelectMany(static block => block.Instructions), instruction =>
            instruction is MirLoad { MovesOutOfSource: true });
    }

    [Fact]
    public void Run_ZipThenAny_LowersMinLengthPairLoop()
    {
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var sequenceType = new TypeId(7140);
        var zippedSequenceType = new TypeId(7141);
        var pairType = new TypeId(7142);
        var seqConstructorType = new TypeId(7143);
        var left = Local(1, sequenceType);
        var right = Local(2, sequenceType);
        var zipped = Local(3, zippedSequenceType);
        var result = Local(4, boolType);
        var predicate = Function("zip_predicate");
        var zip = Function("__eidos_prelude_core__Seq__zip") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceZip,
            FunctionId = new FunctionId { Module = "Seq", Name = "zip" }
        };
        var any = Function("__eidos_prelude_core__Seq__any") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceAny,
            FunctionId = new FunctionId { Module = "Seq", Name = "any" }
        };
        var predicateFunction = new MirFunc
        {
            Name = predicate.Name,
            FunctionId = predicate.FunctionId,
            ReturnType = boolType,
            Locals = [Decl(Local(10, pairType), "pair", true)],
            BasicBlocks = [new MirBasicBlock { Id = new BlockId { Value = 2 }, IsEntry = true, Terminator = new MirReturn { Value = BoolConstant(true) } }]
        };
        var main = new MirFunc
        {
            Name = "zip_any_main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = boolType,
            Locals = [Decl(left, "left"), Decl(right, "right"), Decl(zipped, "zipped"), Decl(result, "result")],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 1 },
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall { Target = zipped, Function = zip, Arguments = [left, right] },
                        new MirCall { Target = result, Function = any, Arguments = [zipped, predicate] }
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "zip_any",
            Functions = [main, predicateFunction],
            CopyLikeTypeIds = [pairType.Value],
            TypeConstructors = [new MirTypeConstructorInfo { Name = WellKnownStrings.BuiltinTypes.Seq, TypeId = seqConstructorType }],
            TypeDescriptors =
            {
                [pairType.Value] = new TypeDescriptor.Tuple([intType, intType]),
                [zippedSequenceType.Value] = new TypeDescriptor.TyCon(TypeConstructorKey.FromTypeId(seqConstructorType), [pairType])
            }
        };
        var pass = new SequencePipelineFusionPass();

        pass.Run(module);

        Assert.Equal(1, pass.Stats.ZipSinkPipelines);
        Assert.Equal(2, main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>().Count(call =>
            call.Function is MirFunctionRef functionRef &&
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayLength)));
        Assert.DoesNotContain(main.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>(), call =>
            call.Function is MirFunctionRef functionRef &&
            functionRef.CompilerSemanticRole is CompilerSemanticRole.SequenceZip or CompilerSemanticRole.SequenceAny);
    }

    private static ViewSinkFixture CreateViewSinkFixture(bool dynamicTakeBound)
    {
        var sequenceType = new TypeId(7100);
        var intType = new TypeId(BaseTypes.IntId);
        var boolType = new TypeId(BaseTypes.BoolId);
        var source = Local(1, sequenceType);
        var takeBound = Local(2, intType);
        var taken = Local(3, sequenceType);
        var dropped = Local(4, sequenceType);
        var reversed = Local(5, sequenceType);
        var result = Local(6, boolType);
        var callback = Function("view_predicate");
        var take = Function("__eidos_prelude_core__Seq__take__spec_TAKE") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceTake,
            FunctionId = new FunctionId { Module = "Seq", Name = "take__spec_TAKE" }
        };
        var drop = Function("__eidos_prelude_core__Seq__drop__spec_DROP") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceDrop,
            FunctionId = new FunctionId { Module = "Seq", Name = "drop__spec_DROP" }
        };
        var reverse = Function("__eidos_prelude_core__Seq__reverse__spec_REVERSE") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceReverse,
            FunctionId = new FunctionId { Module = "Seq", Name = "reverse__spec_REVERSE" }
        };
        var any = Function("__eidos_prelude_core__Seq__any__spec_ANY") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceAny,
            FunctionId = new FunctionId { Module = "Seq", Name = "any__spec_ANY" }
        };
        var callbackFunction = new MirFunc
        {
            Name = callback.Name,
            FunctionId = callback.FunctionId,
            ReturnType = boolType,
            Locals = [Decl(Local(10, intType), "element", isParameter: true)],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = BoolConstant(true) }
                }
            ]
        };
        var main = new MirFunc
        {
            Name = "view_main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = boolType,
            Locals =
            [
                Decl(source, "source"),
                Decl(takeBound, "take_bound", isParameter: true),
                Decl(taken, "taken"),
                Decl(dropped, "dropped"),
                Decl(reversed, "reversed"),
                Decl(result, "result")
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
                            Target = taken,
                            Function = take,
                            Arguments = [source, dynamicTakeBound ? takeBound : Constant(5)]
                        },
                        new MirCall
                        {
                            Target = dropped,
                            Function = drop,
                            Arguments = [taken, Constant(1)]
                        },
                        new MirCall
                        {
                            Target = reversed,
                            Function = reverse,
                            Arguments = [dropped]
                        },
                        new MirCall
                        {
                            Target = result,
                            Function = any,
                            Arguments = [reversed, callback]
                        }
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "view_sink",
            Functions = [main, callbackFunction]
        };
        var pass = new SequencePipelineFusionPass();
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs = new FunctionOptimizationProofIndex(
            new FunctionOptimizationSummaryIndex(module.Functions.ToDictionary(
                MirFunctionIdentity.GetStableKey,
                static _ => FunctionOptimizationSummary.Pure,
                StringComparer.Ordinal)),
            RecursiveCallAnalysis.Analyze(module));
        return new ViewSinkFixture(module, main, source, pass);
    }

    private sealed record ViewSinkFixture(
        MirModule Module,
        MirFunc Main,
        MirPlace Source,
        SequencePipelineFusionPass Pass);

    private static MirLocal Decl(MirPlace place, string name, bool isParameter = false) => new()
    {
        Id = place.Local,
        Name = name,
        TypeId = place.TypeId,
        IsParameter = isParameter
    };

    private static MirConstant Constant(long value) => new()
    {
        Value = new MirConstantValue.IntValue(value),
        TypeId = new TypeId(BaseTypes.IntId)
    };

    private static MirConstant BoolConstant(bool value) => new()
    {
        Value = new MirConstantValue.BoolValue(value),
        TypeId = new TypeId(BaseTypes.BoolId)
    };

    private static (MirModule Module, MirFunc Main, MirPlace Source, SequencePipelineFusionPass Pass) CreateFilterHeadFixture(
        FunctionOptimizationSummary predicateSummary,
        TypeId? predicateParameterType = null)
    {
        var sequenceType = new TypeId(7060);
        var optionType = new TypeId(7061);
        var elementType = new TypeId(7062);
        var predicateType = predicateParameterType ?? new TypeId(7063);
        var source = Local(1, sequenceType);
        var filtered = Local(2, sequenceType);
        var result = Local(3, optionType);
        var predicate = Function("predicate");
        var filter = Function("__eidos_prelude_core__Seq__filter") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceFilter,
            FunctionId = new FunctionId { Module = "Seq", Name = "filter" }
        };
        var head = Function("__eidos_prelude_core__Seq__head") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceHead,
            FunctionId = new FunctionId { Module = "Seq", Name = "head" }
        };
        var find = new MirFunc
        {
            Name = "__eidos_prelude_core__Seq__find",
            SourceName = "find",
            FunctionId = new FunctionId { Module = "Seq", Name = "find" },
            ReturnType = optionType,
            BasicBlocks = []
        };
        var predicateFunction = new MirFunc
        {
            Name = predicate.Name,
            FunctionId = predicate.FunctionId,
            ReturnType = new TypeId(BaseTypes.BoolId),
            Locals = [Decl(Local(10, predicateType), "element", isParameter: true)],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = new BlockId { Value = 2 },
                    IsEntry = true,
                    Terminator = new MirReturn { Value = BoolConstant(true) }
                }
            ]
        };
        var main = new MirFunc
        {
            Name = "main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = optionType,
            Locals = [Decl(source, "source"), Decl(filtered, "filtered"), Decl(result, "result")],
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
                            Target = filtered,
                            Function = filter,
                            Arguments = [source, predicate]
                        },
                        new MirCall
                        {
                            Target = result,
                            Function = head,
                            Arguments = [filtered]
                        }
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "filter_head_negative",
            Functions = [main, predicateFunction, find]
        };
        if (predicateParameterType is null)
            module.TypeDescriptors[predicateType.Value] = new TypeDescriptor.Ref(elementType);
        var summaries = module.Functions
            .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static _ => FunctionOptimizationSummary.Pure, StringComparer.Ordinal);
        summaries[MirFunctionIdentity.GetStableKey(predicateFunction)] = predicateSummary;
        var pass = new SequencePipelineFusionPass();
        ((IFunctionOptimizationProofConsumer)pass).FunctionProofs = new FunctionOptimizationProofIndex(
            new FunctionOptimizationSummaryIndex(summaries),
            RecursiveCallAnalysis.Analyze(module));
        return (module, main, source, pass);
    }

    private static (MirModule Module, MirFunc Main, MirPlace Source, SequencePipelineFusionPass Pass) CreateTakeHeadFixture(long bound)
    {
        var sequenceType = new TypeId(7070);
        var optionType = new TypeId(7071);
        var source = Local(1, sequenceType);
        var taken = Local(2, sequenceType);
        var result = Local(3, optionType);
        var take = Function("__eidos_prelude_core__Seq__take") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceTake,
            FunctionId = new FunctionId { Module = "Seq", Name = "take" }
        };
        var head = Function("__eidos_prelude_core__Seq__head") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceHead,
            FunctionId = new FunctionId { Module = "Seq", Name = "head" }
        };
        var main = new MirFunc
        {
            Name = "main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = optionType,
            Locals = [Decl(source, "source"), Decl(taken, "taken"), Decl(result, "result")],
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
                            Target = taken,
                            Function = take,
                            Arguments = [source, Constant(bound)]
                        },
                        new MirCall
                        {
                            Target = result,
                            Function = head,
                            Arguments = [taken]
                        }
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "take_head",
            Functions = [main]
        };
        var pass = new SequencePipelineFusionPass();
        return (module, main, source, pass);
    }

    private static (MirModule Module, MirFunc Main, MirPlace Source, SequencePipelineFusionPass Pass) CreateTakeTakeFixture(
        long firstBound,
        long secondBound)
    {
        var sequenceType = new TypeId(7080);
        var source = Local(1, sequenceType);
        var first = Local(2, sequenceType);
        var result = Local(3, sequenceType);
        var take = Function("__eidos_prelude_core__Seq__take") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceTake,
            FunctionId = new FunctionId { Module = "Seq", Name = "take" }
        };
        var main = new MirFunc
        {
            Name = "main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = sequenceType,
            Locals = [Decl(source, "source"), Decl(first, "first"), Decl(result, "result")],
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
                            Target = first,
                            Function = take,
                            Arguments = [source, Constant(firstBound)]
                        },
                        new MirCall
                        {
                            Target = result,
                            Function = take,
                            Arguments = [first, Constant(secondBound)]
                        }
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "take_take",
            Functions = [main]
        };
        var pass = new SequencePipelineFusionPass();
        return (module, main, source, pass);
    }

    private static (MirModule Module, MirFunc Main, MirPlace Source, SequencePipelineFusionPass Pass) CreateDropDropFixture(
        long firstBound,
        long secondBound)
    {
        var sequenceType = new TypeId(7090);
        var source = Local(1, sequenceType);
        var first = Local(2, sequenceType);
        var result = Local(3, sequenceType);
        var drop = Function("__eidos_prelude_core__Seq__drop") with
        {
            CompilerSemanticRole = CompilerSemanticRole.SequenceDrop,
            FunctionId = new FunctionId { Module = "Seq", Name = "drop" }
        };
        var main = new MirFunc
        {
            Name = "main",
            EntryBlockId = new BlockId { Value = 1 },
            ReturnType = sequenceType,
            Locals = [Decl(source, "source"), Decl(first, "first"), Decl(result, "result")],
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
                            Target = first,
                            Function = drop,
                            Arguments = [source, Constant(firstBound)]
                        },
                        new MirCall
                        {
                            Target = result,
                            Function = drop,
                            Arguments = [first, Constant(secondBound)]
                        }
                    ],
                    Terminator = new MirReturn { Value = result }
                }
            ]
        };
        var module = new MirModule
        {
            Name = "drop_drop",
            Functions = [main]
        };
        var pass = new SequencePipelineFusionPass();
        return (module, main, source, pass);
    }
}
