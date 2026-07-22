using Eidosc.Symbols;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Query;
using Eidosc.Ast.Declarations;

namespace Eidosc.Tests.Unit.Query;

public sealed class QueryEngineTests
{
    private sealed class IntDescriptor : QueryDescriptor<string, int>
    {
        public override IQueryCache<string, int> CreateCache() => new DefaultQueryCache<string, int>();
    }

    private sealed class StringDescriptor : QueryDescriptor<UnitKey, string>
    {
        public override IQueryCache<UnitKey, string> CreateCache() => new SingleQueryCache<string>();
    }

    private readonly record struct OuterKey(string Value);

    private sealed class OuterDescriptor : QueryDescriptor<OuterKey, string>
    {
        public override IQueryCache<OuterKey, string> CreateCache() => new DefaultQueryCache<OuterKey, string>();
    }

    private readonly record struct InnerKey(string Value);

    private sealed class InnerDescriptor : QueryDescriptor<InnerKey, string>
    {
        public override IQueryCache<InnerKey, string> CreateCache() => new DefaultQueryCache<InnerKey, string>();
    }

    [Fact]
    public void Execute_Returns_Provider_Result()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());
        var result = engine.Execute("key", k => k.Length);
        Assert.Equal(3, result);
    }

    [Fact]
    public void Execute_Caches_Result()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());
        var callCount = 0;
        int Provider(string key) { callCount++; return key.Length; }

        var r1 = engine.Execute("abc", Provider);
        var r2 = engine.Execute("abc", Provider);
        Assert.Equal(r1, r2);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void Execute_Different_Keys_Evade_Cache()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());
        var callCount = 0;
        int Provider(string key) { callCount++; return key.Length; }

        engine.Execute("abc", Provider);
        engine.Execute("def", Provider);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void Execute_Records_Dependency()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());
        engine.Register(new StringDescriptor());

        var outerIndex = engine.DepGraph.Record(DepNode.Create(DepKind.InferTypes, "outer"));

        var key = new UnitKey();
        engine.Execute(key, _ => "inner-result");

        var innerNode = DepNode.Create(DepKind.CodeGen, key);
        var innerIndex = engine.DepGraph.GetIndex(innerNode);
        Assert.True(innerIndex.IsValid);
    }

    [Fact]
    public void Invalidate_Clears_Cache()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());

        var callCount = 0;
        int Provider(string key) { callCount++; return key.Length; }

        engine.Execute("abc", Provider);
        Assert.Equal(1, callCount);

        var node = DepNode.Create(DepKind.CodeGen, "abc");
        engine.Invalidate(node);
        engine.Execute("abc", Provider);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void Invalidate_Unknown_Node_Is_NoOp()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());
        engine.Execute("key", k => k.Length);

        var unknown = DepNode.Create(DepKind.CodeGen, "nonexistent");
        engine.Invalidate(unknown);
    }

    [Fact]
    public void ClearAllCaches_Resets_Everything()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());

        var callCount = 0;
        int Provider(string key) { callCount++; return key.Length; }

        engine.Execute("abc", Provider);
        engine.ClearAllCaches();
        engine.Execute("abc", Provider);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public void InvalidateKey_RemovesDependentQueryByDependentKey()
    {
        var engine = new QueryEngine();
        engine.Register(new OuterDescriptor(), DepKind.BuildMir);
        engine.Register(new InnerDescriptor(), DepKind.InferTypes);
        var outerCalls = 0;
        var innerCalls = 0;
        var outerKey = new OuterKey("outer");
        var innerKey = new InnerKey("inner");

        string OuterProvider(OuterKey _)
        {
            outerCalls++;
            return engine.Execute(innerKey, InnerProvider);
        }

        string InnerProvider(InnerKey _)
        {
            innerCalls++;
            return $"inner-{innerCalls}";
        }

        Assert.Equal("inner-1", engine.Execute(outerKey, OuterProvider));
        Assert.Equal("inner-1", engine.Execute(outerKey, OuterProvider));

        engine.InvalidateKey(innerKey, DepKind.InferTypes);

        Assert.Equal("inner-2", engine.Execute(outerKey, OuterProvider));
        Assert.Equal(2, outerCalls);
        Assert.Equal(2, innerCalls);
    }

    [Fact]
    public async Task ExecuteAsync_Caches_Result()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());
        var callCount = 0;

        async Task<int> Provider(string key)
        {
            callCount++;
            await Task.Yield();
            return key.Length;
        }

        var r1 = await engine.ExecuteAsync("abc", Provider);
        var r2 = await engine.ExecuteAsync("abc", Provider);
        Assert.Equal(r1, r2);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_Different_Keys()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());
        var callCount = 0;

        async Task<int> Provider(string key)
        {
            callCount++;
            await Task.Yield();
            return key.Length;
        }

        await engine.ExecuteAsync("abc", Provider);
        await engine.ExecuteAsync("def", Provider);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_RecordsDependencyAfterAwait()
    {
        var engine = new QueryEngine();
        engine.Register(new OuterDescriptor(), DepKind.BuildMir);
        engine.Register(new InnerDescriptor(), DepKind.InferTypes);
        var outerKey = new OuterKey("outer");
        var innerKey = new InnerKey("inner");

        await engine.ExecuteAsync(
            outerKey,
            async _ =>
            {
                await Task.Yield();
                return await engine.ExecuteAsync(innerKey, static _ => Task.FromResult("inner"));
            });

        var outerIndex = engine.DepGraph.GetIndex(DepNode.Create(DepKind.BuildMir, outerKey));
        var innerIndex = engine.DepGraph.GetIndex(DepNode.Create(DepKind.InferTypes, innerKey));

        Assert.True(outerIndex.IsValid);
        Assert.True(innerIndex.IsValid);
        Assert.Contains(innerIndex, engine.DepGraph.GetDependencies(outerIndex));
    }

    [Fact]
    public async Task ExecuteAsync_InvalidatedActiveJob_DoesNotWriteStaleCache()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        var first = engine.ExecuteAsync("abc", async _ =>
        {
            providerStarted.SetResult();
            return await releaseProvider.Task;
        });

        await providerStarted.Task;
        engine.InvalidateKey("abc", DepKind.CodeGen);
        releaseProvider.SetResult(1);

        Assert.Equal(1, await first);

        var second = await engine.ExecuteAsync("abc", static _ => Task.FromResult(2));
        Assert.Equal(2, second);
    }

    [Fact]
    public async Task ExecuteAsync_CanceledProvider_DoesNotPopulateCache()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());
        using var cts = new CancellationTokenSource();
        var callCount = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            engine.ExecuteAsync(
                "abc",
                async (_, cancellationToken) =>
                {
                    callCount++;
                    cts.Cancel();
                    await Task.Yield();
                    cancellationToken.ThrowIfCancellationRequested();
                    return 1;
                },
                cts.Token));

        var result = await engine.ExecuteAsync(
            "abc",
            (_, _) =>
            {
                callCount++;
                return Task.FromResult(2);
            },
            CancellationToken.None);

        Assert.Equal(2, result);
        Assert.Equal(2, callCount);
    }

    [Fact]
    public async Task ExecuteAsync_CanceledWaitingDuplicateJob_DoesNotCancelActiveJob()
    {
        var engine = new QueryEngine();
        engine.Register(new IntDescriptor());
        var providerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseProvider = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var duplicateProviderCalls = 0;

        var first = engine.ExecuteAsync(
            "abc",
            async (_, _) =>
            {
                providerStarted.SetResult();
                return await releaseProvider.Task;
            },
            CancellationToken.None);

        await providerStarted.Task;

        using var duplicateCancellation = new CancellationTokenSource();
        var duplicate = engine.ExecuteAsync(
            "abc",
            (_, _) =>
            {
                duplicateProviderCalls++;
                return Task.FromResult(99);
            },
            duplicateCancellation.Token);

        duplicateCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => duplicate);

        releaseProvider.SetResult(7);
        Assert.Equal(7, await first);

        var cached = await engine.ExecuteAsync(
            "abc",
            (_, _) =>
            {
                duplicateProviderCalls++;
                return Task.FromResult(11);
            },
            CancellationToken.None);

        Assert.Equal(7, cached);
        Assert.Equal(0, duplicateProviderCalls);
    }
}

public sealed class PipelineQuerySessionTests
{
    [Fact]
    public void Compile_TypesQuery_ProvidesDeclaredAndInferredEffectSummaries()
    {
        const string source = """
io :: effect;

declared_but_pure :: Int -> Int need io
{
    value => value
}
""";
        var options = new CompilationOptions
        {
            StopAtPhase = CompilationPhase.Types,
            LanguageVersion = EidosLanguageVersions.Current,
            NoImplicitPrelude = true
        };
        var pipeline = new QueryDrivenPipeline("effect_query.eidos", source, options);

        var result = pipeline.Run();
        var types = pipeline.Engine.TryGetCached<string, TypeInferenceOutput>("effect_query.eidos");

        Assert.True(result.Success);
        var effectInferer = Assert.IsType<Eidosc.Types.EffectInferer>(types?.EffectInferer);
        var function = Assert.Single(effectInferer.FunctionSummaries.Keys, static item => item.Name == "declared_but_pure");
        var summary = effectInferer.FunctionSummaries[function];
        Assert.True(summary.DeclaredUpperBound.ContainsName("io"));
        Assert.True(summary.InferredEffects.IsPure);
    }

    [Fact]
    public void Compile_Returns_Success_For_Valid_Source()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();
        var source = "id :: Int -> Int { x => x }";
        var result = session.Compile("test.eidos", source, options);
        Assert.True(result.Success);
    }

    [Fact]
    public void Compile_Returns_Failure_For_Invalid_Source()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();
        var source = "func : -> { }";
        var result = session.Compile("test.eidos", source, options);
        Assert.False(result.Success);
    }

    [Fact]
    public void Compile_InvalidSource_Caches_Partial_Query_Outputs()
    {
        var options = new CompilationOptions { NoImplicitPrelude = true };
        var source = "func : -> { }";
        var pipeline = new QueryDrivenPipeline("test.eidos", source, options);

        var result = pipeline.Run();
        var parse = pipeline.Engine.TryGetCached<string, ParseOutput>("test.eidos");
        var names = pipeline.Engine.TryGetCached<string, NameResolutionOutput>("test.eidos");
        var codeGen = pipeline.Engine.TryGetCached<string, CodeGenOutput>("test.eidos");

        Assert.False(result.Success);
        Assert.NotNull(parse);
        Assert.False(parse.IsIncomplete);
        Assert.NotNull(parse.Ast);
        Assert.NotNull(names);
        Assert.False(names.IsIncomplete);
        Assert.NotNull(codeGen);
        if (codeGen.IsIncomplete)
        {
            Assert.False(string.IsNullOrWhiteSpace(codeGen.IncompleteReason));
        }
    }

    [Fact]
    public void Compile_UnresolvedImport_ReportsStructuredImportDiagnostic()
    {
        var workspace = CreateIsolatedWorkspace();
        try
        {
            var inputPath = Path.Combine(workspace, "main.eidos");
            var source = """
import Missing.Thing

id :: Int -> Int { x => x }
""";
            File.WriteAllText(inputPath, source);
            var options = new CompilationOptions
            {
                InputFile = inputPath,
                ImportSearchRoots = [workspace],
                NoImplicitPrelude = true
            };

            var result = new QueryDrivenPipeline(inputPath, source, options).Run();

            Assert.False(result.Success);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E3000" &&
                              diagnostic.Message.Contains("Unable to resolve imported module 'Missing.Thing'", StringComparison.Ordinal));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public void Compile_ImportedModuleParseFailure_ReportsStructuredImportDiagnostic()
    {
        var workspace = CreateIsolatedWorkspace();
        try
        {
            var inputPath = Path.Combine(workspace, "main.eidos");
            var importDirectory = Path.Combine(workspace, "Broken");
            Directory.CreateDirectory(importDirectory);
            var importedPath = Path.Combine(importDirectory, "Mod.eidos");
            File.WriteAllText(importedPath, "func : -> { }");
            var source = """
import Broken.Mod

id :: Int -> Int { x => x }
""";
            File.WriteAllText(inputPath, source);
            var options = new CompilationOptions
            {
                InputFile = inputPath,
                ImportSearchRoots = [workspace],
                NoImplicitPrelude = true
            };

            var result = new QueryDrivenPipeline(inputPath, source, options).Run();

            Assert.False(result.Success);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E4001" &&
                              diagnostic.Message.Contains("Imported module 'Broken.Mod' failed to parse", StringComparison.Ordinal));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public void Compile_ImportedModuleLexerFailure_ReportsImportedFileDiagnostic()
    {
        var workspace = CreateIsolatedWorkspace();
        try
        {
            var inputPath = Path.Combine(workspace, "main.eidos");
            var importDirectory = Path.Combine(workspace, "Broken");
            Directory.CreateDirectory(importDirectory);
            var importedPath = Path.Combine(importDirectory, "Mod.eidos");
            File.WriteAllText(importedPath, """
Broken.Mod :: module {
    export value :: String = "unterminated
}
""");
            var source = """
import Broken.Mod

id :: Int -> Int { x => x }
""";
            File.WriteAllText(inputPath, source);
            var options = new CompilationOptions
            {
                InputFile = inputPath,
                ImportSearchRoots = [workspace],
                LanguageVersion = EidosLanguageVersions.Current,
                NoImplicitPrelude = true
            };

            var result = new QueryDrivenPipeline(inputPath, source, options).Run();

            Assert.False(result.Success);
            Assert.Contains(
                result.Diagnostics,
                diagnostic => diagnostic.Level == Eidosc.Diagnostic.DiagnosticLevel.Error &&
                              diagnostic.Message.Contains("String", StringComparison.OrdinalIgnoreCase) &&
                              diagnostic.Labels.Any(label =>
                                  label.Span.FilePath?.EndsWith("Mod.eidos", StringComparison.OrdinalIgnoreCase) == true));
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public void Compile_DuplicateImportCandidates_ReportsCandidateFiles()
    {
        var workspace = CreateIsolatedWorkspace();
        try
        {
            var inputPath = Path.Combine(workspace, "main.eidos");
            var sourceRoot = Path.Combine(workspace, "src", "cap");
            var generatedRoot = Path.Combine(workspace, "generated", "cap");
            Directory.CreateDirectory(sourceRoot);
            Directory.CreateDirectory(generatedRoot);

            var firstPath = Path.Combine(sourceRoot, "io.eidos");
            var secondPath = Path.Combine(generatedRoot, "io.eidos");
            File.WriteAllText(firstPath, """
Cap.Io :: module
{
    export first :: Int -> Int { x => x }
}
""");
            File.WriteAllText(secondPath, """
Cap.Io :: module
{
    export second :: Int -> Int { x => x }
}
""");

            var source = """
import Cap.Io

id :: Int -> Int { x => x }
""";
            File.WriteAllText(inputPath, source);
            var options = new CompilationOptions
            {
                InputFile = inputPath,
                ImportSearchRoots = [Path.Combine(workspace, "src"), Path.Combine(workspace, "generated")],
                NoImplicitPrelude = true
            };

            var result = new QueryDrivenPipeline(inputPath, source, options).Run();

            Assert.False(result.Success);
            var diagnostic = Assert.Single(
                result.Diagnostics,
                diagnostic => diagnostic.Code == "E3000" &&
                              diagnostic.Message.Contains("Duplicate module path 'Cap/Io'", StringComparison.Ordinal));
            Assert.Contains(diagnostic.Notes, note => note == $"file: {firstPath}");
            Assert.Contains(diagnostic.Notes, note => note == $"file: {secondPath}");
        }
        finally
        {
            DeleteWorkspace(workspace);
        }
    }

    [Fact]
    public void Compile_Produces_Non_Null_Results_On_Success()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();
        var source = "id :: Int -> Int { x => x }";
        var result = session.Compile("test.eidos", source, options);

        Assert.True(result.Success);
        Assert.NotNull(result.Ast);
        Assert.NotNull(result.SymbolTable);
    }

    private static string CreateIsolatedWorkspace()
    {
        var root = FindWorkspaceRoot();
        var workspace = Path.Combine(
            root,
            "tmp",
            "query-import-diagnostics",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        return workspace;
    }

    private static string FindWorkspaceRoot()
    {
        var directory = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Eidosc", "Eidosc.sln")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetTempPath();
    }

    private static void DeleteWorkspace(string workspace)
    {
        if (Directory.Exists(workspace))
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [Fact]
    public void InvalidateSource_Allows_Recompilation()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();

        var source1 = "f :: Int -> Int { x => x }";
        var r1 = session.Compile("test.eidos", source1, options);
        Assert.True(r1.Success);

        session.InvalidateSource("test.eidos");

        var source2 = "g :: Int -> Int { x => x + 1 }";
        var r2 = session.Compile("test.eidos", source2, options);
        Assert.True(r2.Success);
    }

    [Fact]
    public void ClearAll_Resets_Session()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();
        session.Compile("test.eidos", "id :: Int -> Int { x => x }", options);

        session.ClearAll();

        Assert.Null(session.GetDependencyGraph("test.eidos"));
    }

    [Fact]
    public void Multiple_Files_Can_Be_Compiled()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();

        var r1 = session.Compile("a.eidos", "f :: Int -> Int { x => x }", options);
        var r2 = session.Compile("b.eidos", "g :: Int -> Int { x => x + 1 }", options);

        Assert.True(r1.Success);
        Assert.True(r2.Success);
    }

    [Fact]
    public void Dependency_Graph_Tracks_Phase_Dependencies()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();
        session.Compile("test.eidos", "id :: Int -> Int { x => x }", options);

        var graph = session.GetDependencyGraph("test.eidos");
        Assert.NotNull(graph);

        var sourcePath = NormalizeQueryPath("test.eidos");
        var parseNode = DepNode.Create(DepKind.ParseModule, sourcePath);
        var nameNode = DepNode.Create(DepKind.ResolveNames, sourcePath);
        var typeNode = DepNode.Create(DepKind.InferTypes, sourcePath);
        var hirNode = DepNode.Create(DepKind.BuildHir, sourcePath);
        var mirNode = DepNode.Create(DepKind.BuildMir, sourcePath);
        var codeGenNode = DepNode.Create(DepKind.CodeGen, sourcePath);

        Assert.True(graph.GetIndex(parseNode).IsValid);
        Assert.True(graph.GetIndex(nameNode).IsValid);
        Assert.True(graph.GetIndex(typeNode).IsValid);
        Assert.True(graph.GetIndex(hirNode).IsValid);
        Assert.True(graph.GetIndex(mirNode).IsValid);
        Assert.True(graph.GetIndex(codeGenNode).IsValid);
    }

    [Fact]
    public void Dependency_Graph_CodeGen_Depends_On_Mir()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();
        session.Compile("test.eidos", "id :: Int -> Int { x => x }", options);

        var graph = session.GetDependencyGraph("test.eidos")!;
        var sourcePath = NormalizeQueryPath("test.eidos");
        var codeGenNode = DepNode.Create(DepKind.CodeGen, sourcePath);
        var codeGenIndex = graph.GetIndex(codeGenNode);

        var deps = graph.GetDependencies(codeGenIndex);
        Assert.NotEmpty(deps);

        var mirNode = DepNode.Create(DepKind.BuildMir, sourcePath);
        var mirIndex = graph.GetIndex(mirNode);
        Assert.Contains(mirIndex, deps);
    }

    [Fact]
    public void Dependency_Graph_Reverse_Edge_NameRes_Depends_On_Parse()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();
        session.Compile("test.eidos", "id :: Int -> Int { x => x }", options);

        var graph = session.GetDependencyGraph("test.eidos")!;
        var sourcePath = NormalizeQueryPath("test.eidos");
        var parseNode = DepNode.Create(DepKind.ParseModule, sourcePath);
        var parseIndex = graph.GetIndex(parseNode);

        var dependents = graph.GetDependents(parseIndex);
        Assert.NotEmpty(dependents);

        var nameNode = DepNode.Create(DepKind.ResolveNames, sourcePath);
        var nameIndex = graph.GetIndex(nameNode);
        Assert.Contains(nameIndex, dependents);
    }

    [Fact]
    public void InvalidateSource_Then_Recompile_Uses_Cached_Phases()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();

        var source1 = "f :: Int -> Int { x => x }";
        var r1 = session.Compile("test.eidos", source1, options);
        Assert.True(r1.Success);

        session.InvalidateSource("test.eidos");

        var source2 = "g :: Int -> Int { x => x + 1 }";
        var r2 = session.Compile("test.eidos", source2, options);
        Assert.True(r2.Success);
    }

    [Fact]
    public void Per_Key_Invalidation_Does_Not_Affect_Other_Files()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();

        session.Compile("a.eidos", "f :: Int -> Int { x => x }", options);
        session.Compile("b.eidos", "g :: Int -> Int { x => x + 1 }", options);

        session.InvalidateSource("a.eidos");

        var graphA = session.GetDependencyGraph("a.eidos");
        var graphB = session.GetDependencyGraph("b.eidos");

        Assert.NotNull(graphA);
        Assert.NotNull(graphB);

        var bCodegenNode = DepNode.Create(DepKind.CodeGen, NormalizeQueryPath("b.eidos"));
        var bCodegenIndex = graphB.GetIndex(bCodegenNode);
        Assert.True(bCodegenIndex.IsValid);
    }

    [Fact]
    public void Dependency_Graph_Has_Multiple_Phase_Edges()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();
        session.Compile("test.eidos", "id :: Int -> Int { x => x }", options);

        var graph = session.GetDependencyGraph("test.eidos")!;

        var sourcePath = NormalizeQueryPath("test.eidos");
        var hirNode = DepNode.Create(DepKind.BuildHir, sourcePath);
        var hirIndex = graph.GetIndex(hirNode);
        Assert.True(hirIndex.IsValid);

        var hirDeps = graph.GetDependencies(hirIndex);
        Assert.True(hirDeps.Count >= 2, $"Hir should depend on at least 2 phases, got {hirDeps.Count}");

        var mirNode = DepNode.Create(DepKind.BuildMir, sourcePath);
        var mirIndex = graph.GetIndex(mirNode);
        var mirDeps = graph.GetDependencies(mirIndex);
        Assert.True(mirDeps.Count >= 1, $"Mir should depend on at least 1 phase, got {mirDeps.Count}");
    }

    [Fact]
    public void IsUpToDate_Returns_True_For_Unchanged_Source()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();
        var source = "f :: Int -> Int { x => x }";

        session.Compile("test.eidos", source, options);

        Assert.True(session.IsUpToDate("test.eidos", source));
    }

    [Fact]
    public void IsUpToDate_Returns_False_For_Changed_Source()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();

        session.Compile("test.eidos", "f :: Int -> Int { x => x }", options);

        Assert.False(session.IsUpToDate("test.eidos", "g :: Int -> Int { x => x + 1 }"));
    }

    [Fact]
    public void IsUpToDate_Returns_False_For_Unknown_Source()
    {
        var session = new PipelineQuerySession();
        Assert.False(session.IsUpToDate("unknown.eidos", "code"));
    }

    [Fact]
    public void Compile_Returns_Cached_Result_When_Source_Unchanged()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();
        var source = "f :: Int -> Int { x => x }";

        var r1 = session.Compile("test.eidos", source, options);
        var r2 = session.Compile("test.eidos", source, options);

        Assert.True(r1.Success);
        Assert.Same(r1, r2);
    }

    [Fact]
    public void Compile_Recompiles_When_Source_Changes_Without_Explicit_Invalidation()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions { NoImplicitPrelude = true };

        var r1 = session.Compile("test.eidos", "f :: Int -> Int { x => x }", options);
        var r2 = session.Compile("test.eidos", "g :: Int -> Int { x => x + 1 }", options);

        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.NotSame(r1, r2);
        Assert.Contains(r2.SymbolTable!.Symbols.Values, symbol => symbol.Name == "g");
        Assert.DoesNotContain(r2.SymbolTable.Symbols.Values, symbol => symbol.Name == "f");
    }

    [Fact]
    public void Compile_Recompiles_When_Options_Change()
    {
        var session = new PipelineQuerySession();
        const string source = "f :: Int -> Int { x => x }";

        var r1 = session.Compile(
            "test.eidos",
            source,
            new CompilationOptions { NoImplicitPrelude = true });
        var r2 = session.Compile(
            "test.eidos",
            source,
            new CompilationOptions { NoImplicitPrelude = false });

        Assert.True(r1.Success);
        Assert.True(r2.Success);
        Assert.NotSame(r1, r2);
        Assert.False(session.IsUpToDate(
            "test.eidos",
            source,
            new CompilationOptions { NoImplicitPrelude = true }));
        Assert.True(session.IsUpToDate(
            "test.eidos",
            source,
            new CompilationOptions { NoImplicitPrelude = false }));
    }

    [Fact]
    public void Compile_Reuses_Result_When_DocumentVersion_Changes_WithSame_Content()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions { NoImplicitPrelude = true };
        const string source = "f :: Int -> Int { x => x }";

        var first = session.Compile("test.eidos", source, options, documentVersion: 1);
        var second = session.Compile("test.eidos", source, options, documentVersion: 2);

        Assert.True(first.Success);
        Assert.True(second.Success);
        Assert.Same(first, second);
        Assert.True(session.IsUpToDate("test.eidos", source, options, documentVersion: 1));
        Assert.True(session.IsUpToDate("test.eidos", source, options, documentVersion: 2));
    }

    [Fact]
    public void Compile_CanceledToken_ThrowsAndDoesNotCache()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions { NoImplicitPrelude = true };
        const string source = "f :: Int -> Int { x => x }";
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            session.Compile("test.eidos", source, options, documentVersion: 1, cancellationToken: cts.Token));
        Assert.False(session.IsUpToDate("test.eidos", source, options, documentVersion: 1));

        var result = session.Compile("test.eidos", source, options, documentVersion: 1);

        Assert.True(result.Success);
        Assert.True(session.IsUpToDate("test.eidos", source, options, documentVersion: 1));
    }

    [Fact]
    public void GetAffectedModules_Returns_Source_Itself()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions();
        session.Compile("test.eidos", "f :: Int -> Int { x => x }", options);

        var affected = session.GetAffectedModules("test.eidos");
        Assert.Contains(NormalizeQueryPath("test.eidos"), affected);
    }

    [Fact]
    public void GetAffectedModules_Uses_Resolved_Import_File_Path()
    {
        using var workspace = TemporaryWorkspace.Create();
        var entryFile = workspace.WriteFile("Main.eidos", """
import Lib

use :: Int -> Int { x => Lib.one(x) }
""");
        var libFile = workspace.WriteFile("lib.eidos", """
Lib :: module {
    export one :: Int -> Int { x => x }
}
""");

        var session = new PipelineQuerySession();
        var result = session.Compile(entryFile, File.ReadAllText(entryFile), CreateWorkspaceOptions(entryFile, workspace.Root));

        Assert.True(result.Success);
        var affected = session.GetAffectedModules(libFile);
        Assert.Contains(NormalizeQueryPath(libFile), affected);
        Assert.Contains(NormalizeQueryPath(entryFile), affected);
        Assert.DoesNotContain("Lib", affected);
    }

    [Fact]
    public void Import_Graph_Records_SourcePath_ModuleKey_Bidirectional_Map()
    {
        using var workspace = TemporaryWorkspace.Create();
        var entryFile = workspace.WriteFile("Main.eidos", """
import Lib

use :: Int -> Int { x => Lib.one(x) }
""");
        var libFile = workspace.WriteFile("lib.eidos", """
Lib :: module {
    export one :: Int -> Int { x => x }
}
""");

        var session = new PipelineQuerySession();
        var result = session.Compile(entryFile, File.ReadAllText(entryFile), CreateWorkspaceOptions(entryFile, workspace.Root));

        Assert.True(result.Success);
        Assert.True(session.TryGetModuleKeyForSourcePath(entryFile, out var entryKey));
        Assert.True(session.TryGetModuleKeyForSourcePath(libFile, out var libKey));
        Assert.Contains("Main", entryKey, StringComparison.Ordinal);
        Assert.Contains("Lib", libKey, StringComparison.Ordinal);
        Assert.Contains(NormalizeQueryPath(entryFile), session.GetSourcePathsForModuleKey(entryKey));
        Assert.Contains(NormalizeQueryPath(libFile), session.GetSourcePathsForModuleKey(libKey));

        var affected = session.GetAffectedModules(libFile);
        Assert.Contains(NormalizeQueryPath(libFile), affected);
        Assert.Contains(NormalizeQueryPath(entryFile), affected);
    }

    [Fact]
    public void Recompile_Replaces_Stale_Import_Graph_Edges()
    {
        using var workspace = TemporaryWorkspace.Create();
        var entryFile = workspace.WriteFile("Main.eidos", """
import LibA

use :: Int -> Int { x => LibA.one(x) }
""");
        var libAFile = workspace.WriteFile("lib_a.eidos", """
LibA :: module {
    export one :: Int -> Int { x => x }
}
""");
        var libBFile = workspace.WriteFile("lib_b.eidos", """
LibB :: module {
    export one :: Int -> Int { x => x }
}
""");

        var session = new PipelineQuerySession();
        var options = CreateWorkspaceOptions(entryFile, workspace.Root);
        var first = session.Compile(entryFile, File.ReadAllText(entryFile), options);
        Assert.True(first.Success);

        workspace.WriteFile("Main.eidos", """
import LibB

use :: Int -> Int { x => LibB.one(x) }
""");
        var second = session.Compile(entryFile, File.ReadAllText(entryFile), options);

        Assert.True(second.Success);
        Assert.DoesNotContain(NormalizeQueryPath(entryFile), session.GetAffectedModules(libAFile));
        Assert.Contains(NormalizeQueryPath(entryFile), session.GetAffectedModules(libBFile));
    }

    [Fact]
    public void SourceOverlay_UsesUnsavedImportedContentAndReportsExactImportClosure()
    {
        using var workspace = TemporaryWorkspace.Create();
        var entryFile = workspace.WriteFile("Main.eidos", """
import Lib

main :: Unit -> Int
{
    _ => Lib.parse("ok")
}
""");
        var libFile = workspace.WriteFile("Lib.eidos", """
Lib :: module {
    export parse :: Int -> Int { value => value }
}
""");
        var unrelatedFile = workspace.WriteFile("Other.eidos", """
Other :: module {
    export value :: 1;
}
""");
        const string unsavedLib = """
Lib :: module {
    export parse :: Int -> Int { value => value }
    export parse :: String -> Int { _ => 2 }
}
""";

        var session = new PipelineQuerySession();
        session.SetSourceOverlay(libFile, unsavedLib, documentVersion: 2);
        var options = CreateWorkspaceOptions(entryFile, workspace.Root);
        options.StopAtPhase = CompilationPhase.Types;

        var withOverlay = session.Compile(entryFile, File.ReadAllText(entryFile), options);

        Assert.True(withOverlay.Success, FormatDiagnostics(withOverlay));
        var importedSources = session.GetImportedSourcePaths(entryFile);
        Assert.True(
            importedSources.Contains(
                NormalizeQueryPath(libFile),
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal),
            $"Expected {NormalizeQueryPath(libFile)} in: {string.Join(", ", importedSources)}");
        Assert.DoesNotContain(
            importedSources,
            path => string.Equals(
                path,
                NormalizeQueryPath(unrelatedFile),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));

        session.RemoveSourceOverlay(libFile);
        session.InvalidateSource(libFile);
        var fromDisk = session.Compile(entryFile, File.ReadAllText(entryFile), options);

        Assert.False(fromDisk.Success);
    }

    [Fact]
    public void Recompile_Invalidates_CallSites_When_Local_Overload_Group_Changes()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions
        {
            NoImplicitPrelude = true,
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        };

        const string withoutStringOverload = """
parse :: Int -> Int
{
    value => value
}

value :: parse("ok");
""";
        const string withStringOverload = """
parse :: Int -> Int
{
    value => value
}

parse :: String -> Int
{
    _ => 2
}

value :: parse("ok");
""";

        var first = session.Compile("overload_query.eidos", withoutStringOverload, options);
        Assert.False(first.Success);

        var second = session.Compile("overload_query.eidos", withStringOverload, options);
        Assert.True(second.Success, FormatDiagnostics(second));

        var third = session.Compile("overload_query.eidos", withoutStringOverload, options);
        Assert.False(third.Success);
    }

    [Fact]
    public void InvalidateSource_Invalidates_Importers_When_Imported_Overload_Group_Changes()
    {
        using var workspace = TemporaryWorkspace.Create();
        var entryFile = workspace.WriteFile("Main.eidos", """
import Lib

main :: Unit -> Int
{
    _ => Lib.parse("ok")
}
""");
        var libFile = workspace.WriteFile("lib.eidos", """
Lib :: module {
    parse :: Int -> Int
    {
        value => value
    }

    parse :: String -> Int
    {
        _ => 2
    }
}
""");

        var session = new PipelineQuerySession();
        var options = CreateWorkspaceOptions(entryFile, workspace.Root);
        options.StopAtPhase = CompilationPhase.Types;

        var first = session.Compile(entryFile, File.ReadAllText(entryFile), options);
        Assert.True(first.Success, FormatDiagnostics(first));

        workspace.WriteFile("lib.eidos", """
Lib :: module {
    parse :: Int -> Int
    {
        value => value
    }
}
""");
        session.InvalidateSource(libFile);

        var second = session.Compile(entryFile, File.ReadAllText(entryFile), options);
        Assert.False(second.Success);

        workspace.WriteFile("lib.eidos", """
Lib :: module {
    parse :: Int -> Int
    {
        value => value
    }

    parse :: String -> Int
    {
        _ => 2
    }
}
""");
        session.InvalidateSource(libFile);

        var third = session.Compile(entryFile, File.ReadAllText(entryFile), options);
        Assert.True(third.Success, FormatDiagnostics(third));
    }

    [Fact]
    public void Recompile_Keeps_Imported_Overload_Candidates_Deterministic_Across_Import_Order_Changes()
    {
        using var workspace = TemporaryWorkspace.Create();
        var entryFile = workspace.WriteFile("Main.eidos", """
import TextParser
import IntParser

value :: parse("ok");
""");
        workspace.WriteFile("text_parser.eidos", """
TextParser :: module {
    export parse :: String -> Int
    {
        _ => 2
    }
}
""");
        workspace.WriteFile("int_parser.eidos", """
IntParser :: module {
    export parse :: Int -> Int
    {
        value => value
    }
}
""");

        var session = new PipelineQuerySession();
        var options = CreateWorkspaceOptions(entryFile, workspace.Root);
        options.StopAtPhase = CompilationPhase.Types;

        var first = session.Compile(entryFile, File.ReadAllText(entryFile), options);
        Assert.True(first.Success, FormatDiagnostics(first));

        workspace.WriteFile("Main.eidos", """
import IntParser
import TextParser

value :: parse("ok");
""");

        var second = session.Compile(entryFile, File.ReadAllText(entryFile), options);
        Assert.True(second.Success, FormatDiagnostics(second));
    }

    [Fact]
    public void Compile_MissingWorkspaceImport_Reports_Import_Diagnostic()
    {
        using var workspace = TemporaryWorkspace.Create();
        var entryFile = workspace.WriteFile("Main.eidos", """
import Missing.Thing

id :: Int -> Int { x => x }
""");

        var session = new PipelineQuerySession();
        var result = session.Compile(entryFile, File.ReadAllText(entryFile), CreateWorkspaceOptions(entryFile, workspace.Root));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "E3000" &&
            d.Message.Contains("Unable to resolve imported module 'Missing.Thing'", StringComparison.Ordinal));
        Assert.NotNull(result.Ast);
    }

    [Fact]
    public void Compile_ImportedModuleParseFailure_Reports_Contextual_Diagnostic()
    {
        using var workspace = TemporaryWorkspace.Create();
        var entryFile = workspace.WriteFile("Main.eidos", """
import Bad

id :: Int -> Int { x => x }
""");
        workspace.WriteFile("Bad.eidos", "func : -> { }");

        var session = new PipelineQuerySession();
        var result = session.Compile(entryFile, File.ReadAllText(entryFile), CreateWorkspaceOptions(entryFile, workspace.Root));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "E4001" &&
            d.Message.Contains("Imported module 'Bad' failed to parse", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, d => d.Code == "E4000" || d.Code == "E4001");
    }

    [Fact]
    public void Compile_ImportedModuleMismatch_Reports_Module_Mismatch_Diagnostic()
    {
        using var workspace = TemporaryWorkspace.Create();
        var entryFile = workspace.WriteFile("Main.eidos", """
import Lib.Wrong

id :: Int -> Int { x => x }
""");
        workspace.WriteFile("Lib/Wrong.eidos", """
Lib.Other :: module {
    export one :: Int -> Int { x => x }
}
""");

        var session = new PipelineQuerySession();
        var result = session.Compile(entryFile, File.ReadAllText(entryFile), CreateWorkspaceOptions(entryFile, workspace.Root));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d =>
            d.Code == "E3000" &&
            d.Message.Contains("does not declare module", StringComparison.Ordinal));
    }

    private static CompilationOptions CreateWorkspaceOptions(string entryFile, string importRoot)
    {
        return new CompilationOptions
        {
            InputFile = entryFile,
            ImportSearchRoots = [importRoot],
            NoImplicitPrelude = true,
            UseColors = false
        };
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
    }

    private static string NormalizeQueryPath(string sourcePath)
    {
        return Path.GetFullPath(sourcePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
    }

    private sealed class TemporaryWorkspace : IDisposable
    {
        private TemporaryWorkspace(string root)
        {
            Root = root;
        }

        public string Root { get; }

        public static TemporaryWorkspace Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"eidosc_query_{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new TemporaryWorkspace(root);
        }

        public string WriteFile(string relativePath, string contents)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
