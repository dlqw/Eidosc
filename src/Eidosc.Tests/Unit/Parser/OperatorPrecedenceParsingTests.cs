using System.Linq;
using System.Collections.Generic;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Parser;

public class OperatorPrecedenceParsingTests
{
    [Fact]
    public void Parser_LogicalAndOr_PrecedenceIsStable()
    {
        const string source = """
flag :: 1 < 2 && 3 < 4 || 5 < 6;
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetLetValue(result, "flag");
        var root = Assert.IsType<BinaryExpr>(value);
        Assert.Equal(Eidosc.Ast.BinaryOp.Or, root.Operator);

        var left = Assert.IsType<BinaryExpr>(root.Left);
        Assert.Equal(Eidosc.Ast.BinaryOp.And, left.Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.Less, Assert.IsType<BinaryExpr>(left.Left).Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.Less, Assert.IsType<BinaryExpr>(left.Right).Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.Less, Assert.IsType<BinaryExpr>(root.Right).Operator);
    }

    [Fact]
    public void Parser_ComparisonAgainstAdditiveInsideAnd_PreservesGrouping()
    {
        const string source = """
i :: 0;
len :: 10;
ok :: i + 1 < len && i + 2 < len;
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetLetValue(result, "ok");
        var root = Assert.IsType<BinaryExpr>(value);
        Assert.Equal(Eidosc.Ast.BinaryOp.And, root.Operator);

        var leftCmp = Assert.IsType<BinaryExpr>(root.Left);
        var rightCmp = Assert.IsType<BinaryExpr>(root.Right);
        Assert.Equal(Eidosc.Ast.BinaryOp.Less, leftCmp.Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.Less, rightCmp.Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.Add, Assert.IsType<BinaryExpr>(leftCmp.Left).Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.Add, Assert.IsType<BinaryExpr>(rightCmp.Left).Operator);
    }

    [Fact]
    public void Parser_LongLogicalOrChain_KeepsAllComparisonOperands()
    {
        const string source = """
c :: 0;
space :: c == 32 || c == 9 || c == 10 || c == 13;
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetLetValue(result, "space");
        var root = Assert.IsType<BinaryExpr>(value);

        var comparisons = new List<BinaryExpr>();
        CollectOrComparisons(root, comparisons);

        Assert.Equal(4, comparisons.Count);
        Assert.All(comparisons, comparison => Assert.Equal(Eidosc.Ast.BinaryOp.Equal, comparison.Operator));

        var rightValues = comparisons
            .Select(comparison => Assert.IsType<LiteralExpr>(comparison.Right).Value)
            .Select(Convert.ToInt32)
            .ToArray();
        Assert.Equal(new[] { 32, 9, 10, 13 }, rightValues);
    }

    [Fact]
    public void Parser_UnaryDeref_ParsesAsUnaryExpr()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        x := 1;
        ref y := x;
        z := *y;
        z
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var main = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "main");
        var branch = Assert.Single(main.Body);
        var block = Assert.IsType<BlockExpr>(branch.Expression);
        var valueDecl = Assert.IsType<LetDecl>(block.Statements[2]);
        var unary = Assert.IsType<UnaryExpr>(valueDecl.Value);
        Assert.Equal(Eidosc.Ast.UnaryOp.Deref, unary.Operator);
        var operand = Assert.IsType<IdentifierExpr>(unary.Operand);
        Assert.Equal("y", operand.Name);
    }

    [Fact]
    public void Parser_UnaryRef_ParsesAsUnaryExpr()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        x := 1;
        y := ref x;
        x
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var main = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "main");
        var branch = Assert.Single(main.Body);
        var block = Assert.IsType<BlockExpr>(branch.Expression);
        var valueDecl = Assert.IsType<LetDecl>(block.Statements[1]);
        var unary = Assert.IsType<UnaryExpr>(valueDecl.Value);
        Assert.Equal(Eidosc.Ast.UnaryOp.Ref, unary.Operator);
        var operand = Assert.IsType<IdentifierExpr>(unary.Operand);
        Assert.Equal("x", operand.Name);
    }

    [Fact]
    public void Parser_UnaryMRef_ParsesAsUnaryExpr()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        x := 1;
        y := mref x;
        x
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var main = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "main");
        var branch = Assert.Single(main.Body);
        var block = Assert.IsType<BlockExpr>(branch.Expression);
        var valueDecl = Assert.IsType<LetDecl>(block.Statements[1]);
        var unary = Assert.IsType<UnaryExpr>(valueDecl.Value);
        Assert.Equal(Eidosc.Ast.UnaryOp.MRef, unary.Operator);
        var operand = Assert.IsType<IdentifierExpr>(unary.Operand);
        Assert.Equal("x", operand.Name);
    }

    [Fact]
    public void Parser_UnaryRef_OnIndexProjection_ParsesOperandAsIndexExpr()
    {
        const string source = """
main :: Unit -> Int
{
    _ => {
        xs := [1, 2];
        y := ref xs[0];
        0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var main = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "main");
        var branch = Assert.Single(main.Body);
        var block = Assert.IsType<BlockExpr>(branch.Expression);
        var valueDecl = Assert.IsType<LetDecl>(block.Statements[1]);
        var unary = Assert.IsType<UnaryExpr>(valueDecl.Value);
        var index = Assert.IsType<IndexExpr>(unary.Operand);
        var target = Assert.IsType<IdentifierExpr>(index.Object);

        Assert.Equal(Eidosc.Ast.UnaryOp.Ref, unary.Operator);
        Assert.Equal("xs", target.Name);
    }

    [Fact]
    public void Parser_ComplexOperators_ParseWithExpectedPrecedence()
    {
        const string source = """
inc :: Int -> Int { x => x + 1 }
double :: Int -> Int { x => x + x }
piped :: 1 |> inc;
bound :: xs >>= f;
composedRight :: inc >>> double;
composedLeft :: double <<< inc;
mapped :: inc <$> maybeValue;
applied :: maybeFunction <*> maybeValue;
appended :: left <> right;
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        Assert.Equal(Eidosc.Ast.BinaryOp.Pipe, Assert.IsType<BinaryExpr>(GetLetValue(result, "piped")).Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.Bind, Assert.IsType<BinaryExpr>(GetLetValue(result, "bound")).Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.ComposeRight, Assert.IsType<BinaryExpr>(GetLetValue(result, "composedRight")).Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.ComposeLeft, Assert.IsType<BinaryExpr>(GetLetValue(result, "composedLeft")).Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.Fmap, Assert.IsType<BinaryExpr>(GetLetValue(result, "mapped")).Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.Ap, Assert.IsType<BinaryExpr>(GetLetValue(result, "applied")).Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.Append, Assert.IsType<BinaryExpr>(GetLetValue(result, "appended")).Operator);
    }

    [Fact]
    public void Parser_ExplicitTypeApplication_WithTupleTypeArgument_ParsesAsTypeApplication()
    {
        const string source = """
boxed :: box_value[(Int, Int)]((1, 2));
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var call = Assert.IsType<CallExpr>(GetLetValue(result, "boxed"));
        var typeApplication = Assert.IsType<IndexExpr>(call.Function);
        Assert.True(typeApplication.IsTypeApplication);
        Assert.Null(typeApplication.Index);

        var tupleType = Assert.IsType<TupleType>(Assert.Single(typeApplication.TypeArgs));
        Assert.Equal(2, tupleType.Elements.Count);
        Assert.All(tupleType.Elements, element => Assert.Equal("Int", Assert.IsType<TypePath>(element).TypeName));
    }

    [Fact]
    public void Parser_CustomSymbolicOperator_ParsesDeclarationInfixAndPrefixReference()
    {
        const string source = """
(|+|) :: Int -> Int -> Int
{
    left => right => left + right
}

infixed :: 1 + 2 |+| 3 * 4;
prefixed :: (|+|)(3, 4);
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var opFunction = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "|+|");
        Assert.Equal("|+|", opFunction.Name);

        var infixed = Assert.IsType<InfixCallExpr>(GetLetValue(result, "infixed"));
        Assert.Equal("|+|", infixed.FunctionName);
        Assert.Equal(Eidosc.Ast.BinaryOp.Add, Assert.IsType<BinaryExpr>(infixed.Left).Operator);
        Assert.Equal(Eidosc.Ast.BinaryOp.Multiply, Assert.IsType<BinaryExpr>(infixed.Right).Operator);

        var prefixed = Assert.IsType<CallExpr>(GetLetValue(result, "prefixed"));
        var function = Assert.IsType<IdentifierExpr>(prefixed.Function);
        Assert.Equal("|+|", function.Name);
        Assert.Equal(2, prefixed.PositionalArgs.Count);
    }

    private static CompilationResult RunPipeline(string source, CompilationPhase stopAt)
    {
        var options = new CompilationOptions
        {
            InputFile = "operator_precedence_parser_tests.eidos",
            StopAtPhase = stopAt,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static Eidosc.Ast.EidosAstNode GetLetValue(CompilationResult result, string letName)
    {
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var decl = Assert.Single(
            module.Declarations.OfType<LetDecl>(),
            value => value.Pattern is VarPattern { Name: var name } && name == letName);
        Assert.NotNull(decl.Value);
        return decl.Value!;
    }

    private static void CollectOrComparisons(Eidosc.Ast.EidosAstNode node, List<BinaryExpr> comparisons)
    {
        var binary = Assert.IsType<BinaryExpr>(node);
        if (binary.Operator == Eidosc.Ast.BinaryOp.Or)
        {
            Assert.NotNull(binary.Left);
            Assert.NotNull(binary.Right);
            CollectOrComparisons(binary.Left!, comparisons);
            CollectOrComparisons(binary.Right!, comparisons);
            return;
        }

        comparisons.Add(binary);
    }
}
