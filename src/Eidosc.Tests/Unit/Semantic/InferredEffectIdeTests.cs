using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class InferredEffectIdeTests
{
    [Fact]
    public void SnapshotAndLsp_ExposeVirtualNeedAndMaterializeAction()
    {
        const string source = """
Emitter :: effect;

emit :: Unit -> Unit need Emitter
{
    _ => ()
}

helper :: Unit -> Unit
{
    _ => emit(())
}
""";
        var inputPath = Path.GetFullPath("inferred_effect_ide.eidos");
        var result = new CompilationPipeline(source, new CompilationOptions
        {
            InputFile = inputPath,
            LanguageVersion = EidosLanguageVersions.Current,
            StopAtPhase = CompilationPhase.Effects,
            NoImplicitPrelude = true,
            UseColors = false
        }).Run();

        Assert.True(result.Success);
        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var inferred = Assert.Single(snapshot.InferredEffects);
        Assert.Equal("helper", inferred.FunctionName);
        Assert.Equal("Emitter", inferred.EffectText);
        Assert.Equal(" need Emitter", inferred.NeedText);

        var range = new LspRange
        {
            Start = new LspPosition { Line = 0, Character = 0 },
            End = new LspPosition { Line = 20, Character = 0 }
        };
        var hint = Assert.Single(LspSemanticMapper.MapInlayHints(snapshot, inputPath, source, range));
        Assert.Equal(" need Emitter", hint.Label);

        var uri = new Uri(inputPath).AbsoluteUri;
        var action = Assert.Single(
            LspSemanticMapper.MapCodeActions(
                snapshot,
                uri,
                inputPath,
                range,
                source,
                documentVersion: 7),
            candidate => candidate.Title == "Materialize inferred effects for helper");
        Assert.Equal("Materialize inferred effects for helper", action.Title);
        var documentEdit = Assert.Single(action.Edit!.DocumentChanges!);
        var textDocumentEdit = Assert.IsType<LspTextDocumentEdit>(documentEdit);
        var edit = Assert.Single(textDocumentEdit.Edits);
        Assert.Equal(" need Emitter", edit.NewText);
    }
}
