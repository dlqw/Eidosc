using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Eidosc.Debug;

internal static class DebugMessages
{
    private static readonly ResourceManager Resources = new(
        "Eidosc.Debug.DebugResources",
        Assembly.GetExecutingAssembly());

    public static string SourceSpanPrefix(int line, int column) =>
        Format(nameof(SourceSpanPrefix), line, column);

    public static string SourceSpanMessage(int line, int column, string message) =>
        Format(nameof(SourceSpanMessage), line, column, message);

    public static string StartingPhase(string phase) =>
        Format(nameof(StartingPhase), phase);

    public static string FinishedPhase(string phase) =>
        Format(nameof(FinishedPhase), phase);

    public static string PhaseStartedLogLine(string timestamp, string phase) =>
        Format(nameof(PhaseStartedLogLine), timestamp, phase);

    public static string PhaseEndedLogLine(string timestamp, string phase) =>
        Format(nameof(PhaseEndedLogLine), timestamp, phase);

    private static string Get(string name) =>
        Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Format(string name, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(name), args);
}
