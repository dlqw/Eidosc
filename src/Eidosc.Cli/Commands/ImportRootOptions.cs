using System.CommandLine;
using Eidosc.Cli.Resources;

namespace Eidosc.Cli.Commands;

internal static class ImportRootOptions
{
    public static Option<string[]> Create()
    {
        return new Option<string[]>(
            "--import-root",
            CliMessages.CliImportRootOptionDescription);
    }
}
