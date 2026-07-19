using System.Linq;
using Eidosc.Diagnostic;
using Eidosc;
using Eidosc.Pipeline;
using Eidosc.Symbols;
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
    public void NeedFfiWrapper_RemainsAnOrdinaryFunction()
    {
        const string source = """
            wrapper :: Int -> Int need ffi {
                value => value
            }
            """;

        var result = RunPipeline(source);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var wrapper = Assert.Single(table.Symbols.Values.OfType<FuncSymbol>(), static symbol => symbol.Name == "wrapper");
        Assert.False(wrapper.IsExternal);
        Assert.Null(wrapper.ExternalSymbolName);
        Assert.Null(wrapper.ExternalLibrary);
    }

    [Fact]
    public void ExternBodylessFunction_UsesCanonicalLinkageMetadata()
    {
        const string source = """
            native_call :: Int -> Int
                need ffi
                extern(c, library: "native", name: "native_call_v2");
            """;

        var result = RunPipeline(source, ["native"]);

        Assert.True(result.Success, string.Join("; ", result.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        var table = Assert.IsType<SymbolTable>(result.SymbolTable);
        var function = Assert.Single(table.Symbols.Values.OfType<FuncSymbol>(), static symbol => symbol.Name == "native_call");
        Assert.True(function.IsExternal);
        Assert.Equal("native_call_v2", function.ExternalSymbolName);
        Assert.Equal("native", function.ExternalLibrary);
        Assert.Contains("ffi", function.ImplicitAbilities);
    }

    [Fact]
    public void FfiAttribute_WithLibraryPrefix_Succeeds()
    {
        const string source = """
             easyInit :: Unit -> RawPtr need ffi extern(c, library: "curl", name: "curl_easy_init");

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
             myMalloc :: Int -> RawPtr need ffi extern(c, name: "malloc");

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

            abs :: Int -> Int
             need ffi extern(c, name: "abs")
            {
                x => x
            }
            """;

        var result = RunPipeline(source);

        Assert.False(result.Success);
        var diagnostic = Assert.Single(result.Diagnostics, d => d.Code == "E3050");
        Assert.Contains("cannot have an Eidos function body", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CompilerPrivateIntrinsicClause_CannotBeForgedAlongsideExtern()
    {
        const string source = """
            native_call :: Int -> Int
                need ffi
                extern(c)
                compiler(intrinsic: "llvm.native_call");
            """;

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Message.Contains("reserved for toolchain-owned source", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("""puts :: String -> Int need ffi extern(c, name: "puts");""")]
    [InlineData("""bad_tuple :: (Int, Int) -> Int need ffi extern(c, name: "bad_tuple");""")]
    [InlineData("""bad_ref :: Ref[Int] -> Int need ffi extern(c, name: "bad_ref");""")]
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
             acceptClosure :: (Unit -> RawPtr) -> Unit need ffi extern(c, name: "accept_closure");

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
             easyInit :: Unit -> RawPtr need ffi extern(c, library: "curl", name: "curl_easy_init");

            """;

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3052");
    }

    [Fact]
    public void FfiAttribute_MultipleLibraries_Succeeds()
    {
        const string source = """
             initA :: Unit -> RawPtr need ffi extern(c, library: "A", name: "init");

             initB :: Unit -> RawPtr need ffi extern(c, library: "B", name: "init");

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
             initA :: Unit -> RawPtr need ffi extern(c, library: "A", name: "init");

             initB :: Unit -> RawPtr need ffi extern(c, library: "A", name: "init");

            """;

        var result = RunPipeline(source, ["A"]);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3054");
    }

    [Fact]
    public void FfiAttribute_SameEidosNameWithDistinctExternalSymbols_Succeeds()
    {
        const string source = """

            abs :: Int -> Int need ffi extern(c, name: "abs_i");



            abs :: Float -> Float need ffi extern(c, name: "abs_f");

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
             parseInt :: Int -> Int need ffi extern(c, name: "native_parse");

             parseFloat :: Float -> Float need ffi extern(c, name: "native_parse");

            """;

        var result = RunPipeline(source);

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E3054");
    }

    [Fact]
    public void FfiAttribute_OverloadGroupWithDuplicateExternalSymbol_ProducesE3054()
    {
        const string source = """
             parse :: Int -> Int need ffi extern(c, name: "native_parse");

             parse :: Float -> Float need ffi extern(c, name: "native_parse");

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
                 curlInit :: Unit -> RawPtr need ffi extern(c, library: "curl", name: "curl_easy_init");

            }
            """;

        var result = RunPipeline(source, ["curl"]);

        Assert.True(result.Success,
            string.Join(", ", result.Diagnostics
                .Where(d => d.Level == DiagnosticLevel.Error)
                .Select(d => d.Message)));
    }
}
