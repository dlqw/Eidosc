using System.Linq;
using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Parser;

public class ImportParsingTests
{
    [Fact]
    public void Parser_ImportWildcard_ParsesAsWildcardKind()
    {
        const string source = """
import Core.*
""";

        var result = RunParser(source, "import_wildcard_parser_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var import = Assert.Single(module.Declarations.OfType<ImportDecl>());
        Assert.Equal(ImportKind.Wildcard, import.Kind);
        Assert.Equal(["Core"], import.ModulePath);
    }

    [Fact]
    public void Parser_ImportSelectiveWithAlias_ParsesSelectiveItems()
    {
        const string source = """
import Core.{Writer, Reader as ReadCap}
""";

        var result = RunParser(source, "import_selective_parser_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var import = Assert.Single(module.Declarations.OfType<ImportDecl>());
        Assert.Equal(ImportKind.Selective, import.Kind);
        Assert.Equal(2, import.SelectiveImports.Count);
        Assert.Contains(import.SelectiveImports, item => item.Name == "Writer" && item.Alias == null);
        Assert.Contains(import.SelectiveImports, item => item.Name == "Reader" && item.Alias == "ReadCap");
    }

    [Fact]
    public void Parser_ImportAlias_ParsesModuleAlias()
    {
        const string source = """
C :: import Core;
""";

        var result = RunParser(source, "import_alias_parser_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var import = Assert.Single(module.Declarations.OfType<ImportDecl>());
        Assert.Equal(ImportKind.Module, import.Kind);
        Assert.Equal(["Core"], import.ModulePath);
        Assert.Equal("C", import.Alias);
    }

    [Fact]
    public void Parser_ImportDotModulePath_ParsesAllSegments()
    {
        // Module path with dot separator (the unified form). Both dot and slash are accepted.
        const string source = """
Parser :: import Compiler.Parse.Decl;
""";

        var result = RunParser(source, "import_dot_path_parser_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var import = Assert.Single(module.Declarations.OfType<ImportDecl>());
        Assert.Equal(ImportKind.Module, import.Kind);
        Assert.Equal(["Compiler", "Parse", "Decl"], import.ModulePath);
        Assert.Equal("Parser", import.Alias);
    }

    [Fact]
    public void Parser_ImportModuleAlias_Lowercase_Fails()
    {
        const string source = """
c :: import Core;
""";

        var result = RunParser(source, "import_lowercase_module_alias_parser_tests.eidos");

        Assert.False(result.Success);
        Assert.Equal(CompilationPhase.Parser, result.CompletedPhase);
    }

    [Fact]
    public void Parser_ExportImportAlias_ParsesExportedModuleAlias()
    {
        const string source = """
export C :: import Core;
""";

        var result = RunParser(source, "export_import_alias_parser_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var import = Assert.Single(module.Declarations.OfType<ImportDecl>());
        Assert.True(import.IsExported);
        Assert.Equal(ImportKind.Module, import.Kind);
        Assert.Equal(["Core"], import.ModulePath);
        Assert.Equal("C", import.Alias);
    }

    [Fact]
    public void Parser_ImportLegacyAliasForm_FailsInNameFirstMode()
    {
        const string source = """
import Core as C
""";

        var result = RunParser(source, "import_legacy_alias_form_parser_tests.eidos");

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("Eidos 0.6.0-alpha.1", StringComparison.Ordinal));
    }

    [Fact]
    public void Parser_ExportFunc_ParsesExportModifier()
    {
        const string source = """
export id :: Int -> Int
{
    x => x
}
""";

        var result = RunParser(source, "export_func_parser_tests.eidos");

        Assert.True(result.Success);
        var module = Assert.IsType<ModuleDecl>(result.Ast);
        var func = Assert.Single(module.Declarations.OfType<FuncDef>());
        Assert.True(func.IsExported);
        Assert.Equal("id", func.Name);
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
