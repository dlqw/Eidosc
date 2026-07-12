using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Borrow;
using Eidosc.Doc;
using Eidosc.Hir;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Pipeline;

public sealed class CompilationResult
{
    public bool Success { get; init; }
    public CompilationPhase CompletedPhase { get; init; }
    public IReadOnlyList<Diagnostic.Diagnostic> Diagnostics { get; init; } = [];
    public string InputFile { get; init; } = "";
    public IReadOnlyList<string> ImportSearchRoots { get; init; } = [];
    public bool NoImplicitPrelude { get; init; }
    public string SourceText { get; init; } = "";
    public IReadOnlyList<Token>? Tokens { get; init; }
    public ConcreteSyntaxTree? CstRoot { get; init; }
    public ModuleDecl? Ast { get; init; }
    public SymbolTable? SymbolTable { get; init; }
    public TypeInferer? TypeInferer { get; init; }
    public bool TypeAnalysisIncomplete { get; init; }
    public string? TypeAnalysisIncompleteReason { get; init; }
    public int TypeErrorLimit { get; init; }
    public int SuppressedTypeDiagnosticCount { get; init; }
    public int SuppressedTypeConstraintCount { get; init; }
    public EffectInferer? EffectInferer { get; init; }
    public HirModule? HirModule { get; init; }
    public MirModule? MirModule { get; init; }
    public ModuleBorrowCheckResult? BorrowCheckResult { get; init; }
    public LlvmModule? LlvmModule { get; init; }
    public string? LlvmIrText { get; init; }
    public MirFunctionFingerprintSnapshot? MirFunctionFingerprints { get; init; }
    public LlvmFunctionFingerprintSnapshot? LlvmFunctionFingerprints { get; init; }
    public LlvmFunctionFragmentSnapshot? LlvmFunctionFragments { get; init; }
    public LlvmFunctionFragmentRestorePlanSnapshot? LlvmFunctionFragmentRestorePlan { get; init; }
    public LlvmFunctionFragmentRestoreResultSnapshot? LlvmFunctionFragmentRestoreResult { get; init; }
    public LlvmModuleEnvelopeSnapshot? LlvmModuleEnvelope { get; init; }
    public LlvmCodegenUnitPlanSnapshot? LlvmCodegenUnitPlan { get; init; }
    public LlvmObjectGroupRestorePlanSnapshot? LlvmObjectGroupRestorePlan { get; init; }
    public FunctionFingerprintDiffSnapshot? MirFunctionFingerprintDiff { get; init; }
    public FunctionFingerprintDiffSnapshot? LlvmFunctionFingerprintDiff { get; init; }
    public FunctionWorklistSnapshot? MirFunctionWorklist { get; init; }
    public FunctionWorklistSnapshot? LlvmFunctionWorklist { get; init; }
    public IReadOnlyDictionary<int, DocComment>? Documentation { get; init; }
    public ProjectModuleGraphSnapshot? ModuleGraphSnapshot { get; init; }
    public ProjectModuleBuildSchedule? ModuleBuildSchedule { get; init; }
    public ProjectModuleSignatureSnapshot? ModuleSignatureSnapshot { get; init; }
    public ProjectModuleSemanticSignatureSnapshot? ModuleSemanticSignatureSnapshot { get; init; }
    public ProjectModuleTypedSemanticSnapshot? ModuleTypedSemanticSnapshot { get; init; }
    public ProjectModuleMirArtifactSnapshot? ModuleMirArtifactSnapshot { get; init; }
    public CompilationLiveStatePayload? CompilationLiveStatePayload { get; init; }
    public ProjectModuleDependencySignatureSnapshot? ModuleDependencySignatureSnapshot { get; init; }
    public ProjectModuleMemberIndexSnapshot? ModuleMemberIndexSnapshot { get; init; }
    public IReadOnlyList<ModuleNamerStatePayload>? ModuleNamerStatePayloads { get; init; }
    public IReadOnlyList<ModuleTypesStatePayload>? ModuleTypesStatePayloads { get; init; }
    public IReadOnlyList<ModuleHirStateArtifactPayload>? ModuleHirStatePayloads { get; init; }
    public IReadOnlyList<ModuleMirStateArtifactPayload>? ModuleMirStatePayloads { get; init; }
    public ProjectModuleMemberIndexRestorePlan? ModuleMemberIndexRestorePlan { get; init; }
    public ProjectModuleMemberIndexRestorePayloadSnapshot? ModuleMemberIndexRestorePayload { get; init; }
    public ProjectModuleInvalidationPlan? ModuleInvalidationPlan { get; init; }
    public ProjectModuleInvalidationPlan? ModuleTypedInvalidationPlan { get; init; }
    public ProjectModuleExecutionPlan? ModuleExecutionPlan { get; init; }
    public ProjectModuleExecutionPlan? ModuleTypedExecutionPlan { get; init; }
    public ProjectModuleParallelExecutionSnapshot? ModuleParallelExecution { get; init; }
    public ProjectModuleParallelExecutionSnapshot? ModuleTypedParallelExecution { get; init; }
    public ProjectModuleArtifactReadinessPlan? ModuleArtifactReadinessPlan { get; init; }
    public ProjectModuleArtifactReadinessPlan? ModuleTypedArtifactReadinessPlan { get; init; }
    public ProjectModuleArtifactRestorePlan? ModuleArtifactRestorePlan { get; init; }
    public ProjectModuleArtifactRestorePlan? ModuleTypedArtifactRestorePlan { get; init; }
    public ProjectModuleArtifactRestoreExecutionSnapshot? ModuleArtifactRestoreExecution { get; init; }
    public ProjectModuleArtifactRestoreExecutionSnapshot? ModuleTypedArtifactRestoreExecution { get; init; }
    public ProjectModuleArtifactRestorePayloadSnapshot? ModuleArtifactRestorePayload { get; init; }
    public ProjectModuleArtifactRestorePayloadSnapshot? ModuleTypedArtifactRestorePayload { get; init; }
    public ImplOverlapCheckSnapshot? ImplOverlapCheckSnapshot { get; init; }
    public TypeDirectedCallableResolutionSnapshot? TypeDirectedCallableResolutionSnapshot { get; init; }
    public AssociatedTypeProjectionSnapshot? AssociatedTypeProjectionSnapshot { get; init; }
    public AssociatedConstProjectionSnapshot? AssociatedConstProjectionSnapshot { get; init; }
    public TraitCheckSnapshot? TraitCheckSnapshot { get; init; }
    public SendAnalysisSnapshot? SendAnalysisSnapshot { get; init; }
    public BorrowDiagnosticSnapshot? BorrowDiagnosticSnapshot { get; init; }
    public BorrowCodegenHintsSnapshot? BorrowCodegenHintsSnapshot { get; init; }

    public IReadOnlyDictionary<FuncDef, FunctionEffectSummary>? FunctionEffectSummaries =>
        EffectInferer?.FunctionSummaries;

    public TimeSpan TotalTime { get; init; }
    public IReadOnlyDictionary<CompilationPhase, TimeSpan> PhaseTimes { get; init; } = new Dictionary<CompilationPhase, TimeSpan>();
    public IReadOnlyDictionary<CompilationPhase, long> PhaseAllocations { get; init; } = new Dictionary<CompilationPhase, long>();
    public IReadOnlyList<CompilationSubphaseMetrics> SubphaseMetrics { get; init; } = [];
    public IReadOnlyDictionary<string, long> ProfilingCounters { get; init; } = new Dictionary<string, long>();

    public int ErrorCount => Diagnostics.Count(d => d.Level == Diagnostic.DiagnosticLevel.Error);
    public int WarningCount => Diagnostics.Count(d => d.Level == Diagnostic.DiagnosticLevel.Warning);
}
