using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Parser;

public class HigherKindedKindParsingTests
{
    [Fact]
    public void Parser_TypeParamKind_ParenthesizedHigherOrderKind_Parses()
    {
        const string source = """
ho[F: kind2 -> kind1, G: kind2] :: F[G] -> F[G]
{
    x => x
}
""";

        var result = RunParser(source, "hkt_parenthesized_kind_parser_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "ho");
        Assert.Equal(2, func.TypeParams.Count);
        Assert.Equal("kind2 -> kind1", func.TypeParams[0].GetKindText());
        Assert.Equal("kind2", func.TypeParams[1].GetKindText());
    }

    [Fact]
    public void Parser_TraitTypeParamKind_ParenthesizedHigherOrderKind_Parses()
    {
        const string source = """
Functor[F: kind2 -> kind1] :: trait {
    fmap :: F -> Self
}
""";

        var result = RunParser(source, "hkt_trait_parenthesized_kind_parser_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var trait = Assert.Single(module.Declarations.OfType<TraitDef>(), declaration => declaration.Name == "Functor");
        var typeParam = Assert.Single(trait.TypeParams);
        Assert.Equal("F", typeParam.Name);
        Assert.Equal("kind2 -> kind1", typeParam.GetKindText());
    }

    [Fact]
    public void Parser_TypeParamKind_CompactKindName_Parses()
    {
        const string source = """
hk[F: kind2, R: kind3] :: R[Int, String] -> F[Int]
{
    x => x
}
""";

        var result = RunParser(source, "compact_kind_name_parser_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "hk");

        Assert.Equal("kind2", func.TypeParams[0].GetKindText());
        Assert.Equal("kind3", func.TypeParams[1].GetKindText());
    }

    [Fact]
    public void Parser_TypeParamKind_ArrowStarKindSyntax_Fails()
    {
        const string source = """
old[F: * -> *] :: F[Int] -> F[Int]
{
    x => x
}
""";

        var result = RunParser(source, "legacy_arrow_star_kind_parser_tests.eidos");

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
    }

    [Theory]
    [InlineData(
        "lowercase_type_param_parser_tests.eidos",
        """
id[t] :: t -> t
{
    x => x
}
""")]
    [InlineData(
        "lowercase_where_target_parser_tests.eidos",
        """
id[T] :: T -> T where t: kind1
{
    x => x
}
""")]
    public void Parser_TypeParamName_Lowercase_FailsWithParserDiagnostic(string inputFile, string source)
    {
        var result = RunParser(source, inputFile);

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
        Assert.NotEmpty(result.Diagnostics);
    }

    [Fact]
    public void Parser_GenericWhereClause_AfterSignature_AppliesKindAndTraitConstraints()
    {
        const string source = """
lift[A, G] :: A -> G[A] where G: kind2, G: Applicative[G]
{
    x => x
}
""";

        var result = RunParser(source, "generic_where_after_signature_parser_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "lift");
        var g = Assert.Single(func.TypeParams, typeParam => typeParam.Name == "G");
        var constraint = Assert.Single(g.TraitConstraints);

        Assert.Equal("kind2", g.GetKindText());
        Assert.Equal("Applicative", constraint.TraitName);
        Assert.Equal("G", Assert.IsType<Eidosc.Ast.Types.TypePath>(Assert.Single(constraint.TypeArgs)).TypeName);
    }

    [Fact]
    public void Parser_TypeParamTraitConstraint_WithModulePathAndTypeArgs_Parses()
    {
        const string source = """
use[T: Core::Functor[Seq]] :: T -> T
{
    x => x
}
""";

        var result = RunParser(source, "hkt_trait_ref_type_args_parser_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "use");
        var typeParam = Assert.Single(func.TypeParams);
        var traitConstraint = Assert.Single(typeParam.TraitConstraints);

        Assert.Equal("Functor", traitConstraint.TraitName);
        Assert.Equal(["Core"], traitConstraint.ModulePath);
        var typeArg = Assert.Single(traitConstraint.TypeArgs);
        var typePath = Assert.IsType<Eidosc.Ast.Types.TypePath>(typeArg);
        Assert.Equal("Seq", typePath.TypeName);
    }

    private static CompilationResult RunParser(string source, string inputFile)
    {
        var options = new CompilationOptions
        {
            InputFile = inputFile,
            StopAtPhase = CompilationPhase.Parser,
            UseColors = false
        };

        return new CompilationPipeline(source, options).Run();
    }
}
