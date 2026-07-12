using Eidosup.Installation;

namespace Eidosc.Tests.Unit.Eidosup;

public sealed class ProfileScriptWriterTests
{
    [Fact]
    public void BuildUnixProfileBlock_ContainsAllVariables()
    {
        var plan = new EnvironmentPlan(
            "/home/test/.local/share/eidos",
            "/home/test/.local/share/eidos/toolchains/eidosc/0.3.3-alpha.1",
            "/home/test/.local/share/eidos/toolchains/eidosc/0.3.3-alpha.1/runtime",
            "/opt/llvm",
            ["/home/test/.local/share/eidos/toolchains/eidosc/0.3.3-alpha.1", "/opt/llvm/bin"]);

        var block = ProfileScriptWriter.BuildUnixProfileBlock(plan);

        Assert.Contains("export EIDOS_HOME=\"/home/test/.local/share/eidos\"", block, StringComparison.Ordinal);
        Assert.Contains("export EIDOSC_HOME=\"/home/test/.local/share/eidos/toolchains/eidosc/0.3.3-alpha.1\"", block, StringComparison.Ordinal);
        Assert.Contains("export EIDOS_RUNTIME_PATH=\"/home/test/.local/share/eidos/toolchains/eidosc/0.3.3-alpha.1/runtime\"", block, StringComparison.Ordinal);
        Assert.Contains("export EIDOS_LLVM_HOME=\"/opt/llvm\"", block, StringComparison.Ordinal);
        Assert.Contains("export PATH=\"/home/test/.local/share/eidos/toolchains/eidosc/0.3.3-alpha.1:/opt/llvm/bin:$PATH\"", block, StringComparison.Ordinal);
    }

    [Fact]
    public void UpsertBlock_ReplacesExistingManagedBlock()
    {
        const string existing = """
            export PATH="/usr/bin:$PATH"
            # >>> eidosup >>>
            export EIDOS_HOME="/old"
            # <<< eidosup <<<
            """;

        var updated = ProfileScriptWriter.UpsertBlock(existing, "# >>> eidosup >>>\nexport EIDOS_HOME=\"/new\"\n# <<< eidosup <<<\n");

        Assert.DoesNotContain("export EIDOS_HOME=\"/old\"", updated, StringComparison.Ordinal);
        Assert.Contains("export EIDOS_HOME=\"/new\"", updated, StringComparison.Ordinal);
    }
}
