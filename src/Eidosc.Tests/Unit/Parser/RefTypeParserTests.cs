using Eidosc.Tests.Fixtures;
using Xunit;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Pipeline;

namespace Eidosc.Tests.Unit.Parser;

public class RefTypeParserTests
{
    [Fact]
    public void RefType_InSource_IsValidSyntax()
    {
        var source = "read :: Ref[String] -> Unit { s => () }";
        var helper = CompilationHelper.Create(source);

        Assert.Contains("Ref", helper.GetSource());
        Assert.Contains("String", helper.GetSource());
    }

    [Fact]
    public void MRefType_InSource_IsValidSyntax()
    {
        var source = "modify :: MRef[Int] -> Unit { r => () }";
        var helper = CompilationHelper.Create(source);

        Assert.Contains("MRef", helper.GetSource());
        Assert.Contains("Int", helper.GetSource());
    }

    [Fact]
    public void NestedRefType_InSource_IsValidSyntax()
    {
        var source = "deep :: Ref[Ref[Int]] -> Unit { r => () }";
        var helper = CompilationHelper.Create(source);

        Assert.Contains("Ref[Int]", helper.GetSource());
    }

    [Fact]
    public void MixedRefTypes_InSource_IsValidSyntax()
    {
        var source = "both :: Ref[String] -> MRef[Int] { s => ... }";
        var helper = CompilationHelper.Create(source);

        Assert.Contains("Ref[String]", helper.GetSource());
        Assert.Contains("MRef[Int]", helper.GetSource());
    }

    [Fact]
    public void RefTypeWithGeneric_InSource_IsValidSyntax()
    {
        var source = "generic[T] :: Ref[T] -> Unit { r => () }";
        var helper = CompilationHelper.Create(source);

        Assert.Contains("Ref[T]", helper.GetSource());
    }

    [Fact]
    public void AdtFieldType_WithRef_PreservesOuterTypePathAndTypeArg()
    {
        const string source = """
ReaderBox[T] :: type {
    reader: Ref[T], tag: Int
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "ref_type_parser_adt_field.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var adt = Assert.Single(module.Declarations.OfType<AdtDef>(), item => item.Name == "ReaderBox");
        var field = Assert.Single(adt.Fields, item => item.Name == "reader");
        var fieldType = Assert.IsType<TypePath>(field.Type);
        var typeArg = Assert.Single(fieldType.TypeArgs);
        var argPath = Assert.IsType<TypePath>(typeArg);

        Assert.Equal("Ref", fieldType.TypeName);
        Assert.Equal("T", argPath.TypeName);
        Assert.Empty(argPath.TypeArgs);
    }

    [Fact]
    public void AdtFieldType_WithMRef_PreservesOuterTypePathAndTypeArg()
    {
        const string source = """
WriterBox[T] :: type {
    writer: MRef[T], tag: Int
}
""";

        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "mref_type_parser_adt_field.eidos",
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var adt = Assert.Single(module.Declarations.OfType<AdtDef>(), item => item.Name == "WriterBox");
        var field = Assert.Single(adt.Fields, item => item.Name == "writer");
        var fieldType = Assert.IsType<TypePath>(field.Type);
        var typeArg = Assert.Single(fieldType.TypeArgs);
        var argPath = Assert.IsType<TypePath>(typeArg);

        Assert.Equal("MRef", fieldType.TypeName);
        Assert.Equal("T", argPath.TypeName);
        Assert.Empty(argPath.TypeArgs);
    }
}
