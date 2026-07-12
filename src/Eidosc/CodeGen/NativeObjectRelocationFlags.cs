namespace Eidosc.CodeGen;

internal sealed record NativeObjectRelocationFlags(
    IReadOnlyList<string> LlcFlags,
    IReadOnlyList<string> ClangFlags)
{
    public static NativeObjectRelocationFlags Empty { get; } = new([], []);
}
