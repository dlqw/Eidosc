using Eidosc.Tests.Fixtures;
using Xunit;

namespace Eidosc.Tests.Integration;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Category, TestCategories.Benchmark)]
public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    [Trait(TestCategories.Category, TestCategories.Native)]
    public void CallLayerChecksum_WithAndWithoutMirOptimization_IsEquivalent()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            fib_naive :: Int -> Int
            {
                n => if n < 2 then { n } else { fib_naive(n - 1) + fib_naive(n - 2) }
            }

            fib_iter :: Int -> Int
            {
                n => {
                    mut index := 0;
                    mut previous := 0;
                    mut current := 1;
                    loop {
                        if index >= n then { break } else {
                            next := previous + current;
                            previous := current;
                            current := next;
                            index := index + 1
                        }
                    };
                    previous
                }
            }

            identity :: Int -> Int
            {
                value => value
            }

            main :: Unit -> Int
            {
                _ => (fib_naive(12) * 3 + fib_iter(12) * 5 + identity(5) * 7) % 251
            }
            """;

        var baseline = CompileAndRunSourceAtNative(
            source,
            "call_layer_checksum_baseline.eidos",
            "call_layer_checksum_baseline",
            enableMirOptimizations: false);
        var optimized = CompileAndRunSourceAtNative(
            source,
            "call_layer_checksum_optimized.eidos",
            "call_layer_checksum_optimized",
            enableMirOptimizations: true);

        Assert.Equal(183, baseline.ExitCode);
        Assert.Equal(baseline.ExitCode, optimized.ExitCode);
    }
}
