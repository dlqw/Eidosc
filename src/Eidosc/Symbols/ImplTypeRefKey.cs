using System.Collections.Immutable;

namespace Eidosc.Symbols;

/// <summary>
/// Identifies a type reference used in an impl lookup key.
/// </summary>
public readonly record struct ImplTypeRefKey(
    SymbolId SymbolId,
    TypeId TypeId,
    string Text,
    ImmutableArray<ImplTypeRefKey> TypeArguments) : IEquatable<ImplTypeRefKey>
{
    public static readonly ImplTypeRefKey Empty = new(SymbolId.None, TypeId.None, "", []);

    public static ImplTypeRefKey FromText(string? text) =>
        new(SymbolId.None, TypeId.None, NormalizeText(text), []);

    public static ImplTypeRefKey FromCanonicalText(string? text)
    {
        var normalized = NormalizeText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return Empty;
        }

        var bracketIndex = normalized.IndexOf('[');
        if (bracketIndex <= 0 || !normalized.EndsWith("]", StringComparison.Ordinal))
        {
            return FromText(normalized);
        }

        var head = normalized[..bracketIndex];
        var payload = normalized.Substring(bracketIndex + 1, normalized.Length - bracketIndex - 2);
        var typeArguments = SplitTopLevelCommaSeparated(payload)
            .Select(FromCanonicalText)
            .Where(static key => !key.IsEmpty)
            .ToImmutableArray();
        return new ImplTypeRefKey(SymbolId.None, TypeId.None, head, typeArguments);
    }

    /// <summary>
    /// Gets a value indicating whether this key carries no structured or textual identity.
    /// </summary>
    public bool IsEmpty =>
        IsDefaultValue ||
        (!SymbolId.IsValid &&
        !TypeId.IsValid &&
        string.IsNullOrWhiteSpace(Text) &&
        TypeArguments.IsDefaultOrEmpty);

    public bool Equals(ImplTypeRefKey other)
    {
        if (IsEmpty || other.IsEmpty)
        {
            return IsEmpty && other.IsEmpty;
        }

        if (TypeId.IsValid && other.TypeId.IsValid)
        {
            return TypeId == other.TypeId &&
                   TypeArgumentsEqual(TypeArguments, other.TypeArguments);
        }

        if (TypeId.IsValid || other.TypeId.IsValid)
        {
            return false;
        }

        if (SymbolId.IsValid && other.SymbolId.IsValid)
        {
            return SymbolId == other.SymbolId &&
                   TypeArgumentsEqual(TypeArguments, other.TypeArguments);
        }

        if (SymbolId.IsValid || other.SymbolId.IsValid)
        {
            return false;
        }

        return string.Equals(Text, other.Text, StringComparison.Ordinal) &&
               TypeArgumentsEqual(TypeArguments, other.TypeArguments);
    }

    public override int GetHashCode()
    {
        if (IsEmpty)
        {
            return 0;
        }

        var hash = new HashCode();
        if (TypeId.IsValid)
        {
            hash.Add(TypeId);
        }
        else if (SymbolId.IsValid)
        {
            hash.Add(SymbolId);
        }
        else
        {
            hash.Add(Text, StringComparer.Ordinal);
        }

        foreach (var typeArgument in TypeArguments)
        {
            hash.Add(typeArgument);
        }

        return hash.ToHashCode();
    }

    public override string ToString()
    {
        if (IsEmpty)
        {
            return string.Empty;
        }

        var head = TypeId.IsValid
            ? TypeId.ToString()
            : SymbolId.IsValid
                ? SymbolId.ToString()
                : Text;
        return TypeArguments.IsDefaultOrEmpty
            ? head
            : $"{head}[{string.Join(",", TypeArguments.Select(static arg => arg.ToString()))}]";
    }

    private bool IsDefaultValue =>
        SymbolId.Value == default &&
        TypeId.Value == default &&
        Text == null &&
        TypeArguments.IsDefault;

    private static bool TypeArgumentsEqual(
        ImmutableArray<ImplTypeRefKey> left,
        ImmutableArray<ImplTypeRefKey> right)
    {
        if (left.IsDefaultOrEmpty && right.IsDefaultOrEmpty)
        {
            return true;
        }

        if (left.IsDefault || right.IsDefault || left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!left[i].Equals(right[i]))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private static List<string> SplitTopLevelCommaSeparated(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '[':
                case '(':
                case '{':
                    depth++;
                    break;
                case ']':
                case ')':
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    AddPart(text, start, i, result);
                    start = i + 1;
                    break;
            }
        }

        AddPart(text, start, text.Length, result);
        return result;
    }

    private static void AddPart(string text, int start, int end, List<string> result)
    {
        var part = text[start..end];
        if (part.Length > 0)
        {
            result.Add(part);
        }
    }
}
