using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Query;

namespace Eidosc.Tests.Unit.Query;

public sealed class PhysicalStdlibQueryTests
{
    [Fact]
    public void Compile_SequentialPhysicalStdlibDocuments_HasNoErrors()
    {
        var eidoscRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var stdlibRoot = Path.Combine(eidoscRoot, "src", "Eidosc", "Stdlib", "Precompiled", "std");
        var paths = Directory.GetFiles(stdlibRoot, "*.eidos")
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var session = new PipelineQuerySession();
        var openPaths = new List<string>();
        var failures = new List<string>();

        foreach (var path in paths)
        {
            openPaths.Add(path);
            session.SetSourceOverlay(path, File.ReadAllText(path), openPaths.Count);
            var result = session.Compile(
                path,
                File.ReadAllText(path),
                new CompilationOptions
                {
                    InputFile = path,
                    StopAtPhase = CompilationPhase.Types,
                    LanguageVersion = EidosLanguageVersions.Current,
                    UseColors = false,
                    ToolchainOwnedSourcePaths = openPaths.ToArray()
                },
                openPaths.Count);

            var errors = result.Diagnostics
                .Where(diagnostic => diagnostic.Level == Eidosc.Diagnostic.DiagnosticLevel.Error)
                .ToArray();
            if (errors.Length > 0)
            {
                failures.Add($"{path}: {string.Join(" | ", errors.Select(static error =>
                    $"{error.Message}@{string.Join(",", error.Labels.Select(label => $"{label.Span.FilePath}:{label.Span.Location.Line + 1}"))}"))}");
            }
        }

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
}
