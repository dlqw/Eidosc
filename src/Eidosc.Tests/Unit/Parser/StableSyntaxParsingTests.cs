using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Pipeline;

namespace Eidosc.Tests.Unit.Parser;

public sealed class StableSyntaxParsingTests
{
    [Fact]
    public void Parser_GenericFunction_UsesSquareBracketTypeParameters()
    {
        const string source = """
identity[T] :: T -> T
{
    x => x
}
""";

        var result = RunParser(source, "stable_square_generic_parser_tests.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "identity");
        var typeParam = Assert.Single(func.TypeParams);
        Assert.Equal("T", typeParam.Name);
    }

    [Fact]
    public void Parser_GenericFunction_AngleBracketTypeParameters_Fails()
    {
        const string source = """
identity<T> :: T -> T
{
    x => x
}
""";

        var result = RunParser(source, "stable_angle_generic_parser_tests.eidos");

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code is "E4000" or "E4001");
    }

    [Fact]
    public void Parser_PackageQualifiedImport_UsesPackageAliasAndSlashModulePath()
    {
        const string source = """
Sha256A :: import crypto_a::Hash.Sha256;
""";

        var result = RunParser(source, "stable_package_import_parser_tests.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var import = Assert.Single(module.Declarations.OfType<ImportDecl>());
        Assert.Equal("crypto_a", import.PackageAlias);
        Assert.Equal(["Hash", "Sha256"], import.ModulePath);
        Assert.Equal("Sha256A", import.Alias);
    }

    [Fact]
    public void Parser_UnreachableExpression_ParsesAsDedicatedNode()
    {
        const string source = """
fail :: Unit -> Never
{
    _ => unreachable
}
""";

        var result = RunParser(source, "stable_unreachable_parser_tests.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var function = Assert.Single(module.Declarations.OfType<FuncDef>(), declaration => declaration.Name == "fail");
        var branch = Assert.Single(function.Body);
        Assert.IsType<UnreachableExpr>(branch.Expression);
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

    private static string FormatDiagnostics(CompilationResult result)
    {
        return string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic => $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}"));
    }
}
