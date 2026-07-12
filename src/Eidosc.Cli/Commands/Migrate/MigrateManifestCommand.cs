using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using Eidosc.ProjectSystem;

namespace Eidosc.Cli.Commands.Migrate;

public sealed class MigrateManifestOptions
{
    public string Path { get; set; } = ".";
    public bool DryRun { get; set; }
}

public static class MigrateManifestCommand
{
    public static Command Create()
    {
        var path = new Argument<string>("path", () => ".", "Project directory or eidos.toml to migrate.");
        var dryRun = new Option<bool>("--dry-run", "Print the migrated manifest without writing it.");
        var command = new Command("manifest", "Migrate a legacy manifest to schema 3 and Eidos language SemVer.")
        {
            path,
            dryRun
        };
        command.Handler = CommandHandler.Create<MigrateManifestOptions>(Run);
        return command;
    }

    internal static int Run(MigrateManifestOptions options)
    {
        var path = Path.GetFullPath(options.Path);
        var manifestPath = Directory.Exists(path)
            ? Path.Combine(path, EidosProjectConfigurationLoader.DefaultFileName)
            : path;
        if (!File.Exists(manifestPath))
        {
            Console.Error.WriteLine($"Project manifest not found: {manifestPath}");
            return 1;
        }

        var migrated = MigrateText(File.ReadAllText(manifestPath));
        if (options.DryRun)
        {
            Console.Write(migrated);
            return 0;
        }

        File.WriteAllText(manifestPath, migrated);
        Console.WriteLine($"Migrated manifest: {manifestPath}");
        return 0;
    }

    internal static string MigrateText(string text)
    {
        var sourceLines = text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var output = new List<string>();
        string? section = null;
        var wroteSchema = false;
        var sawLanguage = false;
        var wroteLanguageVersion = false;

        foreach (var rawLine in sourceLines)
        {
            var trimmed = rawLine.Split('#', 2)[0].Trim();
            if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
            {
                if (string.Equals(section, "language", StringComparison.Ordinal) && !wroteLanguageVersion)
                {
                    output.Add($"version = \"{EidosLanguageVersions.Current}\"");
                    output.Add(string.Empty);
                    wroteLanguageVersion = true;
                }

                section = trimmed.Trim('[', ']').Trim();
                sawLanguage |= string.Equals(section, "language", StringComparison.Ordinal);
                output.Add(rawLine);
                continue;
            }

            if (section == null && trimmed.StartsWith("eidosVersion", StringComparison.Ordinal))
            {
                continue;
            }

            if (section == null && trimmed.StartsWith("manifestSchema", StringComparison.Ordinal))
            {
                output.Add("manifestSchema = 3");
                wroteSchema = true;
                continue;
            }

            if (string.Equals(section, "language", StringComparison.Ordinal) &&
                (trimmed.StartsWith("syntax", StringComparison.Ordinal) ||
                 trimmed.StartsWith("version", StringComparison.Ordinal)))
            {
                output.Add($"version = \"{EidosLanguageVersions.Current}\"");
                wroteLanguageVersion = true;
                continue;
            }

            output.Add(rawLine);
        }

        while (output.Count > 0 && string.IsNullOrWhiteSpace(output[^1]))
        {
            output.RemoveAt(output.Count - 1);
        }

        if (string.Equals(section, "language", StringComparison.Ordinal) && !wroteLanguageVersion)
        {
            output.Add($"version = \"{EidosLanguageVersions.Current}\"");
            wroteLanguageVersion = true;
        }

        if (!sawLanguage)
        {
            output.Add(string.Empty);
            output.Add("[language]");
            output.Add($"version = \"{EidosLanguageVersions.Current}\"");
        }

        if (!wroteSchema)
        {
            output.Insert(0, string.Empty);
            output.Insert(0, "manifestSchema = 3");
        }

        return string.Join(Environment.NewLine, output).TrimEnd() + Environment.NewLine;
    }
}
