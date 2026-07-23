namespace Eidosc.Ast;

public static class SelectionPlaceholderSyntax
{
    public static bool LooksLikePlaceholder(string? name) =>
        !string.IsNullOrEmpty(name) &&
        name.Length > 1 &&
        name[0] == '_' &&
        name.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0;

    public static bool TryParse(string? name, out int index, out bool hasLeadingZero)
    {
        index = -1;
        hasLeadingZero = false;
        if (!LooksLikePlaceholder(name))
        {
            return false;
        }

        hasLeadingZero = name!.Length > 2 && name[1] == '0';
        return !hasLeadingZero && int.TryParse(name.AsSpan(1), out index);
    }
}
