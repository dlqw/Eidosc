using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Eidosc.Cli.Resources;

internal static class CliMessages
{
    private static readonly ResourceManager Resources = new(
        "Eidosc.Cli.Resources.CliResources",
        Assembly.GetExecutingAssembly());

    public static string LspFullDocumentSyncExpected => Get(nameof(LspFullDocumentSyncExpected));

    public static string LspInferredTypeTooltip => Get(nameof(LspInferredTypeTooltip));

    public static string IdeCheckingAction => Get(nameof(IdeCheckingAction));

    public static string PhaseNone => Get(nameof(PhaseNone));

    public static string CliRootDescription => Get(nameof(CliRootDescription));

    public static string CliVerboseOptionDescription => Get(nameof(CliVerboseOptionDescription));

    public static string CliNoColorOptionDescription => Get(nameof(CliNoColorOptionDescription));

    public static string CliImportRootOptionDescription => Get(nameof(CliImportRootOptionDescription));

    public static string SourceArgumentDescription => Get(nameof(SourceArgumentDescription));

    public static string SourceTextOptionDescription => Get(nameof(SourceTextOptionDescription));

    public static string SourceStdinOptionDescription => Get(nameof(SourceStdinOptionDescription));

    public static string SourceTextAndStdinMutuallyExclusive =>
        Get(nameof(SourceTextAndStdinMutuallyExclusive));

    public static string ProjectOptionDescription => Get(nameof(ProjectOptionDescription));

    public static string DebugLevelOptionDescription => Get(nameof(DebugLevelOptionDescription));

    public static string WerrorOptionDescription => Get(nameof(WerrorOptionDescription));

    public static string WerrorAllOptionDescription => Get(nameof(WerrorAllOptionDescription));

    public static string InputResolutionFailedDetail => Get(nameof(InputResolutionFailedDetail));

    public static string SourceFileMissingDetail => Get(nameof(SourceFileMissingDetail));

    public static string WarningAsErrorAllStatus => Get(nameof(WarningAsErrorAllStatus));

    public static string NativeCpuTuningStatus => Get(nameof(NativeCpuTuningStatus));

    public static string LtoEnabledStatus => Get(nameof(LtoEnabledStatus));

    public static string BuildNativeLinkModeOptionDescription => Get(nameof(BuildNativeLinkModeOptionDescription));

    public static string BuildNativeLinkModeOptionHelp => Get(nameof(BuildNativeLinkModeOptionHelp));

    public static string InvalidNativeLinkMode(string value) => Format(nameof(InvalidNativeLinkMode), value);

    public static string InvalidNativeLinkModeDetail => Get(nameof(InvalidNativeLinkModeDetail));

    public static string IdeCommandDescription => Get(nameof(IdeCommandDescription));

    public static string IdeSourceArgumentDescription => Get(nameof(IdeSourceArgumentDescription));

    public static string IdeProjectOptionDescription => Get(nameof(IdeProjectOptionDescription));

    public static string IdeTargetNameOptionDescription => Get(nameof(IdeTargetNameOptionDescription));

    public static string IdeStdinOptionDescription => Get(nameof(IdeStdinOptionDescription));

    public static string IdePhaseOptionDescription => Get(nameof(IdePhaseOptionDescription));

    public static string IdePrettyOptionDescription => Get(nameof(IdePrettyOptionDescription));

    public static string IdeSnapshotFailedDetail => Get(nameof(IdeSnapshotFailedDetail));

    public static string ReplCommandDescription => Get(nameof(ReplCommandDescription));

    public static string ReplProjectOptionDescription => Get(nameof(ReplProjectOptionDescription));

    public static string ReplSessionAction => Get(nameof(ReplSessionAction));

    public static string ReplBreakOutsideLoopError => Get(nameof(ReplBreakOutsideLoopError));

    public static string ReplNoHirError => Get(nameof(ReplNoHirError));

    public static string ReplEnvironmentEmpty => Get(nameof(ReplEnvironmentEmpty));

    public static string ReplEnvironmentCleared => Get(nameof(ReplEnvironmentCleared));

    public static string ReplCouldNotDetermineType => Get(nameof(ReplCouldNotDetermineType));

    public static string ReplTitle => Get(nameof(ReplTitle));

    public static string ReplWelcomeHelp => Get(nameof(ReplWelcomeHelp));

    public static string ReplTypeUsage => Get(nameof(ReplTypeUsage));

    public static string ReplLoadUsage => Get(nameof(ReplLoadUsage));

    public static string ReplHelpAvailableCommands => Get(nameof(ReplHelpAvailableCommands));

    public static string ReplHelpShowHelp => Get(nameof(ReplHelpShowHelp));

    public static string ReplHelpExit => Get(nameof(ReplHelpExit));

    public static string ReplHelpType => Get(nameof(ReplHelpType));

    public static string ReplHelpLoad => Get(nameof(ReplHelpLoad));

    public static string ReplHelpEnv => Get(nameof(ReplHelpEnv));

    public static string ReplHelpClear => Get(nameof(ReplHelpClear));

    public static string ReplHelpEvaluate => Get(nameof(ReplHelpEvaluate));

    public static string AnalyzeCommandDescription => Get(nameof(AnalyzeCommandDescription));

    public static string AnalyzeTargetNameOptionDescription => Get(nameof(AnalyzeTargetNameOptionDescription));

    public static string AnalyzePhaseOptionDescription => Get(nameof(AnalyzePhaseOptionDescription));

    public static string AnalyzeProfileOptionDescription => Get(nameof(AnalyzeProfileOptionDescription));

    public static string AnalyzeProfileFormatOptionDescription => Get(nameof(AnalyzeProfileFormatOptionDescription));

    public static string AnalyzeProfileOutputOptionDescription => Get(nameof(AnalyzeProfileOutputOptionDescription));

    public static string AnalyzeProfileSnapshotOutputOptionDescription =>
        Get(nameof(AnalyzeProfileSnapshotOutputOptionDescription));

    public static string AnalyzeProfileBaselineOptionDescription =>
        Get(nameof(AnalyzeProfileBaselineOptionDescription));

    public static string AnalyzeTokenListHeader => Get(nameof(AnalyzeTokenListHeader));

    public static string AnalyzeAstStructureHeader => Get(nameof(AnalyzeAstStructureHeader));

    public static string AnalyzeTypeInferenceHeader => Get(nameof(AnalyzeTypeInferenceHeader));

    public static string AnalyzeTypeInferenceCompleted => Get(nameof(AnalyzeTypeInferenceCompleted));

    public static string AnalyzeCompilationSummaryHeader => Get(nameof(AnalyzeCompilationSummaryHeader));

    public static string AnalyzeAstGeneratedSummary => Get(nameof(AnalyzeAstGeneratedSummary));

    public static string AnalyzeTypeInferenceCompletedSummary =>
        Get(nameof(AnalyzeTypeInferenceCompletedSummary));

    public static string AnalyzePhaseTimingHeader => Get(nameof(AnalyzePhaseTimingHeader));

    public static string AnalyzeSubphaseTimingHeader => Get(nameof(AnalyzeSubphaseTimingHeader));

    public static string AnalyzeHotspotSummaryHeader => Get(nameof(AnalyzeHotspotSummaryHeader));

    public static string AnalyzeBaselineComparisonHeader => Get(nameof(AnalyzeBaselineComparisonHeader));

    public static string AnalyzeProfileTableHeader => Get(nameof(AnalyzeProfileTableHeader));

    public static string AnalyzeCompletedStatus => Get(nameof(AnalyzeCompletedStatus));

    public static string AnalyzeFailedStatus => Get(nameof(AnalyzeFailedStatus));

    public static string LspServerAction => Get(nameof(LspServerAction));

    public static string LspServerDescription => Get(nameof(LspServerDescription));

    public static string LspServerStarting => Get(nameof(LspServerStarting));

    public static string LspServerErrorDetail => Get(nameof(LspServerErrorDetail));

    public static string ProjectTargetNameRequiresProjectInput =>
        Get(nameof(ProjectTargetNameRequiresProjectInput));

    public static string ProjectSourcePathRequired => Get(nameof(ProjectSourcePathRequired));

    public static string TuiCommandDescription => Get(nameof(TuiCommandDescription));

    public static string TuiWorkspaceArgumentDescription => Get(nameof(TuiWorkspaceArgumentDescription));

    public static string TuiStartupFailedDetail => Get(nameof(TuiStartupFailedDetail));

    public static string RunCommandDescription => Get(nameof(RunCommandDescription));

    public static string RunArgsArgumentDescription => Get(nameof(RunArgsArgumentDescription));

    public static string RunTargetNameOptionDescription => Get(nameof(RunTargetNameOptionDescription));

    public static string RunOutputOptionDescription => Get(nameof(RunOutputOptionDescription));

    public static string RunCompileFailedNoRun => Get(nameof(RunCompileFailedNoRun));

    public static string RunCompileFailedDetail => Get(nameof(RunCompileFailedDetail));

    public static string RunTargetNotExecutableDetail => Get(nameof(RunTargetNotExecutableDetail));

    public static string RunningAction => Get(nameof(RunningAction));

    public static string StartingAction => Get(nameof(StartingAction));

    public static string ArtifactAction => Get(nameof(ArtifactAction));

    public static string ArtifactKindBuildDirectory => Get(nameof(ArtifactKindBuildDirectory));

    public static string ArtifactKindDebugDirectory => Get(nameof(ArtifactKindDebugDirectory));

    public static string ArtifactKindDebugGraphs => Get(nameof(ArtifactKindDebugGraphs));

    public static string ArtifactKindDocumentation => Get(nameof(ArtifactKindDocumentation));

    public static string ArtifactKindExecutable => Get(nameof(ArtifactKindExecutable));

    public static string ArtifactKindFormattedSource => Get(nameof(ArtifactKindFormattedSource));

    public static string ArtifactKindLlvmIr => Get(nameof(ArtifactKindLlvmIr));

    public static string ArtifactKindLockFile => Get(nameof(ArtifactKindLockFile));

    public static string ArtifactKindManifest => Get(nameof(ArtifactKindManifest));

    public static string ArtifactKindProfileReport => Get(nameof(ArtifactKindProfileReport));

    public static string ArtifactKindProfileSnapshot => Get(nameof(ArtifactKindProfileSnapshot));

    public static string ArtifactKindProfileTable => Get(nameof(ArtifactKindProfileTable));

    public static string ArtifactKindSource => Get(nameof(ArtifactKindSource));

    public static string DiagnosticLevelError => Get(nameof(DiagnosticLevelError));

    public static string DiagnosticLevelWarning => Get(nameof(DiagnosticLevelWarning));

    public static string DiagnosticLevelInfo => Get(nameof(DiagnosticLevelInfo));

    public static string DiagnosticLevelNote => Get(nameof(DiagnosticLevelNote));

    public static string DiagnosticLevelHelp => Get(nameof(DiagnosticLevelHelp));

    public static string DebugCommandDescription => Get(nameof(DebugCommandDescription));

    public static string DebugTargetNameOptionDescription => Get(nameof(DebugTargetNameOptionDescription));

    public static string DebugOutputOptionDescription => Get(nameof(DebugOutputOptionDescription));

    public static string DebugEmitCfgOptionDescription => Get(nameof(DebugEmitCfgOptionDescription));

    public static string DebuggingAction => Get(nameof(DebuggingAction));

    public static string DebugSucceededStatus => Get(nameof(DebugSucceededStatus));

    public static string DebugFailedStatus => Get(nameof(DebugFailedStatus));

    public static string FmtCommandDescription => Get(nameof(FmtCommandDescription));

    public static string FmtSourceArgumentDescription => Get(nameof(FmtSourceArgumentDescription));

    public static string FmtStdinOptionDescription => Get(nameof(FmtStdinOptionDescription));

    public static string FmtWriteOptionDescription => Get(nameof(FmtWriteOptionDescription));

    public static string FmtCheckOptionDescription => Get(nameof(FmtCheckOptionDescription));

    public static string FmtIndentSizeOptionDescription => Get(nameof(FmtIndentSizeOptionDescription));

    public static string FmtMaxLineLengthOptionDescription => Get(nameof(FmtMaxLineLengthOptionDescription));

    public static string FmtNoFinalNewlineOptionDescription => Get(nameof(FmtNoFinalNewlineOptionDescription));

    public static string FmtNoValidateOptionDescription => Get(nameof(FmtNoValidateOptionDescription));

    public static string FmtWriteStdinInvalid => Get(nameof(FmtWriteStdinInvalid));

    public static string FmtSourceRequiredUnlessStdin => Get(nameof(FmtSourceRequiredUnlessStdin));

    public static string FmtInvalidOptionsDetail => Get(nameof(FmtInvalidOptionsDetail));

    public static string FmtMissingSourceDetail => Get(nameof(FmtMissingSourceDetail));

    public static string FmtSyntaxValidationFailedDetail => Get(nameof(FmtSyntaxValidationFailedDetail));

    public static string FmtAlreadyFormattedDetail => Get(nameof(FmtAlreadyFormattedDetail));

    public static string FmtChangesRequiredDetail => Get(nameof(FmtChangesRequiredDetail));

    public static string FmtWrittenDetail => Get(nameof(FmtWrittenDetail));

    public static string FmtStdoutDetail => Get(nameof(FmtStdoutDetail));

    public static string FormattingAction => Get(nameof(FormattingAction));

    public static string DocCommandDescription => Get(nameof(DocCommandDescription));

    public static string DocTargetNameOptionDescription => Get(nameof(DocTargetNameOptionDescription));

    public static string DocOutputOptionDescription => Get(nameof(DocOutputOptionDescription));

    public static string DocFormatOptionDescription => Get(nameof(DocFormatOptionDescription));

    public static string DocInvalidFormat => Get(nameof(DocInvalidFormat));

    public static string DocInvalidFormatDetail => Get(nameof(DocInvalidFormatDetail));

    public static string DocumentingAction => Get(nameof(DocumentingAction));

    public static string ProfileBatchCommandDescription => Get(nameof(ProfileBatchCommandDescription));

    public static string ProfileBatchManifestArgumentDescription =>
        Get(nameof(ProfileBatchManifestArgumentDescription));

    public static string ProfileBatchFormatOptionDescription => Get(nameof(ProfileBatchFormatOptionDescription));

    public static string ProfileBatchOutputOptionDescription => Get(nameof(ProfileBatchOutputOptionDescription));

    public static string ProfileBatchIterationsOptionDescription =>
        Get(nameof(ProfileBatchIterationsOptionDescription));

    public static string ProfileBatchWarmupOptionDescription => Get(nameof(ProfileBatchWarmupOptionDescription));

    public static string ProfileBatchTopPhasesOptionDescription =>
        Get(nameof(ProfileBatchTopPhasesOptionDescription));

    public static string ProfileBatchTopSubphasesOptionDescription =>
        Get(nameof(ProfileBatchTopSubphasesOptionDescription));

    public static string ProfileBatchInvalidFormat => Get(nameof(ProfileBatchInvalidFormat));

    public static string ProfileBatchInvalidFormatDetail => Get(nameof(ProfileBatchInvalidFormatDetail));

    public static string ProfileBatchInvalidIterations => Get(nameof(ProfileBatchInvalidIterations));

    public static string ProfileBatchInvalidIterationsDetail => Get(nameof(ProfileBatchInvalidIterationsDetail));

    public static string ProfileBatchInvalidWarmup => Get(nameof(ProfileBatchInvalidWarmup));

    public static string ProfileBatchInvalidWarmupDetail => Get(nameof(ProfileBatchInvalidWarmupDetail));

    public static string ProfileBatchManifestMissingDetail => Get(nameof(ProfileBatchManifestMissingDetail));

    public static string ProfileBatchNoCases => Get(nameof(ProfileBatchNoCases));

    public static string ProfileBatchNoCasesDetail => Get(nameof(ProfileBatchNoCasesDetail));

    public static string ProfilingAction => Get(nameof(ProfilingAction));

    public static string ProfileBatchCompileFailed => Get(nameof(ProfileBatchCompileFailed));

    public static string ProfileBatchMarkdownTitle => Get(nameof(ProfileBatchMarkdownTitle));

    public static string ProfileBatchCaseSummaryHeading => Get(nameof(ProfileBatchCaseSummaryHeading));

    public static string ProfileBatchCaseSummaryHeader => Get(nameof(ProfileBatchCaseSummaryHeader));

    public static string ProfileBatchCaseSummarySeparator => Get(nameof(ProfileBatchCaseSummarySeparator));

    public static string ProfileBatchFailuresHeading => Get(nameof(ProfileBatchFailuresHeading));

    public static string ProfileBatchFailuresHeader => Get(nameof(ProfileBatchFailuresHeader));

    public static string ProfileBatchFailuresSeparator => Get(nameof(ProfileBatchFailuresSeparator));

    public static string ProfileBatchAggregatePhasesByTimeHeading =>
        Get(nameof(ProfileBatchAggregatePhasesByTimeHeading));

    public static string ProfileBatchAggregatePhasesByAllocationHeading =>
        Get(nameof(ProfileBatchAggregatePhasesByAllocationHeading));

    public static string ProfileBatchAggregatePhaseTimeHeader =>
        Get(nameof(ProfileBatchAggregatePhaseTimeHeader));

    public static string ProfileBatchAggregatePhaseAllocationHeader =>
        Get(nameof(ProfileBatchAggregatePhaseAllocationHeader));

    public static string ProfileBatchAggregatePhaseSeparator =>
        Get(nameof(ProfileBatchAggregatePhaseSeparator));

    public static string ProfileBatchAggregateSubphasesByTimeHeading =>
        Get(nameof(ProfileBatchAggregateSubphasesByTimeHeading));

    public static string ProfileBatchAggregateSubphasesByAllocationHeading =>
        Get(nameof(ProfileBatchAggregateSubphasesByAllocationHeading));

    public static string ProfileBatchAggregateSubphaseTimeHeader =>
        Get(nameof(ProfileBatchAggregateSubphaseTimeHeader));

    public static string ProfileBatchAggregateSubphaseAllocationHeader =>
        Get(nameof(ProfileBatchAggregateSubphaseAllocationHeader));

    public static string ProfileBatchAggregateSubphaseSeparator =>
        Get(nameof(ProfileBatchAggregateSubphaseSeparator));

    public static string ProfileBatchStatusOk => Get(nameof(ProfileBatchStatusOk));

    public static string ProfileBatchStatusFailed => Get(nameof(ProfileBatchStatusFailed));

    public static string TuiSelectActionTitle => Get(nameof(TuiSelectActionTitle));

    public static string TuiGoodbye => Get(nameof(TuiGoodbye));

    public static string TuiPressAnyKey => Get(nameof(TuiPressAnyKey));

    public static string TuiNoSourceFiles => Get(nameof(TuiNoSourceFiles));

    public static string TuiDirectoryMissing => Get(nameof(TuiDirectoryMissing));

    public static string TuiDirectoryChoice(string name) =>
        Format(nameof(TuiDirectoryChoice), name);

    public static string TuiReturnChoice => Get(nameof(TuiReturnChoice));

    public static string TuiChooseWorkspaceChoice => Get(nameof(TuiChooseWorkspaceChoice));

    public static string TuiBuildProjectChoice => Get(nameof(TuiBuildProjectChoice));

    public static string TuiDebugCompileChoice => Get(nameof(TuiDebugCompileChoice));

    public static string TuiAnalyzeProjectChoice => Get(nameof(TuiAnalyzeProjectChoice));

    public static string TuiViewLogsChoice => Get(nameof(TuiViewLogsChoice));

    public static string TuiConfigureChoice => Get(nameof(TuiConfigureChoice));

    public static string TuiExitChoice => Get(nameof(TuiExitChoice));

    public static string TuiInputPathChoice => Get(nameof(TuiInputPathChoice));

    public static string TuiBrowseDirectoryChoice => Get(nameof(TuiBrowseDirectoryChoice));

    public static string TuiSelectWorkspaceMethodTitle => Get(nameof(TuiSelectWorkspaceMethodTitle));

    public static string TuiWorkspacePathPrompt => Get(nameof(TuiWorkspacePathPrompt));

    public static string TuiWorkspaceUpdated => Get(nameof(TuiWorkspaceUpdated));

    public static string TuiParentDirectoryChoice => Get(nameof(TuiParentDirectoryChoice));

    public static string TuiSelectCurrentDirectoryChoice => Get(nameof(TuiSelectCurrentDirectoryChoice));

    public static string TuiCancelChoice => Get(nameof(TuiCancelChoice));

    public static string TuiSelectDirectoryTitle => Get(nameof(TuiSelectDirectoryTitle));

    public static string TuiSelectBuildFileTitle => Get(nameof(TuiSelectBuildFileTitle));

    public static string TuiOutputFilePrompt => Get(nameof(TuiOutputFilePrompt));

    public static string TuiBuildInProgress => Get(nameof(TuiBuildInProgress));

    public static string TuiCompilingStatus => Get(nameof(TuiCompilingStatus));

    public static string TuiCompleteStatus => Get(nameof(TuiCompleteStatus));

    public static string TuiSelectDebugFileTitle => Get(nameof(TuiSelectDebugFileTitle));

    public static string TuiDebugOutputPrompt => Get(nameof(TuiDebugOutputPrompt));

    public static string TuiSelectDebugLevelTitle => Get(nameof(TuiSelectDebugLevelTitle));

    public static string TuiDebugLevelMinimalChoice => Get(nameof(TuiDebugLevelMinimalChoice));

    public static string TuiDebugLevelNormalChoice => Get(nameof(TuiDebugLevelNormalChoice));

    public static string TuiDebugLevelVerboseChoice => Get(nameof(TuiDebugLevelVerboseChoice));

    public static string TuiDebugLevelDiagnosticChoice => Get(nameof(TuiDebugLevelDiagnosticChoice));

    public static string TuiDebugInProgress => Get(nameof(TuiDebugInProgress));

    public static string TuiSelectAnalyzeFileTitle => Get(nameof(TuiSelectAnalyzeFileTitle));

    public static string TuiSelectAnalyzePhaseTitle => Get(nameof(TuiSelectAnalyzePhaseTitle));

    public static string TuiAnalyzePhaseAllChoice => Get(nameof(TuiAnalyzePhaseAllChoice));

    public static string TuiAnalyzePhaseLexerChoice => Get(nameof(TuiAnalyzePhaseLexerChoice));

    public static string TuiAnalyzePhaseParserChoice => Get(nameof(TuiAnalyzePhaseParserChoice));

    public static string TuiAnalyzePhaseNamerChoice => Get(nameof(TuiAnalyzePhaseNamerChoice));

    public static string TuiAnalyzePhaseTypesChoice => Get(nameof(TuiAnalyzePhaseTypesChoice));

    public static string TuiAnalyzePhaseAbilitiesChoice => Get(nameof(TuiAnalyzePhaseAbilitiesChoice));

    public static string TuiAnalyzePhaseHirChoice => Get(nameof(TuiAnalyzePhaseHirChoice));

    public static string TuiAnalyzePhaseMirChoice => Get(nameof(TuiAnalyzePhaseMirChoice));

    public static string TuiAnalyzePhaseBorrowChoice => Get(nameof(TuiAnalyzePhaseBorrowChoice));

    public static string TuiAnalyzePhaseLlvmChoice => Get(nameof(TuiAnalyzePhaseLlvmChoice));

    public static string TuiAnalyzeInProgress => Get(nameof(TuiAnalyzeInProgress));

    public static string TuiAnalyzingStatus => Get(nameof(TuiAnalyzingStatus));

    public static string TuiLogDirectoryMissing => Get(nameof(TuiLogDirectoryMissing));

    public static string TuiNoLogFiles => Get(nameof(TuiNoLogFiles));

    public static string TuiSelectLogFileTitle => Get(nameof(TuiSelectLogFileTitle));

    public static string TuiLogFileChoice(string fileName, DateTime createdAt) =>
        Format(nameof(TuiLogFileChoice), fileName, createdAt);

    public static string TuiLogContentHeader => Get(nameof(TuiLogContentHeader));

    public static string TuiLogPageNavigation => Get(nameof(TuiLogPageNavigation));

    public static string TuiConfigTitle => Get(nameof(TuiConfigTitle));

    public static string TuiConfigOptionsTitle => Get(nameof(TuiConfigOptionsTitle));

    public static string TuiCreateDirectoryStructureChoice => Get(nameof(TuiCreateDirectoryStructureChoice));

    public static string TuiUpdated => Get(nameof(TuiUpdated));

    public static string TuiDirectoryStructureCreated => Get(nameof(TuiDirectoryStructureCreated));

    public static string TuiCompilerProcessStartFailed => Get(nameof(TuiCompilerProcessStartFailed));

    public static string TuiCompileSucceeded => Get(nameof(TuiCompileSucceeded));

    public static string TuiCompileFailed => Get(nameof(TuiCompileFailed));

    public static string TuiOutputHeader => Get(nameof(TuiOutputHeader));

    public static string TuiErrorHeader => Get(nameof(TuiErrorHeader));

    public static string FinishedAction => Get(nameof(FinishedAction));

    public static string FailedAction => Get(nameof(FailedAction));

    public static string PhaseFinishedDetail(string phase) =>
        Format(nameof(PhaseFinishedDetail), phase);

    public static string CommandFinishedMessage(string command, string duration) =>
        Format(nameof(CommandFinishedMessage), command, duration);

    public static string CommandFinishedMessageWithDetails(string command, string duration, string details) =>
        Format(nameof(CommandFinishedMessageWithDetails), command, duration, details);

    public static string ArtifactMessage(string kind, string path) =>
        Format(nameof(ArtifactMessage), kind, path);

    public static string HelpRootHint => Get(nameof(HelpRootHint));

    public static string HelpNewExamples => Get(nameof(HelpNewExamples));

    public static string HelpNewNotes => Get(nameof(HelpNewNotes));

    public static string HelpBuildExamples => Get(nameof(HelpBuildExamples));

    public static string HelpBuildNotes => Get(nameof(HelpBuildNotes));

    public static string HelpRunExamples => Get(nameof(HelpRunExamples));

    public static string HelpRunNotes => Get(nameof(HelpRunNotes));

    public static string HelpAnalyzeExamples => Get(nameof(HelpAnalyzeExamples));

    public static string HelpAnalyzeNotes => Get(nameof(HelpAnalyzeNotes));

    public static string HelpDebugNotes => Get(nameof(HelpDebugNotes));

    public static string HelpIdeNotes => Get(nameof(HelpIdeNotes));

    public static string MirOptimizationEnableOptionDescription =>
        Get(nameof(MirOptimizationEnableOptionDescription));

    public static string MirOptimizationDisableOptionDescription =>
        Get(nameof(MirOptimizationDisableOptionDescription));

    public static string MirOptimizationEnabledStatus => Get(nameof(MirOptimizationEnabledStatus));

    public static string MirOptimizationDisabledStatus => Get(nameof(MirOptimizationDisabledStatus));

    public static string BuildCommandDescription => Get(nameof(BuildCommandDescription));

    public static string BuildTargetOptionDescription => Get(nameof(BuildTargetOptionDescription));

    public static string BuildOutputOptionDescription => Get(nameof(BuildOutputOptionDescription));

    public static string BuildPhaseOptionDescription => Get(nameof(BuildPhaseOptionDescription));

    public static string BuildEmitLlvmOptionDescription => Get(nameof(BuildEmitLlvmOptionDescription));

    public static string BuildTargetNameOptionDescription => Get(nameof(BuildTargetNameOptionDescription));

    public static string BuildDebugOutputOptionDescription => Get(nameof(BuildDebugOutputOptionDescription));

    public static string BuildDebugGraphFormatOptionDescription =>
        Get(nameof(BuildDebugGraphFormatOptionDescription));

    public static string BuildEmitCfgOptionDescription => Get(nameof(BuildEmitCfgOptionDescription));

    public static string BuildTargetTripleOptionDescription => Get(nameof(BuildTargetTripleOptionDescription));

    public static string BuildOptimizationLevelOptionDescription =>
        Get(nameof(BuildOptimizationLevelOptionDescription));

    public static string BuildLtoOptionDescription => Get(nameof(BuildLtoOptionDescription));

    public static string BuildNativeCpuOptionDescription => Get(nameof(BuildNativeCpuOptionDescription));

    public static string InfoCommandDescription => Get(nameof(InfoCommandDescription));

    public static string InfoVersionOptionDescription => Get(nameof(InfoVersionOptionDescription));

    public static string InfoPhasesOptionDescription => Get(nameof(InfoPhasesOptionDescription));

    public static string InfoStdlibOptionDescription => Get(nameof(InfoStdlibOptionDescription));

    public static string InfoInspectingAction => Get(nameof(InfoInspectingAction));

    public static string InfoCompilerMetadataSubject => Get(nameof(InfoCompilerMetadataSubject));

    public static string InfoCompilerTitle => Get(nameof(InfoCompilerTitle));

    public static string InfoVersionUnknown => Get(nameof(InfoVersionUnknown));

    public static string InfoSupportedPhasesHeader => Get(nameof(InfoSupportedPhasesHeader));

    public static string InfoCompileTargetsHeader => Get(nameof(InfoCompileTargetsHeader));

    public static string InfoStdlibHeader => Get(nameof(InfoStdlibHeader));

    public static string InfoStdlibNone => Get(nameof(InfoStdlibNone));

    public static string InfoStdlibCategoryFunctional => Get(nameof(InfoStdlibCategoryFunctional));

    public static string InfoStdlibCategoryMath => Get(nameof(InfoStdlibCategoryMath));

    public static string InfoStdlibCategoryContainers => Get(nameof(InfoStdlibCategoryContainers));

    public static string InfoStdlibCategoryFileIo => Get(nameof(InfoStdlibCategoryFileIo));

    public static string InfoStdlibCategoryConsoleIo => Get(nameof(InfoStdlibCategoryConsoleIo));

    public static string InfoStdlibCategoryNetwork => Get(nameof(InfoStdlibCategoryNetwork));

    public static string InfoStdlibCategorySerialization => Get(nameof(InfoStdlibCategorySerialization));

    public static string InfoStdlibCategoryBasics => Get(nameof(InfoStdlibCategoryBasics));

    public static string InfoStdlibCategoryOther => Get(nameof(InfoStdlibCategoryOther));

    public static string InfoStdlibSummaryFunctional => Get(nameof(InfoStdlibSummaryFunctional));

    public static string InfoStdlibSummaryMath => Get(nameof(InfoStdlibSummaryMath));

    public static string InfoStdlibSummaryContainers => Get(nameof(InfoStdlibSummaryContainers));

    public static string InfoStdlibSummaryFileIo => Get(nameof(InfoStdlibSummaryFileIo));

    public static string InfoStdlibSummaryConsoleIo => Get(nameof(InfoStdlibSummaryConsoleIo));

    public static string InfoStdlibSummaryNetwork => Get(nameof(InfoStdlibSummaryNetwork));

    public static string InfoStdlibSummarySerialization => Get(nameof(InfoStdlibSummarySerialization));

    public static string InfoStdlibSummaryBasics => Get(nameof(InfoStdlibSummaryBasics));

    public static string InfoStdlibSummaryOther => Get(nameof(InfoStdlibSummaryOther));

    public static string InfoPhaseLexerDescription => Get(nameof(InfoPhaseLexerDescription));

    public static string InfoPhaseParserDescription => Get(nameof(InfoPhaseParserDescription));

    public static string InfoPhaseNamerDescription => Get(nameof(InfoPhaseNamerDescription));

    public static string InfoPhaseTypesDescription => Get(nameof(InfoPhaseTypesDescription));

    public static string InfoPhaseAbilitiesDescription => Get(nameof(InfoPhaseAbilitiesDescription));

    public static string InfoPhaseHirDescription => Get(nameof(InfoPhaseHirDescription));

    public static string InfoPhaseMirDescription => Get(nameof(InfoPhaseMirDescription));

    public static string InfoPhaseBorrowDescription => Get(nameof(InfoPhaseBorrowDescription));

    public static string InfoPhaseLlvmDescription => Get(nameof(InfoPhaseLlvmDescription));

    public static string InfoTargetTokensDescription => Get(nameof(InfoTargetTokensDescription));

    public static string InfoTargetAstDescription => Get(nameof(InfoTargetAstDescription));

    public static string InfoTargetResolvedDescription => Get(nameof(InfoTargetResolvedDescription));

    public static string InfoTargetTypedDescription => Get(nameof(InfoTargetTypedDescription));

    public static string InfoTargetHirDescription => Get(nameof(InfoTargetHirDescription));

    public static string InfoTargetMirDescription => Get(nameof(InfoTargetMirDescription));

    public static string InfoTargetLlvmIrDescription => Get(nameof(InfoTargetLlvmIrDescription));

    public static string InfoTargetNativeDescription => Get(nameof(InfoTargetNativeDescription));

    public static string InfoTargetCilDescription => Get(nameof(InfoTargetCilDescription));

    public static string PkgAddCommandDescription => Get(nameof(PkgAddCommandDescription));

    public static string PkgCommandDescription => Get(nameof(PkgCommandDescription));

    public static string PkgInstallCommandDescription => Get(nameof(PkgInstallCommandDescription));

    public static string PkgUpdateCommandDescription => Get(nameof(PkgUpdateCommandDescription));

    public static string PkgRemoveCommandDescription => Get(nameof(PkgRemoveCommandDescription));

    public static string PkgListCommandDescription => Get(nameof(PkgListCommandDescription));

    public static string PkgTreeCommandDescription => Get(nameof(PkgTreeCommandDescription));

    public static string PkgInitCommandDescription => Get(nameof(PkgInitCommandDescription));

    public static string PkgDependencyNameArgumentDescription => Get(nameof(PkgDependencyNameArgumentDescription));

    public static string PkgDependencyNameToRemoveArgumentDescription =>
        Get(nameof(PkgDependencyNameToRemoveArgumentDescription));

    public static string PkgDependencyNameToUpdateArgumentDescription =>
        Get(nameof(PkgDependencyNameToUpdateArgumentDescription));

    public static string PkgPathOptionDescription => Get(nameof(PkgPathOptionDescription));

    public static string PkgGitOptionDescription => Get(nameof(PkgGitOptionDescription));

    public static string PkgTagOptionDescription => Get(nameof(PkgTagOptionDescription));

    public static string PkgBranchOptionDescription => Get(nameof(PkgBranchOptionDescription));

    public static string PkgCommitOptionDescription => Get(nameof(PkgCommitOptionDescription));

    public static string PkgVersionOptionDescription => Get(nameof(PkgVersionOptionDescription));

    public static string PkgPackageNameOptionDescription => Get(nameof(PkgPackageNameOptionDescription));

    public static string PkgInitialVersionOptionDescription => Get(nameof(PkgInitialVersionOptionDescription));

    public static string PkgKindOptionDescription => Get(nameof(PkgKindOptionDescription));

    public static string PkgSourceRootOptionDescription => Get(nameof(PkgSourceRootOptionDescription));

    public static string PkgDescriptionOptionDescription => Get(nameof(PkgDescriptionOptionDescription));

    public static string PkgLicenseOptionDescription => Get(nameof(PkgLicenseOptionDescription));

    public static string PkgAddingAction => Get(nameof(PkgAddingAction));

    public static string PkgResolvingAction => Get(nameof(PkgResolvingAction));

    public static string PkgUpdatingAction => Get(nameof(PkgUpdatingAction));

    public static string PkgRemovingAction => Get(nameof(PkgRemovingAction));

    public static string PkgListingAction => Get(nameof(PkgListingAction));

    public static string PkgManifestMissingInitError => Get(nameof(PkgManifestMissingInitError));

    public static string PkgManifestMissingError => Get(nameof(PkgManifestMissingError));

    public static string PkgDependencySourceMissingError => Get(nameof(PkgDependencySourceMissingError));

    public static string PkgNoDependenciesInManifest => Get(nameof(PkgNoDependenciesInManifest));

    public static string PkgNoLockFile => Get(nameof(PkgNoLockFile));

    public static string PkgLockReadFailed => Get(nameof(PkgLockReadFailed));

    public static string PkgNoDependencies => Get(nameof(PkgNoDependencies));

    public static string PkgStdEmbeddedTreeEntry => Get(nameof(PkgStdEmbeddedTreeEntry));

    public static string PkgManifestMissingDetail => Get(nameof(PkgManifestMissingDetail));

    public static string PkgDependencySourceMissingDetail => Get(nameof(PkgDependencySourceMissingDetail));

    public static string PkgResolveFailedDetail => Get(nameof(PkgResolveFailedDetail));

    public static string PkgLockReadFailedDetail => Get(nameof(PkgLockReadFailedDetail));

    public static string PkgNoLockFileDetail => Get(nameof(PkgNoLockFileDetail));

    public static string PkgNoDependenciesDetail => Get(nameof(PkgNoDependenciesDetail));

    public static string PkgZeroExplicitDependenciesDetail => Get(nameof(PkgZeroExplicitDependenciesDetail));

    public static string PkgPathSourceKind => Get(nameof(PkgPathSourceKind));

    public static string PkgGitSourceKind => Get(nameof(PkgGitSourceKind));

    public static string PkgEmbeddedSourceKind => Get(nameof(PkgEmbeddedSourceKind));

    public static string PkgUnknownSourceKind => Get(nameof(PkgUnknownSourceKind));

    public static string ProjectCreatingAction => Get(nameof(ProjectCreatingAction));

    public static string ProjectInvalidKindDetail => Get(nameof(ProjectInvalidKindDetail));

    public static string ProjectManifestAlreadyExistsDetail => Get(nameof(ProjectManifestAlreadyExistsDetail));

    public static string NewCommandDescription => Get(nameof(NewCommandDescription));

    public static string NewPathArgumentDescription => Get(nameof(NewPathArgumentDescription));

    public static string NewNameOptionDescription => Get(nameof(NewNameOptionDescription));

    public static string NewVersionOptionDescription => Get(nameof(NewVersionOptionDescription));

    public static string NewKindOptionDescription => Get(nameof(NewKindOptionDescription));

    public static string NewSourceRootOptionDescription => Get(nameof(NewSourceRootOptionDescription));

    public static string NewDescriptionOptionDescription => Get(nameof(NewDescriptionOptionDescription));

    public static string NewLicenseOptionDescription => Get(nameof(NewLicenseOptionDescription));

    public static string NewExistingDirectoryInitHelp => Get(nameof(NewExistingDirectoryInitHelp));

    public static string BuildTargetOptionHelp => Get(nameof(BuildTargetOptionHelp));

    public static string BuildOutputOptionHelp => Get(nameof(BuildOutputOptionHelp));

    public static string BuildPhaseOptionHelp => Get(nameof(BuildPhaseOptionHelp));

    public static string BuildEmitLlvmOptionHelp => Get(nameof(BuildEmitLlvmOptionHelp));

    public static string CompilingAction => Get(nameof(CompilingAction));

    public static string CheckingAction => Get(nameof(CheckingAction));

    public static string InvalidTargetTripleDetail => Get(nameof(InvalidTargetTripleDetail));

    public static string CodegenFailedDetail => Get(nameof(CodegenFailedDetail));

    public static string BuildSucceededStatus => Get(nameof(BuildSucceededStatus));

    public static string BuildFailedStatus => Get(nameof(BuildFailedStatus));

    public static string LspCompilationFailed(string message) =>
        Format(nameof(LspCompilationFailed), message);

    public static string SourceFileNotFound(string path) =>
        Format(nameof(SourceFileNotFound), path);

    public static string WarningAsErrorCodesStatus(string codes) =>
        Format(nameof(WarningAsErrorCodesStatus), codes);

    public static string UnknownTargetPlatform(string targetTriple) =>
        Format(nameof(UnknownTargetPlatform), targetTriple);

    public static string SupportedTargetsStatus(string targets) =>
        Format(nameof(SupportedTargetsStatus), targets);

    public static string BuildActionSubject(string sourcePath, object target) =>
        Format(nameof(BuildActionSubject), sourcePath, target);

    public static string CheckingActionSubject(string sourcePath, object? phase) =>
        Format(nameof(CheckingActionSubject), sourcePath, phase ?? string.Empty);

    public static string CompileSourceStatus(string sourcePath) =>
        Format(nameof(CompileSourceStatus), sourcePath);

    public static string AnalyzeSourceStatus(string sourcePath) =>
        Format(nameof(AnalyzeSourceStatus), sourcePath);

    public static string TargetStatus(object target) =>
        Format(nameof(TargetStatus), target);

    public static string StopPhaseStatus(object phase) =>
        Format(nameof(StopPhaseStatus), phase);

    public static string TargetPlatformStatus(string targetTriple) =>
        Format(nameof(TargetPlatformStatus), targetTriple);

    public static string OptimizationLevelStatus(int level) =>
        Format(nameof(OptimizationLevelStatus), level);

    public static string DebugOutputStatus(string path) =>
        Format(nameof(DebugOutputStatus), path);

    public static string DebugGraphArtifactsStatus(object format) =>
        Format(nameof(DebugGraphArtifactsStatus), format);

    public static string LlvmIrWritten(string path) =>
        Format(nameof(LlvmIrWritten), path);

    public static string ExecutableGenerated(string path) =>
        Format(nameof(ExecutableGenerated), path);

    public static string CodeGenerationFailed(string message) =>
        Format(nameof(CodeGenerationFailed), message);

    public static string CompletedPhaseStatus(object phase) =>
        Format(nameof(CompletedPhaseStatus), phase);

    public static string TotalTimeStatus(double milliseconds) =>
        Format(nameof(TotalTimeStatus), milliseconds);

    public static string PhaseTimeStatus(object phase, double milliseconds) =>
        Format(nameof(PhaseTimeStatus), phase, milliseconds);

    public static string PhaseTimeAllocationStatus(object phase, double milliseconds, string allocatedBytes) =>
        Format(nameof(PhaseTimeAllocationStatus), phase, milliseconds, allocatedBytes);

    public static string PhaseTargetDetails(object phase, object target) =>
        Format(nameof(PhaseTargetDetails), phase, target);

    public static string AnalyzePhaseDiagnosticsDetails(object phase, int diagnosticCount) =>
        Format(nameof(AnalyzePhaseDiagnosticsDetails), phase, diagnosticCount);

    public static string AnalyzePhaseStatus(object phase) =>
        Format(nameof(AnalyzePhaseStatus), phase);

    public static string AnalyzePhaseCompletedHeader(object phase) =>
        Format(nameof(AnalyzePhaseCompletedHeader), phase);

    public static string AnalyzeCompletedPhaseSummary(object phase) =>
        Format(nameof(AnalyzeCompletedPhaseSummary), phase);

    public static string AnalyzeTokenCountSummary(int count) =>
        Format(nameof(AnalyzeTokenCountSummary), count);

    public static string AnalyzeTotalTimeSummary(double milliseconds) =>
        Format(nameof(AnalyzeTotalTimeSummary), milliseconds);

    public static string AnalyzeSymbolCountSummary(int count) =>
        Format(nameof(AnalyzeSymbolCountSummary), count);

    public static string AnalyzePhaseTimeLine(object phase, double milliseconds) =>
        Format(nameof(AnalyzePhaseTimeLine), phase, milliseconds);

    public static string AnalyzePhaseTimeAllocationLine(
        object phase,
        double milliseconds,
        string allocatedBytes) =>
        Format(nameof(AnalyzePhaseTimeAllocationLine), phase, milliseconds, allocatedBytes);

    public static string AnalyzeSubphaseMetric(
        string name,
        double milliseconds,
        string allocatedBytes,
        string managedBytesDelta,
        int gen0Collections,
        int gen1Collections,
        int gen2Collections) =>
        Format(
            nameof(AnalyzeSubphaseMetric),
            name,
            milliseconds,
            allocatedBytes,
            managedBytesDelta,
            gen0Collections,
            gen1Collections,
            gen2Collections);

    public static string AnalyzeProfileTableWritten(string path) =>
        Format(nameof(AnalyzeProfileTableWritten), path);

    public static string AnalyzeProfileSnapshotWritten(string path) =>
        Format(nameof(AnalyzeProfileSnapshotWritten), path);

    public static string LspHoverBindingMetadata(string bindingMode) =>
        Format(nameof(LspHoverBindingMetadata), bindingMode);

    public static string LspHoverFfiLibraryMetadata(string library) =>
        Format(nameof(LspHoverFfiLibraryMetadata), library);

    public static string LspServerError(string message) =>
        Format(nameof(LspServerError), message);

    public static string TuiStartFailed(string message) =>
        Format(nameof(TuiStartFailed), message);

    public static string TuiError(string message) =>
        Format(nameof(TuiError), message);

    public static string TuiHeader(string workspacePath, string srcPath, string debugPath, string buildPath, string logPath) =>
        Format(nameof(TuiHeader), workspacePath, srcPath, debugPath, buildPath, logPath);

    public static string TuiCurrentWorkspace(string workspacePath) =>
        Format(nameof(TuiCurrentWorkspace), workspacePath);

    public static string TuiBrowseDirectoryHeader(string currentDirectory) =>
        Format(nameof(TuiBrowseDirectoryHeader), currentDirectory);

    public static string TuiLogPageHeader(string fileName, int page) =>
        Format(nameof(TuiLogPageHeader), fileName, page);

    public static string TuiWorkspacePathOption(string path) =>
        Format(nameof(TuiWorkspacePathOption), path);

    public static string TuiSourceDirectoryOption(string path) =>
        Format(nameof(TuiSourceDirectoryOption), path);

    public static string TuiDebugOutputDirectoryOption(string path) =>
        Format(nameof(TuiDebugOutputDirectoryOption), path);

    public static string TuiBuildOutputDirectoryOption(string path) =>
        Format(nameof(TuiBuildOutputDirectoryOption), path);

    public static string TuiLogDirectoryOption(string path) =>
        Format(nameof(TuiLogDirectoryOption), path);

    public static string TuiNewPathPrompt(string currentPath) =>
        Format(nameof(TuiNewPathPrompt), currentPath);

    public static string TuiCreateDirectoryFailed(string message) =>
        Format(nameof(TuiCreateDirectoryFailed), message);

    public static string TuiWorkspaceSubject(string workspace) =>
        Format(nameof(TuiWorkspaceSubject), workspace);

    public static string RunTargetNotExecutable(string targetName) =>
        Format(nameof(RunTargetNotExecutable), targetName);

    public static string RunActionSubject(string sourcePath) =>
        Format(nameof(RunActionSubject), sourcePath);

    public static string RunSourceStatus(string sourcePath) =>
        Format(nameof(RunSourceStatus), sourcePath);

    public static string RunOutputStatus(string outputPath) =>
        Format(nameof(RunOutputStatus), outputPath);

    public static string RunExecutingStatus(string outputPath) =>
        Format(nameof(RunExecutingStatus), outputPath);

    public static string RunFinishedDetail(int exitCode, double milliseconds) =>
        Format(nameof(RunFinishedDetail), exitCode, milliseconds);

    public static string DebugActionSubject(string sourcePath, object debugLevel) =>
        Format(nameof(DebugActionSubject), sourcePath, debugLevel);

    public static string DebugCompileStatus(string sourcePath) =>
        Format(nameof(DebugCompileStatus), sourcePath);

    public static string DebugOutputDirectoryStatus(string debugOutputPath) =>
        Format(nameof(DebugOutputDirectoryStatus), debugOutputPath);

    public static string DebugLevelStatus(object debugLevel) =>
        Format(nameof(DebugLevelStatus), debugLevel);

    public static string DebugSavedStatus(string debugOutputPath) =>
        Format(nameof(DebugSavedStatus), debugOutputPath);

    public static string FmtSourceFileNotFound(string inputFile) =>
        Format(nameof(FmtSourceFileNotFound), inputFile);

    public static string FmtFormattingChangesRequired(string inputFile) =>
        Format(nameof(FmtFormattingChangesRequired), inputFile);

    public static string FmtDiagnosticLine(string code, string message) =>
        Format(nameof(FmtDiagnosticLine), code, message);

    public static string DocActionSubject(string sourcePath, string format) =>
        Format(nameof(DocActionSubject), sourcePath, format);

    public static string DocGeneratingStatus(string sourcePath) =>
        Format(nameof(DocGeneratingStatus), sourcePath);

    public static string DocGeneratedStatus(string outputPath) =>
        Format(nameof(DocGeneratedStatus), outputPath);

    public static string DocModuleSummary(string moduleName, int typeCount, int functionCount, int traitCount) =>
        Format(nameof(DocModuleSummary), moduleName, typeCount, functionCount, traitCount);

    public static string DocFinishedDetail(int typeCount, int functionCount, int traitCount) =>
        Format(nameof(DocFinishedDetail), typeCount, functionCount, traitCount);

    public static string ProfileBatchManifestMissing(string manifestPath) =>
        Format(nameof(ProfileBatchManifestMissing), manifestPath);

    public static string ProfileBatchSubject(string name, string manifestPath) =>
        Format(nameof(ProfileBatchSubject), name, manifestPath);

    public static string ProfileBatchStatus(string name) =>
        Format(nameof(ProfileBatchStatus), name);

    public static string ProfileBatchCaseCountStatus(int caseCount, int iterations, int warmup) =>
        Format(nameof(ProfileBatchCaseCountStatus), caseCount, iterations, warmup);

    public static string ProfileBatchRunningCase(string name) =>
        Format(nameof(ProfileBatchRunningCase), name);

    public static string ProfileBatchCaseSucceeded(string name, double averageMilliseconds, string hottestPhase) =>
        Format(nameof(ProfileBatchCaseSucceeded), name, averageMilliseconds, hottestPhase);

    public static string ProfileBatchCaseFailed(string name, string reason) =>
        Format(nameof(ProfileBatchCaseFailed), name, reason);

    public static string ProfileBatchReportWritten(string outputPath) =>
        Format(nameof(ProfileBatchReportWritten), outputPath);

    public static string ProfileBatchFinishedDetail(int succeeded, int total) =>
        Format(nameof(ProfileBatchFinishedDetail), succeeded, total);

    public static string ProfileBatchParseManifestFailed(string manifestPath) =>
        Format(nameof(ProfileBatchParseManifestFailed), manifestPath);

    public static string ProfileBatchMarkdownName(string name) =>
        Format(nameof(ProfileBatchMarkdownName), name);

    public static string ProfileBatchMarkdownManifest(string manifestPath) =>
        Format(nameof(ProfileBatchMarkdownManifest), manifestPath);

    public static string ProfileBatchMarkdownCases(int count) =>
        Format(nameof(ProfileBatchMarkdownCases), count);

    public static string ProfileBatchMarkdownSuccessfulCases(int count) =>
        Format(nameof(ProfileBatchMarkdownSuccessfulCases), count);

    public static string ProfileBatchMarkdownIterations(int iterations) =>
        Format(nameof(ProfileBatchMarkdownIterations), iterations);

    public static string ProfileBatchMarkdownWarmup(int warmup) =>
        Format(nameof(ProfileBatchMarkdownWarmup), warmup);

    public static string ProjectConfigSummary(string path) =>
        Format(nameof(ProjectConfigSummary), path);

    public static string ProjectSourceRootsSummary(string roots) =>
        Format(nameof(ProjectSourceRootsSummary), roots);

    public static string ProjectImportRootsSummary(string roots) =>
        Format(nameof(ProjectImportRootsSummary), roots);

    public static string ProjectEntryTargetSummary(string name, string kind) =>
        Format(nameof(ProjectEntryTargetSummary), name, kind);

    public static string ProjectTargetEntrySummary(string path) =>
        Format(nameof(ProjectTargetEntrySummary), path);

    public static string ProjectTargetDependenciesSummary(string dependencies) =>
        Format(nameof(ProjectTargetDependenciesSummary), dependencies);

    public static string ProjectDependenciesSummary(string dependencies) =>
        Format(nameof(ProjectDependenciesSummary), dependencies);

    public static string ProjectDependencySearchRootsSummary(string roots) =>
        Format(nameof(ProjectDependencySearchRootsSummary), roots);

    public static string ProjectBuildGraphSummary(string graph) =>
        Format(nameof(ProjectBuildGraphSummary), graph);

    public static string IdeCheckingDetail(string inputFile, object? phase) =>
        Format(nameof(IdeCheckingDetail), inputFile, phase ?? string.Empty);

    public static string IdeFinishedDetails(object? phase, int diagnosticCount) =>
        Format(nameof(IdeFinishedDetails), phase ?? string.Empty, diagnosticCount);

    public static string IdeCommandFailed(string message) =>
        Format(nameof(IdeCommandFailed), message);

    public static string ReplError(string message) =>
        Format(nameof(ReplError), message);

    public static string ReplFileNotFound(string filePath) =>
        Format(nameof(ReplFileNotFound), filePath);

    public static string ReplFileLoaded(string filePath) =>
        Format(nameof(ReplFileLoaded), filePath);

    public static string ReplCompilationError(string message) =>
        Format(nameof(ReplCompilationError), message);

    public static string ReplUnknownCommand(string command) =>
        Format(nameof(ReplUnknownCommand), command);

    public static string InfoVersionLine(string version) =>
        Format(nameof(InfoVersionLine), version);

    public static string InfoTargetFrameworkLine(string targetFramework) =>
        Format(nameof(InfoTargetFrameworkLine), targetFramework);

    public static string InfoPhaseLine(string phase, string description) =>
        Format(nameof(InfoPhaseLine), phase, description);

    public static string InfoTargetLine(object target, string description) =>
        Format(nameof(InfoTargetLine), target, description);

    public static string InfoStdlibCategoryHeader(string category) =>
        Format(nameof(InfoStdlibCategoryHeader), category);

    public static string InfoStdlibSummaryLine(string summary) =>
        Format(nameof(InfoStdlibSummaryLine), summary);

    public static string InfoStdlibRepresentativeApisLine(string apis) =>
        Format(nameof(InfoStdlibRepresentativeApisLine), apis);

    public static string InfoStdlibModuleLine(string module) =>
        Format(nameof(InfoStdlibModuleLine), module);

    public static string InfoStdlibValuesLine(string values) =>
        Format(nameof(InfoStdlibValuesLine), values);

    public static string InfoStdlibFunctionsLine(string functions) =>
        Format(nameof(InfoStdlibFunctionsLine), functions);

    public static string InfoStdlibTypesLine(string types) =>
        Format(nameof(InfoStdlibTypesLine), types);

    public static string InfoStdlibTraitsLine(string traits) =>
        Format(nameof(InfoStdlibTraitsLine), traits);

    public static string InfoStdlibModulesLine(string modules) =>
        Format(nameof(InfoStdlibModulesLine), modules);

    public static string InfoStdlibConstructorsLine(string constructors) =>
        Format(nameof(InfoStdlibConstructorsLine), constructors);

    public static string PkgAddActionSubject(string name, string configPath) =>
        Format(nameof(PkgAddActionSubject), name, configPath);

    public static string PkgDependenciesForSubject(string configPath) =>
        Format(nameof(PkgDependenciesForSubject), configPath);

    public static string PkgAllDependenciesInSubject(string configPath) =>
        Format(nameof(PkgAllDependenciesInSubject), configPath);

    public static string PkgNamedDependencyInSubject(string name, string configPath) =>
        Format(nameof(PkgNamedDependencyInSubject), name, configPath);

    public static string PkgRemoveActionSubject(string name, string configPath) =>
        Format(nameof(PkgRemoveActionSubject), name, configPath);

    public static string PkgDependenciesFromSubject(string lockPath) =>
        Format(nameof(PkgDependenciesFromSubject), lockPath);

    public static string PkgDependencyTreeForSubject(string configPath) =>
        Format(nameof(PkgDependencyTreeForSubject), configPath);

    public static string PkgPathSource(string path) =>
        Format(nameof(PkgPathSource), path);

    public static string PkgGitSource(string git) =>
        Format(nameof(PkgGitSource), git);

    public static string PkgVersionSource(string version) =>
        Format(nameof(PkgVersionSource), version);

    public static string PkgDependencyLine(string name, string sourceDescription) =>
        Format(nameof(PkgDependencyLine), name, sourceDescription);

    public static string PkgResolvedDependencies(int count) =>
        Format(nameof(PkgResolvedDependencies), count);

    public static string PkgDependencyCountDetail(int count) =>
        Format(nameof(PkgDependencyCountDetail), count);

    public static string PkgResolveDependenciesFailed(string message) =>
        Format(nameof(PkgResolveDependenciesFailed), message);

    public static string PkgUpdateLine(string name, string source) =>
        Format(nameof(PkgUpdateLine), name, source);

    public static string PkgUpdatedDependencies(int count) =>
        Format(nameof(PkgUpdatedDependencies), count);

    public static string PkgUpdateFailed(string message) =>
        Format(nameof(PkgUpdateFailed), message);

    public static string PkgDependencyNotFound(string name) =>
        Format(nameof(PkgDependencyNotFound), name);

    public static string PkgDependencyNotPresentDetail(string name) =>
        Format(nameof(PkgDependencyNotPresentDetail), name);

    public static string PkgAddedDependency(string name) =>
        Format(nameof(PkgAddedDependency), name);

    public static string PkgRemovedDependency(string name) =>
        Format(nameof(PkgRemovedDependency), name);

    public static string PkgDependencyDetail(string name) =>
        Format(nameof(PkgDependencyDetail), name);

    public static string PkgDependenciesHeader(int count) =>
        Format(nameof(PkgDependenciesHeader), count);

    public static string PkgListLine(string name, string source, string hashSuffix) =>
        Format(nameof(PkgListLine), name, source, hashSuffix);

    public static string ProjectPackageInSubject(string directory) =>
        Format(nameof(ProjectPackageInSubject), directory);

    public static string ProjectInvalidKind(string kind) =>
        Format(nameof(ProjectInvalidKind), kind);

    public static string ProjectManifestAlreadyExists(string directory) =>
        Format(nameof(ProjectManifestAlreadyExists), directory);

    public static string ProjectCreatedManifest(string directory) =>
        Format(nameof(ProjectCreatedManifest), directory);

    public static string ProjectPackageDetail(string packageName) =>
        Format(nameof(ProjectPackageDetail), packageName);

    public static string NewTargetPathIsFile(string path) =>
        Format(nameof(NewTargetPathIsFile), path);

    public static string NewTargetDirectoryNotEmpty(string path) =>
        Format(nameof(NewTargetDirectoryNotEmpty), path);

    public static string ProjectCreatedSourceFile(string path) =>
        Format(nameof(ProjectCreatedSourceFile), path);

    public static string ProjectCreatedDirectory(string path) =>
        Format(nameof(ProjectCreatedDirectory), path);

    private static string Get(string name) =>
        Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Format(string name, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(name), args);
}
