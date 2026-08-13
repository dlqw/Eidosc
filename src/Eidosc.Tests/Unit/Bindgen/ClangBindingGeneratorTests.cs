using Eidosc.Bindgen;
using Eidosc.Bindgen.Clang;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Bindgen;

/// <summary>
/// clang 解析模式接线（P0 M3）测试：parseMode、typedef 解析、函数指针 → Cfn、
/// 宏常量/union/全局的生成规则。依赖本机 LLVM/libclang（同 M1/M2）。
/// </summary>
public sealed class ClangBindingGeneratorTests
{
    private static ClangApi RequireClangApi()
    {
        Assert.True(ClangNative.TryLoad(out var error, out var api), error);
        return api!;
    }

    [Fact]
    public void TypeMapper_ResolvesTypedefToPrimitive()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        var header = workspace.WriteText("demo.h", "typedef unsigned long my_size; int f(my_size n);\n");
        var result = new ClangHeaderParser(RequireClangApi()).Parse(header);

        Assert.Empty(result.Errors);
        var fn = result.Ir!.Functions.Single(f => f.Name == "f");
        var mapping = new BindingTypeMapper(result.Ir).Map(fn.Parameters[0].Type);

        Assert.Equal(BindingTypeCategory.Direct, mapping.Category);
        Assert.Equal("Int64", mapping.EidosType);
    }

    [Fact]
    public void TypeMapper_FunctionPointerMapsToCfn()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        var header = workspace.WriteText("demo.h", "int apply(int (*fn)(int, int), int x);\n");
        var result = new ClangHeaderParser(RequireClangApi()).Parse(header);

        Assert.Empty(result.Errors);
        var apply = result.Ir!.Functions.Single(f => f.Name == "apply");
        var fnMapping = new BindingTypeMapper(result.Ir).Map(apply.Parameters[0].Type);

        Assert.Equal(BindingTypeCategory.Direct, fnMapping.Category);
        Assert.Equal("Cfn[Int32, Int32, Int32]", fnMapping.EidosType);
    }

    [Fact]
    public void BindingPackageGenerator_ClangMode_GeneratesPackage()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        workspace.WriteText("demo.h", """
            typedef struct Point { int x; int y; } Point;
            enum Color { RED, GREEN = 5 };
            union Value { int i; float f; };
            extern int global_counter;
            #define VERSION 42
            int demo_init(Point p, int width);
            int apply(int (*fn)(int), int x);
            """);
        var packageDir = workspace.Path("binding");
        Directory.CreateDirectory(packageDir);
        workspace.WriteText("binding/bindgen.toml", """
            package = "dev.eidos.demo"
            version = "0.1.0"
            library = "demo"
            headers = ["../demo.h"]
            parseMode = "clang"
            """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(packageDir, Check: false, NoShim: true));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var raw = File.ReadAllText(Path.Combine(packageDir, "src", "raw.eidos"));

        // struct → @[repr(c)]
        Assert.Contains("@[repr(c)]", raw, StringComparison.Ordinal);
        Assert.Contains("export Point :: type {", raw, StringComparison.Ordinal);
        // enum → Int 常量
        Assert.Contains("export red :: Int = 0;", raw, StringComparison.Ordinal);
        Assert.Contains("export green :: Int = 5;", raw, StringComparison.Ordinal);
        // 宏常量
        Assert.Contains("export version :: Int = 42;", raw, StringComparison.Ordinal);
        // union / 全局 → 注释收编
        Assert.Contains("// SKIP union Value", raw, StringComparison.Ordinal);
        Assert.Contains("// SKIP global global_counter", raw, StringComparison.Ordinal);
        // struct 按值参数 → 自动 shim 绑定（int 返回 → Int32）
        Assert.Contains("@[extern(c, name: \"eidos_shim_demo_init\")]", raw, StringComparison.Ordinal);
        Assert.Contains("export demo_init :: RawPtr -> Int32 -> Int32 need ffi;", raw, StringComparison.Ordinal);
        // 函数指针 → Cfn
        Assert.Contains("export apply :: Cfn[Int32, Int32] -> Int32 -> Int32 need ffi;", raw, StringComparison.Ordinal);
        // 内置/预定义宏（无源文件位置）不得进入生成物
        Assert.DoesNotContain("__llvm__", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("__clang_major__", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void BindingSpecDocument_ClangFields_RoundTrip()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        var path = workspace.WriteText("bindgen.toml", """
            package = "dev.eidos.demo"
            version = "0.1.0"
            library = "demo"
            headers = ["demo.h"]
            parseMode = "clang"
            clangDefines = ["RLAPI="]
            clangArgs = ["-std=c11"]

            [options]
            enumsAsConstants = true
            wrapStrings = false
            """);

        var spec = BindingSpecDocument.Load(path);

        Assert.Equal("clang", spec.ParseMode);
        Assert.Equal(["RLAPI="], spec.ClangDefines!);
        Assert.Equal(["-std=c11"], spec.ClangArgs!);
        Assert.True(spec.Options!.EnumsAsConstants);
        Assert.False(spec.Options.WrapStrings);

        // 往返：ToToml 后再 Load 保持语义
        var second = Path.Combine(workspace.Root, "roundtrip.toml");
        File.WriteAllText(second, spec.ToToml());
        var reloaded = BindingSpecDocument.Load(second);
        Assert.Equal("clang", reloaded.ParseMode);
        Assert.Equal(["RLAPI="], reloaded.ClangDefines!);
        Assert.Equal(["-std=c11"], reloaded.ClangArgs!);
    }

    [Fact]
    public void BindingSpecDocument_RejectsUnknownParseMode()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        var path = workspace.WriteText("bindgen.toml", """
            package = "dev.eidos.demo"
            version = "0.1.0"
            library = "demo"
            headers = ["demo.h"]
            parseMode = "regex"
            """);

        Assert.Throws<InvalidOperationException>(() => BindingSpecDocument.Load(path));
    }
}
