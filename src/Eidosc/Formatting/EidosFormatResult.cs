using EidosDiagnostic = Eidosc.Diagnostic.Diagnostic;

namespace Eidosc.CodeFormatting;

public sealed record EidosFormatResult
{
    public bool Success { get; init; }
    public string FormattedText { get; init; } = "";
    public IReadOnlyList<EidosDiagnostic> Diagnostics { get; init; } = [];

    public static EidosFormatResult Ok(string formattedText) => new()
    {
        Success = true,
        FormattedText = formattedText
    };

    public static EidosFormatResult Failed(IReadOnlyList<EidosDiagnostic> diagnostics) => new()
    {
        Success = false,
        Diagnostics = diagnostics
    };
}
