using Eidosc.Diagnostic;
using Eidosc.Pipeline;

namespace Eidosc.Tests.Fixtures;

public static class TestDiagnosticFormatter
{
    public static string Format(CompilationResult result) => Format(result.Diagnostics);

    public static string Format(IEnumerable<Diagnostic.Diagnostic> diagnostics) =>
        string.Join(Environment.NewLine, diagnostics.Select(Format));

    public static string Format(Diagnostic.Diagnostic diagnostic) =>
        $"{diagnostic.Level} {diagnostic.Code}: {diagnostic.Message}";

    public static string FormatErrors(CompilationResult result) =>
        Format(result.Diagnostics.Where(static diagnostic => diagnostic.Level == DiagnosticLevel.Error));
}
