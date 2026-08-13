using Eidosc.Bindgen;
using Eidosc.Bindgen.Clang;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Bindgen;

/// <summary>
/// in-process libclang 全量提取器（P0 M2）测试。依赖本机 LLVM/libclang（同 M1）。
/// </summary>
public sealed class ClangHeaderParserTests
{
    private static CHeaderIr ParseHeader(string source, string fileName = "demo.h")
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_parser");
        var header = Path.Combine(workspace.Root, fileName);
        File.WriteAllText(header, source);
        Assert.True(ClangNative.TryLoad(out var error, out var api), error);
        var result = new ClangHeaderParser(api!).Parse(header);
        Assert.True(result.Errors.Count == 0, string.Join(Environment.NewLine, result.Errors));
        return result.Ir!;
    }

    [Fact]
    public void Parse_ExtractsFunctionsWithVariadicAndInline()
    {
        var ir = ParseHeader("""
            int add(int a, int b);
            int sum(int n, ...);
            static inline int twice(int x) { return x * 2; }
            """);

        Assert.Equal(3, ir.Functions.Count);

        var add = ir.Functions.Single(f => f.Name == "add");
        Assert.False(add.IsVariadic);
        Assert.False(add.IsInline);
        Assert.Equal(CBindingTypeKind.Primitive, add.ReturnType.Kind);
        Assert.Equal(2, add.Parameters.Count);
        Assert.Equal("a", add.Parameters[0].Name);
        Assert.Equal("int", add.Parameters[0].Type.Name);

        var sum = ir.Functions.Single(f => f.Name == "sum");
        Assert.True(sum.IsVariadic);

        var twice = ir.Functions.Single(f => f.Name == "twice");
        Assert.True(twice.IsInline);
    }

    [Fact]
    public void Parse_ExtractsStructsWithLayoutFacts()
    {
        var ir = ParseHeader("typedef struct Pair { char tag; int value; } Pair;");

        var pair = ir.Structs.Single(s => s.Name == "Pair");
        Assert.Equal(8, pair.Size);
        Assert.Equal(4, pair.Alignment);
        Assert.Equal(2, pair.Fields.Count);

        var tag = pair.Fields.Single(f => f.Name == "tag");
        Assert.Equal(0, tag.Offset);
        Assert.Equal(1, tag.Size);

        var value = pair.Fields.Single(f => f.Name == "value");
        Assert.Equal(4, value.Offset);
        Assert.Equal(4, value.Size);
    }

    [Fact]
    public void Parse_ExtractsUnionsAndTypedefs()
    {
        var ir = ParseHeader("""
            typedef struct Point { int x; int y; } Point;
            typedef unsigned int uint32;
            union Value { int i; float f; };
            """);

        var value = ir.UnionsSafe.Single(u => u.Name == "Value");
        Assert.Equal(2, value.Fields.Count);
        Assert.Equal(0, value.Fields[0].Offset);
        Assert.Equal(4, value.Size);

        var uint32 = ir.TypedefsSafe.Single(t => t.Name == "uint32");
        Assert.Equal(CBindingTypeKind.Primitive, uint32.UnderlyingKind);
        Assert.Equal("unsigned int", uint32.Underlying);

        var pointTypedef = ir.TypedefsSafe.Single(t => t.Name == "Point");
        Assert.Equal(CBindingTypeKind.Struct, pointTypedef.UnderlyingKind);
        Assert.Equal("struct Point", pointTypedef.Underlying);
    }

    [Fact]
    public void Parse_ExtractsEnumValues()
    {
        var ir = ParseHeader("enum Color { RED, GREEN = 5, BLUE };");

        var color = ir.Enums.Single(e => e.Name == "Color");
        Assert.Equal(3, color.Values.Count);
        Assert.Equal(("RED", 0L), (color.Values[0].Name, color.Values[0].Value));
        Assert.Equal(("GREEN", 5L), (color.Values[1].Name, color.Values[1].Value));
        Assert.Equal(("BLUE", 6L), (color.Values[2].Name, color.Values[2].Value));
    }

    [Fact]
    public void Parse_ExtractsMacroConstants()
    {
        var ir = ParseHeader("""
            #define VERSION 42
            #define NAME "hello"
            #define RLAPI=
            #define MAX(a, b) ((a) > (b) ? (a) : (b))
            """);

        Assert.Contains(ir.ConstantsSafe, c => c.Name == "VERSION" && c.Value == "42" && !c.IsString);
        Assert.Contains(ir.ConstantsSafe, c => c.Name == "NAME" && c.Value == "hello" && c.IsString);
        Assert.DoesNotContain(ir.ConstantsSafe, c => c.Name is "RLAPI" or "MAX");
    }

    [Fact]
    public void Parse_ExtractsFunctionPointerParameter()
    {
        var ir = ParseHeader("int apply(int (*fn)(int), int x);");

        var apply = ir.Functions.Single(f => f.Name == "apply");
        var fn = apply.Parameters[0];
        Assert.Equal(CBindingTypeKind.FunctionPointer, fn.Type.Kind);
        Assert.Equal(1, fn.Type.FunctionPointerArity);
        Assert.Equal(CBindingTypeKind.Primitive, apply.Parameters[1].Type.Kind);
    }

    [Fact]
    public void Parse_ExtractsGlobals()
    {
        var ir = ParseHeader("extern int global_counter; const char* version_tag;");

        var counter = ir.GlobalsSafe.Single(g => g.Name == "global_counter");
        Assert.Equal(CBindingTypeKind.Primitive, counter.Type.Kind);
        Assert.Contains(ir.GlobalsSafe, g => g.Name == "version_tag" && g.Type.Kind == CBindingTypeKind.Pointer);
    }

    [Fact]
    public void Parse_ReturnsErrorsWithoutIr()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang_parser");
        var header = Path.Combine(workspace.Root, "broken.h");
        File.WriteAllText(header, "int broken( ;\n");
        Assert.True(ClangNative.TryLoad(out var error, out var api));

        var result = new ClangHeaderParser(api!).Parse(header);

        Assert.Null(result.Ir);
        Assert.NotEmpty(result.Errors);
    }
}
