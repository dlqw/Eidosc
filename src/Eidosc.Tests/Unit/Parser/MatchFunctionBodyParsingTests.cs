using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Parser;

public class MatchFunctionBodyParsingTests
{
    [Fact]
    public void Parser_FunctionBodyWithNestedMatch_KeepsOnlyTopLevelPatternBranch()
    {
        const string source = """
classify :: String -> Int {
    s => match s {
        "x" => string_length(s),
        _ => string_length(s)
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_function_body_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "classify");
        var branch = Assert.Single(func.Body);
        var matchExpr = Assert.IsType<MatchExpr>(branch.Expression);
        Assert.Equal(2, matchExpr.Branches.Count);
    }

    [Fact]
    public void Parser_MatchBranchBlock_WithNestedMatchAndBlockBranches_ParsesBranchBody()
    {
        const string source = """
classify :: Option[Int] -> Int {
    value => match value {
        Some(inner) => {
            match Some(inner) {
                Some(number) => {
                    number
                },
                None() => {
                    0
                }
            }
        },
        None() => {
            0
        }
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_branch_block_nested_match_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "classify");
        var outerMatch = Assert.IsType<MatchExpr>(Assert.Single(func.Body).Expression);
        var someBody = Assert.IsType<BlockExpr>(outerMatch.Branches[0].Expression);
        Assert.IsType<MatchExpr>(Assert.Single(someBody.Statements));
    }

    [Fact]
    public void Parser_MatchBranchDirectNestedMatch_WithInnerBlockBranches_ParsesBranchBody()
    {
        const string source = """
classify :: Option[Int] -> Int {
    value => match value {
        Some(inner) => match inner {
            "}" => {
                1
            },
            _ => {
                0
            }
        },
        None() => {
            0
        }
    }
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_branch_direct_nested_match_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "classify");
        var outerMatch = Assert.IsType<MatchExpr>(Assert.Single(func.Body).Expression);
        var nestedMatch = Assert.IsType<MatchExpr>(outerMatch.Branches[0].Expression);
        var nestedBody = Assert.IsType<BlockExpr>(nestedMatch.Branches[0].Expression);
        Assert.IsType<LiteralExpr>(Assert.Single(nestedBody.Statements));
    }

    [Fact]
    public void Parser_FunctionBodyPatternBranch_ParsesEscapedCharLiteral()
    {
        const string source = """
classify :: Char -> Int {
    '\n' => 1,
    _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_function_body_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "classify");
        Assert.Equal(2, func.Body.Count);

        var firstPattern = Assert.IsType<LiteralPattern>(func.Body[0].Pattern);
        Assert.Equal(LiteralType.Char, firstPattern.Type);
        Assert.Equal('\n', Assert.IsType<char>(firstPattern.Value));
    }

    [Fact]
    public void Parser_FunctionBodyPatternBranch_WithWhenGuard_ParsesGuardExpression()
    {
        const string source = """
abs :: Int -> Int {
    n when n >= 0 => n,
    n => 0 - n
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_function_body_guard_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "abs");
        Assert.Equal(2, func.Body.Count);
        Assert.NotNull(func.Body[0].Guard);
        Assert.Null(func.Body[1].Guard);

        var guard = Assert.IsType<BinaryExpr>(func.Body[0].Guard);
        Assert.Equal(Eidosc.Ast.BinaryOp.GreaterEqual, guard.Operator);
    }

    [Fact]
    public void Parser_FunctionBodyStandardPatternBranch_KeepsRightHandIdentifierAsExpression()
    {
        const string source = """
project :: Int -> Int {
    x => y
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_function_body_standard_rhs_identifier_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "project");
        var branch = Assert.Single(func.Body);

        var pattern = Assert.IsType<VarPattern>(branch.Pattern);
        Assert.Equal("x", pattern.Name);
        var expression = Assert.IsType<IdentifierExpr>(branch.Expression);
        Assert.Equal("y", expression.Name);
    }

    [Fact]
    public void Parser_FunctionBodyCurriedPatternBranch_DesugarsHeadToTuplePattern()
    {
        const string source = """
OptionString :: type { SomeString(String) , NoneString }

optionStringMap :: OptionString -> (String -> String) -> OptionString
{
    SomeString(value) => mapper => SomeString(mapper(value)),
    NoneString() => _ => NoneString()
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_function_body_curried_head_tuple_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "optionStringMap");
        Assert.Equal(2, func.Body.Count);

        var firstTuple = Assert.IsType<TuplePattern>(func.Body[0].Pattern);
        Assert.Equal(2, firstTuple.Elements.Count);
        Assert.IsType<CtorPattern>(firstTuple.Elements[0]);
        var mapperPattern = Assert.IsType<VarPattern>(firstTuple.Elements[1]);
        Assert.Equal("mapper", mapperPattern.Name);

        var secondTuple = Assert.IsType<TuplePattern>(func.Body[1].Pattern);
        Assert.Equal(2, secondTuple.Elements.Count);
        var nonePattern = Assert.IsType<CtorPattern>(secondTuple.Elements[0]);
        Assert.Equal("NoneString", nonePattern.ConstructorName);
        Assert.Empty(nonePattern.PositionalPatterns);
        Assert.IsType<WildcardPattern>(secondTuple.Elements[1]);
    }

    [Fact]
    public void Parser_FunctionBodyCurriedPatternBranch_AllowsWildcardInLaterSegment()
    {
        const string source = """
pickFirst :: Int -> Int -> Int -> Int {
    a => b => _ => a
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_function_body_curried_wildcard_tail_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "pickFirst");
        var branch = Assert.Single(func.Body);
        var tuple = Assert.IsType<TuplePattern>(branch.Pattern);
        Assert.Equal(3, tuple.Elements.Count);
        Assert.IsType<VarPattern>(tuple.Elements[0]);
        Assert.IsType<VarPattern>(tuple.Elements[1]);
        Assert.IsType<WildcardPattern>(tuple.Elements[2]);
    }

    [Fact]
    public void Parser_FunctionBodyCurriedPatternBranch_AllowsGuardOnLaterSegment()
    {
        const string source = """
pickPositive :: Int -> Int -> Int {
    n => i when i > 0 => i,
    _ => _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_function_body_curried_guard_tail_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "pickPositive");
        Assert.Equal(2, func.Body.Count);

        var tuple = Assert.IsType<TuplePattern>(func.Body[0].Pattern);
        Assert.Equal(2, tuple.Elements.Count);
        Assert.IsType<VarPattern>(tuple.Elements[0]);
        Assert.IsType<VarPattern>(tuple.Elements[1]);

        var guard = Assert.IsType<BinaryExpr>(func.Body[0].Guard);
        Assert.Equal(Eidosc.Ast.BinaryOp.Greater, guard.Operator);
    }

    [Fact]
    public void Parser_FunctionBodyCurriedPatternBranch_AllowsMultipleGuardsOnLaterSegment()
    {
        const string source = """
OptionInt :: type { SomeInt(Int) , NoneInt }

addIfPositive :: Int -> OptionInt -> Int {
    base => opt when SomeInt(n) <- opt when n > 0 => base + n,
    _ => _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_function_body_curried_multi_guard_tail_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "addIfPositive");
        var tuple = Assert.IsType<TuplePattern>(func.Body[0].Pattern);
        Assert.Equal(2, tuple.Elements.Count);

        var guardChain = Assert.IsType<SequentialGuardExpr>(func.Body[0].Guard);
        Assert.Equal(2, guardChain.Guards.Count);
        Assert.IsType<PatternGuardExpr>(guardChain.Guards[0]);
        var boolGuard = Assert.IsType<BinaryExpr>(guardChain.Guards[1]);
        Assert.Equal(Eidosc.Ast.BinaryOp.Greater, boolGuard.Operator);
    }

    [Fact]
    public void Parser_FunctionBodyCurriedPatternBranch_ConvertsLaterCtorSegmentToCtorPattern()
    {
        const string source = """
OptionInt :: type { SomeInt(Int) , NoneInt }

zip_left :: OptionInt -> OptionInt -> OptionInt
{
    SomeInt(left) => SomeInt(right) => SomeInt(left),
    SomeInt(_) => NoneInt() => NoneInt(),
    NoneInt() => _ => NoneInt()
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_function_body_curried_ctor_tail_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "zip_left");
        var firstBranch = func.Body[0];

        var tuple = Assert.IsType<TuplePattern>(firstBranch.Pattern);
        Assert.Equal(2, tuple.Elements.Count);

        var leftPattern = Assert.IsType<CtorPattern>(tuple.Elements[0]);
        Assert.Equal("SomeInt", leftPattern.ConstructorName);
        Assert.Equal("left", Assert.IsType<VarPattern>(leftPattern.PositionalPatterns[0]).Name);

        var rightPattern = Assert.IsType<CtorPattern>(tuple.Elements[1]);
        Assert.Equal("SomeInt", rightPattern.ConstructorName);
        Assert.Equal("right", Assert.IsType<VarPattern>(rightPattern.PositionalPatterns[0]).Name);
    }

    [Fact]
    public void Parser_FunctionBodyCurriedPatternBranch_ConvertsFourCtorSegmentsToTuplePattern()
    {
        const string source = """
OptionInt :: type { SomeInt(Int) , NoneInt }

quad_sum :: OptionInt -> OptionInt -> OptionInt -> OptionInt -> Int
{
    SomeInt(first) => SomeInt(second) => SomeInt(third) => SomeInt(fourth) => first + second + third + fourth,
    _ => _ => _ => _ => 0
}
""";

        var options = new CompilationOptions
        {
            InputFile = "match_function_body_curried_quad_ctor_tail_tests.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "quad_sum");
        var firstBranch = func.Body[0];

        var tuple = Assert.IsType<TuplePattern>(firstBranch.Pattern);
        Assert.Equal(4, tuple.Elements.Count);

        var firstPattern = Assert.IsType<CtorPattern>(tuple.Elements[0]);
        Assert.Equal("SomeInt", firstPattern.ConstructorName);
        Assert.Equal("first", Assert.IsType<VarPattern>(firstPattern.PositionalPatterns[0]).Name);

        var secondPattern = Assert.IsType<CtorPattern>(tuple.Elements[1]);
        Assert.Equal("SomeInt", secondPattern.ConstructorName);
        Assert.Equal("second", Assert.IsType<VarPattern>(secondPattern.PositionalPatterns[0]).Name);

        var thirdPattern = Assert.IsType<CtorPattern>(tuple.Elements[2]);
        Assert.Equal("SomeInt", thirdPattern.ConstructorName);
        Assert.Equal("third", Assert.IsType<VarPattern>(thirdPattern.PositionalPatterns[0]).Name);

        var fourthPattern = Assert.IsType<CtorPattern>(tuple.Elements[3]);
        Assert.Equal("SomeInt", fourthPattern.ConstructorName);
        Assert.Equal("fourth", Assert.IsType<VarPattern>(fourthPattern.PositionalPatterns[0]).Name);
    }
}
