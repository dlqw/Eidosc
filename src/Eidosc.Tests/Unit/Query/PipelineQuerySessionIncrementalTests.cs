using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Query;

namespace Eidosc.Tests.Unit.Query;

public sealed class PipelineQuerySessionIncrementalTests
{
    [Fact]
    public void Compile_WithTypesStop_DoesNotRunLaterPhases()
    {
        var session = new PipelineQuerySession();
        var result = session.Compile(
            "types_only.eidos",
            "id :: Int -> Int { x => x }",
            new CompilationOptions
            {
                StopAtPhase = CompilationPhase.Types,
                NoImplicitPrelude = true
            });

        Assert.True(result.Success);
        Assert.Equal(CompilationPhase.Types, result.CompletedPhase);
        Assert.NotNull(result.SymbolTable);
        Assert.NotNull(result.TypeInferer);
        Assert.Null(result.HirModule);
        Assert.Null(result.MirModule);
        Assert.Null(result.LlvmModule);
    }

    [Fact]
    public void Compile_SamePathContentChangeInvalidatesOnlyParseDependents()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions
        {
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true
        };

        var first = session.Compile("hot.eidos", "value :: 1;", options, documentVersion: 1);
        Assert.True(first.Success);
        var graph = session.GetDependencyGraph("hot.eidos");
        Assert.NotNull(graph);
        var sourcePath = NormalizeQueryPath("hot.eidos");
        Assert.True(graph.GetIndex(DepNode.Create(DepKind.ParseModule, sourcePath)).IsValid);

        var second = session.Compile("hot.eidos", "value :: 2;", options, documentVersion: 2);
        Assert.True(second.Success);

        var updatedGraph = session.GetDependencyGraph("hot.eidos");
        Assert.Same(graph, updatedGraph);
        Assert.True(updatedGraph!.GetIndex(DepNode.Create(DepKind.ParseModule, sourcePath)).IsValid);
        Assert.True(updatedGraph.GetIndex(DepNode.Create(DepKind.InferTypes, sourcePath)).IsValid);
    }

    [Fact]
    public void Compile_SameContentDifferentDocumentVersion_ReusesCachedResult()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions
        {
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true
        };

        var source = "value :: 1;";
        var first = session.Compile("same_content.eidos", source, options, documentVersion: 1);
        var second = session.Compile("same_content.eidos", source, options, documentVersion: 2);

        Assert.Same(first, second);
    }

    [Fact]
    public void Compile_TrailingWhitespaceOnlyChange_ReusesCachedResult()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions
        {
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true
        };

        var first = session.Compile("trailing.eidos", "value :: 1;", options, documentVersion: 1);
        var second = session.Compile("trailing.eidos", "value :: 1;\r\n", options, documentVersion: 2);

        Assert.Same(first, second);
    }

    [Fact]
    public void Compile_SpanPreservingInnerWhitespaceOnlyChange_ReusesCachedResult()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions
        {
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true
        };

        var first = session.Compile("inner_whitespace.eidos", "value :: 1;", options, documentVersion: 1);
        var second = session.Compile("inner_whitespace.eidos", "value\t:: 1;", options, documentVersion: 2);

        Assert.Same(first, second);
    }

    [Fact]
    public void Compile_NonSpanPreservingInnerWhitespaceChange_InvalidatesCachedResult()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions
        {
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true
        };

        var first = session.Compile("inner_whitespace_shift.eidos", "value :: 1;", options, documentVersion: 1);
        var second = session.Compile("inner_whitespace_shift.eidos", "value  :: 1;", options, documentVersion: 2);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Compile_CommentTextChange_InvalidatesCachedResult()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions
        {
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true
        };

        var first = session.Compile("comment_change.eidos", "// a\nvalue :: 1;", options, documentVersion: 1);
        var second = session.Compile("comment_change.eidos", "// b\nvalue :: 1;", options, documentVersion: 2);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Compile_NonTrailingContentChange_InvalidatesCachedResult()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions
        {
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true
        };

        var first = session.Compile("content_change.eidos", "value :: 1;", options, documentVersion: 1);
        var second = session.Compile("content_change.eidos", "value :: 2;", options, documentVersion: 2);

        Assert.NotSame(first, second);
    }

    [Fact]
    public void Compile_PreviousExactContentEdit_ReusesHistoryResult()
    {
        var session = new PipelineQuerySession();
        var options = new CompilationOptions
        {
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true
        };

        var first = session.Compile("history.eidos", "value :: 1;", options, documentVersion: 1);
        var second = session.Compile("history.eidos", "value :: 2;", options, documentVersion: 2);
        var third = session.Compile("history.eidos", "value :: 1;", options, documentVersion: 3);
        var fourth = session.Compile("history.eidos", "value :: 3;", options, documentVersion: 4);

        Assert.NotSame(first, second);
        Assert.Same(first, third);
        Assert.NotSame(first, fourth);
        Assert.NotSame(second, fourth);
    }

    [Fact]
    public void Compile_TypesQueryWithPrecompiledStd_UsesSignatureOnlyImportedBodies()
    {
        using var workspace = TempWorkspace.Create();
        var session = new PipelineQuerySession();
        const string source = """
import Std.GameMath

main :: Unit -> Int
{
    _ => GameMath.scale_i(GameMath.east_i, 4).x
}
""";
        var sourcePath = workspace.WriteFile("std_signature_query.eidos", source);

        var result = session.Compile(
            sourcePath,
            source,
            new CompilationOptions
            {
                InputFile = sourcePath,
                StopAtPhase = CompilationPhase.Types,
                LanguageVersion = EidosLanguageVersions.Current,
                EnableDetailedProfiling = true,
                NoImplicitPrelude = true
            });

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.True(
            result.ProfilingCounters.TryGetValue("Query.precompiledSignatureSource.replacedFunctionBodies", out var replacedFunctionBodies),
            FormatCounters(result));
        Assert.True(replacedFunctionBodies > 0, FormatCounters(result));
        Assert.True(
            result.ProfilingCounters.TryGetValue("Query.precompiledSignatureSource.replacedValueInitializers", out var replacedValueInitializers),
            FormatCounters(result));
        Assert.True(replacedValueInitializers > 0, FormatCounters(result));
        Assert.True(
            result.ProfilingCounters.TryGetValue("Query.precompiledSignatureSource.removedNonExportImports", out var removedNonExportImports),
            FormatCounters(result));
        Assert.True(removedNonExportImports > 0, FormatCounters(result));
    }

    [Fact]
    public void Compile_FullPipelineTypesWithPrecompiledStd_UsesSemanticSignatureOnlyButNotQuerySourceStripping()
    {
        using var workspace = TempWorkspace.Create();
        const string source = """
import Std.GameMath

main :: Unit -> Int
{
    _ => GameMath.scale_i(GameMath.east_i, 4).x
}
""";
        var sourcePath = workspace.WriteFile("std_signature_full_pipeline.eidos", source);

        var result = new CompilationPipeline(
            source,
            new CompilationOptions
            {
                InputFile = sourcePath,
                StopAtPhase = CompilationPhase.Types,
                LanguageVersion = EidosLanguageVersions.Current,
                EnableDetailedProfiling = true,
                NoImplicitPrelude = true
            }).Run();

        Assert.True(result.Success, FormatDiagnostics(result));
        Assert.False(
            result.ProfilingCounters.ContainsKey("Query.precompiledSignatureSource.replacedFunctionBodies"),
            FormatCounters(result));
        Assert.False(
            result.ProfilingCounters.ContainsKey("Query.precompiledSignatureSource.replacedValueInitializers"),
            FormatCounters(result));
        Assert.False(
            result.ProfilingCounters.ContainsKey("Query.precompiledSignatureSource.removedNonExportImports"),
            FormatCounters(result));
        Assert.True(
            result.ProfilingCounters.TryGetValue("Namer.precompiledImportSignatureOnly.functions", out var namerSignatureOnly),
            FormatCounters(result));
        Assert.True(namerSignatureOnly > 0, FormatCounters(result));
        Assert.True(
            result.ProfilingCounters.TryGetValue("Types.precompiledImportSignatureOnly.functions", out var typesSignatureOnly),
            FormatCounters(result));
        Assert.True(typesSignatureOnly > 0, FormatCounters(result));
    }

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"));
    }

    private static string FormatCounters(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.ProfilingCounters
                .OrderBy(counter => counter.Key, StringComparer.Ordinal)
                .Select(counter => $"{counter.Key}={counter.Value}"));
    }

    private static string NormalizeQueryPath(string sourcePath)
    {
        return Path.GetFullPath(sourcePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/');
    }

    private sealed class TempWorkspace : IDisposable
    {
        private TempWorkspace(string root)
        {
            Root = root;
        }

        public string Root { get; }

        public static TempWorkspace Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"eidos-query-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new TempWorkspace(root);
        }

        public string WriteFile(string relativePath, string source)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, source);
            return path;
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                {
                    Directory.Delete(Root, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for test temp files.
            }
        }
    }
}
