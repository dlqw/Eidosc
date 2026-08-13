using Eidosc.Bindgen.Clang;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Bindgen;

/// <summary>
/// in-process libclang 提取层（M1）测试。
/// 这些测试要求本机安装 LLVM：libclang 与编译/链接所用的 clang 同装，
/// 定位策略见 <see cref="ClangNative"/>（LLVM_PATH → PATH → 标准安装目录）。
/// </summary>
public sealed class ClangNativeTests
{
    [Fact]
    public void TryLoad_FindsLibclangAndReportsVersion()
    {
        var loaded = ClangNative.TryLoad(out var error, out var api);
        Assert.True(loaded, error);
        Assert.NotNull(api);
        Assert.False(string.IsNullOrWhiteSpace(api!.LibraryPath));

        using var session = new ClangSession(api);
        Assert.Contains("clang version", session.ClangVersion, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Parse_EnumeratesTopLevelDeclarations()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang");
        var header = Path.Combine(workspace.Root, "demo.h");
        File.WriteAllText(header, """
            typedef struct Point { int x; int y; } Point;
            enum Color { RED, GREEN = 5, BLUE };
            int demo_add(int a, int b);
            """);

        Assert.True(ClangNative.TryLoad(out var error, out var api));
        using var session = new ClangSession(api!);
        session.Parse(header);
        Assert.False(session.HasErrors, string.Join(Environment.NewLine, session.Diagnostics));

        var declarations = new List<(ClangCursorKind Kind, string Name)>();
        session.VisitChildren(session.RootCursor, (cursor, _, _) =>
        {
            declarations.Add(((ClangCursorKind)cursor.Kind, session.GetCursorSpelling(cursor)));
            return ClangChildVisitResult.Continue;
        }, IntPtr.Zero);

        Assert.Contains(declarations, d => d.Kind == ClangCursorKind.FunctionDecl && d.Name == "demo_add");
        Assert.Contains(declarations, d => d.Kind == ClangCursorKind.StructDecl && d.Name == "Point");
        Assert.Contains(declarations, d => d.Kind == ClangCursorKind.EnumDecl && d.Name == "Color");
        Assert.Contains(declarations, d => d.Kind == ClangCursorKind.TypedefDecl && d.Name == "Point");
    }

    [Fact]
    public void Parse_ReportsSyntaxErrors()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang");
        var header = Path.Combine(workspace.Root, "broken.h");
        File.WriteAllText(header, "int demo_broken( ;\n");

        Assert.True(ClangNative.TryLoad(out var error, out var api));
        using var session = new ClangSession(api!);
        session.Parse(header);

        Assert.True(session.HasErrors);
        Assert.Contains(session.Diagnostics, d => d.StartsWith("Error", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_StructFieldOffsetsAndSizes()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang");
        var header = Path.Combine(workspace.Root, "layout.h");
        File.WriteAllText(header, "typedef struct Pair { char tag; int value; } Pair;\n");

        Assert.True(ClangNative.TryLoad(out var error, out var api));
        using var session = new ClangSession(api!);
        session.Parse(header);
        Assert.False(session.HasErrors, string.Join(Environment.NewLine, session.Diagnostics));

        var structCursor = default(ClangCursor);
        var found = false;
        session.VisitChildren(session.RootCursor, (cursor, _, _) =>
        {
            if ((ClangCursorKind)cursor.Kind == ClangCursorKind.StructDecl && session.GetCursorSpelling(cursor) == "Pair")
            {
                structCursor = cursor;
                found = true;
                return ClangChildVisitResult.Break;
            }

            return ClangChildVisitResult.Continue;
        }, IntPtr.Zero);
        Assert.True(found);

        var fields = new List<(string Name, long OffsetBits, long Size)>();
        session.VisitChildren(structCursor, (cursor, _, _) =>
        {
            fields.Add((
                session.GetCursorSpelling(cursor),
                session.Api.CursorGetOffsetOfField(cursor),
                session.Api.TypeGetSizeOf(session.Api.GetCursorType(cursor))));
            return ClangChildVisitResult.Continue;
        }, IntPtr.Zero);

        var tag = fields.Single(f => f.Name == "tag");
        Assert.Equal(0, tag.OffsetBits);
        Assert.Equal(1, tag.Size);

        var value = fields.Single(f => f.Name == "value");
        Assert.Equal(32, value.OffsetBits);
        Assert.Equal(4, value.Size);
    }

    [Fact]
    public void Parse_AppliesDefinesAndIncludePaths()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_clang");
        var include = Path.Combine(workspace.Root, "include");
        Directory.CreateDirectory(include);
        File.WriteAllText(Path.Combine(include, "dep.h"), "typedef long dep_value;\n");
        var header = Path.Combine(workspace.Root, "api.h");
        File.WriteAllText(header, """
            #include "dep.h"
            RLAPI int demo_init(dep_value width);
            """);

        Assert.True(ClangNative.TryLoad(out var error, out var api));
        using var session = new ClangSession(api!);

        // 未提供 RLAPI 定义与 include 路径时应有错误
        session.Parse(header);
        Assert.True(session.HasErrors);

        // 提供后应解析干净（对齐 raylib 头文件的 RLAPI= 模式）
        session.Parse(header, includePaths: [include], defines: ["RLAPI="]);
        Assert.False(session.HasErrors, string.Join(Environment.NewLine, session.Diagnostics));
    }
}
