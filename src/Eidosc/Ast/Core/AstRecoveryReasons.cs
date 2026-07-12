namespace Eidosc.Ast;

/// <summary>
/// Defines stable machine-readable parser recovery reason identifiers.
/// </summary>
public static class AstRecoveryReasons
{
    public const string ParserExpectedExpression = "parser.expected_expression";
    public const string ParserMissingIndexExpression = "parser.missing_index_expression";
    public const string ParserMissingInitializer = "parser.missing_initializer";
    public const string ParserRecoveredLiteral = "parser.recovered_literal";
}
