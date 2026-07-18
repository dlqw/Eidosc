using System.Text.Json;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Symbols;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class LiveStateStableIdentityTests
{
    [Fact]
    public void LiveStateIdRemapper_PreservesNoneAndRemapsZeroIds()
    {
        var plan = new LiveStateRemapPlan(
            LiveStateRemapPlan.CurrentSchemaVersion,
            LiveStateRemapKind.StableKey,
            [new LiveStateSymbolRemapEntry(0, 17)],
            [new LiveStateTypeRemapEntry(0, 23)],
            [],
            "");
        var remapper = new LiveStateIdRemapper(plan);

        Assert.Equal(SymbolId.None, remapper.RemapSymbol(SymbolId.None));
        Assert.Equal(TypeId.None, remapper.RemapType(TypeId.None));
        Assert.Equal(new SymbolId(17), remapper.RemapSymbol(new SymbolId(0)));
        Assert.Equal(new TypeId(23), remapper.RemapType(new TypeId(0)));
    }

    [Fact]
    public void LiveStateIdRemapper_OffsetsTypeAndValueVariablesIndependently()
    {
        var plan = LiveStateRemapPlan.Identity(symbolTable: null);
        var remapper = new LiveStateIdRemapper(
            plan,
            typeVariableOffset: 10,
            valueVariableOffset: 20);

        Assert.Equal(13, remapper.RemapTypeVariable(3));
        Assert.Equal(27, remapper.RemapValueVariable(7));
        Assert.Equal(15, remapper.RemapNextTypeVariable(5));
        Assert.Equal(29, remapper.RemapNextValueVariable(9));
        Assert.Equal(-1, remapper.RemapValueVariable(-1));
    }

    [Fact]
    public void PlanRemap_UsesStableKeysWhenRawSymbolIdsChange()
    {
        var previous = new[]
        {
            CreateIdentity(symbolId: 10, typeId: 100, "function", "id")
        };
        var current = new[]
        {
            CreateIdentity(symbolId: 42, typeId: 900, "function", "id")
        };

        var remap = LiveStateStableIdentityBuilder.PlanRemap(previous, current);

        Assert.True(remap.IsValid, string.Join(Environment.NewLine, remap.Failures));
        Assert.Equal(LiveStateRemapKind.StableKey, remap.Kind);
        var symbol = Assert.Single(remap.Symbols);
        Assert.Equal(10, symbol.From);
        Assert.Equal(42, symbol.To);
        var type = Assert.Single(remap.Types);
        Assert.Equal(100, type.From);
        Assert.Equal(900, type.To);
    }

    [Fact]
    public void PlanRemap_BlocksAmbiguousStableKeys()
    {
        var previous = new[]
        {
            CreateIdentity(symbolId: 10, typeId: 100, "function", "id"),
            CreateIdentity(symbolId: 11, typeId: 101, "function", "id")
        };
        var current = new[]
        {
            CreateIdentity(symbolId: 42, typeId: 900, "function", "id")
        };

        var remap = LiveStateStableIdentityBuilder.PlanRemap(previous, current);

        Assert.False(remap.IsValid);
        Assert.Equal(LiveStateRemapKind.NotRestorable, remap.Kind);
        Assert.Contains(remap.Failures, failure =>
            failure.StartsWith("duplicate-previous-symbol-key:", StringComparison.Ordinal));
    }

    [Fact]
    public void PlanRemap_CoalescesPreviousModuleAliases()
    {
        var previous = new[]
        {
            CreateIdentity(symbolId: 10, typeId: -1, nameof(SymbolKind.Module), "Main"),
            CreateIdentity(symbolId: 11, typeId: -1, nameof(SymbolKind.Module), "Main")
        };
        var current = new[]
        {
            CreateIdentity(symbolId: 42, typeId: -1, nameof(SymbolKind.Module), "Main")
        };

        var remap = LiveStateStableIdentityBuilder.PlanRemap(previous, current);

        Assert.True(remap.IsValid, string.Join(Environment.NewLine, remap.Failures));
        Assert.Contains(new LiveStateSymbolRemapEntry(10, 42), remap.Symbols);
        Assert.Contains(new LiveStateSymbolRemapEntry(11, 42), remap.Symbols);
    }

    [Fact]
    public void PlanRemap_SelectsCanonicalCurrentModuleAlias()
    {
        var previous = new[]
        {
            CreateIdentity(symbolId: 10, typeId: -1, nameof(SymbolKind.Module), "Main")
        };
        var current = new[]
        {
            CreateIdentity(symbolId: 43, typeId: -1, nameof(SymbolKind.Module), "Main"),
            CreateIdentity(symbolId: 42, typeId: -1, nameof(SymbolKind.Module), "Main")
        };

        var remap = LiveStateStableIdentityBuilder.PlanRemap(previous, current);

        Assert.True(remap.IsValid, string.Join(Environment.NewLine, remap.Failures));
        Assert.Contains(new LiveStateSymbolRemapEntry(10, 42), remap.Symbols);
    }

    [Fact]
    public void PlanRemap_WithNamerSeed_ComposesSeededAndTypesEntrySymbols()
    {
        var previous = new[]
        {
            CreateIdentity(symbolId: 10, typeId: 100, "function", "from-namer"),
            CreateIdentity(symbolId: 11, typeId: 101, "trait", "types-entry")
        };
        var current = new[]
        {
            CreateIdentity(symbolId: 42, typeId: 900, "function", "from-namer") with
            {
                StableKey = CreateIdentity(symbolId: 99, typeId: 999, "degraded", "from-namer").StableKey
            },
            CreateIdentity(symbolId: 50, typeId: 901, "trait", "types-entry"),
            CreateIdentity(symbolId: 60, typeId: 902, "unrelated", "duplicate"),
            CreateIdentity(symbolId: 61, typeId: 903, "unrelated", "duplicate")
        };
        var seed = new LiveStateRemapPlan(
            LiveStateRemapPlan.CurrentSchemaVersion,
            LiveStateRemapKind.StableKey,
            [new LiveStateSymbolRemapEntry(10, 42)],
            [new LiveStateTypeRemapEntry(100, 900)],
            [],
            "");

        var remap = LiveStateStableIdentityBuilder.PlanRemap(previous, current, seed);

        Assert.True(remap.IsValid, string.Join(Environment.NewLine, remap.Failures));
        Assert.Contains(new LiveStateSymbolRemapEntry(10, 42), remap.Symbols);
        Assert.Contains(new LiveStateSymbolRemapEntry(11, 50), remap.Symbols);
        Assert.Contains(new LiveStateTypeRemapEntry(100, 900), remap.Types);
        Assert.Contains(new LiveStateTypeRemapEntry(101, 901), remap.Types);
    }

    [Fact]
    public void ModuleNamerStatePayload_CapturesMemberIndexAndStableSymbolIdentities()
    {
        var result = RunToNamer("""
Main :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.NotNull(result.SymbolTable);
        var symbolTable = result.SymbolTable;
        var memberIndex = Assert.IsType<ProjectModuleMemberIndexSnapshot>(result.ModuleMemberIndexSnapshot);
        var payload = ModuleNamerStatePayload.Create("Main", symbolTable, memberIndex, result.ModuleGraphSnapshot);

        Assert.Equal(ModuleNamerStatePayload.CurrentSchemaVersion, payload.SchemaVersion);
        Assert.Equal("Main", payload.ModuleKey);
        Assert.False(string.IsNullOrWhiteSpace(payload.PayloadHash));
        Assert.NotEmpty(payload.SymbolIdentities);
        Assert.Contains(payload.MemberIndex.Members, member => member.Name == "id");
        Assert.Equal(payload.MemberIndex.LocalIndexHash, payload.ExportSurfaceHash);
        Assert.Equal(payload.MemberIndex.DependencyIndexHash, payload.DependencyIndexHash);

        var json = JsonSerializer.Serialize(payload);
        var roundTripped = JsonSerializer.Deserialize<ModuleNamerStatePayload>(json);

        Assert.NotNull(roundTripped);
        Assert.Equal(payload.PayloadHash, roundTripped!.PayloadHash);
        var validation = payload.ValidateAgainst(roundTripped);
        Assert.True(validation.IsValid, string.Join(Environment.NewLine, validation.Failures));
        Assert.NotNull(validation.RemapPlan);
        Assert.True(validation.RemapPlan!.IsIdentity);
    }

    [Fact]
    public void SymbolTableStateBuilder_RestoresModuleNamerSurfaceWithCurrentIds()
    {
        var result = RunToNamer("""
Main :: module {
    Pair :: type {
        Pair:: type(Int, Int)
    }

    id :: Int -> Int
    {
        value => value
    }
}
""");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.NotNull(result.SymbolTable);
        var original = result.SymbolTable;
        var memberIndex = Assert.IsType<ProjectModuleMemberIndexSnapshot>(result.ModuleMemberIndexSnapshot);
        var payload = ModuleNamerStatePayload.Create("Main", original, memberIndex, result.ModuleGraphSnapshot);

        var restored = SymbolTableStateBuilder.BuildFromNamerPayload(payload);

        Assert.True(restored.IsApplied, string.Join(Environment.NewLine, restored.Failures));
        Assert.NotNull(restored.SymbolTable);
        Assert.NotNull(restored.RemapPlan);
        Assert.True(restored.RemapPlan!.IsStableKey);
        Assert.Contains(restored.RemapPlan.Symbols, entry => entry.From != entry.To);
        Assert.True(restored.AppliedSymbols > 0);
        Assert.Equal(1, restored.AppliedModules);
        Assert.True(restored.AppliedScopes > 0);
        Assert.True(restored.AppliedGlobalBindings > 0);
        Assert.Equal(
            CanonicalModuleSurface(original, "Main"),
            CanonicalModuleSurface(restored.SymbolTable!, "Main"));
        Assert.NotNull(restored.SymbolTable.LookupType("Pair"));
        Assert.NotNull(restored.SymbolTable.LookupConstructor("Pair"));
        Assert.NotNull(restored.SymbolTable.LookupValue("id"));
        AssertFunctionArity(restored.SymbolTable, "id", 1);
    }

    [Fact]
    public void SymbolTableStateBuilder_RestoresAssociatedItemOwnershipAndDefinitionModules()
    {
        var result = RunToNamer("""
Main :: module {
    Container[T] :: trait {
        Item :: type
        LIMIT :: T
    }

    ContainerInt :: instance Container[Int] {
        Item :: type = Int
        LIMIT :: Int = 1
    }
}
""");

        Assert.True(result.Success, FormatDiagnostics(result));
        var original = Assert.IsType<SymbolTable>(result.SymbolTable);
        var memberIndex = Assert.IsType<ProjectModuleMemberIndexSnapshot>(result.ModuleMemberIndexSnapshot);
        var payload = ModuleNamerStatePayload.Create("Main", original, memberIndex, result.ModuleGraphSnapshot);

        var restored = SymbolTableStateBuilder.BuildFromNamerPayload(payload);

        Assert.True(restored.IsApplied, string.Join(Environment.NewLine, restored.Failures));
        var table = Assert.IsType<SymbolTable>(restored.SymbolTable);
        var restoredTraitId = table.LookupTrait("Container");
        Assert.True(restoredTraitId.HasValue);
        var traitId = restoredTraitId.Value;
        var trait = Assert.IsType<TraitSymbol>(table.GetSymbol(traitId));
        var impl = Assert.Single(table.GetImplsForTrait(traitId));
        Assert.True(trait.DefinitionModuleId.IsValid);
        Assert.Equal(trait.DefinitionModuleId, impl.DefinitionModuleId);
        var traitType = Assert.IsType<AssociatedTypeSymbol>(table.GetSymbol(Assert.Single(trait.AssociatedTypes)));
        var traitConst = Assert.IsType<AssociatedConstSymbol>(table.GetSymbol(Assert.Single(trait.AssociatedConsts)));
        var implType = Assert.IsType<AssociatedTypeSymbol>(table.GetSymbol(Assert.Single(impl.AssociatedTypes)));
        var implConst = Assert.IsType<AssociatedConstSymbol>(table.GetSymbol(Assert.Single(impl.AssociatedConsts)));
        Assert.Equal(traitId, traitType.OwnerTrait);
        Assert.Equal(traitId, traitConst.OwnerTrait);
        Assert.Equal(traitId, implType.OwnerTrait);
        Assert.Equal(traitId, implConst.OwnerTrait);
        Assert.Equal(impl.Id, implType.OwnerImpl);
        Assert.Equal(impl.Id, implConst.OwnerImpl);
        Assert.All(new AssociatedItemSymbol[] { traitType, traitConst, implType, implConst }, item =>
            Assert.Equal(trait.DefinitionModuleId, item.DefinitionModuleId));
    }

    [Fact]
    public void SymbolTableStateBuilder_RestoresOverloadsAndImplIndexes()
    {
        var result = RunToNamer("""
Main :: module {
    Box :: type {
        Box:: type(Int)
    }

    ShowBox :: trait {
        show :: Self -> Int
    }

    show :: Int -> Int
    {
        value => value
    }

    show :: Box -> Int
    {
        value => 1
    }


    show :: Box -> Int
     impl ShowBox
{
        value => 2
    }
}
""");

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.NotNull(result.SymbolTable);
        var original = result.SymbolTable;
        var memberIndex = Assert.IsType<ProjectModuleMemberIndexSnapshot>(result.ModuleMemberIndexSnapshot);
        var payload = ModuleNamerStatePayload.Create("Main", original, memberIndex, result.ModuleGraphSnapshot);
        Assert.Contains(payload.SymbolIdentities, identity =>
            identity.SymbolKind == Eidosc.Symbols.SymbolKind.Impl.ToString());

        var restored = SymbolTableStateBuilder.BuildFromNamerPayload(payload);

        Assert.True(restored.IsApplied, string.Join(Environment.NewLine, restored.Failures));
        Assert.NotNull(restored.SymbolTable);
        Assert.Equal(
            CanonicalModuleSurface(original, "Main"),
            CanonicalModuleSurface(restored.SymbolTable!, "Main"));
        Assert.NotNull(restored.SymbolTable.LookupType("Box"));
        Assert.NotNull(restored.SymbolTable.LookupTrait("ShowBox"));
        Assert.NotEmpty(restored.SymbolTable.LookupValueCandidates("show"));
        var originalBox = Assert.IsType<Eidosc.Symbols.AdtSymbol>(
            original.GetSymbol(original.LookupType("Box")!.Value));
        var originalTrait = Assert.IsType<Eidosc.Symbols.TraitSymbol>(
            original.GetSymbol(original.LookupTrait("ShowBox")!.Value));
        var restoredBox = Assert.IsType<Eidosc.Symbols.AdtSymbol>(
            restored.SymbolTable.GetSymbol(restored.SymbolTable.LookupType("Box")!.Value));
        var restoredTrait = Assert.IsType<Eidosc.Symbols.TraitSymbol>(
            restored.SymbolTable.GetSymbol(restored.SymbolTable.LookupTrait("ShowBox")!.Value));

        Assert.Equal(
            original.GetImplsForTrait(originalTrait.Id).Count,
            restored.SymbolTable.GetImplsForTrait(restoredTrait.Id).Count);
        Assert.Equal(
            original.LookupImpls(originalBox.TypeId).Count,
            restored.SymbolTable.LookupImpls(restoredBox.TypeId).Count);
    }

    [Fact]
    public void SymbolTableStateBuilder_RestoresMultipleModuleNamerPayloadsWithGlobalIds()
    {
        var source = """
Main :: module {
    import Lib

    main :: Int -> Int
    {
        value => Lib.id(value)
    }
}

Lib :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""";
        var result = RunToNamer(source);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.NotNull(result.SymbolTable);
        var payloads = CreateGraphNamerPayloads(result);

        var restored = SymbolTableStateBuilder.BuildFromNamerPayloads(payloads);

        Assert.True(restored.IsApplied, string.Join(Environment.NewLine, restored.Failures));
        Assert.NotNull(restored.SymbolTable);
        Assert.Equal(payloads.Count, restored.AppliedModules);
        Assert.Equal(
            CanonicalModuleSurface(result.SymbolTable, "StableIdentity"),
            CanonicalModuleSurface(restored.SymbolTable!, "StableIdentity"));
        Assert.Equal(
            CanonicalModuleSurface(result.SymbolTable, "Lib"),
            CanonicalModuleSurface(restored.SymbolTable!, "Lib"));
        Assert.Contains("Function:id", CanonicalModuleSurface(restored.SymbolTable!, "Lib"), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_WithPreviousNamerStatePayload_RestoresAtNamerStageEntry()
    {
        var source = """
Main :: module {
    Pair :: type {
        Pair:: type(Int, Int)
    }

    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunToNamer(source);

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.NotNull(first.SymbolTable);
        var semantic = Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(
            first.ModuleSemanticSignatureSnapshot);
        var dependency = Assert.IsType<ProjectModuleDependencySignatureSnapshot>(
            first.ModuleDependencySignatureSnapshot);
        var memberIndex = Assert.IsType<ProjectModuleMemberIndexSnapshot>(
            first.ModuleMemberIndexSnapshot);
        var payload = ModuleNamerStatePayload.Create(
            "Main",
            first.SymbolTable,
            memberIndex,
            first.ModuleGraphSnapshot,
            first.Ast);

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "stable_identity.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false,
            PreviousModuleSemanticSignatureSnapshot = semantic,
            PreviousModuleDependencySignatureSnapshot = dependency,
            PreviousModuleNamerStatePayloads = [payload],
            ModuleArtifactAvailability = static (_, _, _, _) => true
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.NotNull(second.SymbolTable);
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault("Namer.moduleRestore.applied") == 1,
            FormatCounters(second));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.realTaskExecution"));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.restoredModules"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.compiledModules"));
        Assert.Equal(
            CanonicalModuleSurface(first.SymbolTable, "Main"),
            CanonicalModuleSurface(second.SymbolTable, "Main"));
        var firstAstState = CanonicalAstNamerState(first).Split(Environment.NewLine);
        var secondAstState = CanonicalAstNamerState(second).Split(Environment.NewLine);
        Assert.Equal(firstAstState.Length, secondAstState.Length);
        for (var i = 0; i < firstAstState.Length; i++)
        {
            if (!string.Equals(firstAstState[i], secondAstState[i], StringComparison.Ordinal))
            {
                var firstEntry = AstNamerStatePayload.Create(first.Ast).Entries[i];
                var secondEntry = AstNamerStatePayload.Create(second.Ast).Entries[i];
                Assert.Fail(
                    $"AST Namer state mismatch at entry {i} ({firstEntry.StableIdentity.NodeKind}, {firstEntry.StableIdentity.StableKey}); " +
                    $"symbol {firstEntry.SymbolId}->{secondEntry.SymbolId}; " +
                    $"first={firstAstState[i]}; second={secondAstState[i]}");
            }
        }
        Assert.NotNull(second.SymbolTable.LookupType("Pair"));
        Assert.NotNull(second.SymbolTable.LookupValue("id"));
    }

    [Fact]
    public void Run_WithPreviousNamerStatePayloads_RestoresMultipleModulesAtNamerStageEntry()
    {
        var source = """
Main :: module {
    import Lib

    main :: Int -> Int
    {
        value => Lib.id(value)
    }
}

Lib :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunToNamer(source);

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.NotNull(first.SymbolTable);
        var semantic = Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(
            first.ModuleSemanticSignatureSnapshot);
        var dependency = Assert.IsType<ProjectModuleDependencySignatureSnapshot>(
            first.ModuleDependencySignatureSnapshot);
        var payloads = CreateNamerPayloads(first);

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "stable_identity.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false,
            PreviousModuleSemanticSignatureSnapshot = semantic,
            PreviousModuleDependencySignatureSnapshot = dependency,
            PreviousModuleNamerStatePayloads = payloads,
            ModuleArtifactAvailability = static (_, _, _, _) => true
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.NotNull(second.SymbolTable);
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault("Namer.moduleRestore.applied") == 1,
            FormatCounters(second));
        Assert.Equal(2, second.ProfilingCounters.GetValueOrDefault(
            "Namer.moduleRestore.payloadModules"));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.realTaskExecution"));
        Assert.Equal(2, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.restoredModules"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.compiledModules"));
        Assert.Equal(
            CanonicalModuleSurface(first.SymbolTable, "StableIdentity"),
            CanonicalModuleSurface(second.SymbolTable, "StableIdentity"));
        Assert.Equal(
            CanonicalModuleSurface(first.SymbolTable, "Lib"),
            CanonicalModuleSurface(second.SymbolTable, "Lib"));
        Assert.Contains("Function:id", CanonicalModuleSurface(second.SymbolTable, "Lib"), StringComparison.Ordinal);
    }

    [Fact]
    public void Run_WithPreviousNamerStatePayload_ReappliesConfiguredLinkLibraries()
    {
        var source = """
Main :: module {
    main :: Int -> Int
    {
        value => value
    }
}
""";
        var first = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "stable_identity.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Hir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false,
            ConfigFfiLibraries = ["native_test"]
        }).Run();

        Assert.True(first.Success, FormatDiagnostics(first));
        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "stable_identity.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Hir,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false,
            ConfigFfiLibraries = ["native_test"],
            PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot,
            PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot,
            PreviousModuleNamerStatePayloads = first.ModuleNamerStatePayloads,
            ModuleArtifactAvailability = static (_, _, _, _) => true
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault("Namer.moduleRestore.applied") == 1,
            FormatCounters(second));
        var hir = Assert.IsType<Eidosc.Hir.HirModule>(second.HirModule);
        Assert.Contains("native_test", hir.LinkLibraries, StringComparer.Ordinal);
    }

    [Fact]
    public void Run_WithChangedSharedSourceUnit_CompilesUnitWithoutFullNamerFallback()
    {
        var firstSource = """
Main :: module {
    import Lib
    import Helper

    Pair :: type {
        Pair:: type(Int, Int)
    }

    main :: Int -> Int
    {
        value => Lib.id(value)
    }
}

Lib :: module {
    id :: Int -> Int
    {
        value => value
    }
}

Helper :: module {
    keep :: Int -> Int
    {
        value => value
    }
}
""";
        var changedSource = """
Main :: module {
    import Lib
    import Helper

    main :: Int -> Int
    {
        value => Lib.id(value)
    }
}

Lib :: module {
    id :: Int -> Int
    {
        value => value
    }

    plusOne :: Int -> Int
    {
        value => value + 1
    }
}

Helper :: module {
    keep :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunToNamer(firstSource);

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.NotNull(first.SymbolTable);
        var semantic = Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(
            first.ModuleSemanticSignatureSnapshot);
        var dependency = Assert.IsType<ProjectModuleDependencySignatureSnapshot>(
            first.ModuleDependencySignatureSnapshot);
        var payloads = CreateNamerPayloads(first);

        var second = new CompilationPipeline(changedSource, new CompilationOptions
        {
            InputFile = "stable_identity.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false,
            PreviousModuleSemanticSignatureSnapshot = semantic,
            PreviousModuleDependencySignatureSnapshot = dependency,
            PreviousModuleNamerStatePayloads = payloads,
            ModuleArtifactAvailability = static (_, _, _, _) => true
        }).Run();

        Assert.True(second.Success, FormatDiagnostics(second));
        Assert.NotNull(second.SymbolTable);
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault("Namer.moduleRestore.applied") == 1,
            FormatCounters(second));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Namer.moduleRestore.fallbackFullResolve"));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleArtifactRestoreExecution.realTaskExecution"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleArtifactRestoreExecution.restoredModules"));
        Assert.True(
            second.ProfilingCounters.GetValueOrDefault("Build.moduleArtifactRestoreExecution.compiledModules") > 0,
            FormatCounters(second));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.realTaskExecution"));
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.restoredModules"));
        Assert.True(second.ProfilingCounters.GetValueOrDefault(
            "Build.moduleStage.Namer.compiledModules") > 0);
        var cold = RunToNamer(changedSource);
        Assert.True(cold.Success, FormatDiagnostics(cold));
        Assert.NotNull(cold.SymbolTable);
        Assert.Equal(
            CanonicalModuleSurface(second.SymbolTable, "Lib"),
            CanonicalModuleSurface(cold.SymbolTable, "Lib"));
    }

    [Fact]
    public void Run_WithMixedMultiFileChange_MergesCompiledAndRestoredNamerPayloads()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_mixed_namer_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var entryFile = Path.Combine(tempDir, "Main.eidos");
            var libFile = Path.Combine(tempDir, "lib.eidos");
            var helperFile = Path.Combine(tempDir, "helper.eidos");
            File.WriteAllText(entryFile, """
Main :: module {
    import Lib
    import Helper

    main :: Int -> Int
    {
        value => Lib.id(value) + Helper.keep(value)
    }
}
""");
            File.WriteAllText(libFile, """
Lib :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""");
            File.WriteAllText(helperFile, """
Helper :: module {
    keep :: Int -> Int
    {
        value => value
    }
}
""");

            var source = File.ReadAllText(entryFile);
            var first = RunFileToNamer(entryFile, source, static _ => { });
            Assert.True(first.Success, FormatDiagnostics(first));
            var payloads = Assert.IsAssignableFrom<IReadOnlyList<ModuleNamerStatePayload>>(
                first.ModuleNamerStatePayloads);

            File.WriteAllText(libFile, """
Lib :: module {
    id :: Int -> Int
    {
        value => value
    }

    plusOne :: Int -> Int
    {
        value => value + 1
    }
}
""");
            var second = RunFileToNamer(entryFile, source, options =>
            {
                options.PreviousModuleSemanticSignatureSnapshot = first.ModuleSemanticSignatureSnapshot;
                options.PreviousModuleDependencySignatureSnapshot = first.ModuleDependencySignatureSnapshot;
                options.PreviousModuleNamerStatePayloads = payloads;
                options.ModuleArtifactAvailability = static (_, _, _, _) => true;
            });

            Assert.True(second.Success, FormatDiagnostics(second));
            Assert.NotNull(second.SymbolTable);
            Assert.True(
                second.ProfilingCounters.GetValueOrDefault("Namer.moduleRestore.applied") == 1,
                FormatCounters(second));
            Assert.True(
                second.ProfilingCounters.GetValueOrDefault("Namer.moduleRestore.fallbackFullResolve") == 0,
                FormatCounters(second));
            Assert.True(second.ProfilingCounters.GetValueOrDefault(
                "Build.moduleStage.Namer.compiledModules") > 0);
            Assert.True(second.ProfilingCounters.GetValueOrDefault(
                "Build.moduleStage.Namer.restoredModules") > 0);
            Assert.True(second.ProfilingCounters.GetValueOrDefault(
                "Build.moduleStage.Namer.maxObservedParallelism") > 1);
            AssertFunctionArity(second.SymbolTable, "main", 1, "Main");
            AssertFunctionArity(second.SymbolTable, "id", 1, "Lib");
            AssertFunctionArity(second.SymbolTable, "keep", 1, "Helper");
            AssertFunctionArity(second.SymbolTable, "plusOne", 1, "Lib");
            AssertNamerPayloadFunctionParameterClosure(second.ModuleNamerStatePayloads);

            var cold = RunFileToNamer(entryFile, source, static _ => { });
            Assert.True(cold.Success, FormatDiagnostics(cold));
            Assert.NotNull(cold.SymbolTable);
            var coldLibSurface = CanonicalModuleSurface(cold.SymbolTable, "Lib");
            var restoredLibSurface = CanonicalModuleSurface(second.SymbolTable, "Lib");
            Assert.True(
                string.Equals(coldLibSurface, restoredLibSurface, StringComparison.Ordinal),
                $"Cold Lib:{Environment.NewLine}{coldLibSurface}{Environment.NewLine}Restored Lib:{Environment.NewLine}{restoredLibSurface}");
            var coldHelperSurface = CanonicalModuleSurface(cold.SymbolTable, "Helper");
            var restoredHelperSurface = CanonicalModuleSurface(second.SymbolTable, "Helper");
            Assert.True(
                string.Equals(coldHelperSurface, restoredHelperSurface, StringComparison.Ordinal),
                $"Cold Helper:{Environment.NewLine}{coldHelperSurface}{Environment.NewLine}Restored Helper:{Environment.NewLine}{restoredHelperSurface}");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void NamerStateMerger_MergesPreviousUnchangedAndCurrentChangedModulePayloads()
    {
        var firstSource = """
Main :: module {
    import Lib
    import Helper

    main :: Int -> Int
    {
        value => Lib.id(value)
    }
}

Lib :: module {
    id :: Int -> Int
    {
        value => value
    }
}

Helper :: module {
    keep :: Int -> Int
    {
        value => value
    }
}
""";
        var changedSource = """
Main :: module {
    import Lib
    import Helper

    main :: Int -> Int
    {
        value => Lib.id(value)
    }
}

Lib :: module {
    id :: Int -> Int
    {
        value => value
    }

    plusOne :: Int -> Int
    {
        value => value + 1
    }
}

Helper :: module {
    keep :: Int -> Int
    {
        value => value
    }
}
""";
        var first = RunToNamer(firstSource);
        var changed = RunToNamer(changedSource);

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(changed.Success, FormatDiagnostics(changed));
        Assert.NotNull(first.SymbolTable);
        Assert.NotNull(changed.SymbolTable);
        var previousPayloads = CreateNamerPayloads(first);
        var currentPayloads = CreateNamerPayloads(changed);
        var mixedPayloads = currentPayloads
            .Where(static payload => payload.ModuleKey is "Main" or "Lib")
            .Concat(previousPayloads.Where(static payload => payload.ModuleKey == "Helper"))
            .OrderBy(static payload => payload.ModuleKey, StringComparer.Ordinal)
            .ToArray();

        var merge = NamerStateMerger.Merge(mixedPayloads);

        Assert.True(merge.IsApplied, string.Join(Environment.NewLine, merge.Failures));
        Assert.NotNull(merge.BuildResult?.SymbolTable);
        var restored = merge.BuildResult.SymbolTable;
        Assert.Equal(3, merge.PayloadModules);
        Assert.Equal(
            CanonicalModuleSurface(changed.SymbolTable, "Lib"),
            CanonicalModuleSurface(restored, "Lib"));
        Assert.Equal(
            CanonicalModuleSurface(first.SymbolTable, "Helper"),
            CanonicalModuleSurface(restored, "Helper"));
        Assert.Contains("Function:plusOne", CanonicalModuleSurface(restored, "Lib"), StringComparison.Ordinal);
        Assert.Contains("Function:keep", CanonicalModuleSurface(restored, "Helper"), StringComparison.Ordinal);
    }

    [Fact]
    public void NamerStateMerger_DoesNotDuplicateRestoredModuleSurfaceScopes()
    {
        var source = """
Main :: module {
    import Lib
    import Helper

    main :: Int -> Int
    {
        value => Lib.id(value)
    }
}

Lib :: module {
    id :: Int -> Int
    {
        value => value
    }
}

Helper :: module {
    keep :: Int -> Int
    {
        value => value
    }
}
""";
        var result = RunToNamer(source);

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.NotNull(result.SymbolTable);
        var payloads = CreateNamerPayloads(result);

        var merge = NamerStateMerger.Merge(payloads);

        Assert.True(merge.IsApplied, string.Join(Environment.NewLine, merge.Failures));
        Assert.NotNull(merge.BuildResult?.SymbolTable);
        var restoredScopes = CanonicalScopeBindings(merge.BuildResult.SymbolTable)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        var idScopes = restoredScopes
            .Where(scope => scope.StartsWith("Module|v=id:Function:id|o=id:[Function:id]|", StringComparison.Ordinal))
            .ToArray();
        var keepScopes = restoredScopes
            .Where(scope => scope.StartsWith("Module|v=keep:Function:keep|o=keep:[Function:keep]|", StringComparison.Ordinal))
            .ToArray();
        Assert.True(idScopes.Length == 1, string.Join(Environment.NewLine, idScopes));
        Assert.True(keepScopes.Length == 1, string.Join(Environment.NewLine, keepScopes));
    }

    [Fact]
    public void NamerStateMerger_BlocksConflictingPayloadsForSameModule()
    {
        var firstSource = """
Main :: module {
    id :: Int -> Int
    {
        value => value
    }
}
""";
        var changedSource = """
Main :: module {
    id :: Int -> Int
    {
        value => value
    }

    plusOne :: Int -> Int
    {
        value => value + 1
    }
}
""";
        var first = RunToNamer(firstSource);
        var changed = RunToNamer(changedSource);

        Assert.True(first.Success, FormatDiagnostics(first));
        Assert.True(changed.Success, FormatDiagnostics(changed));
        var previousPayload = Assert.Single(CreateNamerPayloads(first), static payload => payload.ModuleKey == "Main");
        var currentPayload = Assert.Single(CreateNamerPayloads(changed), static payload => payload.ModuleKey == "Main");

        var merge = NamerStateMerger.Merge([previousPayload, currentPayload]);

        Assert.False(merge.IsApplied);
        Assert.Contains(
            merge.Failures,
            failure => failure.StartsWith("conflicting-namer-payload:", StringComparison.Ordinal));
    }

    private static LiveStateSymbolIdentity CreateIdentity(
        int symbolId,
        int typeId,
        string kind,
        string name)
    {
        var moduleKey = new LiveStateModuleStableKey("current", "Main", "src/Main.eidos");
        var declKey = new LiveStateDeclStableKey(moduleKey, kind, name, "arity:1", "src/Main.eidos:0:10");
        var symbolKey = new LiveStateSymbolStableKey(declKey, "module-member");
        return new LiveStateSymbolIdentity(symbolId, kind, name, typeId, symbolKey);
    }

    private static CompilationResult RunToNamer(string source) =>
        new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "stable_identity.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        }).Run();

    private static CompilationResult RunFileToNamer(
        string inputFile,
        string source,
        Action<CompilationOptions> configure)
    {
        var options = new CompilationOptions
        {
            InputFile = inputFile,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            EnableIncrementalCompilation = true,
            UseColors = false
        };
        configure(options);
        return new CompilationPipeline(source, options).Run();
    }

    private static IReadOnlyList<ModuleNamerStatePayload> CreateNamerPayloads(CompilationResult result)
    {
        Assert.NotNull(result.SymbolTable);
        var memberIndex = Assert.IsType<ProjectModuleMemberIndexSnapshot>(
            result.ModuleMemberIndexSnapshot);
        return memberIndex.Nodes
            .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
            .Select(node => ModuleNamerStatePayload.Create(
                node.ModuleKey,
                result.SymbolTable,
                memberIndex,
                result.ModuleGraphSnapshot,
                result.Ast))
            .ToArray();
    }

    private static IReadOnlyList<ModuleNamerStatePayload> CreateGraphNamerPayloads(CompilationResult result)
    {
        Assert.NotNull(result.SymbolTable);
        var memberIndex = Assert.IsType<ProjectModuleMemberIndexSnapshot>(
            result.ModuleMemberIndexSnapshot);
        var graph = Assert.IsType<ProjectModuleGraphSnapshot>(result.ModuleGraphSnapshot);
        return graph.Nodes
            .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
            .Select(node => ModuleNamerStatePayload.Create(
                node.ModuleKey,
                result.SymbolTable,
                memberIndex,
                result.ModuleGraphSnapshot,
                result.Ast))
            .ToArray();
    }

    private static string CanonicalModuleSurface(Eidosc.Symbols.SymbolTable symbolTable, string moduleKey)
    {
        Assert.True(
            symbolTable.Modules.ModulePaths.TryGetValue(moduleKey, out var moduleId),
            $"missing module '{moduleKey}'. Available: {string.Join(", ", symbolTable.Modules.ModulePaths.Keys.Order(StringComparer.Ordinal))}");
        return string.Join(
            Environment.NewLine,
            symbolTable.Modules.GetModuleMembers(moduleId)
                .Select(id => symbolTable.GetSymbol(id))
                .Where(static symbol => symbol != null)
                .Cast<Eidosc.Symbols.Symbol>()
                .OrderBy(static symbol => symbol.Kind.ToString(), StringComparer.Ordinal)
                .ThenBy(static symbol => symbol.Name, StringComparer.Ordinal)
                .Select(symbol => $"{symbol.Kind}:{symbol.Name}:{symbol.IsPublic}:{symbol.IsModuleLevel}"));
    }

    private static string CanonicalScopeBindings(Eidosc.Symbols.SymbolTable symbolTable) =>
        string.Join(
            Environment.NewLine,
            symbolTable.ScopeStack
                .Select((scope, index) => new
                {
                    index,
                    scope.Kind,
                    Values = string.Join(",", scope.GetLocalBindings()
                        .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => $"{entry.Key}:{FormatSymbol(symbolTable, entry.Value)}")),
                    Overloads = string.Join(",", scope.GetLocalFunctionOverloads()
                        .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => $"{entry.Key}:[{string.Join("|", entry.Value.Select(id => FormatSymbol(symbolTable, id)).Order(StringComparer.Ordinal))}]")),
                    Types = string.Join(",", scope.GetLocalTypes()
                        .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => $"{entry.Key}:{FormatSymbol(symbolTable, entry.Value)}")),
                    Traits = string.Join(",", scope.GetLocalTraits()
                        .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => $"{entry.Key}:{FormatSymbol(symbolTable, entry.Value)}")),
                    Effects = string.Join(",", scope.GetLocalAbilities()
                        .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => $"{entry.Key}:{FormatSymbol(symbolTable, entry.Value)}")),
                    Constructors = string.Join(",", scope.GetLocalConstructors()
                        .OrderBy(static entry => entry.Key, StringComparer.Ordinal)
                        .Select(entry => $"{entry.Key}:{FormatSymbol(symbolTable, entry.Value)}"))
                })
                .Where(static entry => entry.Values.Length > 0 ||
                                       entry.Overloads.Length > 0 ||
                                       entry.Types.Length > 0 ||
                                       entry.Traits.Length > 0 ||
                                       entry.Effects.Length > 0 ||
                                       entry.Constructors.Length > 0)
                .Select(static entry => $"{entry.Kind}|v={entry.Values}|o={entry.Overloads}|t={entry.Types}|tr={entry.Traits}|a={entry.Effects}|c={entry.Constructors}"));

    private static string CanonicalScopeBindingSet(Eidosc.Symbols.SymbolTable symbolTable) =>
        string.Join(
            Environment.NewLine,
            CanonicalScopeBindings(symbolTable)
                .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal));

    private static string FormatSymbol(Eidosc.Symbols.SymbolTable symbolTable, Eidosc.SymbolId id)
    {
        var symbol = symbolTable.GetSymbol(id);
        return symbol == null ? "missing" : $"{symbol.Kind}:{symbol.Name}";
    }

    private static void AssertFunctionArity(
        SymbolTable symbolTable,
        string name,
        int arity,
        string? moduleDisplayKey = null)
    {
        var matches = symbolTable.Symbols.Values.OfType<FuncSymbol>()
            .Where(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
            .Where(candidate => moduleDisplayKey == null || symbolTable.Modules
                .GetOwningModuleIds(candidate.Id)
                .Select(symbolTable.Modules.GetModule)
                .Any(module => string.Equals(
                    module?.Identity.ToDisplayKey(),
                    moduleDisplayKey,
                    StringComparison.Ordinal)))
            .ToArray();
        Assert.True(
            matches.Length == 1,
            $"expected one function named '{name}', found:{Environment.NewLine}" +
            string.Join(Environment.NewLine, matches.Select(function =>
                $"{function.Id.Value} {function.Span} " +
                $"owners=[{string.Join(',', symbolTable.Modules.GetOwningModuleIds(function.Id).Select(id => symbolTable.Modules.GetModule(id)?.Identity.ToIdentityKey()))}]")));
        var function = matches[0];
        Assert.Equal(arity, function.Parameters.Count);
        Assert.Equal(arity, function.ParamTypes.Count);
        Assert.All(function.Parameters.Where(static parameterId => parameterId.IsValid), parameterId =>
        {
            var parameter = Assert.IsType<VarSymbol>(symbolTable.GetSymbol(parameterId));
            Assert.True(parameter.IsParameter);
        });
    }

    private static void AssertNamerPayloadFunctionParameterClosure(
        IReadOnlyList<ModuleNamerStatePayload>? payloads)
    {
        foreach (var payload in Assert.IsAssignableFrom<IReadOnlyList<ModuleNamerStatePayload>>(payloads))
        {
            var symbolIds = payload.SymbolTable.Symbols
                .Select(static symbol => symbol.Id)
                .ToHashSet();
            var identityIds = payload.SymbolIdentities
                .Select(static identity => identity.SymbolId)
                .ToHashSet();
            foreach (var function in payload.SymbolTable.Symbols.Where(static symbol =>
                         string.Equals(symbol.Kind, nameof(SymbolKind.Function), StringComparison.Ordinal)))
            {
                Assert.True(function.Facts.TryGetValue("parameters", out var parameters));
                foreach (var parameterId in parameters!
                             .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                             .Select(int.Parse)
                             .Where(static parameterId => parameterId > 0))
                {
                    Assert.Contains(parameterId, symbolIds);
                    Assert.Contains(parameterId, identityIds);
                }
            }
        }
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));

    private static string FormatCounters(CompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.ProfilingCounters
                .OrderBy(static counter => counter.Key, StringComparer.Ordinal)
                .Select(static counter => $"{counter.Key}={counter.Value}"));

    private static string CanonicalAstNamerState(CompilationResult result)
    {
        var ast = Assert.IsType<Eidosc.Ast.Declarations.ModuleDecl>(result.Ast);
        var symbolTable = Assert.IsType<SymbolTable>(result.SymbolTable);
        var identities = LiveStateStableIdentityBuilder.BuildSymbolIdentities(
                symbolTable,
                result.ModuleGraphSnapshot)
            .GroupBy(static identity => identity.SymbolId)
            .ToDictionary(
                static group => group.Key,
                static group => group
                    .Select(static identity => identity.StableKey.ToString())
                    .Order(StringComparer.Ordinal)
                    .First());

        string CanonicalSymbol(int value)
        {
            if (value <= 0)
            {
                return "none";
            }

            var symbol = symbolTable.GetSymbol(new SymbolId(value));
            if (symbol != null)
            {
                return $"{symbol.Kind}:{symbol.Name}";
            }

            if (identities.TryGetValue(value, out var identity))
            {
                return identity;
            }

            return $"missing:{value}";
        }

        return string.Join(
            Environment.NewLine,
            AstNamerStatePayload.Create(ast).Entries.Select(entry => string.Join(
                '|',
                entry.StableIdentity.StableKey,
                CanonicalSymbol(entry.SymbolId),
                entry.IsConstructor?.ToString() ?? "",
                entry.IdentifierValueCandidateSymbolIds == null
                    ? ""
                    : string.Join(',', entry.IdentifierValueCandidateSymbolIds.Select(CanonicalSymbol)),
                entry.PathValueCandidateSymbolIds == null
                    ? ""
                    : string.Join(',', entry.PathValueCandidateSymbolIds.Select(CanonicalSymbol)),
                entry.FunctionSymbolId.HasValue ? CanonicalSymbol(entry.FunctionSymbolId.Value) : "",
                entry.FunctionCandidateSymbolIds == null
                    ? ""
                    : string.Join(',', entry.FunctionCandidateSymbolIds.Select(CanonicalSymbol)),
                entry.EvidenceSymbolId.HasValue ? CanonicalSymbol(entry.EvidenceSymbolId.Value) : "",
                entry.TargetSymbolId.HasValue ? CanonicalSymbol(entry.TargetSymbolId.Value) : "",
                entry.ProofIntroSymbolId.HasValue ? CanonicalSymbol(entry.ProofIntroSymbolId.Value) : "",
                entry.MethodCandidateSymbolIds == null
                    ? ""
                    : string.Join(',', entry.MethodCandidateSymbolIds.Select(CanonicalSymbol)),
                entry.ResolvedModule.HasValue ? CanonicalSymbol(entry.ResolvedModule.Value) : "",
                entry.ResolvedSymbols == null
                    ? ""
                    : string.Join(',', entry.ResolvedSymbols.Select(symbol =>
                        $"{symbol.Name}:{CanonicalSymbol(symbol.SymbolId)}:{symbol.Kind}:{symbol.IsAliased}:{symbol.IsImplicitModuleMember}:{symbol.IsTraitMethod}")),
                entry.EffectSymbolIds == null
                    ? ""
                    : string.Join(',', entry.EffectSymbolIds.Select(CanonicalSymbol)))));
    }
}
