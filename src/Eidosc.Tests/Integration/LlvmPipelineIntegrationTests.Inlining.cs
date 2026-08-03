using Xunit;

namespace Eidosc.Tests.Integration;

public partial class LlvmPipelineIntegrationTests
{
    [Fact]
    public void Inlining_ManagedRecordIdentity_NativeSmoke_PreservesOwnership()
    {
        const string source = """
            Box :: type {
                text :: String,
                value :: Int
            }

            identity :: Box -> Box { box => box }

            main :: Unit -> Int {
                _ => {
                    box := identity(Box { text: "managed", value: 37 })
                    box.value
                }
            }
            """;

        var execution = CompileAndRunSourceAtNative(
            source,
            "inlining_managed_record_identity.eidos",
            "inlining_managed_record_identity");

        Assert.Equal(37, execution.ExitCode);
    }
}
