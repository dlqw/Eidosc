using Eidosc.Utils;

namespace Eidosc.Ide;

internal readonly record struct SourceTextPosition(int Line, int Character);

internal sealed class SourceTextCoordinateMap
{
    private readonly string _sourceText;
    private readonly int[] _lineStarts;

    public SourceTextCoordinateMap(string sourceText)
    {
        _sourceText = sourceText ?? string.Empty;
        var lineStarts = new List<int> { 0 };
        for (var index = 0; index < _sourceText.Length; index++)
        {
            if (_sourceText[index] == '\n')
            {
                lineStarts.Add(index + 1);
            }
        }

        _lineStarts = [.. lineStarts];
    }

    public int Length => _sourceText.Length;

    public SourceTextPosition PositionAt(int offset)
    {
        var boundedOffset = Math.Clamp(offset, 0, _sourceText.Length);
        var line = Array.BinarySearch(_lineStarts, boundedOffset);
        if (line < 0)
        {
            line = ~line - 1;
        }

        line = Math.Max(0, line);
        var lineStart = _lineStarts[line];
        var lineContentEnd = GetLineContentEnd(line);
        return new SourceTextPosition(line, Math.Min(boundedOffset, lineContentEnd) - lineStart);
    }

    public IdeSpan CreateSpan(SourceSpan span)
    {
        var start = Math.Clamp(span.Position, 0, _sourceText.Length);
        var requestedEnd = (long)span.Position + Math.Max(0, span.Length);
        var end = (int)Math.Clamp(requestedEnd, start, _sourceText.Length);
        var startPosition = PositionAt(start);
        var endPosition = PositionAt(end);
        return new IdeSpan
        {
            StartLine = startPosition.Line,
            StartCharacter = startPosition.Character,
            EndLine = endPosition.Line,
            EndCharacter = endPosition.Character,
            Start = start,
            Length = end - start,
            FilePath = string.IsNullOrWhiteSpace(span.FilePath) ? null : span.FilePath
        };
    }

    private int GetLineContentEnd(int line)
    {
        if (line + 1 >= _lineStarts.Length)
        {
            return _sourceText.Length;
        }

        var nextLineStart = _lineStarts[line + 1];
        var end = nextLineStart - 1;
        if (end > _lineStarts[line] && _sourceText[end - 1] == '\r')
        {
            end--;
        }

        return end;
    }
}

internal sealed class IdeSourceCoordinateResolver
{
    private readonly string? _primaryFilePath;
    private readonly SourceTextCoordinateMap _primaryMap;
    private readonly Dictionary<string, SourceTextCoordinateMap?> _maps;

    public IdeSourceCoordinateResolver(string? primaryFilePath, string sourceText)
    {
        _primaryFilePath = NormalizeFilePath(primaryFilePath);
        _primaryMap = new SourceTextCoordinateMap(sourceText);
        _maps = new Dictionary<string, SourceTextCoordinateMap?>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        if (_primaryFilePath != null)
        {
            _maps[_primaryFilePath] = _primaryMap;
        }
    }

    public bool TryCreateSpan(SourceSpan span, out IdeSpan result)
    {
        var filePath = NormalizeFilePath(span.FilePath);
        if (filePath == null)
        {
            result = _primaryMap.CreateSpan(span);
            return true;
        }

        if (!_maps.TryGetValue(filePath, out var map))
        {
            map = TryLoadMap(filePath);
            _maps[filePath] = map;
        }

        if (map == null)
        {
            result = IdeSpan.Empty;
            return false;
        }

        result = map.CreateSpan(span);
        return true;
    }

    private static SourceTextCoordinateMap? TryLoadMap(string filePath)
    {
        try
        {
            return File.Exists(filePath)
                ? new SourceTextCoordinateMap(File.ReadAllText(filePath))
                : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? NormalizeFilePath(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) ||
            Uri.TryCreate(filePath, UriKind.Absolute, out var uri) && !uri.IsFile)
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(filePath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }
}
