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
            },
            sourceText: new string('x', 40),
            documentVersion: 17);

        var action = Assert.Single(actions);
        Assert.True(action.IsPreferred);
        Assert.Empty(action.Edit!.Changes);
        var documentEdit = Assert.IsType<LspTextDocumentEdit>(Assert.Single(action.Edit.DocumentChanges!));
        Assert.Equal(17, documentEdit.TextDocument.Version);
        Assert.Equal(2, documentEdit.Edits.Count);
        Assert.All(documentEdit.Edits, edit => Assert.Equal("bad_function", edit.NewText));
    }

    [Fact]
    public void MapCodeActions_RecomputesUtf16MultilineRangeFromOffsetsAndCarriesVersion()
    {
        const string source = "α😀\r\nsecond\nthird";
        const string uri = "file:///coordinate_map.eidos";
        var span = new IdeSpan
        {
            StartLine = 99,
            StartCharacter = 99,
            EndLine = 99,
            EndCharacter = 99,
            Start = 1,
            Length = 7
        };
        var snapshot = SnapshotWithSuggestion(span, "first\nreplacement");

        var action = Assert.Single(LspSemanticMapper.MapCodeActions(
            snapshot,
            uri,
            documentFilePath: null,
            WholeDocumentRange(),
            source,
            documentVersion: 23));

        Assert.Equal("refactor.rewrite", action.Kind);
        var documentEdit = Assert.IsType<LspTextDocumentEdit>(Assert.Single(action.Edit!.DocumentChanges!));
        Assert.Equal(23, documentEdit.TextDocument.Version);
        var edit = Assert.Single(documentEdit.Edits);
        Assert.Equal(0, edit.Range.Start.Line);
        Assert.Equal(1, edit.Range.Start.Character);
        Assert.Equal(1, edit.Range.End.Line);
        Assert.Equal(3, edit.Range.End.Character);
        Assert.Equal("first\nreplacement", edit.NewText);
    }

    [Fact]
    public void MapCodeActions_PreservesZeroLengthInsertionAndNullReplacementDeletion()
    {
        const string source = "first\r\nsecond";
        const string uri = "file:///zero_length.eidos";
        var insertion = SnapshotWithSuggestion(SpanAt(7, 0), "inserted");
        var deletion = SnapshotWithSuggestion(SpanAt(7, 6), replacement: null);

        var insertionEdit = GetSingleEdit(LspSemanticMapper.MapCodeActions(
            insertion, uri, null, WholeDocumentRange(), source, 3));
        Assert.Equal(insertionEdit.Range.Start.Line, insertionEdit.Range.End.Line);
        Assert.Equal(insertionEdit.Range.Start.Character, insertionEdit.Range.End.Character);
        Assert.Equal(1, insertionEdit.Range.Start.Line);
        Assert.Equal(0, insertionEdit.Range.Start.Character);
        Assert.Equal("inserted", insertionEdit.NewText);

        var deletionEdit = GetSingleEdit(LspSemanticMapper.MapCodeActions(
            deletion, uri, null, WholeDocumentRange(), source, 4));
        Assert.Equal(string.Empty, deletionEdit.NewText);
        Assert.Equal(1, deletionEdit.Range.Start.Line);
        Assert.Equal(0, deletionEdit.Range.Start.Character);
        Assert.Equal(1, deletionEdit.Range.End.Line);
        Assert.Equal(6, deletionEdit.Range.End.Character);
    }

    private static IdeSemanticSnapshot SnapshotWithSuggestion(IdeSpan span, string? replacement) => new()
    {
        Refactors =
        [
            new IdeDiagnosticEntry
            {
                Code = "S1005",
                Span = span,
                Suggestions =
                [
                    new IdeDiagnosticSuggestionEntry
                    {
                        Kind = "StyleRewrite",
                        Message = "Rewrite",
                        Span = span,
                        Replacement = replacement
                    }
                ]
            }
        ]
    };

    private static IdeSpan SpanAt(int start, int length) => new()
    {
        Start = start,
        Length = length
    };

    private static LspRange WholeDocumentRange() => new()
    {
        Start = new LspPosition { Line = 0, Character = 0 },
        End = new LspPosition { Line = int.MaxValue, Character = int.MaxValue }
    };

    private static LspTextEdit GetSingleEdit(IReadOnlyList<LspCodeAction> actions)
    {
        var action = Assert.Single(actions);
        var documentEdit = Assert.IsType<LspTextDocumentEdit>(Assert.Single(action.Edit!.DocumentChanges!));
        return Assert.Single(documentEdit.Edits);
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
