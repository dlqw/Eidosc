using System.Linq;
using Eidosc;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Parser;

public class AdvancedPatternParsingTests
{
    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesCtorNamedViewAsRegularCtorPattern()
    {
        const string source = """
to_digit :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        View(to_digit, 7) => 30,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_view_ctor_legacy.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);

        var viewCtorPattern = Assert.IsType<CtorPattern>(matchExpr.Branches[0].Pattern);
        Assert.Equal("View", viewCtorPattern.ConstructorName);
        Assert.Equal(2, viewCtorPattern.PositionalPatterns.Count);
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_RejectsKeywordViewPattern()
    {
        const string source = """
to_digit :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        view(to_digit -> 7) => 30,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_view_keyword.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.False(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics.SelectMany(diagnostic => diagnostic.Helps),
            help => help.Contains("native view pattern", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesNativeViewPattern()
    {
        const string source = """
to_digit :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        (to_digit -> 7) => 30,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_view_native.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);

        var viewPattern = Assert.IsType<ViewPattern>(matchExpr.Branches[0].Pattern);
        var viewExpr = Assert.IsType<IdentifierExpr>(viewPattern.ViewExpression);
        Assert.Equal("to_digit", viewExpr.Name);
        var innerLiteral = Assert.IsType<LiteralPattern>(viewPattern.InnerPattern);
        Assert.Equal(7L, Assert.IsType<long>(innerLiteral.Value));
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesNativeViewPatternWithGeneralExpression()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        (if true then { normalize } else { normalize } -> 7) => 30,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_view_native_expr.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);

        var viewPattern = Assert.IsType<ViewPattern>(matchExpr.Branches[0].Pattern);
        Assert.IsType<IfExpr>(viewPattern.ViewExpression);
        var innerLiteral = Assert.IsType<LiteralPattern>(viewPattern.InnerPattern);
        Assert.Equal(7L, Assert.IsType<long>(innerLiteral.Value));
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesNativeViewPatternWithCallExpression()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x
}

select_view :: Bool -> Int -> Int
{
    b => if b then { normalize } else { normalize }
}

classify :: Int -> Int
{
    x => match x
    {
        (select_view(true) -> 7) => 30,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_view_native_call_expr.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);

        var viewPattern = Assert.IsType<ViewPattern>(matchExpr.Branches[0].Pattern);
        var callExpr = Assert.IsType<CallExpr>(viewPattern.ViewExpression);
        var callee = Assert.IsType<IdentifierExpr>(callExpr.Function);
        Assert.Equal("select_view", callee.Name);
        var argument = Assert.IsType<LiteralExpr>(Assert.Single(callExpr.PositionalArgs));
        Assert.Equal(LiteralKind.Boolean, argument.Kind);
        Assert.True(Assert.IsType<bool>(argument.Value));
        var innerLiteral = Assert.IsType<LiteralPattern>(viewPattern.InnerPattern);
        Assert.Equal(7L, Assert.IsType<long>(innerLiteral.Value));
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesNativeViewPatternWithCallOnGeneralExpression()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        ((if true then { normalize } else { normalize })(x) -> 7) => 30,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_view_native_call_on_general_expr.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);

        var viewPattern = Assert.IsType<ViewPattern>(matchExpr.Branches[0].Pattern);
        var callExpr = Assert.IsType<CallExpr>(viewPattern.ViewExpression);
        Assert.IsType<IfExpr>(callExpr.Function);
        var argument = Assert.IsType<IdentifierExpr>(Assert.Single(callExpr.PositionalArgs));
        Assert.Equal("x", argument.Name);
        var innerLiteral = Assert.IsType<LiteralPattern>(viewPattern.InnerPattern);
        Assert.Equal(7L, Assert.IsType<long>(innerLiteral.Value));
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_RejectsKeywordCommaViewPattern()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x
}

classify :: Int -> Int
{
    x => match x
    {
        view(if true then { normalize } else { normalize }, 7) => 30,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_view_keyword_comma_expr.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.False(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics.SelectMany(diagnostic => diagnostic.Helps),
            help => help.Contains("native view pattern", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesNestedNativeViewPatternInsideCtor()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

to_digit :: Int -> Int
{
    x => x
}

classify :: OptionI -> Int
{
    x => match x
    {
        Some((to_digit -> 7)) => 30,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_nested_view_ctor.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);

        var somePattern = Assert.IsType<CtorPattern>(matchExpr.Branches[0].Pattern);
        Assert.Equal("Some", somePattern.ConstructorName);
        Assert.Single(somePattern.PositionalPatterns);

        var nestedView = Assert.IsType<ViewPattern>(somePattern.PositionalPatterns[0]);
        var viewExpr = Assert.IsType<IdentifierExpr>(nestedView.ViewExpression);
        Assert.Equal("to_digit", viewExpr.Name);
        var innerLiteral = Assert.IsType<LiteralPattern>(nestedView.InnerPattern);
        Assert.Equal(7L, Assert.IsType<long>(innerLiteral.Value));
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesOrAndRangePatterns()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        1 | 2 => 10,
        3..5 => 20,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_or_range.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(3, matchExpr.Branches.Count);

        var orPattern = Assert.IsType<OrPattern>(matchExpr.Branches[0].Pattern);
        Assert.Equal(2, orPattern.Alternatives.Count);
        Assert.Equal(1L, Assert.IsType<long>(Assert.IsType<LiteralPattern>(orPattern.Alternatives[0]).Value));
        Assert.Equal(2L, Assert.IsType<long>(Assert.IsType<LiteralPattern>(orPattern.Alternatives[1]).Value));

        var rangePattern = Assert.IsType<RangePattern>(matchExpr.Branches[1].Pattern);
        Assert.Equal(3L, Assert.IsType<long>(Assert.IsType<LiteralPattern>(rangePattern.Start).Value));
        Assert.Equal(5L, Assert.IsType<long>(Assert.IsType<LiteralPattern>(rangePattern.End).Value));
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesListAndRestPatterns()
    {
        const string source = """
classify :: Int -> Int
{
    _ => match [1, 2, 3]
    {
        [head, ..tail] => head,
        [first, ..middle, last] => last,
        [] => 0,
        [..] => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_list_rest.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(4, matchExpr.Branches.Count);

        var listWithRest = Assert.IsType<ListPattern>(matchExpr.Branches[0].Pattern);
        Assert.Single(listWithRest.Elements);
        var headBinding = Assert.IsType<VarPattern>(listWithRest.Elements[0]);
        Assert.Equal("head", headBinding.Name);
        Assert.True(listWithRest.HasRestMarker);
        var restBinding = Assert.IsType<VarPattern>(listWithRest.RestPattern);
        Assert.Equal("tail", restBinding.Name);

        var listWithMiddleRest = Assert.IsType<ListPattern>(matchExpr.Branches[1].Pattern);
        Assert.Single(listWithMiddleRest.Elements);
        Assert.True(listWithMiddleRest.HasRestMarker);
        var middleBinding = Assert.IsType<VarPattern>(listWithMiddleRest.RestPattern);
        Assert.Equal("middle", middleBinding.Name);
        var lastBinding = Assert.IsType<VarPattern>(Assert.Single(listWithMiddleRest.SuffixElements));
        Assert.Equal("last", lastBinding.Name);

        var emptyList = Assert.IsType<ListPattern>(matchExpr.Branches[2].Pattern);
        Assert.Empty(emptyList.Elements);
        Assert.False(emptyList.HasRestMarker);

        var bareRest = Assert.IsType<ListPattern>(matchExpr.Branches[3].Pattern);
        Assert.Empty(bareRest.Elements);
        Assert.True(bareRest.HasRestMarker);
        Assert.Null(bareRest.RestPattern);
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesAndPatternWithOrPrecedence()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        (1 as n) & 1..3 => n,
        4 | 5 & 5 => 45,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_and.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(3, matchExpr.Branches.Count);

        var andPattern = Assert.IsType<AndPattern>(matchExpr.Branches[0].Pattern);
        Assert.Equal(2, andPattern.Conjuncts.Count);
        Assert.IsType<AsPattern>(andPattern.Conjuncts[0]);
        Assert.IsType<RangePattern>(andPattern.Conjuncts[1]);

        var orPattern = Assert.IsType<OrPattern>(matchExpr.Branches[1].Pattern);
        Assert.Equal(2, orPattern.Alternatives.Count);
        Assert.IsType<LiteralPattern>(orPattern.Alternatives[0]);
        Assert.IsType<AndPattern>(orPattern.Alternatives[1]);
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesNotPatternWithAndPrecedence()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        !1 & 1..3 => 13,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_not_and.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);

        var andPattern = Assert.IsType<AndPattern>(matchExpr.Branches[0].Pattern);
        Assert.Equal(2, andPattern.Conjuncts.Count);

        var notPattern = Assert.IsType<NotPattern>(andPattern.Conjuncts[0]);
        var innerLiteral = Assert.IsType<LiteralPattern>(notPattern.InnerPattern);
        Assert.Equal(1L, Assert.IsType<long>(innerLiteral.Value));
        Assert.IsType<RangePattern>(andPattern.Conjuncts[1]);
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesIdentifierOnlyGuardExpression()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        v when v => 1,
        false => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_guard_identifier.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);

        var guardedPattern = Assert.IsType<VarPattern>(matchExpr.Branches[0].Pattern);
        Assert.Equal("v", guardedPattern.Name);
        var guard = Assert.IsType<IdentifierExpr>(matchExpr.Branches[0].Guard);
        Assert.Equal("v", guard.Name);
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesBooleanLiteralGuardExpression()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        _ when true => 1,
        false => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_guard_bool_literal.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);

        var wildcard = Assert.IsType<WildcardPattern>(matchExpr.Branches[0].Pattern);
        Assert.NotNull(wildcard);
        var guardLiteral = Assert.IsType<LiteralExpr>(matchExpr.Branches[0].Guard);
        Assert.Equal(LiteralKind.Boolean, guardLiteral.Kind);
        Assert.True(Assert.IsType<bool>(guardLiteral.Value));
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesUnaryNotGuardExpression()
    {
        const string source = """
classify :: Bool -> Int
{
    x => match x
    {
        true when !x => 1,
        false => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_guard_unary_not.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);

        var unaryGuard = Assert.IsType<UnaryExpr>(matchExpr.Branches[0].Guard);
        Assert.Equal(UnaryOp.Not, unaryGuard.Operator);
        var operand = Assert.IsType<IdentifierExpr>(unaryGuard.Operand);
        Assert.Equal("x", operand.Name);
    }

    [Fact]
    public void Parser_MatchAdvancedPatterns_ParsesPatternGuardBindingExpression()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x => match x
    {
        _ when Some(n) <- x => n,
        _ => 0
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_pattern_guard_binding.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var branch = Assert.Single(classify.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);
        Assert.IsType<WildcardPattern>(matchExpr.Branches[0].Pattern);

        var guard = Assert.IsType<PatternGuardExpr>(matchExpr.Branches[0].Guard);
        var guardPattern = Assert.IsType<CtorPattern>(guard.Pattern);
        Assert.Equal("Some", guardPattern.ConstructorName);
        var boundVar = Assert.IsType<VarPattern>(Assert.Single(guardPattern.PositionalPatterns));
        Assert.Equal("n", boundVar.Name);
        var guardSource = Assert.IsType<IdentifierExpr>(guard.SourceExpression);
        Assert.Equal("x", guardSource.Name);
    }

    [Fact]
    public void Parser_FunctionPatternBranches_ParsesPatternGuardBindingExpression()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x when Some(n) <- x => n,
    _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_function_pattern_guard_binding.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        Assert.Equal(2, classify.Body.Count);

        var firstBranch = classify.Body[0];
        var pattern = Assert.IsType<VarPattern>(firstBranch.Pattern);
        Assert.Equal("x", pattern.Name);

        var guard = Assert.IsType<PatternGuardExpr>(firstBranch.Guard);
        var guardPattern = Assert.IsType<CtorPattern>(guard.Pattern);
        Assert.Equal("Some", guardPattern.ConstructorName);
        var boundVar = Assert.IsType<VarPattern>(Assert.Single(guardPattern.PositionalPatterns));
        Assert.Equal("n", boundVar.Name);
        var sourceExpr = Assert.IsType<IdentifierExpr>(guard.SourceExpression);
        Assert.Equal("x", sourceExpr.Name);
    }

    [Fact]
    public void Parser_FunctionPatternBranches_ParsesTuplePatternGuardBindingExpression()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

first_from_pair :: (OptionI, Int) -> Int
{
    pair when (Some(n), _) <- pair => n,
    _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_function_tuple_pattern_guard_binding.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "first_from_pair");
        var firstBranch = function.Body[0];

        var pattern = Assert.IsType<VarPattern>(firstBranch.Pattern);
        Assert.Equal("pair", pattern.Name);

        var guard = Assert.IsType<PatternGuardExpr>(firstBranch.Guard);
        var tuplePattern = Assert.IsType<TuplePattern>(guard.Pattern);
        Assert.Equal(2, tuplePattern.Elements.Count);

        var somePattern = Assert.IsType<CtorPattern>(tuplePattern.Elements[0]);
        Assert.Equal("Some", somePattern.ConstructorName);
        var boundVar = Assert.IsType<VarPattern>(Assert.Single(somePattern.PositionalPatterns));
        Assert.Equal("n", boundVar.Name);
        Assert.IsType<WildcardPattern>(tuplePattern.Elements[1]);

        var sourceExpr = Assert.IsType<IdentifierExpr>(guard.SourceExpression);
        Assert.Equal("pair", sourceExpr.Name);
    }

    [Fact]
    public void Parser_FunctionPatternBranches_AllowsMultipleWhenGuards()
    {
        const string source = """
OptionI :: type {
    Some(Int) , None
}

classify :: OptionI -> Int
{
    x when Some(n) <- x when n > 0 => n,
    _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_function_multiple_when_guards.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var classify = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "classify");
        var firstBranch = classify.Body[0];

        var guardChain = Assert.IsType<SequentialGuardExpr>(firstBranch.Guard);
        Assert.Equal(2, guardChain.Guards.Count);

        var patternGuard = Assert.IsType<PatternGuardExpr>(guardChain.Guards[0]);
        var guardPattern = Assert.IsType<CtorPattern>(patternGuard.Pattern);
        Assert.Equal("Some", guardPattern.ConstructorName);

        var compare = Assert.IsType<BinaryExpr>(guardChain.Guards[1]);
        Assert.Equal(Eidosc.Ast.BinaryOp.Greater, compare.Operator);
    }

    [Fact]
    public void Parser_LetPattern_ParsesTuplePatternBinding()
    {
        const string source = """
sum_pair :: (Int, Int) -> Int
{
    pair => {
        (a, b) := pair;
        a + b
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_let_pattern.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "sum_pair");
        var branch = Assert.Single(function.Body);
        var block = Assert.IsType<BlockExpr>(branch.Expression);
        var letDecl = Assert.IsType<LetDecl>(block.Statements[0]);
        var tuplePattern = letDecl.Pattern switch
        {
            TuplePattern directTuple => directTuple,
            OrPattern { Alternatives.Count: 1 } orPattern => Assert.IsType<TuplePattern>(orPattern.Alternatives[0]),
            _ => null!
        };
        Assert.NotNull(tuplePattern);
        Assert.Equal(2, tuplePattern.Elements.Count);
        Assert.IsType<VarPattern>(tuplePattern.Elements[0]);
        Assert.IsType<VarPattern>(tuplePattern.Elements[1]);
        var value = Assert.IsType<IdentifierExpr>(letDecl.Value);
        Assert.Equal("pair", value.Name);
    }

    [Fact]
    public void Parser_IfLetPattern_ParsesPatternScrutineeAndBranches()
    {
        const string source = """
Option[T] :: type { Some(T) , None }

unwrap_or_zero :: Option[Int] -> Int
{
    value => if let Some(n) = value then { n } else { 0 }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_if_let.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "unwrap_or_zero");
        var branch = Assert.Single(function.Body);
        var ifLet = Assert.IsType<IfLetExpr>(branch.Expression);

        var ctorPattern = Assert.IsType<CtorPattern>(ifLet.Pattern);
        Assert.Equal("Some", ctorPattern.ConstructorName);
        Assert.Single(ctorPattern.PositionalPatterns);
        var binding = Assert.IsType<VarPattern>(ctorPattern.PositionalPatterns[0]);
        Assert.Equal("n", binding.Name);

        var matched = Assert.IsType<IdentifierExpr>(ifLet.MatchedExpression);
        Assert.Equal("value", matched.Name);

        var thenBlock = Assert.IsType<BlockExpr>(ifLet.ThenBranch);
        Assert.NotNull(thenBlock.ResultExpression);
        var elseBlock = Assert.IsType<BlockExpr>(ifLet.ElseBranch);
        Assert.NotNull(elseBlock.ResultExpression);
    }

    [Fact]
    public void Parser_WhileLetPattern_ParsesPatternScrutineeAndBody()
    {
        const string source = """
Option[T] :: type { Some(T) , None }

sum_if_some :: Option[Int] -> Int
{
    value => {
        mut total := 0;
        while let Some(n) = value then {
            total := total + n;
        };
        total
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_while_let.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "sum_if_some");
        var branch = Assert.Single(function.Body);
        var outerBlock = Assert.IsType<BlockExpr>(branch.Expression);
        var whileLet = Assert.IsType<WhileLetExpr>(outerBlock.Statements[1]);

        var ctorPattern = Assert.IsType<CtorPattern>(whileLet.Pattern);
        Assert.Equal("Some", ctorPattern.ConstructorName);
        var binding = Assert.IsType<VarPattern>(Assert.Single(ctorPattern.PositionalPatterns));
        Assert.Equal("n", binding.Name);

        var matched = Assert.IsType<IdentifierExpr>(whileLet.MatchedExpression);
        Assert.Equal("value", matched.Name);
        Assert.IsType<BlockExpr>(whileLet.Body);
    }

    [Fact]
    public void Parser_PatternBindingModes_ParseRefMrefAndAsBindings()
    {
        const string source = """
demo :: Int -> Int
{
    x => {
        ref y := x;
        mref z := x;
        match x
        {
            (x as ref keep) => keep,
            _ => y
        }
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_binding_modes.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "demo");
        var branch = Assert.Single(function.Body);
        var block = Assert.IsType<BlockExpr>(branch.Expression);

        var refLet = Assert.IsType<LetDecl>(block.Statements[0]);
        var refPattern = refLet.Pattern switch
        {
            VarPattern directVar => directVar,
            OrPattern { Alternatives.Count: 1 } orPattern => Assert.IsType<VarPattern>(orPattern.Alternatives[0]),
            _ => null!
        };
        Assert.NotNull(refPattern);
        Assert.Equal("y", refPattern.Name);
        Assert.Equal(PatternBindingMode.SharedBorrow, refPattern.BindingMode);

        var mutLet = Assert.IsType<LetDecl>(block.Statements[1]);
        var mutPattern = mutLet.Pattern switch
        {
            VarPattern directVar => directVar,
            OrPattern { Alternatives.Count: 1 } orPattern => Assert.IsType<VarPattern>(orPattern.Alternatives[0]),
            _ => null!
        };
        Assert.NotNull(mutPattern);
        Assert.Equal("z", mutPattern.Name);
        Assert.Equal(PatternBindingMode.MutableBorrow, mutPattern.BindingMode);

        var matchExpr = Assert.IsType<MatchExpr>(block.ResultExpression);
        var asPattern = Assert.IsType<AsPattern>(matchExpr.Branches[0].Pattern);
        Assert.Equal("keep", asPattern.BindingName);
        Assert.Equal(PatternBindingMode.SharedBorrow, asPattern.BindingMode);
    }

    [Fact]
    public void Parser_PatternBindingModes_ParseAsMrefBinding()
    {
        const string source = """
demo :: Int -> MRef[Int]
{
    x => match x
    {
        (x as mref keep) => keep,
        _ => mref x
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "advanced_pattern_parsing_tests_binding_modes_as_mref.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();
        Assert.True(result.Success);

        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "demo");
        var branch = Assert.Single(function.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        var asPattern = Assert.IsType<AsPattern>(matchExpr.Branches[0].Pattern);

        Assert.Equal("keep", asPattern.BindingName);
        Assert.Equal(PatternBindingMode.MutableBorrow, asPattern.BindingMode);
    }
}
