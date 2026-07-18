using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Eidosc.ProjectSystem;

namespace Eidosc.Cli.Commands.Migrate;

public sealed class MigrateClausesOptions
{
    public string Path { get; set; } = ".";
    public string To { get; set; } = EidosLanguageVersions.Current;
    public bool DryRun { get; set; }
    public string? Report { get; set; }
}

public static class MigrateClausesCommand
{
    public static Command Create()
    {
        var command = new Command(
            "clauses",
            "Migrate 0.6 declaration attributes to the 0.7 typed pre-body clause model.")
        {
            new Argument<string>(
                "path",
                getDefaultValue: () => ".",
                description: "Project directory, eidos.toml, or .eidos source file to migrate."),
            new Option<string>("--to", () => EidosLanguageVersions.Current, "Target language version."),
            new Option<bool>("--dry-run", "Print and report the clause migration plan without rewriting files."),
            new Option<string?>("--report", "Write a JSON clause migration plan report.")
        };

        command.Handler = CommandHandler.Create<MigrateClausesOptions>(Run);
        return command;
    }

    internal static int Run(MigrateClausesOptions options)
    {
        if (!string.Equals(options.To, EidosLanguageVersions.Current, StringComparison.Ordinal))
        {
            Console.Error.WriteLine(
                $"Clause migration target must be '{EidosLanguageVersions.Current}', but received '{options.To}'.");
            return 2;
        }

        return MigrateSyntaxCommand.Run(new MigrateSyntaxOptions
        {
            Path = options.Path,
            From = EidosLanguageVersions.Previous,
            To = options.To,
            DryRun = options.DryRun,
            Report = options.Report
        });
    }
}
