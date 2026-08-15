using Eidosc.Mir;
using Eidosc.Mir.Optimize;
using Eidosc.Borrow;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Mir;

public sealed partial class SequencePipelineFusionPassTests
{
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
}
