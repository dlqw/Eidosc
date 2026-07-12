using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void StringLiteralInterning_NativeSmoke_ReturnsExpectedValue_WhenToolchainAvailable()
    {
        if (!ToolExists("clang"))
        {
            return;
        }

        const string source = """
            choose :: Bool -> String
            {
                flag => if flag then { "repeat" } else { "repeat" }
            }

            main :: Unit -> Int
            {
                _ => {
                    a := choose(true);
                    b := choose(false);
                    if a == b then { 0 } else { 1 }
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "string_interning_native_smoke.eidos",
            "string_interning_native_smoke");

        Assert.Equal(0, execution.ExitCode);
    }
}
