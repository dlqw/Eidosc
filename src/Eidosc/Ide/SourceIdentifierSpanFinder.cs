using Eidosc.Utils;

namespace Eidosc.Ide;

internal static class SourceIdentifierSpanFinder
{
    public static bool TryFind(
        string sourceText,
        SourceSpan container,
        string identifier,
        out SourceSpan span,
        bool preferLast = false)
    {
        span = SourceSpan.Empty;
        if (string.IsNullOrEmpty(sourceText) ||
            string.IsNullOrWhiteSpace(identifier) ||
            container.Position < 0 ||
            container.Length <= 0 ||
            container.EndPosition > sourceText.Length)
        {
            return false;
        }

        var matches = new List<int>();
        var end = container.EndPosition - identifier.Length;
        for (var position = container.Position; position <= end; position++)
        {
            if (!sourceText.AsSpan(position, identifier.Length).SequenceEqual(identifier.AsSpan()) ||
                position > 0 && IsIdentifierPart(sourceText[position - 1]) ||
                position + identifier.Length < sourceText.Length &&
                IsIdentifierPart(sourceText[position + identifier.Length]))
            {
                continue;
            }

            matches.Add(position);
        }

        if (matches.Count == 0)
        {
            return false;
        }

        var match = preferLast ? matches[^1] : matches[0];
        span = CreateSpan(sourceText, container, match, identifier.Length);
        return true;
    }

    private static SourceSpan CreateSpan(
        string sourceText,
        SourceSpan container,
        int absolutePosition,
        int length)
    {
        var line = container.Location.Line;
        var column = container.Location.Column;
        for (var position = container.Position; position < absolutePosition; position++)
        {
            if (sourceText[position] == '\n')
            {
                line++;
                column = 0;
            }
            else
            {
                column++;
            }
        }

        return new SourceSpan(
            new SourceLocation(absolutePosition, line, column, container.FilePath),
            length);
    }

    private static bool IsIdentifierPart(char value) =>
        char.IsLetterOrDigit(value) || value == '_';
}
