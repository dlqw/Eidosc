using Eidosc.Pipeline;
using System.Globalization;

namespace Eidosc.ProjectSystem;

public sealed record SemanticVersion : IComparable<SemanticVersion>
{
    public int Major { get; }
    public int Minor { get; }
    public int Patch { get; }
    public string? PreRelease { get; }
    public string? BuildMetadata { get; }

    public SemanticVersion(int major, int minor = 0, int patch = 0, string? preRelease = null, string? buildMetadata = null)
    {
        if (major < 0) throw new ArgumentException(PipelineMessages.MajorVersionMustBeNonNegative, nameof(major));
        if (minor < 0) throw new ArgumentException(PipelineMessages.MinorVersionMustBeNonNegative, nameof(minor));
        if (patch < 0) throw new ArgumentException(PipelineMessages.PatchVersionMustBeNonNegative, nameof(patch));
        if (!string.IsNullOrEmpty(preRelease))
            ValidateIdentifiers(preRelease, allowLeadingZeroInNumericIdentifier: false, preRelease);
        if (!string.IsNullOrEmpty(buildMetadata))
            ValidateIdentifiers(buildMetadata, allowLeadingZeroInNumericIdentifier: true, buildMetadata);
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = string.IsNullOrEmpty(preRelease) ? null : preRelease;
        BuildMetadata = string.IsNullOrEmpty(buildMetadata) ? null : buildMetadata;
    }

    public bool IsPreRelease => PreRelease != null;

    public static SemanticVersion Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new FormatException(PipelineMessages.VersionStringEmpty);

        var span = input.AsSpan().Trim();
        if (span.StartsWith('v') || span.StartsWith('V'))
            throw new FormatException(PipelineMessages.InvalidVersionFormat(input));

        string? preRelease = null;
        string? buildMetadata = null;

        var plusIdx = span.IndexOf('+');
        if (plusIdx >= 0)
        {
            if (plusIdx == span.Length - 1 || span[(plusIdx + 1)..].IndexOf('+') >= 0)
                throw new FormatException(PipelineMessages.InvalidVersionFormat(input));
            buildMetadata = span[(plusIdx + 1)..].ToString();
            span = span[..plusIdx];
            ValidateIdentifiers(buildMetadata, allowLeadingZeroInNumericIdentifier: true, input);
        }

        var dashIdx = span.IndexOf('-');
        if (dashIdx >= 0)
        {
            if (dashIdx == span.Length - 1)
                throw new FormatException(PipelineMessages.InvalidVersionFormat(input));
            preRelease = span[(dashIdx + 1)..].ToString();
            span = span[..dashIdx];
            ValidateIdentifiers(preRelease, allowLeadingZeroInNumericIdentifier: false, input);
        }

        var parts = span.ToString().Split('.');
        if (parts.Length != 3)
            throw new FormatException(PipelineMessages.InvalidVersionFormat(input));

        var major = ParseCoreNumber(parts[0], input);
        var minor = ParseCoreNumber(parts[1], input);
        var patch = ParseCoreNumber(parts[2], input);

        return new SemanticVersion(major, minor, patch, preRelease, buildMetadata);
    }

    private static int ParseCoreNumber(string value, string input)
    {
        if (value.Length == 0 ||
            (value.Length > 1 && value[0] == '0') ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
        {
            throw new FormatException(PipelineMessages.InvalidVersionFormat(input));
        }

        return result;
    }

    private static void ValidateIdentifiers(
        string value,
        bool allowLeadingZeroInNumericIdentifier,
        string input)
    {
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0 ||
                identifier.Any(static character =>
                    character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'Z') and
                    not (>= 'a' and <= 'z') and
                    not '-'))
            {
                throw new FormatException(PipelineMessages.InvalidVersionFormat(input));
            }

            if (!allowLeadingZeroInNumericIdentifier &&
                identifier.Length > 1 &&
                identifier[0] == '0' &&
                identifier.All(static character => character is >= '0' and <= '9'))
            {
                throw new FormatException(PipelineMessages.InvalidVersionFormat(input));
            }
        }
    }

    public static bool TryParse(string input, out SemanticVersion? version)
    {
        try
        {
            version = Parse(input);
            return true;
        }
        catch
        {
            version = null;
            return false;
        }
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null) return 1;

        var result = Major.CompareTo(other.Major);
        if (result != 0) return result;

        result = Minor.CompareTo(other.Minor);
        if (result != 0) return result;

        result = Patch.CompareTo(other.Patch);
        if (result != 0) return result;

        if (PreRelease == null && other.PreRelease == null) return 0;
        if (PreRelease == null) return 1; // release > prerelease
        if (other.PreRelease == null) return -1;

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    private static int ComparePreRelease(string a, string b)
    {
        var partsA = a.Split('.');
        var partsB = b.Split('.');
        var maxLen = Math.Max(partsA.Length, partsB.Length);

        for (var i = 0; i < maxLen; i++)
        {
            if (i >= partsA.Length) return -1;
            if (i >= partsB.Length) return 1;

            var partA = partsA[i];
            var partB = partsB[i];

            var aIsNum = int.TryParse(partA, out var numA);
            var bIsNum = int.TryParse(partB, out var numB);

            if (aIsNum && bIsNum)
            {
                var cmp = numA.CompareTo(numB);
                if (cmp != 0) return cmp;
            }
            else if (aIsNum)
            {
                return -1; // numeric < alpha
            }
            else if (bIsNum)
            {
                return 1;
            }
            else
            {
                var cmp = string.CompareOrdinal(partA, partB);
                if (cmp != 0) return cmp;
            }
        }

        return 0;
    }

    public bool Satisfies(VersionRange range) => range.Contains(this);

    public override string ToString()
    {
        var result = $"{Major}.{Minor}.{Patch}";
        if (PreRelease != null) result += $"-{PreRelease}";
        if (BuildMetadata != null) result += $"+{BuildMetadata}";
        return result;
    }

    public bool HasSamePrecedenceAs(SemanticVersion? other) => CompareTo(other) == 0;

    public bool Equals(SemanticVersion? other)
    {
        if (other is null) return false;
        return Major == other.Major && Minor == other.Minor && Patch == other.Patch
            && PreRelease == other.PreRelease && BuildMetadata == other.BuildMetadata;
    }

    public override int GetHashCode() => HashCode.Combine(Major, Minor, Patch, PreRelease, BuildMetadata);

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;
}
