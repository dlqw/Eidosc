using Eidosc.Symbols;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using EidosAttribute = Eidosc.Ast.Attribute;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private List<string> ParseBorrowCapabilityTags(IReadOnlyList<EidosAttribute> attributes)
    {
        if (attributes.Count == 0)
        {
            return [];
        }

        var tags = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var attribute in attributes)
        {
            if (!string.Equals(attribute.Name, "borrow", StringComparison.Ordinal))
            {
                continue;
            }

            var rawTags = CollectBorrowCapabilityTagTexts(attribute);
            if (rawTags.Count == 0)
            {
                AddError(attribute.Span, DiagnosticMessages.BorrowRequiresCapabilityTag);
                continue;
            }

            foreach (var rawTag in rawTags)
            {
                if (!TryNormalizeBorrowCapabilityTag(rawTag, out var normalizedTag))
                {
                    AddError(
                        attribute.Span,
                        DiagnosticMessages.UnsupportedBorrowCapability(rawTag.Trim()));
                    continue;
                }

                if (seen.Add(normalizedTag))
                {
                    tags.Add(normalizedTag);
                }
            }
        }

        return tags;
    }

    private static List<string> CollectBorrowCapabilityTagTexts(EidosAttribute attribute)
    {
        var tags = new List<string>();
        foreach (var text in attribute.ArgumentTexts)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            foreach (var segment in ExpandBorrowCapabilityTagText(text))
            {
                if (!string.IsNullOrWhiteSpace(segment))
                {
                    tags.Add(segment.Trim());
                }
            }
        }

        foreach (var argument in attribute.Arguments)
        {
            var text = argument switch
            {
                IdentifierExpr identifier => identifier.Name,
                PathExpr pathExpr when pathExpr.Path.Count > 0 => string.Join(WellKnownStrings.Separators.Path, pathExpr.Path),
                LiteralExpr literal => literal.RawText,
                TypePath typePath when typePath.ModulePath.Count > 0 =>
                    string.Join(WellKnownStrings.Separators.Path, typePath.ModulePath) + WellKnownStrings.Separators.Path + typePath.TypeName,
                TypePath typePath => typePath.TypeName,
                _ => ""
            };

            if (!string.IsNullOrWhiteSpace(text))
            {
                tags.Add(text.Trim());
            }
        }

        return tags;
    }

    private static IEnumerable<string> ExpandBorrowCapabilityTagText(string text)
    {
        foreach (var segment in SplitTopLevelCommaList(text))
        {
            var trimmed = segment.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (TryNormalizeBorrowCapabilityTag(trimmed, out _))
            {
                yield return trimmed;
                continue;
            }

            foreach (var expanded in SplitConcatenatedBorrowTags(trimmed))
            {
                yield return expanded;
            }
        }
    }

    private static IEnumerable<string> SplitConcatenatedBorrowTags(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            yield break;
        }

        if (value.Contains(WellKnownStrings.Separators.Path, StringComparison.Ordinal) ||
            value.Contains(WellKnownStrings.Separators.ModulePath, StringComparison.Ordinal))
        {
            yield return value;
            yield break;
        }

        var knownTags = new[] { "ownership", "consume", "mutable", "write", "move", "read", WellKnownStrings.Keywords.Mut };
        var normalized = value.ToLowerInvariant();
        var index = 0;
        var decomposed = new List<string>();
        var matchedKnownTag = false;

        while (index < normalized.Length)
        {
            string? matched = null;
            foreach (var knownTag in knownTags)
            {
                if (normalized.AsSpan(index).StartsWith(knownTag, StringComparison.Ordinal))
                {
                    matched = knownTag;
                    break;
                }
            }

            if (matched != null)
            {
                matchedKnownTag = true;
                decomposed.Add(value.Substring(index, matched.Length));
                index += matched.Length;
                continue;
            }

            var nextKnownTagIndex = -1;
            for (var probe = index + 1; probe < normalized.Length; probe++)
            {
                if (knownTags.Any(tag => normalized.AsSpan(probe).StartsWith(tag, StringComparison.Ordinal)))
                {
                    nextKnownTagIndex = probe;
                    break;
                }
            }

            if (nextKnownTagIndex < 0)
            {
                decomposed.Add(value[index..]);
                index = normalized.Length;
                continue;
            }

            decomposed.Add(value.Substring(index, nextKnownTagIndex - index));
            index = nextKnownTagIndex;
        }

        if (!matchedKnownTag || decomposed.Count <= 1)
        {
            yield return value;
            yield break;
        }

        foreach (var segment in decomposed)
        {
            if (!string.IsNullOrWhiteSpace(segment))
            {
                yield return segment.Trim();
            }
        }
    }

    private static bool TryNormalizeBorrowCapabilityTag(string rawTag, out string normalizedTag)
    {
        normalizedTag = string.Empty;
        if (string.IsNullOrWhiteSpace(rawTag))
        {
            return false;
        }

        var text = rawTag.Trim();
        if (text.Length >= 2 &&
            ((text[0] == '"' && text[^1] == '"') ||
             (text[0] == '\'' && text[^1] == '\'')))
        {
            text = text[1..^1].Trim();
        }

        if (text.Contains(WellKnownStrings.Separators.Path, StringComparison.Ordinal) ||
            text.Contains(WellKnownStrings.Separators.ModulePath, StringComparison.Ordinal))
        {
            text = text.Replace(WellKnownStrings.Separators.ModulePath, WellKnownStrings.Separators.Path, StringComparison.Ordinal);
            var pathSegments = text.Split(
                [WellKnownStrings.Separators.Path],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pathSegments.Length > 0)
            {
                text = pathSegments[^1];
            }
        }

        switch (text.ToLowerInvariant())
        {
            case "read":
                normalizedTag = "read";
                return true;
            case "write":
            case WellKnownStrings.Keywords.Mut:
            case "mutable":
                normalizedTag = "write";
                return true;
            case "move":
            case "consume":
            case "ownership":
                normalizedTag = "move";
                return true;
            default:
                return false;
        }
    }
}
