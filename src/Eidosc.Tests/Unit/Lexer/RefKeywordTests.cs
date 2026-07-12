using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Unit.Lexer;

public class RefKeywordTests
{
    [Fact]
    public void RefType_InSource_IsValidIdentifier()
    {
        var source = "Ref[Int]";
        var helper = CompilationHelper.Create(source);

        Assert.Contains("Ref", helper.GetSource());
    }

    [Fact]
    public void MRefType_InSource_IsValidIdentifier()
    {
        var source = "MRef[String]";
        var helper = CompilationHelper.Create(source);

        Assert.Contains("MRef", helper.GetSource());
    }

    [Fact]
    public void NestedRefType_InSource_IsValid()
    {
        var source = "Ref[Ref[Int]]";
        var helper = CompilationHelper.Create(source);

        Assert.Contains("Ref", helper.GetSource());
    }

    [Fact]
    public void RefInFunctionSignature_IsValid()
    {
        var source = "read :: Ref[String] -> Unit { s => () }";
        var helper = CompilationHelper.Create(source);

        Assert.Contains("Ref", helper.GetSource());
    }

    [Fact]
    public void MRefInFunctionSignature_IsValid()
    {
        var source = "modify :: MRef[Int] -> Unit { r => () }";
        var helper = CompilationHelper.Create(source);

        Assert.Contains("MRef", helper.GetSource());
    }

}
