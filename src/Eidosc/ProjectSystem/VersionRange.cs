using Eidosc.Pipeline;

namespace Eidosc.ProjectSystem;

public sealed record VersionRange
{
    public VersionRangeKind Kind { get; }
    public SemanticVersion? Min { get; }
    public SemanticVersion? Max { get; }
    public bool IncludeMin { get; }
    public bool IncludeMax { get; }
    public string? RawSpec { get; }

    private VersionRange(VersionRangeKind kind, SemanticVersion? min, SemanticVersion? max, bool includeMin, bool includeMax, string? rawSpec)
    {
        Kind = kind;
        Min = min;
        Max = max;
        IncludeMin = includeMin;
        IncludeMax = includeMax;
        RawSpec = rawSpec;
    }

    public static VersionRange Any() => new(VersionRangeKind.Any, null, null, false, false, "*");

    public static VersionRange Exact(SemanticVersion version) =>
        new(VersionRangeKind.Exact, version, version, true, true, version.ToString());

    public static VersionRange Caret(SemanticVersion version) =>
        new(VersionRangeKind.Caret,
            version,
            version.Major > 0
                ? new SemanticVersion(version.Major + 1)
                : version.Minor > 0
                    ? new SemanticVersion(0, version.Minor + 1)
                    : new SemanticVersion(0, 0, version.Patch + 1),
            true, false, $"^{version}");

    public static VersionRange Tilde(SemanticVersion version) =>
        new(VersionRangeKind.Tilde,
            version,
            new SemanticVersion(version.Major, version.Minor + 1),
            true, false, $"~{version}");

    public static VersionRange Range(SemanticVersion min, SemanticVersion max, bool includeMin = true, bool includeMax = false) =>
        new(VersionRangeKind.Range, min, max, includeMin, includeMax,
            $"{(includeMin ? ">=" : ">")}{min} {(includeMax ? "<=" : "<")}{max}");

    public static VersionRange MinInclusive(SemanticVersion min) =>
        new(VersionRangeKind.Range, min, null, true, false, $">={min}");

    public bool Contains(SemanticVersion version)
    {
        if (Kind == VersionRangeKind.Any) return true;
        if (Kind == VersionRangeKind.Exact) return version.CompareTo(Min) == 0;

        if (Min != null)
        {
            var cmp = version.CompareTo(Min);
            if (IncludeMin ? cmp < 0 : cmp <= 0) return false;
        }

        if (Max != null)
        {
            var cmp = version.CompareTo(Max);
            if (IncludeMax ? cmp > 0 : cmp >= 0) return false;
        }

        return true;
    }

    public static VersionRange Parse(string spec)
    {
        if (string.IsNullOrWhiteSpace(spec))
            throw new FormatException(PipelineMessages.VersionRangeSpecEmpty);

        var s = spec.Trim();

        if (s == "*" || s == "")
            return Any();

        if (s.StartsWith('^'))
            return Caret(SemanticVersion.Parse(s[1..]));

        if (s.StartsWith('~'))
            return Tilde(SemanticVersion.Parse(s[1..]));

        if (s.StartsWith(">="))
            return MinInclusive(SemanticVersion.Parse(s[2..].Trim()));

        if (s.StartsWith('>'))
            return new VersionRange(VersionRangeKind.Range,
                SemanticVersion.Parse(s[1..].Trim()), null, false, false, s);

        if (s.StartsWith("<="))
            return new VersionRange(VersionRangeKind.Range,
                null, SemanticVersion.Parse(s[2..].Trim()), false, true, s);

        if (s.StartsWith('<'))
            return new VersionRange(VersionRangeKind.Range,
                null, SemanticVersion.Parse(s[1..].Trim()), false, false, s);

        if (s.Contains(' '))
        {
            var parts = s.Split(' ', 2);
            if (parts.Length != 2)
                throw new FormatException(PipelineMessages.InvalidCompoundVersionRange(spec));

            var left = Parse(parts[0].Trim());
            var right = Parse(parts[1].Trim());

            return new VersionRange(VersionRangeKind.Range,
                left.Min ?? right.Min,
                right.Max ?? left.Max,
                left.Min != null ? left.IncludeMin : right.IncludeMin,
                right.Max != null ? right.IncludeMax : left.IncludeMax,
                spec);
        }

        return Exact(SemanticVersion.Parse(s));
    }

    public static bool TryParse(string spec, out VersionRange? range)
    {
        try
        {
            range = Parse(spec);
            return true;
        }
        catch
        {
            range = null;
            return false;
        }
    }

    public override string ToString() => RawSpec ?? "*";
}

public enum VersionRangeKind
{
    Any,
    Exact,
    Caret,
    Tilde,
    Range
}
