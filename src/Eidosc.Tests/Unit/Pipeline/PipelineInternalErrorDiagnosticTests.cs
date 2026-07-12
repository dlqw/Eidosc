using Eidosc.Debug;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Pipeline;

public class PipelineInternalErrorDiagnosticTests
{
    [Fact]
    public void CreateInternalErrorDiagnostic_NormalMode_DoesNotExposeExceptionDetails()
    {
        var diagnostic = CompilationPipeline.CreateInternalErrorDiagnostic(
            new InvalidOperationException("sensitive internal state"),
            includeExceptionDetails: false);

        Assert.Equal("E0001", diagnostic.Code);
        Assert.DoesNotContain("sensitive internal state", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("堆栈", diagnostic.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("stack trace", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(diagnostic.Notes, note => note.Contains("sensitive internal state", StringComparison.Ordinal));
        Assert.DoesNotContain(diagnostic.Notes, note => note.Contains("stack trace", StringComparison.OrdinalIgnoreCase));
        Assert.NotEmpty(diagnostic.Helps);
    }

    [Fact]
    public void CreateInternalErrorDiagnostic_DiagnosticMode_PreservesExceptionDetailsAsNotes()
    {
        var diagnostic = CompilationPipeline.CreateInternalErrorDiagnostic(
            new InvalidOperationException("expected diagnostic detail"),
            includeExceptionDetails: true);

        Assert.Equal("E0001", diagnostic.Code);
        Assert.DoesNotContain("stack trace", diagnostic.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(diagnostic.Notes, note => note.Contains("InvalidOperationException", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("expected diagnostic detail", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("stack trace", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ShouldExposeInternalExceptionDetails_RequiresDiagnosticDebugOutput()
    {
        var debugOutputPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(debugOutputPath);

        try
        {
            Assert.False(CompilationPipeline.ShouldExposeInternalExceptionDetails(new CompilationOptions
            {
                DebugLevel = DebugLevel.Diagnostic
            }));

            Assert.False(CompilationPipeline.ShouldExposeInternalExceptionDetails(new CompilationOptions
            {
                DebugOutputPath = debugOutputPath,
                DebugLevel = DebugLevel.Normal
            }));

            Assert.True(CompilationPipeline.ShouldExposeInternalExceptionDetails(new CompilationOptions
            {
                DebugOutputPath = debugOutputPath,
                DebugLevel = DebugLevel.Diagnostic
            }));
        }
        finally
        {
            Directory.Delete(debugOutputPath, recursive: true);
        }
    }
}
