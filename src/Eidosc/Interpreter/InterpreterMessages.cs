using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Eidosc.Interpreter;

internal static class InterpreterMessages
{
    private static readonly ResourceManager Resources = new(
        "Eidosc.Interpreter.InterpreterResources",
        Assembly.GetExecutingAssembly());

    public static string PerformRequiresHandlerContext => Get(nameof(PerformRequiresHandlerContext));

    public static string HandleExpressionsUnsupported => Get(nameof(HandleExpressionsUnsupported));

    public static string ResumeUnsupportedOutsideHandlerContext => Get(nameof(ResumeUnsupportedOutsideHandlerContext));

    public static string PatternGuardsNotEvaluable => Get(nameof(PatternGuardsNotEvaluable));

    public static string SequentialGuardsNotEvaluable => Get(nameof(SequentialGuardsNotEvaluable));

    public static string FieldAssignmentUnsupported => Get(nameof(FieldAssignmentUnsupported));

    public static string IndexAssignmentUnsupported => Get(nameof(IndexAssignmentUnsupported));

    public static string TupleDestructuringMismatch => Get(nameof(TupleDestructuringMismatch));

    public static string ConstructorFieldCountMismatch => Get(nameof(ConstructorFieldCountMismatch));

    public static string ListDestructuringRequiresList => Get(nameof(ListDestructuringRequiresList));

    public static string ListLengthMismatchInDestructuring => Get(nameof(ListLengthMismatchInDestructuring));

    public static string NonExhaustivePatternMatch => Get(nameof(NonExhaustivePatternMatch));

    public static string AbsExpectedIntOrFloat => Get(nameof(AbsExpectedIntOrFloat));

    public static string MinExpectedMatchingNumericTypes => Get(nameof(MinExpectedMatchingNumericTypes));

    public static string MaxExpectedMatchingNumericTypes => Get(nameof(MaxExpectedMatchingNumericTypes));

    public static string HeadEmptyList => Get(nameof(HeadEmptyList));

    public static string TailEmptyList => Get(nameof(TailEmptyList));

    public static string FstTupleTooSmall => Get(nameof(FstTupleTooSmall));

    public static string SndTupleTooSmall => Get(nameof(SndTupleTooSmall));

    public static string UnsupportedHirNode(string nodeType) =>
        Format(nameof(UnsupportedHirNode), nodeType);

    public static string UnsupportedLiteralKind(object literalKind) =>
        Format(nameof(UnsupportedLiteralKind), literalKind);

    public static string UndefinedVariable(string name) =>
        Format(nameof(UndefinedVariable), name);

    public static string UnsupportedBinaryOperator(object op) =>
        Format(nameof(UnsupportedBinaryOperator), op);

    public static string CannotAdd(string leftType, string rightType) =>
        Format(nameof(CannotAdd), leftType, rightType);

    public static string CannotNegate(string operandType) =>
        Format(nameof(CannotNegate), operandType);

    public static string UnsupportedUnaryOperator(object op) =>
        Format(nameof(UnsupportedUnaryOperator), op);

    public static string CannotCall(string functionType) =>
        Format(nameof(CannotCall), functionType);

    public static string FunctionArityMismatch(int expectedCount, int actualCount) =>
        Format(nameof(FunctionArityMismatch), expectedCount, actualCount);

    public static string UnsupportedStatementType(string statementType) =>
        Format(nameof(UnsupportedStatementType), statementType);

    public static string UnsupportedDeclarationType(string declarationType) =>
        Format(nameof(UnsupportedDeclarationType), declarationType);

    public static string CannotAssignTo(string targetType) =>
        Format(nameof(CannotAssignTo), targetType);

    public static string ConstructorPatternMismatch(string constructorName) =>
        Format(nameof(ConstructorPatternMismatch), constructorName);

    public static string CannotAccessField(string fieldName, string valueType) =>
        Format(nameof(CannotAccessField), fieldName, valueType);

    public static string IndexOutOfRange(long index) =>
        Format(nameof(IndexOutOfRange), index);

    public static string TupleIndexOutOfRange(long index) =>
        Format(nameof(TupleIndexOutOfRange), index);

    public static string CannotIndex(string targetType, string indexType) =>
        Format(nameof(CannotIndex), targetType, indexType);

    public static string StandaloneHandlerUnsupported(string abilityName) =>
        Format(nameof(StandaloneHandlerUnsupported), abilityName);

    public static string CannotCompare(string leftType, string rightType) =>
        Format(nameof(CannotCompare), leftType, rightType);

    public static string ExpectedRuntimeValueType(string expectedType, string actualType) =>
        Format(nameof(ExpectedRuntimeValueType), expectedType, actualType);

    public static string CharAtIndexOutOfRange(int index) =>
        Format(nameof(CharAtIndexOutOfRange), index);

    public static string StringToIntCannotParse(string text) =>
        Format(nameof(StringToIntCannotParse), text);

    public static string StringToFloatCannotParse(string text) =>
        Format(nameof(StringToFloatCannotParse), text);

    public static string RuntimeFunctionDisplay(string parameters) =>
        Format(nameof(RuntimeFunctionDisplay), parameters);

    public static string RuntimeBuiltinFunctionDisplay(string name) =>
        Format(nameof(RuntimeBuiltinFunctionDisplay), name);

    private static string Get(string name) =>
        Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Format(string name, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(name), args);
}
