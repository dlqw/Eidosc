namespace Eidosup.Distribution;

public sealed record SemanticVersion : IComparable<SemanticVersion>
{
    private SemanticVersion(
        string major,
        string minor,
        string patch,
        string? preRelease,
        string? buildMetadata)
    {
        Major = major;
        Minor = minor;
        Patch = patch;
        PreRelease = preRelease;
        BuildMetadata = buildMetadata;
    }

    public string Major { get; }

    public string Minor { get; }

    public string Patch { get; }

    public string? PreRelease { get; }

    public string? BuildMetadata { get; }

    public bool IsPreRelease => PreRelease != null;

    public static SemanticVersion Parse(string input)
    {
        if (string.IsNullOrEmpty(input) || !string.Equals(input, input.Trim(), StringComparison.Ordinal))
        {
            throw new FormatException($"'{input}' is not a valid SemVer 2.0.0 version.");
        }

        var coreAndPreRelease = input.AsSpan();
        string? buildMetadata = null;
        var plusIndex = coreAndPreRelease.IndexOf('+');
        if (plusIndex >= 0)
        {
            if (plusIndex == coreAndPreRelease.Length - 1 || coreAndPreRelease[(plusIndex + 1)..].Contains('+'))
            {
                throw new FormatException($"'{input}' is not a valid SemVer 2.0.0 version.");
            }

            buildMetadata = coreAndPreRelease[(plusIndex + 1)..].ToString();
            ValidateIdentifiers(buildMetadata, allowNumericLeadingZeros: true, input);
            coreAndPreRelease = coreAndPreRelease[..plusIndex];
        }

        string? preRelease = null;
        var dashIndex = coreAndPreRelease.IndexOf('-');
        if (dashIndex >= 0)
        {
            if (dashIndex == coreAndPreRelease.Length - 1)
            {
                throw new FormatException($"'{input}' is not a valid SemVer 2.0.0 version.");
            }

            preRelease = coreAndPreRelease[(dashIndex + 1)..].ToString();
            ValidateIdentifiers(preRelease, allowNumericLeadingZeros: false, input);
            coreAndPreRelease = coreAndPreRelease[..dashIndex];
        }

        var core = coreAndPreRelease.ToString().Split('.');
        if (core.Length != 3)
        {
            throw new FormatException($"'{input}' is not a valid SemVer 2.0.0 version.");
        }

        ValidateCoreNumber(core[0], input);
        ValidateCoreNumber(core[1], input);
        ValidateCoreNumber(core[2], input);
        return new SemanticVersion(core[0], core[1], core[2], preRelease, buildMetadata);
    }

    public static bool TryParse(string? input, out SemanticVersion? version)
    {
        if (input == null)
        {
            version = null;
            return false;
        }

        try
        {
            version = Parse(input);
            return true;
        }
        catch (FormatException)
        {
            version = null;
            return false;
        }
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other == null)
        {
            return 1;
        }

        var comparison = CompareNumericIdentifier(Major, other.Major);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareNumericIdentifier(Minor, other.Minor);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = CompareNumericIdentifier(Patch, other.Patch);
        if (comparison != 0)
        {
            return comparison;
        }

        if (PreRelease == null)
        {
            return other.PreRelease == null ? 0 : 1;
        }

        if (other.PreRelease == null)
        {
            return -1;
        }

        return ComparePreRelease(PreRelease, other.PreRelease);
    }

    public override string ToString()
    {
        var value = $"{Major}.{Minor}.{Patch}";
        if (PreRelease != null)
        {
            value += $"-{PreRelease}";
        }

        if (BuildMetadata != null)
        {
            value += $"+{BuildMetadata}";
        }

        return value;
    }

    public static bool operator <(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) < 0;

    public static bool operator >(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) > 0;

    public static bool operator <=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) <= 0;

    public static bool operator >=(SemanticVersion left, SemanticVersion right) => left.CompareTo(right) >= 0;

    private static void ValidateCoreNumber(string value, string input)
    {
        if (!IsNumeric(value) || value.Length > 1 && value[0] == '0')
        {
            throw new FormatException($"'{input}' is not a valid SemVer 2.0.0 version.");
        }
    }

    private static void ValidateIdentifiers(string value, bool allowNumericLeadingZeros, string input)
    {
        foreach (var identifier in value.Split('.'))
        {
            if (identifier.Length == 0 || identifier.Any(static character =>
                    character is not (>= '0' and <= '9') and
                    not (>= 'A' and <= 'Z') and
                    not (>= 'a' and <= 'z') and
                    not '-'))
            {
                throw new FormatException($"'{input}' is not a valid SemVer 2.0.0 version.");
            }

            if (!allowNumericLeadingZeros && identifier.Length > 1 && identifier[0] == '0' && IsNumeric(identifier))
            {
                throw new FormatException($"'{input}' is not a valid SemVer 2.0.0 version.");
            }
        }
    }

    private static int ComparePreRelease(string left, string right)
    {
        var leftIdentifiers = left.Split('.');
        var rightIdentifiers = right.Split('.');
        var count = Math.Min(leftIdentifiers.Length, rightIdentifiers.Length);
        for (var index = 0; index < count; index++)
        {
            var leftIdentifier = leftIdentifiers[index];
            var rightIdentifier = rightIdentifiers[index];
            var leftIsNumeric = IsNumeric(leftIdentifier);
            var rightIsNumeric = IsNumeric(rightIdentifier);
            int comparison;
            if (leftIsNumeric && rightIsNumeric)
            {
                comparison = CompareNumericIdentifier(leftIdentifier, rightIdentifier);
            }
            else if (leftIsNumeric)
            {
                return -1;
            }
            else if (rightIsNumeric)
            {
                return 1;
            }
            else
            {
                comparison = string.CompareOrdinal(leftIdentifier, rightIdentifier);
            }

            if (comparison != 0)
            {
                return comparison;
            }
        }

        return leftIdentifiers.Length.CompareTo(rightIdentifiers.Length);
    }

    private static int CompareNumericIdentifier(string left, string right)
    {
        var lengthComparison = left.Length.CompareTo(right.Length);
        return lengthComparison != 0 ? lengthComparison : string.CompareOrdinal(left, right);
    }

    private static bool IsNumeric(string value) =>
        value.Length > 0 && value.All(static character => character is >= '0' and <= '9');
}
