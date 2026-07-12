namespace Eidosc.Utils;

public static class ReservedInternalNames
{
    public static bool TryMatch(string name, out string reservedPrefix)
    {
        reservedPrefix = string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.StartsWith(WellKnownStrings.InternalNames.ModuleSeparator, StringComparison.Ordinal))
        {
            reservedPrefix = WellKnownStrings.InternalNames.ModuleSeparator;
            return true;
        }

        if (name.Contains(WellKnownStrings.InternalNames.SpecializationMarker, StringComparison.Ordinal))
        {
            reservedPrefix = WellKnownStrings.InternalNames.SpecializationMarker;
            return true;
        }

        return false;
    }
}
