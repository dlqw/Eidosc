using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Eidosc.Diagnostic;

// IDE-specific diagnostic message properties
internal static partial class DiagnosticMessages
{

    public static string NamingStyleMismatch(string name, string category, string convention) =>
        Format(nameof(NamingStyleMismatch), name, category, convention);

    public static string NamingStyleExpectedName(string expected) =>
        Format(nameof(NamingStyleExpectedName), expected);

    public static string RenameSymbolSuggestion(string expected) =>
        Format(nameof(RenameSymbolSuggestion), expected);

    public static string NamingFqnRedundancy(string name, string moduleSegment) =>
        Format(nameof(NamingFqnRedundancy), name, moduleSegment);

    public static string NamingWeakPublicTypeName(string name) =>
        Format(nameof(NamingWeakPublicTypeName), name);

    public static string NamingModuleFileMismatch(string actual, string expected) =>
        Format(nameof(NamingModuleFileMismatch), actual, expected);


    public static string MirSpecializationLoopDidNotConverge(int maxIterations) =>
        Format(nameof(MirSpecializationLoopDidNotConverge), maxIterations);

    public static string MirSpecializationLoopDidNotConvergeHelp =>
        Get(nameof(MirSpecializationLoopDidNotConvergeHelp));

    public static string GenericCallWillRemainUnresolvedNote =>
        Get(nameof(GenericCallWillRemainUnresolvedNote));

    public static string TemplateSpecializedNameNote(string templateKey, string specializedName) =>
        Format(nameof(TemplateSpecializedNameNote), templateKey, specializedName);

    public static string MirSpecializationFailureTemplateLabel => Get(nameof(MirSpecializationFailureTemplateLabel));

    public static string MirSpecializationFailureReasonNote(string reason) =>
        Format(nameof(MirSpecializationFailureReasonNote), reason);

    public static string MirSpecializationFailureSignatureNote(string templateKey, string signatureKey, string previewName) =>
        Format(nameof(MirSpecializationFailureSignatureNote), templateKey, signatureKey, previewName);

    public static string MirSpecializationFailureSuggestionNote(string reason) =>
        reason switch
        {
            "unresolved-constructor-binding" => Get("MirSpecializationFailureSuggestionUnresolvedConstructorBinding"),
            "type-inference-failed" => Get("MirSpecializationFailureSuggestionTypeInferenceFailed"),
            "partial-binding-incomplete" => Get("MirSpecializationFailureSuggestionPartialBindingIncomplete"),
            "no-concrete-dispatch-type" => Get("MirSpecializationFailureSuggestionNoConcreteDispatchType"),
            "unresolved-types" => Get("MirSpecializationFailureSuggestionUnresolvedTypes"),
            _ => Get("MirSpecializationFailureSuggestionDefault")
        };

    public static string EnsureConcreteTypesBeforeLlvmNote =>
        Get(nameof(EnsureConcreteTypesBeforeLlvmNote));

    public static string CannotLowerAggregateFieldToByteOffset(string fieldName) =>
        Format(nameof(CannotLowerAggregateFieldToByteOffset), fieldName);

    public static string UnresolvedAggregateFieldLabel => Get(nameof(UnresolvedAggregateFieldLabel));

    public static string UnresolvedTypeVariableReachedLlvm(int variableIndex) =>
        Format(nameof(UnresolvedTypeVariableReachedLlvm), variableIndex);

    public static string GrammarCacheSaveFailed(string message) =>
        Format(nameof(GrammarCacheSaveFailed), message);

    public static string SourceContainsNoDeclarations =>
        Get(nameof(SourceContainsNoDeclarations));

    public static string UnknownEscapeSequence(char escape) =>
        Format(nameof(UnknownEscapeSequence), escape);

    public static string IdentifierLengthExceeded(int maxLength, int actualLength) =>
        Format(nameof(IdentifierLengthExceeded), maxLength, actualLength);

    public static string LlvmToolsNotFound => Get(nameof(LlvmToolsNotFound));

    public static string NativeFfiSourceFileNotFound(string sourcePath) =>
        Format(nameof(NativeFfiSourceFileNotFound), sourcePath);

    public static string ClangNotFoundForNativeFfi => Get(nameof(ClangNotFoundForNativeFfi));

    public static string ClangNotFound => Get(nameof(ClangNotFound));

    public static string ExecutableEntryNotFound => Get(nameof(ExecutableEntryNotFound));

    public static string ClangNotFoundForEntryShim => Get(nameof(ClangNotFoundForEntryShim));

    public static string FailedToRunProcess(string executable, string message) =>
        Format(nameof(FailedToRunProcess), executable, message);

    public static string FailedToCompileRuntimeFromConfiguredPath(string path, string? message) =>
        Format(nameof(FailedToCompileRuntimeFromConfiguredPath), path, message ?? string.Empty);

    public static string ConfiguredRuntimePathMissing(string path) =>
        Format(nameof(ConfiguredRuntimePathMissing), path);

    public static string RuntimeLinkInputNotFound(string runtimeLibraryPath) =>
        Format(nameof(RuntimeLinkInputNotFound), runtimeLibraryPath);

    public static string FailedToCompileRuntimeSource(string sourcePath, string? message) =>
        Format(nameof(FailedToCompileRuntimeSource), sourcePath, message ?? string.Empty);

    public static string ClangNotFoundForRuntime => Get(nameof(ClangNotFoundForRuntime));

    public static string CannotUnifyEffectVariable(object variable, string requiredEffect) =>
        Format(nameof(CannotUnifyEffectVariable), variable, requiredEffect);

    public static string InvalidSyntax => Get(nameof(InvalidSyntax));

    public static string InvalidSyntaxLabel => Get(nameof(InvalidSyntaxLabel));

    public static string StyleSuggestionCurriedPrefixCallsUseFluentOrGroupedCallSyntax =>
        Get(nameof(StyleSuggestionCurriedPrefixCallsUseFluentOrGroupedCallSyntax));

    public static string StyleSuggestionCurriedQualifiedCallsUseGroupedCallSyntax =>
        Get(nameof(StyleSuggestionCurriedQualifiedCallsUseGroupedCallSyntax));

    public static string CurriedPrefixCallCanBeRewrittenLabel =>
        Get(nameof(CurriedPrefixCallCanBeRewrittenLabel));

    public static string CurriedCallUseBestLocalStyleHelp =>
        Get(nameof(CurriedCallUseBestLocalStyleHelp));

    public static string GroupedCallPreservesQualifiedFunctionPathHelp =>
        Get(nameof(GroupedCallPreservesQualifiedFunctionPathHelp));

    public static string IfExpressionMissingElseNonUnitDuringMir =>
        Get(nameof(IfExpressionMissingElseNonUnitDuringMir));

    public static string MissingElseBranchValueLabel => Get(nameof(MissingElseBranchValueLabel));

    public static string MissingNonUnitElseBranchReason => Get(nameof(MissingNonUnitElseBranchReason));

    public static string ReturnExpressionMissingValueNonUnitDuringMir =>
        Get(nameof(ReturnExpressionMissingValueNonUnitDuringMir));

    public static string MissingReturnValueLabel => Get(nameof(MissingReturnValueLabel));

    public static string MissingNonUnitReturnValueReason => Get(nameof(MissingNonUnitReturnValueReason));

    public static string UnreachableAfterReturnReason => Get(nameof(UnreachableAfterReturnReason));

    public static string BreakExpressionOnlyInsideLoop => Get(nameof(BreakExpressionOnlyInsideLoop));

    public static string BreakOutsideLoopLabel => Get(nameof(BreakOutsideLoopLabel));

    public static string ContinueExpressionOnlyInsideLoop => Get(nameof(ContinueExpressionOnlyInsideLoop));

    public static string ContinueOutsideLoopLabel => Get(nameof(ContinueOutsideLoopLabel));

    public static string ModuleValueDependencyCycleDetected(string cycle) =>
        Format(nameof(ModuleValueDependencyCycleDetected), cycle);

    public static string ModuleLevelValueCycleLabel => Get(nameof(ModuleLevelValueCycleLabel));

    public static string RecursiveClosureCaptureParameterResolutionFailed(string parameterName) =>
        Format(nameof(RecursiveClosureCaptureParameterResolutionFailed), parameterName);

    public static string RecursiveClosureSelfBindingLabel => Get(nameof(RecursiveClosureSelfBindingLabel));

    public static string CannotResolveMirTypePreparingPoison(string context) =>
        Format(nameof(CannotResolveMirTypePreparingPoison), context);

    public static string MirPoisonTypeLabel => Get(nameof(MirPoisonTypeLabel));

    public static string CannotResolveMirTypePreparingMissingFallback(string context) =>
        Format(nameof(CannotResolveMirTypePreparingMissingFallback), context);

    public static string MissingMirTypeLabel => Get(nameof(MissingMirTypeLabel));

    public static string MissingMirTypeForPlaceOperandReason =>
        Get(nameof(MissingMirTypeForPlaceOperandReason));

    public static string UnsupportedHirExpressionDuringMirLowering(string expressionType) =>
        Format(nameof(UnsupportedHirExpressionDuringMirLowering), expressionType);

    public static string UnsupportedHirExpressionReason(string expressionType) =>
        Format(nameof(UnsupportedHirExpressionReason), expressionType);

    public static string MirFallbackLabel => Get(nameof(MirFallbackLabel));

    public static string UnsupportedHirUnaryOperatorDuringMirLowering(object op) =>
        Format(nameof(UnsupportedHirUnaryOperatorDuringMirLowering), op);

    public static string UnsupportedHirUnaryOperatorReason(object op) =>
        Format(nameof(UnsupportedHirUnaryOperatorReason), op);

    public static string UnsupportedUnaryOperatorLabel => Get(nameof(UnsupportedUnaryOperatorLabel));

    public static string UnsupportedHirBinaryOperatorDuringMirLowering(object op) =>
        Format(nameof(UnsupportedHirBinaryOperatorDuringMirLowering), op);

    public static string UnsupportedHirBinaryOperatorReason(object op) =>
        Format(nameof(UnsupportedHirBinaryOperatorReason), op);

    public static string UnsupportedBinaryOperatorLabel => Get(nameof(UnsupportedBinaryOperatorLabel));

    public static string HirErrorNodeReachedMirLowering => Get(nameof(HirErrorNodeReachedMirLowering));

    public static string CannotLowerHirErrorNode(string reason) =>
        Format(nameof(CannotLowerHirErrorNode), reason);

    public static string MirPoisonLabel => Get(nameof(MirPoisonLabel));

    public static string UnsupportedHirStatementDuringMirLowering(string statementType) =>
        Format(nameof(UnsupportedHirStatementDuringMirLowering), statementType);

    public static string UnsupportedHirStatementReason(string statementType) =>
        Format(nameof(UnsupportedHirStatementReason), statementType);

    public static string UnsupportedStatementLabel => Get(nameof(UnsupportedStatementLabel));

    public static string UnsupportedDeclarationInMirBlockStatement(string declarationType) =>
        Format(nameof(UnsupportedDeclarationInMirBlockStatement), declarationType);

    public static string UnsupportedDeclarationLabel => Get(nameof(UnsupportedDeclarationLabel));

    public static string UnsupportedAssignmentTargetDuringMirLowering(string targetType) =>
        Format(nameof(UnsupportedAssignmentTargetDuringMirLowering), targetType);

    public static string UnsupportedAssignmentTargetLabel => Get(nameof(UnsupportedAssignmentTargetLabel));

    public static string EmptyMatchExpressionDuringMirLowering =>
        Get(nameof(EmptyMatchExpressionDuringMirLowering));

    public static string EmptyMatchExpressionLabel => Get(nameof(EmptyMatchExpressionLabel));

    public static string MatchContainsPoisonedPatternReason =>
        Get(nameof(MatchContainsPoisonedPatternReason));

    public static string NonExhaustiveMatchFallbackDuringMirLowering =>
        Get(nameof(NonExhaustiveMatchFallbackDuringMirLowering));

    public static string NonExhaustiveMatchFallbackLabel =>
        Get(nameof(NonExhaustiveMatchFallbackLabel));

    public static string NonExhaustiveMatchFallbackHelp =>
        Get(nameof(NonExhaustiveMatchFallbackHelp));

    public static string UnresolvedVariableDuringMirLowering(string name) =>
        Format(nameof(UnresolvedVariableDuringMirLowering), name);

    public static string UnresolvedVariableLabel => Get(nameof(UnresolvedVariableLabel));

    public static string UnresolvedVariableReason(string name) =>
        Format(nameof(UnresolvedVariableReason), name);

    public static string BlockedModuleValueReferenceReason(string name) =>
        Format(nameof(BlockedModuleValueReferenceReason), name);

    public static string CapturedVariableResolutionFailed(string captureName) =>
        Format(nameof(CapturedVariableResolutionFailed), captureName);

    public static string CaptureTypeInferenceFailed(string captureName) =>
        Format(nameof(CaptureTypeInferenceFailed), captureName);

    public static string CapturedLambdaLabel => Get(nameof(CapturedLambdaLabel));

    public static string RefutableLetPatternUnsupportedInMir =>
        Get(nameof(RefutableLetPatternUnsupportedInMir));

    public static string RefutableLetPatternLabel => Get(nameof(RefutableLetPatternLabel));

    public static string ResumeOutsideHandlerBranch => Get(nameof(ResumeOutsideHandlerBranch));

    public static string ResumeContinuationAlreadyConsumed => Get(nameof(ResumeContinuationAlreadyConsumed));

    public static string EmptyHandlerWarning(string abilityName) =>
        Format(nameof(EmptyHandlerWarning), abilityName);

    public static string IncompleteHandlerWarning(string abilityName, string missingOperations) =>
        Format(nameof(IncompleteHandlerWarning), abilityName, missingOperations);

    public static string FailedCapturedLambdaLoweringReason =>
        Get(nameof(FailedCapturedLambdaLoweringReason));

    public static string BlockContainsPoisonedStatementReason =>
        Get(nameof(BlockContainsPoisonedStatementReason));

    public static string UnsupportedHirDeclarationReason(string declarationType) =>
        Format(nameof(UnsupportedHirDeclarationReason), declarationType);

    public static string FailedRecursiveClosureGroupEnvironmentReason =>
        Get(nameof(FailedRecursiveClosureGroupEnvironmentReason));

    public static string MissingMirTypeForReadValueReason =>
        Get(nameof(MissingMirTypeForReadValueReason));

    public static string MissingMirTypeForCallArgumentReason =>
        Get(nameof(MissingMirTypeForCallArgumentReason));

    public static string MissingMirTypeForForcedCopyCallArgumentReason =>
        Get(nameof(MissingMirTypeForForcedCopyCallArgumentReason));

    public static string MissingMirTypeForProjectedCallArgumentReason =>
        Get(nameof(MissingMirTypeForProjectedCallArgumentReason));

    public static string MissingMirTypeForReadonlyStringArgumentReason =>
        Get(nameof(MissingMirTypeForReadonlyStringArgumentReason));

    public static string MissingMirTypeForInitializationReason =>
        Get(nameof(MissingMirTypeForInitializationReason));

    public static string MissingMirTypeForStoreValueReason =>
        Get(nameof(MissingMirTypeForStoreValueReason));

    public static string MirTerminatorRole => Get(nameof(MirTerminatorRole));

    public static string HirFallbackLabel => Get(nameof(HirFallbackLabel));

    public static string ProofEqualitySidesContext(string proofName) =>
        Format(nameof(ProofEqualitySidesContext), proofName);

    public static string ParserDidNotProduceAst => Get(nameof(ParserDidNotProduceAst));

    public static string ParsePhaseIncomplete => Get(nameof(ParsePhaseIncomplete));

    public static string NameResolutionIncomplete => Get(nameof(NameResolutionIncomplete));

    public static string FfiTypeValidationFailed => Get(nameof(FfiTypeValidationFailed));

    public static string DependencyPhaseIncomplete => Get(nameof(DependencyPhaseIncomplete));

    public static string HirBuilderReportedErrors => Get(nameof(HirBuilderReportedErrors));

    public static string MirOptimizationSkippedDueToLoweringErrors =>
        Get(nameof(MirOptimizationSkippedDueToLoweringErrors));

    public static string MirOptimizationDisabledByOption =>
        Get(nameof(MirOptimizationDisabledByOption));

    public static string IdeBuiltinIntDocumentation => Get(nameof(IdeBuiltinIntDocumentation));

    public static string IdeBuiltinFloatDocumentation => Get(nameof(IdeBuiltinFloatDocumentation));

    public static string IdeBuiltinBoolDocumentation => Get(nameof(IdeBuiltinBoolDocumentation));

    public static string IdeBuiltinStringDocumentation => Get(nameof(IdeBuiltinStringDocumentation));

    public static string IdeBuiltinCharDocumentation => Get(nameof(IdeBuiltinCharDocumentation));

    public static string IdeBuiltinUnitDocumentation => Get(nameof(IdeBuiltinUnitDocumentation));

    public static string IdeBuiltinNeverDocumentation => Get(nameof(IdeBuiltinNeverDocumentation));

    public static string IdeModulePathDetail => Get(nameof(IdeModulePathDetail));

    public static string IdeModulePathDocumentation(string modulePath) =>
        Format(nameof(IdeModulePathDocumentation), modulePath);

    public static string IdeQualifiedPathDetail => Get(nameof(IdeQualifiedPathDetail));

    public static string IdeQualifiedSymbolDetail(string detail) =>
        Format(nameof(IdeQualifiedSymbolDetail), detail);

    public static string IdeKeywordDocumentation => Get(nameof(IdeKeywordDocumentation));

    public static string IdeTraitSelfDocumentation => Get(nameof(IdeTraitSelfDocumentation));

    public static string IdeKeywordDetail => Get(nameof(IdeKeywordDetail));

    public static string IdeAttributeDetail => Get(nameof(IdeAttributeDetail));

    public static string IdeAttributeDocumentation => Get(nameof(IdeAttributeDocumentation));

    public static string IdeDeriveTraitDetail => Get(nameof(IdeDeriveTraitDetail));

    public static string IdeDeriveTraitDocumentation => Get(nameof(IdeDeriveTraitDocumentation));

    public static string IdeOutlineDetailDeclaration => Get(nameof(IdeOutlineDetailDeclaration));

    public static string IdeSymbolDetailImport => Get(nameof(IdeSymbolDetailImport));

    public static string IdeSymbolDetailOperator => Get(nameof(IdeSymbolDetailOperator));

    public static string IdeSymbolDetailFunction => Get(nameof(IdeSymbolDetailFunction));

    public static string IdeSymbolDetailParameter => Get(nameof(IdeSymbolDetailParameter));

    public static string IdeSymbolDetailPatternBinding => Get(nameof(IdeSymbolDetailPatternBinding));

    public static string IdeSymbolDetailMutableVariable => Get(nameof(IdeSymbolDetailMutableVariable));

    public static string IdeSymbolDetailValue => Get(nameof(IdeSymbolDetailValue));

    public static string IdeSymbolDetailConstructor => Get(nameof(IdeSymbolDetailConstructor));

    public static string IdeSymbolDetailTypeParameter => Get(nameof(IdeSymbolDetailTypeParameter));

    public static string IdeSymbolDetailField => Get(nameof(IdeSymbolDetailField));

    public static string IdeSymbolDetailTraitImpl => Get(nameof(IdeSymbolDetailTraitImpl));

    public static string IdeFunctionDocumentation(string name) =>
        Format(nameof(IdeFunctionDocumentation), name);

    public static string IdeTypeDocumentation(string name) =>
        Format(nameof(IdeTypeDocumentation), name);

    public static string IdeConstructorDocumentation(string name) =>
        Format(nameof(IdeConstructorDocumentation), name);

    public static string IdeTraitDocumentation(string name) =>
        Format(nameof(IdeTraitDocumentation), name);

    public static string IdeEffectDocumentation(string name) =>
        Format(nameof(IdeEffectDocumentation), name);

    public static string IdeProofDocumentation(string name) =>
        Format(nameof(IdeProofDocumentation), name);

    public static string IdeTypeParameterDocumentation(string name) =>
        Format(nameof(IdeTypeParameterDocumentation), name);

    public static string IdeModuleDocumentation(string name) =>
        Format(nameof(IdeModuleDocumentation), name);

    public static string IdeFieldDocumentation(string name) =>
        Format(nameof(IdeFieldDocumentation), name);

    public static string IdeTraitImplementationDocumentation(string name) =>
        Format(nameof(IdeTraitImplementationDocumentation), name);

    public static string IdeValueDocumentation(string name) =>
        Format(nameof(IdeValueDocumentation), name);

    public static string IdeSymbolDocumentation(string kind, string name) =>
        Format(nameof(IdeSymbolDocumentation), kind, name);

    private static string Get(string name) =>
        Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Format(string name, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(name), args);
}
