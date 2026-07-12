using System.Diagnostics;
using System.Text.Json;
using System.Text;

namespace Eidosc.Pipeline;

public enum ProfilingTableFormat
{
    Markdown,
    Csv,
    Tsv,
    Json
}

public sealed class CompilationProfilingSnapshot
{
    public string SchemaVersion { get; init; } = "1";
    public string InputFile { get; init; } = "";
    public double TotalTimeMs { get; init; }
    public List<CompilationProfilingPhaseSnapshot> Phases { get; init; } = [];
    public List<CompilationProfilingSubphaseSnapshot> Subphases { get; init; } = [];
    public List<CompilationProfilingSubphaseAggregateSnapshot> SubphaseAggregates { get; init; } = [];
    public List<CompilationProfilingCounterSnapshot> Counters { get; init; } = [];
    public ProjectModuleGraphSnapshot? ModuleGraph { get; init; }
    public ProjectModuleBuildSchedule? ModuleBuildSchedule { get; init; }
    public ProjectModuleSignatureSnapshot? ModuleSignatures { get; init; }
    public ProjectModuleSemanticSignatureSnapshot? ModuleSemanticSignatures { get; init; }
    public ProjectModuleTypedSemanticSnapshot? ModuleTypedSemanticSignatures { get; init; }
    public ProjectModuleMirArtifactSnapshot? ModuleMirArtifacts { get; init; }
    public ProjectModuleDependencySignatureSnapshot? ModuleDependencySignatures { get; init; }
    public ProjectModuleMemberIndexSnapshot? ModuleMemberIndex { get; init; }
    public ProjectModuleMemberIndexRestorePlan? ModuleMemberIndexRestorePlan { get; init; }
    public ProjectModuleMemberIndexRestorePayloadSnapshot? ModuleMemberIndexRestorePayload { get; init; }
    public ProjectModuleInvalidationPlan? ModuleInvalidation { get; init; }
    public ProjectModuleInvalidationPlan? ModuleTypedInvalidation { get; init; }
    public ProjectModuleExecutionPlan? ModuleExecution { get; init; }
    public ProjectModuleExecutionPlan? ModuleTypedExecution { get; init; }
    public ProjectModuleParallelExecutionSnapshot? ModuleParallelExecution { get; init; }
    public ProjectModuleParallelExecutionSnapshot? ModuleTypedParallelExecution { get; init; }
    public ProjectModuleArtifactReadinessPlan? ModuleArtifactReadiness { get; init; }
    public ProjectModuleArtifactReadinessPlan? ModuleTypedArtifactReadiness { get; init; }
    public ProjectModuleArtifactRestorePlan? ModuleArtifactRestore { get; init; }
    public ProjectModuleArtifactRestorePlan? ModuleTypedArtifactRestore { get; init; }
    public ProjectModuleArtifactRestoreExecutionSnapshot? ModuleArtifactRestoreExecution { get; init; }
    public ProjectModuleArtifactRestoreExecutionSnapshot? ModuleTypedArtifactRestoreExecution { get; init; }
    public ProjectModuleArtifactRestorePayloadSnapshot? ModuleArtifactRestorePayload { get; init; }
    public ProjectModuleArtifactRestorePayloadSnapshot? ModuleTypedArtifactRestorePayload { get; init; }
    public Semantic.ImplOverlapCheckSnapshot? ImplOverlapChecks { get; init; }
    public Types.TypeDirectedCallableResolutionSnapshot? TypeDirectedCallableResolution { get; init; }
    public Types.AssociatedTypeProjectionSnapshot? AssociatedTypeProjection { get; init; }
    public Types.AssociatedConstProjectionSnapshot? AssociatedConstProjection { get; init; }
    public Types.TraitCheckSnapshot? TraitCheck { get; init; }
    public SendAnalysisSnapshot? SendAnalysis { get; init; }
    public BorrowDiagnosticSnapshot? BorrowDiagnostics { get; init; }
    public BorrowCodegenHintsSnapshot? BorrowCodegenHints { get; init; }
    public Mir.MirFunctionFingerprintSnapshot? MirFunctionFingerprints { get; init; }
    public CodeGen.Llvm.LlvmFunctionFingerprintSnapshot? LlvmFunctionFingerprints { get; init; }
    public CodeGen.Llvm.LlvmFunctionFragmentSnapshot? LlvmFunctionFragments { get; init; }
    public CodeGen.Llvm.LlvmFunctionFragmentRestorePlanSnapshot? LlvmFunctionFragmentRestorePlan { get; init; }
    public CodeGen.Llvm.LlvmFunctionFragmentRestoreResultSnapshot? LlvmFunctionFragmentRestoreResult { get; init; }
    public CodeGen.Llvm.LlvmModuleEnvelopeSnapshot? LlvmModuleEnvelope { get; init; }
    public CodeGen.Llvm.LlvmCodegenUnitPlanSnapshot? LlvmCodegenUnitPlan { get; init; }
    public CodeGen.Llvm.LlvmObjectGroupRestorePlanSnapshot? LlvmObjectGroupRestorePlan { get; init; }
    public FunctionFingerprintDiffSnapshot? MirFunctionFingerprintDiff { get; init; }
    public FunctionFingerprintDiffSnapshot? LlvmFunctionFingerprintDiff { get; init; }
    public FunctionWorklistSnapshot? MirFunctionWorklist { get; init; }
    public FunctionWorklistSnapshot? LlvmFunctionWorklist { get; init; }
    public List<CodeGen.CodeGenProfileEvent> CodeGenEvents { get; init; } = [];
}

public sealed class CompilationProfilingPhaseSnapshot
{
    public string Phase { get; init; } = "";
    public double ElapsedMs { get; init; }
    public double TotalPercent { get; init; }
    public long AllocatedBytes { get; init; }
    public double AllocPercent { get; init; }
}

public sealed class CompilationProfilingSubphaseSnapshot
{
    public string Phase { get; init; } = "";
    public string Name { get; init; } = "";
    public double ElapsedMs { get; init; }
    public double PhasePercent { get; init; }
    public double TotalPercent { get; init; }
    public long AllocatedBytes { get; init; }
    public double AllocPercent { get; init; }
    public long ManagedBytesBefore { get; init; }
    public long ManagedBytesAfter { get; init; }
    public long ManagedBytesDelta { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
}

public sealed class CompilationProfilingSubphaseAggregateSnapshot
{
    public string Phase { get; init; } = "";
    public string Name { get; init; } = "";
    public int Records { get; init; }
    public double ElapsedMs { get; init; }
    public long AllocatedBytes { get; init; }
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
}

public sealed class CompilationProfilingCounterSnapshot
{
    public string Name { get; init; } = "";
    public long Value { get; init; }
}

public sealed class CompilationSubphaseMetrics
{
    public CompilationPhase Phase { get; init; }
    public string Name { get; init; } = "";
    public TimeSpan Elapsed { get; init; }
    public long AllocatedBytes { get; init; }
    public long ManagedBytesBefore { get; init; }
    public long ManagedBytesAfter { get; init; }
    public long ManagedBytesDelta => ManagedBytesAfter - ManagedBytesBefore;
    public int Gen0Collections { get; init; }
    public int Gen1Collections { get; init; }
    public int Gen2Collections { get; init; }
}

internal sealed class CompilationProfiler
{
    private static readonly IDisposable NoopScopeInstance = new NoopScope();
    private readonly List<CompilationSubphaseMetrics> _subphases = [];
    private readonly bool _enabled;

    public CompilationProfiler(bool enabled = true)
    {
        _enabled = enabled;
    }

    public List<CompilationSubphaseMetrics> Subphases => _enabled ? _subphases : [];

    public IDisposable MeasureSubphase(CompilationPhase phase, string name)
    {
        if (!_enabled)
        {
            return NoopScopeInstance;
        }

        return new ProfilingScope(this, phase, name);
    }

    private void Record(CompilationSubphaseMetrics metrics)
    {
        _subphases.Add(metrics);
    }

    private sealed class NoopScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private sealed class ProfilingScope : IDisposable
    {
        private readonly CompilationProfiler _owner;
        private readonly CompilationPhase _phase;
        private readonly string _name;
        private readonly Stopwatch _stopwatch;
        private readonly long _allocatedBytesBefore;
        private readonly long _managedBytesBefore;
        private readonly int _gen0Before;
        private readonly int _gen1Before;
        private readonly int _gen2Before;
        private bool _disposed;

        public ProfilingScope(CompilationProfiler owner, CompilationPhase phase, string name)
        {
            _owner = owner;
            _phase = phase;
            _name = name;
            _stopwatch = Stopwatch.StartNew();
            _allocatedBytesBefore = GC.GetAllocatedBytesForCurrentThread();
            _managedBytesBefore = GC.GetTotalMemory(false);
            _gen0Before = GC.CollectionCount(0);
            _gen1Before = GC.CollectionCount(1);
            _gen2Before = GC.CollectionCount(2);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();

            _owner.Record(new CompilationSubphaseMetrics
            {
                Phase = _phase,
                Name = _name,
                Elapsed = _stopwatch.Elapsed,
                AllocatedBytes = Math.Max(0L, GC.GetAllocatedBytesForCurrentThread() - _allocatedBytesBefore),
                ManagedBytesBefore = _managedBytesBefore,
                ManagedBytesAfter = GC.GetTotalMemory(false),
                Gen0Collections = GC.CollectionCount(0) - _gen0Before,
                Gen1Collections = GC.CollectionCount(1) - _gen1Before,
                Gen2Collections = GC.CollectionCount(2) - _gen2Before
            });
        }
    }
}

public static class CompilationProfilingFormatter
{
    private const int DefaultTopPhaseCount = 5;
    private const int DefaultTopSubphaseCount = 10;

    public static string FormatTables(CompilationResult result, ProfilingTableFormat format)
    {
        return format switch
        {
            ProfilingTableFormat.Json => JsonSerializer.Serialize(
                CreateSnapshot(result),
                CreateJsonOptions()),
            ProfilingTableFormat.Csv => FormatDelimited(result, ","),
            ProfilingTableFormat.Tsv => FormatDelimited(result, "\t"),
            _ => FormatMarkdown(result)
        };
    }

    public static string FormatHotspotReport(
        CompilationResult result,
        int topPhaseCount = DefaultTopPhaseCount,
        int topSubphaseCount = DefaultTopSubphaseCount)
    {
        var phaseRows = BuildPhaseRows(result);
        var subphaseRows = BuildSubphaseRows(result);
        var sb = new StringBuilder();

        sb.AppendLine(PipelineMessages.ProfilingHotspotSummaryHeader);
        sb.AppendLine();

        if (phaseRows.Count == 0)
        {
            sb.AppendLine(PipelineMessages.ProfilingNoPhaseDataRecorded);
            return sb.ToString();
        }

        AppendPhaseHotspots(
            sb,
            PipelineMessages.ProfilingTopPhasesByTime,
            phaseRows.OrderByDescending(row => row.ElapsedMs),
            topPhaseCount,
            row => $"{row.ElapsedMs:F2} ms",
            row => row.TotalPercent);
        AppendPhaseHotspots(
            sb,
            PipelineMessages.ProfilingTopPhasesByAllocation,
            phaseRows.OrderByDescending(row => row.AllocatedBytes),
            topPhaseCount,
            row => FormatBytes(row.AllocatedBytes),
            row => row.AllocPercent);

        if (subphaseRows.Count > 0)
        {
            AppendSubphaseHotspots(
                sb,
                PipelineMessages.ProfilingTopSubphasesByTime,
                subphaseRows.OrderByDescending(row => row.ElapsedMs),
                topSubphaseCount,
                row => $"{row.ElapsedMs:F2} ms",
                row => row.TotalPercent);
            AppendSubphaseHotspots(
                sb,
                PipelineMessages.ProfilingTopSubphasesByAllocation,
                subphaseRows.OrderByDescending(row => row.AllocatedBytes),
                topSubphaseCount,
                row => FormatBytes(row.AllocatedBytes),
                row => row.AllocPercent);

            AppendOptimizationCandidates(
                sb,
                phaseRows.OrderByDescending(row => row.ElapsedMs).First(),
                phaseRows.OrderByDescending(row => row.AllocatedBytes).First(),
                subphaseRows.OrderByDescending(row => row.ElapsedMs).First(),
                subphaseRows.OrderByDescending(row => row.AllocatedBytes).First());
        }
        else
        {
            AppendOptimizationCandidates(
                sb,
                phaseRows.OrderByDescending(row => row.ElapsedMs).First(),
                phaseRows.OrderByDescending(row => row.AllocatedBytes).First(),
                null,
                null);
        }

        return sb.ToString();
    }

    public static string FormatMarkdownReportWithTables(
        CompilationResult result,
        int topPhaseCount = DefaultTopPhaseCount,
        int topSubphaseCount = DefaultTopSubphaseCount)
    {
        var summary = FormatHotspotReport(result, topPhaseCount, topSubphaseCount).TrimEnd();
        var tables = FormatMarkdown(result).TrimStart();
        return $"{summary}{Environment.NewLine}{Environment.NewLine}{tables}";
    }

    public static CompilationProfilingSnapshot CreateSnapshot(CompilationResult result)
    {
        var phaseRows = BuildPhaseRows(result);
        var subphaseRows = BuildSubphaseRows(result);

        return new CompilationProfilingSnapshot
        {
            InputFile = result.InputFile,
            TotalTimeMs = result.TotalTime.TotalMilliseconds,
            Phases = phaseRows
                .Select(row => new CompilationProfilingPhaseSnapshot
                {
                    Phase = row.Phase.ToString(),
                    ElapsedMs = row.ElapsedMs,
                    TotalPercent = row.TotalPercent,
                    AllocatedBytes = row.AllocatedBytes,
                    AllocPercent = row.AllocPercent
                })
                .ToList(),
            Subphases = subphaseRows
                .Select(row => new CompilationProfilingSubphaseSnapshot
                {
                    Phase = row.Phase.ToString(),
                    Name = row.Name,
                    ElapsedMs = row.ElapsedMs,
                    PhasePercent = row.PhasePercent,
                    TotalPercent = row.TotalPercent,
                    AllocatedBytes = row.AllocatedBytes,
                    AllocPercent = row.AllocPercent,
                    ManagedBytesBefore = row.ManagedBytesBefore,
                    ManagedBytesAfter = row.ManagedBytesAfter,
                    ManagedBytesDelta = row.ManagedBytesDelta,
                    Gen0Collections = row.Gen0Collections,
                    Gen1Collections = row.Gen1Collections,
                    Gen2Collections = row.Gen2Collections
                })
                .ToList(),
            SubphaseAggregates = BuildSubphaseAggregateRows(result)
                .Select(row => new CompilationProfilingSubphaseAggregateSnapshot
                {
                    Phase = row.Phase.ToString(),
                    Name = row.Name,
                    Records = row.Records,
                    ElapsedMs = row.ElapsedMs,
                    AllocatedBytes = row.AllocatedBytes,
                    Gen0Collections = row.Gen0Collections,
                    Gen1Collections = row.Gen1Collections,
                    Gen2Collections = row.Gen2Collections
                })
                .ToList(),
            Counters = result.ProfilingCounters
                .OrderBy(entry => entry.Key, StringComparer.Ordinal)
                .Select(entry => new CompilationProfilingCounterSnapshot
                {
                    Name = entry.Key,
                    Value = entry.Value
                })
                .ToList(),
            ModuleGraph = result.ModuleGraphSnapshot,
            ModuleBuildSchedule = result.ModuleBuildSchedule,
            ModuleSignatures = result.ModuleSignatureSnapshot,
            ModuleSemanticSignatures = result.ModuleSemanticSignatureSnapshot,
            ModuleTypedSemanticSignatures = result.ModuleTypedSemanticSnapshot,
            ModuleMirArtifacts = result.ModuleMirArtifactSnapshot,
            ModuleDependencySignatures = result.ModuleDependencySignatureSnapshot,
            ModuleMemberIndex = result.ModuleMemberIndexSnapshot,
            ModuleMemberIndexRestorePlan = result.ModuleMemberIndexRestorePlan,
            ModuleMemberIndexRestorePayload = result.ModuleMemberIndexRestorePayload,
            ModuleInvalidation = result.ModuleInvalidationPlan,
            ModuleTypedInvalidation = result.ModuleTypedInvalidationPlan,
            ModuleExecution = result.ModuleExecutionPlan,
            ModuleTypedExecution = result.ModuleTypedExecutionPlan,
            ModuleParallelExecution = result.ModuleParallelExecution,
            ModuleTypedParallelExecution = result.ModuleTypedParallelExecution,
            ModuleArtifactReadiness = result.ModuleArtifactReadinessPlan,
            ModuleTypedArtifactReadiness = result.ModuleTypedArtifactReadinessPlan,
            ModuleArtifactRestore = result.ModuleArtifactRestorePlan,
            ModuleTypedArtifactRestore = result.ModuleTypedArtifactRestorePlan,
            ModuleArtifactRestoreExecution = result.ModuleArtifactRestoreExecution,
            ModuleTypedArtifactRestoreExecution = result.ModuleTypedArtifactRestoreExecution,
            ModuleArtifactRestorePayload = result.ModuleArtifactRestorePayload,
            ModuleTypedArtifactRestorePayload = result.ModuleTypedArtifactRestorePayload,
            ImplOverlapChecks = result.ImplOverlapCheckSnapshot,
            TypeDirectedCallableResolution = result.TypeDirectedCallableResolutionSnapshot,
            AssociatedTypeProjection = result.AssociatedTypeProjectionSnapshot,
            AssociatedConstProjection = result.AssociatedConstProjectionSnapshot,
            TraitCheck = result.TraitCheckSnapshot,
            SendAnalysis = result.SendAnalysisSnapshot,
            BorrowDiagnostics = result.BorrowDiagnosticSnapshot,
            BorrowCodegenHints = result.BorrowCodegenHintsSnapshot,
            MirFunctionFingerprints = result.MirFunctionFingerprints,
            LlvmFunctionFingerprints = result.LlvmFunctionFingerprints,
            LlvmFunctionFragments = result.LlvmFunctionFragments,
            LlvmFunctionFragmentRestorePlan = result.LlvmFunctionFragmentRestorePlan,
            LlvmFunctionFragmentRestoreResult = result.LlvmFunctionFragmentRestoreResult,
            LlvmModuleEnvelope = result.LlvmModuleEnvelope,
            LlvmCodegenUnitPlan = result.LlvmCodegenUnitPlan,
            LlvmObjectGroupRestorePlan = result.LlvmObjectGroupRestorePlan,
            MirFunctionFingerprintDiff = result.MirFunctionFingerprintDiff,
            LlvmFunctionFingerprintDiff = result.LlvmFunctionFingerprintDiff,
            MirFunctionWorklist = result.MirFunctionWorklist,
            LlvmFunctionWorklist = result.LlvmFunctionWorklist
        };
    }

    public static string FormatComparisonReport(
        CompilationResult currentResult,
        CompilationProfilingSnapshot baselineSnapshot,
        int topCount = DefaultTopPhaseCount)
    {
        var currentSnapshot = CreateSnapshot(currentResult);
        var sb = new StringBuilder();
        var phaseComparisons = BuildPhaseComparisons(currentSnapshot, baselineSnapshot);
        var subphaseComparisons = BuildSubphaseComparisons(currentSnapshot, baselineSnapshot);

        sb.AppendLine(PipelineMessages.ProfilingBaselineComparisonHeader);
        sb.AppendLine();
        sb.AppendLine(PipelineMessages.ProfilingCurrentInputLine(currentSnapshot.InputFile));
        sb.AppendLine(PipelineMessages.ProfilingBaselineInputLine(baselineSnapshot.InputFile));
        sb.AppendLine();

        if (phaseComparisons.Count == 0)
        {
            sb.AppendLine(PipelineMessages.ProfilingNoOverlappingPhaseData);
            return sb.ToString();
        }

        sb.AppendLine(PipelineMessages.ProfilingPhaseTimeRegressionHeader);
        sb.AppendLine();
        sb.AppendLine(PipelineMessages.ProfilingComparisonTimeTableHeader);
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var row in phaseComparisons.OrderByDescending(row => row.ElapsedDeltaMs).Take(Math.Max(1, topCount)))
        {
            sb.AppendLine(
                $"| {row.Key} | {row.CurrentElapsedMs:F2} | {row.BaselineElapsedMs:F2} | {FormatSignedNumber(row.ElapsedDeltaMs)} | {FormatSignedPercent(row.ElapsedDeltaPercent)} |");
        }

        sb.AppendLine();
        sb.AppendLine(PipelineMessages.ProfilingPhaseAllocationRegressionHeader);
        sb.AppendLine();
        sb.AppendLine(PipelineMessages.ProfilingComparisonAllocationTableHeader);
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var row in phaseComparisons.OrderByDescending(row => row.AllocatedDeltaBytes).Take(Math.Max(1, topCount)))
        {
            sb.AppendLine(
                $"| {row.Key} | {FormatBytes(row.CurrentAllocatedBytes)} | {FormatBytes(row.BaselineAllocatedBytes)} | {FormatSignedBytes(row.AllocatedDeltaBytes)} | {FormatSignedPercent(row.AllocatedDeltaPercent)} |");
        }

        if (subphaseComparisons.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(PipelineMessages.ProfilingSubphaseTimeRegressionHeader);
            sb.AppendLine();
            sb.AppendLine(PipelineMessages.ProfilingSubphaseComparisonTimeTableHeader);
            sb.AppendLine("| --- | ---: | ---: | ---: | ---: |");
            foreach (var row in subphaseComparisons.OrderByDescending(row => row.ElapsedDeltaMs).Take(Math.Max(1, topCount)))
            {
                sb.AppendLine(
                    $"| {EscapeMarkdown(row.Key)} | {row.CurrentElapsedMs:F2} | {row.BaselineElapsedMs:F2} | {FormatSignedNumber(row.ElapsedDeltaMs)} | {FormatSignedPercent(row.ElapsedDeltaPercent)} |");
            }

            sb.AppendLine();
            sb.AppendLine(PipelineMessages.ProfilingSubphaseAllocationRegressionHeader);
            sb.AppendLine();
            sb.AppendLine(PipelineMessages.ProfilingSubphaseComparisonAllocationTableHeader);
            sb.AppendLine("| --- | ---: | ---: | ---: | ---: |");
            foreach (var row in subphaseComparisons.OrderByDescending(row => row.AllocatedDeltaBytes).Take(Math.Max(1, topCount)))
            {
                sb.AppendLine(
                    $"| {EscapeMarkdown(row.Key)} | {FormatBytes(row.CurrentAllocatedBytes)} | {FormatBytes(row.BaselineAllocatedBytes)} | {FormatSignedBytes(row.AllocatedDeltaBytes)} | {FormatSignedPercent(row.AllocatedDeltaPercent)} |");
            }
        }

        AppendComparisonSummary(sb, phaseComparisons, subphaseComparisons);
        return sb.ToString();
    }

    public static CompilationProfilingSnapshot LoadSnapshot(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CompilationProfilingSnapshot>(json, CreateJsonOptions()) ??
               throw new InvalidOperationException(PipelineMessages.ProfilingSnapshotParseFailed(path));
    }

    private static string FormatMarkdown(CompilationResult result)
    {
        var sb = new StringBuilder();
        var phaseRows = BuildPhaseRows(result);
        var subphaseRows = BuildSubphaseRows(result);

        sb.AppendLine(PipelineMessages.ProfilingPhaseProfilingHeader);
        sb.AppendLine();
        sb.AppendLine(PipelineMessages.ProfilingPhaseProfilingTableHeader);
        sb.AppendLine("| --- | ---: | ---: | ---: | ---: |");
        foreach (var row in phaseRows)
        {
            sb.AppendLine(
                $"| {row.Phase} | {row.ElapsedMs:F2} | {row.TotalPercent:F2}% | {FormatBytes(row.AllocatedBytes)} | {row.AllocPercent:F2}% |");
        }

        sb.AppendLine();
        sb.AppendLine(PipelineMessages.ProfilingSubphaseProfilingHeader);
        sb.AppendLine();
        sb.AppendLine(PipelineMessages.ProfilingSubphaseProfilingTableHeader);
        sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var row in subphaseRows)
        {
            sb.AppendLine(
                $"| {row.Phase} | {EscapeMarkdown(row.Name)} | {row.ElapsedMs:F2} | {row.PhasePercent:F2}% | {row.TotalPercent:F2}% | {FormatBytes(row.AllocatedBytes)} | {FormatSignedBytes(row.ManagedBytesDelta)} | {row.Gen0Collections} | {row.Gen1Collections} | {row.Gen2Collections} |");
        }

        return sb.ToString();
    }

    private static void AppendPhaseHotspots(
        StringBuilder sb,
        string title,
        IEnumerable<PhaseRow> rows,
        int count,
        Func<PhaseRow, string> metricText,
        Func<PhaseRow, double> percentSelector)
    {
        sb.AppendLine($"### {title}");
        sb.AppendLine();
        sb.AppendLine(PipelineMessages.ProfilingHotspotPhaseTableHeader);
        sb.AppendLine("| --- | ---: | ---: |");
        foreach (var row in rows.Take(Math.Max(1, count)))
        {
            sb.AppendLine($"| {row.Phase} | {metricText(row)} | {percentSelector(row):F2}% |");
        }

        sb.AppendLine();
    }

    private static void AppendSubphaseHotspots(
        StringBuilder sb,
        string title,
        IEnumerable<SubphaseRow> rows,
        int count,
        Func<SubphaseRow, string> metricText,
        Func<SubphaseRow, double> percentSelector)
    {
        sb.AppendLine($"### {title}");
        sb.AppendLine();
        sb.AppendLine(PipelineMessages.ProfilingHotspotSubphaseTableHeader);
        sb.AppendLine("| --- | --- | ---: | ---: | ---: |");
        foreach (var row in rows.Take(Math.Max(1, count)))
        {
            sb.AppendLine(
                $"| {row.Phase} | {EscapeMarkdown(row.Name)} | {metricText(row)} | {row.PhasePercent:F2}% | {percentSelector(row):F2}% |");
        }

        sb.AppendLine();
    }

    private static void AppendOptimizationCandidates(
        StringBuilder sb,
        PhaseRow hottestPhaseByTime,
        PhaseRow hottestPhaseByAllocation,
        SubphaseRow? hottestSubphaseByTime,
        SubphaseRow? hottestSubphaseByAllocation)
    {
        sb.AppendLine(PipelineMessages.ProfilingOptimizationCandidatesHeader);
        sb.AppendLine();
        sb.AppendLine(PipelineMessages.ProfilingTimeHotspotPhaseLine(
            hottestPhaseByTime.Phase,
            hottestPhaseByTime.ElapsedMs,
            hottestPhaseByTime.TotalPercent));
        sb.AppendLine(PipelineMessages.ProfilingAllocationHotspotPhaseLine(
            hottestPhaseByAllocation.Phase,
            FormatBytes(hottestPhaseByAllocation.AllocatedBytes),
            hottestPhaseByAllocation.AllocPercent));

        if (hottestSubphaseByTime is { } subphaseByTime)
        {
            sb.AppendLine(PipelineMessages.ProfilingDeepestTimeHotspotLine(
                subphaseByTime.Phase,
                subphaseByTime.Name,
                subphaseByTime.ElapsedMs,
                subphaseByTime.PhasePercent,
                subphaseByTime.TotalPercent));
        }

        if (hottestSubphaseByAllocation is { } subphaseByAllocation)
        {
            sb.AppendLine(PipelineMessages.ProfilingDeepestAllocationHotspotLine(
                subphaseByAllocation.Phase,
                subphaseByAllocation.Name,
                FormatBytes(subphaseByAllocation.AllocatedBytes),
                subphaseByAllocation.PhasePercent,
                subphaseByAllocation.AllocPercent));
        }
    }

    private static string FormatDelimited(CompilationResult result, string delimiter)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(delimiter, [
            "Kind", "Phase", "Name", "ElapsedMs", "PhasePercent", "TotalPercent",
            "AllocatedBytes", "Allocated", "AllocPercent", "ManagedBytesBefore",
            "ManagedBytesAfter", "ManagedBytesDelta", "Gen0", "Gen1", "Gen2"
        ]));

        foreach (var row in BuildPhaseRows(result))
        {
            sb.AppendLine(string.Join(delimiter, [
                "phase",
                EscapeDelimited(row.Phase.ToString(), delimiter),
                "",
                row.ElapsedMs.ToString("F2"),
                "100.00",
                row.TotalPercent.ToString("F2"),
                row.AllocatedBytes.ToString(),
                EscapeDelimited(FormatBytes(row.AllocatedBytes), delimiter),
                row.AllocPercent.ToString("F2"),
                "",
                "",
                "",
                "",
                "",
                ""
            ]));
        }

        foreach (var row in BuildSubphaseRows(result))
        {
            sb.AppendLine(string.Join(delimiter, [
                "subphase",
                EscapeDelimited(row.Phase.ToString(), delimiter),
                EscapeDelimited(row.Name, delimiter),
                row.ElapsedMs.ToString("F2"),
                row.PhasePercent.ToString("F2"),
                row.TotalPercent.ToString("F2"),
                row.AllocatedBytes.ToString(),
                EscapeDelimited(FormatBytes(row.AllocatedBytes), delimiter),
                row.AllocPercent.ToString("F2"),
                row.ManagedBytesBefore.ToString(),
                row.ManagedBytesAfter.ToString(),
                row.ManagedBytesDelta.ToString(),
                row.Gen0Collections.ToString(),
                row.Gen1Collections.ToString(),
                row.Gen2Collections.ToString()
            ]));
        }

        var aggregateRows = BuildSubphaseAggregateRows(result)
            .Where(static row => row.Records > 1)
            .ToList();
        if (aggregateRows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("## Subphase Aggregates");
            sb.AppendLine();
            sb.AppendLine("| Phase | Name | Records | Time (ms) | Allocated | Gen0 | Gen1 | Gen2 |");
            sb.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
            foreach (var row in aggregateRows)
            {
                sb.AppendLine(
                    $"| {row.Phase} | {EscapeMarkdown(row.Name)} | {row.Records} | {row.ElapsedMs:F2} | {FormatBytes(row.AllocatedBytes)} | {row.Gen0Collections} | {row.Gen1Collections} | {row.Gen2Collections} |");
            }
        }

        return sb.ToString();
    }

    private static List<PhaseRow> BuildPhaseRows(CompilationResult result)
    {
        var totalMs = Math.Max(0.0001, result.PhaseTimes.Values.Sum(time => time.TotalMilliseconds));
        var totalAlloc = Math.Max(1L, result.PhaseAllocations.Values.Sum());

        return result.PhaseTimes
            .OrderBy(entry => entry.Key)
            .Select(entry =>
            {
                result.PhaseAllocations.TryGetValue(entry.Key, out var allocatedBytes);
                return new PhaseRow(
                    entry.Key,
                    entry.Value.TotalMilliseconds,
                    entry.Value.TotalMilliseconds / totalMs * 100.0,
                    allocatedBytes,
                    allocatedBytes / (double)totalAlloc * 100.0);
            })
            .ToList();
    }

    private static List<SubphaseRow> BuildSubphaseRows(CompilationResult result)
    {
        if (result.SubphaseMetrics.Count == 0)
        {
            return [];
        }

        var phaseTimesMs = result.PhaseTimes.ToDictionary(
            entry => entry.Key,
            entry => entry.Value.TotalMilliseconds);
        var totalMs = Math.Max(0.0001, result.PhaseTimes.Values.Sum(time => time.TotalMilliseconds));
        var totalAlloc = Math.Max(1L, result.SubphaseMetrics.Sum(metric => metric.AllocatedBytes));

        return result.SubphaseMetrics
            .OrderBy(metric => metric.Phase)
            .ThenByDescending(metric => metric.Elapsed)
            .Select(metric =>
            {
                phaseTimesMs.TryGetValue(metric.Phase, out var phaseMs);
                phaseMs = Math.Max(0.0001, phaseMs);
                return new SubphaseRow(
                    metric.Phase,
                    metric.Name,
                    metric.Elapsed.TotalMilliseconds,
                    metric.Elapsed.TotalMilliseconds / phaseMs * 100.0,
                    metric.Elapsed.TotalMilliseconds / totalMs * 100.0,
                    metric.AllocatedBytes,
                    metric.AllocatedBytes / (double)totalAlloc * 100.0,
                    metric.ManagedBytesBefore,
                    metric.ManagedBytesAfter,
                    metric.ManagedBytesDelta,
                    metric.Gen0Collections,
                    metric.Gen1Collections,
                    metric.Gen2Collections);
            })
            .ToList();
    }

    private static List<SubphaseAggregateRow> BuildSubphaseAggregateRows(CompilationResult result)
    {
        if (result.SubphaseMetrics.Count == 0)
        {
            return [];
        }

        return result.SubphaseMetrics
            .GroupBy(
                metric => new
                {
                    metric.Phase,
                    Name = NormalizeAggregateSubphaseName(metric.Phase, metric.Name)
                })
            .Select(group => new SubphaseAggregateRow(
                group.Key.Phase,
                group.Key.Name,
                group.Count(),
                group.Sum(metric => metric.Elapsed.TotalMilliseconds),
                group.Sum(metric => metric.AllocatedBytes),
                group.Sum(metric => metric.Gen0Collections),
                group.Sum(metric => metric.Gen1Collections),
                group.Sum(metric => metric.Gen2Collections)))
            .OrderBy(static row => row.Phase)
            .ThenByDescending(static row => row.ElapsedMs)
            .ToList();
    }

    private static string NormalizeAggregateSubphaseName(CompilationPhase phase, string name)
    {
        if (phase != CompilationPhase.Borrow ||
            !name.StartsWith("func:", StringComparison.Ordinal))
        {
            return name;
        }

        var separatorIndex = name.LastIndexOf('.');
        return separatorIndex < 0 || separatorIndex == name.Length - 1
            ? name
            : name[(separatorIndex + 1)..];
    }

    private static string EscapeMarkdown(string value)
    {
        return value.Replace(WellKnownStrings.Punctuation.Pipe, "\\|", StringComparison.Ordinal);
    }

    private static string EscapeDelimited(string value, string delimiter)
    {
        if (delimiter == "\t")
        {
            return value.Replace('\t', ' ');
        }

        if (value.Contains('"') || value.Contains(',') || value.Contains('\n') || value.Contains('\r'))
        {
            return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        }

        return value;
    }

    private static string FormatBytes(long bytes)
    {
        const long kib = 1024;
        const long mib = kib * 1024;
        const long gib = mib * 1024;

        return bytes switch
        {
            >= gib => $"{bytes / (double)gib:F2} GiB",
            >= mib => $"{bytes / (double)mib:F2} MiB",
            >= kib => $"{bytes / (double)kib:F2} KiB",
            _ => $"{bytes} B"
        };
    }

    private static string FormatSignedBytes(long bytes)
    {
        if (bytes == 0)
        {
            return "0 B";
        }

        var prefix = bytes > 0 ? WellKnownStrings.Operators.Add : WellKnownStrings.Operators.Subtract;
        return prefix + FormatBytes(Math.Abs(bytes));
    }

    private static string FormatSignedNumber(double value)
    {
        return value == 0 ? "0.00" : value.ToString("+0.00;-0.00", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string FormatSignedPercent(double value)
    {
        if (value == 0)
        {
            return "0.00%";
        }

        return value > 0
            ? $"+{value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}%"
            : $"{value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)}%";
    }

    private static List<MetricComparisonRow> BuildPhaseComparisons(
        CompilationProfilingSnapshot current,
        CompilationProfilingSnapshot baseline)
    {
        var baselineMap = baseline.Phases.ToDictionary(row => row.Phase, StringComparer.Ordinal);
        var comparisons = new List<MetricComparisonRow>();
        foreach (var row in current.Phases)
        {
            if (!baselineMap.TryGetValue(row.Phase, out var baselineRow))
            {
                continue;
            }

            comparisons.Add(CreateComparisonRow(
                row.Phase,
                row.ElapsedMs,
                baselineRow.ElapsedMs,
                row.AllocatedBytes,
                baselineRow.AllocatedBytes));
        }

        return comparisons;
    }

    private static List<MetricComparisonRow> BuildSubphaseComparisons(
        CompilationProfilingSnapshot current,
        CompilationProfilingSnapshot baseline)
    {
        var baselineMap = baseline.Subphases.ToDictionary(
            row => BuildSubphaseKey(row.Phase, row.Name),
            StringComparer.Ordinal);
        var comparisons = new List<MetricComparisonRow>();
        foreach (var row in current.Subphases)
        {
            var key = BuildSubphaseKey(row.Phase, row.Name);
            if (!baselineMap.TryGetValue(key, out var baselineRow))
            {
                continue;
            }

            comparisons.Add(CreateComparisonRow(
                key,
                row.ElapsedMs,
                baselineRow.ElapsedMs,
                row.AllocatedBytes,
                baselineRow.AllocatedBytes));
        }

        return comparisons;
    }

    private static MetricComparisonRow CreateComparisonRow(
        string key,
        double currentElapsedMs,
        double baselineElapsedMs,
        long currentAllocatedBytes,
        long baselineAllocatedBytes)
    {
        var elapsedDeltaMs = currentElapsedMs - baselineElapsedMs;
        var allocatedDeltaBytes = currentAllocatedBytes - baselineAllocatedBytes;
        return new MetricComparisonRow(
            key,
            currentElapsedMs,
            baselineElapsedMs,
            elapsedDeltaMs,
            ComputeDeltaPercent(currentElapsedMs, baselineElapsedMs),
            currentAllocatedBytes,
            baselineAllocatedBytes,
            allocatedDeltaBytes,
            ComputeDeltaPercent(currentAllocatedBytes, baselineAllocatedBytes));
    }

    private static double ComputeDeltaPercent(double current, double baseline)
    {
        if (baseline == 0)
        {
            return current == 0 ? 0 : 100;
        }

        return (current - baseline) / baseline * 100.0;
    }

    private static void AppendComparisonSummary(
        StringBuilder sb,
        IReadOnlyList<MetricComparisonRow> phaseComparisons,
        IReadOnlyList<MetricComparisonRow> subphaseComparisons)
    {
        var worstPhaseTime = phaseComparisons.OrderByDescending(row => row.ElapsedDeltaMs).FirstOrDefault();
        var worstPhaseAlloc = phaseComparisons.OrderByDescending(row => row.AllocatedDeltaBytes).FirstOrDefault();
        var bestPhaseTime = phaseComparisons.OrderBy(row => row.ElapsedDeltaMs).FirstOrDefault();

        sb.AppendLine();
        sb.AppendLine(PipelineMessages.ProfilingRegressionSummaryHeader);
        sb.AppendLine();

        if (worstPhaseTime is not null)
        {
            sb.AppendLine(PipelineMessages.ProfilingWorstPhaseTimeRegressionLine(
                worstPhaseTime.Key,
                FormatSignedNumber(worstPhaseTime.ElapsedDeltaMs),
                FormatSignedPercent(worstPhaseTime.ElapsedDeltaPercent)));
        }

        if (worstPhaseAlloc is not null)
        {
            sb.AppendLine(PipelineMessages.ProfilingWorstPhaseAllocationRegressionLine(
                worstPhaseAlloc.Key,
                FormatSignedBytes(worstPhaseAlloc.AllocatedDeltaBytes),
                FormatSignedPercent(worstPhaseAlloc.AllocatedDeltaPercent)));
        }

        if (bestPhaseTime is not null)
        {
            sb.AppendLine(PipelineMessages.ProfilingBestPhaseTimeImprovementLine(
                bestPhaseTime.Key,
                FormatSignedNumber(bestPhaseTime.ElapsedDeltaMs),
                FormatSignedPercent(bestPhaseTime.ElapsedDeltaPercent)));
        }

        if (subphaseComparisons.Count > 0)
        {
            var worstSubphaseTime = subphaseComparisons.OrderByDescending(row => row.ElapsedDeltaMs).First();
            sb.AppendLine(PipelineMessages.ProfilingWorstSubphaseTimeRegressionLine(
                worstSubphaseTime.Key,
                FormatSignedNumber(worstSubphaseTime.ElapsedDeltaMs),
                FormatSignedPercent(worstSubphaseTime.ElapsedDeltaPercent)));
        }
    }

    private static string BuildSubphaseKey(string phase, string name)
    {
        return $"{phase}.{name}";
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }

    private readonly record struct PhaseRow(
        CompilationPhase Phase,
        double ElapsedMs,
        double TotalPercent,
        long AllocatedBytes,
        double AllocPercent);

    private readonly record struct SubphaseRow(
        CompilationPhase Phase,
        string Name,
        double ElapsedMs,
        double PhasePercent,
        double TotalPercent,
        long AllocatedBytes,
        double AllocPercent,
        long ManagedBytesBefore,
        long ManagedBytesAfter,
        long ManagedBytesDelta,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections);

    private readonly record struct SubphaseAggregateRow(
        CompilationPhase Phase,
        string Name,
        int Records,
        double ElapsedMs,
        long AllocatedBytes,
        int Gen0Collections,
        int Gen1Collections,
        int Gen2Collections);

    private sealed record MetricComparisonRow(
        string Key,
        double CurrentElapsedMs,
        double BaselineElapsedMs,
        double ElapsedDeltaMs,
        double ElapsedDeltaPercent,
        long CurrentAllocatedBytes,
        long BaselineAllocatedBytes,
        long AllocatedDeltaBytes,
        double AllocatedDeltaPercent);
}
