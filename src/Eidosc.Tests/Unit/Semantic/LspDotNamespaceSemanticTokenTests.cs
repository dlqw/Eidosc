using Eidosc.Cli.Lsp;
using Eidosc.Ide;
using Xunit;

namespace Eidosc.Tests.Unit.Semantic;

public sealed class LspDotNamespaceSemanticTokenTests
{
    [Fact]
    public void MapSemanticTokens_ClassifiesDotNamespacePrefixesWithoutTreatingBindingAsNamespace()
    {
        const string source = """
Std.Option.unwrap_or(value)
Thing :: type { A, B }
""";
        var tokens = Decode(LspSemanticMapper.MapSemanticTokens(
            new IdeSemanticSnapshot(),
            documentFilePath: null,
            sourceText: source));

        Assert.Contains(tokens, token => token is (0, 0, 3, "module"));
        Assert.Contains(tokens, token => token is (0, 4, 6, "module"));
        Assert.Contains(tokens, token => token is (0, 11, 9, "function"));
        Assert.DoesNotContain(tokens, token => token is (1, 0, 5, "module"));
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
                    Source = "PathExpr",
                    Span = new IdeSpan
                    {
                        StartLine = 0,
                        StartCharacter = 0,
                        EndLine = 0,
                        EndCharacter = 27,
                        Start = 0,
                        Length = 27
                    }
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
