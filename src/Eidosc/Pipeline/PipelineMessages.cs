using Eidosc.ProjectSystem;
using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Eidosc.Pipeline;

internal static class PipelineMessages
{
    private static readonly ResourceManager Resources = new(
        "Eidosc.Pipeline.PipelineResources",
        Assembly.GetExecutingAssembly());

    private static string Get(string name) =>
        Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Format(string name, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(name), args);

    public static string AbilitiesHeader => Get(nameof(AbilitiesHeader));

    public static string EffectRequirementsColumn => Get(nameof(EffectRequirementsColumn));

    public static string ActiveBorrowsHeader => Get(nameof(ActiveBorrowsHeader));

    public static string AffineTypeErrorsHeader => Get(nameof(AffineTypeErrorsHeader));

    public static string AstHeader => Get(nameof(AstHeader));

    public static string BasicBlockLiveVariablesHeader => Get(nameof(BasicBlockLiveVariablesHeader));

    public static string BlockIdColumn => Get(nameof(BlockIdColumn));

    public static string BorrowAliasStatesHeader => Get(nameof(BorrowAliasStatesHeader));

    public static string BorrowCheckErrorsTitle => Get(nameof(BorrowCheckErrorsTitle));

    public static string BorroweeColumn => Get(nameof(BorroweeColumn));

    public static string BorrowerColumn => Get(nameof(BorrowerColumn));

    public static string CompilationSummaryHeader => Get(nameof(CompilationSummaryHeader));

    public static string CreatedColumn => Get(nameof(CreatedColumn));

    public static string CstHeader => Get(nameof(CstHeader));

    public static string DefinitionPointColumn => Get(nameof(DefinitionPointColumn));

    public static string EndedColumn => Get(nameof(EndedColumn));

    public static string ExternalDeclarationsHeader => Get(nameof(ExternalDeclarationsHeader));

    public static string FunctionEffectRequirementsHeader => Get(nameof(FunctionEffectRequirementsHeader));

    public static string FunctionNameColumn => Get(nameof(FunctionNameColumn));

    public static string FunctionSignaturesHeader => Get(nameof(FunctionSignaturesHeader));

    public static string HirHeader => Get(nameof(HirHeader));

    public static string InferredTypesHeader => Get(nameof(InferredTypesHeader));

    public static string LiveBlockCountColumn => Get(nameof(LiveBlockCountColumn));

    public static string LivenessHeader => Get(nameof(LivenessHeader));

    public static string LlvmIrTextHeader => Get(nameof(LlvmIrTextHeader));

    public static string LlvmModuleHeader => Get(nameof(LlvmModuleHeader));

    public static string LoanConstraintErrorsHeader => Get(nameof(LoanConstraintErrorsHeader));

    public static string LoanConstraintStatesHeader => Get(nameof(LoanConstraintStatesHeader));

    public static string LoanSignatureHeader => Get(nameof(LoanSignatureHeader));

    public static string LoanSignatureInferenceErrorsTitle => Get(nameof(LoanSignatureInferenceErrorsTitle));

    public static string MirHeader => Get(nameof(MirHeader));

    public static string MirOptimizationSummaryHeader => Get(nameof(MirOptimizationSummaryHeader));

    public static string MutableColumn => Get(nameof(MutableColumn));

    public static string NameColumn => Get(nameof(NameColumn));

    public static string No => Get(nameof(No));

    public static string NoEffectRequirements => Get(nameof(NoEffectRequirements));

    public static string NoActiveAlias => Get(nameof(NoActiveAlias));

    public static string NoActiveBorrows => Get(nameof(NoActiveBorrows));

    public static string NoAffineTypeErrors => Get(nameof(NoAffineTypeErrors));

    public static string NoAliasStates => Get(nameof(NoAliasStates));

    public static string NoBorrowErrors => Get(nameof(NoBorrowErrors));

    public static string NoLoanConstraintErrors => Get(nameof(NoLoanConstraintErrors));

    public static string NoLoanConstraintStates => Get(nameof(NoLoanConstraintStates));

    public static string NoneItem => Get(nameof(NoneItem));

    public static string NoSubstitutionBindings => Get(nameof(NoSubstitutionBindings));

    public static string OmittableDropHeader => Get(nameof(OmittableDropHeader));

    public static string OmittableDupHeader => Get(nameof(OmittableDupHeader));

    public static string ParameterCountColumn => Get(nameof(ParameterCountColumn));

    public static string ParameterRequirementsHeader => Get(nameof(ParameterRequirementsHeader));

    public static string PerceusHintsHeader => Get(nameof(PerceusHintsHeader));

    public static string PhaseTimingsHeader => Get(nameof(PhaseTimingsHeader));

    public static string PureEffect => Get(nameof(PureEffect));

    public static string ReturnConstraintOwn => Get(nameof(ReturnConstraintOwn));

    public static string ReturnTypeColumn => Get(nameof(ReturnTypeColumn));

    public static string ScopesHeader => Get(nameof(ScopesHeader));

    public static string SourceColumn => Get(nameof(SourceColumn));

    public static string StatusColumn => Get(nameof(StatusColumn));

    public static string StatusFailed => Get(nameof(StatusFailed));

    public static string StatusSuccess => Get(nameof(StatusSuccess));

    public static string SubphaseTimingsHeader => Get(nameof(SubphaseTimingsHeader));

    public static string SubstitutionHeader => Get(nameof(SubstitutionHeader));

    public static string SymbolsHeader => Get(nameof(SymbolsHeader));

    public static string TokenEof => Get(nameof(TokenEof));

    public static string TokenListHeader => Get(nameof(TokenListHeader));

    public static string TypeVariableColumn => Get(nameof(TypeVariableColumn));

    public static string VariableColumn => Get(nameof(VariableColumn));

    public static string VariableLiveRangesHeader => Get(nameof(VariableLiveRangesHeader));

    public static string VariableStateSimplifiedNote => Get(nameof(VariableStateSimplifiedNote));

    public static string VariableStateTrackingHeader => Get(nameof(VariableStateTrackingHeader));

    public static string Yes => Get(nameof(Yes));

    public static string AffineIssueCount(int count) =>
        Format(nameof(AffineIssueCount), count);

    public static string BindingCount(int count) =>
        Format(nameof(BindingCount), count);

    public static string CompletedPhase(object phase) =>
        Format(nameof(CompletedPhase), phase);

    public static string DeclarationCount(int count) =>
        Format(nameof(DeclarationCount), count);

    public static string ErrorLine(string message) =>
        Format(nameof(ErrorLine), message);

    public static string ErrorsWarnings(int errors, int warnings) =>
        Format(nameof(ErrorsWarnings), errors, warnings);

    public static string ExternalDeclarationCount(int count) =>
        Format(nameof(ExternalDeclarationCount), count);

    public static string FunctionCount(int count) =>
        Format(nameof(FunctionCount), count);

    public static string GlobalVariableCount(int count) =>
        Format(nameof(GlobalVariableCount), count);

    public static string IndentedHintLine(string hint) =>
        Format(nameof(IndentedHintLine), hint);

    public static string IndentedLocationLine(int block, int index) =>
        Format(nameof(IndentedLocationLine), block, index);

    public static string IndentedRelatedLocationLine(int block, int index) =>
        Format(nameof(IndentedRelatedLocationLine), block, index);

    public static string IndentedTypeLine(object type) =>
        Format(nameof(IndentedTypeLine), type);

    public static string IndentedVariableLine(int variable) =>
        Format(nameof(IndentedVariableLine), variable);

    public static string ModuleLine(string module) =>
        Format(nameof(ModuleLine), module);

    public static string ModuleNameLine(string module) =>
        Format(nameof(ModuleNameLine), module);

    public static string PathLine(string path) =>
        Format(nameof(PathLine), path);

    public static string ReturnConstraint(object constraint) =>
        Format(nameof(ReturnConstraint), constraint);

    public static string SummaryStatus(string status) =>
        Format(nameof(SummaryStatus), status);

    public static string SymbolCount(int count) =>
        Format(nameof(SymbolCount), count);

    public static string TokenComment(string comment) =>
        Format(nameof(TokenComment), comment);

    public static string TokenError(string message) =>
        Format(nameof(TokenError), message);

    public static string TotalCount(int count) =>
        Format(nameof(TotalCount), count);

    public static string TotalTimeMs(double milliseconds) =>
        Format(nameof(TotalTimeMs), milliseconds);

    public static string GitNotInstalled => Get(nameof(GitNotInstalled));

    public static string FailedToStartGitProcess => Get(nameof(FailedToStartGitProcess));

    public static string FailedToDeserializeLockFile => Get(nameof(FailedToDeserializeLockFile));

    public static string VersionStringEmpty => Get(nameof(VersionStringEmpty));

    public static string VersionRangeSpecEmpty => Get(nameof(VersionRangeSpecEmpty));

    public static string GitCloneFailed(string url) => Format(nameof(GitCloneFailed), url);

    public static string GitCommandFailed(string arguments, int exitCode, string stderr) =>
        Format(nameof(GitCommandFailed), arguments, exitCode, stderr);

    public static string FailedToLoadProjectConfig(string filePath, string message) =>
        Format(nameof(FailedToLoadProjectConfig), filePath, message);

    public static string FailedToResolveDependency(string name, string message) =>
        Format(nameof(FailedToResolveDependency), name, message);

    public static string UnknownDependencySource(string name) =>
        Format(nameof(UnknownDependencySource), name);

    public static string PathDependencyMissingPath(string name) =>
        Format(nameof(PathDependencyMissingPath), name);

    public static string PathDependencyDirectoryNotFound(string path) =>
        Format(nameof(PathDependencyDirectoryNotFound), path);

    public static string GitDependencyMissingUrl(string name) =>
        Format(nameof(GitDependencyMissingUrl), name);

    public static string VersionDependencyRequiresRegistry(string name) =>
        Format(nameof(VersionDependencyRequiresRegistry), name);

    public static string InvalidVersionFormat(string input) =>
        Format(nameof(InvalidVersionFormat), input);

    public static string InvalidCompoundVersionRange(string spec) =>
        Format(nameof(InvalidCompoundVersionRange), spec);

    public static string MajorVersionMustBeNonNegative => Get(nameof(MajorVersionMustBeNonNegative));

    public static string MinorVersionMustBeNonNegative => Get(nameof(MinorVersionMustBeNonNegative));

    public static string PatchVersionMustBeNonNegative => Get(nameof(PatchVersionMustBeNonNegative));

    public static string ProjectDoesNotDefineTarget(string projectPath, string targetName, string availableTargets) =>
        Format(nameof(ProjectDoesNotDefineTarget), projectPath, targetName, availableTargets);

    public static string ProjectConfigNotFound(string projectPath) =>
        Format(nameof(ProjectConfigNotFound), projectPath);

    public static string LockFileNotFound(string filePath) =>
        Format(nameof(LockFileNotFound), filePath);

    public static string DirectoryNotFound(string directory) =>
        Format(nameof(DirectoryNotFound), directory);

    public static string ProjectBuildGraphDependencyCycleDetected(string cycle) =>
        Format(nameof(ProjectBuildGraphDependencyCycleDetected), cycle);

    public static string TargetDependsOnMissingTarget(string targetName, string projectPath, string dependencyName) =>
        Format(nameof(TargetDependsOnMissingTarget), targetName, projectPath, dependencyName);

    public static string TargetDependsOnMissingProjectDependency(
        string targetName,
        string projectPath,
        string dependencyName) =>
        Format(nameof(TargetDependsOnMissingProjectDependency), targetName, projectPath, dependencyName);

    public static string ProjectDependencyRequiresPath(string dependencyName, string projectPath) =>
        Format(nameof(ProjectDependencyRequiresPath), dependencyName, projectPath);

    public static string ProjectContainsTargetWithEmptyName(string projectPath) =>
        Format(nameof(ProjectContainsTargetWithEmptyName), projectPath);

    public static string ProjectDeclaresDuplicateTarget(string projectPath, string targetName) =>
        Format(nameof(ProjectDeclaresDuplicateTarget), projectPath, targetName);

    public static string ProjectContainsDependencyWithEmptyName(string projectPath) =>
        Format(nameof(ProjectContainsDependencyWithEmptyName), projectPath);

    public static string ProjectDeclaresDuplicateDependency(string projectPath, string dependencyName) =>
        Format(nameof(ProjectDeclaresDuplicateDependency), projectPath, dependencyName);

    public static string ProjectDefaultTargetMissing(string projectPath, string targetName) =>
        Format(nameof(ProjectDefaultTargetMissing), projectPath, targetName);

    public static string ProjectDeclaresNoTargets(string projectPath) =>
        Format(nameof(ProjectDeclaresNoTargets), projectPath);

    public static string ProjectDeclaresMultipleTargets(string projectPath, string availableTargets) =>
        Format(nameof(ProjectDeclaresMultipleTargets), projectPath, availableTargets);

    public static string TargetRequiresEntryFile(string targetName, string projectPath) =>
        Format(nameof(TargetRequiresEntryFile), targetName, projectPath);

    public static string TargetReferencesMissingEntryFile(string targetName, string projectPath, string entryFile) =>
        Format(nameof(TargetReferencesMissingEntryFile), targetName, projectPath, entryFile);

    public static string TargetDeclaresUnsupportedKind(string targetName, string projectPath, string? kind) =>
        Format(nameof(TargetDeclaresUnsupportedKind), targetName, projectPath, kind ?? string.Empty);

    public static string TargetDeclaresDuplicateReference(
        string targetName,
        string projectPath,
        string referenceKind,
        string referenceName) =>
        Format(nameof(TargetDeclaresDuplicateReference), targetName, projectPath, referenceKind, referenceName);

    public static string ProfilingSnapshotParseFailed(string path) =>
        Format(nameof(ProfilingSnapshotParseFailed), path);

    public static string ProfilingHotspotSummaryHeader => Get(nameof(ProfilingHotspotSummaryHeader));

    public static string ProfilingNoPhaseDataRecorded => Get(nameof(ProfilingNoPhaseDataRecorded));

    public static string ProfilingTopPhasesByTime => Get(nameof(ProfilingTopPhasesByTime));

    public static string ProfilingTopPhasesByAllocation => Get(nameof(ProfilingTopPhasesByAllocation));

    public static string ProfilingTopSubphasesByTime => Get(nameof(ProfilingTopSubphasesByTime));

    public static string ProfilingTopSubphasesByAllocation => Get(nameof(ProfilingTopSubphasesByAllocation));

    public static string ProfilingBaselineComparisonHeader => Get(nameof(ProfilingBaselineComparisonHeader));

    public static string ProfilingCurrentInputLine(string inputFile) =>
        Format(nameof(ProfilingCurrentInputLine), inputFile);

    public static string ProfilingBaselineInputLine(string inputFile) =>
        Format(nameof(ProfilingBaselineInputLine), inputFile);

    public static string ProfilingNoOverlappingPhaseData => Get(nameof(ProfilingNoOverlappingPhaseData));

    public static string ProfilingPhaseTimeRegressionHeader => Get(nameof(ProfilingPhaseTimeRegressionHeader));

    public static string ProfilingPhaseAllocationRegressionHeader =>
        Get(nameof(ProfilingPhaseAllocationRegressionHeader));

    public static string ProfilingSubphaseTimeRegressionHeader =>
        Get(nameof(ProfilingSubphaseTimeRegressionHeader));

    public static string ProfilingSubphaseAllocationRegressionHeader =>
        Get(nameof(ProfilingSubphaseAllocationRegressionHeader));

    public static string ProfilingComparisonTimeTableHeader => Get(nameof(ProfilingComparisonTimeTableHeader));

    public static string ProfilingComparisonAllocationTableHeader =>
        Get(nameof(ProfilingComparisonAllocationTableHeader));

    public static string ProfilingSubphaseComparisonTimeTableHeader =>
        Get(nameof(ProfilingSubphaseComparisonTimeTableHeader));

    public static string ProfilingSubphaseComparisonAllocationTableHeader =>
        Get(nameof(ProfilingSubphaseComparisonAllocationTableHeader));

    public static string ProfilingPhaseProfilingHeader => Get(nameof(ProfilingPhaseProfilingHeader));

    public static string ProfilingPhaseProfilingTableHeader => Get(nameof(ProfilingPhaseProfilingTableHeader));

    public static string ProfilingSubphaseProfilingHeader => Get(nameof(ProfilingSubphaseProfilingHeader));

    public static string ProfilingSubphaseProfilingTableHeader => Get(nameof(ProfilingSubphaseProfilingTableHeader));

    public static string ProfilingHotspotPhaseTableHeader => Get(nameof(ProfilingHotspotPhaseTableHeader));

    public static string ProfilingHotspotSubphaseTableHeader => Get(nameof(ProfilingHotspotSubphaseTableHeader));

    public static string ProfilingOptimizationCandidatesHeader =>
        Get(nameof(ProfilingOptimizationCandidatesHeader));

    public static string ProfilingTimeHotspotPhaseLine(object phase, double elapsedMs, double totalPercent) =>
        Format(nameof(ProfilingTimeHotspotPhaseLine), phase, elapsedMs, totalPercent);

    public static string ProfilingAllocationHotspotPhaseLine(object phase, string allocatedBytes, double allocPercent) =>
        Format(nameof(ProfilingAllocationHotspotPhaseLine), phase, allocatedBytes, allocPercent);

    public static string ProfilingDeepestTimeHotspotLine(
        object phase,
        string name,
        double elapsedMs,
        double phasePercent,
        double totalPercent) =>
        Format(nameof(ProfilingDeepestTimeHotspotLine), phase, name, elapsedMs, phasePercent, totalPercent);

    public static string ProfilingDeepestAllocationHotspotLine(
        object phase,
        string name,
        string allocatedBytes,
        double phasePercent,
        double allocPercent) =>
        Format(nameof(ProfilingDeepestAllocationHotspotLine), phase, name, allocatedBytes, phasePercent, allocPercent);

    public static string ProfilingRegressionSummaryHeader => Get(nameof(ProfilingRegressionSummaryHeader));

    public static string ProfilingWorstPhaseTimeRegressionLine(string key, string deltaMs, string deltaPercent) =>
        Format(nameof(ProfilingWorstPhaseTimeRegressionLine), key, deltaMs, deltaPercent);

    public static string ProfilingWorstPhaseAllocationRegressionLine(string key, string deltaBytes, string deltaPercent) =>
        Format(nameof(ProfilingWorstPhaseAllocationRegressionLine), key, deltaBytes, deltaPercent);

    public static string ProfilingBestPhaseTimeImprovementLine(string key, string deltaMs, string deltaPercent) =>
        Format(nameof(ProfilingBestPhaseTimeImprovementLine), key, deltaMs, deltaPercent);

    public static string ProfilingWorstSubphaseTimeRegressionLine(string key, string deltaMs, string deltaPercent) =>
        Format(nameof(ProfilingWorstSubphaseTimeRegressionLine), key, deltaMs, deltaPercent);
}
