using System.Linq;
using Eidosc.Diagnostic;
using Eidosc;
using Eidosc.Pipeline;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public class FfiLibraryQualificationTests
{
    private static CompilationResult RunPipeline(string source, string[]? configLibraries = null)
    {
        var options = new CompilationOptions
        {
            InputFile = "ffi_lib_test.eidos",
            StopAtPhase = CompilationPhase.Types,
            UseColors = false,
            ConfigFfiLibraries = configLibraries ?? []
        };

        return new CompilationPipeline(source, options).Run();
    }

    [Fact]
    public void FfiAttribute_WithLibraryPrefix_Succeeds()
    {
        const string source = """
            @ffi("curl/curl_easy_init") easyInit :: Unit -> RawPtr
            """;

        var result = RunPipeline(source, ["curl"]);

        Assert.True(result.Success,
            string.Join(", ", result.Diagnostics
                .Where(d => d.Level == DiagnosticLevel.Error)
                .Select(d => d.Message)));
    }

    [Fact]
    public void FfiAttribute_WithoutLibraryPrefix_Succeeds()
    {
        const string source = """
            @ffi("malloc") myMalloc :: Int -> RawPtr
            """;

        var result = RunPipeline(source);

        Assert.True(result.Success,
            string.Join(", ", result.Diagnostics
                .Where(d => d.Level == DiagnosticLevel.Error)
                .Select(d => d.Message)));
    }

    [Fact]
    public void FfiAttribute_WithFunctionBody_ProducesE3050()
    {
        const string source = """
            @ffi("abs")
            abs :: Int -> Int
            {
                x => x
            }
            """;

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3050");
    }

    [Theory]
    [InlineData("""@ffi("puts") puts :: String -> Int""")]
    [InlineData("""@ffi("bad_tuple") badTuple :: (Int, Int) -> Int""")]
    [InlineData("""@ffi("bad_ref") badRef :: Ref[Int] -> Int""")]
    public void FfiAttribute_WithNonFfiSafeAbiType_ProducesE3051(string source)
    {
        var result = RunPipeline(source);
        var parameterLocation = DiagnosticMessages.FfiParameterLocation(1, DiagnosticMessages.FfiParameterRole);

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            d => d.Code == "E3051" &&
                 d.Message.Contains(parameterLocation, StringComparison.Ordinal));
    }

    [Fact]
    public void FfiAttribute_WithFunctionParameter_TreatsValueAsClosurePointer()
    {
        const string source = """
            @ffi("accept_closure") acceptClosure :: (Unit -> RawPtr) -> Unit
            """;

        var result = RunPipeline(source);

        Assert.True(result.Success,
            string.Join(", ", result.Diagnostics
                .Where(d => d.Level == DiagnosticLevel.Error)
                .Select(d => d.Message)));
    }

    [Fact]
    public void FfiAttribute_UndeclaredLibrary_ProducesE3052()
    {
        const string source = """
            @ffi("curl/curl_easy_init") easyInit :: Unit -> RawPtr
            """;

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3052");
    }

    [Fact]
    public void FfiAttribute_MultipleLibraries_Succeeds()
    {
        const string source = """
            @ffi("A/init") initA :: Unit -> RawPtr
            @ffi("B/init") initB :: Unit -> RawPtr
            """;

        var result = RunPipeline(source, ["A", "B"]);

        Assert.True(result.Success,
            string.Join(", ", result.Diagnostics
                .Where(d => d.Level == DiagnosticLevel.Error)
                .Select(d => d.Message)));
    }

    [Fact]
    public void ConfigLibraryWithoutFfiFunction_ProducesW3050()
    {
        const string source = """
            main :: Unit -> Int
            {
                _ => 0
            }
            """;

        var result = RunPipeline(source, ["curl"]);

        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Level == DiagnosticLevel.Warning && d.Code == "W3050");
    }

    [Fact]
    public void FfiAttribute_DuplicateLibrarySymbol_ProducesE3054()
    {
        const string source = """
            @ffi("A/init") initA :: Unit -> RawPtr
            @ffi("A/init") initB :: Unit -> RawPtr
            """;

        var result = RunPipeline(source, ["A"]);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3054");
    }

    [Fact]
    public void FfiAttribute_SameEidosNameWithDistinctExternalSymbols_Succeeds()
    {
        const string source = """
            @ffi("abs_i")
            abs :: Int -> Int

            @ffi("abs_f")
            abs :: Float -> Float
            """;

        var result = RunPipeline(source);

        Assert.True(result.Success,
            string.Join(", ", result.Diagnostics
                .Where(d => d.Level == DiagnosticLevel.Error)
                .Select(d => d.Message)));
    }

    [Fact]
    public void FfiAttribute_DuplicateDefaultSymbol_ProducesE3054()
    {
        const string source = """
            @ffi("native_parse") parseInt :: Int -> Int
            @ffi("native_parse") parseFloat :: Float -> Float
            """;

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3054");
    }

    [Fact]
    public void FfiAttribute_OverloadGroupWithDuplicateExternalSymbol_ProducesE3054()
    {
        const string source = """
            @ffi("native_parse") parse :: Int -> Int
            @ffi("native_parse") parse :: Float -> Float
            """;

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3054");
    }

    [Fact]
    public void ConfigLibraries_AreAvailableInNestedModules()
    {
        const string source = """
            Outer :: module {
                @ffi("curl/curl_easy_init") curlInit :: Unit -> RawPtr
            }
            """;

        var result = RunPipeline(source, ["curl"]);

        Assert.True(result.Success,
            string.Join(", ", result.Diagnostics
                .Where(d => d.Level == DiagnosticLevel.Error)
                .Select(d => d.Message)));
    }
}
