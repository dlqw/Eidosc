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
        // unsigned long 宽度随平台：LLP64（Windows）4 字节 → UInt32，LP64 8 字节 → UInt64。
        Assert.Equal(OperatingSystem.IsWindows() ? "UInt32" : "UInt64", mapping.EidosType);
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
    public void TypeMapper_FunctionPointerZeroAndHighArityMapToCfn()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        var header = workspace.WriteText(
            "demo.h",
            "int zero(int (*fn)(void)); int seven(int (*fn)(int, int, int, int, int, int, int));\n");
        var result = new ClangHeaderParser(RequireClangApi()).Parse(header);

        Assert.Empty(result.Errors);
        var ir = result.Ir!;
        var mapper = new BindingTypeMapper(ir);

        var zero = mapper.Map(ir.Functions.Single(f => f.Name == "zero").Parameters[0].Type);
        Assert.Equal(BindingTypeCategory.Direct, zero.Category);
        Assert.Equal("Cfn[Int32]", zero.EidosType);

        var seven = mapper.Map(ir.Functions.Single(f => f.Name == "seven").Parameters[0].Type);
        Assert.Equal(BindingTypeCategory.Direct, seven.Category);
        Assert.Equal("Cfn[Int32, Int32, Int32, Int32, Int32, Int32, Int32, Int32]", seven.EidosType);
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

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(packageDir, Check: false, NoShim: false));

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
        // union → 成员视图访问器（M4a）；标量 C 全局 → extern(c) 声明
        Assert.Contains("export value_size :: Int = 4;", raw, StringComparison.Ordinal);
        Assert.Contains("export value_align :: Int = 4;", raw, StringComparison.Ordinal);
        Assert.Contains("@[extern(c, name: \"eidos_shim_union_Value_i_get\")]", raw, StringComparison.Ordinal);
        Assert.Contains("export value_i_get :: RawPtr -> Int32 need ffi;", raw, StringComparison.Ordinal);
        Assert.Contains("export value_f_set :: RawPtr -> Float32 -> Unit need ffi;", raw, StringComparison.Ordinal);
        Assert.Contains("@[extern(c, name: \"global_counter\")]", raw, StringComparison.Ordinal);
        Assert.Contains("export mut global_counter : Int32;", raw, StringComparison.Ordinal);
        // struct 按值参数 → 字段拆分 shim（int 叶字段原生位宽直连）
        Assert.Contains("@[extern(c, name: \"eidos_shim_demo_init\")]", raw, StringComparison.Ordinal);
        Assert.Contains("export demo_init :: Int32 -> Int32 -> Int32 -> Int32 need ffi;", raw, StringComparison.Ordinal);
        // 函数指针 → Cfn
        Assert.Contains("export apply :: Cfn[Int32, Int32] -> Int32 -> Int32 need ffi;", raw, StringComparison.Ordinal);
        // 内置/预定义宏（无源文件位置）不得进入生成物
        Assert.DoesNotContain("__llvm__", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("__clang_major__", raw, StringComparison.Ordinal);

        // 自动 shim：字段拆分 + compound literal 组装
        var shim = File.ReadAllText(Path.Combine(packageDir, "native", "demo_shim.c"));
        Assert.Contains("int eidos_shim_demo_init(int p_x, int p_y, int width)", shim, StringComparison.Ordinal);
        Assert.Contains("return demo_init((struct Point){ p_x, p_y }, width);", shim, StringComparison.Ordinal);
    }

    [Fact]
    public void StructByValueReturn_GeneratesStaticSlotShimAndRawPtrBinding()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        workspace.WriteText("demo.h", """
            typedef struct Point { int x; int y; } Point;
            Point make_point(int x, int y);
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

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(packageDir, Check: false, NoShim: false));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var raw = File.ReadAllText(Path.Combine(packageDir, "src", "raw.eidos"));
        Assert.Contains("@[extern(c, name: \"eidos_shim_make_point\")]", raw, StringComparison.Ordinal);
        Assert.Contains("export make_point :: Int32 -> Int32 -> RawPtr need ffi;", raw, StringComparison.Ordinal);

        var shim = File.ReadAllText(Path.Combine(packageDir, "native", "demo_shim.c"));
        Assert.Contains("static Point eidos_shim_make_point_result;", shim, StringComparison.Ordinal);
        Assert.Contains("return &eidos_shim_make_point_result;", shim, StringComparison.Ordinal);
    }

    [Fact]
    public void NarrowScalars_BindDirectlyWithoutNarrowingShims()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        workspace.WriteText("demo.h", """
            int demo(int a, unsigned int b);
            float scale(float f);
            long long direct64(long long v);
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

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(packageDir, Check: false, NoShim: false));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var raw = File.ReadAllText(Path.Combine(packageDir, "src", "raw.eidos"));
        // 窄标量以原生位宽直接过 FFI 边界（E5337 收口），不再生成窄化 shim
        Assert.Contains("export demo :: Int32 -> UInt32 -> Int32 need ffi;", raw, StringComparison.Ordinal);
        Assert.Contains("@[extern(c, name: \"demo\")]", raw, StringComparison.Ordinal);
        Assert.Contains("export scale :: Float32 -> Float32 need ffi;", raw, StringComparison.Ordinal);
        Assert.Contains("@[extern(c, name: \"scale\")]", raw, StringComparison.Ordinal);
        // 64 位标量直连（long long → 惯用 Int）
        Assert.Contains("export direct64 :: Int -> Int need ffi;", raw, StringComparison.Ordinal);
        Assert.Contains("@[extern(c, name: \"direct64\")]", raw, StringComparison.Ordinal);

        // 全窄标量签名 → 无任何 shim 产物，manifest 直接链库
        Assert.False(File.Exists(Path.Combine(packageDir, "native", "demo_shim.c")));
        var manifest = File.ReadAllText(Path.Combine(packageDir, "eidos.toml"));
        Assert.Contains("libraries = [\"demo\"]", manifest, StringComparison.Ordinal);
    }

    [Fact]
    public void StructParams_WithFloatsAndNestedStructs_SplitRecursively()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        workspace.WriteText("demo.h", """
            typedef struct Vector2 { float x; float y; } Vector2;
            typedef struct Camera2D { Vector2 offset; float rotation; } Camera2D;
            void draw_vec(Vector2 v, float scale);
            void begin2d(Camera2D cam);
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

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(packageDir, Check: false, NoShim: false));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var raw = File.ReadAllText(Path.Combine(packageDir, "src", "raw.eidos"));
        Assert.Contains("export draw_vec :: Float32 -> Float32 -> Float32 -> Unit need ffi;", raw, StringComparison.Ordinal);
        Assert.Contains("export begin2d :: Float32 -> Float32 -> Float32 -> Unit need ffi;", raw, StringComparison.Ordinal);

        var shim = File.ReadAllText(Path.Combine(packageDir, "native", "demo_shim.c"));
        Assert.Contains("void eidos_shim_draw_vec(float v_x, float v_y, float scale)", shim, StringComparison.Ordinal);
        Assert.Contains("    draw_vec((struct Vector2){ v_x, v_y }, scale);", shim, StringComparison.Ordinal);
        // 嵌套 struct 递归拆分
        Assert.Contains("void eidos_shim_begin2d(float cam_offset_x, float cam_offset_y, float cam_rotation)", shim, StringComparison.Ordinal);
        Assert.Contains("    begin2d((struct Camera2D){ (struct Vector2){ cam_offset_x, cam_offset_y }, cam_rotation });", shim, StringComparison.Ordinal);
    }

    [Fact]
    public void TaggedUnion_DeclaredAssociation_GeneratesAdtWithDecodeEncode()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        workspace.WriteText("demo.h", """
            union Value { int i; float f; };
            enum Kind { KIND_CLICK, KIND_MOVE };
            struct Event {
                enum Kind kind;
                union Value payload;
            };
            void handle_event(struct Event* e);
            """);
        var packageDir = workspace.Path("binding");
        Directory.CreateDirectory(packageDir);
        workspace.WriteText("binding/bindgen.toml", """
            package = "dev.eidos.demo"
            version = "0.1.0"
            library = "demo"
            headers = ["../demo.h"]
            parseMode = "clang"

            [[unions]]
            union = "Value"
            struct = "Event"
            tagField = "kind"
            payloadField = "payload"
            tagEnum = "Kind"
            name = "EventValue"

            [[unions.variants]]
            tag = "KIND_CLICK"
            member = "i"

            [[unions.variants]]
            tag = "KIND_MOVE"
            member = "f"
            """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(packageDir, Check: false, NoShim: false));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var raw = File.ReadAllText(Path.Combine(packageDir, "src", "raw.eidos"));
        // enum + union 对 ↦ 单个和类型（case-type 形式）
        Assert.Contains("export EventValue :: type { KindClick :: type(Int32), KindMove :: type(Float32) }", raw, StringComparison.Ordinal);
        Assert.Contains("export event_value_decode :: RawPtr -> EventValue need ffi", raw, StringComparison.Ordinal);
        Assert.Contains("tag == kind_click then KindClick(value_i_get(event_payload_ptr(p)))", raw, StringComparison.Ordinal);
        Assert.Contains("export event_value_encode :: EventValue -> RawPtr -> Unit need ffi", raw, StringComparison.Ordinal);
        Assert.Contains("KindClick(value) => p => {", raw, StringComparison.Ordinal);
        Assert.Contains("event_kind_set(p, kind_click);", raw, StringComparison.Ordinal);
        // 宿主 struct 辅助访问器
        Assert.Contains("export event_kind_get :: RawPtr -> Int need ffi;", raw, StringComparison.Ordinal);
        Assert.Contains("export event_payload_ptr :: RawPtr -> RawPtr need ffi;", raw, StringComparison.Ordinal);

        var shim = File.ReadAllText(Path.Combine(packageDir, "native", "demo_shim.c"));
        Assert.Contains("int64_t eidos_shim_struct_Event_kind_get(void* p)", shim, StringComparison.Ordinal);
        Assert.Contains("void eidos_shim_struct_Event_kind_set(void* p, int64_t v)", shim, StringComparison.Ordinal);
        Assert.Contains("void* eidos_shim_struct_Event_payload_ptr(void* p)", shim, StringComparison.Ordinal);

        // 生成模块过完整语义管线（extern + ADT + decode/encode）
        var rawPath = Path.Combine(packageDir, "src", "raw.eidos");
        var pipeline = new Eidosc.Pipeline.CompilationPipeline(
            File.ReadAllText(rawPath),
            new Eidosc.Pipeline.CompilationOptions
            {
                InputFile = rawPath,
                StopAtPhase = Eidosc.Pipeline.CompilationPhase.Llvm,
                UseColors = false,
                AllowVirtualInputFile = true
            });
        var analysis = pipeline.Run();
        Assert.True(analysis.Success, string.Join(
            Environment.NewLine,
            analysis.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    [Fact]
    public void TaggedUnion_UnknownMember_SkipsWithComment()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        workspace.WriteText("demo.h", """
            union Value { int i; float f; };
            enum Kind { KIND_CLICK };
            struct Event {
                enum Kind kind;
                union Value payload;
            };
            """);
        var packageDir = workspace.Path("binding");
        Directory.CreateDirectory(packageDir);
        workspace.WriteText("binding/bindgen.toml", """
            package = "dev.eidos.demo"
            version = "0.1.0"
            library = "demo"
            headers = ["../demo.h"]
            parseMode = "clang"

            [[unions]]
            union = "Value"
            struct = "Event"
            tagField = "kind"
            payloadField = "payload"
            tagEnum = "Kind"

            [[unions.variants]]
            tag = "KIND_CLICK"
            member = "missing_member"
            """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(packageDir, Check: false, NoShim: false));

        Assert.True(result.Success);
        var raw = File.ReadAllText(Path.Combine(packageDir, "src", "raw.eidos"));
        Assert.Contains("// SKIP tagged union Value: union member 'Value.missing_member' not found", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void StructParams_WithUnionField_Skipped()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        workspace.WriteText("demo.h", """
            union Value { int i; float f; };
            typedef struct Box { union Value v; int size; } Box;
            int box_area(Box b);
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
        Assert.Contains("// SKIP box_area: struct parameter 'b' contains unsplittable fields (union/array/unknown)", raw, StringComparison.Ordinal);
    }

    [Fact]
    public void MixedSplittableAndUnsplittableStructParams_SkipsFunctionAndGeneratesNoShim()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        workspace.WriteText("demo.h", """
            typedef struct Good { int x; int y; } Good;
            typedef struct Bad { char name[32]; int n; } Bad;
            void mixed(Good g, Bad b);
            void only_good(Good g);
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

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(packageDir, Check: false, NoShim: false));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));

        var raw = File.ReadAllText(Path.Combine(packageDir, "src", "raw.eidos"));
        // mixed：可拆分的 Good + 不可拆分的 Bad → 整函数 SKIP，不产出缺参数坏绑定
        Assert.Contains("// SKIP mixed: struct parameter 'b' contains unsplittable fields (union/array/unknown)", raw, StringComparison.Ordinal);
        Assert.Contains("export only_good :: Int32 -> Int32 -> Unit need ffi;", raw, StringComparison.Ordinal);

        var shim = File.ReadAllText(Path.Combine(packageDir, "native", "demo_shim.c"));
        Assert.DoesNotContain("eidos_shim_mixed", shim, StringComparison.Ordinal);
        Assert.Contains("void eidos_shim_only_good(int g_x, int g_y)", shim, StringComparison.Ordinal);
    }

    [Fact]
    public void SymbolAllowlist_RestrictsBoundFunctions()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_bind");
        workspace.WriteText("demo.h", "void a(void); void b(void); void c(void);\n");
        var packageDir = workspace.Path("binding");
        Directory.CreateDirectory(packageDir);
        workspace.WriteText("binding/bindgen.toml", """
            package = "dev.eidos.demo"
            version = "0.1.0"
            library = "demo"
            headers = ["../demo.h"]
            parseMode = "clang"
            symbols = ["a", "c"]
            """);

        var result = new BindingPackageGenerator().Generate(new BindingPackageGenerateOptions(packageDir, Check: false, NoShim: true));

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Diagnostics));
        var raw = File.ReadAllText(Path.Combine(packageDir, "src", "raw.eidos"));
        Assert.Contains("export a :: Unit -> Unit need ffi;", raw, StringComparison.Ordinal);
        Assert.Contains("export c :: Unit -> Unit need ffi;", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("export b ::", raw, StringComparison.Ordinal);
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
