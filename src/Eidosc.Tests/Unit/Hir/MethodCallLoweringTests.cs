using System;
using System.IO;
using System.Linq;
using Eidosc.Hir;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Hir;

public class MethodCallLoweringTests
{
    [Fact]
    public void HirBuilder_MethodCall_LowersToRegularCallWithReceiverAsFirstArgument()
    {
        const string source = """
id :: Int -> Int { x => x }
toString :: Int -> Int { x => x }
z :: id(1).toString();
""";

        var options = new CompilationOptions
        {
            InputFile = "method_call_lowering.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        };

        var result = new CompilationPipeline(source, options).Run();

        Assert.True(result.Success);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code?.StartsWith("E5", StringComparison.Ordinal) == true &&
                          diagnostic.Message?.Contains("MethodCallExpr", StringComparison.Ordinal) == true);

        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var hirVal = Assert.Single(module.Declarations.OfType<HirVal>(), value => value.Name == "z");
        var call = Assert.IsType<HirCall>(hirVal.Initializer);
        var callee = Assert.IsType<HirVar>(call.Function);

        Assert.Equal("toString", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Method, call.SurfaceSyntax);
        Assert.False(call.HasExplicitOwner);
        Assert.Equal(0, call.ReceiverArgumentIndex);
        Assert.Equal(1, call.InjectedArgumentCount);
        Assert.Single(call.Arguments);
        Assert.IsType<HirCall>(call.Arguments[0]);
    }

    [Fact]
    public void HirBuilder_DotApplicationWithoutParens_LowersToRegularCall()
    {
        const string source = """
inc :: Int -> Int { x => x + 1 }
use :: Int -> Int { n => n.inc }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_no_parens_lowering.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var use = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "use");
        var call = Assert.IsType<HirCall>(use.Body);
        var callee = Assert.IsType<HirVar>(call.Function);

        Assert.Equal("inc", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Method, call.SurfaceSyntax);
        Assert.False(call.HasExplicitOwner);
        Assert.Equal(0, call.ReceiverArgumentIndex);
        Assert.Equal(1, call.InjectedArgumentCount);
        Assert.Single(call.Arguments);
        var receiver = Assert.IsType<HirVar>(call.Arguments[0]);
        Assert.Equal("n", receiver.Name);
    }

    [Fact]
    public void HirBuilder_DotApplicationChainWithoutParens_LowersToNestedCalls()
    {
        const string source = """
inc :: Int -> Int { x => x + 1 }
double :: Int -> Int { x => x + x }
use :: Int -> Int { n => n.inc.double }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_chain_no_parens_lowering.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var use = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "use");
        var outer = Assert.IsType<HirCall>(use.Body);
        var outerCallee = Assert.IsType<HirVar>(outer.Function);

        Assert.Equal("double", outerCallee.Name);
        Assert.Single(outer.Arguments);
        var inner = Assert.IsType<HirCall>(outer.Arguments[0]);
        var innerCallee = Assert.IsType<HirVar>(inner.Function);
        Assert.Equal("inc", innerCallee.Name);
        Assert.Single(inner.Arguments);
        var receiver = Assert.IsType<HirVar>(inner.Arguments[0]);
        Assert.Equal("n", receiver.Name);
    }

    [Fact]
    public void HirBuilder_DotApplicationWithArgs_KeepsReceiverAsFirstArgument()
    {
        const string source = """
add :: Int -> Int -> Int { (x, y) => x + y }
use :: Int -> Int { n => n.add(1) }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_with_args_lowering.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var use = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "use");
        var call = Assert.IsType<HirCall>(use.Body);
        var callee = Assert.IsType<HirVar>(call.Function);

        Assert.Equal("add", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Method, call.SurfaceSyntax);
        Assert.False(call.HasExplicitOwner);
        Assert.Equal(0, call.ReceiverArgumentIndex);
        Assert.Equal(1, call.InjectedArgumentCount);
        Assert.Equal(2, call.Arguments.Count);
        Assert.IsType<HirVar>(call.Arguments[0]);
        Assert.IsType<HirLiteral>(call.Arguments[1]);
    }

    [Fact]
    public void HirBuilder_DotApplication_NumberLiteralReceiver_LowersToRegularCall()
    {
        const string source = """
inc :: Int -> Int { x => x + 1 }
use :: 3.inc;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_number_receiver_lowering.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var value = Assert.Single(module.Declarations.OfType<HirVal>(), declaration => declaration.Name == "use");
        var call = Assert.IsType<HirCall>(value.Initializer);
        var callee = Assert.IsType<HirVar>(call.Function);

        Assert.Equal("inc", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Method, call.SurfaceSyntax);
        Assert.Single(call.Arguments);
        Assert.IsType<HirLiteral>(call.Arguments[0]);
    }

    [Fact]
    public void HirBuilder_DotApplication_NumberLiteralChain_LowersToNestedCalls()
    {
        const string source = """
inc :: Int -> Int { x => x + 1 }
double :: Int -> Int { x => x + x }
use :: 3.inc.double;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_number_chain_lowering.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var value = Assert.Single(module.Declarations.OfType<HirVal>(), declaration => declaration.Name == "use");
        var outer = Assert.IsType<HirCall>(value.Initializer);
        var outerCallee = Assert.IsType<HirVar>(outer.Function);

        Assert.Equal("double", outerCallee.Name);
        Assert.Single(outer.Arguments);
        var inner = Assert.IsType<HirCall>(outer.Arguments[0]);
        var innerCallee = Assert.IsType<HirVar>(inner.Function);
        Assert.Equal("inc", innerCallee.Name);
        Assert.Single(inner.Arguments);
        Assert.IsType<HirLiteral>(inner.Arguments[0]);
    }

    [Fact]
    public void HirBuilder_DotApplication_OnMRefReceiver_ToRefMethod_KeepsReceiverAsFirstArgument()
    {
        const string source = """
read :: Ref[Int] -> Int { r => r }
use :: Int -> Int { x => (mref x).read }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_mref_receiver_to_ref_method.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var use = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "use");
        var call = Assert.IsType<HirCall>(use.Body);
        var callee = Assert.IsType<HirVar>(call.Function);
        var receiver = Assert.Single(call.Arguments);
        var unary = Assert.IsType<HirUnaryOp>(receiver);

        Assert.Equal("read", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Method, call.SurfaceSyntax);
        Assert.Equal(Eidosc.Hir.UnaryOp.MRef, unary.Operator);
    }

    [Fact]
    public void HirBuilder_DotApplication_OnRefReceiver_ToRefMethod_KeepsReceiverAsFirstArgument()
    {
        const string source = """
read :: Ref[Int] -> Int { r => r }
use :: Ref[Int] -> Int { r => r.read }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_ref_receiver_to_ref_method.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var use = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "use");
        var call = Assert.IsType<HirCall>(use.Body);
        var callee = Assert.IsType<HirVar>(call.Function);
        var receiver = Assert.Single(call.Arguments);
        var varRef = Assert.IsType<HirVar>(receiver);

        Assert.Equal("read", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Method, call.SurfaceSyntax);
        Assert.Equal("r", varRef.Name);
    }

    [Fact]
    public void HirBuilder_BareDotPrefersFieldRead_OverSameNamedZeroArgCall()
    {
        const string source = """
Range :: type {
    start: Int, end: Int
}

start :: Ref[Range] -> Bool { _ => false }
read_field :: Ref[Range] -> Int { r => r.start }
read_method :: Ref[Range] -> Bool { r => r.start() }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_field_over_same_named_call.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;

        var readField = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "read_field");
        var field = Assert.IsType<HirFieldAccess>(readField.Body);
        Assert.Equal("start", field.FieldName);

        var readMethod = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "read_method");
        var call = Assert.IsType<HirCall>(readMethod.Body);
        var callee = Assert.IsType<HirVar>(call.Function);
        Assert.Equal("start", callee.Name);
    }

    [Fact]
    public void HirBuilder_FieldRead_ThenDotApplication_KeepsFieldAccessAsReceiver()
    {
        const string source = """
Range :: type {
    start: Int, end: Int
}

inc :: Int -> Int { x => x + 1 }
use :: Ref[Range] -> Int { r => r.start.inc }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_after_field_read.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var use = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "use");
        var call = Assert.IsType<HirCall>(use.Body);
        var callee = Assert.IsType<HirVar>(call.Function);
        var receiver = Assert.Single(call.Arguments);
        var field = Assert.IsType<HirFieldAccess>(receiver);

        Assert.Equal("inc", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Method, call.SurfaceSyntax);
        Assert.Equal("start", field.FieldName);
    }

    [Fact]
    public void HirBuilder_FieldThenDotApplication_OnRefRecordContainingRef_KeepsFieldAccessAsReceiver()
    {
        const string source = """
ReaderBox[T] :: type {
    reader: Ref[T], tag: Int
}

read :: Ref[Int] -> Int { r => r }
use :: Ref[ReaderBox[Int]] -> Int { box => box.reader.read }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_after_ref_field_read.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var use = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "use");
        var call = Assert.IsType<HirCall>(use.Body);
        var callee = Assert.IsType<HirVar>(call.Function);
        var receiver = Assert.Single(call.Arguments);
        var field = Assert.IsType<HirFieldAccess>(receiver);

        Assert.Equal("read", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Method, call.SurfaceSyntax);
        Assert.Equal("reader", field.FieldName);
    }

    [Fact]
    public void HirBuilder_FieldThenDotApplication_OnRefRecordContainingMRef_KeepsFieldAccessAsReceiver()
    {
        const string source = """
WriterBox[T] :: type {
    writer: MRef[T], tag: Int
}

read :: Ref[Int] -> Int { r => r }
use :: Ref[WriterBox[Int]] -> Int { box => box.writer.read }
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_after_mref_field_read.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var use = Assert.Single(module.Declarations.OfType<HirFunc>(), function => function.Name == "use");
        var call = Assert.IsType<HirCall>(use.Body);
        var callee = Assert.IsType<HirVar>(call.Function);
        var receiver = Assert.Single(call.Arguments);
        var field = Assert.IsType<HirFieldAccess>(receiver);

        Assert.Equal("read", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Method, call.SurfaceSyntax);
        Assert.Equal("writer", field.FieldName);
    }

    [Fact]
    public void HirBuilder_IndexThenFieldRead_OnRefListOfRecords_LowersToFieldAccess()
    {
        const string source = """
Range :: type {
    start: Int, end: Int
}

first_start :: Ref[Seq[Range]] -> Int
{
    xs => xs[0].start
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_index_then_field_read.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var function = Assert.Single(module.Declarations.OfType<HirFunc>(), item => item.Name == "first_start");
        var field = Assert.IsType<HirFieldAccess>(function.Body);
        var index = Assert.IsType<HirIndexAccess>(field.Target);

        Assert.Equal("start", field.FieldName);
        Assert.Equal(HirIndexAccessKind.RuntimeArray, index.TargetKind);
    }

    [Fact]
    public void HirBuilder_IndexThenDotApplication_OnRefListOfRefs_KeepsIndexAccessAsReceiver()
    {
        const string source = """
read :: Ref[Int] -> Int { r => r }

use :: Ref[Seq[Ref[Int]]] -> Int
{
    xs => xs[0].read
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "dot_application_after_index_read.eidos",
            StopAtPhase = CompilationPhase.Hir,
                UseColors = false
        }).Run();

        Assert.True(result.Success);
        Assert.NotNull(result.HirModule);
        var module = result.HirModule!;
        var function = Assert.Single(module.Declarations.OfType<HirFunc>(), item => item.Name == "use");
        var call = Assert.IsType<HirCall>(function.Body);
        var callee = Assert.IsType<HirVar>(call.Function);
        var receiver = Assert.Single(call.Arguments);
        var index = Assert.IsType<HirIndexAccess>(receiver);

        Assert.Equal("read", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Method, call.SurfaceSyntax);
        Assert.Equal(HirIndexAccessKind.RuntimeArray, index.TargetKind);
    }

    [Fact]
    public void HirBuilder_InfixCall_LowersToSingleRegularCallWithTwoArguments()
    {
        const string source = """
join :: Int -> Int -> Int { left => right => left + right }
use :: 1 `join` 2;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "infix_call_lowering.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<HirModule>(result.HirModule);
        var use = Assert.Single(module.Declarations.OfType<HirVal>(), value => value.Name == "use");
        var call = Assert.IsType<HirCall>(use.Initializer);
        var callee = Assert.IsType<HirVar>(call.Function);

        Assert.Equal("join", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Infix, call.SurfaceSyntax);
        Assert.False(call.HasExplicitOwner);
        Assert.Null(call.ReceiverArgumentIndex);
        Assert.Equal(0, call.InjectedArgumentCount);
        Assert.Equal(2, call.Arguments.Count);
        Assert.All(call.Arguments, argument => Assert.IsNotType<HirCall>(argument));
    }

    [Fact]
    public void HirBuilder_QualifiedCurriedDirectCall_FlattensAndPreservesExplicitOwnerMetadata()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"eidosc_qualified_curried_hir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        var moduleFile = Path.Combine(tempDir, "ProbeMath.eidos");
        var entryFile = Path.Combine(tempDir, "main.eidos");

        const string moduleSource = """
ProbeMath :: module {
    add :: Int -> Int -> Int { x => y => x + y }
}
""";

        const string entrySource = """
import ProbeMath

use :: Unit -> Int
{
    _ => ProbeMath.add(1)(2)
}
""";

        File.WriteAllText(moduleFile, moduleSource);
        File.WriteAllText(entryFile, entrySource);

        try
        {
            var result = new CompilationPipeline(entrySource, new CompilationOptions
            {
                InputFile = entryFile,
                StopAtPhase = CompilationPhase.Hir,
                UseColors = false
            }).Run();

            Assert.True(
                result.Success,
                string.Join(Environment.NewLine, result.Diagnostics.Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
            var module = Assert.IsType<HirModule>(result.HirModule);
            var use = Assert.Single(module.Declarations.OfType<HirFunc>(), value => value.Name == "use");
            var call = Assert.IsType<HirCall>(use.Body);
            var callee = Assert.IsType<HirVar>(call.Function);

            Assert.Equal("ProbeMath.add", callee.Name);
            Assert.Equal(HirCallSurfaceSyntax.Direct, call.SurfaceSyntax);
            Assert.True(call.HasExplicitOwner);
            Assert.Equal("ProbeMath", call.OwnerPath);
            Assert.Null(call.ReceiverArgumentIndex);
            Assert.Equal(0, call.InjectedArgumentCount);
            Assert.Equal(2, call.Arguments.Count);
            Assert.All(call.Arguments, argument => Assert.IsNotType<HirCall>(argument));
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [Fact]
    public void HirBuilder_PipeCall_LowersToSingleRegularCallWithInjectedArgument()
    {
        const string source = """
inc :: Int -> Int { x => x + 1 }
use :: 1 |> inc;
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "pipe_call_lowering.eidos",
            StopAtPhase = CompilationPhase.Hir,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<HirModule>(result.HirModule);
        var use = Assert.Single(module.Declarations.OfType<HirVal>(), value => value.Name == "use");
        var call = Assert.IsType<HirCall>(use.Initializer);
        var callee = Assert.IsType<HirVar>(call.Function);

        Assert.Equal("inc", callee.Name);
        Assert.Equal(HirCallSurfaceSyntax.Pipe, call.SurfaceSyntax);
        Assert.False(call.HasExplicitOwner);
        Assert.Null(call.ReceiverArgumentIndex);
        Assert.Equal(1, call.InjectedArgumentCount);
        Assert.Single(call.Arguments);
        Assert.IsType<HirLiteral>(call.Arguments[0]);
    }
}
