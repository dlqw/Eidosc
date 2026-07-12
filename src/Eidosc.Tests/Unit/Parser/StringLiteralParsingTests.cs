using System;
using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Parser;

public class StringLiteralParsingTests
{
    [Fact]
    public void Parser_StringLiteral_WithEscapedQuoteAndBackslash_ParsesAsStringLiteral()
    {
        const string source = """
s :: "a\\\"b\\\\c";
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetLetValue(result, "s");
        var literal = Assert.IsType<LiteralExpr>(value);

        Assert.Equal(LiteralKind.String, literal.Kind);
        Assert.StartsWith("\"", literal.RawText, StringComparison.Ordinal);
        Assert.EndsWith("\"", literal.RawText, StringComparison.Ordinal);
        Assert.Contains("\\\"", literal.RawText, StringComparison.Ordinal);
        Assert.Contains("\\\\", literal.RawText, StringComparison.Ordinal);
        Assert.Equal("a\\\"b\\\\c", Assert.IsType<string>(literal.Value));
    }

    [Fact]
    public void Parser_CharLiteral_WithEscape_ParsesAsCharLiteral()
    {
        const string source = """
c :: '\n';
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetLetValue(result, "c");
        var literal = Assert.IsType<LiteralExpr>(value);

        Assert.Equal(LiteralKind.Char, literal.Kind);
        Assert.Contains("\\n", literal.RawText, StringComparison.Ordinal);
        Assert.Equal('\n', Assert.IsType<char>(literal.Value));
    }

    private static CompilationResult RunPipeline(string source, CompilationPhase stopAt)
    {
        var options = new CompilationOptions
        {
            InputFile = "string_literal_parser_tests.eidos",
            StopAtPhase = stopAt,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static Eidosc.Ast.EidosAstNode GetLetValue(CompilationResult result, string bindingName)
    {
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var decl = Assert.Single(
            module.Declarations.OfType<LetDecl>(),
            value => value.Pattern is VarPattern { Name: var name } && name == bindingName);
        Assert.NotNull(decl.Value);
        return decl.Value!;
    }
}
