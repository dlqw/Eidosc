using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspClauseSchemaTests
{
    [Fact]
    public void Completion_uses_schema_and_filters_by_declaration_target()
    {
        const string source = "@[der";

        var items = LspSemanticMapper.MapCompletions(
            new IdeSemanticSnapshot(),
            line: 0,
            character: source.Length,
            source);

        var derive = Assert.Single(items, item => item.Label == "derive");
        Assert.Contains("typed declaration tag", derive.Detail, StringComparison.Ordinal);
        Assert.Contains("Valid targets: Type", derive.Documentation, StringComparison.Ordinal);
        Assert.DoesNotContain(items, item => item.Label is "need" or "impl");
    }

    [Fact]
    public void Hover_renders_the_versioned_clause_contract()
    {
        const string source = "@[derive(Eq)] Subject :: type {}";
        var snapshot = new IdeSemanticSnapshot();

        var hover = LspSemanticMapper.MapHover(
            snapshot,
            new LspSemanticMapper.SnapshotIndex(snapshot),
            line: 0,
            character: source.IndexOf("derive", StringComparison.Ordinal) + 2,
            source);

        Assert.NotNull(hover);
        var markup = Assert.IsType<LspMarkupContent>(hover.Contents);
        Assert.Contains("`derive` typed declaration tag", markup.Value, StringComparison.Ordinal);
        Assert.Contains("Stage: `Semantic`", markup.Value, StringComparison.Ordinal);
        Assert.Contains("Arguments: `Trait`", markup.Value, StringComparison.Ordinal);
        Assert.Contains("Source order: `GeneratorSequence`", markup.Value, StringComparison.Ordinal);
    }
}
