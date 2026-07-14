using Eidosc.Diagnostic;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private MetaComptimeContext CreateMetaComptimeContext(string trace) => new(
        _symbolTable,
        _adtDefinitionsBySymbol,
        _traitDefinitionsBySymbol,
        (level, span, message) => AddMetaComptimeDiagnostic(level, span, message, trace),
        ExpansionTrace: trace,
        ResourceBudget: ComptimeExecution.CreateBudget(),
        Trace: ComptimeExecution.Trace,
        TracePhase: "types.comptime");

    private void AddMetaComptimeDiagnostic(
        MetaDiagnosticLevel level,
        SourceSpan span,
        string message,
        string trace)
    {
        var diagnosticLevel = level == MetaDiagnosticLevel.Error
            ? DiagnosticLevel.Error
            : DiagnosticLevel.Warning;
        var code = level == MetaDiagnosticLevel.Error ? "E4015" : "W4015";
        var diagnostic = new Diagnostic.Diagnostic(diagnosticLevel, message, code);
        diagnostic.WithLabel(span, message);
        diagnostic.WithNote($"comptime trace: {trace}");
        _diagnostics.Add(diagnostic);
        if (diagnosticLevel == DiagnosticLevel.Error)
        {
            _recoveryContext.RecordError();
        }
    }
}
