using System.CommandLine;
using Eidosc.Cli.Resources;
using Eidosc.Diagnostic;

namespace Eidosc.Cli.Commands;

internal static class MirOptimizationOptions
{
    public static Option<bool> CreateEnableOption()
    {
        return new Option<bool>("--mir-opt", CliMessages.MirOptimizationEnableOptionDescription);
    }

    public static Option<bool> CreateDisableOption()
    {
        return new Option<bool>("--no-mir-opt", CliMessages.MirOptimizationDisableOptionDescription);
    }

    public static bool IsEnabled(bool noMirOpt)
    {
        return !noMirOpt;
    }

    public static void WriteStatus(bool noMirOpt, bool useColors)
    {
        CliOutput.WriteStatus(
            DiagnosticLevel.Note,
            IsEnabled(noMirOpt)
                ? CliMessages.MirOptimizationEnabledStatus
                : CliMessages.MirOptimizationDisabledStatus,
            useColors);
    }
}
