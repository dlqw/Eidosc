using System.Linq;
using Eidosc;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Parser;

public class ChainedCallParsingTests
{
    [Fact]
    public void Parser_NestedCall_ParsesAsCallOfCall()
    {
        const string source = """
id :: Int -> Int { x => x }
y :: id(1)(2);
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetLetValue(result, "y");
        var outerCall = Assert.IsType<CallExpr>(value);
        var innerCall = Assert.IsType<CallExpr>(outerCall.Function);
        var callee = Assert.IsType<IdentifierExpr>(innerCall.Function);

        Assert.Equal("id", callee.Name);
        Assert.Single(innerCall.PositionalArgs);
        Assert.Single(outerCall.PositionalArgs);
    }

    [Fact]
    public void Parser_MethodCallChain_BindsReceiverAndMethodName()
    {
        const string source = """
id :: Int -> Int { x => x }
toString :: Int -> Int { x => x }
z :: id(1).toString();
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetLetValue(result, "z");
        var methodCall = Assert.IsType<MethodCallExpr>(value);

        Assert.Equal("toString", methodCall.MethodName);
        Assert.NotNull(methodCall.Receiver);
        Assert.IsType<CallExpr>(methodCall.Receiver);
    }

    [Fact]
    public void Parser_MethodCall_OnIdentifierReceiver_BindsMethodAndReceiver()
    {
        const string source = """
add1 :: Int -> Int { x => x + 1 }
use :: Int -> Int { n => n.add1() }
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetFuncBodyValue(result, "use");
        var methodCall = Assert.IsType<MethodCallExpr>(value);

        Assert.Equal("add1", methodCall.MethodName);
        var receiver = Assert.IsType<IdentifierExpr>(methodCall.Receiver);
        Assert.Equal("n", receiver.Name);
        Assert.Empty(methodCall.PositionalArgs);
    }

    [Fact]
    public void Parser_DotApplication_WithoutParens_ParsesAsZeroArgMethodCall()
    {
        const string source = """
add1 :: Int -> Int { x => x + 1 }
use :: Int -> Int { n => n.add1 }
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetFuncBodyValue(result, "use");
        var methodCall = Assert.IsType<MethodCallExpr>(value);

        Assert.Equal("add1", methodCall.MethodName);
        var receiver = Assert.IsType<IdentifierExpr>(methodCall.Receiver);
        Assert.Equal("n", receiver.Name);
        Assert.Empty(methodCall.PositionalArgs);
    }

    [Fact]
    public void Parser_DotApplication_WithArgs_RemainsSingleMethodCallNode()
    {
        const string source = """
add2 :: Int -> Int -> Int { (x, y) => x + y }
use :: Int -> Int { n => n.add2(1) }
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetFuncBodyValue(result, "use");
        var methodCall = Assert.IsType<MethodCallExpr>(value);

        Assert.Equal("add2", methodCall.MethodName);
        Assert.Single(methodCall.PositionalArgs);
        Assert.IsType<LiteralExpr>(methodCall.PositionalArgs[0]);
    }

    [Fact]
    public void Parser_HandwrittenDotApplication_WithArgs_RemainsSingleMethodCallNode()
    {
        const string source = """
add2 :: Int -> Int -> Int { (x, y) => x + y }
use :: Int -> Int { n => n.add2(1) }
""";

        var result = RunPipeline(source, CompilationPhase.Parser, useHandwrittenParser: true);

        Assert.True(result.Success);
        var value = GetFuncBodyValue(result, "use");
        var methodCall = Assert.IsType<MethodCallExpr>(value);

        Assert.Equal("add2", methodCall.MethodName);
        Assert.True(methodCall.HasExplicitCallSyntax);
        Assert.Single(methodCall.PositionalArgs);
        Assert.IsType<LiteralExpr>(methodCall.PositionalArgs[0]);
    }

    [Fact]
    public void Parser_HandwrittenDotApplication_WithIdentifierArg_BindsArgumentExpression()
    {
        const string source = """
apply_to :: Int -> (Int -> Int) -> Int { x => f => f(x) }
id :: Int -> Int { x => x }
use :: Int -> Int { n => n.apply_to(id) }
""";

        var result = RunPipeline(source, CompilationPhase.Parser, useHandwrittenParser: true);

        Assert.True(result.Success);
        var value = GetFuncBodyValue(result, "use");
        var methodCall = Assert.IsType<MethodCallExpr>(value);

        Assert.Equal("apply_to", methodCall.MethodName);
        var arg = Assert.Single(methodCall.PositionalArgs);
        var identifier = Assert.IsType<IdentifierExpr>(arg);
        Assert.Equal("id", identifier.Name);
    }

    [Fact]
    public void Parser_DotApplication_ChainWithoutParens_NestsMethodCalls()
    {
        const string source = """
add1 :: Int -> Int { x => x + 1 }
mul2 :: Int -> Int { x => x * 2 }
use :: Int -> Int { n => n.add1.mul2 }
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetFuncBodyValue(result, "use");
        var outer = Assert.IsType<MethodCallExpr>(value);
        var inner = Assert.IsType<MethodCallExpr>(outer.Receiver);

        Assert.Equal("mul2", outer.MethodName);
        Assert.Equal("add1", inner.MethodName);
        var receiver = Assert.IsType<IdentifierExpr>(inner.Receiver);
        Assert.Equal("n", receiver.Name);
    }

    [Fact]
    public void Parser_HandwrittenDotApplication_ChainWithoutParens_NestsMethodCalls()
    {
        const string source = """
add1 :: Int -> Int { x => x + 1 }
mul2 :: Int -> Int { x => x * 2 }
use :: Int -> Int { n => n.add1.mul2 }
""";

        var result = RunPipeline(source, CompilationPhase.Parser, useHandwrittenParser: true);

        Assert.True(result.Success);
        var value = GetFuncBodyValue(result, "use");
        var outer = Assert.IsType<MethodCallExpr>(value);
        var inner = Assert.IsType<MethodCallExpr>(outer.Receiver);

        Assert.Equal("mul2", outer.MethodName);
        Assert.Equal("add1", inner.MethodName);
        var receiver = Assert.IsType<IdentifierExpr>(inner.Receiver);
        Assert.Equal("n", receiver.Name);
    }

    [Fact]
    public void Parser_DotApplication_NumberLiteralReceiver_ParsesAsMethodCall()
    {
        const string source = """
add1 :: Int -> Int { x => x + 1 }
y :: 3.add1;
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetLetValue(result, "y");
        var methodCall = Assert.IsType<MethodCallExpr>(value);
        var receiver = Assert.IsType<LiteralExpr>(methodCall.Receiver);

        Assert.Equal("add1", methodCall.MethodName);
        Assert.Empty(methodCall.PositionalArgs);
        Assert.Equal("3", receiver.Value?.ToString());
    }

    [Fact]
    public void Parser_DotApplication_NumberLiteralChain_NestsMethodCalls()
    {
        const string source = """
add1 :: Int -> Int { x => x + 1 }
mul2 :: Int -> Int { x => x * 2 }
y :: 3.add1.mul2;
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetLetValue(result, "y");
        var outer = Assert.IsType<MethodCallExpr>(value);
        var inner = Assert.IsType<MethodCallExpr>(outer.Receiver);
        var receiver = Assert.IsType<LiteralExpr>(inner.Receiver);

        Assert.Equal("mul2", outer.MethodName);
        Assert.Equal("add1", inner.MethodName);
        Assert.Equal("3", receiver.Value?.ToString());
    }

    [Fact]
    public void Parser_CallWithIdentifierArgument_PreservesPositionalArg()
    {
        const string source = """
id :: Int -> Int { x => x }
test :: Int -> Int { x => id(x) }
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetFuncBodyValue(result, "test");
        var call = Assert.IsType<CallExpr>(value);
        Assert.Single(call.PositionalArgs);
        var arg = Assert.IsType<IdentifierExpr>(call.PositionalArgs[0]);
        Assert.Equal("x", arg.Name);
    }

    [Fact]
    public void Parser_CtorCallWithLiteralArgument_PreservesPositionalArg()
    {
        const string source = """
Foo :: type { A:: type(Int) , B :: type {} }
make :: Unit -> Foo { _ => A(1) }
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var value = GetFuncBodyValue(result, "make");
        var call = Assert.IsType<CallExpr>(value);
        Assert.Equal("A", Assert.IsType<IdentifierExpr>(call.Function).Name);
        Assert.IsType<LiteralExpr>(Assert.Single(call.PositionalArgs));
    }

    [Fact]
    public void Parser_CtorPatternWithIdentifierArguments_PreservesConstructorNameAndBindings()
    {
        const string source = """
CToken :: type { TkInt:: type(Int) , TkEof :: type {} }
CTokenList :: type { TokNil :: type {} , TokCons:: type(CToken, CTokenList) }
tokenCount :: CTokenList -> Int {
    TokNil => 0,
    TokCons(tok, tail) => 1
}
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "tokenCount");
        var branch = Assert.Single(func.Body.Skip(1));
        var ctorPattern = Assert.IsType<CtorPattern>(branch.Pattern);

        Assert.Equal("TokCons", ctorPattern.ConstructorName);
        Assert.Equal(2, ctorPattern.PositionalPatterns.Count);
        Assert.Equal("tok", Assert.IsType<VarPattern>(ctorPattern.PositionalPatterns[0]).Name);
        Assert.Equal("tail", Assert.IsType<VarPattern>(ctorPattern.PositionalPatterns[1]).Name);
    }

    [Fact]
    public void Parser_CtorNamedShorthandPattern_ExpandsToBindingPattern()
    {
        const string source = """
HttpResponse :: type {ok:: Bool}
ok :: HttpResponse -> Bool {
    HttpResponse{ok} => ok
}
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "ok");
        var branch = Assert.Single(func.Body);
        var ctorPattern = Assert.IsType<CtorPattern>(branch.Pattern);
        var fieldPattern = Assert.Single(ctorPattern.NamedPatterns);

        Assert.Equal("ok", fieldPattern.FieldName);
        Assert.True(fieldPattern.IsShorthand);
        var binding = Assert.IsType<VarPattern>(fieldPattern.Pattern);
        Assert.Equal("ok", binding.Name);
        Assert.Equal(PatternBindingMode.ByValue, binding.BindingMode);
    }

    [Fact]
    public void Parser_ConstructorPositionalArgs_AreExtractedFromCtorArgs()
    {
        const string source = """
Pair :: type {
    Pair:: type(Int, Int)
}
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var adt = Assert.Single(module.Declarations.OfType<AdtDef>(), declaration => declaration.Name == "Pair");
        var ctor = Assert.Single(adt.Constructors, constructor => constructor.Name == "Pair");

        Assert.Equal(2, ctor.PositionalArgs.Count);
        Assert.Empty(ctor.NamedArgs);
    }

    [Fact]
    public void Parser_GadtConstructorReturnType_IsPreserved()
    {
        const string source = """
Expr[T] :: type {
    IntLit:: type(Int) case Expr[Int] ,
    BoolLit:: type(Bool) case Expr[Bool]
}
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var adt = Assert.Single(module.Declarations.OfType<AdtDef>(), declaration => declaration.Name == "Expr");
        var intLit = Assert.Single(adt.Constructors, constructor => constructor.Name == "IntLit");
        var boolLit = Assert.Single(adt.Constructors, constructor => constructor.Name == "BoolLit");

        var intReturn = Assert.IsType<TypePath>(intLit.ReturnType);
        var boolReturn = Assert.IsType<TypePath>(boolLit.ReturnType);
        Assert.Equal("Expr", intReturn.TypeName);
        Assert.Equal("Expr", boolReturn.TypeName);
        Assert.Equal("Int", Assert.IsType<TypePath>(Assert.Single(intReturn.TypeArgs)).TypeName);
        Assert.Equal("Bool", Assert.IsType<TypePath>(Assert.Single(boolReturn.TypeArgs)).TypeName);
    }

    [Fact]
    public void Parser_TypeBodyFields_AreExtractedFromProductType()
    {
        const string source = """
Person :: type {name:: Int, age:: Int}
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var adt = Assert.Single(module.Declarations.OfType<AdtDef>(), declaration => declaration.Name == "Person");
        Assert.Empty(adt.Constructors);
        Assert.Collection(
            adt.Fields,
            field =>
            {
                Assert.Equal("name", field.Name);
                Assert.NotNull(field.Type);
            },
            field =>
            {
                Assert.Equal("age", field.Name);
                Assert.NotNull(field.Type);
            });
    }

    [Fact]
    public void Parser_CtorExprNamedArgs_PreservesIdentifierFieldValues()
    {
        const string source = """
Range :: type {start:: Int, end:: Int}

sample :: Range{start: start, end: end};
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var value = Assert.Single(
            module.Declarations.OfType<LetDecl>(),
            declaration => declaration.Pattern is VarPattern { Name: "sample" });
        var ctor = Assert.IsType<CtorExpr>(value.Value);

        Assert.Equal("Range", ctor.ConstructorName);
        Assert.Equal(2, ctor.NamedArgs.Count);
        Assert.Equal("start", ctor.NamedArgs[0].FieldName);
        Assert.Equal("start", Assert.IsType<IdentifierExpr>(ctor.NamedArgs[0].Value).Name);
        Assert.Equal("end", ctor.NamedArgs[1].FieldName);
        Assert.Equal("end", Assert.IsType<IdentifierExpr>(ctor.NamedArgs[1].Value).Name);
    }

    [Fact]
    public void Parser_CtorExprRecordUpdate_PreservesUpdateBase()
    {
        const string source = """
sample :: GameState { ..state, tick: 0 };
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.True(result.Success);
        var ctor = Assert.IsType<CtorExpr>(GetLetValue(result, "sample"));

        Assert.Equal("GameState", ctor.ConstructorName);
        var updateBase = Assert.IsType<IdentifierExpr>(ctor.UpdateBase);
        Assert.Equal("state", updateBase.Name);
        var field = Assert.Single(ctor.NamedArgs);
        Assert.Equal("tick", field.FieldName);
    }

    [Fact]
    public void Parser_CtorExprRecordUpdateAfterField_ReportsSyntaxError()
    {
        const string source = """
sample :: GameState { tick: 0, ..state };
""";

        var result = RunPipeline(source, CompilationPhase.Parser);

        Assert.False(result.Success);
    }

    [Fact]
    public void Parser_RecordUpdateShorthand_PreservesBaseAndFields()
    {
        const string source = """
sample :: state.{ tick: 0 };
""";

        foreach (var useHandwrittenParser in new[] { false, true })
        {
            var result = RunPipeline(source, CompilationPhase.Parser, useHandwrittenParser);

            Assert.True(result.Success);
            var update = Assert.IsType<RecordUpdateExpr>(GetLetValue(result, "sample"));
            var updateBase = Assert.IsType<IdentifierExpr>(update.Base);
            Assert.Equal("state", updateBase.Name);
            var field = Assert.Single(update.NamedArgs);
            Assert.Equal("tick", field.FieldName);
        }
    }

    [Fact]
    public void Namer_MethodCall_ResolvesToFunctionSymbol()
    {
        const string source = """
id :: Int -> Int { x => x }
toString :: Int -> Int { x => x }
z :: id(1).toString();
""";

        var result = RunPipeline(source, CompilationPhase.Namer);

        Assert.True(result.Success);
        var value = GetLetValue(result, "z");
        var methodCall = Assert.IsType<MethodCallExpr>(value);

        Assert.True(methodCall.SymbolId.IsValid);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4001");
    }

    [Fact]
    public void Namer_DotApplicationChainWithoutParens_ResolvesToFunctionSymbols()
    {
        const string source = """
add1 :: Int -> Int { x => x + 1 }
mul2 :: Int -> Int { x => x * 2 }
use :: Int -> Int { n => n.add1.mul2 }
""";

        var result = RunPipeline(source, CompilationPhase.Namer);

        Assert.True(result.Success);
        var value = GetFuncBodyValue(result, "use");
        var outer = Assert.IsType<MethodCallExpr>(value);
        var inner = Assert.IsType<MethodCallExpr>(outer.Receiver);

        Assert.True(outer.SymbolId.IsValid);
        Assert.True(inner.SymbolId.IsValid);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E3000");
    }

    private static CompilationResult RunPipeline(
        string source,
        CompilationPhase stopAt,
        bool useHandwrittenParser = false)
    {
        var options = new CompilationOptions
        {
            InputFile = "chain_call_tests.eidos",
            StopAtPhase = stopAt,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }

    private static Eidosc.Ast.EidosAstNode GetFuncBodyValue(CompilationResult result, string funcName)
    {
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var decl = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == funcName);
        var branch = Assert.Single(decl.Body);
        Assert.NotNull(branch.Expression);
        return branch.Expression!;
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
}
