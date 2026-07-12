using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Eidosc.Cli.Resources;

namespace Eidosc.Cli.Commands;

public static class NewCommand
{
    public static Command Create()
    {
        var command = new Command("new", CliMessages.NewCommandDescription)
        {
            new Argument<string>("path", CliMessages.NewPathArgumentDescription),
            new Option<string>("--name", CliMessages.NewNameOptionDescription),
            new Option<string>("--version", () => "0.1.0", CliMessages.NewVersionOptionDescription),
            new Option<string>("--kind", () => "executable", CliMessages.NewKindOptionDescription),
            new Option<string[]>("--source-root", CliMessages.NewSourceRootOptionDescription),
            new Option<string>("--description", CliMessages.NewDescriptionOptionDescription),
            new Option<string>("--license", CliMessages.NewLicenseOptionDescription)
        };

        command.Handler = CommandHandler.Create<NewOptions>(Execute);
        return command;
    }

    private sealed class NewOptions
    {
        public string Path { get; set; } = "";
        public string? Name { get; set; }
        public string Version { get; set; } = "0.1.0";
        public string Kind { get; set; } = "executable";
        public string[] SourceRoot { get; set; } = [];
        public string? Description { get; set; }
        public string? License { get; set; }
    }

    private static int Execute(NewOptions options)
    {
        var targetDirectory = System.IO.Path.GetFullPath(options.Path);
        if (File.Exists(targetDirectory))
        {
            CliOutput.WriteStatus(
                Diagnostic.DiagnosticLevel.Error,
                CliMessages.NewTargetPathIsFile(targetDirectory),
                useColors: true);
            return 1;
        }

        if (Directory.Exists(targetDirectory) && Directory.EnumerateFileSystemEntries(targetDirectory).Any())
        {
            CliOutput.WriteStatus(
                Diagnostic.DiagnosticLevel.Error,
                CliMessages.NewTargetDirectoryNotEmpty(targetDirectory),
                useColors: true);
            CliOutput.WriteStatus(
                Diagnostic.DiagnosticLevel.Help,
                CliMessages.NewExistingDirectoryInitHelp,
                useColors: true);
            return 1;
        }

        var directoryName = System.IO.Path.GetFileName(
            targetDirectory.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar));
        return EidosProjectInitializer.Initialize(
            targetDirectory,
            new EidosProjectInitOptions
            {
                Name = options.Name,
                Version = options.Version,
                Kind = options.Kind,
                SourceRoot = options.SourceRoot,
                Description = options.Description,
                License = options.License
            },
            "new",
            useColors: true,
            defaultPackageName: string.IsNullOrWhiteSpace(directoryName) ? null : directoryName);
    }
}
