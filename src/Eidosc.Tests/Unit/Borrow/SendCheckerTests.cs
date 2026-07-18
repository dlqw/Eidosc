using Eidosc.Diagnostic;
using Eidosc.Borrow;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Borrow;

public class SendCheckerTests
{
    [Fact]
    public void CompilationPipeline_NoSpawn_PassesSendPhaseWithoutE0200Errors()
    {
        const string source = """
forty_two :: Int -> Int
{
    _ => 42
}
""";
        var options = new CompilationOptions
        {
            InputFile = "send_no_spawn.eidos",
            StopAtPhase = CompilationPhase.Send,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Send, result.CompletedPhase);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == DiagnosticLevel.Error && d.Code == "E0200");
    }

    [Fact]
    public void CompilationPipeline_SimpleFunction_CanStopAtSendPhase()
    {
        const string source = """
add :: Int -> Int -> Int
{
    x => y => x + y
}
""";
        var options = new CompilationOptions
        {
            InputFile = "send_simple_func.eidos",
            StopAtPhase = CompilationPhase.Send,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Send, result.CompletedPhase);
        Assert.True(result.Success);
    }

    [Fact]
    public void CompilationPipeline_BorrowPhase_EmitsBorrowDiagnosticSnapshot()
    {
        const string source = """
identity :: Int -> Int
{
    x => x
}
""";
        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "borrow_diagnostic_snapshot.eidos",
            StopAtPhase = CompilationPhase.Borrow,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
        Assert.Equal(CompilationPhase.Borrow, result.CompletedPhase);
        Assert.NotNull(result.BorrowDiagnosticSnapshot);
        Assert.True(result.BorrowDiagnosticSnapshot!.Functions.Count > 0);
        Assert.True(result.ProfilingCounters.GetValueOrDefault("Borrow.diagnosticSnapshot.functions") > 0);
    }

    [Fact]
    public void CompilationPipeline_BorrowPhase_RestoresCleanPreviousBorrowDiagnosticSnapshot()
    {
        const string source = """
identity :: Int -> Int
{
    x => x
}
""";
        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "borrow_diagnostic_restore.eidos",
            StopAtPhase = CompilationPhase.Borrow,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "borrow_diagnostic_restore.eidos",
            StopAtPhase = CompilationPhase.Borrow,
            EnableDetailedProfiling = true,
            PreviousBorrowDiagnosticSnapshot = first.BorrowDiagnosticSnapshot,
            UseColors = false
        }).Run();

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(first.BorrowDiagnosticSnapshot);
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Borrow.previous_build.diagnostic_restore_hits"));
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Borrow.previous_build.diagnostic_restore_functions") > 0);
    }

    [Fact]
    public void CompilationPipeline_BorrowPhase_RestoresUnchangedCleanFunctions()
    {
        const string firstSource = """
unchanged :: Int -> Int
{
    x => x
}

changed :: Int -> Int
{
    x => x + 1
}
""";
        const string secondSource = """
unchanged :: Int -> Int
{
    x => x
}

changed :: Int -> Int
{
    x => {
        y := x + 2;
        y
    }
}
""";
        var first = new CompilationPipeline(firstSource, new CompilationOptions
        {
            InputFile = "borrow_diagnostic_mixed_restore.eidos",
            StopAtPhase = CompilationPhase.Borrow,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(secondSource, new CompilationOptions
        {
            InputFile = "borrow_diagnostic_mixed_restore.eidos",
            StopAtPhase = CompilationPhase.Borrow,
            EnableDetailedProfiling = true,
            PreviousBorrowDiagnosticSnapshot = first.BorrowDiagnosticSnapshot,
            UseColors = false
        }).Run();

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(first.BorrowDiagnosticSnapshot);
        Assert.NotNull(second.BorrowDiagnosticSnapshot);
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Borrow.previous_build.diagnostic_mixed_restore_dependency_hash_match"));
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault("Borrow.previous_build.diagnostic_mixed_restore_functions") > 0,
            FormatFingerprintComparison(first, second));
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Borrow.previous_build.diagnostic_mixed_restore_rebuild_functions") > 0);
    }

    [Fact]
    public void CompilationPipeline_LlvmPhase_RestoresCleanPreviousBorrowCodegenHintsSnapshot()
    {
        const string source = """
identity :: Int -> Int
{
    x => x
}
""";
        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "borrow_codegen_hint_restore.eidos",
            StopAtPhase = CompilationPhase.Llvm,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "borrow_codegen_hint_restore.eidos",
            StopAtPhase = CompilationPhase.Llvm,
            EnableDetailedProfiling = true,
            PreviousBorrowDiagnosticSnapshot = first.BorrowDiagnosticSnapshot,
            PreviousBorrowCodegenHintsSnapshot = first.BorrowCodegenHintsSnapshot,
            UseColors = false
        }).Run();

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(first.BorrowDiagnosticSnapshot);
        Assert.NotNull(first.BorrowCodegenHintsSnapshot);
        Assert.NotNull(second.BorrowCheckResult);
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Borrow.previous_build.codegen_hint_restore_hits"));
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Borrow.previous_build.codegen_hint_restore_functions") > 0);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Borrow.codegenHintsSnapshot.functions") > 0);
    }

    [Fact]
    public void CompilationPipeline_LlvmPhase_RestoresUnchangedCleanBorrowCodegenHints()
    {
        const string firstSource = """
unchanged :: Int -> Int
{
    x => x
}

changed :: Int -> Int
{
    x => x + 1
}
""";
        const string secondSource = """
unchanged :: Int -> Int
{
    x => x
}

changed :: Int -> Int
{
    x => x + 2
}
""";
        var first = new CompilationPipeline(firstSource, new CompilationOptions
        {
            InputFile = "borrow_codegen_hint_mixed_restore.eidos",
            StopAtPhase = CompilationPhase.Llvm,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(secondSource, new CompilationOptions
        {
            InputFile = "borrow_codegen_hint_mixed_restore.eidos",
            StopAtPhase = CompilationPhase.Llvm,
            EnableDetailedProfiling = true,
            PreviousBorrowDiagnosticSnapshot = first.BorrowDiagnosticSnapshot,
            PreviousBorrowCodegenHintsSnapshot = first.BorrowCodegenHintsSnapshot,
            UseColors = false
        }).Run();

        Assert.True(first.Success, string.Join(Environment.NewLine, first.Diagnostics.Select(d => d.Message)));
        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        Assert.NotNull(first.BorrowDiagnosticSnapshot);
        Assert.NotNull(first.BorrowCodegenHintsSnapshot);
        Assert.NotNull(second.BorrowCodegenHintsSnapshot);
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Borrow.previous_build.codegen_hint_mixed_restore_dependency_hash_match"));
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault("Borrow.previous_build.codegen_hint_mixed_restore_functions") > 0,
            FormatFingerprintComparison(first, second));
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Borrow.previous_build.codegen_hint_mixed_restore_rebuild_functions") > 0);
    }

    [Fact]
    public void BorrowCodegenHintsSnapshot_RoundTripsDirectHintPayloads()
    {
        var functionSymbol = new SymbolId(42);
        var block = new BlockId { Value = 1 };
        var local = new LocalId { Value = 2 };
        var perceus = new PerceusHints();
        perceus.OmitDup.Add((block, 0));
        perceus.OmitDrop.Add((block, 1));
        var reuse = new ReuseHints { SlotCount = 1 };
        reuse.DropReuseSites[(block, 1)] = 0;
        reuse.AllocReuseSites[(block, 2)] = 0;
        var stack = new StackPromotionHints();
        stack.StackAllocSites.Add((block, 2));
        stack.StackAllocInfoByLocal[local] = new StackAllocInfo(2, 99, 16);
        stack.PromotedLocals.Add(local);
        var unified = new UnifiedStackPromotionHints();
        unified.AllocInfoByLocal[local] = new UnifiedStackAllocInfo(
            PromotableAllocationKind.AdtConstructor,
            (block, 2),
            local,
            [0])
        {
            TypeId = 99,
            FieldCount = 2,
            PayloadSize = 16
        };
        unified.PromotedLocals.Add(local);
        var result = new ModuleBorrowCheckResult();
        result.AddResult(new BorrowCheckResult
        {
            FunctionName = "main",
            FunctionSymbolId = functionSymbol,
            PerceusHints = perceus,
            ReuseHints = reuse,
            StackPromotionHints = stack,
            UnifiedStackPromotionHints = unified
        });
        var mirFingerprints = new MirFunctionFingerprintSnapshot(
            "mir-function-fingerprint-snapshot-v1",
            [new MirFunctionFingerprint("name:main", "body", 1, 3, 1, 0)]);

        var snapshot = BorrowCodegenHintsSnapshot.Create(mirFingerprints, "borrow-codegen-deps", result);
        var restored = snapshot.ToBorrowCheckResult();

        Assert.Single(snapshot.Functions);
        Assert.True(restored.TryGetFunctionResult(functionSymbol, "main", out var restoredResult));
        Assert.NotNull(restoredResult);
        Assert.Contains((block, 0), restoredResult!.PerceusHints!.OmitDup);
        Assert.Equal(1, restoredResult.ReuseHints!.SlotCount);
        Assert.Equal(0, restoredResult.ReuseHints.AllocReuseSites[(block, 2)]);
        Assert.Contains(local, restoredResult.StackPromotionHints!.PromotedLocals);
        Assert.Contains(local, restoredResult.UnifiedStackPromotionHints!.PromotedLocals);
    }

    [Fact]
    public void BorrowCodegenHintsSnapshot_UsesSymbolKeyBeforeNameFallback()
    {
        var functionSymbol = new SymbolId(42);
        var result = new ModuleBorrowCheckResult();
        result.AddResult(new BorrowCheckResult
        {
            FunctionName = "main",
            FunctionSymbolId = functionSymbol
        });
        var mirFingerprints = new MirFunctionFingerprintSnapshot(
            "mir-function-fingerprint-snapshot-v1",
            [
                new MirFunctionFingerprint("sym:42", "symbol-body", 1, 0, 0, 0),
                new MirFunctionFingerprint("name:main", "name-body", 1, 0, 0, 0)
            ]);

        var snapshot = BorrowCodegenHintsSnapshot.Create(mirFingerprints, "borrow-codegen-deps", result);

        var function = Assert.Single(snapshot.Functions);
        Assert.Equal("sym:42", function.FunctionKey);
        Assert.Equal("symbol-body", function.BodyHash);
    }

    [Fact]
    public void BorrowDiagnosticSnapshot_PreservesModuleQualifiedSymbolKey()
    {
        var functionSymbol = new SymbolId(42);
        var result = new ModuleBorrowCheckResult();
        result.AddResult(new BorrowCheckResult
        {
            FunctionName = "main",
            FunctionSymbolId = functionSymbol
        });
        var mirFingerprints = new MirFunctionFingerprintSnapshot(
            "mir-function-fingerprint-snapshot-v1",
            [
                new MirFunctionFingerprint("sym:module-a::42", "qualified-symbol-body", 1, 0, 0, 0),
                new MirFunctionFingerprint("name:main", "name-body", 1, 0, 0, 0)
            ]);

        var snapshot = BorrowDiagnosticSnapshot.Create(mirFingerprints, "borrow-deps", result);

        var function = Assert.Single(snapshot.Functions);
        Assert.Equal("sym:module-a::42", function.FunctionKey);
        Assert.Equal("qualified-symbol-body", function.BodyHash);
    }

    [Fact]
    public void BorrowDiagnosticSnapshot_PreservesStableFunctionKey()
    {
        const string stableKey = "stable:current@source\0Function\0main\0source.eidos\00";
        var result = new ModuleBorrowCheckResult();
        result.AddResult(new BorrowCheckResult
        {
            FunctionName = "main",
            FunctionSymbolId = new SymbolId(42)
        });
        var mirFingerprints = new MirFunctionFingerprintSnapshot(
            "mir-function-fingerprint-snapshot-v1",
            [new MirFunctionFingerprint(stableKey, "stable-body", 1, 0, 0, 0)]);

        var snapshot = BorrowDiagnosticSnapshot.Create(mirFingerprints, "borrow-deps", result);

        var function = Assert.Single(snapshot.Functions);
        Assert.Equal(stableKey, function.FunctionKey);
        Assert.Equal("stable-body", function.BodyHash);
    }

    [Fact]
    public void BorrowDiagnosticSnapshot_DoesNotUseAmbiguousNameBodyHash()
    {
        var result = new ModuleBorrowCheckResult();
        result.AddResult(new BorrowCheckResult
        {
            FunctionName = "same",
            FunctionSymbolId = SymbolId.None
        });
        var mirFingerprints = new MirFunctionFingerprintSnapshot(
            "mir-function-fingerprint-snapshot-v1",
            [
                new MirFunctionFingerprint("name:same", "body-a", 1, 0, 0, 0),
                new MirFunctionFingerprint("name:same", "body-b", 1, 0, 0, 0)
            ]);

        var snapshot = BorrowDiagnosticSnapshot.Create(mirFingerprints, "borrow-deps", result);

        var function = Assert.Single(snapshot.Functions);
        Assert.Equal("name:same", function.FunctionKey);
        Assert.Equal(string.Empty, function.BodyHash);
    }

    [Fact]
    public void CompilationPipeline_MultipleFunctionsNoSpawn_AllPassSendCheck()
    {
        const string source = """
identity :: Int -> Int
{
    x => x
}

double :: Int -> Int
{
    x => x * 2
}

add :: Int -> Int -> Int
{
    x => y => x + y
}
""";
        var options = new CompilationOptions
        {
            InputFile = "send_multi_func.eidos",
            StopAtPhase = CompilationPhase.Send,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Send, result.CompletedPhase);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == DiagnosticLevel.Error && d.Code == "E0200");
    }

    [Fact]
    public void CompilationPipeline_SendAnalysisSnapshot_RestoresUnchangedFunctions()
    {
        const string source = """
identity :: Int -> Int
{
    x => x
}

double :: Int -> Int
{
    x => x * 2
}
""";
        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "send_snapshot_restore.eidos",
            StopAtPhase = CompilationPhase.Send,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "send_snapshot_restore.eidos",
            StopAtPhase = CompilationPhase.Send,
            EnableDetailedProfiling = true,
            PreviousSendAnalysisSnapshot = first.SendAnalysisSnapshot,
            PreviousMirFunctionFingerprintSnapshot = first.MirFunctionFingerprints,
            UseColors = false
        }).Run();

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.NotNull(first.SendAnalysisSnapshot);
        Assert.NotNull(second.SendAnalysisSnapshot);
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Send.previous_build.restore_functions") > 0);
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Send.previous_build.rebuild_functions"));
    }

    [Fact]
    public void CompilationPipeline_SendAnalysisSnapshot_RebuildsChangedFunctions()
    {
        const string firstSource = """
identity :: Int -> Int
{
    x => x
}
""";
        const string secondSource = """
identity :: Int -> Int
{
    x => x + 1
}
""";
        var first = new CompilationPipeline(firstSource, new CompilationOptions
        {
            InputFile = "send_snapshot_rebuild.eidos",
            StopAtPhase = CompilationPhase.Send,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(secondSource, new CompilationOptions
        {
            InputFile = "send_snapshot_rebuild.eidos",
            StopAtPhase = CompilationPhase.Send,
            EnableDetailedProfiling = true,
            PreviousSendAnalysisSnapshot = first.SendAnalysisSnapshot,
            PreviousMirFunctionFingerprintSnapshot = first.MirFunctionFingerprints,
            UseColors = false
        }).Run();

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Send.previous_build.restore_functions"));
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Send.previous_build.rebuild_functions") > 0);
    }

    [Fact]
    public void CompilationPipeline_TupleOfInts_PassesSendCheck()
    {
        const string source = """
make_pair :: Int -> Int -> (Int, Int)
{
    x => y => (x, y)
}
""";
        var options = new CompilationOptions
        {
            InputFile = "send_tuple_ints.eidos",
            StopAtPhase = CompilationPhase.Send,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Send, result.CompletedPhase);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == DiagnosticLevel.Error && d.Code == "E0200");
    }

    [Fact]
    public void CompilationPipeline_OptionInt_PassesSendCheck()
    {
        const string source = """
Option[T] :: type { Some:: type(T) , None :: type {} }

make_some :: Int -> Option[Int]
{
    x => Some(x)
}
""";
        var options = new CompilationOptions
        {
            InputFile = "send_option_int.eidos",
            StopAtPhase = CompilationPhase.Send,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Send, result.CompletedPhase);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == DiagnosticLevel.Error && d.Code == "E0200");
    }

    [Fact]
    public void CompilationPipeline_RecordOfInts_PassesSendCheck()
    {
        const string source = """
Point :: type { Point:: type(Int, Int) }

origin :: Unit -> Point
{
    _ => Point(0, 0)
}
""";
        var options = new CompilationOptions
        {
            InputFile = "send_record_ints.eidos",
            StopAtPhase = CompilationPhase.Send,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.Equal(CompilationPhase.Send, result.CompletedPhase);
        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, d => d.Level == DiagnosticLevel.Error && d.Code == "E0200");
    }

    [Fact]
    public void SendChecker_SpawnRefArgument_ReportsError()
    {
        var refType = new TypeId(100);
        var (checker, blockId) = CreateCheckerForSpawnArgument(
            refType,
            new Dictionary<int, TypeDescriptor>
            {
                [refType.Value] = new TypeDescriptor.Ref(new TypeId(BaseTypes.IntId))
            });

        checker.Check();

        var error = Assert.Single(checker.Errors);
        Assert.Equal(blockId, error.Block);
        Assert.Equal(0, error.InstructionIndex);
        Assert.Contains("Send", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SendChecker_SpawnTupleOfSendArguments_Passes()
    {
        var tupleType = new TypeId(101);
        var (checker, _) = CreateCheckerForSpawnArgument(
            tupleType,
            new Dictionary<int, TypeDescriptor>
            {
                [tupleType.Value] = new TypeDescriptor.Tuple(
                [
                    new TypeId(BaseTypes.IntId),
                    new TypeId(BaseTypes.StringId)
                ])
            });

        checker.Check();

        Assert.Empty(checker.Errors);
    }

    [Fact]
    public void SendChecker_SpawnFunctionArgument_IsRejectedConservatively()
    {
        var functionType = new TypeId(102);
        var (checker, _) = CreateCheckerForSpawnArgument(
            functionType,
            new Dictionary<int, TypeDescriptor>
            {
                [functionType.Value] = new TypeDescriptor.Function([], new TypeId(BaseTypes.IntId))
            });

        checker.Check();

        Assert.Single(checker.Errors);
    }

    [Fact]
    public void SendChecker_SpawnClosureWithSendCaptures_Passes()
    {
        var functionType = new TypeId(103);
        var (checker, _) = CreateCheckerForSpawnClosureCapture(
            captureType: new TypeId(BaseTypes.IntId),
            functionType,
            new Dictionary<int, TypeDescriptor>
            {
                [functionType.Value] = new TypeDescriptor.Function([], new TypeId(BaseTypes.IntId))
            });

        checker.Check();

        Assert.Empty(checker.Errors);
    }

    [Fact]
    public void SendChecker_SpawnClosureWithRefCapture_ReportsError()
    {
        var functionType = new TypeId(104);
        var refType = new TypeId(105);
        var (checker, blockId) = CreateCheckerForSpawnClosureCapture(
            captureType: refType,
            functionType,
            new Dictionary<int, TypeDescriptor>
            {
                [functionType.Value] = new TypeDescriptor.Function([], new TypeId(BaseTypes.IntId)),
                [refType.Value] = new TypeDescriptor.Ref(new TypeId(BaseTypes.IntId))
            });

        checker.Check();

        var error = Assert.Single(checker.Errors);
        Assert.Equal(blockId, error.Block);
        Assert.Equal(1, error.InstructionIndex);
        Assert.Contains("Send", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SendChecker_SpawnAliasedClosureWithSendCaptures_Passes()
    {
        var functionType = new TypeId(106);
        var (checker, _) = CreateCheckerForSpawnClosureCapture(
            captureType: new TypeId(BaseTypes.IntId),
            functionType,
            new Dictionary<int, TypeDescriptor>
            {
                [functionType.Value] = new TypeDescriptor.Function([], new TypeId(BaseTypes.IntId))
            },
            aliasClosure: true);

        checker.Check();

        Assert.Empty(checker.Errors);
    }

    [Fact]
    public void SendChecker_SpawnAliasedClosureWithRefCapture_ReportsError()
    {
        var functionType = new TypeId(107);
        var refType = new TypeId(108);
        var (checker, blockId) = CreateCheckerForSpawnClosureCapture(
            captureType: refType,
            functionType,
            new Dictionary<int, TypeDescriptor>
            {
                [functionType.Value] = new TypeDescriptor.Function([], new TypeId(BaseTypes.IntId)),
                [refType.Value] = new TypeDescriptor.Ref(new TypeId(BaseTypes.IntId))
            },
            aliasClosure: true);

        checker.Check();

        var error = Assert.Single(checker.Errors);
        Assert.Equal(blockId, error.Block);
        Assert.Equal(2, error.InstructionIndex);
        Assert.Contains("Send", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SendChecker_SpawnClosureWithAmbiguousAlias_IsRejectedConservatively()
    {
        var functionType = new TypeId(109);
        var (checker, blockId) = CreateCheckerForSpawnClosureCapture(
            captureType: new TypeId(BaseTypes.IntId),
            functionType,
            new Dictionary<int, TypeDescriptor>
            {
                [functionType.Value] = new TypeDescriptor.Function([], new TypeId(BaseTypes.IntId))
            },
            aliasClosure: true,
            duplicateAlias: true);

        checker.Check();

        var error = Assert.Single(checker.Errors);
        Assert.Equal(blockId, error.Block);
        Assert.Equal(3, error.InstructionIndex);
        Assert.Contains("Send", error.Message, StringComparison.Ordinal);
    }

    private static (SendChecker Checker, BlockId BlockId) CreateCheckerForSpawnArgument(
        TypeId argumentType,
        Dictionary<int, TypeDescriptor> typeDescriptors)
    {
        var blockId = new BlockId { Value = 1 };
        var resultLocal = new LocalId { Value = 1 };
        var argumentLocal = new LocalId { Value = 2 };
        var continuationLocal = new LocalId { Value = 3 };
        var unitType = new TypeId(BaseTypes.UnitId);
        var function = new MirFunc
        {
            Name = "send_check_test",
            EntryBlockId = blockId,
            ReturnType = unitType,
            Locals =
            [
                new MirLocal { Id = resultLocal, Name = "result", TypeId = unitType },
                new MirLocal { Id = argumentLocal, Name = "argument", TypeId = argumentType },
                new MirLocal { Id = continuationLocal, Name = "continuation", TypeId = new TypeId(BaseTypes.ErasedCallableId) }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = blockId,
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(resultLocal, unitType),
                            Function = new MirFunctionRef
                            {
                                Name = "spawn",
                                FunctionId = new FunctionId { Name = "spawn" }
                            },
                            Arguments = [LocalPlace(argumentLocal, argumentType)]
                        }
                    ]
                }
            ]
        };
        var module = new MirModule
        {
            Name = "send_check_test",
            Functions = [function],
            TypeDescriptors = typeDescriptors
        };

        return (new SendChecker(function, module), blockId);
    }

    private static (SendChecker Checker, BlockId BlockId) CreateCheckerForSpawnClosureCapture(
        TypeId captureType,
        TypeId functionType,
        Dictionary<int, TypeDescriptor> typeDescriptors,
        bool aliasClosure = false,
        bool duplicateAlias = false)
    {
        var blockId = new BlockId { Value = 1 };
        var resultLocal = new LocalId { Value = 1 };
        var closureLocal = new LocalId { Value = 2 };
        var continuationLocal = new LocalId { Value = 3 };
        var captureLocal = new LocalId { Value = 4 };
        var aliasLocal = new LocalId { Value = 5 };
        var duplicateAliasLocal = new LocalId { Value = 6 };
        var unitType = new TypeId(BaseTypes.UnitId);
        var calleeSymbol = new SymbolId(20);
        var callee = new MirFunc
        {
            Name = "worker",
            SymbolId = calleeSymbol,
            EntryBlockId = blockId,
            ReturnType = new TypeId(BaseTypes.IntId),
            Locals =
            [
                new MirLocal { Id = new LocalId { Value = 10 }, Name = "capture", TypeId = captureType, IsParameter = true },
                new MirLocal { Id = new LocalId { Value = 11 }, Name = "input", TypeId = new TypeId(BaseTypes.IntId), IsParameter = true }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = blockId,
                    IsEntry = true,
                    Terminator = new MirReturn { Value = null }
                }
            ]
        };
        var function = new MirFunc
        {
            Name = "send_check_closure_test",
            EntryBlockId = blockId,
            ReturnType = unitType,
            Locals =
            [
                new MirLocal { Id = resultLocal, Name = "result", TypeId = unitType },
                new MirLocal { Id = closureLocal, Name = "closure", TypeId = functionType },
                new MirLocal { Id = continuationLocal, Name = "continuation", TypeId = new TypeId(BaseTypes.ErasedCallableId) },
                new MirLocal { Id = captureLocal, Name = "capture", TypeId = captureType },
                new MirLocal { Id = aliasLocal, Name = "closureAlias", TypeId = functionType },
                new MirLocal { Id = duplicateAliasLocal, Name = "duplicateClosureAlias", TypeId = functionType }
            ],
            BasicBlocks =
            [
                new MirBasicBlock
                {
                    Id = blockId,
                    IsEntry = true,
                    Instructions =
                    [
                        new MirCall
                        {
                            Target = LocalPlace(closureLocal, functionType),
                            Function = new MirFunctionRef
                            {
                                Name = "worker",
                                SymbolId = calleeSymbol,
                                TypeId = functionType
                            },
                            Arguments = [LocalPlace(captureLocal, captureType)]
                        },
                        .. CreateClosureAliasInstructions(
                            closureLocal,
                            aliasLocal,
                            duplicateAliasLocal,
                            functionType,
                            aliasClosure,
                            duplicateAlias),
                        new MirCall
                        {
                            Target = LocalPlace(resultLocal, unitType),
                            Function = new MirFunctionRef
                            {
                                Name = "spawn",
                                FunctionId = new FunctionId { Name = "spawn" }
                            },
                            Arguments = [LocalPlace(aliasClosure ? aliasLocal : closureLocal, functionType)]
                        }
                    ]
                }
            ]
        };
        var module = new MirModule
        {
            Name = "send_check_closure_test",
            Functions = [callee, function],
            TypeDescriptors = typeDescriptors
        };

        return (new SendChecker(function, module), blockId);
    }

    private static List<MirInstruction> CreateClosureAliasInstructions(
        LocalId closureLocal,
        LocalId aliasLocal,
        LocalId duplicateAliasLocal,
        TypeId functionType,
        bool aliasClosure,
        bool duplicateAlias)
    {
        if (!aliasClosure)
        {
            return [];
        }

        var instructions = new List<MirInstruction>
        {
            new MirAssign
            {
                Target = LocalPlace(aliasLocal, functionType),
                Source = LocalPlace(closureLocal, functionType)
            }
        };

        if (duplicateAlias)
        {
            instructions.Add(new MirAssign
            {
                Target = LocalPlace(aliasLocal, functionType),
                Source = LocalPlace(duplicateAliasLocal, functionType)
            });
        }

        return instructions;
    }

    private static MirPlace LocalPlace(LocalId localId, TypeId typeId) => new()
    {
        Kind = PlaceKind.Local,
        Local = localId,
        TypeId = typeId
    };

    private static string FormatFingerprintComparison(CompilationResult first, CompilationResult second) =>
        string.Join(
            Environment.NewLine,
            (first.MirFunctionFingerprints?.Functions ?? [])
                .Select(static fingerprint => $"first:{fingerprint.FunctionKey}:{fingerprint.BodyHash}")
                .Concat((second.MirFunctionFingerprints?.Functions ?? [])
                    .Select(static fingerprint => $"second:{fingerprint.FunctionKey}:{fingerprint.BodyHash}"))
                .Concat((first.BorrowDiagnosticSnapshot?.Functions ?? [])
                    .Select(static function => $"diagnostic:{function.FunctionKey}:{function.BodyHash}:{function.Diagnostics.Count}:{function.LoanConstraintFailures}"))
                .Concat(first.BorrowCheckResult?.ResultsByFunctionKey.Values
                    .Select(static result => $"result:{result.FunctionName}:{result.FunctionSymbolId.Value}") ?? [])
                .Concat(second.ProfilingCounters
                    .Where(static counter => counter.Key.StartsWith("Borrow.previous_build.", StringComparison.Ordinal))
                    .OrderBy(static counter => counter.Key, StringComparer.Ordinal)
                    .Select(static counter => $"counter:{counter.Key}:{counter.Value}")));
}
