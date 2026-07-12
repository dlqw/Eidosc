using Eidosc.Diagnostic;

namespace Eidosc.Types;

/// <summary>
/// Kind 文本解析与应用工具。
/// </summary>
public static class KindParser
{
    public static bool TryParse(string kindText, out Kind kind, out string? error)
    {
        kind = Kind.KStar.Instance;
        error = null;

        if (string.IsNullOrWhiteSpace(kindText))
        {
            error = DiagnosticMessages.KindAnnotationIsEmpty;
            return false;
        }

        var tokens = Tokenize(kindText);
        if (tokens.Count == 0)
        {
            error = DiagnosticMessages.KindAnnotationIsEmpty;
            return false;
        }

        var parser = new Parser(tokens);
        if (!parser.TryParseKind(out kind, out error))
        {
            return false;
        }

        if (!parser.IsAtEnd)
        {
            error = DiagnosticMessages.UnexpectedTokenInKindAnnotation(parser.CurrentToken);
            return false;
        }

        return true;
    }

    public static string ToKindText(Kind kind)
    {
        if (TryGetCompactKindName(Resolve(kind), out var compactName))
        {
            return compactName;
        }

        return kind switch
        {
            Kind.KArrow arrow => $"{FormatArrowSide(arrow.Param)} -> {ToKindText(arrow.Result)}",
            Kind.KVar kindVar when kindVar.Instance != null => ToKindText(kindVar.Instance),
            Kind.KVar kindVar => $"k{kindVar.Id}",
            Kind.KRow row => $"({string.Join(", ", row.Fields.Select(ToKindText))})",
            _ => kind.Name
        };
    }

    public static int GetTopLevelArity(Kind kind)
    {
        var arity = 0;
        var current = Resolve(kind);
        while (current is Kind.KArrow arrow)
        {
            arity++;
            current = Resolve(arrow.Result);
        }

        return arity;
    }

    public static bool TryApply(
        Kind constructorKind,
        IReadOnlyList<Kind> argumentKinds,
        out Kind resultKind,
        out string? error)
    {
        error = null;
        resultKind = Resolve(constructorKind);
        foreach (var argumentKind in argumentKinds)
        {
            var normalized = Resolve(resultKind);
            if (normalized is not Kind.KArrow arrow)
            {
                error = DiagnosticMessages.KindCannotBeAppliedToAdditionalTypeArguments(ToKindText(resultKind));
                return false;
            }

            if (!Kind.IsCompatible(arrow.Param, argumentKind))
            {
                error = DiagnosticMessages.KindMismatchInTypeApplication(
                    ToKindText(arrow.Param),
                    ToKindText(argumentKind));
                return false;
            }

            resultKind = Resolve(arrow.Result);
        }

        return true;
    }

    public static bool IsCompactKindName(string text)
    {
        return TryParseCompactKindName(text, out _);
    }

    private static Kind Resolve(Kind kind)
    {
        return kind is Kind.KVar { Instance: not null } kindVar
            ? Resolve(kindVar.Instance)
            : kind;
    }

    private static string FormatArrowSide(Kind kind)
    {
        var resolved = Resolve(kind);
        if (TryGetCompactKindName(resolved, out var compactName))
        {
            return compactName;
        }

        var text = ToKindText(resolved);
        return resolved is Kind.KArrow ? $"({text})" : text;
    }

    private static bool TryGetCompactKindName(Kind kind, out string compactName)
    {
        var arity = 0;
        var current = Resolve(kind);
        while (current is Kind.KArrow arrow && Resolve(arrow.Param) is Kind.KStar)
        {
            arity++;
            current = Resolve(arrow.Result);
        }

        if (current is Kind.KStar)
        {
            compactName = $"kind{arity + 1}";
            return true;
        }

        compactName = "";
        return false;
    }

    private static bool TryParseCompactKindName(string text, out Kind kind)
    {
        kind = Kind.KStar.Instance;
        if (!text.StartsWith("kind", StringComparison.Ordinal) ||
            text.Length == "kind".Length ||
            !int.TryParse(text["kind".Length..], out var kindNumber) ||
            kindNumber < 1)
        {
            return false;
        }

        kind = Kind.BuildArrowKind(kindNumber - 1);
        return true;
    }

    private static List<string> Tokenize(string source)
    {
        var tokens = new List<string>();
        for (var i = 0; i < source.Length;)
        {
            var ch = source[i];
            if (char.IsWhiteSpace(ch))
            {
                i++;
                continue;
            }

            if (ch == '(' || ch == ')')
            {
                tokens.Add(ch.ToString());
                i++;
                continue;
            }

            if (ch == '-' && i + 1 < source.Length && source[i + 1] == '>')
            {
                tokens.Add(WellKnownStrings.Punctuation.RightArrow);
                i += 2;
                continue;
            }

            if (char.IsLetter(ch) || ch == '_')
            {
                var start = i;
                i++;
                while (i < source.Length && (char.IsLetterOrDigit(source[i]) || source[i] == '_'))
                {
                    i++;
                }

                tokens.Add(source[start..i]);
                continue;
            }

            tokens.Add(ch.ToString());
            i++;
        }

        return tokens;
    }

    private sealed class Parser
    {
        private readonly IReadOnlyList<string> _tokens;
        private int _index;

        public Parser(IReadOnlyList<string> tokens)
        {
            _tokens = tokens;
            _index = 0;
        }

        public bool IsAtEnd => _index >= _tokens.Count;

        public string CurrentToken => IsAtEnd ? "<eof>" : _tokens[_index];

        public bool TryParseKind(out Kind kind, out string? error)
        {
            error = null;
            if (!TryParseAtom(out var left, out error))
            {
                kind = Kind.KStar.Instance;
                return false;
            }

            if (Match(WellKnownStrings.Punctuation.RightArrow))
            {
                if (!TryParseKind(out var right, out error))
                {
                    kind = Kind.KStar.Instance;
                    return false;
                }

                kind = new Kind.KArrow(left, right);
                return true;
            }

            kind = left;
            return true;
        }

        private bool TryParseAtom(out Kind kind, out string? error)
        {
            error = null;
            if (!IsAtEnd && TryParseCompactKindName(CurrentToken, out var compactKind))
            {
                kind = compactKind;
                _index++;
                return true;
            }

            if (Match("("))
            {
                if (!TryParseKind(out kind, out error))
                {
                    return false;
                }

                if (!Match(")"))
                {
                    error = DiagnosticMessages.ExpectedKindClosingParen(CurrentToken);
                    kind = Kind.KStar.Instance;
                    return false;
                }

                return true;
            }

            error = DiagnosticMessages.UnexpectedTokenInKindAnnotation(CurrentToken);
            kind = Kind.KStar.Instance;
            return false;
        }

        private bool Match(string token)
        {
            if (IsAtEnd || _tokens[_index] != token)
            {
                return false;
            }

            _index++;
            return true;
        }
    }
}
