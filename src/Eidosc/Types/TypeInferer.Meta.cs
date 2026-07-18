using Eidosc.Ast.Declarations;
using Eidosc.Diagnostic;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private MetaComptimeContext CreateMetaComptimeContext(string trace, SymbolId requester) => new(
        _symbolTable,
        _adtDefinitionsBySymbol,
        _traitDefinitionsBySymbol,
        (level, span, message) => AddMetaComptimeDiagnostic(level, span, message, trace),
        ExpansionTrace: trace,
        ResourceBudget: ComptimeExecution.CreateBudget(),
        Trace: ComptimeExecution.Trace,
        TracePhase: "types.comptime",
        Declarations: _declarationsBySymbol,
        QueryAccess: new MetaQueryAccessContext(
            _symbolTable.Modules.TryGetOwningModuleId(requester, out var moduleId) ? moduleId : SymbolId.None,
            ClauseStage.Body,
            MetaQueryCapability.CurrentPackagePrivateShapes | MetaQueryCapability.CurrentPackageBodies,
            RequesterIdentity: _symbolTable.Modules.GetModule(moduleId) is { } requesterModule
                ? MetaComptimeIntrinsics.CreateStableIdentity(requesterModule, _symbolTable)
                : string.Empty));

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
