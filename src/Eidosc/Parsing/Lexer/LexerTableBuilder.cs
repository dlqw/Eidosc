namespace Eidosc.Parsing.Lexer;

/// <summary>
/// Static hardcoded lexer table builder. Creates all lexer rules, terminals,
/// and lookup tables directly — no grammar definition or code generation needed.
/// </summary>
public static class LexerTableBuilder
{
    /// <summary>
    /// Build the ScannerData and GrammarData (terminals only) needed for lexing.
    /// Builds ScannerData and GrammarData directly from hardcoded rules.
    /// </summary>
    public static (GrammarData grammarData, ScannerData scannerData) Build()
    {
        int terminalId = 0;
        var terminals = new List<Terminal>();

        // Intermediate storage: keyed by (priority, char) for ScannerDataBuilder algorithm
        // We track per-rule metadata for grouping
        var ruleEntries = new List<RuleEntry>();

        // ──────────────────────────────────────────────────────────────
        //  Helper: register a rule that has a Terminal and gets looked up
        //  via the first-character table.
        // ──────────────────────────────────────────────────────────────
        void AddRule(LexerRule rule, string debugName, TerminalFlag flags, int priority, bool isLexerOnly = false, bool isNoPrefix = false)
        {
            var id = terminalId++;
            var terminal = new Terminal(id, debugName, flags);
            terminals.Add(terminal);
            rule.SetTerminalId(id);
            ruleEntries.Add(new RuleEntry(rule, terminal, priority, isLexerOnly, isNoPrefix));
        }

        // ──────────────────────────────────────────────────────────────
        //  Identifiers (NoPrefix — go into fallback table)
        // ──────────────────────────────────────────────────────────────
        AddRule(
            new UnicodeIdentifierRule(0, SyntaxKind.Identifier),
            "identifier", TerminalFlag.None, priority: 0, isNoPrefix: true);

        AddRule(
            new SymbolOperatorRule(0, SyntaxKind.OperatorIdentifier),
            WellKnownStrings.Terminals.OperatorIdentifier, TerminalFlag.None, priority: 0);

        // ──────────────────────────────────────────────────────────────
        //  Comments (LexerOnly — not emitted as tokens)
        // ──────────────────────────────────────────────────────────────
        var lineComment = new CommentMatchRule("//", []);
        AddRule(lineComment, "lineComment", TerminalFlag.None, priority: 0, isLexerOnly: true);

        var blockComment = new CommentMatchRule("/*", ["*/"]);
        AddRule(blockComment, "blockComment", TerminalFlag.None, priority: 0, isLexerOnly: true);

        // ──────────────────────────────────────────────────────────────
        //  Keywords (priority 900 — matched before identifiers)
        // ──────────────────────────────────────────────────────────────
        const int keywordPriority = 900;
        const TerminalFlag keywordFlags = TerminalFlag.IsReservedWord | TerminalFlag.IsKeyword;

        AddKeyword("module");   // KwModule
        AddKeyword("import");   // KwImport
        AddKeyword("export");   // KwExport
        AddKeyword("let");      // KwLet
        AddKeyword("func");     // KwFunc
        AddKeyword("effect");   // KwEffect
        AddKeyword("effects");  // KwEffects
        AddKeyword("type");     // KwType
        AddKeyword("trait");    // KwTrait
        AddKeyword("fn");       // KwFn
        AddKeyword("if");       // KwIf
        AddKeyword("then");     // KwThen
        AddKeyword("else");     // KwElse
        AddKeyword("decide");   // KwDecide
        AddKeyword("while");    // KwWhile
        AddKeyword("loop");     // KwLoop
        AddKeyword("match");    // KwMatch
        AddKeyword("when");     // KwWhen
        AddKeyword("return");   // KwReturn
        AddKeyword("need");     // KwNeed
        AddKeyword("requires"); // KwRequires
        AddKeyword("break");    // KwBreak
        AddKeyword("continue"); // KwContinue
        AddKeyword("as");       // KwAs
        AddKeyword("ref");      // KwRef
        AddKeyword("mut");      // KwMut
        AddKeyword("mref");     // KwMref
        AddKeyword("do");       // KwDo
        AddKeyword("unreachable"); // KwUnreachable
        AddKeyword("quote");    // KwQuote

        // ──────────────────────────────────────────────────────────────
        //  Operators (priority 0, no special flags)
        // ──────────────────────────────────────────────────────────────
        AddOp("->");    // OpArrow
        AddOp("=>");    // OpFatArrow
        AddOp(":=");    // OpAssign
        AddOp("=");     // OpBind
        AddOp("::");    // OpColonColon
        AddOp("|");     // OpPipe
        AddOp("&");     // OpPatternAnd
        AddOp("+");     // OpPlus
        AddOp("++");    // OpConcat
        AddOp("-");     // OpMinus
        AddOp("*");     // OpStar
        AddOp("/");     // OpSlash
        AddOp("%");     // OpPercent
        AddOp("==");    // OpEq
        AddOp("!=");    // OpNe
        AddOp("<");     // OpLt
        AddOp(">");     // OpGt
        AddOp("<=");    // OpLe
        AddOp(">=");    // OpGe
        AddOp("..");    // OpRange
        AddOp("&&");    // OpAnd
        AddOp("||");    // OpOr
        AddOp("<-");    // OpLeftArrow
        AddOp("!");     // OpNot
        AddOp("|>");    // OpPipeForward
        AddOp(">>=");   // OpBindArrow
        AddOp("??");    // OpCoalesce
        AddOp(">>>");   // OpComposeR
        AddOp("<<<");   // OpComposeL
        AddOp("<$>");   // OpFmap
        AddOp("<*>");   // OpAp
        AddOp("<>");    // OpAppend
        AddOp("+:");    // OpPrepend
        AddOp(":+");    // OpAppendLast
        AddOp("?");     // OpQuestion

        // ──────────────────────────────────────────────────────────────
        //  Punctuation (priority 0, IsPunctuation flag)
        // ──────────────────────────────────────────────────────────────
        AddPunct("`", "backtick");    // PtBacktick
        AddPunct("(", "lparen");      // PtLParen
        AddPunct(")", "rparen");      // PtRParen
        AddPunct("[", "lbrack");      // PtLBrack
        AddPunct("]", "rbrack");      // PtRBrack
        AddPunct("{", "lbrace");      // PtLBrace
        AddPunct("}", "rbrace");      // PtRBrace
        AddPunct(",", "comma");       // PtComma
        AddPunct(";", "semi");        // PtSemi
        AddPunct(".", "dot");         // PtDot
        AddPunct("_", "underscore");  // PtUnderscore
        AddPunct(":", "colon");       // PtColon
        AddPunct("@", "at");          // PtAt

        // ──────────────────────────────────────────────────────────────
        //  Literals
        // ──────────────────────────────────────────────────────────────
        // Number literal
        AddRule(
            new NumberLiteralRule(0, new NumberConfig
            {
                EnableBinary = true,
                EnableHex = true,
                AllowUnderscore = true,
                AllowLeadingDot = false,
                AllowLeadingSign = true,
                CaseSensitive = true,
                Suffixes =
                [
                    new NumberSuffix('u', TypeCode.UInt32),
                    new NumberSuffix('U', TypeCode.UInt64),
                    new NumberSuffix('l', TypeCode.Int64),
                    new NumberSuffix('s', TypeCode.Int16),
                    new NumberSuffix('f', TypeCode.Single),
                    new NumberSuffix('d', TypeCode.Double),
                    new NumberSuffix('b', TypeCode.Byte)
                ]
            }, SyntaxKind.NumberLiteral),
            "numberLiteral", TerminalFlag.None, priority: 0);

        // String literal
        AddRule(
            new StringLiteralRule(0,
            [
                new StringStyle
                {
                    Prefix = "\"",
                    Suffix = "\"",
                    AllowEscape = true,
                    EscapeByDoubling = false,
                    MultiLine = false,
                },
                new StringStyle
                {
                    Prefix = "r\"",
                    Suffix = "\"",
                    AllowEscape = false,
                    EscapeByDoubling = true,
                    MultiLine = true
                },
                new StringStyle
                {
                    Prefix = "f\"",
                    Suffix = "'",
                    AllowEscape = true,
                    EscapeByDoubling = false,
                    MultiLine = false
                }
            ], SyntaxKind.StringLiteral),
            "stringLiteral", TerminalFlag.None, priority: 0);

        // Char literal
        AddRule(
            new StringLiteralRule(0,
            [
                new StringStyle
                {
                    Prefix = "'",
                    Suffix = "'",
                    AllowEscape = true,
                    EscapeByDoubling = false,
                    MultiLine = false,
                    IsChar = true
                }
            ], SyntaxKind.CharLiteral),
            "charLiteral", TerminalFlag.None, priority: 0);

        // Boolean literal
        AddRule(
            new BooleanMatchRule(0, caseSensitive: true, kind: SyntaxKind.BooleanLiteral),
            "booleanLiteral", TerminalFlag.None, priority: 0);

        // ──────────────────────────────────────────────────────────────
        //  Special terminals (ε, SyntaxError, $)
        // ──────────────────────────────────────────────────────────────
        var eofTerminal = new Terminal(terminalId++, "$", TerminalFlag.None);
        terminals.Add(eofTerminal);

        var syntaxErrorTerminal = new Terminal(terminalId++, "SyntaxError", TerminalFlag.None);
        terminals.Add(syntaxErrorTerminal);

        // ε (empty) terminal — id but no rule needed for lexer
        var emptyTerminal = new Terminal(terminalId++, "ε", TerminalFlag.None);
        terminals.Add(emptyTerminal);

        // ──────────────────────────────────────────────────────────────
        //  Build ScannerData using same algorithm as ScannerDataBuilder
        // ──────────────────────────────────────────────────────────────
        var scannerData = BuildScannerData(ruleEntries);

        // ──────────────────────────────────────────────────────────────
        //  Build GrammarData (terminals only, no non-terminals needed)
        // ──────────────────────────────────────────────────────────────
        var grammarData = new GrammarData(
            terminals,
            [], // no non-terminals — parser is handwritten
            syntaxErrorTerminal,
            eofTerminal);

        return (grammarData, scannerData);

        // ── Local helper methods ──────────────────────────────────────

        void AddKeyword(string text)
        {
            SyntaxKindHelper.TryFromText(text, out var kind);
            var rule = new KeywordRule(text, 0, kind);
            AddRule(rule, text, keywordFlags, priority: keywordPriority);
        }

        void AddOp(string text)
        {
            SyntaxKindHelper.TryFromText(text, out var kind);
            var rule = new KeywordRule(text, 0, kind);
            AddRule(rule, text, TerminalFlag.None, priority: 0);
        }

        void AddPunct(string text, string name)
        {
            SyntaxKindHelper.TryFromText(text, out var kind);
            var rule = new KeywordRule(text, 0, kind);
            AddRule(rule, text, TerminalFlag.IsPunctuation, priority: 0);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  ScannerDataBuilder algorithm (inlined here)
    // ──────────────────────────────────────────────────────────────────

    private readonly struct RuleEntry(LexerRule rule, Terminal terminal, int priority, bool isLexerOnly, bool isNoPrefix)
    {
        public readonly LexerRule Rule = rule;
        public readonly Terminal Terminal = terminal;
        public readonly int Priority = priority;
        public readonly bool IsLexerOnly = isLexerOnly;
        public readonly bool IsNoPrefix = isNoPrefix;
    }

    private static ScannerData BuildScannerData(List<RuleEntry> entries)
    {
        // 1. Group by priority
        var groupedByPriority = new Dictionary<int, List<RuleEntry>>();
        foreach (var entry in entries)
        {
            if (!groupedByPriority.TryGetValue(entry.Priority, out var list))
            {
                list = [];
                groupedByPriority[entry.Priority] = list;
            }
            list.Add(entry);
        }

        // 2. Sort: index 0 = highest priority
        var sortedGroups = groupedByPriority
            .OrderByDescending(x => x.Key)
            .ToList();

        int totalPhases = sortedGroups.Count;

        // Intermediate storage
        var lexerOnlyRaw = new Dictionary<char, List<LexerRule>?[]>();
        var lexerSymbolRaw = new Dictionary<char, List<LexerRule>?[]>();

        // Fallback rules (NoPrefix rules)
        var fallbackRaw = new List<LexerRule>[totalPhases];
        for (int i = 0; i < totalPhases; i++) fallbackRaw[i] = [];

        // 3. Fill data
        for (int phaseIndex = 0; phaseIndex < totalPhases; phaseIndex++)
        {
            var (_, phaseEntries) = sortedGroups[phaseIndex];

            foreach (var entry in phaseEntries)
            {
                var rule = entry.Rule;
                var firsts = rule.GetFirsts();

                // Fallback: NoPrefix rules (identifiers, symbol operators)
                if (entry.IsNoPrefix || firsts.Count == 0)
                {
                    fallbackRaw[phaseIndex].Add(rule);
                    continue;
                }

                // Lookup table rules
                var targetDict = entry.IsLexerOnly
                    ? lexerOnlyRaw
                    : lexerSymbolRaw;

                foreach (var c in firsts)
                {
                    if (!targetDict.TryGetValue(c, out var phases))
                    {
                        phases = new List<LexerRule>[totalPhases];
                        targetDict[c] = phases;
                    }

                    phases[phaseIndex] ??= [];
                    phases[phaseIndex]!.Add(rule);
                }
            }
        }

        // 4. Compress and build final data
        var lexerOnlyLookup = CompressDictionary(lexerOnlyRaw);
        var lexerLookup = CompressDictionary(lexerSymbolRaw);

        var fallbackRules = fallbackRaw
            .Where(list => list is { Count: > 0 })
            .Select(list => list.ToArray())
            .ToArray();

        return new ScannerData(lexerOnlyLookup, lexerLookup, fallbackRules);
    }

    private static Dictionary<char, LexerRule[][]> CompressDictionary(Dictionary<char, List<LexerRule>?[]> rawDict)
    {
        var result = new Dictionary<char, LexerRule[][]>(rawDict.Count);

        foreach (var kvp in rawDict)
        {
            var compactedPhases = kvp.Value
                .Where(list => list is { Count: > 0 })
                .Select(list => list!.ToArray())
                .ToArray();

            result.Add(kvp.Key, compactedPhases);
        }

        return result;
    }
}
