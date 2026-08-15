using Eidosc;
using Eidosc.CodeGen;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    /// <summary>
    /// M6 混合编译验收：--lto 下 Eidos IR 经 clang -flto 产 LLVM bitcode 对象、
    /// C 侧对象统一带 -flto，链接期跨语言优化后行为不变（与关闭 LTO 的退出码一致）。
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void MixedCompilation_LtoBuild_CrossLanguageBehaviorPreserved()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            import std.IntNarrow

            @[extern(c, name: "eidos_test_lto_scale")]
            c_scale :: Int32 -> Int32 need ffi;

            main :: Unit -> Int need ffi
            {
                _ => IntNarrow.to_int32(c_scale(IntNarrow.from_int32(6)))
            }
            """;

        const string cSource = """
            int eidos_test_lto_scale(int x) { return x * 7; }
            """;

        var withLto = CompileAndRunSourceAtNative(
            source,
            "mixed_lto_native.eidos",
            "mixed_lto_native",
            nativeCSource: cSource,
            enableLto: true);

        var withoutLto = CompileAndRunSourceAtNative(
            source,
            "mixed_no_lto_native.eidos",
            "mixed_no_lto_native",
            nativeCSource: cSource,
            enableLto: false);

        Assert.Equal(42, withoutLto.ExitCode);
        Assert.Equal(withoutLto.ExitCode, withLto.ExitCode);
    }
}
