using Eidosc.Cli.Resources;
using Eidosc.Diagnostic;
using Eidosc.Ide;
using System.Collections.Concurrent;

namespace Eidosc.Cli.Lsp;

/// <summary>
/// 将 IdeSemanticSnapshot 数据映射到 LSP 协议类型
/// </summary>
public static class LspSemanticMapper
{
    private static readonly HashSet<string> PatternCoverageCodes = ["W4200", "W4201"];

    private readonly record struct SemanticTokenCandidate(
        int Line,
        int Character,
        int Length,
        int TokenType,
        int Modifiers,
        int Priority,
        bool IsSemantic);

    private sealed class SemanticTokenCandidateSet
    {
        private readonly Dictionary<int, List<int>> _indicesByLine = new();

        public List<SemanticTokenCandidate> Items { get; } = [];

        public int Count => Items.Count;

        public void Add(SemanticTokenCandidate candidate)
        {
            var index = Items.Count;
            Items.Add(candidate);
            if (!_indicesByLine.TryGetValue(candidate.Line, out var indices))
            {
                indices = [];
                _indicesByLine[candidate.Line] = indices;
            }

            indices.Add(index);
        }

        public bool TryReplaceExact(
            int line,
            int character,
            int length,
            int tokenType,
            int modifiers,
            int priority,
            bool isSemantic)
        {
            if (!_indicesByLine.TryGetValue(line, out var indices))
            {
                return false;
            }

            foreach (var index in indices)
            {
                var candidate = Items[index];
                if (candidate.Character != character || candidate.Length != length)
                {
                    continue;
                }

                if ((isSemantic && !candidate.IsSemantic) ||
                    (isSemantic == candidate.IsSemantic && priority > candidate.Priority))
                {
                    Items[index] = candidate with
                    {
                        TokenType = tokenType,
                        Modifiers = modifiers,
                        Priority = priority,
                        IsSemantic = isSemantic
                    };
                }

                return true;
            }

            return false;
        }

        public bool Overlaps(int line, int character, int length)
        {
            if (!_indicesByLine.TryGetValue(line, out var indices))
            {
                return false;
            }

            var end = character + length;
            foreach (var index in indices)
            {
                var candidate = Items[index];
                if (character < candidate.Character + candidate.Length &&
                    end > candidate.Character)
                {
                    return true;
                }
            }

            return false;
        }
    }

    public sealed class SnapshotIndex
    {
        private readonly IdeSemanticSnapshot _snapshot;
        private readonly ConcurrentDictionary<int, string> _hoverMarkdownBySymbolId = new();
        private readonly ConcurrentDictionary<string, HashSet<int>> _unusedSymbolIdsByDocumentKey = new(StringComparer.Ordinal);

        public SnapshotIndex(IdeSemanticSnapshot snapshot)
        {
            _snapshot = snapshot;
            SymbolsById = snapshot.Symbols.ToDictionary(static symbol => symbol.SymbolId);
            OccurrencesBySymbolId = snapshot.Occurrences
                .GroupBy(static occurrence => occurrence.SymbolId)
                .ToDictionary(static group => group.Key, static group => group.ToArray());
            OccurrencesByLine = ExpandOccurrenceLines(snapshot.Occurrences)
                .GroupBy(static entry => entry.Line)
                .ToDictionary(
                    static group => group.Key,
                    group => group
                        .Select(static entry => entry.Occurrence)
                        .Distinct()
                        .OrderBy(static occurrence => occurrence.Span.StartCharacter)
                        .ThenBy(static occurrence => occurrence.Span.EndCharacter)
                        .ToArray());
            DefinitionBySymbolId = snapshot.Occurrences
                .Where(static occurrence => string.Equals(occurrence.Role, "definition", StringComparison.Ordinal))
                .GroupBy(static occurrence => occurrence.SymbolId)
                .ToDictionary(
                    static group => group.Key,
                    static group => group
                        .OrderBy(static occurrence => occurrence.Span.Length)
                        .ThenBy(static occurrence => occurrence.Span.Start)
                        .First());
            SemanticTokenOccurrences = snapshot.Occurrences
                .OrderBy(static occurrence => occurrence.Span.Length)
                .ThenBy(static occurrence => OccurrenceRoleRank(occurrence.Role))
                .ThenBy(static occurrence => occurrence.Span.Start)
                .ToArray();
        }

        public IReadOnlyDictionary<int, IdeSymbolEntry> SymbolsById { get; }

        public IReadOnlyDictionary<int, IdeOccurrenceEntry[]> OccurrencesBySymbolId { get; }

        public IReadOnlyDictionary<int, IdeOccurrenceEntry[]> OccurrencesByLine { get; }

        public IReadOnlyDictionary<int, IdeOccurrenceEntry> DefinitionBySymbolId { get; }

        public IdeOccurrenceEntry[] SemanticTokenOccurrences { get; }

        public string GetHoverMarkdown(IdeSymbolEntry symbol) =>
            _hoverMarkdownBySymbolId.GetOrAdd(symbol.SymbolId, _ => BuildHoverMarkdown(symbol, _snapshot));

        public HashSet<int> GetUnusedSymbolIds(string? documentFilePath) =>
            _unusedSymbolIdsByDocumentKey.GetOrAdd(
                CreateDocumentKey(documentFilePath),
                _ => FindUnusedSymbolIds(_snapshot, this, documentFilePath));

        private static IEnumerable<(int Line, IdeOccurrenceEntry Occurrence)> ExpandOccurrenceLines(
            IEnumerable<IdeOccurrenceEntry> occurrences)
        {
            foreach (var occurrence in occurrences)
            {
                if (occurrence.Span.Length <= 0)
                {
                    continue;
                }

                var startLine = Math.Max(0, occurrence.Span.StartLine);
                var endLine = Math.Max(startLine, occurrence.Span.EndLine);
                for (var line = startLine; line <= endLine; line++)
                {
                    yield return (line, occurrence);
                }
            }
        }
    }

    public static List<LspDiagnostic> MapDiagnostics(IdeSemanticSnapshot snapshot)
    {
        var diagnostics = new List<LspDiagnostic>();
        foreach (var entry in snapshot.Diagnostics)
        {
            var diag = new LspDiagnostic
            {
                Severity = MapSeverity(entry.Severity),
                Code = entry.Code,
                Source = "eidosc",
                Message = entry.Message
            };

            if (entry.Span != null)
            {
                diag.Range = MapSpanToRange(entry.Span);
            }

            if (entry.Metadata.Count > 0)
            {
                diag.Data = new Dictionary<string, string>(entry.Metadata, StringComparer.Ordinal);
            }

            if (entry.Related.Count > 0)
            {
                diag.RelatedInformation = [];
                foreach (var related in entry.Related)
                {
                    var relatedPath = related.Span?.FilePath ?? entry.Span?.FilePath ?? snapshot.InputFile;
                    diag.RelatedInformation.Add(new LspDiagnosticRelatedInfo
                    {
                        Location = new LspLocation
                        {
                            Uri = ToFileUri(relatedPath),
                            Range = MapSpanToRange(related.Span)
                        },
                        Message = related.Message
                    });
                }
            }

            diagnostics.Add(diag);
        }

        return diagnostics;
    }

    public static LspPatternCoverageExplainReport MapPatternCoverageExplain(
        IdeSemanticSnapshot snapshot,
        string? documentFilePath,
        LspRange? requestedRange = null)
    {
        var entries = snapshot.Diagnostics
            .Where(diagnostic => diagnostic.Code != null &&
                                 PatternCoverageCodes.Contains(diagnostic.Code) &&
                                 IsDiagnosticInDocument(snapshot, diagnostic, documentFilePath) &&
                                 IsDiagnosticInRequestedRange(diagnostic, requestedRange))
            .Select(diagnostic => new LspPatternCoverageExplainEntry
            {
                Code = diagnostic.Code ?? "",
                Severity = diagnostic.Severity,
                Message = diagnostic.Message,
                Location = CreatePatternCoverageLocation(snapshot, diagnostic, documentFilePath),
                Notes = [.. diagnostic.Notes]
            })
            .ToList();

        return new LspPatternCoverageExplainReport
        {
            InputFile = documentFilePath ?? snapshot.InputFile,
            Entries = entries
        };
    }

    private static LspPatternCoverageExplainLocation? CreatePatternCoverageLocation(
        IdeSemanticSnapshot snapshot,
        IdeDiagnosticEntry diagnostic,
        string? documentFilePath)
    {
        if (diagnostic.Span == null)
        {
            return null;
        }

        return new LspPatternCoverageExplainLocation
        {
            File = diagnostic.Span.FilePath ?? documentFilePath ?? snapshot.InputFile,
            Line = diagnostic.Span.StartLine + 1,
            Column = diagnostic.Span.StartCharacter + 1
        };
    }

    private static bool IsDiagnosticInDocument(
        IdeSemanticSnapshot snapshot,
        IdeDiagnosticEntry diagnostic,
        string? documentFilePath) =>
        diagnostic.Span == null || IsSpanInDocument(snapshot, diagnostic.Span, documentFilePath);

    private static bool IsDiagnosticInRequestedRange(
        IdeDiagnosticEntry diagnostic,
        LspRange? requestedRange)
    {
        if (requestedRange == null)
        {
            return true;
        }

        return diagnostic.Span != null &&
               RangesIntersect(MapSpanToRange(diagnostic.Span), requestedRange);
    }

    public static List<LspCompletionItem> MapCompletions(IdeSemanticSnapshot snapshot, int line, int character)
    {
        return MapCompletions(snapshot, line, character, sourceText: null);
    }

    public static List<LspCompletionItem> MapCompletions(
        IdeSemanticSnapshot snapshot,
        int line,
        int character,
        string? sourceText)
    {
        var items = new List<LspCompletionItem>();
        var prefix = ExtractCompletionPrefix(sourceText, line, character);
        var replacementRange = !string.IsNullOrEmpty(prefix)
            ? CreatePrefixReplacementRange(line, character, prefix.Length)
            : null;

        foreach (var entry in snapshot.Completions)
        {
            if (entry.VisibilitySpan != null &&
                !ContainsPosition(entry.VisibilitySpan, line, character))
            {
                continue;
            }

            if (!MatchesCompletionPrefix(entry.Label, prefix))
            {
                continue;
            }

            items.Add(new LspCompletionItem
            {
                Label = entry.Label,
                Kind = MapCompletionKind(entry.Kind),
                Detail = string.IsNullOrEmpty(entry.Detail) ? null : entry.Detail,
                Documentation = string.IsNullOrEmpty(entry.Documentation) ? null : entry.Documentation,
                SortText = string.IsNullOrEmpty(entry.SortText) ? entry.Label : entry.SortText,
                InsertText = entry.Label,
                TextEdit = replacementRange == null
                    ? null
                    : new LspTextEdit
                    {
                        Range = replacementRange,
                        NewText = entry.Label
                    }
            });
        }

        return items;
    }

    private static bool MatchesCompletionPrefix(string label, string prefix)
    {
        return string.IsNullOrEmpty(prefix) ||
               label.StartsWith(prefix, StringComparison.Ordinal);
    }

    private static LspRange CreatePrefixReplacementRange(int line, int character, int prefixLength)
    {
        return new LspRange
        {
            Start = new LspPosition
            {
                Line = line,
                Character = Math.Max(0, character - prefixLength)
            },
            End = new LspPosition
            {
                Line = line,
                Character = Math.Max(0, character)
            }
        };
    }

    private static string ExtractCompletionPrefix(string? sourceText, int line, int character)
    {
        if (string.IsNullOrEmpty(sourceText) || line < 0 || character < 0)
        {
            return string.Empty;
        }

        var lineText = TryGetLine(sourceText, line);
        if (lineText == null)
        {
            return string.Empty;
        }

        var end = Math.Min(character, lineText.Length);
        var start = end;
        while (start > 0 && IsCompletionPrefixCharacter(lineText[start - 1]))
        {
            start--;
        }

        return lineText[start..end];
    }

    private static string? TryGetLine(string sourceText, int line)
    {
        var currentLine = 0;
        var lineStart = 0;

        for (var i = 0; i < sourceText.Length; i++)
        {
            if (sourceText[i] != '\n')
            {
                continue;
            }

            if (currentLine == line)
            {
                var lineEnd = i > lineStart && sourceText[i - 1] == '\r'
                    ? i - 1
                    : i;
                return sourceText[lineStart..lineEnd];
            }

            currentLine++;
            lineStart = i + 1;
        }

        return currentLine == line ? sourceText[lineStart..] : null;
    }

    private static bool IsCompletionPrefixCharacter(char value)
    {
        return char.IsLetterOrDigit(value) ||
               value is '_' or ':' or '/';
    }

    public static LspHover? MapHover(IdeSemanticSnapshot snapshot, int line, int character)
    {
        return MapHover(snapshot, new SnapshotIndex(snapshot), line, character);
    }

    public static LspHover? MapHover(IdeSemanticSnapshot snapshot, SnapshotIndex index, int line, int character)
    {
        var occurrence = FindBestOccurrenceAt(snapshot, index, line, character);
        if (occurrence == null)
        {
            return null;
        }

        if (!index.SymbolsById.TryGetValue(occurrence.SymbolId, out var symbol))
        {
            return null;
        }

        var value = index.GetHoverMarkdown(symbol);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return new LspHover
        {
            Contents = new LspMarkupContent
            {
                Kind = "markdown",
                Value = value
            },
            Range = MapIdeSpanToRange(occurrence.Span)
        };
    }

    private static readonly string[] SemanticTokenKeywords =
    [
        "func", "fn", "val", "var", "let", "effect", "trait", "instance", "given", "comptime", "type", "effects", "forall", "refl", "by",
        "cases", "induction", "module", "import", "export", "requires", "if", "then", "else", "while",
        "match", "when", "need", "return", "loop", "break", "continue", "as",
        "ref", "mref", "link", "internal", "Self"
    ];
    private static readonly HashSet<string> SemanticTokenKeywordSet = new(SemanticTokenKeywords, StringComparer.Ordinal);

    private static readonly string[] VariableDeclarationKeywords = ["val", "var", "let"];

    private static readonly string[] SemanticTokenOperators =
    [
        ">>>", "<<<", ">>=", "<$>", "<*>", "->", "=>", ":=", "<>", "<=", ">=", "<-", "::",
        ".{", "..", "++", "==", "!=", "&&", "||", "|>", "+=", "-=", "*=", "/=", "%=", "+", "-", "*",
        "/", "%", "=", "!", "<", ">"
    ];
    private static readonly Dictionary<char, string[]> SemanticTokenOperatorsByFirstChar = SemanticTokenOperators
        .GroupBy(static op => op[0])
        .ToDictionary(
            static group => group.Key,
            static group => group.OrderByDescending(static op => op.Length).ToArray());
    private static readonly int SemanticTokenModuleType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Module);
    private static readonly int SemanticTokenTypeType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Type);
    private static readonly int SemanticTokenInterfaceType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Interface);
    private static readonly int SemanticTokenTypeParameterType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.TypeParameter);
    private static readonly int SemanticTokenFunctionType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Function);
    private static readonly int SemanticTokenMethodType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Method);
    private static readonly int SemanticTokenPropertyType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Property);
    private static readonly int SemanticTokenVariableType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Variable);
    private static readonly int SemanticTokenParameterType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Parameter);
    private static readonly int SemanticTokenKeywordType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Keyword);
    private static readonly int SemanticTokenOperatorType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Operator);
    private static readonly int SemanticTokenEffectType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Effect);
    private static readonly int SemanticTokenConstructorType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Constructor);
    private static readonly int SemanticTokenClassType = LspSemanticTokenTypes.All.IndexOf(LspSemanticTokenTypes.Class);
    private static readonly int SemanticTokenDeclarationModifier = 1 << LspSemanticTokenModifiers.All.IndexOf(LspSemanticTokenModifiers.Declaration);
    private static readonly int SemanticTokenBuiltinModifier = 1 << LspSemanticTokenModifiers.All.IndexOf(LspSemanticTokenModifiers.Builtin);
    private static readonly int SemanticTokenMutableModifier = 1 << LspSemanticTokenModifiers.All.IndexOf(LspSemanticTokenModifiers.Mutable);
    private static readonly int SemanticTokenEffectModifier = 1 << LspSemanticTokenModifiers.All.IndexOf(LspSemanticTokenModifiers.Effect);
    private static readonly int SemanticTokenUnusedModifier = 1 << LspSemanticTokenModifiers.All.IndexOf(LspSemanticTokenModifiers.Unused);

    public static LspSemanticTokens MapSemanticTokens(
        IdeSemanticSnapshot snapshot,
        string? documentFilePath,
        string? sourceText = null)
    {
        return MapSemanticTokens(snapshot, new SnapshotIndex(snapshot), documentFilePath, sourceText);
    }

    public static LspSemanticTokens MapSemanticTokens(
        IdeSemanticSnapshot snapshot,
        SnapshotIndex index,
        string? documentFilePath,
        string? sourceText = null)
    {
        var unusedSymbolIds = index.GetUnusedSymbolIds(documentFilePath);
        var candidates = new SemanticTokenCandidateSet();

        foreach (var occurrence in index.SemanticTokenOccurrences)
        {
            if (!index.SymbolsById.TryGetValue(occurrence.SymbolId, out var symbol) ||
                !IsOccurrenceInDocument(snapshot, occurrence, documentFilePath) ||
                occurrence.Span.Length <= 0 ||
                occurrence.Span.StartLine != occurrence.Span.EndLine)
            {
                continue;
            }

            if (!TryMapSemanticToken(symbol, occurrence, unusedSymbolIds, out var tokenType, out var modifiers))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(sourceText) &&
                TryAddQualifiedSemanticTokenSegments(
                    sourceText,
                    occurrence,
                    tokenType,
                    modifiers,
                    candidates))
            {
                continue;
            }

            AddCandidate(
                candidates,
                occurrence.Span.StartLine,
                occurrence.Span.StartCharacter,
                occurrence.Span.Length,
                tokenType,
                modifiers,
                isSemantic: true);
        }

        if (!string.IsNullOrEmpty(sourceText))
        {
            AddLexicalSemanticTokens(sourceText, candidates);
        }

        candidates.Items.Sort(static (left, right) =>
        {
            var lineCompare = left.Line.CompareTo(right.Line);
            return lineCompare != 0
                ? lineCompare
                : left.Character.CompareTo(right.Character);
        });

        var data = new List<int>(candidates.Count * 5);
        var previousLine = 0;
        var previousCharacter = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var token = candidates.Items[i];
            var deltaLine = i == 0 ? token.Line : token.Line - previousLine;
            var deltaStart = deltaLine == 0 ? token.Character - previousCharacter : token.Character;
            data.Add(deltaLine);
            data.Add(deltaStart);
            data.Add(token.Length);
            data.Add(token.TokenType);
            data.Add(token.Modifiers);

            previousLine = token.Line;
            previousCharacter = token.Character;
        }

        return new LspSemanticTokens { Data = data };
    }

    public static List<LspInlayHint> MapInlayHints(
        IdeSemanticSnapshot snapshot,
        string? documentFilePath,
        string? sourceText,
        LspRange? requestedRange = null)
    {
        return MapInlayHints(snapshot, new SnapshotIndex(snapshot), documentFilePath, sourceText, requestedRange);
    }

    public static List<LspInlayHint> MapInlayHints(
        IdeSemanticSnapshot snapshot,
        SnapshotIndex index,
        string? documentFilePath,
        string? sourceText,
        LspRange? requestedRange = null)
    {
        var hints = new List<LspInlayHint>();
        if (string.IsNullOrEmpty(sourceText))
        {
            return hints;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var occurrence in snapshot.Occurrences)
        {
            if (!string.Equals(occurrence.Role, "definition", StringComparison.Ordinal) ||
                !IsVariableDeclarationSource(occurrence.Source) ||
                !IsOccurrenceInDocument(snapshot, occurrence, documentFilePath) ||
                occurrence.Span.StartLine != occurrence.Span.EndLine ||
                !index.SymbolsById.TryGetValue(occurrence.SymbolId, out var symbol) ||
                !CanEmitVariableTypeInlayHint(symbol))
            {
                continue;
            }

            if (!TryFindDeclarationNameLocation(
                    sourceText,
                    occurrence.Span,
                    symbol.Name,
                    out var line,
                    out var nameEndCharacter,
                    out var hasExplicitType) ||
                hasExplicitType)
            {
                continue;
            }

            if (requestedRange != null &&
                !ContainsPosition(requestedRange, line, nameEndCharacter))
            {
                continue;
            }

            var key = $"{line}:{nameEndCharacter}:{symbol.TypeText}";
            if (!seen.Add(key))
            {
                continue;
            }

            hints.Add(new LspInlayHint
            {
                Position = new LspPosition
                {
                    Line = line,
                    Character = nameEndCharacter
                },
                Label = $": {symbol.TypeText}",
                Kind = LspInlayHintKind.Type,
                Tooltip = CliMessages.LspInferredTypeTooltip,
                PaddingRight = true
            });
        }

        return hints;
    }

    public static LspLocation? MapDefinition(IdeSemanticSnapshot snapshot, int line, int character)
    {
        return MapDefinition(snapshot, new SnapshotIndex(snapshot), line, character);
    }

    public static LspLocation? MapDefinition(IdeSemanticSnapshot snapshot, SnapshotIndex index, int line, int character)
    {
        var occurrence = FindBestOccurrenceAt(snapshot, index, line, character);
        if (occurrence == null)
        {
            return null;
        }

        if (string.Equals(occurrence.Role, "definition", StringComparison.Ordinal))
        {
            return new LspLocation
            {
                Uri = ToFileUri(occurrence.Span.FilePath ?? snapshot.InputFile),
                Range = MapIdeSpanToRange(occurrence.Span)
            };
        }

        var defOccurrence = index.DefinitionBySymbolId.GetValueOrDefault(occurrence.SymbolId);
        if (defOccurrence != null)
        {
            return new LspLocation
            {
                Uri = ToFileUri(defOccurrence.Span.FilePath ?? snapshot.InputFile),
                Range = MapIdeSpanToRange(defOccurrence.Span)
            };
        }

        if (!index.SymbolsById.TryGetValue(occurrence.SymbolId, out var symbol))
        {
            return null;
        }

        if (symbol.Span != null)
        {
            return new LspLocation
            {
                Uri = ToFileUri(symbol.Span.FilePath ?? snapshot.InputFile),
                Range = MapIdeSpanToRange(symbol.Span)
            };
        }

        return IsSyntheticModulePath(symbol)
            ? new LspLocation
            {
                Uri = ToFileUri(occurrence.Span.FilePath ?? snapshot.InputFile),
                Range = MapIdeSpanToRange(occurrence.Span)
            }
            : null;
    }

    public static List<LspLocation> MapReferences(IdeSemanticSnapshot snapshot, int line, int character)
    {
        return MapReferences(snapshot, new SnapshotIndex(snapshot), line, character);
    }

    public static List<LspLocation> MapReferences(IdeSemanticSnapshot snapshot, SnapshotIndex index, int line, int character)
    {
        var locations = new List<LspLocation>();
        var occurrence = FindBestOccurrenceAt(snapshot, index, line, character);
        if (occurrence == null)
        {
            return locations;
        }

        if (!index.OccurrencesBySymbolId.TryGetValue(occurrence.SymbolId, out var occurrences))
        {
            return locations;
        }

        foreach (var o in occurrences)
        {
            locations.Add(new LspLocation
            {
                Uri = ToFileUri(o.Span.FilePath ?? snapshot.InputFile),
                Range = MapIdeSpanToRange(o.Span)
            });
        }

        return locations;
    }

    public static List<LspDocumentSymbol> MapDocumentSymbols(IdeSemanticSnapshot snapshot, string? documentFilePath)
    {
        var symbols = new List<LspDocumentSymbol>();
        foreach (var entry in snapshot.Outline)
        {
            if (entry.Span == null ||
                !IsSpanInDocument(snapshot, entry.Span, documentFilePath))
            {
                continue;
            }

            var range = MapIdeSpanToRange(entry.Span);
            symbols.Add(new LspDocumentSymbol
            {
                Name = entry.Name,
                Detail = string.IsNullOrWhiteSpace(entry.Detail) ? null : entry.Detail,
                Kind = MapDocumentSymbolKind(entry.Kind),
                Range = range,
                SelectionRange = range
            });
        }

        return symbols;
    }

    public static List<LspCodeAction> MapCodeActions(
        IdeSemanticSnapshot snapshot,
        string uri,
        string? documentFilePath,
        LspRange requestedRange)
    {
        var actions = new List<LspCodeAction>();
        foreach (var diagnostic in snapshot.Diagnostics)
        {
            var diagnosticInRange = diagnostic.Span != null &&
                                    IsSpanInDocument(snapshot, diagnostic.Span, documentFilePath) &&
                                    RangesIntersect(MapIdeSpanToRange(diagnostic.Span), requestedRange);

            foreach (var suggestion in diagnostic.Suggestions)
            {
                if (string.IsNullOrEmpty(suggestion.Replacement) ||
                    suggestion.Span == null ||
                    !IsSpanInDocument(snapshot, suggestion.Span, documentFilePath))
                {
                    continue;
                }

                var suggestionRange = MapIdeSpanToRange(suggestion.Span);
                if (!diagnosticInRange && !RangesIntersect(suggestionRange, requestedRange))
                {
                    continue;
                }

                actions.Add(new LspCodeAction
                {
                    Title = string.IsNullOrWhiteSpace(suggestion.Message)
                        ? "Apply suggestion"
                        : suggestion.Message,
                    Kind = "quickfix",
                    IsPreferred = string.Equals(suggestion.Kind, "AddImport", StringComparison.OrdinalIgnoreCase),
                    Edit = new LspWorkspaceEdit
                    {
                        Changes = new Dictionary<string, List<LspTextEdit>>
                        {
                            [uri] =
                            [
                                new LspTextEdit
                                {
                                    Range = suggestionRange,
                                    NewText = suggestion.Replacement
                                }
                            ]
                        }
                    }
                });
            }

            // Proof code actions removed during proof migration
        }

        return actions;
    }


    private static int? MapSeverity(string severity) => severity switch
    {
        "error" => LspDiagnosticSeverity.Error,
        "warning" => LspDiagnosticSeverity.Warning,
        "info" => LspDiagnosticSeverity.Information,
        "note" => LspDiagnosticSeverity.Hint,
        "help" => LspDiagnosticSeverity.Hint,
        _ => null
    };

    private static int MapCompletionKind(string kind) => kind switch
    {
        "function" => LspCompletionItemKind.Function,
        "type" => LspCompletionItemKind.Class,
        "constructor" => LspCompletionItemKind.Constructor,
        "trait" => LspCompletionItemKind.Interface,
        "variable" => LspCompletionItemKind.Variable,
        "field" => LspCompletionItemKind.Field,
        "module" => LspCompletionItemKind.Module,
        "keyword" => LspCompletionItemKind.Keyword,
        "typeParameter" => LspCompletionItemKind.TypeParameter,
        _ => LspCompletionItemKind.Text
    };

    private static int MapDocumentSymbolKind(string kind) => kind switch
    {
        "module" => LspSymbolKind.Module,
        "function" => LspSymbolKind.Function,
        "type" => LspSymbolKind.Class,
        "constructor" => LspSymbolKind.Constructor,
        "trait" => LspSymbolKind.Interface,
        "effect" => LspSymbolKind.Interface,
        "field" => LspSymbolKind.Field,
        "variable" => LspSymbolKind.Variable,
        "typeParameter" => LspSymbolKind.TypeParameter,
        _ => LspSymbolKind.Variable
    };

    private static string BuildHoverMarkdown(IdeSymbolEntry symbol, IdeSemanticSnapshot snapshot)
    {
        var header = BuildHoverHeader(symbol);
        var lines = new List<string>();
        if (!string.IsNullOrWhiteSpace(header))
        {
            lines.Add($"```eidos\n{header}\n```");
        }

        if (TryFindOverloadGroup(snapshot, symbol.SymbolId, out var overloadGroup))
        {
            var overloadLines = overloadGroup.Members
                .Select(member => string.IsNullOrWhiteSpace(member.TypeText)
                    ? member.Name
                    : $"{member.Name}: {member.TypeText}")
                .ToList();
            if (overloadLines.Count > 1)
            {
                lines.Add(FormatHoverBlock("overloads", string.Join("\n", overloadLines)));
            }
        }

        var metadata = new List<string>();
        if (!string.IsNullOrWhiteSpace(symbol.Detail) &&
            !string.Equals(symbol.Detail, symbol.Kind, StringComparison.Ordinal) &&
            !IsSyntheticModulePath(symbol))
        {
            metadata.Add(symbol.Detail);
        }

        if (!string.IsNullOrWhiteSpace(symbol.BindingMode) &&
            !string.Equals(symbol.BindingMode, "value", StringComparison.Ordinal))
        {
            metadata.Add(CliMessages.LspHoverBindingMetadata(symbol.BindingMode));
        }

        if (!string.IsNullOrWhiteSpace(symbol.ExternalLibrary))
        {
            metadata.Add(CliMessages.LspHoverFfiLibraryMetadata(symbol.ExternalLibrary));
        }

        if (metadata.Count > 0)
        {
            lines.Add(string.Join("  \n", metadata));
        }

        if (!string.IsNullOrWhiteSpace(symbol.Documentation) &&
            !IsSyntheticModulePath(symbol) &&
            !IsTrivialDocumentation(symbol.Documentation, symbol.Name))
        {
            lines.Add(symbol.Documentation);
        }

        return string.Join("\n\n", lines);
    }

    private static string FormatHoverBlock(string label, string content) =>
        $"{label}:\n```text\n{content}\n```";

    private static bool TryFindOverloadGroup(
        IdeSemanticSnapshot snapshot,
        int symbolId,
        out IdeOverloadGroupEntry overloadGroup)
    {
        overloadGroup = null!;
        foreach (var group in snapshot.OverloadGroups)
        {
            if (group.MemberSymbolIds.Contains(symbolId))
            {
                overloadGroup = group;
                return true;
            }
        }

        return false;
    }

    private static string BuildHoverHeader(IdeSymbolEntry symbol)
    {
        return symbol.Kind switch
        {
            "function" when HasCleanTypeText(symbol) => $"func {symbol.Name}: {symbol.TypeText}",
            "function" => $"func {symbol.Name}",
            "variable" when HasCleanTypeText(symbol) => $"{symbol.Name}: {symbol.TypeText}",
            "field" when HasCleanTypeText(symbol) => $"{symbol.Name}: {symbol.TypeText}",
            "type" => $"type {symbol.Name}",
            "typeAlias" => $"type {symbol.Name}",
            "trait" => $"trait {symbol.Name}",
            "effect" => $"effect {symbol.Name}",
            "constructor" when HasCleanTypeText(symbol) => symbol.TypeText!,
            "constructor" => symbol.Name,
            "typeParameter" => symbol.Name,
            "module" => $"module {symbol.Name}",
            _ when HasCleanTypeText(symbol) => $"{symbol.Name}: {symbol.TypeText}",
            _ => symbol.Name
        };
    }

    private static bool HasCleanTypeText(IdeSymbolEntry symbol) =>
        !string.IsNullOrWhiteSpace(symbol.TypeText) &&
        string.Equals(symbol.TypeConfidence, "TypedClean", StringComparison.Ordinal);

    private static bool CanEmitVariableTypeInlayHint(IdeSymbolEntry symbol)
    {
        return string.Equals(symbol.Kind, "variable", StringComparison.Ordinal) &&
               HasCleanTypeText(symbol) &&
               !IsSymbolDetail(symbol, IdeLocalizedText.ParameterDetail, "parameter") &&
               !IsSymbolDetail(symbol, IdeLocalizedText.PatternBindingDetail, "pattern binding");
    }

    private static bool IsVariableDeclarationSource(string source)
    {
        return string.Equals(source, "ValDecl", StringComparison.Ordinal) ||
               string.Equals(source, "VarDecl", StringComparison.Ordinal) ||
               string.Equals(source, "LetDecl", StringComparison.Ordinal);
    }

    private static bool TryFindDeclarationNameLocation(
        string sourceText,
        IdeSpan span,
        string symbolName,
        out int line,
        out int nameEndCharacter,
        out bool hasExplicitType)
    {
        line = span.StartLine;
        nameEndCharacter = 0;
        hasExplicitType = false;

        var lineText = TryGetLine(sourceText, span.StartLine);
        if (lineText == null)
        {
            return false;
        }

        var searchStart = 0;
        var declarationSearchLimit = Math.Clamp(span.StartCharacter + span.Length, 0, lineText.Length);

        while (searchStart < declarationSearchLimit)
        {
            if (!TryReadDeclarationKeyword(lineText, searchStart, out var afterKeyword))
            {
                searchStart++;
                continue;
            }

            var nameStart = SkipWhitespace(lineText, afterKeyword);
            if (!TryReadIdentifier(lineText, nameStart, out var nameLength))
            {
                searchStart = afterKeyword;
                continue;
            }

            var name = lineText.Substring(nameStart, nameLength);
            if (!string.Equals(name, symbolName, StringComparison.Ordinal))
            {
                searchStart = nameStart + nameLength;
                continue;
            }

            nameEndCharacter = nameStart + nameLength;
            hasExplicitType = NextNonWhitespaceIs(lineText, nameEndCharacter, ':');
            return true;
        }

        return false;
    }

    private static bool TryReadDeclarationKeyword(string lineText, int start, out int afterKeyword)
    {
        afterKeyword = start;
        foreach (var keyword in VariableDeclarationKeywords)
        {
            if (start + keyword.Length > lineText.Length ||
                string.CompareOrdinal(lineText, start, keyword, 0, keyword.Length) != 0 ||
                (start > 0 && IsIdentifierPart(lineText[start - 1])) ||
                (start + keyword.Length < lineText.Length && IsIdentifierPart(lineText[start + keyword.Length])))
            {
                continue;
            }

            afterKeyword = start + keyword.Length;
            return true;
        }

        return false;
    }

    private static int SkipWhitespace(string text, int start)
    {
        var index = start;
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index;
    }

    private static bool IsTrivialDocumentation(string documentation, string name)
    {
        var candidates = new[]
        {
            IdeLocalizedText.FunctionDocumentation(name),
            IdeLocalizedText.TypeDocumentation(name),
            IdeLocalizedText.ConstructorDocumentation(name),
            IdeLocalizedText.TraitDocumentation(name),
            IdeLocalizedText.EffectDocumentation(name),
            IdeLocalizedText.TypeParameterDocumentation(name),
            IdeLocalizedText.ModuleDocumentation(name),
            IdeLocalizedText.FieldDocumentation(name),
            IdeLocalizedText.TraitImplementationDocumentation(name),
            IdeLocalizedText.ValueDocumentation(name)
        };

        return candidates.Any(candidate => string.Equals(candidate, documentation, StringComparison.Ordinal));
    }

    private static bool IsSyntheticModulePath(IdeSymbolEntry? symbol) =>
        symbol != null &&
        string.Equals(symbol.Kind, "module", StringComparison.Ordinal) &&
        IsSymbolDetail(symbol, IdeLocalizedText.ModulePathDetail, "module path") &&
        symbol.Span == null;

    private static bool IsSymbolDetail(IdeSymbolEntry symbol, string localizedDetail, string invariantDetail) =>
        string.Equals(symbol.Detail, localizedDetail, StringComparison.Ordinal) ||
        string.Equals(symbol.Detail, invariantDetail, StringComparison.Ordinal);

    private static IdeOccurrenceEntry? FindBestOccurrenceAt(
        IdeSemanticSnapshot snapshot,
        SnapshotIndex index,
        int line,
        int character)
    {
        return index.OccurrencesByLine.TryGetValue(line, out var sameLineOccurrences)
            ? sameLineOccurrences
            .Where(occurrence => ContainsPosition(occurrence.Span, line, character))
            .OrderByDescending(occurrence => IsOccurrenceInDocument(snapshot, occurrence, snapshot.InputFile))
            .ThenBy(static occurrence => occurrence.Span.Length)
            .ThenBy(static occurrence => OccurrenceRoleRank(occurrence.Role))
            .ThenBy(static occurrence => occurrence.Span.Start)
            .FirstOrDefault()
            : null;
    }

    private static int OccurrenceRoleRank(string role) => role switch
    {
        "reference" => 0,
        "definition" => 1,
        _ => 2
    };

    private static bool ContainsPosition(IdeSpan span, int line, int character)
    {
        if (line < span.StartLine || line > span.EndLine)
        {
            return false;
        }

        if (line == span.StartLine && character < span.StartCharacter)
        {
            return false;
        }

        return line != span.EndLine || character < span.EndCharacter;
    }

    private static bool ContainsPosition(LspRange range, int line, int character)
    {
        if (line < range.Start.Line || line > range.End.Line)
        {
            return false;
        }

        if (line == range.Start.Line && character < range.Start.Character)
        {
            return false;
        }

        return line != range.End.Line || character < range.End.Character;
    }

    private static bool IsOccurrenceInDocument(
        IdeSemanticSnapshot snapshot,
        IdeOccurrenceEntry occurrence,
        string? documentFilePath)
    {
        var occurrencePath = occurrence.Span.FilePath;
        if (string.IsNullOrWhiteSpace(occurrencePath))
        {
            return true;
        }

        var targetPath = string.IsNullOrWhiteSpace(documentFilePath)
            ? snapshot.InputFile
            : documentFilePath;
        return PathsEqual(occurrencePath, targetPath);
    }

    private static bool IsSpanInDocument(
        IdeSemanticSnapshot snapshot,
        IdeSpan span,
        string? documentFilePath)
    {
        var spanPath = span.FilePath;
        if (string.IsNullOrWhiteSpace(spanPath))
        {
            return true;
        }

        var targetPath = string.IsNullOrWhiteSpace(documentFilePath)
            ? snapshot.InputFile
            : documentFilePath;
        return PathsEqual(spanPath, targetPath);
    }

    private static bool RangesIntersect(LspRange left, LspRange right)
    {
        return ComparePositions(left.End.Line, left.End.Character, right.Start.Line, right.Start.Character) > 0 &&
               ComparePositions(right.End.Line, right.End.Character, left.Start.Line, left.Start.Character) > 0;
    }

    private static int ComparePositions(int leftLine, int leftCharacter, int rightLine, int rightCharacter)
    {
        var lineCompare = leftLine.CompareTo(rightLine);
        return lineCompare != 0
            ? lineCompare
            : leftCharacter.CompareTo(rightCharacter);
    }

    private static bool TryMapSemanticToken(
        IdeSymbolEntry symbol,
        IdeOccurrenceEntry occurrence,
        HashSet<int> unusedSymbolIds,
        out int tokenType,
        out int modifiers)
    {
        tokenType = symbol.Kind switch
        {
            "module" => SemanticTokenModuleType,
            "type" or "typeAlias" => SemanticTokenTypeType,
            "trait" => SemanticTokenInterfaceType,
            "effect" => SemanticTokenEffectType,
            "constructor" => SemanticTokenConstructorType,
            "field" => SemanticTokenPropertyType,
            "function" => SemanticTokenFunctionType,
            "variable" when IsSymbolDetail(symbol, IdeLocalizedText.ParameterDetail, "parameter") => SemanticTokenParameterType,
            "variable" when IsSymbolDetail(symbol, IdeLocalizedText.PatternBindingDetail, "pattern binding") &&
                            string.Equals(occurrence.Role, "definition", StringComparison.Ordinal) => SemanticTokenParameterType,
            "variable" => SemanticTokenVariableType,
            "typeParameter" => SemanticTokenTypeParameterType,
            _ => -1
        };

        if (tokenType < 0)
        {
            modifiers = 0;
            return false;
        }

        modifiers = 0;
        if (string.Equals(occurrence.Role, "definition", StringComparison.Ordinal))
        {
            modifiers |= SemanticTokenDeclarationModifier;
        }

        if (symbol.IsBuiltin)
        {
            modifiers |= SemanticTokenBuiltinModifier;
        }

        if (IsSymbolDetail(symbol, IdeLocalizedText.MutableVariableDetail, "mutable variable"))
        {
            modifiers |= SemanticTokenMutableModifier;
        }

        if (string.Equals(occurrence.Source, "EffectfulType", StringComparison.Ordinal) ||
            string.Equals(occurrence.Source, "EffectRequirementNode", StringComparison.Ordinal))
        {
            modifiers |= SemanticTokenEffectModifier;
        }

        if (string.Equals(occurrence.Role, "definition", StringComparison.Ordinal) &&
            unusedSymbolIds.Contains(symbol.SymbolId))
        {
            modifiers |= SemanticTokenUnusedModifier;
        }

        return true;
    }

    private static HashSet<int> FindUnusedSymbolIds(
        IdeSemanticSnapshot snapshot,
        SnapshotIndex index,
        string? documentFilePath)
    {
        var unusedCandidates = new HashSet<int>();
        foreach (var symbol in index.SymbolsById.Values)
        {
            if (string.Equals(symbol.Kind, "variable", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(symbol.Name) &&
                !string.Equals(symbol.Name, "_", StringComparison.Ordinal))
            {
                unusedCandidates.Add(symbol.SymbolId);
            }
        }

        foreach (var occurrence in snapshot.Occurrences)
        {
            if (!unusedCandidates.Contains(occurrence.SymbolId) ||
                !IsOccurrenceInDocument(snapshot, occurrence, documentFilePath))
            {
                continue;
            }

            if (string.Equals(occurrence.Role, "reference", StringComparison.Ordinal))
            {
                unusedCandidates.Remove(occurrence.SymbolId);
            }
        }

        return unusedCandidates;
    }

    private static bool PathsEqual(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.GetFullPath(left),
                Path.GetFullPath(right),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return string.Equals(left, right, StringComparison.Ordinal);
        }
    }

    private static void AddLexicalSemanticTokens(
        string sourceText,
        SemanticTokenCandidateSet candidates)
    {
        var line = 0;
        var character = 0;
        for (var i = 0; i < sourceText.Length;)
        {
            var current = sourceText[i];
            var next = i + 1 < sourceText.Length ? sourceText[i + 1] : '\0';

            if (current == '\r' || current == '\n')
            {
                AdvanceNewLine(sourceText, ref i, ref line, ref character);
                continue;
            }

            if (current == '/' && next == '/')
            {
                i += 2;
                character += 2;
                while (i < sourceText.Length && sourceText[i] != '\r' && sourceText[i] != '\n')
                {
                    i++;
                    character++;
                }
                continue;
            }

            if (current == '/' && next == '*')
            {
                i += 2;
                character += 2;
                while (i < sourceText.Length)
                {
                    if (sourceText[i] == '\r' || sourceText[i] == '\n')
                    {
                        AdvanceNewLine(sourceText, ref i, ref line, ref character);
                        continue;
                    }

                    if (sourceText[i] == '*' && i + 1 < sourceText.Length && sourceText[i + 1] == '/')
                    {
                        i += 2;
                        character += 2;
                        break;
                    }

                    i++;
                    character++;
                }
                continue;
            }

            if ((current == 'r' || current == 'f') && next == '"')
            {
                i++;
                character++;
                SkipQuoted(sourceText, ref i, ref line, ref character, '"');
                continue;
            }

            if (current == '"' || current == '\'')
            {
                SkipQuoted(sourceText, ref i, ref line, ref character, current);
                continue;
            }

            if (IsIdentifierStart(current))
            {
                if (TryAddQualifiedModulePrefixTokens(
                        sourceText,
                        i,
                        line,
                        character,
                        candidates,
                        out var consumedLength))
                {
                    i += consumedLength;
                    character += consumedLength;
                    continue;
                }

                var start = i;
                var startCharacter = character;
                i++;
                character++;
                while (i < sourceText.Length && IsIdentifierPart(sourceText[i]))
                {
                    i++;
                    character++;
                }

                if (start > 0 &&
                    sourceText[start - 1] == '.' &&
                    IsLowercaseIdentifier(sourceText, start) &&
                    NextNonWhitespaceIs(sourceText, i, '('))
                {
                    AddCandidate(
                        candidates,
                        line,
                        startCharacter,
                        i - start,
                        SemanticTokenFunctionType,
                        modifiers: 0);
                }

                if (i + 1 < sourceText.Length &&
                    sourceText[i] == ':' &&
                    sourceText[i + 1] == ':')
                {
                    AddCandidate(
                        candidates,
                        line,
                        startCharacter,
                        i - start,
                        SemanticTokenModuleType,
                        modifiers: 0);
                }

                if (IsSemanticTokenKeyword(sourceText, start, i - start))
                {
                    AddCandidate(
                        candidates,
                        line,
                        startCharacter,
                        i - start,
                        SemanticTokenKeywordType,
                        modifiers: 0);
                }
                continue;
            }

            if (TryReadSemanticTokenOperator(sourceText, i, current, out var operatorLength))
            {
                AddCandidate(
                    candidates,
                    line,
                    character,
                    operatorLength,
                    SemanticTokenOperatorType,
                    modifiers: 0);
                i += operatorLength;
                character += operatorLength;
                continue;
            }

            i++;
            character++;
        }
    }

    private static bool TryAddQualifiedSemanticTokenSegments(
        string sourceText,
        IdeOccurrenceEntry occurrence,
        int leafTokenType,
        int leafModifiers,
        SemanticTokenCandidateSet candidates)
    {
        var span = occurrence.Span;
        if (span.Start < 0 ||
            span.Length <= 0 ||
            span.Start + span.Length > sourceText.Length ||
            span.StartLine != span.EndLine)
        {
            return false;
        }

        var spanEnd = span.Start + span.Length;
        if (!ContainsQualifiedSeparator(sourceText, span.Start, spanEnd))
        {
            return false;
        }

        var pathEnd = FindQualifiedPathEnd(sourceText, span.Start, spanEnd);
        if (!TryReadQualifiedPathSegments(
                sourceText,
                span.Start,
                pathEnd,
                out var lastOffset,
                out var lastLength,
                candidates,
                span.StartLine,
                span.StartCharacter))
        {
            return false;
        }

        AddCandidate(
            candidates,
            span.StartLine,
            span.StartCharacter + lastOffset,
            lastLength,
            leafTokenType,
            leafModifiers,
            isSemantic: true);
        return true;
    }

    private static bool ContainsQualifiedSeparator(string text, int start, int end)
    {
        for (var index = start; index + 1 < end; index++)
        {
            if (text[index] == ':' && text[index + 1] == ':')
            {
                return true;
            }
        }

        return false;
    }

    private static int FindQualifiedPathEnd(string text, int start, int end)
    {
        for (var index = start; index < end; index++)
        {
            if (text[index] is '[' or '(' or ' ' or '\t' or '\r' or '\n')
            {
                return index;
            }
        }

        return end;
    }

    private static bool TryReadQualifiedPathSegments(
        string text,
        int start,
        int end,
        out int lastOffset,
        out int lastLength,
        SemanticTokenCandidateSet candidates,
        int line,
        int startCharacter)
    {
        lastOffset = 0;
        lastLength = 0;
        var segmentCount = 0;

        for (var index = start; index < end;)
        {
            if (!IsIdentifierStart(text[index]))
            {
                index++;
                continue;
            }

            var segmentStart = index;
            index++;
            while (index < end && IsIdentifierPart(text[index]))
            {
                index++;
            }

            var offset = segmentStart - start;
            var length = index - segmentStart;
            if (segmentCount > 0)
            {
                AddCandidate(
                    candidates,
                    line,
                    startCharacter + lastOffset,
                    lastLength,
                    SemanticTokenModuleType,
                    modifiers: 0,
                    isSemantic: true);
            }

            lastOffset = offset;
            lastLength = length;
            segmentCount++;
        }

        return segmentCount >= 2;
    }

    private static bool TryAddQualifiedModulePrefixTokens(
        string sourceText,
        int start,
        int line,
        int character,
        SemanticTokenCandidateSet candidates,
        out int consumedLength)
    {
        consumedLength = 0;
        var segmentRanges = new List<(int Character, int Length)>();
        var cursor = start;
        var cursorCharacter = character;

        if (!TryReadIdentifier(sourceText, cursor, out var firstLength))
        {
            return false;
        }

        segmentRanges.Add((cursorCharacter, firstLength));
        cursor += firstLength;
        cursorCharacter += firstLength;

        var sawQualifiedSegment = false;
        while (TryReadQualifiedModuleSeparator(sourceText, cursor, out var separatorLength) &&
               TryReadIdentifier(sourceText, cursor + separatorLength, out var segmentLength))
        {
            sawQualifiedSegment = true;
            cursor += separatorLength;
            cursorCharacter += separatorLength;
            segmentRanges.Add((cursorCharacter, segmentLength));
            cursor += segmentLength;
            cursorCharacter += segmentLength;
        }

        if (!sawQualifiedSegment ||
            cursor + 1 >= sourceText.Length ||
            sourceText[cursor] != ':' ||
            sourceText[cursor + 1] != ':')
        {
            return false;
        }

        foreach (var (segmentCharacter, segmentLength) in segmentRanges)
        {
            AddCandidate(
                candidates,
                line,
                segmentCharacter,
                segmentLength,
                SemanticTokenModuleType,
                modifiers: 0,
                isSemantic: false);
        }

        consumedLength = cursor - start;
        return true;
    }

    private static bool TryReadQualifiedModuleSeparator(string sourceText, int start, out int length)
    {
        length = 0;
        if (start >= sourceText.Length)
        {
            return false;
        }

        if (sourceText[start] == '/')
        {
            length = 1;
            return true;
        }

        if (start + 1 < sourceText.Length &&
            sourceText[start] == ':' &&
            sourceText[start + 1] == ':')
        {
            length = 2;
            return true;
        }

        return false;
    }

    private static bool TryReadIdentifier(string sourceText, int start, out int length)
    {
        length = 0;
        if (start >= sourceText.Length || !IsIdentifierStart(sourceText[start]))
        {
            return false;
        }

        var cursor = start + 1;
        while (cursor < sourceText.Length && IsIdentifierPart(sourceText[cursor]))
        {
            cursor++;
        }

        length = cursor - start;
        return true;
    }

    private static bool TryReadSemanticTokenOperator(
        string sourceText,
        int start,
        char firstCharacter,
        out int length)
    {
        length = 0;
        if (!SemanticTokenOperatorsByFirstChar.TryGetValue(firstCharacter, out var operators))
        {
            return false;
        }

        foreach (var op in operators)
        {
            if (start + op.Length <= sourceText.Length &&
                string.CompareOrdinal(sourceText, start, op, 0, op.Length) == 0)
            {
                length = op.Length;
                return true;
            }
        }

        return false;
    }

    private static void AddCandidate(
        SemanticTokenCandidateSet candidates,
        int line,
        int character,
        int length,
        int tokenType,
        int modifiers,
        bool isSemantic = false)
    {
        if (tokenType < 0 || length <= 0)
        {
            return;
        }

        var priority = GetSemanticTokenPriority(tokenType);
        if (candidates.TryReplaceExact(line, character, length, tokenType, modifiers, priority, isSemantic))
        {
            return;
        }

        if (candidates.Overlaps(line, character, length))
        {
            return;
        }

        candidates.Add(new SemanticTokenCandidate(line, character, length, tokenType, modifiers, priority, isSemantic));
    }

    private static int GetSemanticTokenPriority(int tokenType)
    {
        if (tokenType == SemanticTokenModuleType)
        {
            return 100;
        }

        if (tokenType == SemanticTokenConstructorType)
        {
            return 95;
        }

        if (tokenType == SemanticTokenFunctionType ||
            tokenType == SemanticTokenMethodType)
        {
            return 90;
        }

        if (tokenType == SemanticTokenPropertyType)
        {
            return 85;
        }

        if (tokenType == SemanticTokenParameterType ||
            tokenType == SemanticTokenVariableType)
        {
            return 80;
        }

        if (tokenType == SemanticTokenTypeParameterType)
        {
            return 75;
        }

        if (tokenType == SemanticTokenTypeType ||
            tokenType == SemanticTokenClassType ||
            tokenType == SemanticTokenInterfaceType ||
            tokenType == SemanticTokenEffectType)
        {
            return 70;
        }

        if (tokenType == SemanticTokenKeywordType ||
            tokenType == SemanticTokenOperatorType)
        {
            return 10;
        }

        return 0;
    }

    private static bool IsLowercaseIdentifier(string value) =>
        value.Length > 0 && (char.IsLower(value[0]) || value[0] == '_');

    private static bool IsLowercaseIdentifier(string value, int start) =>
        start >= 0 &&
        start < value.Length &&
        (char.IsLower(value[start]) || value[start] == '_');

    private static bool IsSemanticTokenKeyword(string text, int start, int length)
    {
        if (start < 0 || length <= 0 || start + length > text.Length)
        {
            return false;
        }

        foreach (var keyword in SemanticTokenKeywords)
        {
            if (keyword.Length == length &&
                string.CompareOrdinal(text, start, keyword, 0, length) == 0)
            {
                return true;
            }
        }

        return false;
    }

    private static string CreateDocumentKey(string? documentFilePath) =>
        string.IsNullOrWhiteSpace(documentFilePath)
            ? string.Empty
            : Path.GetFullPath(documentFilePath);

    private static bool NextNonWhitespaceIs(string text, int index, char expected)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index]))
        {
            index++;
        }

        return index < text.Length && text[index] == expected;
    }

    private static void AdvanceNewLine(string text, ref int index, ref int line, ref int character)
    {
        if (text[index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n')
        {
            index += 2;
        }
        else
        {
            index++;
        }

        line++;
        character = 0;
    }

    private static void SkipQuoted(string text, ref int index, ref int line, ref int character, char quote)
    {
        index++;
        character++;
        while (index < text.Length)
        {
            var current = text[index];
            if (current == '\r' || current == '\n')
            {
                AdvanceNewLine(text, ref index, ref line, ref character);
                continue;
            }

            if (current == '\\' && index + 1 < text.Length)
            {
                index += 2;
                character += 2;
                continue;
            }

            index++;
            character++;
            if (current == quote)
            {
                break;
            }
        }
    }

    private static bool IsIdentifierStart(char value)
    {
        return char.IsLetter(value) || value == '_';
    }

    private static bool IsIdentifierPart(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }

    private static LspRange MapSpanToRange(IdeSpan? span)
    {
        if (span == null)
            return new LspRange();

        return new LspRange
        {
            Start = new LspPosition { Line = span.StartLine, Character = span.StartCharacter },
            End = new LspPosition { Line = span.EndLine, Character = span.EndCharacter }
        };
    }

    private static LspRange MapIdeSpanToRange(IdeSpan span)
    {
        return new LspRange
        {
            Start = new LspPosition { Line = span.StartLine, Character = span.StartCharacter },
            End = new LspPosition { Line = span.EndLine, Character = span.EndCharacter }
        };
    }

    private static string ToFileUri(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        if (Uri.TryCreate(filePath, UriKind.Absolute, out var absoluteUri) && absoluteUri.IsAbsoluteUri)
        {
            return absoluteUri.IsFile ? absoluteUri.AbsoluteUri : filePath;
        }

        var normalizedPath = Path.GetFullPath(filePath);
        return new Uri(normalizedPath).AbsoluteUri;
    }
}
