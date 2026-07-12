using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Types;

public class TypeInfererTests
{
    [Fact]
    public void CompilationHelper_Create_WithIntegerLiteral_ReturnsSource()
    {
        var source = "42";
        var helper = CompilationHelper.Create(source);

        Assert.Equal("42", helper.GetSource());
    }

    [Fact]
    public void CompilationHelper_Create_WithStringLiteral_ReturnsSource()
    {
        var source = "\"hello\"";
        var helper = CompilationHelper.Create(source);

        Assert.Equal(source, helper.GetSource());
    }

    [Fact]
    public void CompilationHelper_Create_WithSimpleFunction_ReturnsSource()
    {
        var source = "identity :: Int -> Int { x => x }";
        var helper = CompilationHelper.Create(source);

        Assert.Equal(source, helper.GetSource());
    }

    [Fact]
    public void CompilationHelper_Create_WithGenericFunction_ReturnsSource()
    {
        var source = "id[T] :: T -> T { x => x }";
        var helper = CompilationHelper.Create(source);

        Assert.Equal(source, helper.GetSource());
    }

    [Fact]
    public void CompilationHelper_Create_WithEffectfulFunction_ReturnsSource()
    {
        var source = "Console :: effect;";
        var helper = CompilationHelper.Create(source);

        Assert.Equal(source, helper.GetSource());
    }
}
