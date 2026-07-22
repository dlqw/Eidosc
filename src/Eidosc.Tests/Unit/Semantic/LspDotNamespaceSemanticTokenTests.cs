using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspDotNamespaceSemanticTokenTests
{
    [Fact]
    public void MapSemanticTokens_DoesNotGuessNamespaceOrFunctionKindsFromSpelling()
    {
        const string source = """
std.Option.unwrap_or(value)
Thing :: type { A :: type {}, B :: type {} }
""";
        var tokens = Decode(LspSemanticMapper.MapSemanticTokens(
            new IdeSemanticSnapshot(),
            documentFilePath: null,
            sourceText: source));

        Assert.DoesNotContain(tokens, token => token.Type is "module" or "function");
    }

    [Fact]
    public void MapSemanticTokens_ClassifiesLowercasePackageAliasFollowedByUppercaseNamespace()
    {
        const string source = "crypto_a.Hash.Sha256.digest(value)";
        var snapshot = new IdeSemanticSnapshot
        {
            Symbols =
            [
                new IdeSymbolEntry
                {
                    SymbolId = 1,
                    Name = "crypto_a",
                    Kind = "module",
                    Detail = "module"
                },
                new IdeSymbolEntry
                {
                    SymbolId = 2,
                    Name = "Hash",
                    Kind = "module",
                    Detail = "module"
                },
                new IdeSymbolEntry
                {
                    SymbolId = 3,
                    Name = "Sha256",
                    Kind = "module",
                    Detail = "module"
                },
                new IdeSymbolEntry
                {
                    SymbolId = 4,
                    Name = "digest",
                    Kind = "function",
                    Detail = "function"
                }
            ],
            Occurrences =
            [
                new IdeOccurrenceEntry
                {
                    SymbolId = 1,
                    Role = "reference",
                    Source = "PathExprPrefix",
                    Span = Span(0, 8)
                },
                new IdeOccurrenceEntry
                {
                    SymbolId = 2,
                    Role = "reference",
                    Source = "PathExprPrefix",
                    Span = Span(9, 4)
                },
                new IdeOccurrenceEntry
                {
                    SymbolId = 3,
                    Role = "reference",
                    Source = "PathExprPrefix",
                    Span = Span(14, 6)
                },
                new IdeOccurrenceEntry
                {
                    SymbolId = 4,
                    Role = "reference",
                    Source = "PathExpr",
                    Span = Span(21, 6)
                }
            ]
        };

        var tokens = Decode(LspSemanticMapper.MapSemanticTokens(
            snapshot,
            documentFilePath: null,
            sourceText: source));

        Assert.Contains(tokens, token => token is (0, 0, 8, "module"));
        Assert.Contains(tokens, token => token is (0, 9, 4, "module"));
        Assert.Contains(tokens, token => token is (0, 14, 6, "module"));
        Assert.Contains(tokens, token => token is (0, 21, 6, "function"));
    }

    [Fact]
    public void MapCodeActions_RenameSymbolSuggestion_EditsAllSemanticOccurrences()
    {
        var filePath = Path.GetFullPath("rename_symbol.eidos");
        var definition = Span(0, 11, filePath);
        var reference = Span(20, 11, filePath);
        var snapshot = new IdeSemanticSnapshot
        {
            InputFile = filePath,
            Occurrences =
            [
                new IdeOccurrenceEntry { SymbolId = 7, Role = "definition", Span = definition },
                new IdeOccurrenceEntry { SymbolId = 7, Role = "reference", Span = reference }
            ],
            Diagnostics =
            [
                new IdeDiagnosticEntry
                {
                    Severity = "warning",
                    Code = "S1101",
                    Span = definition,
                    Suggestions =
                    [
                        new IdeDiagnosticSuggestionEntry
                        {
                            Kind = "RenameSymbol",
                            Message = "Rename symbol",
                            Span = definition,
                            Replacement = "bad_function",
                            OriginalSymbolId = 7
                        }
                    ]
                }
            ]
        };

        var actions = LspSemanticMapper.MapCodeActions(
            snapshot,
            new Uri(filePath).AbsoluteUri,
            filePath,
            new LspRange
            {
                Start = new LspPosition { Line = 0, Character = 0 },
                End = new LspPosition { Line = 0, Character = 11 }
            });

        var action = Assert.Single(actions);
        Assert.True(action.IsPreferred);
        Assert.Equal(2, Assert.Single(action.Edit!.Changes).Value.Count);
        Assert.All(action.Edit.Changes.Single().Value, edit => Assert.Equal("bad_function", edit.NewText));
    }

    private static IdeSpan Span(int start, int length, string? filePath = null) => new()
    {
        StartLine = 0,
        StartCharacter = start,
        EndLine = 0,
        EndCharacter = start + length,
        Start = start,
        Length = length,
        FilePath = filePath
    };

    private static List<(int Line, int Character, int Length, string Type)> Decode(LspSemanticTokens tokens)
    {
        var result = new List<(int, int, int, string)>();
        var line = 0;
        var character = 0;
        for (var index = 0; index < tokens.Data.Count; index += 5)
        {
            var deltaLine = tokens.Data[index];
            line += deltaLine;
            character = deltaLine == 0 ? character + tokens.Data[index + 1] : tokens.Data[index + 1];
            result.Add((
                line,
                character,
                tokens.Data[index + 2],
                LspSemanticTokenTypes.All[tokens.Data[index + 3]]));
        }

        return result;
    }
}
