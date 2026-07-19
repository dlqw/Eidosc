using Eidosc.Bindgen;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Bindgen;

public sealed class BindingPackageGeneratorTests
{
    [Fact]
    public void Initialize_CreatesBindingPackageSkeleton()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        var header = Path.Combine(tempDir, "demo.h");
        var include = Path.Combine(tempDir, "include");
        var native = Path.Combine(tempDir, "native", "demo.c");
        Directory.CreateDirectory(include);
        Directory.CreateDirectory(Path.GetDirectoryName(native)!);
        File.WriteAllText(header, "void demo_init(void);");
        File.WriteAllText(native, "void demo_init(void) {}");

        var packageDir = Path.Combine(tempDir, "binding");
        new BindingPackageGenerator().Initialize(
            packageDir,
            "dev.eidos.demo",
            "demo",
            [header],
            [include],
            [native],
            ["-ldemo"]);

        Assert.True(File.Exists(Path.Combine(packageDir, BindingPackageGenerator.SpecFileName)));
        Assert.True(Directory.Exists(Path.Combine(packageDir, "src")));
        Assert.True(Directory.Exists(Path.Combine(packageDir, "native")));

        var spec = File.ReadAllText(Path.Combine(packageDir, BindingPackageGenerator.SpecFileName));
        Assert.Contains("package = \"dev.eidos.demo\"", spec, StringComparison.Ordinal);
        Assert.Contains("library = \"demo\"", spec, StringComparison.Ordinal);
        Assert.Contains("headers = [\"../demo.h\"]", spec, StringComparison.Ordinal);
        Assert.Contains("includePaths = [\"../include\"]", spec, StringComparison.Ordinal);
        Assert.Contains("nativeSources = [\"../native/demo.c\"]", spec, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_BindingPackage_WritesManifestRawAndWrapper()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        File.WriteAllText(Path.Combine(tempDir, "demo.h"), """
                typedef struct DemoPoint { int x; int y; } DemoPoint;
                typedef enum DemoMode { DEMO_A = 1, DEMO_B = 2 } DemoMode;
                void demo_init(int width, int height);
                int demo_key_down(int key);
                """);
        File.WriteAllText(Path.Combine(tempDir, BindingPackageGenerator.SpecFileName), """
                package = "dev.eidos.demo"
                version = "0.1.0"
                library = "demo"
                headers = ["demo.h"]
                includePaths = ["."]
                nativeSources = ["native/demo.c"]
                linkerFlags = ["-ldemo"]

                [[wrappers]]
                module = "window"
                raw = "demo_init"
                name = "init"

                [[wrappers]]
                module = "input"
                raw = "demo_key_down"
                name = "key_down"
                """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(tempDir, Check: false, NoShim: false));

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var manifest = EidosProjectManifestDocument.Load(Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName));
        Assert.Equal("dev.eidos.demo", manifest.Package!.Name);
        Assert.NotNull(manifest.Ffi);
        Assert.Null(manifest.Ffi.Libraries);
        Assert.Contains("""nativeSources = ["native/demo.c"]""", File.ReadAllText(Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName)));

        var rawPath = Path.Combine(tempDir, "src", "raw.eidos");
        var raw = File.ReadAllText(rawPath);
        Assert.Contains("""export demo_init :: Int32 -> Int32 -> Unit need ffi extern(c, name: "demo_init");""", raw, StringComparison.Ordinal);
        Assert.Contains("export demo_a :: Int = 1;", raw, StringComparison.Ordinal);
        Assert.Contains("repr c", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("link \"demo\"", raw, StringComparison.Ordinal);

        var wrapperPath = Path.Combine(tempDir, "src", "window.eidos");
        var wrapper = File.ReadAllText(wrapperPath);
        Assert.Contains("Window :: module", wrapper, StringComparison.Ordinal);
        Assert.Contains("export init :: Int32 -> Int32 -> Unit", wrapper, StringComparison.Ordinal);
        Assert.Contains("Raw.demo_init(arg0, arg1)", wrapper, StringComparison.Ordinal);
        AssertGeneratedSourcePassesDenyStyle(rawPath);
        AssertGeneratedSourcePassesDenyStyle(wrapperPath);
    }

    [Fact]
    public void Generate_ZeroArgumentWrapper_UsesEmptyCallSyntax()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        File.WriteAllText(Path.Combine(tempDir, "demo.h"), "int demo_should_close(void);");
        File.WriteAllText(Path.Combine(tempDir, BindingPackageGenerator.SpecFileName), """
                package = "dev.eidos.demo"
                version = "0.1.0"
                library = "demo"
                headers = ["demo.h"]

                [[wrappers]]
                module = "window"
                raw = "demo_should_close"
                name = "should_close"
                """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(tempDir, Check: false, NoShim: true));

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var wrapper = File.ReadAllText(Path.Combine(tempDir, "src", "window.eidos"));
        Assert.Contains("export should_close :: Unit -> Int32", wrapper, StringComparison.Ordinal);
        Assert.Contains("_ => Raw.demo_should_close()", wrapper, StringComparison.Ordinal);
        Assert.DoesNotContain("Raw.demo_should_close(())", wrapper, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_FfiNamingContract_NormalizesBindingAndPreservesExternalLinkName()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        File.WriteAllText(Path.Combine(tempDir, "ssl.h"), "void SSL_CTX_new(void);");
        File.WriteAllText(Path.Combine(tempDir, BindingPackageGenerator.SpecFileName), """
                package = "dev.eidos.ssl"
                library = "ssl"
                headers = ["ssl.h"]
                """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(tempDir, Check: false, NoShim: true));

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var raw = File.ReadAllText(Path.Combine(tempDir, "src", "raw.eidos"));
        Assert.Contains("export ssl_ctx_new :: Unit -> Unit need ffi extern(c, name: \"SSL_CTX_new\");", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("SSL_CTX_new ::", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_RejectsNonCanonicalBindgenPackageId()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        File.WriteAllText(Path.Combine(tempDir, "demo.h"), "void demo_init(void);");
        File.WriteAllText(Path.Combine(tempDir, BindingPackageGenerator.SpecFileName), """
                package = "Dev.eidos.demo"
                library = "demo"
                headers = ["demo.h"]
                """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(tempDir, Check: false, NoShim: true));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("lower-kebab-case", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_BindingPackageWithoutNativeSources_WritesExternalLibrary()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        File.WriteAllText(Path.Combine(tempDir, "demo.h"), "void demo_init(void);");
        File.WriteAllText(Path.Combine(tempDir, BindingPackageGenerator.SpecFileName), """
                package = "dev.eidos.demo"
                library = "demo"
                headers = ["demo.h"]
                """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(tempDir, Check: false, NoShim: false));

        Assert.True(result.Success, string.Join("; ", result.Diagnostics));
        var manifest = EidosProjectManifestDocument.Load(Path.Combine(tempDir, EidosProjectConfigurationLoader.DefaultFileName));
        Assert.NotNull(manifest.Ffi);
        Assert.NotNull(manifest.Ffi.Libraries);
        Assert.Equal(["demo"], manifest.Ffi.Libraries);
    }

    [Fact]
    public void Generate_ReturnsDiagnosticWhenWrapperReferencesUnknownRawSymbol()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        File.WriteAllText(Path.Combine(tempDir, "demo.h"), "void demo_init(void);");
        File.WriteAllText(Path.Combine(tempDir, BindingPackageGenerator.SpecFileName), """
                package = "dev.eidos.demo"
                library = "demo"
                headers = ["demo.h"]

                [[wrappers]]
                module = "window"
                raw = "demo_missing"
                name = "missing"
                """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(tempDir, Check: false, NoShim: false));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("unknown raw symbol 'demo_missing'", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("module = \"Window\"", "name = \"init\"", "wrapper module 'Window'")]
    [InlineData("module = \"window\"", "name = \"badName\"", "wrapper name 'badName'")]
    public void Generate_RejectsNonCanonicalWrapperNames(
        string moduleDeclaration,
        string nameDeclaration,
        string expectedDiagnostic)
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        File.WriteAllText(Path.Combine(tempDir, "demo.h"), "void demo_init(void);");
        File.WriteAllText(Path.Combine(tempDir, BindingPackageGenerator.SpecFileName), $$"""
                package = "dev.eidos.demo"
                library = "demo"
                headers = ["demo.h"]

                [[wrappers]]
                {{moduleDeclaration}}
                raw = "demo_init"
                {{nameDeclaration}}
                """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(tempDir, Check: false, NoShim: true));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains(expectedDiagnostic, StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_RejectsNonCanonicalEffectLabel()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        File.WriteAllText(Path.Combine(tempDir, "demo.h"), "void demo_init(void);");
        File.WriteAllText(Path.Combine(tempDir, BindingPackageGenerator.SpecFileName), """
                package = "dev.eidos.demo"
                library = "demo"
                headers = ["demo.h"]

                [[effects]]
                name = "WindowIO"
                """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(tempDir, Check: false, NoShim: true));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("effect label 'WindowIO'", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_RejectsWrapperNameThatRepeatsModuleIdentity()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        File.WriteAllText(Path.Combine(tempDir, "demo.h"), "void demo_open(void);");
        File.WriteAllText(Path.Combine(tempDir, BindingPackageGenerator.SpecFileName), """
                package = "dev.eidos.demo"
                library = "demo"
                headers = ["demo.h"]

                [[wrappers]]
                module = "window"
                raw = "demo_open"
                name = "window_open"
                """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(tempDir, Check: false, NoShim: true));

        Assert.False(result.Success);
        Assert.Contains(
            result.Diagnostics,
            diagnostic => diagnostic.Contains("redundantly repeats module segment 'window'", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_RejectsEffectOperationsRemovedFromEidos07()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        File.WriteAllText(Path.Combine(tempDir, "demo.h"), "void demo_init(void);");
        File.WriteAllText(Path.Combine(tempDir, BindingPackageGenerator.SpecFileName), """
                package = "dev.eidos.demo"
                library = "demo"
                headers = ["demo.h"]

                [[effects]]
                name = "window_io"
                operations = ["open"]
                """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(tempDir, Check: false, NoShim: true));

        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Contains("cannot declare operations in Eidos 0.7", StringComparison.Ordinal));
    }

    [Fact]
    public void Generate_Check_ReturnsFailureWhenGeneratedFilesAreOutOfDate()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_bindgen");
        var tempDir = workspace.Root;
        File.WriteAllText(Path.Combine(tempDir, "demo.h"), "void demo_init(void);");
        File.WriteAllText(Path.Combine(tempDir, BindingPackageGenerator.SpecFileName), """
                package = "dev.eidos.demo"
                library = "demo"
                headers = ["demo.h"]
                """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(tempDir, Check: true, NoShim: false));

        Assert.False(result.Success);
        Assert.NotEmpty(result.ChangedFiles);
    }

    private static void AssertGeneratedSourcePassesDenyStyle(string sourcePath)
    {
        var result = new CompilationPipeline(File.ReadAllText(sourcePath), new CompilationOptions
        {
            InputFile = sourcePath,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Types,
            ImportSearchRoots = [Path.GetDirectoryName(sourcePath)!],
            EmitStyleSuggestions = true,
            DenyStyle = true,
            UseColors = false
        }).Run();

        Assert.True(
            result.Success,
            string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }
}
