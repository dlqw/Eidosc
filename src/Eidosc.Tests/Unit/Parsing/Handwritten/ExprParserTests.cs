using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Parsing.Handwritten;
using Eidosc.ProjectSystem;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Tests.Unit.Parsing.Handwritten;

public sealed class ExprParserTests
{
    [Fact]
    public void Parse_integer_literal()
    {
        var ctx = MakeCtx(Num("42"));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var lit = Assert.IsType<LiteralExpr>(result);
        Assert.Equal(42, lit.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_string_literal()
    {
        var ctx = MakeCtx(Str("\"hello\""));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var lit = Assert.IsType<LiteralExpr>(result);
        Assert.Equal("hello", lit.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Theory]
    [InlineData("\"return\"", "return")]
    [InlineData("\"break\"", "break")]
    public void Parse_string_literal_with_keyword_body_as_literal(string sourceText, string value)
    {
        var ctx = MakeCtx(StringLiteral(sourceText, value));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var lit = Assert.IsType<LiteralExpr>(result);
        Assert.Equal(value, lit.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_bool_literal()
    {
        var ctx = MakeCtx("true");
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var lit = Assert.IsType<LiteralExpr>(result);
        Assert.Equal(true, lit.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_identifier()
    {
        var ctx = MakeCtx(Ident("x"));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var ident = Assert.IsType<IdentifierExpr>(result);
        Assert.Equal("x", ident.Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_mut_identifier_lambda_parameter()
    {
        var ctx = MakeCtx("mut", Ident("state"), "=>", Ident("state"));
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var lambda = Assert.IsType<LambdaExpr>(result);
        var param = Assert.IsType<VarPattern>(Assert.Single(lambda.Parameters));
        Assert.Equal("state", param.Name);
        Assert.True(param.IsMutableBinding);
        Assert.IsType<IdentifierExpr>(lambda.Body);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_path_expression()
    {
        var ctx = MakeCtx(Ident("Std"), "::", Ident("Seq"), "::", Ident("map"));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var path = Assert.IsType<PathExpr>(result);
        Assert.Equal("map", path.Name);
        Assert.Equal("Std", path.PackageAlias);
        Assert.Equal(["Seq"], path.ModulePath);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_package_qualified_path_expression()
    {
        var ctx = MakeCtx(Ident("crypto_a"), "::", Ident("hash"), "/", Ident("sha256"), "::", Ident("digest"));
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var path = Assert.IsType<PathExpr>(result);
        Assert.Equal("crypto_a", path.PackageAlias);
        Assert.Equal(["hash", "sha256"], path.ModulePath);
        Assert.Equal("digest", path.Name);
        Assert.Equal(["crypto_a", "hash", "sha256", "digest"], path.Path);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_package_qualified_dot_path_expression()
    {
        // Std.Collections.Seq.map — module path uses dot separator
        var ctx = MakeCtx(Ident("Std"), "::", Ident("Collections"), ".", Ident("Seq"), "::", Ident("map"));
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var path = Assert.IsType<PathExpr>(result);
        Assert.Equal("Std", path.PackageAlias);
        Assert.Equal(["Collections", "Seq"], path.ModulePath);
        Assert.Equal("map", path.Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_binary_add()
    {
        var ctx = MakeCtx(Ident("a"), "+", Ident("b"));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var binary = Assert.IsType<BinaryExpr>(result);
        Assert.Equal(BinaryOp.Add, binary.Operator);
        Assert.IsType<IdentifierExpr>(binary.Left);
        Assert.IsType<IdentifierExpr>(binary.Right);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_binary_precedence()
    {
        // a + b * c => a + (b * c)
        var ctx = MakeCtx(Ident("a"), "+", Ident("b"), "*", Ident("c"));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var outer = Assert.IsType<BinaryExpr>(result);
        Assert.Equal(BinaryOp.Add, outer.Operator);
        Assert.IsType<IdentifierExpr>(outer.Left);
        var inner = Assert.IsType<BinaryExpr>(outer.Right);
        Assert.Equal(BinaryOp.Multiply, inner.Operator);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_unary_negate()
    {
        var ctx = MakeCtx("-", Num("42"));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var unary = Assert.IsType<UnaryExpr>(result);
        Assert.Equal(UnaryOp.Negate, unary.Operator);
        var lit = Assert.IsType<LiteralExpr>(unary.Operand);
        Assert.Equal(42, lit.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_call_expr()
    {
        var ctx = MakeCtx(Ident("f"), "(", Ident("x"), ")");
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var call = Assert.IsType<CallExpr>(result);
        Assert.IsType<IdentifierExpr>(call.Function);
        Assert.Single(call.PositionalArgs);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_call_like_syntax_keeps_constructor_category_unresolved()
    {
        var ctx = MakeCtx(TypeId("Some"), "(", Num("1"), ")");
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var call = Assert.IsType<CallExpr>(result);
        Assert.Equal("Some", Assert.IsType<IdentifierExpr>(call.Function).Name);
        Assert.Single(call.PositionalArgs);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_contextual_record_literal()
    {
        var ctx = MakeCtx(".", "{", Ident("x"), ":", Num("1"), ",", Ident("y"), ":", Num("2"), "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var literal = Assert.IsType<ContextualRecordLiteralExpr>(result);
        Assert.Equal(2, literal.NamedArgs.Count);
        Assert.Equal("x", literal.NamedArgs[0].FieldName);
        Assert.Equal("y", literal.NamedArgs[1].FieldName);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_tuple_expr()
    {
        var ctx = MakeCtx("(", Ident("a"), ",", Ident("b"), ",", Ident("c"), ")");
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var tuple = Assert.IsType<TupleExpr>(result);
        Assert.Equal(3, tuple.Elements.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_list_expr()
    {
        var ctx = MakeCtx("[", Num("1"), ",", Num("2"), ",", Num("3"), "]");
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var list = Assert.IsType<ListExpr>(result);
        Assert.Equal(3, list.Elements.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_list_rest_with_curried_call_expr()
    {
        var ctx = MakeCtx(
            "[",
            Ident("head"),
            ",",
            "..",
            Ident("fmap"),
            "(",
            Ident("tail"),
            ")",
            "(",
            Ident("f"),
            ")",
            "]");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var list = Assert.IsType<ListExpr>(result);
        Assert.True(list.HasRest);
        Assert.Equal(2, list.Elements.Count);
        Assert.IsType<CallExpr>(list.Elements[1]);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_lambda_fn()
    {
        var ctx = MakeCtx("fn", "(", Ident("x"), ")", "=>", Ident("x"), "+", Num("1"));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var lambda = Assert.IsType<LambdaExpr>(result);
        Assert.Single(lambda.Parameters);
        Assert.NotNull(lambda.Body);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_anonymous_lambda()
    {
        var ctx = MakeCtx(Ident("x"), "=>", Ident("x"), "+", Num("1"));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var lambda = Assert.IsType<LambdaExpr>(result);
        Assert.Single(lambda.Parameters);
        Assert.NotNull(lambda.Body);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_prepend_right_assoc()
    {
        // 1 +: 2 +: [] => 1 +: (2 +: [])
        var ctx = MakeCtx(Num("1"), "+:", Num("2"), "+:", "[", "]");
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var outer = Assert.IsType<BinaryExpr>(result);
        Assert.Equal(BinaryOp.Prepend, outer.Operator);
        Assert.IsType<BinaryExpr>(outer.Right);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_append_last_left_assoc()
    {
        // [] :+ 1 :+ 2 => ([] :+ 1) :+ 2
        var ctx = MakeCtx("[", "]", ":+", Num("1"), ":+", Num("2"));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var outer = Assert.IsType<BinaryExpr>(result);
        Assert.Equal(BinaryOp.AppendLast, outer.Operator);
        var inner = Assert.IsType<BinaryExpr>(outer.Left);
        Assert.Equal(BinaryOp.AppendLast, inner.Operator);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_pipe_left_assoc()
    {
        // x |> f |> g => (x |> f) |> g
        var ctx = MakeCtx(Ident("x"), "|>", Ident("f"), "|>", Ident("g"));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var outer = Assert.IsType<BinaryExpr>(result);
        Assert.Equal(BinaryOp.Pipe, outer.Operator);
        var inner = Assert.IsType<BinaryExpr>(outer.Left);
        Assert.Equal(BinaryOp.Pipe, inner.Operator);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_coalesce_right_assoc()
    {
        // a ?? b ?? c => a ?? (b ?? c)
        var ctx = MakeCtx(Ident("a"), "??", Ident("b"), "??", Ident("c"));
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var outer = Assert.IsType<BinaryExpr>(result);
        Assert.Equal(BinaryOp.Coalesce, outer.Operator);
        Assert.IsType<IdentifierExpr>(outer.Left);
        var inner = Assert.IsType<BinaryExpr>(outer.Right);
        Assert.Equal(BinaryOp.Coalesce, inner.Operator);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_if_expr()
    {
        var ctx = MakeCtx("if", Ident("x"), ">", Num("0"), "then", "{", Ident("x"), "}", "else", "{", "-", Ident("x"), "}");
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var ifExpr = Assert.IsType<IfExpr>(result);
        Assert.NotNull(ifExpr.Condition);
        Assert.NotNull(ifExpr.ThenBranch);
        Assert.NotNull(ifExpr.ElseBranch);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_continue_expr()
    {
        var ctx = MakeCtx("continue");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        Assert.IsType<ContinueExpr>(result);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_if_expr_accepts_continue_branch()
    {
        var ctx = MakeCtx("if", Ident("d"), ">", Ident("cur"), "then", "continue", "else", "{", "(", ")", "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var ifExpr = Assert.IsType<IfExpr>(result);
        Assert.IsType<ContinueExpr>(ifExpr.ThenBranch);
        Assert.IsType<BlockExpr>(ifExpr.ElseBranch);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_decide_expr_lowers_row_key_list_to_or_condition()
    {
        var ctx = MakeNameFirstCtx(
            "decide", Ident("current"), "{",
            Ident("key_down"), "(", "_", ")", "!=", Num("0"), ":",
            Num("87"), "|", Num("265"), "=>", TypeId("North"), "(", ")", ",",
            Num("83"), "=>", TypeId("South"), "(", ")",
            "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var first = Assert.IsType<IfExpr>(result);
        var firstCondition = Assert.IsType<BinaryExpr>(first.Condition);
        Assert.Equal(BinaryOp.Or, firstCondition.Operator);
        AssertDecisionConditionUsesKey(firstCondition.Left, 87);
        AssertDecisionConditionUsesKey(firstCondition.Right, 265);
        Assert.IsType<CallExpr>(first.ThenBranch);

        var second = Assert.IsType<IfExpr>(first.ElseBranch);
        AssertDecisionConditionUsesKey(second.Condition, 83);
        var fallback = Assert.IsType<IdentifierExpr>(second.ElseBranch);
        Assert.Equal("current", fallback.Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_decide_expr_accepts_multiple_template_groups()
    {
        var ctx = MakeNameFirstCtx(
            "decide", Ident("fallback"), "{",
            Ident("a"), "(", "_", ")", ":",
            Num("1"), "=>", Ident("one"), ",",
            Ident("b"), "(", "_", ")", ":",
            Num("2"), "=>", Ident("two"),
            "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var first = Assert.IsType<IfExpr>(result);
        AssertDecisionConditionUsesKey(first.Condition, 1);
        var second = Assert.IsType<IfExpr>(first.ElseBranch);
        AssertDecisionConditionUsesKey(second.Condition, 2);
        var fallback = Assert.IsType<IdentifierExpr>(second.ElseBranch);
        Assert.Equal("fallback", fallback.Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_decide_expr_accepts_tuple_keys_for_multi_hole_template()
    {
        var ctx = MakeNameFirstCtx(
            "decide", Ident("fallback"), "{",
            Ident("same"), "(", "_", ",", "_", ")", ":",
            "(", Ident("a"), ",", Ident("b"), ")", "=>", Ident("hit"),
            "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var first = Assert.IsType<IfExpr>(result);
        var call = Assert.IsType<CallExpr>(first.Condition);
        Assert.Equal(2, call.PositionalArgs.Count);
        Assert.Equal("a", Assert.IsType<IdentifierExpr>(call.PositionalArgs[0]).Name);
        Assert.Equal("b", Assert.IsType<IdentifierExpr>(call.PositionalArgs[1]).Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_decide_expr_accepts_row_guard()
    {
        var ctx = MakeNameFirstCtx(
            "decide", Ident("fallback"), "{",
            Ident("key_down"), "(", "_", ")", ":",
            Num("87"), "when", Ident("enabled"), "=>", Ident("hit"),
            "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var first = Assert.IsType<IfExpr>(result);
        var condition = Assert.IsType<BinaryExpr>(first.Condition);
        Assert.Equal(BinaryOp.And, condition.Operator);
        AssertDecisionConditionUsesKey(condition.Left, 87);
        Assert.Equal("enabled", Assert.IsType<IdentifierExpr>(condition.Right).Name);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_decide_expr_reports_missing_template_hole()
    {
        var ctx = MakeNameFirstCtx(
            "decide", Ident("fallback"), "{",
            Ident("ready"), ":",
            Num("1"), "=>", Ident("one"),
            "}");
        var parser = new ExprParser(ctx);

        _ = parser.ParseExpr();

        Assert.Contains(ctx.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("must contain '_' hole", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_decide_expr_warns_for_duplicate_literal_key_in_same_template_group()
    {
        var ctx = MakeNameFirstCtx(
            "decide", Ident("fallback"), "{",
            Ident("key_down"), "(", "_", ")", ":",
            Num("87"), "|", Num("265"), "=>", Ident("north"), ",",
            Num("87"), "=>", Ident("south"),
            "}");
        var parser = new ExprParser(ctx);

        _ = parser.ParseExpr();

        var warning = Assert.Single(ctx.Diagnostics, diagnostic => diagnostic.Code == "W4301");
        Assert.Equal(Eidosc.Diagnostic.DiagnosticLevel.Warning, warning.Level);
        Assert.Contains("duplicate decision key", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_decide_expr_warns_for_duplicate_literal_tuple_key()
    {
        var ctx = MakeNameFirstCtx(
            "decide", Ident("fallback"), "{",
            Ident("same"), "(", "_", ",", "_", ")", ":",
            "(", Num("1"), ",", Num("2"), ")", "=>", Ident("first"), ",",
            "(", Num("1"), ",", Num("2"), ")", "=>", Ident("second"),
            "}");

        _ = new ExprParser(ctx).ParseExpr();

        Assert.Contains(ctx.Diagnostics, diagnostic => diagnostic.Code == "W4301");
    }

    [Fact]
    public void Parse_decide_expr_allows_same_literal_key_in_different_template_groups()
    {
        var ctx = MakeNameFirstCtx(
            "decide", Ident("fallback"), "{",
            Ident("keyboard"), "(", "_", ")", ":",
            Num("1"), "=>", Ident("first"), ",",
            Ident("gamepad"), "(", "_", ")", ":",
            Num("1"), "=>", Ident("second"),
            "}");

        _ = new ExprParser(ctx).ParseExpr();

        Assert.DoesNotContain(ctx.Diagnostics, diagnostic => diagnostic.Code == "W4301");
    }

    [Fact]
    public void Parse_match_expr()
    {
        var ctx = MakeCtx("match", Ident("x"), "{", TypeId("Some"), "(", Ident("v"), ")", "=>", Ident("v"), ",", TypeId("None"), "(", ")", "=>", Num("0"), "}");
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var match = Assert.IsType<MatchExpr>(result);
        Assert.Equal(2, match.Branches.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_match_expr_with_uppercase_scrutinee_and_or_pattern()
    {
        var ctx = MakeNameFirstCtx("match", TypeId("Tier"), "{", Num("0"), "|", Num("1"), "=>", Num("16"), ",", "_", "=>", Num("0"), "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var match = Assert.IsType<MatchExpr>(result);
        var scrutinee = Assert.IsType<IdentifierExpr>(match.MatchedExpression);
        Assert.Equal("Tier", scrutinee.Name);
        var orPattern = Assert.IsType<OrPattern>(match.Branches[0].Pattern);
        Assert.Equal(2, orPattern.Alternatives.Count);
        Assert.Equal(2, match.Branches.Count);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_chained_calls()
    {
        // f(x)(y)
        var ctx = MakeCtx(Ident("f"), "(", Ident("x"), ")", "(", Ident("y"), ")");
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var outerCall = Assert.IsType<CallExpr>(result);
        Assert.Single(outerCall.PositionalArgs);
        var innerCall = Assert.IsType<CallExpr>(outerCall.Function);
        Assert.Single(innerCall.PositionalArgs);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_complex_expression()
    {
        // a + b * c - d
        // => (a + (b * c)) - d
        var ctx = MakeCtx(Ident("a"), "+", Ident("b"), "*", Ident("c"), "-", Ident("d"));
        var parser = new ExprParser(ctx);
        var result = parser.ParseExpr();
        var sub = Assert.IsType<BinaryExpr>(result);
        Assert.Equal(BinaryOp.Subtract, sub.Operator);
        var add = Assert.IsType<BinaryExpr>(sub.Left);
        Assert.Equal(BinaryOp.Add, add.Operator);
        var mul = Assert.IsType<BinaryExpr>(add.Right);
        Assert.Equal(BinaryOp.Multiply, mul.Operator);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_loop_block_with_assignment_and_break()
    {
        var ctx = MakeCtx(
            "loop", "{",
            "let", "mut", Ident("result"), "=", "true", ";",
            Ident("result"), ":=", "false", ";",
            "break",
            "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var loop = Assert.IsType<LoopExpr>(result);
        var body = Assert.IsType<BlockExpr>(loop.Body);
        Assert.Contains(body.Statements, statement => statement is LetDecl { IsMutable: true });
        Assert.Contains(body.Statements, statement => statement is Assignment assignment && assignment.Target == "result");
        Assert.Contains(body.Statements, statement => statement is BreakExpr);
        Assert.IsType<BreakExpr>(body.ResultExpression);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_block_with_expression_tail_sets_result_expression()
    {
        var ctx = MakeCtx("{", Ident("x"), "+", Num("1"), "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var block = Assert.IsType<BlockExpr>(result);
        Assert.IsType<BinaryExpr>(block.ResultExpression);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_block_with_assignment_as_last_item_has_no_tail_expression()
    {
        var ctx = MakeCtx("{", Ident("x"), ":=", Num("1"), "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var block = Assert.IsType<BlockExpr>(result);
        Assert.Null(block.ResultExpression);
        Assert.Single(block.Statements);
        Assert.IsType<Assignment>(block.Statements[0]);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_block_with_field_place_assignment_as_last_item_has_no_tail_expression()
    {
        var ctx = MakeCtx("{", Ident("state"), ".", Ident("tick"), ":=", Num("0"), "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var block = Assert.IsType<BlockExpr>(result);
        Assert.Null(block.ResultExpression);
        var assignment = Assert.IsType<Assignment>(Assert.Single(block.Statements));
        var target = Assert.IsType<MethodCallExpr>(assignment.TargetExpression);
        Assert.Equal("tick", target.MethodName);
        Assert.IsType<IdentifierExpr>(target.Receiver);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_block_with_index_place_assignment_as_last_item_has_no_tail_expression()
    {
        var ctx = MakeCtx("{", Ident("items"), "[", Num("0"), "]", ":=", Num("1"), "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var block = Assert.IsType<BlockExpr>(result);
        Assert.Null(block.ResultExpression);
        var assignment = Assert.IsType<Assignment>(Assert.Single(block.Statements));
        Assert.IsType<IndexExpr>(assignment.TargetExpression);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_block_with_deref_place_assignment_as_last_item_has_no_tail_expression()
    {
        var ctx = MakeCtx("{", "*", Ident("target"), ":=", Num("1"), "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var block = Assert.IsType<BlockExpr>(result);
        Assert.Null(block.ResultExpression);
        var assignment = Assert.IsType<Assignment>(Assert.Single(block.Statements));
        Assert.IsType<UnaryExpr>(assignment.TargetExpression);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_block_requires_last_item_to_be_unterminated_expression_for_tail()
    {
        var ctx = MakeCtx("{", Ident("x"), "+", Num("1"), ";", Ident("y"), ":=", Num("2"), "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var block = Assert.IsType<BlockExpr>(result);
        Assert.Null(block.ResultExpression);
        Assert.Equal(2, block.Statements.Count);
        Assert.IsType<BinaryExpr>(block.Statements[0]);
        Assert.IsType<Assignment>(block.Statements[1]);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_semicolonless_qualified_call_before_mutable_assignment()
    {
        var ctx = MakeNameFirstCtx(
            "{",
            "mut", Ident("index"), ":=", Num("0"),
            Ident("runtime_array"), ".", Ident("swap"),
            "(", Ident("heap"), ",", Ident("index"), ",", Ident("parent"), ")",
            Ident("index"), ":=", Ident("parent"),
            Ident("index"),
            "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var block = Assert.IsType<BlockExpr>(result);
        Assert.Equal(4, block.Statements.Count);
        Assert.IsAssignableFrom<Expression>(block.Statements[1]);
        var assignment = Assert.IsType<Assignment>(block.Statements[2]);
        Assert.Equal("index", assignment.Target);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_semicolonless_call_before_inferred_local_binding()
    {
        var ctx = MakeNameFirstCtx(
            "{",
            Ident("emit"), "(", Ident("value"), ")",
            Ident("next"), ":=", Num("1"),
            Ident("next"),
            "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var block = Assert.IsType<BlockExpr>(result);
        Assert.Equal(3, block.Statements.Count);
        Assert.IsType<CallExpr>(block.Statements[0]);
        Assert.IsType<LetDecl>(block.Statements[1]);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_comptime_local_binding()
    {
        var ctx = MakeNameFirstCtx("{", "comptime", Ident("size"), ":", TypeId("Int"), ":=", Num("32"), ";", Ident("size"), "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var block = Assert.IsType<BlockExpr>(result);
        var letDecl = Assert.IsType<LetDecl>(block.Statements[0]);
        var pattern = Assert.IsType<VarPattern>(letDecl.Pattern);
        Assert.True(letDecl.IsComptime);
        Assert.False(letDecl.IsMutable);
        Assert.Equal("size", pattern.Name);
        Assert.Equal("Int", Assert.IsType<TypePath>(letDecl.TypeAnnotation).TypeName);
        Assert.Equal("32", Assert.IsType<LiteralExpr>(letDecl.Value).RawText);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_ref_local_binding()
    {
        var ctx = MakeNameFirstCtx("{", "ref", Ident("borrowed"), ":=", Ident("text"), ";", Ident("borrowed"), "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var block = Assert.IsType<BlockExpr>(result);
        var letDecl = Assert.IsType<LetDecl>(block.Statements[0]);
        var pattern = Assert.IsType<VarPattern>(letDecl.Pattern);
        Assert.Equal("borrowed", pattern.Name);
        Assert.Equal(PatternBindingMode.SharedBorrow, pattern.BindingMode);
        Assert.IsType<IdentifierExpr>(letDecl.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_tuple_pattern_local_binding()
    {
        var ctx = MakeNameFirstCtx(
            "{",
            "(", Ident("left"), ",", Ident("right"), ")", ":=", Ident("pieces"), ";",
            Ident("left"),
            "}");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var block = Assert.IsType<BlockExpr>(result);
        var letDecl = Assert.IsType<LetDecl>(block.Statements[0]);
        var pattern = Assert.IsType<TuplePattern>(letDecl.Pattern);
        Assert.Equal(2, pattern.Elements.Count);
        Assert.Equal("left", Assert.IsType<VarPattern>(pattern.Elements[0]).Name);
        Assert.Equal("right", Assert.IsType<VarPattern>(pattern.Elements[1]).Name);
        Assert.IsType<IdentifierExpr>(letDecl.Value);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_given_expr()
    {
        var ctx = MakeNameFirstCtx(Ident("contains"), "(", Ident("names"), ",", Str("bob"), ")", "given", TypeId("CaseInsensitiveStringEq"));
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var given = Assert.IsType<GivenExpr>(result);
        Assert.IsType<CallExpr>(given.Target);
        Assert.Equal(["CaseInsensitiveStringEq"], given.EvidencePath);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_associated_const_expr()
    {
        var ctx = MakeNameFirstCtx(TypeId("Bounded"), "[", TypeId("Int"), "]", ".", TypeId("Min"));
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var member = Assert.IsType<MethodCallExpr>(result);
        Assert.Equal("Min", member.MethodName);
        Assert.False(member.HasExplicitCallSyntax);
        var target = Assert.IsType<IndexExpr>(member.Receiver);
        Assert.Equal("Bounded", Assert.IsType<IdentifierExpr>(target.Object).Name);
        Assert.Equal("Int", Assert.IsType<IdentifierExpr>(target.Index).Name);
        Assert.Empty(target.GenericArguments);
        Assert.Empty(ctx.Diagnostics);
    }

    [Fact]
    public void Parse_name_first_generic_application_preserves_const_expression_candidate()
    {
        var ctx = MakeNameFirstCtx(
            Ident("specialize"), "[",
            TypeId("N"), "+", Num("1"), ",",
            TypeId("Int"), "]");
        var parser = new ExprParser(ctx);

        var result = parser.ParseExpr();

        var application = Assert.IsType<IndexExpr>(result);
        Assert.Equal(2, application.GenericArguments.Count);
        Assert.IsType<BinaryExpr>(Assert.IsType<UnresolvedGenericArgumentNode>(application.GenericArguments[0]).ValueCandidate);
        Assert.Equal("Int", Assert.IsType<TypePath>(Assert.IsType<UnresolvedGenericArgumentNode>(application.GenericArguments[1]).TypeCandidate).TypeName);
        Assert.Single(application.TypeArgs);
        Assert.Empty(ctx.Diagnostics);
    }

    #region Helpers

    private static ParserContext MakeCtx(params object[] tokenSpecs)
    {
        return MakeCtxWithSyntax(EidosLanguageVersions.Legacy, tokenSpecs);
    }

    private static ParserContext MakeNameFirstCtx(params object[] tokenSpecs)
    {
        return MakeCtxWithSyntax(EidosLanguageVersions.Current, tokenSpecs);
    }

    private static ParserContext MakeCtxWithSyntax(string languageVersion, params object[] tokenSpecs)
    {
        var tokens = new List<Token>();
        foreach (var spec in tokenSpecs)
        {
            switch (spec)
            {
                case string s:
                    tokens.Add(new PlainToken(s));
                    break;
                case Token t:
                    tokens.Add(t);
                    break;
            }
        }
        tokens.Add(new EofToken(new SourceLocation(tokens.Count, 0, 0)));
        return new ParserContext(tokens, "test", languageVersion);
    }

    private static Token Ident(string name)
        => new DebugNameToken(name, "identifier");

    private static Token TypeId(string name)
        => new DebugNameToken(name, "identifier");

    private static Token Num(string text)
        => new DebugNameToken(text, "numberLiteral");

    private static Token Str(string text)
        => new DebugNameToken(text, "stringLiteral");

    private static Token StringLiteral(string sourceText, string value)
        => new ContentToken(
            new SourceLocation(0, 0, 0),
            SyntaxKind.StringLiteral,
            new Terminal(0, "stringLiteral", TerminalFlag.None),
            sourceText.GetOrIntern(),
            sourceText.Length,
            value);

    private static void AssertDecisionConditionUsesKey(EidosAstNode? condition, int expectedKey)
    {
        var callSource = condition is BinaryExpr { Operator: BinaryOp.NotEqual } comparison
            ? comparison.Left
            : condition;
        var call = Assert.IsType<CallExpr>(callSource);
        var key = Assert.IsType<LiteralExpr>(Assert.Single(call.PositionalArgs));
        Assert.Equal(expectedKey, key.Value);
    }

    private sealed class PlainToken(string text) : ContentToken(
        new SourceLocation(0, 0, 0),
        SyntaxKind.None,
        new Terminal(0, text, TerminalFlag.None),
        text.GetOrIntern(), text.Length, text);

    private static SyntaxKind DebugNameToKind(string debugName) => debugName switch
    {
        "identifier" => SyntaxKind.Identifier,
        "operatorIdentifier" => SyntaxKind.OperatorIdentifier,
        "numberLiteral" => SyntaxKind.NumberLiteral,
        "stringLiteral" => SyntaxKind.StringLiteral,
        "charLiteral" => SyntaxKind.CharLiteral,
        "booleanLiteral" => SyntaxKind.BooleanLiteral,
        _ => SyntaxKindHelper.TryFromText(debugName, out var k) ? k : SyntaxKind.None
    };

    private sealed class DebugNameToken(string text, string debugName) : ContentToken(
        new SourceLocation(0, 0, 0),
        DebugNameToKind(debugName),
        new Terminal(0, debugName, TerminalFlag.None),
        text.GetOrIntern(), text.Length, text);

    #endregion
}
