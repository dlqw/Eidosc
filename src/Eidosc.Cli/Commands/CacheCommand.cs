using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Text.Json;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;

namespace Eidosc.Cli.Commands;

public static class CacheCommand
{
    public static Command Create()
    {
        var command = new Command("cache", "Inspect and manage the project build cache.")
        {
            CreateStatusCommand(),
            CreateCleanCommand(),
            CreatePruneCommand()
        };
        return command;
    }

    private static Command CreateStatusCommand()
    {
        var command = new Command("status", "Show project cache size and artifact counts.")
        {
            new Argument<string>("path", () => "", "Project directory, eidos.toml, or .eidos source path."),
            new Option<bool>("--json", "Write machine-readable JSON output.")
        };
        command.Handler = CommandHandler.Create<string, bool>(ExecuteStatus);
        return command;
    }

    private static Command CreateCleanCommand()
    {
        var command = new Command("clean", "Delete all cached project artifacts.")
        {
            new Argument<string>("path", () => "", "Project directory, eidos.toml, or .eidos source path."),
            new Option<bool>("--json", "Write machine-readable JSON output.")
        };
        command.Handler = CommandHandler.Create<string, bool>(ExecuteClean);
        return command;
    }

    private static Command CreatePruneCommand()
    {
        var maxMiBOption = new Option<int>("--max-mib", () => 512, "Target maximum cache size in MiB.");
        maxMiBOption.AddValidator(result =>
        {
            if (result.GetValueOrDefault<int>() < 0)
            {
                result.ErrorMessage = "--max-mib cannot be negative.";
            }
        });
        var command = new Command("prune", "Remove oldest cached artifacts until the size limit is met.")
        {
            new Argument<string>("path", () => "", "Project directory, eidos.toml, or .eidos source path."),
            maxMiBOption,
            new Option<bool>("--json", "Write machine-readable JSON output.")
        };
        command.Handler = CommandHandler.Create<string, int, bool>(ExecutePrune);
        return command;
    }

    private static int ExecuteStatus(string path, bool json)
    {
        var cache = CreateCache(path);
        var status = cache.GetStatus();
        Write(status, json, static value =>
        {
            Console.WriteLine($"Cache: {value.CacheDirectory}");
            Console.WriteLine($"Size: {FormatBytes(value.TotalBytes)}");
            Console.WriteLine($"Artifacts: {value.ArtifactManifests}");
            Console.WriteLine($"Payloads: {value.PayloadFiles}");
            Console.WriteLine($"Orphan payloads: {value.OrphanPayloadFiles}");
        });
        return 0;
    }

    private static int ExecuteClean(string path, bool json)
    {
        var cache = CreateCache(path);
        var result = cache.Clear();
        Write(result, json, static value =>
            Console.WriteLine($"Deleted {value.DeletedFiles} files ({FormatBytes(value.DeletedBytes)})."));
        return 0;
    }

    private static int ExecutePrune(string path, int maxMib, bool json)
    {
        var cache = CreateCache(path);
        var result = cache.Prune((long)maxMib * 1024 * 1024);
        Write(result, json, static value =>
            Console.WriteLine(
                $"Pruned {value.DeletedFiles} files ({FormatBytes(value.DeletedBytes)}); " +
                $"cache is now {FormatBytes(value.BytesAfter)}."));
        return 0;
    }

    private static ModuleArtifactCache CreateCache(string path)
    {
        var projectDirectory = ResolveProjectDirectory(path);
        return new ModuleArtifactCache(Path.Combine(projectDirectory, "build", ".eidos-cache"));
    }

    internal static string ResolveProjectDirectory(string path)
    {
        var candidate = string.IsNullOrWhiteSpace(path)
            ? Directory.GetCurrentDirectory()
            : Path.GetFullPath(path);
        if (File.Exists(candidate))
        {
            candidate = string.Equals(Path.GetFileName(candidate), EidosProjectConfigurationLoader.DefaultFileName, StringComparison.OrdinalIgnoreCase)
                ? Path.GetDirectoryName(candidate)!
                : Path.GetDirectoryName(candidate)!;
        }

        var directory = new DirectoryInfo(candidate);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, EidosProjectConfigurationLoader.DefaultFileName)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return Path.GetFullPath(candidate);
    }

    private static void Write<T>(T value, bool json, Action<T> writeText)
    {
        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
            return;
        }

        writeText(value);
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KiB", "MiB", "GiB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
