namespace Eidosc.Diagnostic;

internal static partial class DiagnosticMessages
{
    public static string PublicClosedCaseRootCannotContainInternalDescendant(
        string rootName,
        string caseName) =>
        Format(nameof(PublicClosedCaseRootCannotContainInternalDescendant), rootName, caseName);
}
