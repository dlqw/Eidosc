using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public sealed class ProjectModuleGraphSnapshotTests
{
    [Fact]
    public void FromDependencyGraph_BuildsTopologicalLayers()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("a.eidos", "A");
        graph.RegisterModuleIdentity("b.eidos", "B");
        graph.RegisterModuleIdentity("c.eidos", "C");
        graph.AddDependency("B", "A");
        graph.AddDependency("C", "A");

        var snapshot = ProjectModuleGraphSnapshot.FromDependencyGraph(graph);

        Assert.Equal(["A"], snapshot.TopologicalLayers[0]);
        Assert.Equal(["B", "C"], snapshot.TopologicalLayers[1]);
        Assert.Contains(snapshot.Nodes, node => node.ModuleKey == "A" && node.Dependents.SequenceEqual(["B", "C"]));
    }

    [Fact]
    public void FromDependencyGraph_IncludesRegisteredModuleWithoutEdges()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("single.eidos", "Single");

        var snapshot = ProjectModuleGraphSnapshot.FromDependencyGraph(graph);

        var node = Assert.Single(snapshot.Nodes);
        Assert.Equal("Single", node.ModuleKey);
        Assert.Single(node.SourcePaths);
        Assert.Equal(["Single"], snapshot.TopologicalLayers[0]);
    }

    [Fact]
    public void SignatureSnapshot_ChangesImportedModuleSignatureWhenSourceChanges()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("lib.eidos", "Lib");
        graph.RegisterModuleIdentity("main.eidos", "Main");
        graph.AddDependency("Main", "Lib");
        var snapshot = ProjectModuleGraphSnapshot.FromDependencyGraph(graph);

        var before = ProjectModuleSignatureSnapshot.FromGraphSnapshot(
            snapshot,
            path => path.EndsWith("lib.eidos", StringComparison.Ordinal)
                ? "Lib :: module { value :: Int = 1; }"
                : "Main :: module { import Lib; main :: Unit -> Unit { _ => () } }",
            "syntax",
            "flags");
        var after = ProjectModuleSignatureSnapshot.FromGraphSnapshot(
            snapshot,
            path => path.EndsWith("lib.eidos", StringComparison.Ordinal)
                ? "Lib :: module { value :: Int = 2; }"
                : "Main :: module { import Lib; main :: Unit -> Unit { _ => () } }",
            "syntax",
            "flags");

        Assert.NotEqual(Node(before, "Lib").SignatureHash, Node(after, "Lib").SignatureHash);
        Assert.NotEqual(Node(before, "Main").DependencySignatureHash, Node(after, "Main").DependencySignatureHash);
        Assert.NotEqual(Node(before, "Main").SignatureHash, Node(after, "Main").SignatureHash);
    }

    [Fact]
    public void SignatureSnapshot_MissingSourceHashDiffersFromEmptySource()
    {
        var graph = new ModuleDependencyGraph();
        graph.RegisterModuleIdentity("missing.eidos", "Missing");
        var snapshot = ProjectModuleGraphSnapshot.FromDependencyGraph(graph);

        var missing = ProjectModuleSignatureSnapshot.FromGraphSnapshot(snapshot, _ => null, "syntax", "flags");
        var empty = ProjectModuleSignatureSnapshot.FromGraphSnapshot(snapshot, _ => "", "syntax", "flags");

        Assert.NotEqual(Node(missing, "Missing").SourceHash, Node(empty, "Missing").SourceHash);
        Assert.NotEqual(Node(missing, "Missing").SignatureHash, Node(empty, "Missing").SignatureHash);
    }

    [Fact]
    public void SemanticSignatureSnapshot_ChangesWhenExportedFunctionTypeChanges()
    {
        var before = CompileSemanticSnapshot(
            "Lib.eidos",
            """
Lib :: module {
    export id :: Int -> Int { value => value }
}
""");
        var after = CompileSemanticSnapshot(
            "Lib.eidos",
            """
Lib :: module {
    export id :: Int -> Bool { value => true }
}
""");

        Assert.NotEqual(SemanticNode(before, "Lib").ExportSurfaceHash, SemanticNode(after, "Lib").ExportSurfaceHash);
        Assert.NotEqual(SemanticNode(before, "Lib").SemanticSignatureHash, SemanticNode(after, "Lib").SemanticSignatureHash);
    }

    [Fact]
    public void SemanticSignatureSnapshot_IgnoresUnexportedFunctionBodyInExplicitExportMode()
    {
        var before = CompileSemanticSnapshot(
            "Lib.eidos",
            """
Lib :: module {
    export id :: Int -> Int { value => value }
    helper :: Int -> Int { value => value + 1 }
}
""");
        var after = CompileSemanticSnapshot(
            "Lib.eidos",
            """
Lib :: module {
    export id :: Int -> Int { value => value }
    helper :: Int -> Int { value => value + 100 }
}
""");

        Assert.Equal(SemanticNode(before, "Lib").ExportSurfaceHash, SemanticNode(after, "Lib").ExportSurfaceHash);
        Assert.Equal(SemanticNode(before, "Lib").SemanticSignatureHash, SemanticNode(after, "Lib").SemanticSignatureHash);
    }

    [Fact]
    public void SemanticSignatureSnapshot_PropagatesImportedExportSurfaceChanges()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_semantic_signature_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var mainFile = Path.Combine(tempDir, "Main.eidos");
            var libFile = Path.Combine(tempDir, "Lib.eidos");
            File.WriteAllText(mainFile, """
Main :: module {
    import Lib

    main :: Unit -> Unit { _ => () }
}
""");
            File.WriteAllText(libFile, """
Lib :: module {
    export id :: Int -> Int { value => value }
}
""");
            var before = CompileSemanticSnapshotFromFile(mainFile);

            File.WriteAllText(libFile, """
Lib :: module {
    export id :: Int -> Bool { value => true }
}
""");
            var after = CompileSemanticSnapshotFromFile(mainFile);

            Assert.NotEqual(SemanticNode(before, "Lib").SemanticSignatureHash, SemanticNode(after, "Lib").SemanticSignatureHash);
            Assert.NotEqual(
                SemanticNode(before, "Main").DependencySemanticSignatureHash,
                SemanticNode(after, "Main").DependencySemanticSignatureHash);
            Assert.NotEqual(SemanticNode(before, "Main").SemanticSignatureHash, SemanticNode(after, "Main").SemanticSignatureHash);
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
    public void SignatureSnapshot_ReusesPreloadedImportedSourceText()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_source_cache_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var mainFile = Path.Combine(tempDir, "Main.eidos");
            var libFile = Path.Combine(tempDir, "Lib.eidos");
            File.WriteAllText(mainFile, """
Main :: module {
    import Lib

    main :: Unit -> Unit { _ => () }
}
""");
            File.WriteAllText(libFile, """
Lib :: module {
    export id :: Int -> Int { value => value }
}
""");

            var result = new CompilationPipeline(File.ReadAllText(mainFile), new CompilationOptions
            {
                InputFile = mainFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                EnableDetailedProfiling = true,
                UseColors = false
            }).Run();

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            Assert.True(
                result.ProfilingCounters.TryGetValue("Build.importSourceText.fileReads", out var importFileReads),
                FormatCounters(result));
            Assert.Equal(1, importFileReads);
            Assert.True(
                result.ProfilingCounters.TryGetValue("Build.moduleSignatureSourceText.cacheHits", out var signatureCacheHits),
                FormatCounters(result));
            Assert.True(signatureCacheHits >= 1, FormatCounters(result));
            Assert.False(
                result.ProfilingCounters.ContainsKey("Build.moduleSignatureSourceText.fileReads"),
                FormatCounters(result));
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
    public void ImportPreload_ReusesLanguageVersionLookupForSameDirectoryImports()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_import_syntax_cache_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var mainFile = Path.Combine(tempDir, "Main.eidos");
            var libAFile = Path.Combine(tempDir, "LibA.eidos");
            var libBFile = Path.Combine(tempDir, "LibB.eidos");
            File.WriteAllText(mainFile, """
Main :: module {
    import LibA
    import LibB

    main :: Unit -> Unit { _ => () }
}
""");
            File.WriteAllText(libAFile, """
LibA :: module {
    export a :: Int = 1;
}
""");
            File.WriteAllText(libBFile, """
LibB :: module {
    export b :: Int = 2;
}
""");

            var result = new CompilationPipeline(File.ReadAllText(mainFile), new CompilationOptions
            {
                InputFile = mainFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                EnableDetailedProfiling = true,
                UseColors = false
            }).Run();

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            Assert.Equal(1, result.ProfilingCounters["Build.importLanguageVersion.lookups"]);
            Assert.Equal(1, result.ProfilingCounters["Build.importLanguageVersion.cacheHits"]);
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
    public void ImportPreload_ReportsLexerDiagnosticsFromImportedModule()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_import_lexer_diag_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var mainFile = Path.Combine(tempDir, "Main.eidos");
            var libFile = Path.Combine(tempDir, "Lib.eidos");
            File.WriteAllText(mainFile, """
Main :: module {
    import Lib

    main :: Unit -> Unit { _ => () }
}
""");
            File.WriteAllText(libFile, """
Lib :: module {
    export value :: String = "unterminated
}
""");

            var result = new CompilationPipeline(File.ReadAllText(mainFile), new CompilationOptions
            {
                InputFile = mainFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                UseColors = false
            }).Run();

            Assert.False(result.Success);
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Level == Eidosc.Diagnostic.DiagnosticLevel.Error &&
                diagnostic.Message.Contains("String", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(result.Diagnostics, diagnostic =>
                diagnostic.Labels.Any(label =>
                    label.Span.FilePath?.EndsWith("Lib.eidos", StringComparison.OrdinalIgnoreCase) == true));
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
    public void ImportPreload_ReusesPrecompiledFullTokenCacheAcrossPipelineRuns()
    {
        const string source = """
Main :: module {
    import Std.GameMath

    main :: Unit -> Unit { _ => () }
}
""";

        _ = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "Main.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "Main.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(
            second.Success,
            string.Join(Environment.NewLine, second.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.True(
            second.ProfilingCounters.TryGetValue("Build.precompiledFullTokenCache.hits", out var hits),
            FormatCounters(second));
        Assert.True(hits >= 1, FormatCounters(second));
    }

    [Fact]
    public void SignatureSnapshot_ReportsParallelSourceHashForMultiModuleProject()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_module_source_hash_parallel_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            File.WriteAllText(Path.Combine(tempDir, "Main.eidos"), """
Main :: module {
    import A
    import B
    import C

    main :: Unit -> Unit { _ => () }
}
""");
            File.WriteAllText(Path.Combine(tempDir, "A.eidos"), "A :: module { export a :: Int = 1; }");
            File.WriteAllText(Path.Combine(tempDir, "B.eidos"), "B :: module { export b :: Int = 2; }");
            File.WriteAllText(Path.Combine(tempDir, "C.eidos"), "C :: module { export c :: Int = 3; }");
            var mainFile = Path.Combine(tempDir, "Main.eidos");

            var result = new CompilationPipeline(File.ReadAllText(mainFile), new CompilationOptions
            {
                InputFile = mainFile,
                LanguageVersion = EidosLanguageVersions.Current,
                StopAtPhase = CompilationPhase.Namer,
                NoImplicitPrelude = true,
                EnableDetailedProfiling = true,
                UseColors = false
            }).Run();

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            Assert.Equal(4, result.ProfilingCounters["Build.moduleSignatures.sourceHashModules"]);
            Assert.Equal(1, result.ProfilingCounters["Build.moduleSignatures.sourceHashParallelEnabled"]);
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
    public void TypedSemanticSnapshot_IncludesResolvedAbiLayoutAndTraitImplFacts()
    {
        var result = CompileToTypes("""
Main :: module {
    @cstruct
    Point :: type {
        x: Float,
        y: Float
    }

    Show :: trait {
        show :: Self -> String
    }

    ShowPoint :: instance Show {
        show :: Point -> String
        {
            p => "Point"
        }
    }

    main :: Unit -> Unit { _ => () }
}
""");

        var snapshot = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(result.ModuleTypedSemanticSnapshot);
        Assert.Equal("typed-semantic-snapshot-v3", snapshot.SchemaVersion);
        var node = Assert.Single(snapshot.Nodes);
        Assert.NotEmpty(node.TypedSemanticHash);

        var point = Assert.Single(node.Declarations, declaration => declaration.Name == "Point");
        Assert.Contains("cstruct:True", point.CanonicalFacts);
        Assert.Contains("cstructSize:16", point.CanonicalFacts);
        Assert.Contains("cstructAlignment:8", point.CanonicalFacts);
        Assert.Contains(point.CanonicalFacts, fact => fact.StartsWith("cstructField:x:", StringComparison.Ordinal) && fact.Contains(":0:8:8", StringComparison.Ordinal));
        Assert.Contains(point.CanonicalFacts, fact => fact.StartsWith("cstructField:y:", StringComparison.Ordinal) && fact.Contains(":8:8:8", StringComparison.Ordinal));

        var impl = Assert.Single(node.Declarations, declaration => declaration.Kind == "Impl");
        Assert.Equal("Impl", impl.Kind);
        Assert.Contains(impl.CanonicalFacts, fact => fact.StartsWith("trait:", StringComparison.Ordinal));
        Assert.Contains(impl.CanonicalFacts, fact => fact.StartsWith("implementingType:", StringComparison.Ordinal));
        Assert.Contains("runtimeMethods:True", impl.CanonicalFacts);

        var profile = CompilationProfilingFormatter.CreateSnapshot(result);
        Assert.NotNull(profile.ModuleTypedSemanticSignatures);
        Assert.Equal(snapshot.Nodes.Count, profile.ModuleTypedSemanticSignatures!.Nodes.Count);
    }

    [Fact]
    public void TypedSemanticSnapshot_CanonicalHashesAreStableAcrossEquivalentCompiles()
    {
        const string source = """
Main :: module {
    Box :: type { Box(Int) }

    Show :: trait {
        show :: Self -> String
    }

    ShowBox :: instance Show {
        show :: Box -> String
        {
            value => "Box"
        }
    }
}
""";

        var first = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(CompileToTypes(source).ModuleTypedSemanticSnapshot);
        var second = Assert.IsType<ProjectModuleTypedSemanticSnapshot>(CompileToTypes(source).ModuleTypedSemanticSnapshot);

        var firstNode = Assert.Single(first.Nodes);
        var secondNode = Assert.Single(second.Nodes);
        Assert.Equal(firstNode.LocalSurfaceHash, secondNode.LocalSurfaceHash);
        Assert.Equal(firstNode.TypedSemanticHash, secondNode.TypedSemanticHash);

        var firstCanonical = firstNode.Declarations
            .Select(static declaration => $"{declaration.Kind}:{declaration.CanonicalName}:{declaration.CanonicalType}:{declaration.CanonicalHash}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        var secondCanonical = secondNode.Declarations
            .Select(static declaration => $"{declaration.Kind}:{declaration.CanonicalName}:{declaration.CanonicalType}:{declaration.CanonicalHash}")
            .OrderBy(static value => value, StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(firstCanonical, secondCanonical);
        Assert.Contains(firstNode.Declarations, declaration =>
            declaration.Kind == "Impl" &&
            declaration.CanonicalFacts.Any(static fact => fact.Contains("implementingType:", StringComparison.Ordinal)));
    }

    [Fact]
    public void ModuleMemberIndexSnapshot_IncludesStableMembersAndAccessibleBindings()
    {
        var result = CompileToTypes("""
Main :: module {
    export Box :: type { Box(Int) }

    export make :: Int -> Box
    {
        value => Box(value)
    }
}
""");

        var index = Assert.IsType<ProjectModuleMemberIndexSnapshot>(result.ModuleMemberIndexSnapshot);
        var node = Assert.Single(index.Nodes, static node => node.ModuleKey == "Main");

        Assert.True(node.UsesExplicitExports);
        Assert.Contains(node.Members, static binding => binding.Name == "Box" && binding.Kind == "Type");
        Assert.Contains(node.Members, static binding => binding.Name == "make" && binding.Kind == "Value");
        Assert.Contains(node.Exports, static binding => binding.Name == "Box" && binding.CanonicalSymbol.Contains("Main::", StringComparison.Ordinal));
        Assert.Contains(node.AccessibleBindings, static binding => binding.Name == "make" && binding.IsPublic);

        var profile = CompilationProfilingFormatter.CreateSnapshot(result);
        Assert.NotNull(profile.ModuleMemberIndex);
        Assert.NotNull(profile.ModuleMemberIndexRestorePlan);
        Assert.Equal(index.Nodes.Count, profile.ModuleMemberIndex!.Nodes.Count);
        Assert.Equal(index.Nodes.Count, profile.ModuleMemberIndexRestorePlan!.AddedModules);
        Assert.True(result.ProfilingCounters.TryGetValue("Namer.moduleMemberIndex.modules", out var modules));
        Assert.Equal(index.Nodes.Count, modules);
    }

    [Fact]
    public void ModuleMemberIndexSnapshot_ReportsPreviousSnapshotComparisonCounters()
    {
        const string source = """
Main :: module {
    export score :: Int = 1;
}
""";
        var first = CompileToTypes(source);
        var second = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "module_member_index_previous.eidos",
            StopAtPhase = CompilationPhase.Types,
            EnableDetailedProfiling = true,
            PreviousModuleMemberIndexSnapshot = first.ModuleMemberIndexSnapshot,
            UseColors = false
        }).Run();

        Assert.True(second.Success, string.Join(Environment.NewLine, second.Diagnostics.Select(d => d.Message)));
        Assert.Equal(1, second.ProfilingCounters.GetValueOrDefault("Namer.moduleMemberIndexPrevious.available"));
        Assert.True(second.ProfilingCounters.GetValueOrDefault("Namer.moduleMemberIndexPrevious.unchangedModules") > 0);
        Assert.Equal(0, second.ProfilingCounters.GetValueOrDefault("Namer.moduleMemberIndexPrevious.changedModules"));
        var plan = Assert.IsType<ProjectModuleMemberIndexRestorePlan>(second.ModuleMemberIndexRestorePlan);
        Assert.True(plan.RestoreModules > 0);
        Assert.Equal(0, plan.RebuildModules);
        var payload = Assert.IsType<ProjectModuleMemberIndexRestorePayloadSnapshot>(second.ModuleMemberIndexRestorePayload);
        Assert.Equal(plan.RestoreModules, payload.ValidatedModules);
        var profile = CompilationProfilingFormatter.CreateSnapshot(second);
        Assert.NotNull(profile.ModuleMemberIndexRestorePlan);
        Assert.NotNull(profile.ModuleMemberIndexRestorePayload);
    }

    [Fact]
    public void ModuleMemberIndexSnapshot_HashesLocalAndDependencyMemberIndexes()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_member_index_hash_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var mainFile = Path.Combine(tempDir, "Main.eidos");
            var libFile = Path.Combine(tempDir, "Lib.eidos");
            File.WriteAllText(mainFile, """
Main :: module {
    import Lib

    export main :: Int -> Int
    {
        value => Lib.id(value)
    }
}
""");
            File.WriteAllText(libFile, """
Lib :: module {
    export id :: Int -> Int
    {
        value => value
    }
}
""");
            var before = CompileToTypesFromFile(mainFile);

            File.WriteAllText(libFile, """
Lib :: module {
    export id :: Int -> Int
    {
        value => value
    }

    export next :: Int -> Int
    {
        value => value + 1
    }
}
""");
            var after = CompileToTypesFromFile(mainFile);

            var beforeIndex = Assert.IsType<ProjectModuleMemberIndexSnapshot>(before.ModuleMemberIndexSnapshot);
            var afterIndex = Assert.IsType<ProjectModuleMemberIndexSnapshot>(after.ModuleMemberIndexSnapshot);
            var beforeLib = MemberIndexNode(beforeIndex, "Lib");
            var afterLib = MemberIndexNode(afterIndex, "Lib");
            var beforeMain = MemberIndexNode(beforeIndex, "Main");
            var afterMain = MemberIndexNode(afterIndex, "Main");

            Assert.NotEmpty(beforeLib.LocalIndexHash);
            Assert.NotEmpty(beforeLib.MemberIndexHash);
            Assert.NotEqual(beforeLib.LocalIndexHash, afterLib.LocalIndexHash);
            Assert.NotEqual(beforeLib.MemberIndexHash, afterLib.MemberIndexHash);
            Assert.Equal(beforeMain.LocalIndexHash, afterMain.LocalIndexHash);
            Assert.NotEqual(beforeMain.DependencyIndexHash, afterMain.DependencyIndexHash);
            Assert.NotEqual(beforeMain.MemberIndexHash, afterMain.MemberIndexHash);
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
    public void ModuleMemberIndexRestorePlan_TracksRestoreRebuildAddedAndRemovedModules()
    {
        var previous = new ProjectModuleMemberIndexSnapshot(
            "module-member-index-snapshot-v1",
            [
                CreateMemberIndexNode("A", "a1"),
                CreateMemberIndexNode("B", "b1"),
                CreateMemberIndexNode("Removed", "r1")
            ]);
        var current = new ProjectModuleMemberIndexSnapshot(
            "module-member-index-snapshot-v1",
            [
                CreateMemberIndexNode("A", "a1"),
                CreateMemberIndexNode("B", "b2"),
                CreateMemberIndexNode("Added", "n1")
            ]);

        var plan = ProjectModuleMemberIndexRestorePlan.Create(previous, current);

        Assert.Equal(4, plan.TotalModules);
        Assert.Equal(1, plan.RestoreModules);
        Assert.Equal(1, plan.RebuildModules);
        Assert.Equal(1, plan.AddedModules);
        Assert.Equal(1, plan.RemovedModules);
        Assert.Contains(plan.Modules, static item =>
            item.ModuleKey == "A" && item.Action == ProjectModuleMemberIndexRestoreAction.Restore);
        Assert.Contains(plan.Modules, static item =>
            item.ModuleKey == "B" && item.Action == ProjectModuleMemberIndexRestoreAction.Rebuild);
        Assert.Contains(plan.Modules, static item =>
            item.ModuleKey == "Added" && item.Action == ProjectModuleMemberIndexRestoreAction.Add);
        Assert.Contains(plan.Modules, static item =>
            item.ModuleKey == "Removed" && item.Action == ProjectModuleMemberIndexRestoreAction.Remove);
    }

    [Fact]
    public void ModuleMemberIndexRestorePlan_GatesRestoreWithValidatedPayload()
    {
        var previous = new ProjectModuleMemberIndexSnapshot(
            "module-member-index-snapshot-v1",
            [
                CreateMemberIndexNode("A", "a1"),
                CreateMemberIndexNode("B", "b1")
            ]);
        var current = new ProjectModuleMemberIndexSnapshot(
            "module-member-index-snapshot-v1",
            [
                CreateMemberIndexNode("A", "a1"),
                CreateMemberIndexNode("B", "b1")
            ]);
        var plan = ProjectModuleMemberIndexRestorePlan.Create(previous, current);
        var stalePrevious = new ProjectModuleMemberIndexSnapshot(
            "module-member-index-snapshot-v1",
            [
                CreateMemberIndexNode("A", "a1"),
                CreateMemberIndexNode("B", "stale")
            ]);

        var payload = ProjectModuleMemberIndexRestorePayloadSnapshot.Load(plan, stalePrevious);
        var gated = plan.GateWithPayload(payload);

        Assert.Equal(2, payload.RestoreModules);
        Assert.Equal(1, payload.ValidatedModules);
        Assert.Equal(1, payload.StaleModules);
        Assert.Equal(1, gated.RestoreModules);
        Assert.Equal(1, gated.RebuildModules);
        Assert.Contains(gated.Modules, static item =>
            item.ModuleKey == "A" && item.Action == ProjectModuleMemberIndexRestoreAction.Restore);
        Assert.Contains(gated.Modules, static item =>
            item.ModuleKey == "B" && item.Action == ProjectModuleMemberIndexRestoreAction.Rebuild);
    }

    private static ProjectModuleSignatureNode Node(ProjectModuleSignatureSnapshot snapshot, string moduleKey)
    {
        return Assert.Single(snapshot.Nodes, node => node.ModuleKey == moduleKey);
    }

    private static ProjectModuleSemanticSignatureNode SemanticNode(
        ProjectModuleSemanticSignatureSnapshot snapshot,
        string moduleKey)
    {
        return Assert.Single(snapshot.Nodes, node => node.ModuleKey == moduleKey);
    }

    private static ProjectModuleSemanticSignatureSnapshot CompileSemanticSnapshot(string fileName, string source)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_semantic_signature_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var file = Path.Combine(tempDir, fileName);
            File.WriteAllText(file, source);
            return CompileSemanticSnapshotFromFile(file);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    private static ProjectModuleSemanticSignatureSnapshot CompileSemanticSnapshotFromFile(string file)
    {
        var result = new CompilationPipeline(File.ReadAllText(file), new CompilationOptions
        {
            InputFile = file,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Namer,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        return Assert.IsType<ProjectModuleSemanticSignatureSnapshot>(result.ModuleSemanticSignatureSnapshot);
    }

    private static CompilationResult CompileToTypes(string source)
    {
        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "typed_semantic_snapshot_test.eidos",
            AllowVirtualInputFile = true,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        return result;
    }

    private static CompilationResult CompileToTypesFromFile(string file)
    {
        var result = new CompilationPipeline(File.ReadAllText(file), new CompilationOptions
        {
            InputFile = file,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            EnableDetailedProfiling = true,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        return result;
    }

    private static ProjectModuleMemberIndexNode MemberIndexNode(
        ProjectModuleMemberIndexSnapshot snapshot,
        string moduleKey) =>
        Assert.Single(snapshot.Nodes, node => node.ModuleKey == moduleKey);

    private static ProjectModuleMemberIndexNode CreateMemberIndexNode(string moduleKey, string hash) =>
        new(
            moduleKey,
            moduleKey,
            UsesExplicitExports: false,
            LocalIndexHash: $"local:{hash}",
            DependencyIndexHash: $"deps:{hash}",
            MemberIndexHash: $"member:{hash}",
            Members: [],
            Exports: [],
            AccessibleBindings: []);

    private static string FormatCounters(CompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.ProfilingCounters
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
}
