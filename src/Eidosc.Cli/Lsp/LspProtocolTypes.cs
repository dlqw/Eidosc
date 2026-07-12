using System.Text.Json.Serialization;

namespace Eidosc.Cli.Lsp;

#region LSP Protocol Types

public sealed class LspPosition
{
    public int Line { get; set; }
    public int Character { get; set; }
}

public sealed class LspRange
{
    public LspPosition Start { get; set; } = new();
    public LspPosition End { get; set; } = new();
}

public sealed class LspLocation
{
    public string Uri { get; set; } = "";
    public LspRange Range { get; set; } = new();
}

public sealed class LspTextEdit
{
    public LspRange Range { get; set; } = new();
    public string NewText { get; set; } = "";
}

public sealed class LspWorkspaceEdit
{
    public Dictionary<string, List<LspTextEdit>> Changes { get; set; } = [];
}

public sealed class LspCodeAction
{
    public string Title { get; set; } = "";
    public string Kind { get; set; } = "quickfix";
    public LspWorkspaceEdit? Edit { get; set; }
    public bool? IsPreferred { get; set; }
}

public sealed class LspDiagnostic
{
    public LspRange Range { get; set; } = new();
    public int? Severity { get; set; }
    public string? Code { get; set; }
    public string? Source { get; set; }
    public string Message { get; set; } = "";
    public List<LspDiagnosticRelatedInfo>? RelatedInformation { get; set; }
    public Dictionary<string, string>? Data { get; set; }
}

public sealed class LspDiagnosticRelatedInfo
{
    public LspLocation Location { get; set; } = new();
    public string Message { get; set; } = "";
}

public sealed class LspCompletionItem
{
    public string Label { get; set; } = "";
    public int? Kind { get; set; }
    public string? Detail { get; set; }
    public string? Documentation { get; set; }
    public string? SortText { get; set; }
    public string? InsertText { get; set; }
    public LspTextEdit? TextEdit { get; set; }
}

public sealed class LspHover
{
    public object? Contents { get; set; }
    public LspRange? Range { get; set; }
}

public sealed class LspMarkupContent
{
    public string Kind { get; set; } = "markdown";
    public string Value { get; set; } = "";
}

public sealed class LspSemanticTokens
{
    public List<int> Data { get; set; } = [];
}

public sealed class LspInlayHint
{
    public LspPosition Position { get; set; } = new();
    public string Label { get; set; } = "";
    public int? Kind { get; set; }
    public string? Tooltip { get; set; }
    public bool? PaddingLeft { get; set; }
    public bool? PaddingRight { get; set; }
}

public sealed class LspDocumentSymbol
{
    public string Name { get; set; } = "";
    public string? Detail { get; set; }
    public int Kind { get; set; }
    public LspRange Range { get; set; } = new();
    public LspRange SelectionRange { get; set; } = new();
    public List<LspDocumentSymbol>? Children { get; set; }
}

public sealed class LspSemanticTokensOptions
{
    [JsonPropertyName("legend")]
    public LspSemanticTokensLegend Legend { get; set; } = new();

    [JsonPropertyName("full")]
    public bool Full { get; set; } = true;
}

public sealed class LspSemanticTokensLegend
{
    [JsonPropertyName("tokenTypes")]
    public List<string> TokenTypes { get; set; } = [];

    [JsonPropertyName("tokenModifiers")]
    public List<string> TokenModifiers { get; set; } = [];
}

#endregion

#region Server Capabilities

public sealed class LspServerCapabilities
{
    [JsonPropertyName("textDocumentSync")]
    public int TextDocumentSync { get; set; } = 1; // Full sync

    [JsonPropertyName("completionProvider")]
    public LspCompletionOptions? CompletionProvider { get; set; } = new();

    [JsonPropertyName("hoverProvider")]
    public bool HoverProvider { get; set; } = true;

    [JsonPropertyName("definitionProvider")]
    public bool DefinitionProvider { get; set; } = true;

    [JsonPropertyName("declarationProvider")]
    public bool DeclarationProvider { get; set; } = true;

    [JsonPropertyName("typeDefinitionProvider")]
    public bool TypeDefinitionProvider { get; set; } = true;

    [JsonPropertyName("implementationProvider")]
    public bool ImplementationProvider { get; set; } = true;

    [JsonPropertyName("referencesProvider")]
    public bool ReferencesProvider { get; set; } = true;

    [JsonPropertyName("documentSymbolProvider")]
    public bool DocumentSymbolProvider { get; set; } = true;

    [JsonPropertyName("codeActionProvider")]
    public bool CodeActionProvider { get; set; } = true;

    [JsonPropertyName("documentFormattingProvider")]
    public bool DocumentFormattingProvider { get; set; } = true;

    [JsonPropertyName("inlayHintProvider")]
    public bool InlayHintProvider { get; set; } = true;

    [JsonPropertyName("semanticTokensProvider")]
    public LspSemanticTokensOptions SemanticTokensProvider { get; set; } = new()
    {
        Legend = new LspSemanticTokensLegend
        {
            TokenTypes = LspSemanticTokenTypes.All,
            TokenModifiers = LspSemanticTokenModifiers.All
        },
        Full = true
    };

    [JsonPropertyName("experimental")]
    public Dictionary<string, bool> Experimental { get; set; } = new()
    {
        ["eidosProofStates"] = true,
        ["eidosProofSearch"] = true,
        ["eidosPatternCoverageExplain"] = true
    };
}

public sealed class LspProofSearchEntry
{
    public string ProofName { get; set; } = "";
    public string CheckStatus { get; set; } = "not-run";
    public string Goal { get; set; } = "";
    public string SearchReport { get; set; } = "";
}

public sealed class LspPatternCoverageExplainReport
{
    public string InputFile { get; set; } = "";
    public List<LspPatternCoverageExplainEntry> Entries { get; set; } = [];
}

public sealed class LspPatternCoverageExplainEntry
{
    public string Code { get; set; } = "";
    public string Severity { get; set; } = "";
    public string Message { get; set; } = "";
    public LspPatternCoverageExplainLocation? Location { get; set; }
    public List<string> Notes { get; set; } = [];
}

public sealed class LspPatternCoverageExplainLocation
{
    public string File { get; set; } = "";
    public int Line { get; set; }
    public int Column { get; set; }
}

public sealed class LspCompletionOptions
{
    [JsonPropertyName("triggerCharacters")]
    public List<string> TriggerCharacters { get; set; } = [".", ":"];
}

#endregion

#region LSP Diagnostic Severities

public static class LspDiagnosticSeverity
{
    public const int Error = 1;
    public const int Warning = 2;
    public const int Information = 3;
    public const int Hint = 4;
}

public static class LspCompletionItemKind
{
    public const int Text = 1;
    public const int Method = 2;
    public const int Function = 3;
    public const int Constructor = 4;
    public const int Field = 5;
    public const int Variable = 6;
    public const int Class = 7;
    public const int Interface = 8;
    public const int Module = 9;
    public const int Property = 10;
    public const int Keyword = 14;
    public const int TypeParameter = 25;
}

public static class LspInlayHintKind
{
    public const int Type = 1;
    public const int Parameter = 2;
}

public static class LspSymbolKind
{
    public const int File = 1;
    public const int Module = 2;
    public const int Namespace = 3;
    public const int Package = 4;
    public const int Class = 5;
    public const int Method = 6;
    public const int Property = 7;
    public const int Field = 8;
    public const int Constructor = 9;
    public const int Enum = 10;
    public const int Interface = 11;
    public const int Function = 12;
    public const int Variable = 13;
    public const int Constant = 14;
    public const int Struct = 23;
    public const int TypeParameter = 26;
}

public static class LspSemanticTokenTypes
{
    public const string Module = "module";
    public const string Type = "type";
    public const string Class = "class";
    public const string Interface = "interface";
    public const string TypeParameter = "typeParameter";
    public const string Function = "function";
    public const string Method = "method";
    public const string Property = "property";
    public const string Variable = "variable";
    public const string Parameter = "parameter";
    public const string Keyword = "keyword";
    public const string Operator = "operator";
    public const string Effect = "effectTag";
    public const string Constructor = "constructor";

    public static readonly List<string> All =
    [
        Module,
        Type,
        Class,
        Interface,
        TypeParameter,
        Function,
        Method,
        Property,
        Variable,
        Parameter,
        Keyword,
        Operator,
        Effect,
        Constructor
    ];
}

public static class LspSemanticTokenModifiers
{
    public const string Declaration = "declaration";
    public const string Builtin = "builtin";
    public const string Mutable = "mutable";
    public const string Effect = "effect";
    public const string Unused = "unused";

    public static readonly List<string> All =
    [
        Declaration,
        Builtin,
        Mutable,
        Effect,
        Unused
    ];
}

#endregion
