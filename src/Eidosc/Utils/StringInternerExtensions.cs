using System.Runtime.CompilerServices;
using Eidosc.Utils;

namespace Eidosc.Utilities;

public static class StringInternerExtensions
{
    private static readonly StringInterner Default = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringId GetOrIntern(this ReadOnlySpan<char> span)
    {
        return Default.GetOrIntern(span);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringId GetOrIntern(this string str)
    {
        return Default.GetOrIntern(str);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static StringId GetOrIntern(this char str)
    {
        return Default.GetOrIntern(str.ToString());
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string Resolve(this StringId id)
    {
        return Default.Resolve(id);
    }
}