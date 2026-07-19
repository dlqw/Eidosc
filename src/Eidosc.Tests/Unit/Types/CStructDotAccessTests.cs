using System.Linq;
using Eidosc.Diagnostic;
using Eidosc;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Pipeline;
using Eidosc.Types;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class CStructDotAccessTests
{
    [Fact]
    public void CStructDotAccess_SingleMatch_ResolvesToGetterCall()
    {
        const string source = """

malloc :: Int -> RawPtr need ffi extern(c, name: "malloc");



Point :: type  repr c
{
    x:: Float,
    y:: Float
}

main :: Int -> Int
{
    _ => {
        p: RawPtr := malloc(16);
        x := p.x;
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CStructDotAccess_MultipleCStructsDistinctFields_ResolvesCorrectly()
    {
        const string source = """

malloc :: Int -> RawPtr need ffi extern(c, name: "malloc");



Point :: type  repr c
{
    x:: Float,
    y:: Float
}


Header :: type  repr c
{
    magic:: Int,
    version:: Int
}

main :: Int -> Int
{
    _ => {
        p: RawPtr := malloc(16);
        h: RawPtr := malloc(8);
        x := p.x;
        m := h.magic;
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.True(result.Success, $"Expected success but got errors: {string.Join(", ", result.Diagnostics.Where(d => d.Level == DiagnosticLevel.Error).Select(d => d.Message))}");
    }

    [Fact]
    public void CStructDotAccess_AmbiguousField_ProducesError()
    {
        const string source = """

malloc :: Int -> RawPtr need ffi extern(c, name: "malloc");



Point :: type  repr c
{
    x:: Float,
    y:: Float
}


Rect :: type  repr c
{
    x:: Float,
    y:: Float,
    w:: Float,
    h:: Float
}

main :: Int -> Int
{
    _ => {
        p: RawPtr := malloc(16);
        x := p.x;
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d =>
            d.Level == DiagnosticLevel.Error &&
            d.Message.Contains("Ambiguous CStruct field access", System.StringComparison.Ordinal) &&
            d.Message.Contains("Point", System.StringComparison.Ordinal) &&
            d.Message.Contains("Rect", System.StringComparison.Ordinal));
    }

    [Fact]
    public void CStructDotAccess_NoMatchingField_ProducesOriginalError()
    {
        const string source = """

malloc :: Int -> RawPtr need ffi extern(c, name: "malloc");



Point :: type  repr c
{
    x:: Float,
    y:: Float
}

main :: Int -> Int
{
    _ => {
        p: RawPtr := malloc(16);
        z := p.z;
        0
    }
}
""";

        var result = RunPipeline(source);

        var errorMessages = result.Diagnostics
            .Where(d => d.Level == DiagnosticLevel.Error)
            .Select(d => d.Message)
            .ToList();

        Assert.False(result.Success, $"Expected failure. Errors: {string.Join("; ", errorMessages)}");
        Assert.Contains(errorMessages, msg => msg.Contains("no readable field", System.StringComparison.Ordinal));
    }

    [Fact]
    public void CStructDotAccess_NonRawPtrReceiver_NotAffected()
    {
        const string source = """
main :: Int -> Int
{
    _ => {
        s := "hello";
        x := s.foo;
        0
    }
}
""";

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d =>
            d.Level == DiagnosticLevel.Error &&
            d.Message.Contains("no readable field", System.StringComparison.Ordinal));
    }

    [Fact]
    public void CStructDotAccess_ExplicitCallSyntax_NotResolvedAsCStructField()
    {
        const string source = """

malloc :: Int -> RawPtr need ffi extern(c, name: "malloc");



Point :: type  repr c
{
    x:: Float
}

main :: Int -> Int
{
    _ => {
        p: RawPtr := malloc(16);
        x := p.x();
        0
    }
}
""";

        var result = RunPipeline(source);

        // p.x() is explicit call syntax, not bare dot access.
        // It should try to resolve as a function call to "x(p)", not as a CStruct getter.
        Assert.False(result.Success);
    }

    private static CompilationResult RunPipeline(string source)
    {
        var options = new CompilationOptions
        {
            InputFile = "cstruct_dot_access_test.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }
}
