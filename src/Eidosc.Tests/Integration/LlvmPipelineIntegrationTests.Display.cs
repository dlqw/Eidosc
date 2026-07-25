using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void Native_DisplayPrintln_PrimitivesAndExplicitScalarRef_UseReferenceAbi()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            import std.Console

            main :: Unit -> Int need ffi, io
            {
                _ => {
                    value := 73;
                    print(render(ref value));
                    print("|");
                    println(42);
                    println(1.5);
                    println(true);
                    println('Z');
                    println("done");
                    Console.write("plain");
                    println();
                    Console.write_line("prefix=")(9);
                    0
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_display_primitive_ref_abi.eidos",
            "native_display_primitive_ref_abi");

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("73|42", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("1.5", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("true", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("Z", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("done", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("plain", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("prefix=9", execution.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public void Native_DisplayPrintln_UserAdt_UsesOrdinaryTraitDispatch()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            Point :: type { Point:: type(Int) }

            DisplayPoint :: instance Display {
                display :: Ref[Point] -> String {
                    value => match *value {
                        Point(_) => "Point"
                    }
                }
            }

            main :: Unit -> Int need ffi, io
            {
                _ => {
                    point := Point(7);
                    println(point);
                    match point {
                        Point(value) => println(value)
                    };
                    0
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "native_display_user_adt.eidos",
            "native_display_user_adt");

        Assert.Equal(0, execution.ExitCode);
        Assert.Contains("Point", execution.StandardOutput, StringComparison.Ordinal);
        Assert.Contains("7", execution.StandardOutput, StringComparison.Ordinal);
    }
}
