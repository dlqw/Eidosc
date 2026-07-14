using Eidosc.Ast.Declarations;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

internal enum MetaDiagnosticLevel
{
    Error,
    Warning
}

internal sealed record MetaComptimeContext(
    SymbolTable SymbolTable,
    IReadOnlyDictionary<SymbolId, AdtDef> AdtDefinitions,
    IReadOnlyDictionary<SymbolId, TraitDef> TraitDefinitions,
    Action<MetaDiagnosticLevel, SourceSpan, string>? ReportDiagnostic = null,
    ComptimeMetaObjectValue? DeriveInput = null,
    string? ExpansionTrace = null,
    ComptimeResourceBudget? ResourceBudget = null,
    ComptimeTraceCollector? Trace = null,
    string TracePhase = "comptime")
{
    public ComptimeResourceBudget Resources { get; } = ResourceBudget ?? new ComptimeResourceBudget();
}
