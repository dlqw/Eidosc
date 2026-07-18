using Eidosc.Diagnostic;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class NamingStyleDiagnosticTests
{
    [Fact]
    public void CompilationPipeline_ReportsCategoryNamingAfterSemanticResolution()
    {
        const string source = """
http_server[t, comptime count: Int] :: type {
    some_case :: type { BadField :: Int }
}

IO :: effect;

BadFunction :: Int -> Int {
    BadParam => BadParam
}
""";

        var result = RunTypes(source, "naming_style.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        AssertNaming(result, "S1102", "http_server", "HttpServer", originalSymbolRequired: true);
        AssertNaming(result, "S1102", "some_case", "SomeCase", originalSymbolRequired: true);
        AssertNaming(result, "S1101", "IO", "io", originalSymbolRequired: true);
        AssertNaming(result, "S1101", "BadFunction", "bad_function", originalSymbolRequired: true);
        AssertNaming(result, "S1102", "t", "T", originalSymbolRequired: true);
        AssertNaming(result, "S1103", "count", "COUNT", originalSymbolRequired: true);
        AssertNaming(result, "S1101", "BadParam", "bad_param", originalSymbolRequired: true);
        AssertNaming(result, "S1101", "BadField", "bad_field", originalSymbolRequired: false);
    }

    [Fact]
    public void CompilationPipeline_ReportsAcronymAndFqnRedundancyWithRenameFixes()
    {
        const string source = """
module json {
HTTPServer :: type {}
JsonValue :: type {}
Option :: type {}
}
""";

        var result = RunTypes(source, "json.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        AssertNaming(result, "S1102", "json", "Json", originalSymbolRequired: true);
        AssertNaming(result, "S1102", "HTTPServer", "HttpServer", originalSymbolRequired: true);
        AssertNaming(result, "S1104", "JsonValue", "Value", originalSymbolRequired: true);
        Assert.DoesNotContain(
            result.Diagnostics,
            diagnostic => diagnostic.Code == "S1104" &&
                          diagnostic.Message.Contains("Option", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_DenyStyle_PromotesNamingWarningsAfterTheyAreBuilt()
    {
        const string source = "BadFunction :: Int -> Int { value => value }";
        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = "deny_style.eidos",
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false,
            DenyStyle = true
        }).Run();

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic => diagnostic.Code == "S1101");
        Assert.Equal(DiagnosticLevel.Error, diagnostic.Level);
        Assert.Equal("naming", diagnostic.Metadata["style"]);
    }

    [Fact]
    public void CompilationPipeline_FfiNamingContract_SeparatesBindingFromExternalLinkName()
    {
        const string source = """
curlEasyInit :: Unit -> RawPtr need ffi extern c link_name "SSL_CTX_new";
""";

        var result = RunTypes(source, "ffi_naming_contract.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        AssertNaming(result, "S1101", "curlEasyInit", "curl_easy_init", originalSymbolRequired: true);
        Assert.DoesNotContain(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("SSL_CTX_new", StringComparison.Ordinal));
    }

    [Fact]
    public void CompilationPipeline_ReportsEverySemanticDeclarationCategory()
    {
        const string source = """
bad_trait :: trait {
    bad_item :: type
    bad_limit :: Int
    BadMethod :: Self -> Int
}

bad_instance :: instance bad_trait {
    bad_item :: type = Int
    bad_limit :: Int = 1
    BadMethod :: Int -> Int { BadValue => BadValue }
}

BadConstant :: comptime 1;

BadGenerator :: comptime meta.Target[meta.Stage.Semantic] -> meta.Transformation {
    _ => meta.keep()
}

Expanded :: type expand BadGenerator {}
""";

        var result = RunTypes(source, "semantic_categories.eidos");

        Assert.True(result.Success, FormatDiagnostics(result));
        AssertCategory(result, "bad_trait", "trait");
        AssertCategory(result, "bad_item", "associated type");
        AssertCategory(result, "bad_limit", "associated constant");
        AssertCategory(result, "BadMethod", "function or method");
        AssertCategory(result, "bad_instance", "named instance");
        AssertCategory(result, "BadConstant", "module-level comptime constant");
        AssertCategory(result, "BadGenerator", "function or method");
        AssertCategory(result, "BadGenerator", "meta generator");
        AssertCategory(result, "BadValue", "parameter");
    }

    [Theory]
    [InlineData("HTTPServer", "HttpServer")]
    [InlineData("UTF8Text", "Utf8Text")]
    [InlineData("FFILibrary", "FfiLibrary")]
    [InlineData("IOError", "IoError")]
    public void UpperCamelNormalizer_TreatsAcronymsAsOrdinaryWords(string source, string expected)
    {
        Assert.Equal(
            expected,
            NamingStyleDiagnosticBuilder.Normalize(
                source,
                NamingStyleDiagnosticBuilder.NamingConvention.UpperCamelCase));
    }

    private static CompilationResult RunTypes(string source, string inputFile) =>
        new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = inputFile,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            NoImplicitPrelude = true,
            UseColors = false,
            EmitStyleSuggestions = true
        }).Run();

    private static void AssertNaming(
        CompilationResult result,
        string code,
        string original,
        string replacement,
        bool originalSymbolRequired)
    {
        var diagnostic = Assert.Single(
            result.Diagnostics,
            diagnostic => diagnostic.Code == code &&
                          diagnostic.Message.Contains($"'{original}'", StringComparison.Ordinal));
        Assert.Equal(DiagnosticLevel.Warning, diagnostic.Level);
        Assert.Equal("naming", diagnostic.Metadata["style"]);
        var suggestion = Assert.Single(
            diagnostic.Suggestions,
            suggestion => suggestion.Kind == SuggestionKind.RenameSymbol);
        Assert.Equal(replacement, suggestion.Replacement);
        Assert.Equal(original.Length, suggestion.Span?.Length);
        if (originalSymbolRequired)
        {
            Assert.NotNull(suggestion.OriginalSymbolId);
        }
    }

    private static void AssertCategory(CompilationResult result, string name, string category)
    {
        Assert.Contains(
            result.Diagnostics,
            diagnostic =>
                diagnostic.Metadata.TryGetValue("naming.category", out var actualCategory) &&
                string.Equals(actualCategory, category, StringComparison.Ordinal) &&
                diagnostic.Message.Contains($"'{name}'", StringComparison.Ordinal));
    }

    private static string FormatDiagnostics(CompilationResult result) =>
        string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(diagnostic =>
                $"[{diagnostic.Level}] {diagnostic.Code} {diagnostic.Message}"));
}
