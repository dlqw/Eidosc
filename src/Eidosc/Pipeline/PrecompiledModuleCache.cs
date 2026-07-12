using System.Text;
using Eidosc.Parsing.Lexer;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;
using Eidosc.Utils;

namespace Eidosc.Pipeline;

internal enum PrecompiledModuleSourcePolicy
{
    FullBody,
    SignatureOnly
}

internal enum PrecompiledTokenCacheKind
{
    FullBody,
    SignatureOnly
}

internal sealed record PrecompiledModuleSourceResult(
    string Source,
    long FunctionBodyReplacementCount,
    long ValueInitializerReplacementCount,
    long ImportRemovalCount);

internal sealed record PrecompiledTokenCacheResult(
    IReadOnlyList<Token> Tokens,
    IReadOnlyList<Diagnostic.Diagnostic> LexerDiagnostics,
    bool CacheHit);

internal static class PrecompiledModuleCache
{
    private static readonly Dictionary<string, string> SourceCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (string Source, long FunctionBodyReplacementCount, long ValueInitializerReplacementCount, long ImportRemovalCount)> SignatureSourceCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (IReadOnlyList<Token> Tokens, IReadOnlyList<Diagnostic.Diagnostic> Diagnostics)> FullTokenCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, (IReadOnlyList<Token> Tokens, IReadOnlyList<Diagnostic.Diagnostic> Diagnostics)> SignatureTokenCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, string> SourceFilePathCache = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();

    public static bool TryGetSource(
        IReadOnlyList<string> modulePath,
        PrecompiledModuleSourcePolicy policy,
        out PrecompiledModuleSourceResult result)
    {
        result = new PrecompiledModuleSourceResult(string.Empty, 0, 0, 0);
        var key = BuildModulePathKey(modulePath);
        if (policy == PrecompiledModuleSourcePolicy.SignatureOnly)
        {
            lock (CacheLock)
            {
                if (SignatureSourceCache.TryGetValue(key, out var cached))
                {
                    result = new PrecompiledModuleSourceResult(
                        cached.Source,
                        cached.FunctionBodyReplacementCount,
                        cached.ValueInitializerReplacementCount,
                        cached.ImportRemovalCount);
                    return true;
                }
            }
        }

        string source;
        lock (CacheLock)
        {
            if (!SourceCache.TryGetValue(key, out source!))
            {
                source = string.Empty;
            }
        }

        if (source.Length == 0)
        {
            if (!PrecompiledModuleRegistry.TryGetSource(modulePath, out source))
            {
                return false;
            }

            lock (CacheLock)
            {
                SourceCache[key] = source;
            }
        }

        if (policy == PrecompiledModuleSourcePolicy.FullBody)
        {
            result = new PrecompiledModuleSourceResult(source, 0, 0, 0);
            return true;
        }

        var signatureSource = GetOrCreateSignatureSource(
            key,
            source,
            out var functionBodyReplacementCount,
            out var valueInitializerReplacementCount,
            out var importRemovalCount);
        result = new PrecompiledModuleSourceResult(
            signatureSource,
            functionBodyReplacementCount,
            valueInitializerReplacementCount,
            importRemovalCount);
        return true;
    }

    public static PrecompiledModuleSourceResult GetOrCreateSignatureSource(
        string key,
        string source)
    {
        var signatureSource = GetOrCreateSignatureSource(
            key,
            source,
            out var functionBodyReplacementCount,
            out var valueInitializerReplacementCount,
            out var importRemovalCount);
        return new PrecompiledModuleSourceResult(
            signatureSource,
            functionBodyReplacementCount,
            valueInitializerReplacementCount,
            importRemovalCount);
    }

    public static bool TryGetSourceFilePath(IReadOnlyList<string> modulePath, out string filePath)
    {
        var key = BuildModulePathKey(modulePath);
        lock (CacheLock)
        {
            if (SourceFilePathCache.TryGetValue(key, out filePath!))
            {
                return true;
            }
        }

        if (!PrecompiledModuleRegistry.TryGetSourceFilePath(modulePath, out filePath))
        {
            return false;
        }

        lock (CacheLock)
        {
            SourceFilePathCache[key] = filePath;
        }

        return true;
    }

    public static PrecompiledTokenCacheResult GetOrCreateTokens(
        PrecompiledTokenCacheKind kind,
        string sourceText,
        string sourceName,
        ModuleParseService parseService,
        CancellationToken cancellationToken = default,
        bool addLexerErrorDiagnosticsBeforeContextDiagnostics = true)
    {
        var cache = kind == PrecompiledTokenCacheKind.FullBody
            ? FullTokenCache
            : SignatureTokenCache;
        var cacheKey = $"{sourceName}\0{ContentHash.ComputeHash(sourceText)}";

        lock (CacheLock)
        {
            if (cache.TryGetValue(cacheKey, out var cached))
            {
                return new PrecompiledTokenCacheResult(cached.Tokens, cached.Diagnostics, CacheHit: true);
            }
        }

        var lexResult = parseService.LexSource(
            sourceText,
            sourceName,
            cancellationToken,
            addLexerErrorDiagnosticsBeforeContextDiagnostics);

        lock (CacheLock)
        {
            if (!cache.ContainsKey(cacheKey))
            {
                cache[cacheKey] = (lexResult.Tokens, lexResult.Diagnostics);
            }
        }

        return new PrecompiledTokenCacheResult(lexResult.Tokens, lexResult.Diagnostics, CacheHit: false);
    }

    private static string BuildModulePathKey(IReadOnlyList<string> modulePath) =>
        string.Join(WellKnownStrings.Operators.Divide, modulePath);

    private static string GetOrCreateSignatureSource(
        string key,
        string source,
        out long functionBodyReplacementCount,
        out long valueInitializerReplacementCount,
        out long importRemovalCount)
    {
        lock (CacheLock)
        {
            if (SignatureSourceCache.TryGetValue(key, out var cached))
            {
                functionBodyReplacementCount = cached.FunctionBodyReplacementCount;
                valueInitializerReplacementCount = cached.ValueInitializerReplacementCount;
                importRemovalCount = cached.ImportRemovalCount;
                return cached.Source;
            }
        }

        var stripped = StripImplementationsFromPrecompiledSource(
            source,
            out functionBodyReplacementCount,
            out valueInitializerReplacementCount);
        stripped = RemoveUnusedNonExportImportsFromSignatureSource(stripped, out importRemovalCount);
        lock (CacheLock)
        {
            SignatureSourceCache[key] = (
                stripped,
                functionBodyReplacementCount,
                valueInitializerReplacementCount,
                importRemovalCount);
        }

        return stripped;
    }

    private static string StripImplementationsFromPrecompiledSource(
        string source,
        out long functionBodyReplacementCount,
        out long valueInitializerReplacementCount)
    {
        var builder = new StringBuilder(source.Length);
        var index = 0;
        functionBodyReplacementCount = 0;
        valueInitializerReplacementCount = 0;
        while (index < source.Length)
        {
            var ch = source[index];
            if (ch == '"' || ch == '\'')
            {
                CopyQuotedLiteral(source, builder, ref index, ch);
                continue;
            }

            if (ch == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                CopyLineComment(source, builder, ref index);
                continue;
            }

            if (ch == '{' && IsFunctionBodyBrace(source, index))
            {
                builder.Append(';');
                functionBodyReplacementCount++;
                index = SkipBalancedBraceBlock(source, index);
                continue;
            }

            if (ch == '=' && IsValueInitializerEquals(source, index))
            {
                builder.Append(';');
                valueInitializerReplacementCount++;
                index = SkipValueInitializer(source, index);
                continue;
            }

            builder.Append(ch);
            index++;
        }

        return builder.ToString();
    }

    private static string RemoveUnusedNonExportImportsFromSignatureSource(
        string source,
        out long importRemovalCount)
    {
        importRemovalCount = 0;
        var builder = new StringBuilder(source.Length);
        var lineStart = 0;
        while (lineStart < source.Length)
        {
            var lineEnd = source.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = source.Length;
            }

            var lineLength = lineEnd - lineStart;
            var line = source.AsSpan(lineStart, lineLength);
            var trimmed = line.TrimStart();
            if (IsRemovableSignatureImport(source, lineStart, lineEnd, trimmed))
            {
                importRemovalCount++;
            }
            else
            {
                builder.Append(source, lineStart, lineLength);
                if (lineEnd < source.Length)
                {
                    builder.Append(source[lineEnd]);
                }
            }

            lineStart = lineEnd < source.Length ? lineEnd + 1 : source.Length;
        }

        return builder.ToString();
    }

    private static bool IsRemovableSignatureImport(
        string source,
        int lineStart,
        int lineEnd,
        ReadOnlySpan<char> trimmedLine)
    {
        if (!trimmedLine.StartsWith("import ".AsSpan(), StringComparison.Ordinal) &&
            !trimmedLine.StartsWith("import\t".AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }

        var importTarget = trimmedLine["import".Length..].TrimStart();
        var moduleNameLength = 0;
        while (moduleNameLength < importTarget.Length &&
               IsIdentifierChar(importTarget[moduleNameLength]))
        {
            moduleNameLength++;
        }

        if (moduleNameLength == 0)
        {
            return false;
        }

        var moduleName = importTarget[..moduleNameLength].ToString();
        return !ContainsIdentifierOutsideRange(source, moduleName, lineStart, lineEnd);
    }

    private static bool ContainsIdentifierOutsideRange(
        string source,
        string identifier,
        int excludedStart,
        int excludedEnd)
    {
        var index = source.IndexOf(identifier, StringComparison.Ordinal);
        while (index >= 0)
        {
            var outsideExcludedRange = index < excludedStart || index >= excludedEnd;
            var before = index == 0 || !IsIdentifierChar(source[index - 1]);
            var afterIndex = index + identifier.Length;
            var after = afterIndex >= source.Length || !IsIdentifierChar(source[afterIndex]);
            if (outsideExcludedRange && before && after)
            {
                return true;
            }

            var nextStart = index + identifier.Length;
            var nextIndex = source.IndexOf(identifier, nextStart, StringComparison.Ordinal);
            index = nextIndex;
        }

        return false;
    }

    private static bool IsFunctionBodyBrace(string source, int braceIndex)
    {
        var start = braceIndex - 1;
        while (start >= 0)
        {
            var ch = source[start];
            if (ch is ';' or '{' or '}')
            {
                start++;
                break;
            }

            start--;
        }

        if (start < 0)
        {
            start = 0;
        }

        var header = source.AsSpan(start, braceIndex - start).Trim();
        return header.Contains("::", StringComparison.Ordinal) &&
               header.Contains("->", StringComparison.Ordinal) &&
               !header.Contains("=>", StringComparison.Ordinal) &&
               !header.Contains("=", StringComparison.Ordinal);
    }

    private static bool IsValueInitializerEquals(string source, int equalsIndex)
    {
        if (equalsIndex > 0 && source[equalsIndex - 1] == '=')
        {
            return false;
        }

        if (equalsIndex + 1 < source.Length && source[equalsIndex + 1] == '=')
        {
            return false;
        }

        var start = equalsIndex - 1;
        while (start >= 0)
        {
            var ch = source[start];
            if (ch is ';' or '{' or '}')
            {
                start++;
                break;
            }

            start--;
        }

        if (start < 0)
        {
            start = 0;
        }

        var header = source.AsSpan(start, equalsIndex - start).Trim();
        if (!header.Contains("::", StringComparison.Ordinal) ||
            header.Contains("=>", StringComparison.Ordinal) ||
            header.Contains("->", StringComparison.Ordinal))
        {
            return false;
        }

        return !ContainsDeclarationKeyword(header, WellKnownStrings.Keywords.Type) &&
               !ContainsDeclarationKeyword(header, WellKnownStrings.Keywords.Trait) &&
               !ContainsDeclarationKeyword(header, WellKnownStrings.Keywords.Effect) &&
               !ContainsDeclarationKeyword(header, "instance") &&
               !ContainsDeclarationKeyword(header, WellKnownStrings.Keywords.Module) &&
               !ContainsDeclarationKeyword(header, WellKnownStrings.Keywords.Import);
    }

    private static bool ContainsDeclarationKeyword(ReadOnlySpan<char> header, string keyword)
    {
        var index = header.IndexOf(keyword.AsSpan(), StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 || !IsIdentifierChar(header[index - 1]);
            var afterIndex = index + keyword.Length;
            var after = afterIndex >= header.Length || !IsIdentifierChar(header[afterIndex]);
            if (before && after)
            {
                return true;
            }

            var nextStart = index + keyword.Length;
            var nextIndex = header[nextStart..].IndexOf(keyword.AsSpan(), StringComparison.Ordinal);
            index = nextIndex < 0 ? -1 : nextStart + nextIndex;
        }

        return false;
    }

    private static bool IsIdentifierChar(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch == '_';
    }

    private static int SkipBalancedBraceBlock(string source, int start)
    {
        var depth = 0;
        var index = start;
        while (index < source.Length)
        {
            var ch = source[index];
            if (ch == '"' || ch == '\'')
            {
                SkipQuotedLiteral(source, ref index, ch);
                continue;
            }

            if (ch == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                SkipLineComment(source, ref index);
                continue;
            }

            if (ch == '{')
            {
                depth++;
            }
            else if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return index + 1;
                }
            }

            index++;
        }

        return source.Length;
    }

    private static int SkipValueInitializer(string source, int start)
    {
        var parenDepth = 0;
        var bracketDepth = 0;
        var braceDepth = 0;
        var index = start + 1;
        while (index < source.Length)
        {
            var ch = source[index];
            if (ch == '"' || ch == '\'')
            {
                SkipQuotedLiteral(source, ref index, ch);
                continue;
            }

            if (ch == '/' && index + 1 < source.Length && source[index + 1] == '/')
            {
                SkipLineComment(source, ref index);
                continue;
            }

            switch (ch)
            {
                case '(':
                    parenDepth++;
                    break;
                case ')':
                    if (parenDepth > 0)
                    {
                        parenDepth--;
                    }
                    break;
                case '[':
                    bracketDepth++;
                    break;
                case ']':
                    if (bracketDepth > 0)
                    {
                        bracketDepth--;
                    }
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    if (braceDepth > 0)
                    {
                        braceDepth--;
                    }
                    break;
                case ';' when parenDepth == 0 && bracketDepth == 0 && braceDepth == 0:
                    return index + 1;
            }

            index++;
        }

        return source.Length;
    }

    private static void CopyQuotedLiteral(string source, StringBuilder builder, ref int index, char quote)
    {
        var start = index;
        SkipQuotedLiteral(source, ref index, quote);
        builder.Append(source, start, index - start);
    }

    private static void SkipQuotedLiteral(string source, ref int index, char quote)
    {
        index++;
        while (index < source.Length)
        {
            var ch = source[index++];
            if (ch == '\\' && index < source.Length)
            {
                index++;
                continue;
            }

            if (ch == quote)
            {
                break;
            }
        }
    }

    private static void CopyLineComment(string source, StringBuilder builder, ref int index)
    {
        var start = index;
        SkipLineComment(source, ref index);
        builder.Append(source, start, index - start);
    }

    private static void SkipLineComment(string source, ref int index)
    {
        index += 2;
        while (index < source.Length && source[index] != '\n')
        {
            index++;
        }

        if (index < source.Length)
        {
            index++;
        }
    }
}
