using Eidosc.Debug;
using Eidosc.Tests.Fixtures;

namespace Eidosc.Tests.Unit.Debug;

public class DebugOutputTests
{
    [Fact]
    public void PhaseScope_Emit_WritesTextArtifactInsidePhaseDirectory()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_debug_output");
        var tempDir = workspace.Root;
        using var emitter = new FileDebugEmitter(tempDir, DebugLevel.Diagnostic);
        var context = new DebugContext(emitter);

        using (context.PhaseScope("01_lexer"))
        {
            context.Emit("tokens", "token text");
        }

        Assert.True(File.Exists(Path.Combine(tempDir, "01_lexer", "tokens.txt")));
        Assert.False(File.Exists(Path.Combine(tempDir, "tokens.txt")));
    }

    [Fact]
    public void Emit_WithGraphFormatBoth_DoesNotWriteGraphArtifactsForTextSnapshots()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_debug_output");
        var tempDir = workspace.Root;
        var parserDir = Path.Combine(tempDir, "02_parser");
        Directory.CreateDirectory(parserDir);
        File.WriteAllText(Path.Combine(parserDir, "ast.d2"), "stale");
        File.WriteAllText(Path.Combine(parserDir, "ast.svg"), "stale");

        using var emitter = new FileDebugEmitter(
            tempDir,
            DebugLevel.Diagnostic,
            DebugGraphFormat.Both);
        var context = new DebugContext(emitter);

        using (context.PhaseScope("02_parser"))
        {
            context.Emit("ast", "ModuleDecl\n  FuncDef main");
        }

        Assert.True(File.Exists(Path.Combine(tempDir, "02_parser", "ast.txt")));
        Assert.False(File.Exists(Path.Combine(tempDir, "02_parser", "ast.d2")));
        Assert.False(File.Exists(Path.Combine(tempDir, "02_parser", "ast.svg")));
    }

    [Fact]
    public void Emit_WithGraphFormatBoth_WritesMirCfgGraphArtifacts()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_debug_output");
        var tempDir = workspace.Root;
        using var emitter = new FileDebugEmitter(
            tempDir,
            DebugLevel.Diagnostic,
            DebugGraphFormat.Both);
        var context = new DebugContext(emitter);

        using (context.PhaseScope("07_mir"))
        {
            context.Emit(
                "mir",
                """
                    func main {
                      locals:
                        param %1: _arg
                      bb1:
                        %2 = true
                        switch %2 [true => bb2, _ => bb3]
                      bb2:
                        goto bb4
                      bb3:
                        goto bb4
                      bb4:
                        return %2
                    }
                    """);
        }

        var d2Path = Path.Combine(tempDir, "07_mir", "mir_001_main.d2");
        var svgPath = Path.Combine(tempDir, "07_mir", "mir_001_main.svg");

        Assert.True(File.Exists(Path.Combine(tempDir, "07_mir", "mir.txt")));
        Assert.False(File.Exists(Path.Combine(tempDir, "07_mir", "mir.d2")));
        Assert.False(File.Exists(Path.Combine(tempDir, "07_mir", "mir.svg")));
        Assert.True(File.Exists(d2Path));
        Assert.True(File.Exists(svgPath));

        var d2 = File.ReadAllText(d2Path);
        Assert.Contains("block_main_bb1", d2);
        Assert.Contains("block_main_bb2", d2);
        Assert.Contains("fn_main.block_main_bb1 -> fn_main.block_main_bb2", d2);
        Assert.Contains("fn_main.block_main_bb1 -> fn_main.block_main_bb3", d2);
        Assert.Contains("fn_main.block_main_bb2 -> fn_main.block_main_bb4", d2);
    }

    [Fact]
    public void Constructor_WithCleanOutputDirectory_RemovesPreviousArtifacts()
    {
        using var workspace = TestTempWorkspace.Create("eidosc_debug_output");
        var tempDir = workspace.Root;
        var stalePhaseDir = Path.Combine(tempDir, "07_mir");
        Directory.CreateDirectory(stalePhaseDir);
        File.WriteAllText(Path.Combine(stalePhaseDir, "stale.txt"), "old");

        using var emitter = new FileDebugEmitter(
            tempDir,
            DebugLevel.Diagnostic,
            DebugGraphFormat.None,
            cleanOutputDirectory: true);
        var context = new DebugContext(emitter);

        using (context.PhaseScope("01_lexer"))
        {
            context.Emit("tokens", "fresh");
        }

        Assert.False(File.Exists(Path.Combine(stalePhaseDir, "stale.txt")));
        Assert.True(File.Exists(Path.Combine(tempDir, "01_lexer", "tokens.txt")));
    }
}
