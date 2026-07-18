using Eidosc.Ast.Declarations;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

internal enum MetaDiagnosticLevel
{
    Error,
    Warning
}

[Flags]
internal enum MetaDefinitionSiteLookupKind
{
    None = 0,
    Value = 1 << 0,
    Type = 1 << 1,
    Constructor = 1 << 2,
    Module = 1 << 3,
    Effect = 1 << 4,
    Any = Value | Type | Constructor | Module | Effect
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
    string TracePhase = "comptime",
    IReadOnlyDictionary<SymbolId, Declaration>? Declarations = null,
    MetaQueryAccessContext? QueryAccess = null,
    MetaQueryState? SharedQueryState = null,
    Func<IReadOnlyList<string>, MetaDefinitionSiteLookupKind, Symbol?>? DefinitionSiteResolver = null,
    SymbolId GeneratorSymbolId = default,
    string? InvocationOccurrenceIdentity = null)
{
    public ComptimeResourceBudget Resources { get; } = ResourceBudget ?? new ComptimeResourceBudget();

    public IReadOnlyDictionary<SymbolId, Declaration> DeclarationDefinitions { get; } =
        Declarations ?? new Dictionary<SymbolId, Declaration>();

    public MetaQueryAccessContext Access { get; } = QueryAccess ?? MetaQueryAccessContext.Default;

    public MetaQueryState Queries { get; } = SharedQueryState ?? MetaQueryState.For(SymbolTable);

    public Func<IReadOnlyList<string>, MetaDefinitionSiteLookupKind, Symbol?>? ResolveDefinitionSite { get; } =
        DefinitionSiteResolver;
}
