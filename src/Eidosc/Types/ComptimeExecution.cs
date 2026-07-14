using Eidosc.Utils;

namespace Eidosc.Types;

public sealed record ComptimeTraceEntry(
    long Sequence,
    string Phase,
    string Kind,
    string Operation,
    string Outcome,
    string Detail,
    string FilePath,
    int Position,
    int Length,
    int CallDepth);

internal sealed class ComptimeTraceCollector(bool enabled)
{
    private const int MaxDetailLength = 512;
    private readonly object _gate = new();
    private readonly List<ComptimeTraceEntry> _entries = [];
    private long _nextSequence;

    public bool Enabled { get; } = enabled;

    public void Record(
        string phase,
        string kind,
        string operation,
        string outcome,
        string detail,
        SourceSpan span,
        int callDepth)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _entries.Add(new ComptimeTraceEntry(
                ++_nextSequence,
                phase,
                kind,
                operation,
                outcome,
                detail.Length <= MaxDetailLength ? detail : detail[..MaxDetailLength] + "…",
                span.FilePath ?? string.Empty,
                span.Position,
                span.Length,
                callDepth));
        }
    }

    public IReadOnlyList<ComptimeTraceEntry> Snapshot()
    {
        lock (_gate)
        {
            return _entries.ToArray();
        }
    }
}

internal sealed record ComptimeExecutionOptions(
    long FuelLimit,
    long AllocatedValueBytesLimit,
    int DiagnosticLimit,
    ComptimeTraceCollector Trace)
{
    public static ComptimeExecutionOptions Create(Pipeline.CompilationOptions options) => new(
        Math.Max(1, options.ComptimeFuelBudget),
        Math.Max(1, options.ComptimeAllocatedValueBytesBudget),
        Math.Max(1, options.ComptimeDiagnosticBudget),
        new ComptimeTraceCollector(options.TraceComptime));

    public static ComptimeExecutionOptions Disabled { get; } = new(
        ComptimeResourceBudget.DefaultFuel,
        ComptimeResourceBudget.DefaultAllocatedBytes,
        ComptimeResourceBudget.DefaultDiagnosticCount,
        new ComptimeTraceCollector(false));

    public ComptimeResourceBudget CreateBudget() => new(
        FuelLimit,
        AllocatedValueBytesLimit,
        DiagnosticLimit);
}
