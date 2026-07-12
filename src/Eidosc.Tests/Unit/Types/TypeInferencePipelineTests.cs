using System;
using System.IO;
using System.Linq;
using Eidosc.Diagnostic;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Pipeline;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public partial class TypeInferencePipelineTests
{
    [Fact]
    public void Types_BlockWithoutTailExpression_DefaultsToUnit()
    {
        const string source = """
main :: Unit -> Unit
{
    _ => {
        mut x := 0;
        x + 1;
        x := 2
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Types_BlockWithSemicolonExpressionTail_DefaultsToUnit()
    {
        const string source = """
main :: Unit -> Unit
{
    _ => {
        1 + 2;
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Types_BlockWithExplicitExpressionTail_DoesNotDefaultToUnit()
    {
        const string source = """
main :: Unit -> Unit
{
    _ => {
        1 + 2
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Cannot unify", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("Unit", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_IfExpressionWithUnitThenBranch_AllowsMissingElse()
    {
        const string source = """
draw_if :: Bool -> Unit
{
    ok => if ok
    then
    {
        ()
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Types_IfExpressionWithNonUnitThenBranch_RejectsMissingElse()
    {
        const string source = """
choose :: Bool -> Int
{
    ok => if ok
    then
    {
        1
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("If branch type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_EffectfulSignatureEffect_IsInferredWithoutOutputTypeLeak()
    {
        const string source = """
Emitter :: effect;

emit :: String -> Unit need Emitter
{
    _ => ()
}

helper :: Unit -> Unit need Emitter
{
    _ => emit("ok")
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var helper = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "helper");
        var inferredType = Assert.IsType<TyFun>(helper.InferredType);
        Assert.NotNull(inferredType.Effects);
        var abilities = inferredType.Effects!;
        var inferredEffect = Assert.Single(abilities.Effects);

        Assert.Equal("Emitter", inferredEffect.Name);
        Assert.DoesNotContain("::Unit", inferredEffect.Name, StringComparison.Ordinal);
        Assert.Equal("Unit", Assert.IsType<TyCon>(inferredType.Result).Name);
    }

    [Fact]
    public void Types_HigherOrderCall_AcceptsFunctionWithTupleParameter()
    {
        const string source = """
pair_sum :: (Int, Int) -> Int
{
    (left, right) => left + right
}

apply_pair[F] :: (Int, Int) -> F -> Int
{
    pair => f => f(pair)
}

main :: Unit -> Int
{
    _ => apply_pair((1, 2))(pair_sum)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_CurriedFunctionBodyPatternBranch_WithFourCtorSegments_TypeChecks()
    {
        const string source = """
OptionInt :: type { SomeInt(Int) | NoneInt }

quad_sum :: OptionInt -> OptionInt -> OptionInt -> OptionInt -> Int
{
    SomeInt(first) => SomeInt(second) => SomeInt(third) => SomeInt(fourth) => first + second + third + fourth,
    _ => _ => _ => _ => 0
}

main :: Unit -> Int
{
    _ => quad_sum(SomeInt(1))(SomeInt(2))(SomeInt(3))(SomeInt(4))
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_CurriedFunctionBody_WithNestedMatchInRemainingLambda_TypeChecks()
    {
        const string source = """
Token :: type { Ident(String) | Punct(String) | End }
Tokens :: type { Cons(Token, Tokens) | Nil }
ParseResult :: type { ParseOk(Int, Tokens) | ParseError(String) }

helper :: String -> ParseResult
{
    name => ParseOk(1, Nil())
}

parse_after_keyword :: Int -> Tokens -> ParseResult
{
    env => tokens => {
        match tokens
        {
            Cons(Ident(name), rest) => match rest
            {
                Cons(Punct(punct), afteropen) => {
                    if punct == "{" then { helper(name) }
                    else if punct == ";" then { ParseOk(env, afteropen) }
                    else { ParseError("expected body") }
                },
                _ => ParseError("expected body")
            },
            _ => ParseError("expected declaration")
        }
    }
}

main :: Unit -> ParseResult
{
    _ => parse_after_keyword(2)(Cons(Ident("S"), Cons(Punct(";"), Nil())))
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("body result type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_CurriedFunctionBody_WithEccStyleSixArgumentTailParser_TypeChecks()
    {
        const string source = """
Token :: type { Punct(String) | Text(String) | End }
Tokens :: type { Cons(Token, Tokens) | Nil }
CType :: type { TyInt | TyPtr(CType) }
Stmt :: type { StmtDecl(String, CType) | StmtInit(String, CType, String) }
Stmts :: type { StmtCons(Stmt, Stmts) | StmtNil }
DeclItemListResult :: type { DeclItemListOk(Stmts, Tokens) | DeclItemListError(String) }

push_stmt :: Stmt -> Stmts -> Stmts
{
    stmt => acc => StmtCons(stmt, acc)
}

parse_initializer :: Tokens -> DeclItemListResult
{
    tokens => DeclItemListOk(StmtNil(), tokens)
}

parse_separator :: CType -> Tokens -> Stmts -> DeclItemListResult
{
    baseTy => tokens => acc => DeclItemListOk(acc, tokens)
}

parse_decl_item_tail :: Int -> CType -> String -> CType -> Tokens -> Stmts -> DeclItemListResult
{
    env => baseTy => name => fullTy => afterName => acc => {
        match afterName
        {
            Cons(Punct("="), Cons(Punct("{"), afterOpen)) => match parse_initializer(afterOpen)
            {
                DeclItemListOk(_, afterInit) => parse_separator(baseTy)(afterInit)(push_stmt(StmtInit(name, fullTy, "tree"))(acc)),
                DeclItemListError(error) => DeclItemListError(error)
            },
            Cons(Punct("="), Cons(Text(text), afterText)) => parse_separator(baseTy)(afterText)(push_stmt(StmtInit(name, fullTy, text))(acc)),
            _ => parse_separator(baseTy)(afterName)(push_stmt(StmtDecl(name, fullTy))(acc))
        }
    }
}

main :: Unit -> DeclItemListResult
{
    _ => parse_decl_item_tail(0)(TyInt())("x")(TyPtr(TyInt()))(Nil())(StmtNil())
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Level == DiagnosticLevel.Error &&
                          diagnostic.Message.Contains("body result type mismatch", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_EffectfulSignatureWithQualifiedEffectPath_ResolvesEffectSymbol()
    {
        const string source = """
Core :: module {
    Emitter :: effect;

    emit :: String -> Unit need Emitter
    {
        _ => ()
    }
}

helper :: Unit -> Unit need Core::Emitter
{
    _ => print_newline()
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var root = Assert.IsType<ModuleDecl>(result.Ast);
        var helper = Assert.Single(root.Declarations.OfType<FuncDef>(), function => function.Name == "helper");
        var inferredType = Assert.IsType<TyFun>(helper.InferredType);
        Assert.NotNull(inferredType.Effects);
        var inferredEffect = Assert.Single(inferredType.Effects!.Effects);

        Assert.True(inferredEffect.Symbol.IsValid);
        Assert.Equal("Emitter", inferredEffect.Name);
    }

    [Fact]
    public void Types_EffectfulSignatureWithDeepQualifiedEffectPath_ResolvesEffectSymbol()
    {
        const string source = """
Core.Io :: module
{
    Emitter :: effect;

    emit :: String -> Unit need Emitter
    {
        _ => ()
    }
}

helper :: Unit -> Unit need Core::Io::Emitter
{
    _ => print_newline()
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var root = Assert.IsType<ModuleDecl>(result.Ast);
        var helper = Assert.Single(root.Declarations.OfType<FuncDef>(), function => function.Name == "helper");
        var inferredType = Assert.IsType<TyFun>(helper.InferredType);
        Assert.NotNull(inferredType.Effects);
        var inferredEffect = Assert.Single(inferredType.Effects!.Effects);

        Assert.True(inferredEffect.Symbol.IsValid);
        Assert.Equal("Emitter", inferredEffect.Name);
    }

    [Fact]
    public void Types_EffectfulSignatureWithBraceEffectRow_InfersAllAbilities()
    {
        const string source = """
Emitter :: effect;

emit :: String -> Unit need Emitter
{
    _ => ()
}

Logger :: effect;

log :: String -> Unit need Logger
{
    _ => ()
}

helper :: Unit -> Unit need Emitter, Logger
{
    _ => print_newline()
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var helper = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "helper");
        var inferredType = Assert.IsType<TyFun>(helper.InferredType);
        Assert.NotNull(inferredType.Effects);
        var abilityNames = inferredType.Effects!.Effects
            .Select(ability => ability.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Emitter", "Logger"], abilityNames);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Message.Contains("no longer supported", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("Unit", Assert.IsType<TyCon>(inferredType.Result).Name);
    }

    [Fact]
    public void Types_EffectfulFunctionCall_PreservesDeclaredEffectTag()
    {
        const string source = """
Emitter :: effect;

emit :: String -> Unit need Emitter
{
    _ => ()
}

helper :: Unit -> Unit need Emitter
{
    _ => emit("ok")
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var helper = Assert.Single(module.Declarations.OfType<FuncDef>(), function => function.Name == "helper");
        var inferredType = Assert.IsType<TyFun>(helper.InferredType);
        var ability = Assert.Single(inferredType.Effects.Effects);

        Assert.Equal("Emitter", ability.Name);
        Assert.Equal("Unit", Assert.IsType<TyCon>(inferredType.Result).Name);
    }

    [Fact]
    public void Types_NestedModuleFunction_IsInferredRecursively()
    {
        const string source = """
Nested :: module {
    is_positive :: Int -> Bool
    {
        n => n > 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var root = Assert.IsType<ModuleDecl>(result.Ast);
        var nestedModule = Assert.Single(root.Declarations.OfType<ModuleDecl>());
        var nestedFunction = Assert.Single(nestedModule.Declarations.OfType<FuncDef>(), function => function.Name == "is_positive");
        var inferredType = Assert.IsType<TyFun>(nestedFunction.InferredType);
        var inferredResult = Assert.IsType<TyCon>(inferredType.Result);
        Assert.Equal("Bool", inferredResult.Name);
    }

    [Fact]
    public void Types_BlockWithTailExpression_MatchesFunctionResultType()
    {
        const string source = """
add1ViaBlock :: Int -> Int
{
    x => {
        y := x + 1;
        y
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Cannot unify type 'Int' with '()'", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_NestedCall_WithCurriedSignature_Succeeds()
    {
        const string source = """
add :: Int -> Int -> Int
{
    (x, y) => x + y
}
nested :: add(1)(2);
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
    }

    [Fact]
    public void Types_ForwardReferencedCurriedFunctionCall_Succeeds()
    {
        const string source = """
caller :: String -> Int -> Int
{
    (src, n) => pairLater(src)(n)
}

pairLater :: String -> Int -> Int
{
    (src, n) => string_length(src) + n
}

result :: caller("ab")(1);
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_NestedCall_OnNonFunctionResult_Fails()
    {
        const string source = """
id :: Int -> Int { x => x }
nested :: id(1)(2);
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Call target is not callable", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("Int", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_DotApplication_ChainWithoutParens_Succeeds()
    {
        const string source = """
inc :: Int -> Int { x => x + 1 }
double :: Int -> Int { x => x + x }
use :: Int -> Int { n => n.inc.double }
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_DotApplication_WithArgs_Succeeds()
    {
        const string source = """
add :: Int -> Int -> Int { (x, y) => x + y }
use :: Int -> Int { n => n.add(1) }
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_DotApplication_NumberLiteralReceiver_Succeeds()
    {
        const string source = """
inc :: Int -> Int { x => x + 1 }
use :: 3.inc;
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_DotApplication_NumberLiteralChain_Succeeds()
    {
        const string source = """
inc :: Int -> Int { x => x + 1 }
double :: Int -> Int { x => x + x }
use :: 3.inc.double;
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_ViewPatternNativeGeneralExpression_Succeeds()
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

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_ViewPatternPartialApplicationWithUnitScrutinee_Succeeds()
    {
        const string source = """
is_key :: Int -> Unit -> Bool
{
    key => _ => key == 81
}

read :: Unit -> Bool
{
    _ => match ()
    {
        (is_key(81) -> true) => true,
        _ => false
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Types_ViewPatternQualifiedEffectfulPartialApplicationWithUnitScrutinee_Succeeds()
    {
        const string source = """
Main :: module {
    is_key :: Int -> Unit -> Bool need FFI
    {
        key => _ => key == 81
    }

    read :: Unit -> Bool need FFI
    {
        _ => match ()
        {
            (Main::is_key(81) -> true) => true,
            _ => false
        }
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void Types_ViewPatternCallExpressionWithNonCallableResult_ReportsDedicatedDiagnostic()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x + 1
}

project :: Int -> Int
{
    x => normalize(x)
}

classify :: Int -> Int
{
    x => match x
    {
        (project(1) -> 7) => 30,
        _ => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "E4014");
        Assert.Contains("View-pattern expression is invalid", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("View expression inferred as: Int", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ViewPatternCallOnGeneralExpressionWithNonCallableResult_ReportsDedicatedDiagnostic()
    {
        const string source = """
normalize :: Int -> Int
{
    x => x + 1
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

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, item => item.Code == "E4014");
        Assert.Contains("View-pattern expression is invalid", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("not callable", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains(diagnostic.Notes, note => note.Contains("View expression inferred as: Int", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ViewPatternNonCallableExpression_ReportsDedicatedDiagnostic()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        (1 -> 7) => 30,
        _ => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "E4014" &&
                    item.Message.Contains("View-pattern expression is invalid", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Labels, label => label.Message.Contains("view expression", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("Scrutinee type inferred as: Int", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("requires `expr` to be callable", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_ViewPatternMultiParameterViewExpression_ReportsDedicatedDiagnostic()
    {
        const string source = """
classify :: Int -> Int
{
    x => match x
    {
        ({ (a, b) => a + b } -> 7) => 30,
        _ => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(
            result.Diagnostics,
            item => item.Code == "E4014" &&
                    item.Message.Contains("View-pattern expression is invalid", StringComparison.Ordinal));
        Assert.Contains(diagnostic.Notes, note => note.Contains("View expression inferred as:", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericCtorExpression_ArgumentTypeMismatch_Fails()
    {
        const string source = """
Option[T] :: type {
    Some(T) | None
}

bad :: Unit -> Option[Int]
{
    _ => Some("x")
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Int", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("String", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericCtorPattern_ArgumentTypeMismatch_Fails()
    {
        const string source = """
Option[T] :: type {
    Some(T) | None
}

bad :: Option[Int] -> Int
{
    value => match value
    {
        Some("x") => 1,
        None => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "E4000" &&
                          diagnostic.Message.Contains("Int", StringComparison.Ordinal) &&
                          diagnostic.Message.Contains("String", StringComparison.Ordinal));
    }

    [Fact]
    public void Types_GenericCtorPattern_BindsTypedVariable_Succeeds()
    {
        const string source = """
Option[T] :: type {
    Some(T) | None
}

plus_one :: Option[Int] -> Int
{
    value => match value
    {
        Some(v) => v + 1,
        None => 0
    }
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.DoesNotContain(result.Diagnostics, diagnostic => diagnostic.Code == "E4000");
    }

    [Fact]
    public void Types_NamedCtorPatternShorthand_WithFunctionNameCollision_Succeeds()
    {
        const string source = """
HttpResponse :: type {
    HttpResponse{ok: Bool}
}

ok :: HttpResponse -> Bool
{
    HttpResponse{ok} => ok
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void Types_UnaryRef_ReturnsRefType()
    {
        const string source = """
id :: Int -> Ref[Int]
{
    x => ref x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "id");
        var inferredType = Assert.IsType<TyFun>(function.InferredType);
        var inferredResult = Assert.IsType<TyRef>(inferredType.Result);

        var innerType = Assert.IsType<TyCon>(inferredResult.Inner);
        Assert.Equal("Int", innerType.Name);
    }

    [Fact]
    public void Types_UnaryMRef_ReturnsMRefType()
    {
        const string source = """
id :: Int -> MRef[Int]
{
    x => mref x
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), item => item.Name == "id");
        var inferredType = Assert.IsType<TyFun>(function.InferredType);
        var inferredResult = Assert.IsType<TyMutRef>(inferredType.Result);

        var innerType = Assert.IsType<TyCon>(inferredResult.Inner);
        Assert.Equal("Int", innerType.Name);
        Assert.Equal("MRef[Int]", inferredResult.ToString());
    }

    [Fact]
    public void Types_AutoDeref_RefInt_ArgumentUsedAsInt_InCall()
    {
        const string source = """
foo :: Int -> Int
{
    x => x
}

test :: Ref[Int] -> Int
{
    r => foo(r)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(d => d.Message))
            : "Expected success");
    }

    [Fact]
    public void Types_AutoDeref_MRefInt_ArgumentUsedAsInt_InCall()
    {
        const string source = """
foo :: Int -> Int
{
    x => x
}

test :: MRef[Int] -> Int
{
    r => foo(r)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);

        Assert.True(result.Success, result.Diagnostics.Count > 0
            ? string.Join("; ", result.Diagnostics.Select(d => d.Message))
            : "Expected success");
    }

    [Fact]
    public void Types_AutoDeref_RefInt_DoesNotTrigger_WhenExpectedIsRef()
    {
        const string source = """
read_ref :: Ref[Int] -> Ref[Int]
{
    r => r
}

test :: Ref[Int] -> Ref[Int]
{
    r => read_ref(r)
}
""";

        var result = RunPipeline(source, CompilationPhase.Types);
        Assert.True(result.Success);
    }

}
