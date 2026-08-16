using Eidosc;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    /// <summary>
    /// 调用方持有的 out-ABI 聚合体栈 blob 必须带 EidosHeader（EIDOS_STACK_BIT|1 + 构造器
    /// 类型 id）。否则 ref 接收者的访问器（eidos_incref_local）会把 blob 前 8 字节当作
    /// 引用计数递增，破坏相邻栈槽 —— std.Network 冒烟测试中表现为已持有指针被 +1 后的
    /// 原生崩溃（issue #82）。
    /// </summary>
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Network)]
    public void CallerOwnedAggregateBlob_CarriesStackHeader_RefReceiverAccessorsAreSafe()
    {
        var result = RunFixtureAtLlvm(Fx("stdlib/std_network_import.eidos"));
        Assert.True(result.Success, string.Join(
            Environment.NewLine,
            result.Diagnostics.Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}")));
        var llvmIr = Assert.IsType<string>(result.LlvmIrText);

        // out-ABI 生效：main 通过 caller_out 变体把结果写进自己的栈 blob。
        Assert.Contains("caller_out", llvmIr, StringComparison.Ordinal);

        // blob 带栈头：ref_count = EIDOS_STACK_BIT|1 = 0x40000001 = 1073741825。
        Assert.Contains("store i32 1073741825", llvmIr, StringComparison.Ordinal);

        // 聚合体数据位于 wrapper 字段 1（字段 0 是头），ref 访问器拿到的是 data 指针。
        Assert.Matches(@"aggregate_l\d+_data(_\d+)? = getelementptr", llvmIr);
    }
}
