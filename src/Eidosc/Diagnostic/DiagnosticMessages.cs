using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Eidosc.Diagnostic;

internal static partial class DiagnosticMessages
{
    private static readonly ResourceManager Resources = new(
        "Eidosc.Diagnostic.DiagnosticResources",
        Assembly.GetExecutingAssembly());

    public static string BinaryExpressionRequiresLeftOperand => Get(nameof(BinaryExpressionRequiresLeftOperand));

    public static string BinaryExpressionRequiresRightOperand => Get(nameof(BinaryExpressionRequiresRightOperand));

    public static string DiagnosticMemoryFilePath => Get(nameof(DiagnosticMemoryFilePath));

    public static string DiagnosticSuggestionHelpUrl(string helpUrl) =>
        Format(nameof(DiagnosticSuggestionHelpUrl), helpUrl);

    public static string OpenDynamicTypeReachedLlvmLowering(string typeKey, TypeId typeId) =>
        Format(nameof(OpenDynamicTypeReachedLlvmLowering), typeKey, typeId);

    public static string DiagnosticLevelError => Get(nameof(DiagnosticLevelError));

    public static string DiagnosticLevelWarning => Get(nameof(DiagnosticLevelWarning));

    public static string DiagnosticLevelInfo => Get(nameof(DiagnosticLevelInfo));

    public static string DiagnosticLevelNote => Get(nameof(DiagnosticLevelNote));

    public static string DiagnosticLevelHelp => Get(nameof(DiagnosticLevelHelp));

    public static string DiagnosticLevelLabel(DiagnosticLevel level) => level switch
    {
        DiagnosticLevel.Error => DiagnosticLevelError,
        DiagnosticLevel.Warning => DiagnosticLevelWarning,
        DiagnosticLevel.Info => DiagnosticLevelInfo,
        DiagnosticLevel.Note => DiagnosticLevelNote,
        DiagnosticLevel.Help => DiagnosticLevelHelp,
        _ => level.ToString().ToLowerInvariant()
    };

    public static string DoBindingRequiresValueExpression => Get(nameof(DoBindingRequiresValueExpression));

    public static string DoBindRequiresPattern => Get(nameof(DoBindRequiresPattern));

    public static string DoLetBindingRequiresVariableName => Get(nameof(DoLetBindingRequiresVariableName));

    public static string ListComprehensionGeneratorRequiresPattern => Get(nameof(ListComprehensionGeneratorRequiresPattern));

    public static string ListComprehensionGeneratorRequiresSourceExpression => Get(nameof(ListComprehensionGeneratorRequiresSourceExpression));

    public static string ListComprehensionGuardRequiresExpression => Get(nameof(ListComprehensionGuardRequiresExpression));

    public static string ListComprehensionRequiresOutputExpression => Get(nameof(ListComprehensionRequiresOutputExpression));

    public static string ListComprehensionMissingGeneratorPatternLabel =>
        Get(nameof(ListComprehensionMissingGeneratorPatternLabel));

    public static string ListComprehensionMissingGeneratorSourceLabel =>
        Get(nameof(ListComprehensionMissingGeneratorSourceLabel));

    public static string ListComprehensionMissingGuardExpressionLabel =>
        Get(nameof(ListComprehensionMissingGuardExpressionLabel));

    public static string LoopExpressionRequiresBody => Get(nameof(LoopExpressionRequiresBody));

    public static string MethodCallMissingMethodName => Get(nameof(MethodCallMissingMethodName));

    public static string MissingIndexExpression => Get(nameof(MissingIndexExpression));

    public static string MissingIndexedObject => Get(nameof(MissingIndexedObject));

    public static string AssignmentRequiresValueExpression => Get(nameof(AssignmentRequiresValueExpression));

    public static string ArrowTypeRequiresParameterType => Get(nameof(ArrowTypeRequiresParameterType));

    public static string ArrowTypeRequiresReturnType => Get(nameof(ArrowTypeRequiresReturnType));

    public static string CallExpressionMissingTarget => Get(nameof(CallExpressionMissingTarget));

    public static string IfExpressionMissingCondition => Get(nameof(IfExpressionMissingCondition));

    public static string IfExpressionRequiresThenBranch => Get(nameof(IfExpressionRequiresThenBranch));

    public static string IfLetExpressionMissingPattern => Get(nameof(IfLetExpressionMissingPattern));

    public static string IfLetExpressionRequiresThenBranch => Get(nameof(IfLetExpressionRequiresThenBranch));

    public static string InfixCallRequiresLeftOperand => Get(nameof(InfixCallRequiresLeftOperand));

    public static string InfixCallRequiresRightOperand => Get(nameof(InfixCallRequiresRightOperand));

    public static string LambdaExpressionRequiresBody => Get(nameof(LambdaExpressionRequiresBody));

    public static string MatchExpressionRequiresBranch => Get(nameof(MatchExpressionRequiresBranch));

    public static string PatternBranchRequiresBodyExpression => Get(nameof(PatternBranchRequiresBodyExpression));

    public static string PatternGuardRequiresPattern => Get(nameof(PatternGuardRequiresPattern));

    public static string PatternGuardRequiresSourceExpression => Get(nameof(PatternGuardRequiresSourceExpression));

    public static string ProofPropositionsMustBePure => Get(nameof(ProofPropositionsMustBePure));

    public static string ReturnExpressionOutsideFunction => Get(nameof(ReturnExpressionOutsideFunction));

    public static string UnaryExpressionRequiresOperand => Get(nameof(UnaryExpressionRequiresOperand));

    public static string WhileLetExpressionMissingPattern => Get(nameof(WhileLetExpressionMissingPattern));

    public static string WhileLetExpressionRequiresBody => Get(nameof(WhileLetExpressionRequiresBody));

    public static string AndPatternRequiresAtLeastTwoConjuncts => Get(nameof(AndPatternRequiresAtLeastTwoConjuncts));

    public static string AsPatternRequiresBindingName => Get(nameof(AsPatternRequiresBindingName));

    public static string FieldInitializerRequiresFieldName => Get(nameof(FieldInitializerRequiresFieldName));

    public static string NotPatternMissingInnerPattern => Get(nameof(NotPatternMissingInnerPattern));

    public static string NotPatternMissingInnerLabel => Get(nameof(NotPatternMissingInnerLabel));

    public static string OrPatternRequiresAtLeastTwoAlternatives => Get(nameof(OrPatternRequiresAtLeastTwoAlternatives));

    public static string RecordUpdateCouldNotResolveBaseAdt => Get(nameof(RecordUpdateCouldNotResolveBaseAdt));

    public static string RecordUpdateCannotMixPositionalArgumentsWithBase => Get(nameof(RecordUpdateCannotMixPositionalArgumentsWithBase));

    public static string RecordUpdateRequiresStableBasePlace => Get(nameof(RecordUpdateRequiresStableBasePlace));

    public static string RecordUpdateShorthandMissingBaseExpression => Get(nameof(RecordUpdateShorthandMissingBaseExpression));

    public static string RecordUpdateSpreadRequiresStableBasePlace => Get(nameof(RecordUpdateSpreadRequiresStableBasePlace));

    public static string ViewPatternMissingInnerPattern => Get(nameof(ViewPatternMissingInnerPattern));

    public static string ViewPatternMissingViewExpression => Get(nameof(ViewPatternMissingViewExpression));

    public static string ViewPatternMissingViewExpressionLabel =>
        Get(nameof(ViewPatternMissingViewExpressionLabel));

    public static string ViewPatternMissingInnerPatternLabel =>
        Get(nameof(ViewPatternMissingInnerPatternLabel));

    public static string BreakExpressionOutsideLoop => Get(nameof(BreakExpressionOutsideLoop));

    public static string CfnCallFirstArgumentNotCfn => Get(nameof(CfnCallFirstArgumentNotCfn));

    public static string CfnFromArgumentNotFunction => Get(nameof(CfnFromArgumentNotFunction));

    public static string CfnFromCapturedClosureUnsupported =>
        Get(nameof(CfnFromCapturedClosureUnsupported));

    public static string ContinueExpressionOutsideLoop => Get(nameof(ContinueExpressionOutsideLoop));

    public static string HandlerBranchMissingOperationName => Get(nameof(HandlerBranchMissingOperationName));

    public static string ExplicitTypeApplicationCannotMixWithIndexExpression => Get(nameof(ExplicitTypeApplicationCannotMixWithIndexExpression));

    public static string EffectfulTypeRequiresInputType => Get(nameof(EffectfulTypeRequiresInputType));

    public static string ExplicitTypeApplicationMissingTarget => Get(nameof(ExplicitTypeApplicationMissingTarget));

    public static string ExplicitTypeApplicationRequiresNamedPolymorphicValue => Get(nameof(ExplicitTypeApplicationRequiresNamedPolymorphicValue));

    public static string InvalidRangeOrderingLabel => Get(nameof(InvalidRangeOrderingLabel));

    public static string InvalidRangePatternLabel => Get(nameof(InvalidRangePatternLabel));

    public static string MissingRangeEndBoundaryLiteralNote => Get(nameof(MissingRangeEndBoundaryLiteralNote));

    public static string MissingRangeStartBoundaryLiteralNote => Get(nameof(MissingRangeStartBoundaryLiteralNote));

    public static string ParsedEndBoundaryLabel => Get(nameof(ParsedEndBoundaryLabel));

    public static string ParsedStartBoundaryLabel => Get(nameof(ParsedStartBoundaryLabel));

    public static string RangeEndBoundaryLabel => Get(nameof(RangeEndBoundaryLabel));

    public static string RangePatternRequiresStartAndEndLiterals => Get(nameof(RangePatternRequiresStartAndEndLiterals));

    public static string RangePatternTypeMismatchLabel => Get(nameof(RangePatternTypeMismatchLabel));

    public static string RangeOrderingCheckNote => Get(nameof(RangeOrderingCheckNote));

    public static string RangeStartBoundaryLabel => Get(nameof(RangeStartBoundaryLabel));

    public static string RangeStartMustBeLessThanOrEqualToEnd => Get(nameof(RangeStartMustBeLessThanOrEqualToEnd));

    public static string RangePatternSupportsOnlyIntAndCharNote => Get(nameof(RangePatternSupportsOnlyIntAndCharNote));

    public static string AsPatternBindingLabel => Get(nameof(AsPatternBindingLabel));

    public static string AsPatternInnerPatternLabel => Get(nameof(AsPatternInnerPatternLabel));

    public static string AsPatternRequiresInnerTypeMatchNote => Get(nameof(AsPatternRequiresInnerTypeMatchNote));

    public static string ViewExpressionIsNotCallable => Get(nameof(ViewExpressionIsNotCallable));

    public static string ViewExpressionLabel => Get(nameof(ViewExpressionLabel));

    public static string ViewPatternLabel => Get(nameof(ViewPatternLabel));

    public static string ViewPatternCallableNote => Get(nameof(ViewPatternCallableNote));

    public static string ConstraintSolvingStoppedBeforeAllConstraintsChecked =>
        Get(nameof(ConstraintSolvingStoppedBeforeAllConstraintsChecked));

    public static string BorrowRequiresCapabilityTag => Get(nameof(BorrowRequiresCapabilityTag));

    public static string IfLetExpressionRequiresBindingPattern => Get(nameof(IfLetExpressionRequiresBindingPattern));

    public static string ImportError => Get(nameof(ImportError));

    public static string ImportModuleAliasMustStartWithUppercase(string alias) =>
        Format(nameof(ImportModuleAliasMustStartWithUppercase), alias);

    public static string SelectiveImportAliasTierMismatch(string alias, string symbolName, string expectedTier) =>
        Format(nameof(SelectiveImportAliasTierMismatch), alias, symbolName, expectedTier);

    public static string ImplRequiresConcreteFirstParameter => Get(nameof(ImplRequiresConcreteFirstParameter));

    public static string GuardedBranchesNotExhaustiveNote => Get(nameof(GuardedBranchesNotExhaustiveNote));

    public static string LetDeclarationRequiresBindingPattern => Get(nameof(LetDeclarationRequiresBindingPattern));

    public static string LetDeclarationRequiresIrrefutablePattern => Get(nameof(LetDeclarationRequiresIrrefutablePattern));

    public static string LetQuestionRequiresBindingPattern => Get(nameof(LetQuestionRequiresBindingPattern));

    public static string LetQuestionRequiresIrrefutablePattern => Get(nameof(LetQuestionRequiresIrrefutablePattern));

    public static string LetQuestionRequiresValueExpression => Get(nameof(LetQuestionRequiresValueExpression));

    public static string LetQuestionOutsideFunction => Get(nameof(LetQuestionOutsideFunction));

    public static string LetQuestionRhsMustBeOptionOrResult => Get(nameof(LetQuestionRhsMustBeOptionOrResult));

    public static string ComptimeBindingRhsMustBeEvaluable(string reason) =>
        Format(nameof(ComptimeBindingRhsMustBeEvaluable), reason);

    public static string LetQuestionOptionRequiresOptionReturn => Get(nameof(LetQuestionOptionRequiresOptionReturn));

    public static string LetQuestionResultRequiresResultReturn => Get(nameof(LetQuestionResultRequiresResultReturn));

    public static string LetQuestionMissingConstructor(string typeName, string constructorName) =>
        Format(nameof(LetQuestionMissingConstructor), typeName, constructorName);

    public static string MethodCallMissingMethodNameLabel => Get(nameof(MethodCallMissingMethodNameLabel));

    public static string NamedFieldPatternRequiresFieldName => Get(nameof(NamedFieldPatternRequiresFieldName));

    public static string PatternCoverageLabel => Get(nameof(PatternCoverageLabel));

    public static string PatternGuardRequiresBindingPattern => Get(nameof(PatternGuardRequiresBindingPattern));

    public static string ProofImplRequiresConcreteFirstTraitArgument => Get(nameof(ProofImplRequiresConcreteFirstTraitArgument));

    public static string UnsupportedConstrainedImplHead => Get(nameof(UnsupportedConstrainedImplHead));

    public static string UnreachablePatternBranchLabel => Get(nameof(UnreachablePatternBranchLabel));

    public static string UnreachablePatternCannotMatchNote => Get(nameof(UnreachablePatternCannotMatchNote));

    public static string UnreachablePatternMoveEarlierOrAddGuardNote => Get(nameof(UnreachablePatternMoveEarlierOrAddGuardNote));

    public static string UnreachablePatternMoveEarlierOrRefineNote => Get(nameof(UnreachablePatternMoveEarlierOrRefineNote));

    public static string UnreachablePatternRemoveOrChangeGuardNote => Get(nameof(UnreachablePatternRemoveOrChangeGuardNote));

    public static string WhileLetExpressionRequiresBindingPattern => Get(nameof(WhileLetExpressionRequiresBindingPattern));

    public static string AndPatternConjunctTypeMismatch => Get(nameof(AndPatternConjunctTypeMismatch));

    public static string AppendOperandsMustHaveSameType => Get(nameof(AppendOperandsMustHaveSameType));

    public static string ApplicativeApplyLeftOperandTypeMismatch => Get(nameof(ApplicativeApplyLeftOperandTypeMismatch));

    public static string ApplicativeApplyRightOperandTypeMismatch => Get(nameof(ApplicativeApplyRightOperandTypeMismatch));

    public static string ArithmeticOperandTypeMismatch => Get(nameof(ArithmeticOperandTypeMismatch));

    public static string AssignmentTypeMismatch => Get(nameof(AssignmentTypeMismatch));

    public static string BindLeftOperandTypeMismatch => Get(nameof(BindLeftOperandTypeMismatch));

    public static string BindRightOperandMustReturnSameMonadicType => Get(nameof(BindRightOperandMustReturnSameMonadicType));

    public static string CallArgumentTypeMismatch => Get(nameof(CallArgumentTypeMismatch));

    public static string CallTargetIsNotCallable => Get(nameof(CallTargetIsNotCallable));

    public static string CallTargetIsNotZeroArgumentFunction => Get(nameof(CallTargetIsNotZeroArgumentFunction));

    public static string CoalesceLeftOperandMustBeOption => Get(nameof(CoalesceLeftOperandMustBeOption));

    public static string ComparisonOperandTypeMismatch => Get(nameof(ComparisonOperandTypeMismatch));

    public static string ComposeLeftOperandMustBeCallable => Get(nameof(ComposeLeftOperandMustBeCallable));

    public static string ComposeRightOperandMustBeCallable => Get(nameof(ComposeRightOperandMustBeCallable));

    public static string PrependRightOperandMustBeList => Get(nameof(PrependRightOperandMustBeList));

    public static string AppendLastLeftOperandMustBeList => Get(nameof(AppendLastLeftOperandMustBeList));

    public static string ConstructorPatternTypeMismatch => Get(nameof(ConstructorPatternTypeMismatch));

    public static string FmapLeftOperandMustBeCallable => Get(nameof(FmapLeftOperandMustBeCallable));

    public static string FmapRightOperandTypeMismatch => Get(nameof(FmapRightOperandTypeMismatch));

    public static string HandlerBranchResultTypeMismatch => Get(nameof(HandlerBranchResultTypeMismatch));

    public static string IfBranchTypeMismatch => Get(nameof(IfBranchTypeMismatch));

    public static string IfConditionMustBeBool => Get(nameof(IfConditionMustBeBool));

    public static string IfLetBranchTypeMismatch => Get(nameof(IfLetBranchTypeMismatch));

    public static string IfLetPatternTypeMismatch => Get(nameof(IfLetPatternTypeMismatch));

    public static string IndexedObjectMustBeList => Get(nameof(IndexedObjectMustBeList));

    public static string IndexExpressionMustBeInt => Get(nameof(IndexExpressionMustBeInt));

    public static string InlineHandlerResultTypeMismatch => Get(nameof(InlineHandlerResultTypeMismatch));

    public static string LambdaBodyResultTypeMismatch => Get(nameof(LambdaBodyResultTypeMismatch));

    public static string LetPatternTypeMismatch => Get(nameof(LetPatternTypeMismatch));

    public static string ListComprehensionGeneratorMustIterateList => Get(nameof(ListComprehensionGeneratorMustIterateList));

    public static string ListComprehensionGeneratorPatternTypeMismatch => Get(nameof(ListComprehensionGeneratorPatternTypeMismatch));

    public static string ListComprehensionGuardMustBeBool => Get(nameof(ListComprehensionGuardMustBeBool));

    public static string ListElementTypeMismatch => Get(nameof(ListElementTypeMismatch));

    public static string ListPatternElementTypeMismatch => Get(nameof(ListPatternElementTypeMismatch));

    public static string ListPatternExpectedTypeMismatch => Get(nameof(ListPatternExpectedTypeMismatch));

    public static string ListPatternRestBindingTypeMismatch => Get(nameof(ListPatternRestBindingTypeMismatch));

    public static string ListPrefixElementTypeMismatch => Get(nameof(ListPrefixElementTypeMismatch));

    public static string ListRestExpressionTypeMismatch => Get(nameof(ListRestExpressionTypeMismatch));

    public static string LiteralPatternTypeMismatch => Get(nameof(LiteralPatternTypeMismatch));

    public static string LogicalLeftOperandMustBeBool => Get(nameof(LogicalLeftOperandMustBeBool));

    public static string LogicalNegationOperandMustBeBool => Get(nameof(LogicalNegationOperandMustBeBool));

    public static string LogicalRightOperandMustBeBool => Get(nameof(LogicalRightOperandMustBeBool));

    public static string MatchBranchTypeMismatch => Get(nameof(MatchBranchTypeMismatch));

    public static string NotPatternInnerTypeMismatch => Get(nameof(NotPatternInnerTypeMismatch));

    public static string OrPatternAlternativeTypeMismatch => Get(nameof(OrPatternAlternativeTypeMismatch));

    public static string OrPatternExpectedTypeMismatch => Get(nameof(OrPatternExpectedTypeMismatch));

    public static string PatternBranchGuardMustBeBool => Get(nameof(PatternBranchGuardMustBeBool));

    public static string PatternBranchResultTypeMismatch => Get(nameof(PatternBranchResultTypeMismatch));

    public static string PatternGuardSourceTypeMismatch => Get(nameof(PatternGuardSourceTypeMismatch));

    public static string RangePatternEndTypeMismatch => Get(nameof(RangePatternEndTypeMismatch));

    public static string RangePatternStartTypeMismatch => Get(nameof(RangePatternStartTypeMismatch));

    public static string RecordUpdateSpreadBaseTypeMismatch => Get(nameof(RecordUpdateSpreadBaseTypeMismatch));

    public static string ReturnValueTypeMismatch => Get(nameof(ReturnValueTypeMismatch));

    public static string SequentialGuardMustBeBool => Get(nameof(SequentialGuardMustBeBool));

    public static string StringConcatenationLeftOperandMustBeString => Get(nameof(StringConcatenationLeftOperandMustBeString));

    public static string StringConcatenationRightOperandMustBeString => Get(nameof(StringConcatenationRightOperandMustBeString));

    public static string TuplePatternTypeMismatch => Get(nameof(TuplePatternTypeMismatch));

    public static string ViewPatternInnerTypeMismatch => Get(nameof(ViewPatternInnerTypeMismatch));

    public static string WhileLetPatternTypeMismatch => Get(nameof(WhileLetPatternTypeMismatch));

    public static string FunctionBodyResultTypeMismatch(string functionName) =>
        Format(nameof(FunctionBodyResultTypeMismatch), functionName);

    public static string HandlerResultTypeMismatch(string handlerName) =>
        Format(nameof(HandlerResultTypeMismatch), handlerName);

    public static string ValueTypeMismatch(string name) =>
        Format(nameof(ValueTypeMismatch), name);

    public static string VariableTypeMismatch(string name) =>
        Format(nameof(VariableTypeMismatch), name);


    public static string DeclarationRequiresInitializer(string declarationKind, string displayName) =>
        Format(nameof(DeclarationRequiresInitializer), declarationKind, displayName);

    public static string CannotAssignToImmutableVariable(string name) =>
        Format(nameof(CannotAssignToImmutableVariable), name);

    public static string CannotAssignThroughImmutableBinding(string name) =>
        $"cannot assign through immutable binding '{name}'";

    public static string CannotAssignThroughImmutableParameter(string name) =>
        $"cannot assign through immutable parameter '{name}'";

    public static string CannotAssignThroughImmutableParameterHelp(string name) =>
        $"make the parameter mutable: `mut {name} => ...`";

    public static string CannotAssignThroughImmutableBindingHelp(string name) =>
        $"declare the binding as mutable: `mut {name} := ...`";

    public static string ConservativelySuppressedCoveredWarnings(string traces, string reason) =>
        Format(nameof(ConservativelySuppressedCoveredWarnings), traces, reason);

    public static string CoveredCaseLowerBoundTraces(string traces) =>
        Format(nameof(CoveredCaseLowerBoundTraces), traces);

    public static string CoveredCaseTraces(string traces) =>
        Format(nameof(CoveredCaseTraces), traces);

    public static string CoveredCaseWitnesses(string witnesses) =>
        Format(nameof(CoveredCaseWitnesses), witnesses);

    public static string CStructFieldMissingName(string typeName, int fieldIndex) =>
        Format(nameof(CStructFieldMissingName), typeName, fieldIndex);

    public static string CStructTypeDoesNotSupportConstructorVariants(string typeName) =>
        Format(nameof(CStructTypeDoesNotSupportConstructorVariants), typeName);

    public static string CStructTypeDoesNotSupportTypeParameters(string typeName) =>
        Format(nameof(CStructTypeDoesNotSupportTypeParameters), typeName);

    public static string CStructTypeRequiresAtLeastOneField(string typeName) =>
        Format(nameof(CStructTypeRequiresAtLeastOneField), typeName);

    public static string CStructFieldTypeMustBeSimpleTypePath(string typeName, string fieldName) =>
        Format(nameof(CStructFieldTypeMustBeSimpleTypePath), typeName, fieldName);

    public static string CStructFieldTypeNotFfiSafe(string typeName, string fieldName, string fieldType) =>
        Format(nameof(CStructFieldTypeNotFfiSafe), typeName, fieldName, fieldType);

    public static string ConstructorPatternDisallowsNamedForPositionalForm(string constructorName) =>
        Format(nameof(ConstructorPatternDisallowsNamedForPositionalForm), constructorName);

    public static string ConstructorPatternDisallowsPositionalForNamedForm(string constructorName) =>
        Format(nameof(ConstructorPatternDisallowsPositionalForNamedForm), constructorName);

    public static string ConstructorPatternExpectsPositionalCount(string constructorName, int expectedCount, int actualCount) =>
        Format(nameof(ConstructorPatternExpectsPositionalCount), constructorName, expectedCount, actualCount);

    public static string ConstructorPatternHasNoNamedField(string constructorName, string fieldName) =>
        Format(nameof(ConstructorPatternHasNoNamedField), constructorName, fieldName);

    public static string DeriveTypeHasNoConstructors(string traitName, string typeName) =>
        Format(nameof(DeriveTypeHasNoConstructors), traitName, typeName);

    public static string DeriveUnsupportedTrait(string traitName) =>
        Format(nameof(DeriveUnsupportedTrait), traitName);

    public static string ConstructorConstantDuplicate(string constructorName, string constantName) =>
        $"constructor '{constructorName}' declares associated constant '{constantName}' more than once";

    public static string ConstructorConstantMissingForDerivedTrait(
        string constructorName,
        string constantName,
        string traitName) =>
        $"constructor '{constructorName}' must provide associated constant '{constantName}' for @derive({traitName})";

    public static string ConstructorConstantUnused(string constructorName, string constantName) =>
        $"constructor associated constant '{constructorName}.{constantName}' is not consumed by any @derive trait";

    public static string DeriveTraitMethodUnsupported(string traitName, string methodName) =>
        $"@derive({traitName}) only supports trait method '{methodName}' when it has signature Self -> R";

    public static string ConstructorConstantExpressionUnsupported(string constructorName, string constantName) =>
        $"constructor associated constant '{constructorName}.{constantName}' must be a derive-supported expression";

    public static string DisplayNameIsNotEffect(string displayName) =>
        Format(nameof(DisplayNameIsNotEffect), displayName);

    public static string DisplayNameIsNotTrait(string displayName) =>
        Format(nameof(DisplayNameIsNotTrait), displayName);

    public static string DuplicateNamedFieldInConstructorPattern(string fieldName, string constructorName) =>
        Format(nameof(DuplicateNamedFieldInConstructorPattern), fieldName, constructorName);

    public static string DuplicateExportedName(string name, string moduleName) =>
        Format(nameof(DuplicateExportedName), name, moduleName);

    public static string FfiFunctionCannotHaveBody(string functionName) =>
        Format(nameof(FfiFunctionCannotHaveBody), functionName);

    public static string IntrinsicFunctionCannotHaveBody(string functionName) =>
        Format(nameof(IntrinsicFunctionCannotHaveBody), functionName);

    public static string FfiDuplicateBinding(string library, string symbol) =>
        Format(nameof(FfiDuplicateBinding), library, symbol);

    public static string FfiLinkWithoutFunction(string library) =>
        Format(nameof(FfiLinkWithoutFunction), library);

    public static string FfiUndeclaredLibraryReference(string library, string symbol) =>
        Format(nameof(FfiUndeclaredLibraryReference), library, symbol);

    public static string FfiParameterRole => Get(nameof(FfiParameterRole));

    public static string FfiParameterLocation(int parameterIndex, string role) =>
        Format(nameof(FfiParameterLocation), parameterIndex, role);

    public static string FfiReturnRole => Get(nameof(FfiReturnRole));

    public static string FfiStringRequiresCstrConversionHelp =>
        Get(nameof(FfiStringRequiresCstrConversionHelp));

    public static string FfiTypeDisplayFunction => Get(nameof(FfiTypeDisplayFunction));

    public static string FfiTypeDisplayTuple => Get(nameof(FfiTypeDisplayTuple));

    public static string FfiTypeDisplayReference => Get(nameof(FfiTypeDisplayReference));

    public static string FfiTypeDisplayMutableReference => Get(nameof(FfiTypeDisplayMutableReference));

    public static string FfiTypeDisplayUnknown => Get(nameof(FfiTypeDisplayUnknown));

    public static string FfiUnsafeTypeForFunctionLocation(string typeName, string location) =>
        Format(nameof(FfiUnsafeTypeForFunctionLocation), typeName, location);

    public static string GuardedBranchesExcludedFromExactCoverage(string branches) =>
        Format(nameof(GuardedBranchesExcludedFromExactCoverage), branches);

    public static string MissingCaseTraceGroups(string traceGroups) =>
        Format(nameof(MissingCaseTraceGroups), traceGroups);

    public static string MissingCaseTraceKv(string traceKv) =>
        Format(nameof(MissingCaseTraceKv), traceKv);

    public static string MissingCaseTraces(string traces) =>
        Format(nameof(MissingCaseTraces), traces);

    public static string MissingCaseWitnesses(string witnesses) =>
        Format(nameof(MissingCaseWitnesses), witnesses);

    public static string NotPatternCannotBindVariables(string names) =>
        Format(nameof(NotPatternCannotBindVariables), names);

    public static string AndPatternConjunctsCannotBindSameVariableMoreThanOnce(string names) =>
        Format(nameof(AndPatternConjunctsCannotBindSameVariableMoreThanOnce), names);

    public static string AndPatternConjunctsCannotBindSameVariableMoreThanOnceWithDetails(string names, string details) =>
        Format(nameof(AndPatternConjunctsCannotBindSameVariableMoreThanOnceWithDetails), names, details);

    public static string NonExhaustivePatternMatchingAddWildcardBranch(string ownerDescription) =>
        Format(nameof(NonExhaustivePatternMatchingAddWildcardBranch), ownerDescription);

    public static string NonExhaustivePatternMatchingMissingAdtConstructors(string ownerDescription, string missingCases) =>
        Format(nameof(NonExhaustivePatternMatchingMissingAdtConstructors), ownerDescription, missingCases);

    public static string NonExhaustivePatternMatchingMissingBoolCases(string ownerDescription, string missingCases) =>
        Format(nameof(NonExhaustivePatternMatchingMissingBoolCases), ownerDescription, missingCases);

    public static string NonExhaustivePatternMatchingMissingListCases(string ownerDescription, string missingCases) =>
        Format(nameof(NonExhaustivePatternMatchingMissingListCases), ownerDescription, missingCases);

    public static string NonExhaustivePatternMatchingMissingTupleAdtCases(string ownerDescription, string missingCases) =>
        Format(nameof(NonExhaustivePatternMatchingMissingTupleAdtCases), ownerDescription, missingCases);

    public static string NonExhaustivePatternMatchingMissingTupleBoolCases(string ownerDescription, string missingCases) =>
        Format(nameof(NonExhaustivePatternMatchingMissingTupleBoolCases), ownerDescription, missingCases);

    public static string OperatorAlreadyDeclared(string symbol) =>
        Format(nameof(OperatorAlreadyDeclared), symbol);

    public static string OperatorPrecedenceMustBeInteger(string precedenceText) =>
        Format(nameof(OperatorPrecedenceMustBeInteger), precedenceText);

    public static string OperatorPrecedenceOutOfRange(int precedence) =>
        Format(nameof(OperatorPrecedenceOutOfRange), precedence);

    public static string OperatorUnsupportedFixity(string fixity) =>
        Format(nameof(OperatorUnsupportedFixity), fixity);

    public static string OrPatternAliasResolvesDifferentValueSlots(string aliasName) =>
        Format(nameof(OrPatternAliasResolvesDifferentValueSlots), aliasName);

    public static string OrPatternAlternativesMustBindSameValueSlots =>
        Get(nameof(OrPatternAlternativesMustBindSameValueSlots));

    public static string OrPatternAlternativesMustBindSameValueSlotsWithDetails(string details) =>
        Format(nameof(OrPatternAlternativesMustBindSameValueSlotsWithDetails), details);

    public static string OrPatternAlternativesMustUseSameBindingMode(string details) =>
        Format(nameof(OrPatternAlternativesMustUseSameBindingMode), details);

    public static string OrPatternAlternativesMustUseSameLegacyBindingMode(string details) =>
        Format(nameof(OrPatternAlternativesMustUseSameLegacyBindingMode), details);

    public static string OverlappingImplExistingCanonicalHead(string head) =>
        Format(nameof(OverlappingImplExistingCanonicalHead), head);

    public static string OverlappingImplExistingHead(string head) =>
        Format(nameof(OverlappingImplExistingHead), head);

    public static string OverlappingImplHelp =>
        Get(nameof(OverlappingImplHelp));

    public static string OverlappingImplRequestedCanonicalHead(string head) =>
        Format(nameof(OverlappingImplRequestedCanonicalHead), head);

    public static string OverlappingImplRequestedHead(string head) =>
        Format(nameof(OverlappingImplRequestedHead), head);

    public static string OverlappingImplRequestedHere(string head) =>
        Format(nameof(OverlappingImplRequestedHere), head);

    public static string OverlappingImplSpecializationRelation(string relation) =>
        Format(nameof(OverlappingImplSpecializationRelation), relation);

    public static string PatternDiagnosticWithContext(string message, string contextPath) =>
        Format(nameof(PatternDiagnosticWithContext), message, contextPath);

    public static string PatternVariableBoundMoreThanOnce(string name) =>
        Format(nameof(PatternVariableBoundMoreThanOnce), name);

    public static string ProofCaseIndexRequiresPattern(int caseIndex) =>
        Format(nameof(ProofCaseIndexRequiresPattern), caseIndex);

    public static string ProofImplMatchingDeclarationHelp =>
        Get(nameof(ProofImplMatchingDeclarationHelp));

    public static string PtrIntrinsicRequiresExactlyOneTypeArgument(string name) =>
        Format(nameof(PtrIntrinsicRequiresExactlyOneTypeArgument), name);

    public static string PtrIntrinsicRequiresExplicitTypeArgument(string name) =>
        Format(nameof(PtrIntrinsicRequiresExplicitTypeArgument), name);

    public static string ReservedSelfDeclaration(string declarationKind) =>
        Format(nameof(ReservedSelfDeclaration), declarationKind);

    public static string ReservedInternalNameDeclaration(string name, string prefix, string declarationKind) =>
        Format(nameof(ReservedInternalNameDeclaration), name, prefix, declarationKind);

    public static string ReservedInternalNameDeclarationHelp =>
        Get(nameof(ReservedInternalNameDeclarationHelp));

    public static string CyclicSupertrait(string traitName, string cyclePath) =>
        Format(nameof(CyclicSupertrait), traitName, cyclePath);

    public static string UndefinedSupertrait(string supertraitName, string traitName) =>
        Format(nameof(UndefinedSupertrait), supertraitName, traitName);

    public static string SupertraitTypeArgumentCountMismatch(string supertraitName, int expected, int actual, string traitName) =>
        Format(nameof(SupertraitTypeArgumentCountMismatch), supertraitName, expected, actual, traitName);

    public static string SelfReferentialSupertrait(string traitName) =>
        Format(nameof(SelfReferentialSupertrait), traitName);

    public static string DuplicateSupertrait(string supertraitName, string traitName) =>
        Format(nameof(DuplicateSupertrait), supertraitName, traitName);

    public static string SuppressedCoveredTraceKv(string traceKv) =>
        Format(nameof(SuppressedCoveredTraceKv), traceKv);

    public static string UndefinedEffect(string abilityName) =>
        Format(nameof(UndefinedEffect), abilityName);

    public static string UndefinedEffectWithCandidates(string abilityName, string shortName, string candidates) =>
        Format(nameof(UndefinedEffectWithCandidates), abilityName, shortName, candidates);

    public static string UndefinedConstructor(string name) =>
        Format(nameof(UndefinedConstructor), name);

    public static string UndefinedFunction(string name) =>
        Format(nameof(UndefinedFunction), name);

    public static string UndefinedHandler(string name) =>
        Format(nameof(UndefinedHandler), name);

    public static string UndefinedTrait(string name) =>
        Format(nameof(UndefinedTrait), name);

    public static string UndefinedTraitInImpl(string name) =>
        Format(nameof(UndefinedTraitInImpl), name);

    public static string UndefinedType(string name) =>
        Format(nameof(UndefinedType), name);

    public static string UndefinedVariable(string name) =>
        Format(nameof(UndefinedVariable), name);

    public static string UnreachablePatternBranchPreviousIrrefutable(int currentBranchIndex, int previousBranchIndex) =>
        Format(nameof(UnreachablePatternBranchPreviousIrrefutable), currentBranchIndex, previousBranchIndex);

    public static string UnreachablePatternBranchFalseGuard(int branchIndex) =>
        Format(nameof(UnreachablePatternBranchFalseGuard), branchIndex);

    public static string UnreachablePatternBranchUnsatisfiable(int branchIndex) =>
        Format(nameof(UnreachablePatternBranchUnsatisfiable), branchIndex);

    public static string UnreachablePatternBranchCoveredByPrevious(int branchIndex, string branchList) =>
        Format(nameof(UnreachablePatternBranchCoveredByPrevious), branchIndex, branchList);

    public static string UnresolvedGuardBranchHints(string hints) =>
        Format(nameof(UnresolvedGuardBranchHints), hints);

    public static string UnsupportedBorrowCapability(string capability) =>
        Format(nameof(UnsupportedBorrowCapability), capability);

    public static string UnsupportedPtrIntrinsicTypeArgument(string typeName, string name) =>
        Format(nameof(UnsupportedPtrIntrinsicTypeArgument), typeName, name);

    public static string AmbiguousImportedValueRequiresCallSiteTypeInfo(string name) =>
        Format(nameof(AmbiguousImportedValueRequiresCallSiteTypeInfo), name);

    public static string AmbiguousImportedValue(string name) =>
        Format(nameof(AmbiguousImportedValue), name);

    public static string AmbiguousImportedValueWithCandidates(string name, string candidates) =>
        Format(nameof(AmbiguousImportedValueWithCandidates), name, candidates);

    public static string AmbiguousCallableOverload(string name, string candidates) =>
        Format(nameof(AmbiguousCallableOverload), name, candidates);

    public static string DuplicateFunctionOverloadSignature(string name, string signature) =>
        Format(nameof(DuplicateFunctionOverloadSignature), name, signature);

    public static string FunctionOverloadConflictsWithValue(string name) =>
        Format(nameof(FunctionOverloadConflictsWithValue), name);

    public static string ValueConflictsWithFunctionOverload(string name) =>
        Format(nameof(ValueConflictsWithFunctionOverload), name);

    public static string AmbiguousModulePathWithCandidates(string modulePath, string candidates) =>
        Format(nameof(AmbiguousModulePathWithCandidates), modulePath, candidates);

    public static string AmbiguousPathWithCandidates(string path, string candidates) =>
        Format(nameof(AmbiguousPathWithCandidates), path, candidates);

    public static string AmbiguousEffect(string abilityName) =>
        Format(nameof(AmbiguousEffect), abilityName);

    public static string AmbiguousEffectWithCandidates(string abilityName, string candidates) =>
        Format(nameof(AmbiguousEffectWithCandidates), abilityName, candidates);

    public static string EffectAuthorizationAvailableNote(string available) =>
        Format(nameof(EffectAuthorizationAvailableNote), available);

    public static string EffectAuthorizationCalleeNote(string callee) =>
        Format(nameof(EffectAuthorizationCalleeNote), callee);

    public static string EffectAuthorizationCallerNote(string caller) =>
        Format(nameof(EffectAuthorizationCallerNote), caller);

    public static string EffectAuthorizationFailed(string caller, string callee) =>
        Format(nameof(EffectAuthorizationFailed), caller, callee);

    public static string EffectAuthorizationFailedLabel => Get(nameof(EffectAuthorizationFailedLabel));

    public static string EffectAuthorizationHelp => Get(nameof(EffectAuthorizationHelp));

    public static string EffectAuthorizationMissingNote(string missing) =>
        Format(nameof(EffectAuthorizationMissingNote), missing);

    public static string EffectAuthorizationRequiredNote(string required) =>
        Format(nameof(EffectAuthorizationRequiredNote), required);

    public static string EffectRequiredForOperationDescription(string abilityName, string operationName) =>
        Format(nameof(EffectRequiredForOperationDescription), abilityName, operationName);

    public static string EffectRequiredDescription(string abilityName) =>
        Format(nameof(EffectRequiredDescription), abilityName);

    public static string AmbiguousOverlappingImplRegistration =>
        Get(nameof(AmbiguousOverlappingImplRegistration));

    public static string ExistingOverlappingImplRegisteredHere =>
        Get(nameof(ExistingOverlappingImplRegisteredHere));

    public static string ImplSpecializationEquivalent =>
        Get(nameof(ImplSpecializationEquivalent));

    public static string ImplSpecializationIncomparable =>
        Get(nameof(ImplSpecializationIncomparable));

    public static string ImplSpecializationRequestedLessSpecific =>
        Get(nameof(ImplSpecializationRequestedLessSpecific));

    public static string ImplSpecializationRequestedMoreSpecific =>
        Get(nameof(ImplSpecializationRequestedMoreSpecific));

    public static string AmbiguousCStructFieldAccess(string fieldName, string structNames, string getterName) =>
        Format(nameof(AmbiguousCStructFieldAccess), fieldName, structNames, getterName);

    public static string CannotConstructAdtTypeMissingAdtSymbol(object ctorId) =>
        Format(nameof(CannotConstructAdtTypeMissingAdtSymbol), ctorId);

    public static string CannotConstructAdtTypeMissingTypeParameter(string adtName, string typeParameterName) =>
        Format(nameof(CannotConstructAdtTypeMissingTypeParameter), adtName, typeParameterName);

    public static string CannotDereferenceNonReferenceType(object type) =>
        Format(nameof(CannotDereferenceNonReferenceType), type);

    public static string CannotInferAdtTypeForConstructor(string constructorName) =>
        Format(nameof(CannotInferAdtTypeForConstructor), constructorName);

    public static string CannotInferCallableTypeForVariable(string name) =>
        Format(nameof(CannotInferCallableTypeForVariable), name);

    public static string CannotInferCallableTypeMissingSymbol(object symbolId) =>
        Format(nameof(CannotInferCallableTypeMissingSymbol), symbolId);

    public static string CannotInferIdentifierTypeMissingSymbol(string name) =>
        Format(nameof(CannotInferIdentifierTypeMissingSymbol), name);

    public static string CannotInferPathTypeMissingSymbol(string path) =>
        Format(nameof(CannotInferPathTypeMissingSymbol), path);

    public static string CannotInferVariablePathTypeUnavailable(string path) =>
        Format(nameof(CannotInferVariablePathTypeUnavailable), path);

    public static string CannotInferVariableTypeUnavailable(string name) =>
        Format(nameof(CannotInferVariableTypeUnavailable), name);

    public static string CannotResolvePath(string path) =>
        Format(nameof(CannotResolvePath), path);

    public static string EmptyPath => Get(nameof(EmptyPath));

    public static string CannotResolveImportedModulePath(string path) =>
        Format(nameof(CannotResolveImportedModulePath), path);

    public static string ImportedModulePathIsEmpty => Get(nameof(ImportedModulePathIsEmpty));

    public static string ModuleNotFound(string modulePath) =>
        Format(nameof(ModuleNotFound), modulePath);

    public static string UnknownImportKind => Get(nameof(UnknownImportKind));

    public static string SymbolNotFoundInModule(string symbolName, string modulePath) =>
        Format(nameof(SymbolNotFoundInModule), symbolName, modulePath);

    public static string SymbolNotFoundInImportedModule(string symbolName) =>
        Format(nameof(SymbolNotFoundInImportedModule), symbolName);

    public static string TypeHasNoConstructors(object typeId) =>
        Format(nameof(TypeHasNoConstructors), typeId);

    public static string ConstructorNotFound(string constructorName) =>
        Format(nameof(ConstructorNotFound), constructorName);

    public static string CannotUnifyTypes(object left, object right) =>
        Format(nameof(CannotUnifyTypes), left, right);

    public static string CannotUnifyTypeConstructors(object left, object right) =>
        Format(nameof(CannotUnifyTypeConstructors), left, right);

    public static string OccursCheckFailed(int variableIndex, object type) =>
        Format(nameof(OccursCheckFailed), variableIndex, type);

    public static string KindOccursCheckFailed(int variableIndex, string kindName) =>
        Format(nameof(KindOccursCheckFailed), variableIndex, kindName);

    public static string TypeArgumentCountMismatch(object left, int leftCount, object right, int rightCount) =>
        Format(nameof(TypeArgumentCountMismatch), left, leftCount, right, rightCount);

    public static string ExpectedTypeArgumentCount(int expectedCount, int actualCount) =>
        Format(nameof(ExpectedTypeArgumentCount), expectedCount, actualCount);

    public static string CannotUnifyKinds(string leftKind, string rightKind) =>
        Format(nameof(CannotUnifyKinds), leftKind, rightKind);

    public static string KindAnnotationIsEmpty => Get(nameof(KindAnnotationIsEmpty));

    public static string UnexpectedTokenInKindAnnotation(object currentToken) =>
        Format(nameof(UnexpectedTokenInKindAnnotation), currentToken);

    public static string KindMismatchInTypeApplication(string expectedKind, string actualKind) =>
        Format(nameof(KindMismatchInTypeApplication), expectedKind, actualKind);

    public static string ExpectedKindClosingParen(object currentToken) =>
        Format(nameof(ExpectedKindClosingParen), currentToken);

    public static string InvalidCharacter(char invalidChar, int codePoint) =>
        Format(nameof(InvalidCharacter), invalidChar, codePoint);

    public static string InvalidToken => Get(nameof(InvalidToken));

    public static string UnexpectedEndOfFile => Get(nameof(UnexpectedEndOfFile));

    public static string MissingProofForImpl(string proofName, string traitName, string typeDisplay) =>
        Format(nameof(MissingProofForImpl), proofName, traitName, typeDisplay);

    public static string UnableToInferConstructorPlaceholderPositions =>
        Get(nameof(UnableToInferConstructorPlaceholderPositions));

    public static string ThirtyTwoBitWindowsUnsupported =>
        Get(nameof(ThirtyTwoBitWindowsUnsupported));

    public static string UnknownTarget(string target, string supportedTargets) =>
        Format(nameof(UnknownTarget), target, supportedTargets);

    public static string CannotMoveBackInSource(int value, int currentPosition) =>
        Format(nameof(CannotMoveBackInSource), value, currentPosition);

    public static string CurrentScopeRequired =>
        Get(nameof(CurrentScopeRequired));

    public static string ConstructorCannotUseRecordUpdateUnknownFields(string constructorName) =>
        Format(nameof(ConstructorCannotUseRecordUpdateUnknownFields), constructorName);

    public static string ConstructorDoesNotAcceptTypeArguments(string constructorName) =>
        Format(nameof(ConstructorDoesNotAcceptTypeArguments), constructorName);

    public static string ConstructorExpectsTypeArguments(string constructorName, int expectedCount, int actualCount) =>
        Format(nameof(ConstructorExpectsTypeArguments), constructorName, expectedCount, actualCount);

    public static string GadtConstructorReturnTypeMustTargetOwnAdt(string adtName) =>
        Format(nameof(GadtConstructorReturnTypeMustTargetOwnAdt), adtName);

    public static string ConstructorPathDoesNotAcceptExplicitTypeArguments(string path) =>
        Format(nameof(ConstructorPathDoesNotAcceptExplicitTypeArguments), path);

    public static string ConstructorRequiresArgumentsInExpressionPosition(string constructorName) =>
        Format(nameof(ConstructorRequiresArgumentsInExpressionPosition), constructorName);

    public static string ConstructExpressionMissingScrutinee(string constructName) =>
        Format(nameof(ConstructExpressionMissingScrutinee), constructName);

    public static string ApplicativeApplyOperandsMustShareUnaryTypeConstructor(object leftType, object rightType) =>
        Format(nameof(ApplicativeApplyOperandsMustShareUnaryTypeConstructor), leftType, rightType);

    public static string BindLeftOperandMustBeUnaryTypeConstructorValue(object type) =>
        Format(nameof(BindLeftOperandMustBeUnaryTypeConstructorValue), type);

    public static string DoBindExpectsMonadicValue(object type) =>
        Format(nameof(DoBindExpectsMonadicValue), type);

    public static string DoLetBindingMissingResolvedSymbol(string bindingName) =>
        Format(nameof(DoLetBindingMissingResolvedSymbol), bindingName);

    public static string FieldInitializerRequiresValueExpression(string fieldName) =>
        Format(nameof(FieldInitializerRequiresValueExpression), fieldName);

    public static string FmapRightOperandMustBeUnaryTypeConstructorValue(object type) =>
        Format(nameof(FmapRightOperandMustBeUnaryTypeConstructorValue), type);

    public static string FunctionBodyBranchRequiresBodyExpression(string functionName) =>
        Format(nameof(FunctionBodyBranchRequiresBodyExpression), functionName);

    public static string FunctionTypeDoesNotImplementTrait(string traitName) =>
        Format(nameof(FunctionTypeDoesNotImplementTrait), traitName);

    public static string HandlerBranchRequiresBodyExpression(string operationName) =>
        Format(nameof(HandlerBranchRequiresBodyExpression), operationName);

    public static string HandlerMustBeFunctionType(object? type) =>
        Format(nameof(HandlerMustBeFunctionType), type ?? "<unknown>");

    public static string HandlerNamedMustBeFunctionType(string handlerName, object type) =>
        Format(nameof(HandlerNamedMustBeFunctionType), handlerName, type);

    public static string HandlerNotFound(string handlerName) =>
        Format(nameof(HandlerNotFound), handlerName);

    public static string HandlerOperationCouldNotBeResolved(string operationName) =>
        Format(nameof(HandlerOperationCouldNotBeResolved), operationName);

    public static string HandlerOperationExpectsArguments(string operationName, int expectedCount, int actualCount) =>
        Format(nameof(HandlerOperationExpectsArguments), operationName, expectedCount, actualCount);

    public static string HandlerOperationNotDefinedByEffect(string operationName, string abilityName) =>
        Format(nameof(HandlerOperationNotDefinedByEffect), operationName, abilityName);

    public static string AsPatternInnerTypeMismatch(string reason) =>
        Format(nameof(AsPatternInnerTypeMismatch), reason);

    public static string InnerPatternInferredAs(object type) =>
        Format(nameof(InnerPatternInferredAs), type);

    public static string InfixOperatorCannotBeApplied(string operatorName, string reason) =>
        Format(nameof(InfixOperatorCannotBeApplied), operatorName, reason);

    public static string NamedArgumentRequiresValueExpression(string argumentName) =>
        Format(nameof(NamedArgumentRequiresValueExpression), argumentName);

    public static string NoImportedOverloadAcceptsArgumentTypes(string name) =>
        Format(nameof(NoImportedOverloadAcceptsArgumentTypes), name);

    public static string NoMethodOverloadAcceptsReceiverType(string methodName, object receiverType) =>
        Format(nameof(NoMethodOverloadAcceptsReceiverType), methodName, receiverType);

    public static string InvalidHigherKindedTypeApplication(string typeName) =>
        Format(nameof(InvalidHigherKindedTypeApplication), typeName);

    public static string InvalidKindApplicationForType(object type) =>
        Format(nameof(InvalidKindApplicationForType), type);

    public static string InvalidTypeArgumentsForTrait(string traitName) =>
        Format(nameof(InvalidTypeArgumentsForTrait), traitName);

    public static string KindMismatchForTypeArgument(
        int argumentIndex,
        string typeParameterName,
        string typeName,
        string expectedKind,
        string actualKind) =>
        Format(nameof(KindMismatchForTypeArgument), argumentIndex, typeParameterName, typeName, expectedKind, actualKind);

    public static string KindMismatchInDefinition(string ownerKind, string ownerName, string reason) =>
        Format(nameof(KindMismatchInDefinition), ownerKind, ownerName, reason);

    public static string KindCannotBeAppliedToAdditionalTypeArguments(string kindText) =>
        Format(nameof(KindCannotBeAppliedToAdditionalTypeArguments), kindText);

    public static string KindMismatch(string expectedKind, string actualKind, string reason) =>
        Format(nameof(KindMismatch), expectedKind, actualKind, reason);

    public static string KindMismatchForTraitArgument(
        int argumentIndex,
        string typeParameterName,
        string traitName,
        string expectedKind,
        string actualKind,
        string reason) =>
        Format(nameof(KindMismatchForTraitArgument), argumentIndex, typeParameterName, traitName, expectedKind, actualKind, reason);

    public static string PathDoesNotAcceptExplicitTypeArguments(string path) =>
        Format(nameof(PathDoesNotAcceptExplicitTypeArguments), path);

    public static string PipeTargetNotCallable(string reason) =>
        Format(nameof(PipeTargetNotCallable), reason);

    public static string RecordUpdateFieldNotPresentOnEveryConstructor(string fieldName, string typeName) =>
        Format(nameof(RecordUpdateFieldNotPresentOnEveryConstructor), fieldName, typeName);

    public static string RecordUpdateRequiresAdtRecordBase(object baseType) =>
        Format(nameof(RecordUpdateRequiresAdtRecordBase), baseType);

    public static string ProofCaseContainsCaptureProneSubstitution(string proofName) =>
        Format(nameof(ProofCaseContainsCaptureProneSubstitution), proofName);

    public static string ProofCaseOnlySupportsReflEvidence(string proofName) =>
        Format(nameof(ProofCaseOnlySupportsReflEvidence), proofName);

    public static string ProofCasePatternCannotConvertToEvidence(string proofName) =>
        Format(nameof(ProofCasePatternCannotConvertToEvidence), proofName);

    public static string ProofCaseReflNotDefinitionallyEqual(string proofName) =>
        Format(nameof(ProofCaseReflNotDefinitionallyEqual), proofName);

    public static string ProofCaseRequiresPattern(string proofName) =>
        Format(nameof(ProofCaseRequiresPattern), proofName);

    public static string ProofCasesMissingBranches(string proofName) =>
        Format(nameof(ProofCasesMissingBranches), proofName);

    public static string ProofCasesMissingScrutinee(string proofName) =>
        Format(nameof(ProofCasesMissingScrutinee), proofName);

    public static string ProofInductionCaseContainsCaptureProneSubstitution(string proofName) =>
        Format(nameof(ProofInductionCaseContainsCaptureProneSubstitution), proofName);

    public static string ProofInductionCaseOnlySupportsReflEvidence(string proofName) =>
        Format(nameof(ProofInductionCaseOnlySupportsReflEvidence), proofName);

    public static string ProofInductionCasePatternCannotConvertToEvidence(string proofName) =>
        Format(nameof(ProofInductionCasePatternCannotConvertToEvidence), proofName);

    public static string ProofInductionCaseReflNotDefinitionallyEqualUnderHypothesis(string proofName) =>
        Format(nameof(ProofInductionCaseReflNotDefinitionallyEqualUnderHypothesis), proofName);

    public static string ProofInductionCaseRequiresPattern(string proofName) =>
        Format(nameof(ProofInductionCaseRequiresPattern), proofName);

    public static string ProofInductionMissingBranches(string proofName) =>
        Format(nameof(ProofInductionMissingBranches), proofName);

    public static string ProofInductionMissingScrutinee(string proofName) =>
        Format(nameof(ProofInductionMissingScrutinee), proofName);

    public static string ProofInductionRequiresVariableScrutinee(string proofName) =>
        Format(nameof(ProofInductionRequiresVariableScrutinee), proofName);

    public static string ProofInductionSupportsStructuralScrutineeOnly(string proofName) =>
        Format(nameof(ProofInductionSupportsStructuralScrutineeOnly), proofName);

    public static string ProofInductionRequiresQuantifiedListVariableScrutinee(string proofName) =>
        Format(nameof(ProofInductionRequiresQuantifiedListVariableScrutinee), proofName);

    public static string ProofInductionSupportsListScrutineeOnly(string proofName) =>
        Format(nameof(ProofInductionSupportsListScrutineeOnly), proofName);

    public static string ProofCaseExhaustivenessCannotBeProven(string proofName) =>
        Format(nameof(ProofCaseExhaustivenessCannotBeProven), proofName);

    public static string ProofCasesNotExhaustive(string proofName, string missingCases) =>
        Format(nameof(ProofCasesNotExhaustive), proofName, missingCases);

    public static string ProofParameterRequiresTypeAnnotation(string parameterName) =>
        Format(nameof(ProofParameterRequiresTypeAnnotation), parameterName);

    public static string ProofReflNotDefinitionallyEqual(string proofName) =>
        Format(nameof(ProofReflNotDefinitionallyEqual), proofName);

    public static string ProofRequiresEqualityProposition(string proofName) =>
        Format(nameof(ProofRequiresEqualityProposition), proofName);

    public static string ProofRequiresEvidence(string proofName) =>
        Format(nameof(ProofRequiresEvidence), proofName);

    public static string ProofHoleIncomplete(string proofName) =>
        Format(nameof(ProofHoleIncomplete), proofName);

    public static string SymbolIsNotValue(string displayName, string symbolKind) =>
        Format(nameof(SymbolIsNotValue), displayName, symbolKind);

    public static string SymbolKindTypeParameter => Get(nameof(SymbolKindTypeParameter));

    public static string SymbolKindType => Get(nameof(SymbolKindType));

    public static string SymbolKindTypeAlias => Get(nameof(SymbolKindTypeAlias));

    public static string SymbolKindEffect => Get(nameof(SymbolKindEffect));

    public static string SymbolKindTrait => Get(nameof(SymbolKindTrait));

    public static string SymbolKindModule => Get(nameof(SymbolKindModule));

    public static string SymbolKindField => Get(nameof(SymbolKindField));

    public static string SymbolKindTraitImplementation => Get(nameof(SymbolKindTraitImplementation));

    public static string SymbolKindProof => Get(nameof(SymbolKindProof));

    public static string TypeHasNoReadableField(object type, string fieldName) =>
        Format(nameof(TypeHasNoReadableField), type, fieldName);

    public static string TypeHasNoRecordConstructorForUpdate(string typeName) =>
        Format(nameof(TypeHasNoRecordConstructorForUpdate), typeName);

    public static string TooManyConstraintErrors(int maxErrors) =>
        Format(nameof(TooManyConstraintErrors), maxErrors);

    public static string TooManyTypeErrors(int maxErrors) =>
        Format(nameof(TooManyTypeErrors), maxErrors);

    public static string TypeExpectsAtLeastTypeArguments(string typeName, int expectedMinimum, int actualCount) =>
        Format(nameof(TypeExpectsAtLeastTypeArguments), typeName, expectedMinimum, actualCount);

    public static string TypeExpectsTypeArguments(string typeName, int expectedCount, int actualCount) =>
        Format(nameof(TypeExpectsTypeArguments), typeName, expectedCount, actualCount);

    public static string TraitExpectsTypeArguments(string traitName, int expectedCount, int actualCount) =>
        Format(nameof(TraitExpectsTypeArguments), traitName, expectedCount, actualCount);

    public static string TupleElementTypeDoesNotImplementTrait(int elementIndex, object elementType, string traitName) =>
        Format(nameof(TupleElementTypeDoesNotImplementTrait), elementIndex, elementType, traitName);

    public static string TypeArgumentDoesNotImplementTrait(int argumentIndex, string typeName, object actualType, string traitRequirement) =>
        Format(nameof(TypeArgumentDoesNotImplementTrait), argumentIndex, typeName, actualType, traitRequirement);

    public static string TypeParameterCannotBeUsedAsTypeConstructor(string typeParameterName) =>
        Format(nameof(TypeParameterCannotBeUsedAsTypeConstructor), typeParameterName);

    public static string TypeParameterDoesNotAcceptTypeArguments(string typeParameterName) =>
        Format(nameof(TypeParameterDoesNotAcceptTypeArguments), typeParameterName);

    public static string TypeParameterExpectsTypeArguments(string typeParameterName, int expectedCount, int actualCount) =>
        Format(nameof(TypeParameterExpectsTypeArguments), typeParameterName, expectedCount, actualCount);

    public static string TypeDoesNotImplementTrait(object type, string traitName) =>
        Format(nameof(TypeDoesNotImplementTrait), type, traitName);

    public static string TypeMissingTypeArgumentRequiredByTraitImpl(string typeName, int argumentIndex, string implName) =>
        Format(nameof(TypeMissingTypeArgumentRequiredByTraitImpl), typeName, argumentIndex, implName);

    public static string RangePatternExpectsIntOrCharScrutinee(object type) =>
        Format(nameof(RangePatternExpectsIntOrCharScrutinee), type);

    public static string ScrutineeTypeInferredAs(object type) =>
        Format(nameof(ScrutineeTypeInferredAs), type);

    public static string UndefinedIdentifier(string name) =>
        Format(nameof(UndefinedIdentifier), name);

    public static string UnsupportedKindAnnotation(string kindText) =>
        Format(nameof(UnsupportedKindAnnotation), kindText);

    public static string UnsupportedBinaryOperator(object operatorName) =>
        Format(nameof(UnsupportedBinaryOperator), operatorName);

    public static string UnsupportedDoBindingKind(object bindingKind) =>
        Format(nameof(UnsupportedDoBindingKind), bindingKind);

    public static string UnsupportedExpressionKind(string expressionKind) =>
        Format(nameof(UnsupportedExpressionKind), expressionKind);

    public static string UnsupportedListComprehensionQualifierKind(object qualifierKind) =>
        Format(nameof(UnsupportedListComprehensionQualifierKind), qualifierKind);

    public static string UnsupportedLiteralKind(object literalKind) =>
        Format(nameof(UnsupportedLiteralKind), literalKind);

    public static string UnsupportedLiteralPatternKind(object literalKind) =>
        Format(nameof(UnsupportedLiteralPatternKind), literalKind);

    public static string UnsupportedPatternKind(string patternKind) =>
        Format(nameof(UnsupportedPatternKind), patternKind);

    public static string UnsupportedTypeNodeKind(string typeNodeKind) =>
        Format(nameof(UnsupportedTypeNodeKind), typeNodeKind);

    public static string UnsupportedUnaryOperator(object operatorName) =>
        Format(nameof(UnsupportedUnaryOperator), operatorName);

    public static string ViewExpressionInferredAs(object type) =>
        Format(nameof(ViewExpressionInferredAs), type);

    public static string ViewExpressionMustAcceptOneArgument(int actualCount) =>
        Format(nameof(ViewExpressionMustAcceptOneArgument), actualCount);

    public static string ViewExpressionTypeMismatch(string reason) =>
        Format(nameof(ViewExpressionTypeMismatch), reason);

    public static string ViewPatternExpressionInvalid(string reason) =>
        Format(nameof(ViewPatternExpressionInvalid), reason);

    public static string TooManyBorrowErrors(int maxErrors) =>
        Format(nameof(TooManyBorrowErrors), maxErrors);

    public static string TooManyAffineTypeErrors(int maxErrors) =>
        Format(nameof(TooManyAffineTypeErrors), maxErrors);

    public static string AffineVariableMovedTwice => Get(nameof(AffineVariableMovedTwice));

    public static string AffineUseAfterMove => Get(nameof(AffineUseAfterMove));

    public static string AffineUseBeforeInit => Get(nameof(AffineUseBeforeInit));

    public static string BorrowReturnValueStillActive => Get(nameof(BorrowReturnValueStillActive));

    public static string BorrowCreateMutableConflict => Get(nameof(BorrowCreateMutableConflict));

    public static string BorrowCreateImmutableWhileMutable => Get(nameof(BorrowCreateImmutableWhileMutable));

    public static string BorrowCreateSharedWhileMutable => Get(nameof(BorrowCreateSharedWhileMutable));

    public static string BorrowLifetimeExceedsBorrowee => Get(nameof(BorrowLifetimeExceedsBorrowee));

    public static string BorrowSharedWhileMutable => Get(nameof(BorrowSharedWhileMutable));

    public static string BorrowSharedWhileMutableResult => Get(nameof(BorrowSharedWhileMutableResult));

    public static string BorrowMultipleMutable => Get(nameof(BorrowMultipleMutable));

    public static string BorrowSharedExistsCannotCreateMutable => Get(nameof(BorrowSharedExistsCannotCreateMutable));

    public static string BorrowMutableBorrowedCannotBorrowAgain => Get(nameof(BorrowMutableBorrowedCannotBorrowAgain));

    public static string BorrowSharedBorrowedCannotCreateMutable => Get(nameof(BorrowSharedBorrowedCannotCreateMutable));

    public static string BorrowArgumentRequiresOwnershipButAlias =>
        Get(nameof(BorrowArgumentRequiresOwnershipButAlias));

    public static string BorrowArgumentRequiresOwnershipButBorrowed =>
        Get(nameof(BorrowArgumentRequiresOwnershipButBorrowed));

    public static string BorrowReturnBoundToInvalidValue => Get(nameof(BorrowReturnBoundToInvalidValue));

    public static string BorrowReturnedValueLifetimeExceedsArgument =>
        Get(nameof(BorrowReturnedValueLifetimeExceedsArgument));

    public static string BorrowValueBorrowedCannotModify => Get(nameof(BorrowValueBorrowedCannotModify));

    public static string BorrowUseAfterMove => Get(nameof(BorrowUseAfterMove));

    public static string BorrowUseAfterMoveCannotUse => Get(nameof(BorrowUseAfterMoveCannotUse));

    public static string BorrowUseAfterMoveCannotRead => Get(nameof(BorrowUseAfterMoveCannotRead));

    public static string BorrowCannotMoveBorrowedValue => Get(nameof(BorrowCannotMoveBorrowedValue));

    public static string BorrowReturnedBorrowMustComeFromInputParameter =>
        Get(nameof(BorrowReturnedBorrowMustComeFromInputParameter));

    public static string BorrowWaitMutableEndsBeforeShared =>
        Get(nameof(BorrowWaitMutableEndsBeforeShared));

    public static string BorrowWaitExistingEndsBeforeMutable =>
        Get(nameof(BorrowWaitExistingEndsBeforeMutable));

    public static string BorrowOwnershipRequiredHint => Get(nameof(BorrowOwnershipRequiredHint));

    public static string BorrowKeepBorroweeAliveHint => Get(nameof(BorrowKeepBorroweeAliveHint));

    public static string BorrowMoveOrCopyHint => Get(nameof(BorrowMoveOrCopyHint));

    public static string BorrowReadBeforeMoveHint => Get(nameof(BorrowReadBeforeMoveHint));

    public static string BorrowWaitEndsBeforeWrite => Get(nameof(BorrowWaitEndsBeforeWrite));

    public static string BorrowWaitEndsBeforeMove => Get(nameof(BorrowWaitEndsBeforeMove));

    public static string BorrowReturnReferenceFromParameterHint =>
        Get(nameof(BorrowReturnReferenceFromParameterHint));

    public static string BorrowActionCreateSharedBorrow => Get(nameof(BorrowActionCreateSharedBorrow));

    public static string BorrowActionCreateMutableBorrow => Get(nameof(BorrowActionCreateMutableBorrow));

    public static string BorrowActionWrite => Get(nameof(BorrowActionWrite));

    public static string BorrowActionMoveValue => Get(nameof(BorrowActionMoveValue));

    public static string BorrowActionCreateSharedBorrowThroughCallArgument =>
        Get(nameof(BorrowActionCreateSharedBorrowThroughCallArgument));

    public static string BorrowActionCreateMutableBorrowThroughCallArgument =>
        Get(nameof(BorrowActionCreateMutableBorrowThroughCallArgument));

    public static string BorrowActionReadThroughCallArgument =>
        Get(nameof(BorrowActionReadThroughCallArgument));

    public static string BorrowActionMoveValueThroughCallArgument =>
        Get(nameof(BorrowActionMoveValueThroughCallArgument));

    public static string BorrowActionBindReturnedSharedBorrow =>
        Get(nameof(BorrowActionBindReturnedSharedBorrow));

    public static string BorrowActionBindReturnedMutableBorrow =>
        Get(nameof(BorrowActionBindReturnedMutableBorrow));

    public static string BorrowReadCapabilityDenied(string action, string target) =>
        Format(nameof(BorrowReadCapabilityDenied), action, target);

    public static string BorrowWriteCapabilityDenied(string action, string target) =>
        Format(nameof(BorrowWriteCapabilityDenied), action, target);

    public static string BorrowMoveCapabilityDenied(string action, string target) =>
        Format(nameof(BorrowMoveCapabilityDenied), action, target);

    public static string BorrowReadCapabilityRequiredHint(string target, string resolution) =>
        Format(nameof(BorrowReadCapabilityRequiredHint), target, resolution);

    public static string BorrowWriteCapabilityRequiredHint(string target, string resolution) =>
        Format(nameof(BorrowWriteCapabilityRequiredHint), target, resolution);

    public static string BorrowMoveLocalCapabilityRequiredHint(string local, string resolution) =>
        Format(nameof(BorrowMoveLocalCapabilityRequiredHint), local, resolution);

    public static string BorrowMoveTargetCapabilityRequiredHint(string target, string resolution) =>
        Format(nameof(BorrowMoveTargetCapabilityRequiredHint), target, resolution);

    public static string BorrowReadCapabilityAdjustmentHint(string resolution) =>
        Format(nameof(BorrowReadCapabilityAdjustmentHint), resolution);

    public static string BorrowWriteCapabilityAdjustmentHint(string resolution) =>
        Format(nameof(BorrowWriteCapabilityAdjustmentHint), resolution);

    public static string BorrowMoveCapabilityAdjustmentHint(string resolution) =>
        Format(nameof(BorrowMoveCapabilityAdjustmentHint), resolution);

    public static string ParserDiagnosticWithPosition(string message, int position, string token) =>
        Format(nameof(ParserDiagnosticWithPosition), message, position, token);

    public static string ParserHereLabel => Get(nameof(ParserHereLabel));

    public static string ParserExpectedToken(string token) =>
        Format(nameof(ParserExpectedToken), token);

    public static string ParserExpectedTokenBefore(string token, string context) =>
        Format(nameof(ParserExpectedTokenBefore), token, context);

    public static string ParserExpectedTokenAfter(string token, string context) =>
        Format(nameof(ParserExpectedTokenAfter), token, context);

    public static string ParserExpectedTokenIn(string token, string context) =>
        Format(nameof(ParserExpectedTokenIn), token, context);

    public static string ParserExpectedType(string token) =>
        Format(nameof(ParserExpectedType), token);

    public static string ParserExpectedKind(string token) =>
        Format(nameof(ParserExpectedKind), token);

    public static string ParserUnexpectedToken(string token) =>
        Format(nameof(ParserUnexpectedToken), token);

    public static string ParserUnexpectedTokenInPattern(string token) =>
        Format(nameof(ParserUnexpectedTokenInPattern), token);

    public static string ParserExpectedFunctionName => Get(nameof(ParserExpectedFunctionName));

    public static string ParserExpectedProofName => Get(nameof(ParserExpectedProofName));

    public static string ParserProofNameMustStartWithUppercase =>
        Get(nameof(ParserProofNameMustStartWithUppercase));

    public static string ParserRuntimeDeclarationNameMustStartWithLowercase(string declarationKind) =>
        Format(nameof(ParserRuntimeDeclarationNameMustStartWithLowercase), declarationKind);

    public static string ParserCompileTimeDeclarationNameMustStartWithUppercase(string declarationKind) =>
        Format(nameof(ParserCompileTimeDeclarationNameMustStartWithUppercase), declarationKind);

    public static string ParserTypeParameterNameMustStartWithUppercase =>
        Get(nameof(ParserTypeParameterNameMustStartWithUppercase));

    public static string ParserGenericWhereTargetMustStartWithUppercase =>
        Get(nameof(ParserGenericWhereTargetMustStartWithUppercase));

    public static string ParserModulePathSegmentMustStartWithUppercase =>
        Get(nameof(ParserModulePathSegmentMustStartWithUppercase));

    public static string ParserModuleAliasMustStartWithUppercase =>
        Get(nameof(ParserModuleAliasMustStartWithUppercase));

    public static string ParserImportAliasUseNameFirstForm =>
        Get(nameof(ParserImportAliasUseNameFirstForm));

    public static string ParserImportBindingExpectsModulePath =>
        Get(nameof(ParserImportBindingExpectsModulePath));

    public static string ParserExpectedProofParameterName => Get(nameof(ParserExpectedProofParameterName));

    public static string ParserExpectedCasesOrInductionAfterBy =>
        Get(nameof(ParserExpectedCasesOrInductionAfterBy));

    public static string ParserExpectedProofTerm => Get(nameof(ParserExpectedProofTerm));

    public static string ParserExpectedTraitBodyMember(string token) =>
        Format(nameof(ParserExpectedTraitBodyMember), token);

    public static string ParserIndexExpressionRequiresIndex =>
        Get(nameof(ParserIndexExpressionRequiresIndex));

    public static string ParserExpectedExpression(string token) =>
        Format(nameof(ParserExpectedExpression), token);

    public static string ParserRecordUpdateSpreadPosition =>
        Get(nameof(ParserRecordUpdateSpreadPosition));

    public static string ParserInlineWithRequiresEffectPath =>
        Get(nameof(ParserInlineWithRequiresEffectPath));

    public static string ParserRangePatternStartMustBeLiteral =>
        Get(nameof(ParserRangePatternStartMustBeLiteral));

    public static string ParserExpectedPattern(string token) =>
        Format(nameof(ParserExpectedPattern), token);

    public static string ParserInvalidListRestPatternUsage =>
        Get(nameof(ParserInvalidListRestPatternUsage));

    public static string ParserListRestMarkerLabel => Get(nameof(ParserListRestMarkerLabel));

    public static string ParserListRestUseHelp => Get(nameof(ParserListRestUseHelp));

    public static string ParserListRestValidFormsHelp => Get(nameof(ParserListRestValidFormsHelp));

    public static string ParserListRestExplicitElementsHelp =>
        Get(nameof(ParserListRestExplicitElementsHelp));

    public static string ParserExpectedRightBraceToCloseEffectSet =>
        Get(nameof(ParserExpectedRightBraceToCloseEffectSet));

    public static string ParserEffectfulTypeSyntaxRemoved =>
        Get(nameof(ParserEffectfulTypeSyntaxRemoved));

    public static string ParserLegacyEffectSyntaxRemoved =>
        Get(nameof(ParserLegacyEffectSyntaxRemoved));

    public static string ParserEffectTagTypeParametersNotSupported =>
        Get(nameof(ParserEffectTagTypeParametersNotSupported));

    public static string ParserEffectTagMembersNotSupported =>
        Get(nameof(ParserEffectTagMembersNotSupported));

    public static string ParserEffectTagRequiresSemicolon =>
        Get(nameof(ParserEffectTagRequiresSemicolon));

    public static string ParserHandlerSyntaxRemoved =>
        Get(nameof(ParserHandlerSyntaxRemoved));

    public static string ParserWithClauseSyntaxRemoved =>
        Get(nameof(ParserWithClauseSyntaxRemoved));

    public static string ParserNeedClauseRequiresEffectPath =>
        Get(nameof(ParserNeedClauseRequiresEffectPath));

    public static string ParserNeedClauseAllowsEffectPathsOnly =>
        Get(nameof(ParserNeedClauseAllowsEffectPathsOnly));

    public static string ParserExpectedRightParenToCloseViewPattern =>
        Get(nameof(ParserExpectedRightParenToCloseViewPattern));

    public static string ParserExpectedColonBeforeSignature =>
        Get(nameof(ParserExpectedColonBeforeSignature));

    public static string ParserExpectedRightParenAfterOperatorFunctionName =>
        Get(nameof(ParserExpectedRightParenAfterOperatorFunctionName));

    public static string ParserExpectedProofKeyword =>
        Get(nameof(ParserExpectedProofKeyword));

    public static string ParserExpectedColonBeforeProofProposition =>
        Get(nameof(ParserExpectedColonBeforeProofProposition));

    public static string ParserExpectedDotAfterProofParameters =>
        Get(nameof(ParserExpectedDotAfterProofParameters));

    public static string ParserExpectedEqualInProofProposition =>
        Get(nameof(ParserExpectedEqualInProofProposition));

    public static string ParserExpectedRightBraceAfterProofBody =>
        Get(nameof(ParserExpectedRightBraceAfterProofBody));

    public static string ParserExpectedColonAfterProofParameterName =>
        Get(nameof(ParserExpectedColonAfterProofParameterName));

    public static string ParserExpectedLeftBraceBeforeProofCases =>
        Get(nameof(ParserExpectedLeftBraceBeforeProofCases));

    public static string ParserExpectedRightBraceAfterProofCases =>
        Get(nameof(ParserExpectedRightBraceAfterProofCases));

    public static string ParserExpectedFatArrowInProofCase =>
        Get(nameof(ParserExpectedFatArrowInProofCase));

    public static string ParserExpectedProofReflTerm =>
        Get(nameof(ParserExpectedProofReflTerm));

    public static string ParserExpectedLeftBraceAfterDo =>
        Get(nameof(ParserExpectedLeftBraceAfterDo));

    public static string ParserExpectedRightBraceAfterDoExpression =>
        Get(nameof(ParserExpectedRightBraceAfterDoExpression));

    public static string ParserExpectedEqualInDoLetBinding =>
        Get(nameof(ParserExpectedEqualInDoLetBinding));

    public static string ParserExpectedLeftArrowInDoBinding =>
        Get(nameof(ParserExpectedLeftArrowInDoBinding));

    public static string ParserExpectedIdentifierAfterQualifiedSeparator =>
        Get(nameof(ParserExpectedIdentifierAfterQualifiedSeparator));

    public static string ParserExpectedTypeIdentifierAfterQualifiedSeparator =>
        Get(nameof(ParserExpectedTypeIdentifierAfterQualifiedSeparator));

    public static string ParserListRestStandaloneNote =>
        Get(nameof(ParserListRestStandaloneNote));

    public static string ParserListRestMustBeLastNote =>
        Get(nameof(ParserListRestMustBeLastNote));

    public static string MissingPatternDuringHirLowering => Get(nameof(MissingPatternDuringHirLowering));

    public static string MissingExpressionWhileLowering(string context) =>
        Format(nameof(MissingExpressionWhileLowering), context);

    public static string UnsupportedAstExpressionDuringHirLowering(string nodeType) =>
        Format(nameof(UnsupportedAstExpressionDuringHirLowering), nodeType);

    public static string UnsupportedAstPatternDuringHirLowering(string patternType) =>
        Format(nameof(UnsupportedAstPatternDuringHirLowering), patternType);

    public static string UnsupportedAstPatternReason(string patternType) =>
        Format(nameof(UnsupportedAstPatternReason), patternType);

    public static string UnsupportedPatternLabel => Get(nameof(UnsupportedPatternLabel));

    public static string PipelineInternalError => Get(nameof(PipelineInternalError));

    public static string PipelineInternalErrorHelp => Get(nameof(PipelineInternalErrorHelp));

    public static string ExceptionNote(string exceptionType, string message) =>
        Format(nameof(ExceptionNote), exceptionType, message);

    public static string StackTraceNote(string stackTrace) =>
        Format(nameof(StackTraceNote), stackTrace);

    public static string StackTraceUnavailable => Get(nameof(StackTraceUnavailable));

    public static string SendCheckFailed(string functionName, string message) =>
        Format(nameof(SendCheckFailed), functionName, message);

    public static string SpawnArgumentTypeMustImplementSend(object typeId) =>
        Format(nameof(SpawnArgumentTypeMustImplementSend), typeId);

    public static string CircularImportDetected(string chain) =>
        Format(nameof(CircularImportDetected), chain);

    public static string FailedToLoadImportedModuleFile(string filePath, string message) =>
        Format(nameof(FailedToLoadImportedModuleFile), filePath, message);

    public static string UnableToResolveImportedModule(string importPath) =>
        Format(nameof(UnableToResolveImportedModule), importPath);

    public static string ImportedModuleFailedToParse(string importPath) =>
        Format(nameof(ImportedModuleFailedToParse), importPath);

    public static string ImportedFileDoesNotDeclareModule(string filePath, string modulePath) =>
        Format(nameof(ImportedFileDoesNotDeclareModule), filePath, modulePath);

    public static string EntryFileNote(string filePath) =>
        Format(nameof(EntryFileNote), filePath);

    public static string SearchedRootNote(string root) =>
        Format(nameof(SearchedRootNote), root);

    public static string FileNote(string filePath) =>
        Format(nameof(FileNote), filePath);

    public static string ReportedDiagnosticsNote(int count) =>
        Format(nameof(ReportedDiagnosticsNote), count);

    public static string MatchedAliasTraceHint(string traceId, string aliasTrace) =>
        Format(nameof(MatchedAliasTraceHint), traceId, aliasTrace);

    public static string LoadedImportedFileLabel => Get(nameof(LoadedImportedFileLabel));

    public static string RequestedImportNote(string importPath) =>
        Format(nameof(RequestedImportNote), importPath);

    public static string ResolvedFromRootNote(string rootDirectory) =>
        Format(nameof(ResolvedFromRootNote), rootDirectory);

    public static string FilesystemModulePathNote(string modulePath) =>
        Format(nameof(FilesystemModulePathNote), modulePath);

    public static string DuplicateModulePath(string modulePath) =>
        Format(nameof(DuplicateModulePath), modulePath);

    public static string DuplicateModuleDeclarationLabel => Get(nameof(DuplicateModuleDeclarationLabel));

    public static string FirstDeclarationOfModuleHere(string modulePath) =>
        Format(nameof(FirstDeclarationOfModuleHere), modulePath);

    public static string FirstModuleDeclarationLabel => Get(nameof(FirstModuleDeclarationLabel));

    public static string FunctionNote(string functionName) =>
        Format(nameof(FunctionNote), functionName);

    public static string LocalNote(int localId) =>
        Format(nameof(LocalNote), localId);

    public static string MirLocationNote(int blockId, string instruction) =>
        Format(nameof(MirLocationNote), blockId, instruction);

    public static string MirLocationShortNote(int blockId, int instruction) =>
        Format(nameof(MirLocationShortNote), blockId, instruction);

    public static string FirstMirLocationNote(int blockId, int instruction) =>
        Format(nameof(FirstMirLocationNote), blockId, instruction);

    public static string SecondMirLocationNote(int blockId, int instruction) =>
        Format(nameof(SecondMirLocationNote), blockId, instruction);

    public static string RelatedMirLocationNote(int blockId, int instruction) =>
        Format(nameof(RelatedMirLocationNote), blockId, instruction);

    public static string RoleNote(string role) =>
        Format(nameof(RoleNote), role);

    public static string TargetNote(object target) =>
        Format(nameof(TargetNote), target);

    public static string ReasonNote(string reason) =>
        Format(nameof(ReasonNote), reason);

    public static string RelatedAffineOperationNote => Get(nameof(RelatedAffineOperationNote));

    public static string RelatedBorrowNote => Get(nameof(RelatedBorrowNote));

    public static string RelatedLabel => Get(nameof(RelatedLabel));

    public static string RelatedDiagnosticMessageWithLabel(string message, string labelMessage) =>
        Format(nameof(RelatedDiagnosticMessageWithLabel), message, labelMessage);

    public static string AliasTraceIdNote(string traceId) =>
        Format(nameof(AliasTraceIdNote), traceId);

    public static string AliasStateLookupNote(string traceId, string functionName) =>
        Format(nameof(AliasStateLookupNote), traceId, functionName);

    public static string AliasTraceNote(string trace) =>
        Format(nameof(AliasTraceNote), trace);

    public static string MirUnsupportedNode(string role, string typeName) =>
        Format(nameof(MirUnsupportedNode), role, typeName);

    public static string MirUnsupportedNodeLabel(string role) =>
        Format(nameof(MirUnsupportedNodeLabel), role);

    public static string MirUnsupportedNodeHelp => Get(nameof(MirUnsupportedNodeHelp));

    public static string MirMissingTerminator => Get(nameof(MirMissingTerminator));

    public static string MirMissingTerminatorLabel => Get(nameof(MirMissingTerminatorLabel));

    public static string MirMissingTerminatorHelp => Get(nameof(MirMissingTerminatorHelp));

    public static string MirMissingBlockTarget(string role, object target) =>
        Format(nameof(MirMissingBlockTarget), role, target);

    public static string MirMissingBlockTargetLabel => Get(nameof(MirMissingBlockTargetLabel));

    public static string MirMissingBlockTargetHelp => Get(nameof(MirMissingBlockTargetHelp));

    public static string MirUnknownTypeId(object typeId) =>
        Format(nameof(MirUnknownTypeId), typeId);

    public static string MirUnknownTypeIdLabel => Get(nameof(MirUnknownTypeIdLabel));

    public static string MirExpectedKnownTypeIdNote => Get(nameof(MirExpectedKnownTypeIdNote));

    public static string MirMissingTypeId(string role) =>
        Format(nameof(MirMissingTypeId), role);

    public static string MirMissingTypeIdLabel => Get(nameof(MirMissingTypeIdLabel));

    public static string MirMissingFunctionIdentity(string functionName) =>
        Format(nameof(MirMissingFunctionIdentity), functionName);

    public static string MirMissingFunctionIdentityLabel => Get(nameof(MirMissingFunctionIdentityLabel));

    public static string MirMissingFunctionIdentityHelp => Get(nameof(MirMissingFunctionIdentityHelp));

    public static string MirPoisonOperand => Get(nameof(MirPoisonOperand));

    public static string MirPoisonOperandLabel => Get(nameof(MirPoisonOperandLabel));

    public static string MirFunctionRequiresConcreteReturnType =>
        Get(nameof(MirFunctionRequiresConcreteReturnType));

    public static string MirAllocRequiresConcreteAllocationType =>
        Get(nameof(MirAllocRequiresConcreteAllocationType));


    public static string HirErrorPatternReachedMirLowering =>
        Get(nameof(HirErrorPatternReachedMirLowering));

    public static string CannotLowerHirErrorPattern(string reason) =>
        Format(nameof(CannotLowerHirErrorPattern), reason);

    public static string MirPatternPoisonLabel => Get(nameof(MirPatternPoisonLabel));

    public static string UnsupportedHirPatternDuringMirLowering(string patternType) =>
        Format(nameof(UnsupportedHirPatternDuringMirLowering), patternType);

    public static string UnsupportedHirPatternReason(string patternType) =>
        Format(nameof(UnsupportedHirPatternReason), patternType);

    public static string AmbiguousFieldAcrossConstructors(string fieldName) =>
        Format(nameof(AmbiguousFieldAcrossConstructors), fieldName);

    public static string AmbiguousFieldAccessLabel => Get(nameof(AmbiguousFieldAccessLabel));

    public static string InconsistentAdtFieldOrdinal(string fieldName, string adtName) =>
        Format(nameof(InconsistentAdtFieldOrdinal), fieldName, adtName);

    public static string AmbiguousAdtFieldAccessLabel => Get(nameof(AmbiguousAdtFieldAccessLabel));

    public static string UseConstructorPatternMatchingFieldHelp =>
        Get(nameof(UseConstructorPatternMatchingFieldHelp));

    public static string NonTotalAdtField(string fieldName, string adtName) =>
        Format(nameof(NonTotalAdtField), fieldName, adtName);

    public static string NonTotalAdtFieldAccessLabel => Get(nameof(NonTotalAdtFieldAccessLabel));

    public static string UseMatchBeforeConstructorSpecificFieldHelp =>
        Get(nameof(UseMatchBeforeConstructorSpecificFieldHelp));

    public static string UnknownAdtField(string fieldName, string adtName) =>
        Format(nameof(UnknownAdtField), fieldName, adtName);

    public static string UnknownAdtFieldLabel => Get(nameof(UnknownAdtFieldLabel));

    public static string ModuleValueInitializerCannotCall(string valueName) =>
        Format(nameof(ModuleValueInitializerCannotCall), valueName);

    public static string UnsupportedModuleInitializerLabel =>
        Get(nameof(UnsupportedModuleInitializerLabel));

    public static string ModuleLambdaCapturesState(string valueName) =>
        Format(nameof(ModuleLambdaCapturesState), valueName);

    public static string ModuleLambdaValueLabel => Get(nameof(ModuleLambdaValueLabel));

    public static string ListComprehensionMirPoison(string reason) =>
        Format(nameof(ListComprehensionMirPoison), reason);

    public static string ListComprehensionMissingGeneratorSourceReason =>
        Get(nameof(ListComprehensionMissingGeneratorSourceReason));

    public static string ListComprehensionGeneratorRequiredReason =>
        Get(nameof(ListComprehensionGeneratorRequiredReason));

    public static string ListComprehensionLoweringPoisonLabel =>
        Get(nameof(ListComprehensionLoweringPoisonLabel));

    public static string ListComprehensionVarPatternGeneratorHelp =>
        Get(nameof(ListComprehensionVarPatternGeneratorHelp));

    public static string ListComprehensionLoweringFailedReason(string reason) =>
        Format(nameof(ListComprehensionLoweringFailedReason), reason);

    public static string ListComprehensionUnsupportedGeneratorPattern(string patternKind) =>
        Format(nameof(ListComprehensionUnsupportedGeneratorPattern), patternKind);

    public static string ListComprehensionUnsupportedGeneratorPatternLabel =>
        Get(nameof(ListComprehensionUnsupportedGeneratorPatternLabel));

    public static string ListComprehensionUnsupportedGeneratorPatternHelp =>
        Get(nameof(ListComprehensionUnsupportedGeneratorPatternHelp));

    public static string ListComprehensionUnsupportedGeneratorPatternReason(string patternKind) =>
        Format(nameof(ListComprehensionUnsupportedGeneratorPatternReason), patternKind);

    public static string BorrowRequiresStablePlace(string operatorSymbol) =>
        Format(nameof(BorrowRequiresStablePlace), operatorSymbol);

    public static string TraitDoesNotAcceptTypeArgumentsInImpl(string traitName) =>
        Format(nameof(TraitDoesNotAcceptTypeArgumentsInImpl), traitName);

    public static string TraitExpectsTypeArgumentsInImpl(string traitName, int expectedCount, int actualCount) =>
        Format(nameof(TraitExpectsTypeArgumentsInImpl), traitName, expectedCount, actualCount);

    public static string TraitDefinitionUnavailableForImplSignature(string traitName) =>
        Format(nameof(TraitDefinitionUnavailableForImplSignature), traitName);

    public static string TraitFunctionDoesNotMatchMethods(
        string functionName,
        string traitName,
        string expectedMethods) =>
        Format(nameof(TraitFunctionDoesNotMatchMethods), functionName, traitName, expectedMethods);

    public static string TraitMethodSignatureMismatch(string expectedSignature, string actualSignature) =>
        Format(nameof(TraitMethodSignatureMismatch), expectedSignature, actualSignature);

    public static string TraitDefinitionUnavailableForProofImpl(string traitName) =>
        Format(nameof(TraitDefinitionUnavailableForProofImpl), traitName);

    public static string ProofDoesNotMatchTraitNoObligations(string proofName, string traitName) =>
        Format(nameof(ProofDoesNotMatchTraitNoObligations), proofName, traitName);

    public static string ProofDoesNotMatchTraitObligations(
        string proofName,
        string traitName,
        string expectedProofs) =>
        Format(nameof(ProofDoesNotMatchTraitObligations), proofName, traitName, expectedProofs);

    public static string ProofObligationPropositionMismatch(string proofName, string traitName) =>
        Format(nameof(ProofObligationPropositionMismatch), proofName, traitName);

    public static string ConventionGenericTraitHint(string functionName, string traitName) =>
        Format(nameof(ConventionGenericTraitHint), functionName, traitName);

    public static string ConventionGenericTraitsHint(string functionName, string traitNames) =>
        Format(nameof(ConventionGenericTraitsHint), functionName, traitNames);

    public static string FunctionArityMismatch(int leftCount, int rightCount) =>
        Format(nameof(FunctionArityMismatch), leftCount, rightCount);

    public static string FunctionEffectMismatch(object left, object right) =>
        Format(nameof(FunctionEffectMismatch), left, right);

    public static string TupleSizeMismatch(int leftCount, int rightCount) =>
        Format(nameof(TupleSizeMismatch), leftCount, rightCount);

    public static string InnerPatternRecoveredAfterEarlierMismatch =>
        Get(nameof(InnerPatternRecoveredAfterEarlierMismatch));

    public static string StyleSuggestionPreferFluentPrefixCalls =>
        Get(nameof(StyleSuggestionPreferFluentPrefixCalls));

    public static string StyleSuggestionPreferInfixBinaryCalls =>
        Get(nameof(StyleSuggestionPreferInfixBinaryCalls));

    public static string StyleSuggestionPreferPatternGuardBranches =>
        Get(nameof(StyleSuggestionPreferPatternGuardBranches));

    public static string PrefixCallCanBeRewrittenLabel =>
        Get(nameof(PrefixCallCanBeRewrittenLabel));

    public static string PatternGuardBranchConditionChainLabel =>
        Get(nameof(PatternGuardBranchConditionChainLabel));

    public static string PrefixCallRewriteHelp => Get(nameof(PrefixCallRewriteHelp));

    public static string PatternGuardBranchRewriteHelp => Get(nameof(PatternGuardBranchRewriteHelp));

    public static string InfixCallRewriteHelp => Get(nameof(InfixCallRewriteHelp));

    public static string RewriteAsInfixSuggestion => Get(nameof(RewriteAsInfixSuggestion));

    public static string RewriteAsSuggestion(string replacement) =>
        Format(nameof(RewriteAsSuggestion), replacement);

    public static string UnsupportedMirInstructionDuringLlvmLowering(string instructionType) =>
        Format(nameof(UnsupportedMirInstructionDuringLlvmLowering), instructionType);

    public static string UnsupportedInstructionLabel => Get(nameof(UnsupportedInstructionLabel));

    public static string UnsupportedMirTerminatorDuringLlvmLowering(string terminatorType) =>
        Format(nameof(UnsupportedMirTerminatorDuringLlvmLowering), terminatorType);

    public static string UnsupportedTerminatorLabel => Get(nameof(UnsupportedTerminatorLabel));

    public static string UnresolvedValueTypeDuringLlvmOperandLowering(string context) =>
        Format(nameof(UnresolvedValueTypeDuringLlvmOperandLowering), context);

    public static string OnlyGenericPartialPlaceholdersMayRemainTypeErased =>
        Get(nameof(OnlyGenericPartialPlaceholdersMayRemainTypeErased));

    public static string SiteNote(string site) => Format(nameof(SiteNote), site);

    public static string ContextNote(string context) => Format(nameof(ContextNote), context);

    public static string UnresolvedOperandValueTypeLabel =>
        Get(nameof(UnresolvedOperandValueTypeLabel));

    public static string UnknownTypeIdOpaquePointerFallback(object typeId) =>
        Format(nameof(UnknownTypeIdOpaquePointerFallback), typeId);

    public static string UnknownTypeIdFallbackLabel => Get(nameof(UnknownTypeIdFallbackLabel));

    public static string UnsupportedTargetOperandFallback(string operandKind, string context) =>
        Format(nameof(UnsupportedTargetOperandFallback), operandKind, context);

    public static string ExpectedMirPlaceOrTempTargetBeforeLlvm =>
        Get(nameof(ExpectedMirPlaceOrTempTargetBeforeLlvm));

    public static string UnsupportedTargetOperandLabel => Get(nameof(UnsupportedTargetOperandLabel));

    public static string UnsupportedTargetOperandFallbackLabel =>
        Get(nameof(UnsupportedTargetOperandFallbackLabel));

    public static string UnsupportedPlaceKindFallback(int kind, string context) =>
        Format(nameof(UnsupportedPlaceKindFallback), kind, context);

    public static string ExpectedLocalDerefFieldOrIndexPlaceBeforeLlvm =>
        Get(nameof(ExpectedLocalDerefFieldOrIndexPlaceBeforeLlvm));

    public static string UnsupportedPlaceKindLabel => Get(nameof(UnsupportedPlaceKindLabel));

    public static string UnsupportedPlaceKindFallbackLabel =>
        Get(nameof(UnsupportedPlaceKindFallbackLabel));

    public static string MissingMirTargetPlaceForContext(string context) =>
        Format(nameof(MissingMirTargetPlaceForContext), context);

    public static string ExpectedMirPlaceTargetBeforeLlvm =>
        Get(nameof(ExpectedMirPlaceTargetBeforeLlvm));

    public static string MissingTargetPlaceLabel => Get(nameof(MissingTargetPlaceLabel));

    public static string UnsupportedMirTargetPlaceKind(int kind, string context) =>
        Format(nameof(UnsupportedMirTargetPlaceKind), kind, context);

    public static string ExpectedLocalMirPlaceTargetBeforeTargetLowering =>
        Get(nameof(ExpectedLocalMirPlaceTargetBeforeTargetLowering));

    public static string UnsupportedTargetPlaceKindLabel =>
        Get(nameof(UnsupportedTargetPlaceKindLabel));

    public static string FunctionSymbolMultipleLlvmSignatures(int symbolId) =>
        Format(nameof(FunctionSymbolMultipleLlvmSignatures), symbolId);

    public static string SourceFunctionNote(string functionName) =>
        Format(nameof(SourceFunctionNote), functionName);

    public static string UnresolvedFunctionSignatureRole(string role) =>
        Format(nameof(UnresolvedFunctionSignatureRole), role);

    public static string EnsureInferenceMonomorphizationBeforeLlvm =>
        Get(nameof(EnsureInferenceMonomorphizationBeforeLlvm));

    public static string UnresolvedFunctionSignatureTypeLabel =>
        Get(nameof(UnresolvedFunctionSignatureTypeLabel));

    public static string GenericCallEscapedMirSpecialization(string functionName) =>
        Format(nameof(GenericCallEscapedMirSpecialization), functionName);

    public static string SpecializeCallWithConcreteTypesNote =>
        Get(nameof(SpecializeCallWithConcreteTypesNote));

    public static string ZeroArgumentGenericPartialCannotMonomorphize =>
        Get(nameof(ZeroArgumentGenericPartialCannotMonomorphize));

    public static string GenericCallLabel => Get(nameof(GenericCallLabel));

    public static string IndirectGenericCallEscapedMirSpecialization(int localId) =>
        Format(nameof(IndirectGenericCallEscapedMirSpecialization), localId);

    public static string BindLocalFunctionToConcreteSpecializationNote =>
        Get(nameof(BindLocalFunctionToConcreteSpecializationNote));

    public static string GenericIndirectCallLabel => Get(nameof(GenericIndirectCallLabel));

    public static string FunctionReferenceUnresolvedDuringLlvm(string functionName) =>
        Format(nameof(FunctionReferenceUnresolvedDuringLlvm), functionName);

    public static string MissingTerminatorDuringLlvmLowering =>
        Get(nameof(MissingTerminatorDuringLlvmLowering));

    public static string MissingReturnValueDuringLlvmLowering =>
        Get(nameof(MissingReturnValueDuringLlvmLowering));

    public static string DefaultReturnValueRejectedDuringLlvmLowering =>
        Get(nameof(DefaultReturnValueRejectedDuringLlvmLowering));

    public static string LlvmFallbackLoweredToUnreachableHelp =>
        Get(nameof(LlvmFallbackLoweredToUnreachableHelp));

    public static string OnlyRuntimeIntrinsicsMayRemainUnresolvedNote =>
        Get(nameof(OnlyRuntimeIntrinsicsMayRemainUnresolvedNote));

    public static string EnsureResolutionMonomorphizationBeforeLlvmNote =>
        Get(nameof(EnsureResolutionMonomorphizationBeforeLlvmNote));

    public static string SymbolIdNote(int symbolId) => Format(nameof(SymbolIdNote), symbolId);

    public static string UnresolvedFunctionReferenceLabel =>
        Get(nameof(UnresolvedFunctionReferenceLabel));

    public static string UnresolvedExternalDeclarationRetained(string declarationName) =>
        Format(nameof(UnresolvedExternalDeclarationRetained), declarationName);

    public static string LlvmDuplicateGlobalDefinition(string globalName) =>
        Format(nameof(LlvmDuplicateGlobalDefinition), globalName);

    public static string LlvmDuplicateGlobalDefinitionNote(string definitions) =>
        Format(nameof(LlvmDuplicateGlobalDefinitionNote), definitions);

    public static string InternalFunctionReferencesMustResolveBeforeLlvmNote =>
        Get(nameof(InternalFunctionReferencesMustResolveBeforeLlvmNote));

    public static string OnlyRuntimeIntrinsicsMayRemainAsDeclarationsNote =>
        Get(nameof(OnlyRuntimeIntrinsicsMayRemainAsDeclarationsNote));

    public static string MissedResolutionSpecializationOrRewritingNote =>
        Get(nameof(MissedResolutionSpecializationOrRewritingNote));

    public static string RemainingGenericArityUnknown =>
        Get(nameof(RemainingGenericArityUnknown));

    public static string RemainingGenericArity(int remainingArity) =>
        Format(nameof(RemainingGenericArity), remainingArity);

    public static string SpecializationRejectedUnresolvedTypes(string templateName, object signature) =>
        Format(nameof(SpecializationRejectedUnresolvedTypes), templateName, signature);
}
