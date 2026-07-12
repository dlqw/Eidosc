using Eidosc.Symbols;
using System.Xml;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;
using Xunit;
using EidosType = Eidosc.Types.Type;

namespace Eidosc.Tests.Unit.Types;

public sealed class TypeInfererRecoveryTests
{
    [Fact]
    public void InferExpression_UnsupportedExpression_ReturnsRecoveryType()
    {
        var inferer = new TypeInferer(new SymbolTable());
        var expression = new UnsupportedExpression();

        var type = inferer.InferExpression(expression);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported expression kind", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_UnsupportedPattern_UsesRecoveryType()
    {
        var pattern = new UnsupportedPattern
        {
            Span = CreateSpan()
        };
        var declaration = new LetDecl
        {
            Span = CreateSpan()
        };
        declaration.SetPattern(pattern);
        declaration.SetValue(CreateIntegerLiteral("1"));
        var module = CreateModule(declaration);
        var inferer = new TypeInferer(new SymbolTable());

        var success = inferer.Infer(module);

        Assert.False(success);
        var patternType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(pattern.InferredType));
        Assert.True(patternType.IsErrorRecovery);
        var declarationType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(declaration.InferredType));
        Assert.True(declarationType.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported pattern kind 'UnsupportedPattern'", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_UnsupportedTypeNode_UsesRecoveryType()
    {
        var typeNode = new UnsupportedTypeNode
        {
            Span = CreateSpan()
        };
        var declaration = CreateLetDeclaration("bad_type", CreateIntegerLiteral("1"), typeNode);
        var module = CreateModule(declaration);
        var inferer = new TypeInferer(new SymbolTable());

        var success = inferer.Infer(module);

        Assert.False(success);
        var annotationType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(typeNode.InferredType));
        Assert.True(annotationType.IsErrorRecovery);
        var declarationType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(declaration.InferredType));
        Assert.True(declarationType.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported type node kind 'UnsupportedTypeNode'", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_ArrowTypeMissingParameter_UsesRecoveryType()
    {
        var typeNode = CreateArrowType(null!, CreateNamedType("Int"));
        var declaration = CreateLetDeclaration("bad_arrow", CreateIntegerLiteral("1"), typeNode);
        var module = CreateModule(declaration);
        var inferer = new TypeInferer(new SymbolTable());

        var success = inferer.Infer(module);

        Assert.False(success);
        var declarationType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(declaration.InferredType));
        Assert.True(declarationType.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Arrow type requires a parameter type", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_ArrowTypeMissingReturn_UsesRecoveryType()
    {
        var typeNode = CreateArrowType(CreateNamedType("Int"), null!);
        var declaration = CreateLetDeclaration("bad_arrow", CreateIntegerLiteral("1"), typeNode);
        var module = CreateModule(declaration);
        var inferer = new TypeInferer(new SymbolTable());

        var success = inferer.Infer(module);

        Assert.False(success);
        var declarationType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(declaration.InferredType));
        Assert.True(declarationType.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Arrow type requires a return type", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_EffectfulTypeMissingInput_UsesRecoveryType()
    {
        var typeNode = new EffectfulType
        {
            Span = CreateSpan()
        };
        var declaration = CreateLetDeclaration("bad_effect", CreateIntegerLiteral("1"), typeNode);
        var module = CreateModule(declaration);
        var inferer = new TypeInferer(new SymbolTable());

        var success = inferer.Infer(module);

        Assert.False(success);
        var declarationType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(declaration.InferredType));
        Assert.True(declarationType.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Effectful type requires an input type", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_BinaryMissingOperands_UsesRecoveryType()
    {
        var binary = new BinaryExpr();
        binary.SetSpan(CreateSpan());
        binary.SetOperator(BinaryOp.Add);
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(binary);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Binary expression requires a left operand", StringComparison.Ordinal));
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Binary expression requires a right operand", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_UnsupportedBinaryOperator_UsesRecoveryType()
    {
        var binary = new BinaryExpr();
        binary.SetSpan(CreateSpan());
        binary.SetLeft(CreateIntegerLiteral("1"));
        binary.SetRight(CreateIntegerLiteral("2"));
        binary.SetOperator((BinaryOp)999);
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(binary);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Operator '999' is not supported by type inference", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_InfixCallMissingOperands_UsesRecoveryType()
    {
        var infixCall = new InfixCallExpr();
        infixCall.SetSpan(CreateSpan());
        infixCall.SetFunctionName("append");
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(infixCall);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Infix call requires a left operand", StringComparison.Ordinal));
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Infix call requires a right operand", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_UnaryMissingOperand_UsesRecoveryType()
    {
        var unary = new UnaryExpr();
        unary.SetSpan(CreateSpan());
        unary.SetOperator(UnaryOp.Not);
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(unary);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unary expression requires an operand", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_UnsupportedUnaryOperator_UsesRecoveryType()
    {
        var unary = new UnaryExpr();
        unary.SetSpan(CreateSpan());
        unary.SetOperator((UnaryOp)999);
        unary.SetOperand(CreateIntegerLiteral("1"));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(unary);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unary operator '999' is not supported by type inference", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_MethodCallMissingMethodName_UsesRecoveryType()
    {
        var method = new MethodCallExpr();
        method.SetSpan(CreateSpan());
        method.SetReceiver(CreateIntegerLiteral("1"));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(method);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Method call missing method name", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_IndexMissingObject_UsesRecoveryType()
    {
        var index = new IndexExpr();
        index.SetSpan(CreateSpan());
        index.SetIndex(CreateIntegerLiteral("0"));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(index);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Missing indexed object", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_RecoveredIndexMissingIndex_UsesRecoveryType()
    {
        var index = new IndexExpr();
        index.SetSpan(CreateSpan());
        index.SetObject(CreateIntegerList());
        index.MarkRecoveredMissingIndex();
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(index);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Missing index expression", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_DoBindingMissingValue_UsesRecoveryType()
    {
        var expression = new DoExpr
        {
            Span = CreateSpan()
        };
        expression.Bindings.Add(new DoBinding
        {
            Span = CreateSpan()
        });
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(expression);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Do binding requires a value expression", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_DoBindingMissingPattern_UsesRecoveryType()
    {
        var expression = new DoExpr
        {
            Span = CreateSpan()
        };
        var binding = new DoBinding
        {
            Span = CreateSpan()
        };
        SetDoBindingValue(binding, CreateIntegerLiteral("1"));
        expression.Bindings.Add(binding);
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(expression);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Do bind requires a pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_DoLetMissingResolvedSymbol_UsesRecoveryType()
    {
        var expression = new DoExpr
        {
            Span = CreateSpan()
        };
        expression.Bindings.Add(DoBinding.CreateLet("value", CreateIntegerLiteral("1")));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(expression);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Do let binding 'value' has no resolved symbol", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_DoBindingRecoveryKeepsWholeDoRecovered()
    {
        var expression = new DoExpr
        {
            Span = CreateSpan()
        };
        expression.Bindings.Add(new DoBinding
        {
            Span = CreateSpan()
        });
        expression.Bindings.Add(DoBinding.CreateExpr(CreateIntegerLiteral("1")));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(expression);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Do binding requires a value expression", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_AssignmentMissingValue_UsesRecoveryType()
    {
        var assignment = new Assignment
        {
            Span = CreateSpan()
        };
        assignment.SetTarget("target");
        var block = new BlockExpr
        {
            Span = CreateSpan()
        };
        block.Statements.Add(assignment);
        var inferer = new TypeInferer(new SymbolTable());

        inferer.InferExpression(block);

        var assignmentType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(assignment.InferredType));
        Assert.True(assignmentType.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Assignment requires a value expression", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_BlockWithRecoveredStatement_UsesRecoveryType()
    {
        var block = new BlockExpr
        {
            Span = CreateSpan()
        };
        block.AddStatement(new UnsupportedExpression
        {
            Span = CreateSpan()
        });
        var result = CreateIntegerLiteral("1");
        block.AddStatement(result);
        block.SetResultExpression(result);
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(block);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported expression kind", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_LambdaMissingBody_UsesRecoveryType()
    {
        var lambda = new LambdaExpr();
        lambda.SetSpan(CreateSpan());
        lambda.AddParameter(new WildcardPattern { Span = CreateSpan() });
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(lambda);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Lambda expression requires a body", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_CallNamedArgumentMissingValue_UsesRecoveryType()
    {
        var function = new LambdaExpr();
        function.SetSpan(CreateSpan());
        function.AddParameter(new WildcardPattern { Span = CreateSpan() });
        function.SetBody(CreateIntegerLiteral("1"));
        var call = new CallExpr();
        call.SetSpan(CreateSpan());
        call.SetFunction(function);
        call.AddNamedArg(new NamedArg
        {
            Name = "value",
            Span = CreateSpan()
        });
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(call);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Named argument 'value' requires a value expression", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_TypeDirectedMethodNamedArgumentMissingValue_UsesRecoveryType()
    {
        var symbolTable = new SymbolTable();
        var candidateId = symbolTable.RegisterSymbol(new FuncSymbol
        {
            Name = "map",
            Span = CreateSpan(),
            Parameters = [SymbolId.None, SymbolId.None]
        });
        var method = new MethodCallExpr();
        method.SetSpan(CreateSpan());
        method.SetReceiver(CreateIntegerLiteral("1"));
        method.SetMethodName("map");
        method.MarkExplicitCallSyntax();
        method.AddMethodCandidate(candidateId);
        method.AddNamedArg(new NamedArg
        {
            Name = "f",
            Span = CreateSpan()
        });
        var inferer = new TypeInferer(symbolTable);

        var type = inferer.InferExpression(method);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.False(method.SymbolId.IsValid);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Named argument 'f' requires a value expression", StringComparison.Ordinal));
        Assert.DoesNotContain(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("No overload of method", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_CtorFieldMissingValue_UsesRecoveryType()
    {
        var ctor = new CtorExpr();
        ctor.SetSpan(CreateSpan());
        ctor.SetConstructorName("Point");
        var field = new FieldInit();
        field.SetSpan(CreateSpan());
        field.SetFieldName("x");
        ctor.AddNamedArg(field);
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(ctor);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Field initializer 'x' requires a value expression", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_CtorFieldMissingName_UsesRecoveryType()
    {
        var ctor = new CtorExpr();
        ctor.SetSpan(CreateSpan());
        ctor.SetConstructorName("Point");
        var field = new FieldInit();
        field.SetSpan(CreateSpan());
        field.SetValue(CreateIntegerLiteral("1"));
        ctor.AddNamedArg(field);
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(ctor);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Field initializer requires a field name", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_BreakOutsideLoop_UsesRecoveryType()
    {
        var breakExpr = new BreakExpr
        {
            Span = CreateSpan()
        };
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(breakExpr);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Break expression can only be used inside a loop", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_ContinueOutsideLoop_UsesRecoveryType()
    {
        var continueExpr = new ContinueExpr
        {
            Span = CreateSpan()
        };
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(continueExpr);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Continue expression can only be used inside a loop", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_LoopMissingBody_UsesRecoveryType()
    {
        var loopExpr = new LoopExpr
        {
            Span = CreateSpan()
        };
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(loopExpr);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Loop expression requires a body", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_TupleWithRecoveredElement_UsesRecoveryType()
    {
        var tuple = new TupleExpr
        {
            Span = CreateSpan()
        };
        tuple.Elements.Add(new UnsupportedExpression
        {
            Span = CreateSpan()
        });
        var value = new LiteralExpr();
        value.SetSpan(CreateSpan());
        value.SetLiteral("1");
        tuple.Elements.Add(value);
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(tuple);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported expression kind", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_IfMissingCondition_UsesRecoveryType()
    {
        var ifExpr = new IfExpr();
        ifExpr.SetSpan(CreateSpan());
        ifExpr.SetThenBranch(CreateIntegerLiteral("1"));
        ifExpr.SetElseBranch(CreateIntegerLiteral("2"));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(ifExpr);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("If expression is missing a condition", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_IfMissingThenBranch_UsesRecoveryType()
    {
        var ifExpr = new IfExpr();
        ifExpr.SetSpan(CreateSpan());
        ifExpr.SetCondition(CreateBoolLiteral(true));
        ifExpr.SetElseBranch(CreateIntegerLiteral("2"));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(ifExpr);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("If expression requires a then branch", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_IfLetMissingPattern_UsesRecoveryType()
    {
        var ifLet = new IfLetExpr();
        ifLet.SetSpanValue(CreateSpan());
        ifLet.SetMatchedExpression(CreateIntegerLiteral("1"));
        ifLet.SetThenBranch(CreateIntegerLiteral("2"));
        ifLet.SetElseBranch(CreateIntegerLiteral("3"));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(ifLet);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("If-let expression is missing a pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_IfLetMissingThenBranch_UsesRecoveryType()
    {
        var ifLet = new IfLetExpr();
        ifLet.SetSpanValue(CreateSpan());
        ifLet.SetPattern(new WildcardPattern { Span = CreateSpan() });
        ifLet.SetMatchedExpression(CreateIntegerLiteral("1"));
        ifLet.SetElseBranch(CreateIntegerLiteral("3"));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(ifLet);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("If-let expression requires a then branch", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_WhileLetMissingPattern_UsesRecoveryType()
    {
        var whileLet = new WhileLetExpr();
        whileLet.SetSpanValue(CreateSpan());
        whileLet.SetMatchedExpression(CreateIntegerLiteral("1"));
        whileLet.SetBody(CreateIntegerLiteral("2"));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(whileLet);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("While-let expression is missing a pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_WhileLetMissingBody_UsesRecoveryType()
    {
        var whileLet = new WhileLetExpr();
        whileLet.SetSpanValue(CreateSpan());
        whileLet.SetPattern(new WildcardPattern { Span = CreateSpan() });
        whileLet.SetMatchedExpression(CreateIntegerLiteral("1"));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(whileLet);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("While-let expression requires a body", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_MatchWithoutBranches_UsesRecoveryType()
    {
        var match = new MatchExpr();
        match.SetSpan(CreateSpan());
        match.SetMatchedExpression(CreateIntegerLiteral("1"));
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(match);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Match expression requires at least one branch", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_MatchBranchMissingBody_UsesRecoveryType()
    {
        var match = new MatchExpr();
        match.SetSpan(CreateSpan());
        match.SetMatchedExpression(CreateIntegerLiteral("1"));
        var branch = new PatternBranch();
        branch.SetSpan(CreateSpan());
        branch.SetPattern(new WildcardPattern { Span = CreateSpan() });
        match.AddBranch(branch);
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(match);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Pattern branch requires a body expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_DirectFunctionBranchMissingBody_ReportsDiagnosticAndPreservesDeclaredSignature()
    {
        var symbolTable = new SymbolTable();
        var function = new FuncDef();
        function.SetSpan(CreateSpan());
        function.SetName("missing_body");
        function.SymbolId = symbolTable.DeclareFunction(function.Name, CreateSpan());
        function.SetSignature(CreateArrowType(CreateNamedType("Int"), CreateArrowType(CreateNamedType("Int"), CreateNamedType("Int"))));

        var branch = new PatternBranch();
        branch.SetSpan(CreateSpan());
        branch.SetPattern(new WildcardPattern { Span = CreateSpan() });
        function.SetBody([branch]);
        var module = CreateModule(function);
        var inferer = new TypeInferer(symbolTable);

        var success = inferer.Infer(module);

        Assert.False(success);
        var functionType = Assert.IsType<TyFun>(Assert.IsAssignableFrom<EidosType>(function.InferredType));
        var returnType = Assert.IsType<TyCon>(GetFinalResult(functionType));
        Assert.Equal("Int", returnType.Name);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Function 'missing_body' body branch requires a body expression", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_PatternFunctionBranchMissingBody_ReportsDiagnosticAndPreservesDeclaredSignature()
    {
        var symbolTable = new SymbolTable();
        var function = new FuncDef();
        function.SetSpan(CreateSpan());
        function.SetName("match_body");
        function.SymbolId = symbolTable.DeclareFunction(function.Name, CreateSpan());
        function.SetSignature(CreateArrowType(CreateNamedType("Int"), CreateNamedType("Int")));

        var branch = new PatternBranch();
        branch.SetSpan(CreateSpan());
        branch.SetPattern(CreateIntegerPattern("1"));
        function.SetBody([branch]);
        var module = CreateModule(function);
        var inferer = new TypeInferer(symbolTable);

        var success = inferer.Infer(module);

        Assert.False(success);
        var functionType = Assert.IsType<TyFun>(Assert.IsAssignableFrom<EidosType>(function.InferredType));
        var returnType = Assert.IsType<TyCon>(GetFinalResult(functionType));
        Assert.Equal("Int", returnType.Name);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Pattern branch requires a body expression", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_ListComprehensionMissingOutput_UsesRecoveryType()
    {
        var comprehension = new ListComprehension
        {
            Span = CreateSpan()
        };
        comprehension.AddQualifier(new Qualifier
        {
            Kind = QualifierKind.Generator,
            Span = CreateSpan(),
            GeneratorPattern = new WildcardPattern { Span = CreateSpan() },
            GeneratorExpression = CreateIntegerList()
        });
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(comprehension);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Seq comprehension requires an output expression", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_ListComprehensionGeneratorMissingSource_UsesRecoveryType()
    {
        var comprehension = new ListComprehension
        {
            Span = CreateSpan()
        };
        comprehension.SetOutput(CreateIntegerLiteral("1"));
        comprehension.AddQualifier(new Qualifier
        {
            Kind = QualifierKind.Generator,
            Span = CreateSpan(),
            GeneratorPattern = new WildcardPattern { Span = CreateSpan() }
        });
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(comprehension);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Seq comprehension generator requires a source expression", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_ListComprehensionGeneratorMissingPattern_UsesRecoveryType()
    {
        var comprehension = new ListComprehension
        {
            Span = CreateSpan()
        };
        comprehension.SetOutput(CreateIntegerLiteral("1"));
        comprehension.AddQualifier(new Qualifier
        {
            Kind = QualifierKind.Generator,
            Span = CreateSpan(),
            GeneratorExpression = CreateIntegerList()
        });
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(comprehension);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Seq comprehension generator requires a pattern", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_ListComprehensionGuardMissingExpression_UsesRecoveryType()
    {
        var comprehension = new ListComprehension
        {
            Span = CreateSpan()
        };
        comprehension.SetOutput(CreateIntegerLiteral("1"));
        comprehension.AddQualifier(new Qualifier
        {
            Kind = QualifierKind.Guard,
            Span = CreateSpan()
        });
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(comprehension);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Seq comprehension guard requires an expression", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_ListComprehensionUnsupportedQualifierKind_UsesRecoveryType()
    {
        var comprehension = new ListComprehension
        {
            Span = CreateSpan()
        };
        comprehension.SetOutput(CreateIntegerLiteral("1"));
        comprehension.AddQualifier(new Qualifier
        {
            Kind = (QualifierKind)999,
            Span = CreateSpan()
        });
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(comprehension);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported seq comprehension qualifier kind '999'", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_FieldAccessWithRecoveredReceiver_SuppressesReadableFieldCascade()
    {
        var method = new MethodCallExpr();
        method.SetSpan(CreateSpan());
        method.SetReceiver(new UnsupportedExpression
        {
            Span = CreateSpan()
        });
        method.SetMethodName("field");
        var inferer = new TypeInferer(new SymbolTable());

        var type = inferer.InferExpression(method);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported expression kind", StringComparison.Ordinal));
        Assert.DoesNotContain(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("has no readable field", StringComparison.Ordinal));
    }

    [Fact]
    public void InferExpression_TypeDirectedMethodWithRecoveredReceiver_DoesNotBindCandidate()
    {
        var symbolTable = new SymbolTable();
        var candidateId = symbolTable.RegisterSymbol(new FuncSymbol
        {
            Name = "map",
            Span = CreateSpan(),
            Parameters = [SymbolId.None]
        });
        var method = new MethodCallExpr();
        method.SetSpan(CreateSpan());
        method.SetReceiver(new UnsupportedExpression
        {
            Span = CreateSpan()
        });
        method.SetMethodName("map");
        method.MarkExplicitCallSyntax();
        method.AddMethodCandidate(candidateId);
        var inferer = new TypeInferer(symbolTable);

        var type = inferer.InferExpression(method);

        var tyVar = Assert.IsType<TyVar>(type);
        Assert.True(tyVar.IsErrorRecovery);
        Assert.False(method.SymbolId.IsValid);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported expression kind", StringComparison.Ordinal));
        Assert.DoesNotContain(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("No overload of method", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_MissingLetInitializer_UsesRecoveryType()
    {
        var pattern = new VarPattern();
        pattern.SetName("missing");
        var declaration = new LetDecl
        {
            Span = CreateSpan()
        };
        declaration.SetPattern(pattern);
        var module = CreateModule(declaration);
        var inferer = new TypeInferer(new SymbolTable());

        var success = inferer.Infer(module);

        Assert.False(success);
        var type = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(declaration.InferredType));
        Assert.True(type.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Let binding requires an initializer", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_UnsupportedLiteralPatternKind_UsesRecoveryType()
    {
        var pattern = new LiteralPattern
        {
            Span = CreateSpan()
        };
        pattern.SetLiteral("1");
        SetLiteralPatternType(pattern, (LiteralType)999);
        var value = new LiteralExpr();
        value.SetSpan(CreateSpan());
        value.SetLiteral("1");
        var declaration = new LetDecl
        {
            Span = CreateSpan()
        };
        declaration.SetPattern(pattern);
        declaration.SetValue(value);
        var module = CreateModule(declaration);
        var inferer = new TypeInferer(new SymbolTable());

        var success = inferer.Infer(module);

        Assert.False(success);
        var patternType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(pattern.InferredType));
        Assert.True(patternType.IsErrorRecovery);
        var declarationType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(declaration.InferredType));
        Assert.True(declarationType.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported literal pattern kind", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_TuplePatternWithRecoveredElement_UsesRecoveryType()
    {
        var badElement = new LiteralPattern
        {
            Span = CreateSpan()
        };
        badElement.SetLiteral("1");
        SetLiteralPatternType(badElement, (LiteralType)999);
        var goodElement = new VarPattern();
        goodElement.SetName("good");
        var pattern = new TuplePattern
        {
            Span = CreateSpan()
        };
        pattern.Elements.Add(badElement);
        pattern.Elements.Add(goodElement);
        var value = new TupleExpr
        {
            Span = CreateSpan()
        };
        value.Elements.Add(CreateIntegerLiteral("1"));
        value.Elements.Add(CreateIntegerLiteral("2"));
        var declaration = new LetDecl
        {
            Span = CreateSpan()
        };
        declaration.SetPattern(pattern);
        declaration.SetValue(value);
        var module = CreateModule(declaration);
        var inferer = new TypeInferer(new SymbolTable());

        var success = inferer.Infer(module);

        Assert.False(success);
        var patternType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(pattern.InferredType));
        Assert.True(patternType.IsErrorRecovery);
        var declarationType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(declaration.InferredType));
        Assert.True(declarationType.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported literal pattern kind", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_ListPatternWithRecoveredElement_UsesRecoveryType()
    {
        var badElement = new LiteralPattern
        {
            Span = CreateSpan()
        };
        badElement.SetLiteral("1");
        SetLiteralPatternType(badElement, (LiteralType)999);
        var goodElement = new VarPattern();
        goodElement.SetName("good");
        var pattern = new ListPattern
        {
            Span = CreateSpan()
        };
        pattern.Elements.Add(badElement);
        pattern.Elements.Add(goodElement);
        var value = new ListExpr();
        value.SetSpan(CreateSpan());
        value.AddElement(CreateIntegerLiteral("1"));
        value.AddElement(CreateIntegerLiteral("2"));
        var declaration = new LetDecl
        {
            Span = CreateSpan()
        };
        declaration.SetPattern(pattern);
        declaration.SetValue(value);
        var module = CreateModule(declaration);
        var inferer = new TypeInferer(new SymbolTable());

        var success = inferer.Infer(module);

        Assert.False(success);
        var patternType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(pattern.InferredType));
        Assert.True(patternType.IsErrorRecovery);
        var declarationType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(declaration.InferredType));
        Assert.True(declarationType.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported literal pattern kind", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_RangePatternWithRecoveredBoundary_SuppressesComparableCascade()
    {
        var start = new LiteralPattern
        {
            Span = CreateSpan()
        };
        start.SetLiteral("1");
        SetLiteralPatternType(start, (LiteralType)999);
        var pattern = new RangePattern
        {
            Span = CreateSpan(),
            Start = start,
            End = CreateIntegerPattern("3")
        };
        var declaration = new LetDecl
        {
            Span = CreateSpan()
        };
        declaration.SetPattern(pattern);
        declaration.SetValue(CreateIntegerLiteral("2"));
        var module = CreateModule(declaration);
        var inferer = new TypeInferer(new SymbolTable());

        var success = inferer.Infer(module);

        Assert.False(success);
        var patternType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(pattern.InferredType));
        Assert.True(patternType.IsErrorRecovery);
        var declarationType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(declaration.InferredType));
        Assert.True(declarationType.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Unsupported literal pattern kind", StringComparison.Ordinal));
        Assert.DoesNotContain(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("Range pattern expects Int or Char scrutinee", StringComparison.Ordinal));
    }

    [Fact]
    public void Infer_CtorPatternWithMissingAdtSymbol_UsesRecoveryType()
    {
        var adtSymbolId = new SymbolId(100);
        var ctorSymbolId = new SymbolId(101);
        var constructor = new Constructor
        {
            Span = CreateSpan(),
            SymbolId = ctorSymbolId
        };
        constructor.SetName("Ghost");
        var adt = new AdtDef
        {
            Span = CreateSpan(),
            SymbolId = adtSymbolId
        };
        adt.SetName("GhostType");
        adt.SetConstructors([constructor]);
        var pattern = new CtorPattern
        {
            Span = CreateSpan(),
            SymbolId = ctorSymbolId
        };
        pattern.SetConstructorName("Ghost");
        var value = new LiteralExpr();
        value.SetSpan(CreateSpan());
        value.SetLiteral("1");
        var declaration = new LetDecl
        {
            Span = CreateSpan()
        };
        declaration.SetPattern(pattern);
        declaration.SetValue(value);
        var module = CreateModule(adt, declaration);
        var inferer = new TypeInferer(new SymbolTable());

        var success = inferer.Infer(module);

        Assert.False(success);
        var patternType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(pattern.InferredType));
        Assert.True(patternType.IsErrorRecovery);
        var declarationType = Assert.IsType<TyVar>(Assert.IsAssignableFrom<EidosType>(declaration.InferredType));
        Assert.True(declarationType.IsErrorRecovery);
        Assert.Contains(
            inferer.Diagnostics,
            diagnostic => diagnostic.Message.Contains("ADT symbol is unavailable", StringComparison.Ordinal));
    }

    private static ModuleDecl CreateModule(params Declaration[] declarations)
    {
        var module = new ModuleDecl
        {
            Span = CreateSpan()
        };
        module.SetDeclarations([.. declarations]);
        return module;
    }

    private static LetDecl CreateLetDeclaration(string name, EidosAstNode value, TypeNode? typeAnnotation = null, bool isMutable = false)
    {
        var pattern = new VarPattern();
        pattern.SetName(name);

        var declaration = new LetDecl
        {
            Span = CreateSpan()
        };
        declaration.SetPattern(pattern);
        declaration.SetMutable(isMutable);
        declaration.SetTypeAnnotation(typeAnnotation);
        declaration.SetValue(value);
        return declaration;
    }

    private static SourceSpan CreateSpan()
    {
        return new SourceSpan(new SourceLocation(0, 0, 0, "type_recovery_tests.eidos"), 7);
    }

    private static LiteralExpr CreateIntegerLiteral(string value)
    {
        var literal = new LiteralExpr();
        literal.SetSpan(CreateSpan());
        literal.SetLiteral(value);
        return literal;
    }

    private static LiteralExpr CreateBoolLiteral(bool value)
    {
        var literal = new LiteralExpr();
        literal.SetSpan(CreateSpan());
        literal.SetLiteral(value ? "true" : "false");
        return literal;
    }

    private static ListExpr CreateIntegerList()
    {
        var list = new ListExpr();
        list.SetSpan(CreateSpan());
        list.AddElement(CreateIntegerLiteral("1"));
        return list;
    }

    private static TypePath CreateNamedType(string name)
    {
        var type = new TypePath();
        type.SetSpan(CreateSpan());
        type.SetTypeName(name);
        return type;
    }

    private static ArrowType CreateArrowType(TypeNode paramType, TypeNode returnType)
    {
        var arrow = new ArrowType();
        arrow.SetSpan(CreateSpan());
        arrow.SetParamType(paramType);
        arrow.SetReturnType(returnType);
        return arrow;
    }

    private static EidosType GetFinalResult(TyFun functionType)
    {
        var current = functionType;
        while (current.Result is TyFun nested)
        {
            current = nested;
        }

        return current.Result;
    }

    private static LiteralPattern CreateIntegerPattern(string value)
    {
        var pattern = new LiteralPattern
        {
            Span = CreateSpan()
        };
        pattern.SetLiteral(value);
        return pattern;
    }

    private static void SetLiteralPatternType(LiteralPattern pattern, LiteralType type)
    {
        var setter = typeof(LiteralPattern)
            .GetProperty(nameof(LiteralPattern.Type))?
            .GetSetMethod(nonPublic: true);

        Assert.NotNull(setter);
        setter.Invoke(pattern, [type]);
    }

    private static void SetDoBindingValue(DoBinding binding, EidosAstNode value)
    {
        var setter = typeof(DoBinding)
            .GetProperty(nameof(DoBinding.Value))?
            .GetSetMethod(nonPublic: true);

        Assert.NotNull(setter);
        setter.Invoke(binding, [value]);
    }

    private sealed record UnsupportedExpression : Expression
    {
        public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
        {
        }

        public override XmlElement ToXmlElement(XmlDocument doc) => CreateElement(doc, nameof(UnsupportedExpression));
    }

    private sealed record UnsupportedPattern : Pattern
    {
        public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
        {
        }

        public override XmlElement ToXmlElement(XmlDocument doc) => CreateElement(doc, nameof(UnsupportedPattern));
    }

    private sealed record UnsupportedTypeNode : TypeNode
    {
        public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
        {
        }

        public override XmlElement ToXmlElement(XmlDocument doc) => CreateElement(doc, nameof(UnsupportedTypeNode));
    }
}
