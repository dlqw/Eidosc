using Eidosc.Ast;

namespace Eidosc.Types;

internal static partial class ComptimeEvaluator
{
    private static string GetSyntaxKind(EidosAstNode node) =>
        MetaComptimeIntrinsics.GetCanonicalNodeKind(node);

    private static string GetLiteralKind(object? value) => value switch
    {
        null => "unit",
        bool => "bool",
        byte or short or int or long => "integer",
        float or double => "float",
        char => "char",
        string => "string",
        _ => "unsupported-literal"
    };

    private static string GetValueKind(ComptimeValue value) => value switch
    {
        ComptimeUnitValue => "unit",
        ComptimeBoolValue => "bool",
        ComptimeIntegerValue => "integer",
        ComptimeFloatValue => "float",
        ComptimeCharValue => "char",
        ComptimeStringValue => "string",
        ComptimeSequenceValue { Kind: ComptimeSequenceKind.Tuple } => "tuple",
        ComptimeSequenceValue => "list",
        ComptimeMapValue => "map",
        ComptimeSetValue => "set",
        ComptimeAdtValue => "adt",
        ComptimeTypeValue => "type",
        ComptimeDeclValue => "declaration",
        ComptimeSyntaxValue => "syntax",
        ComptimeTokensValue => "tokens",
        ComptimeMetaObjectValue => "meta-handle",
        ComptimeLambdaValue or ComptimeFunctionValue => "function",
        _ => "unsupported-value"
    };
}
