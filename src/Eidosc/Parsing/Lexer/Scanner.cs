using Eidosc.ErrorRecovery;
using Eidosc.Diagnostic;
using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc;

public static class Scanner
{
    /// <summary>
    /// Lexer 错误恢复上下文（线程静态，避免多次初始化）
    /// </summary>
    [ThreadStatic]
    private static ErrorRecoveryContext? _recoveryContext;

    /// <summary>
    /// 获取或创建错误恢复上下文
    /// </summary>
    private static ErrorRecoveryContext GetRecoveryContext()
    {
        return _recoveryContext ??= ErrorRecoveryContext.ForLexer();
    }

    public static void Init(LexerContext context)
    {
        var tokenStream = Tokenize(context);
        var filters = context.TokenFilters;
        tokenStream = filters.Aggregate(tokenStream, (current, filter) => filter.Filter(context, current));
        context.TokenStream = tokenStream.GetEnumerator();

        // 重置错误恢复上下文
        GetRecoveryContext().Reset();
    }

    public static Token? GetToken(LexerContext context)
    {
        if (context.TokenStream == null) return null;
        if (!context.TokenStream.MoveNext()) return null;
        return context.TokenStream.Current;
    }

    private static IEnumerable<Token> Tokenize(LexerContext context)
    {
        var recovery = GetRecoveryContext();

        while (true)
        {
            // 检查是否达到错误限制
            if (recovery.HasReachedLimit)
            {
                yield break;
            }

            var newToken = NextToken(context);

            if (newToken is EofToken) break;

            if (newToken is ErrorToken)
            {
                recovery.RecordError();
                yield return newToken;
            }
            else
            {
                recovery.RecordSuccess();
                yield return newToken;
            }
        }
    }

    private static Token NextToken(LexerContext context)
    {
        if (context.PendingTokenStack.Count > 0) return context.PendingTokenStack.Pop();
        var source = context.Source;
        context.SkipWhitespace(source);
        source.Position = source.PreviewPosition;
        if (source.Eof())
        {
            return Token.CreateEofToken(source);
        }

        var newToken = ScanToken(context, source);
        if (newToken is ErrorToken)
        {
            // 尝试恢复 - 跳过无效字符并继续
            RecoverFromError(source, newToken);
        }

        return newToken;
    }

 private static Token ScanToken(LexerContext context, ISourceStream stream)
    {
        Token? newToken;
        char previewChar = stream.PreviewChar;

        // ── Keyword trie fast path ──────────────────────────────
        // For ASCII lowercase letters, try the trie first to get O(L)
        // longest-keyword match instead of O(K) sequential MatchSymbol calls.
        if (context.KeywordTrie != null && (uint)(previewChar - 'a') <= (uint)('z' - 'a'))
        {
            newToken = TryKeywordTrie(context, stream);
            if (newToken != null)
            {
                stream.Step(newToken.Length);
                return newToken;
            }
            stream.ResetPreviewPosition();
            // Trie miss → identifier or boolean literal.
            // Skip keyword phase, go directly to fallback rules.
            // (BooleanMatchRule for "true"/"false" is in OtherLexerSymbols)
            newToken = TokenizeLongest(context.OtherLexerSymbols, context);
            if (newToken != null)
            {
                stream.Step(newToken.Length);
                return newToken;
            }
            stream.ResetPreviewPosition();
        }
        else
        {
            // ── Existing rule-based path for non-letter characters ──

            if (context.LexerOnlySymbolLookup.TryGetValue(previewChar, out var lexerOnlyRules))
            {
                newToken = TokenizeLongest(lexerOnlyRules, context);
                if (newToken != null)
                {
                    newToken.IsLexerOnly = true;
                    stream.Step(newToken.Length);
                    return newToken;
                }
            }

            stream.ResetPreviewPosition();

            if (context.LexerSymbolLookup.TryGetValue(previewChar, out var lexerRules))
            {
                newToken = TokenizeLongest(lexerRules, context);
                if (newToken != null)
                {
                    stream.Step(newToken.Length);
                    return newToken;
                }
            }

            stream.ResetPreviewPosition();

            newToken = TokenizeLongest(context.OtherLexerSymbols, context);
            if (newToken != null)
            {
                stream.Step(newToken.Length);
                return newToken;
            }

            stream.ResetPreviewPosition();
        }

        // 无效字符时跳过并继续
        // 记录无效字符错误
        var invalidChar = stream.PreviewChar;
        var errorToken = new ErrorToken(
            stream.Location,
            DiagnosticMessages.InvalidCharacter(invalidChar, invalidChar));
        return errorToken;
    }

    /// <summary>
    /// Try matching a keyword via the trie. Returns a token if a keyword matches
    /// with valid word boundaries, or null if no keyword matches.
    /// </summary>
    private static Token? TryKeywordTrie(LexerContext context, ISourceStream stream)
    {
        var remaining = stream.RemainingSpan;
        var match = context.KeywordTrie!.TryMatchLongest(remaining);
        if (match == null)
            return null;

        var entry = match.Value.Entry;
        var length = match.Value.Length;

        // Boundary check: if keyword ends with letter/digit/_,
        // the next character must not be letter/digit/_ (prevents
        // matching "import" inside "important")
        var lastChar = entry.Text[^1];
        if (char.IsLetterOrDigit(lastChar) || lastChar == '_')
        {
            var nextChar = length < remaining.Length ? remaining[length] : '\0';
            if (char.IsLetterOrDigit(nextChar) || nextChar == '_')
                return null;
        }

        // Match confirmed — advance preview and create token
        stream.PreviewPosition = stream.Position + length;
        var terminal = context.Terminals[entry.TerminalId];
        return Token.CreateContentToken(stream, entry.Kind, terminal, entry.Text.AsSpan().GetOrIntern());
    }

    /// <summary>
    /// 在给定的规则组中寻找匹配项。
    /// 逻辑：
    /// 1. 从高优先级数组向低优先级数组遍历。
    /// 2. 一旦在某个优先级层级（Phase）中找到匹配项，就不再查看更低优先级的层级。
    /// 3. 在同一层级内，寻找【最长】的匹配项。
    /// </summary>
    private static Token? TokenizeLongest(LexerRule[][] phaseGroups, LexerContext context)
    {
        var stream = context.Source;

        // 遍历每一个优先级层级
        foreach (var rulesInCurrentPhase in phaseGroups)
        {
            if (rulesInCurrentPhase.Length == 0) continue;

            Token? bestTokenInPhase = null;

            // 遍历当前层级的所有规则，寻找最长匹配
            foreach (var rule in rulesInCurrentPhase)
            {
                // 每次尝试规则前必须重置游标
                stream.ResetPreviewPosition();

                var tempToken = rule.Tokenize(context);

                if (tempToken != null)
                {
                    // 如果之前没找到，或者当前找到的比之前的更长，则更新
                    if (bestTokenInPhase == null || tempToken.Length > bestTokenInPhase.Length)
                    {
                        bestTokenInPhase = tempToken;
                    }
                }
            }

            // 如果在当前（较高的）优先级层级找到了 Token，我们直接返回它。
            if (bestTokenInPhase != null)
            {
                return bestTokenInPhase;
            }
        }

        return null;
    }

#region Error recovery

    /// <summary>
    /// 从词法错误中恢复
    /// 策略：跳过到下一个分隔符或空白字符
    /// </summary>
    private static void RecoverFromError(ISourceStream stream, Token errorToken)
    {
        // 未闭合字符串/无效字符/数字格式错误时报告并尝试恢复
        // 至少跳过一个字符
        stream.PreviewPosition = stream.Position + 1;

        string text = stream.Text;
        int len = text.Length;

        // 跳过到下一个分隔符
        while (stream.PreviewPosition < len)
        {
            char c = text[stream.PreviewPosition];

            // 使用统一的 CodePoints 判断
            if (CodePoints.IsDelimiter(c))
            {
                stream.Position = stream.PreviewPosition;
                return;
            }

            stream.PreviewPosition++;
        }

        // 到达 EOF
        stream.Position = stream.PreviewPosition;
    }

    /// <summary>
    /// 跳过无效字符并恢复到有效位置
    /// </summary>
    private static bool SkipToRecoveryPoint(ISourceStream stream)
    {
        stream.PreviewPosition++; // 至少跳过一个字符

        string text = stream.Text;
        int len = text.Length;

        while (stream.PreviewPosition < len)
        {
            char c = text[stream.PreviewPosition];

            // 使用统一的 CodePoints 判断
            if (CodePoints.IsDelimiter(c))
            {
                stream.Position = stream.PreviewPosition;
                return true;
            }

            stream.PreviewPosition++;
        }

        return false;
    }

    /// <summary>
    /// 尝试从未闭合字符串中恢复
    /// </summary>
    private static bool RecoverFromUnclosedString(ISourceStream stream)
    {
        string text = stream.Text;
        int len = text.Length;
        int pos = stream.PreviewPosition;

        // 尝试找到下一个换行符或字符串结束符
        while (pos < len)
        {
            char c = text[pos];
            if (c == '"' || c == '\'' || CodePoints.IsNewLine(c))
            {
                stream.PreviewPosition = pos;
                stream.Position = pos;
                return true;
            }
            pos++;
        }

        stream.PreviewPosition = len;
        stream.Position = len;
        return false;
    }

    /// <summary>
    /// 尝试从数字格式错误中恢复
    /// </summary>
    private static bool RecoverFromInvalidNumber(ISourceStream stream)
    {
        string text = stream.Text;
        int len = text.Length;
        int pos = stream.PreviewPosition;

        // 跳过数字字符直到遇到非数字字符
        while (pos < len)
        {
            char c = text[pos];
            if (!char.IsAsciiDigit(c) && c != '.' && c != 'x' && c != 'X' &&
                c != 'b' && c != 'B' && c != 'e' && c != 'E' && c != '+' &&
                c != '-' && c != '_')
            {
                break;
            }
            pos++;
        }

        stream.PreviewPosition = pos;
        stream.Position = pos;
        return true;
    }

#endregion
}
