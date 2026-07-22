using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Text.Json;
using Eidosc.ProjectSystem;

namespace Eidosc.Cli.Commands.Migrate;

public sealed class MigrateNamesOptions
{
    public string Path { get; set; } = ".";
    public string? DependencyAlias { get; set; }
    public bool Modules { get; set; }
    public bool NoPathDependencies { get; set; }
    public bool DryRun { get; set; }
    public string? Report { get; set; }
}

public static class MigrateNamesCommand
{
    public static Command Create()
    {
        var path = new Argument<string>(
            "path",
            () => ".",
            "Project directory or eidos.toml whose dependency identities should be renamed.");
        var dependencyAlias = new Option<string?>(
            "--dependency-alias",
            "Rename one root dependency alias as OLD=NEW; otherwise normalize every non-canonical alias.");
        var modules = new Option<bool>(
            "--modules",
            "Normalize module directory/file identities and all semantic paths.");
        var noPathDependencies = new Option<bool>(
            "--no-path-dependencies",
            "Do not traverse local path-dependency manifests.");
        var dryRun = new Option<bool>(
            "--dry-run",
            "Print the semantic rename plan without writing files.");
        var report = new Option<string?>(
            "--report",
            "Write the semantic rename plan as JSON.");
        var command = new Command(
            "names",
            "Rename dependency aliases through manifest identity and parsed Eidos paths.")
        {
            path,
            dependencyAlias,
            modules,
            noPathDependencies,
            dryRun,
            report
        };
        command.Handler = CommandHandler.Create<MigrateNamesOptions>(Run);
        return command;
    }

    internal static int Run(MigrateNamesOptions options)
    {
        if (options.Modules && !string.IsNullOrWhiteSpace(options.DependencyAlias))
        {
            Console.Error.WriteLine("--modules and --dependency-alias are mutually exclusive.");
            return 2;
        }

        if (options.Modules)
        {
            return RunModuleIdentityRename(options);
        }

        string? oldAlias = null;
        string? newAlias = null;
        if (!string.IsNullOrWhiteSpace(options.DependencyAlias) &&
            !TryParseAliasRename(options.DependencyAlias, out oldAlias, out newAlias))
        {
            Console.Error.WriteLine(
                "--dependency-alias must use OLD=NEW with non-empty aliases.");
            return 2;
        }

        DependencyAliasRenamePlan plan;
        try
        {
            plan = DependencyAliasRenamePlanner.CreatePlan(
                options.Path,
                oldAlias,
                newAlias,
                includePathDependencies: !options.NoPathDependencies);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        WriteSummary(plan);
        if (!string.IsNullOrWhiteSpace(options.Report))
        {
            WriteReport(options.Report, plan);
        }

        if (!plan.CanApply)
        {
            foreach (var diagnostic in plan.Diagnostics)
            {
                Console.Error.WriteLine(diagnostic);
            }

            return 1;
        }

        if (options.DryRun || string.Equals(plan.Status, "unchanged", StringComparison.Ordinal))
        {
            return 0;
        }

        try
        {
            DependencyAliasRenamePlanner.ApplyPlan(plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"Renamed dependency aliases in {plan.Packages.Length} package(s).");
        return 0;
    }

    private static int RunModuleIdentityRename(MigrateNamesOptions options)
    {
        ModuleIdentityRenamePlan plan;
        try
        {
            plan = ModuleIdentityRenamePlanner.CreatePlan(
                options.Path,
                includePathDependencies: !options.NoPathDependencies);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"Module identity rename plan: {plan.Status}");
        Console.WriteLine($"Root manifest: {plan.RootManifestPath}");
        Console.WriteLine($"Packages: {plan.Packages.Length}");
        Console.WriteLine($"Moves: {plan.TotalMoveCount}");
        Console.WriteLine($"Edits: {plan.TotalEditCount}");
        if (!string.IsNullOrWhiteSpace(options.Report))
        {
            WriteReport(options.Report, plan);
        }

        if (!plan.CanApply)
        {
            foreach (var diagnostic in plan.Diagnostics)
            {
                Console.Error.WriteLine(diagnostic);
            }
            return 1;
        }

        if (options.DryRun || string.Equals(plan.Status, "unchanged", StringComparison.Ordinal))
        {
            return 0;
        }

        try
        {
            ModuleIdentityRenamePlanner.ApplyPlan(plan);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"Renamed module identities in {plan.Packages.Length} package(s).");
        return 0;
    }

    internal static bool TryParseAliasRename(
        string value,
        out string? oldAlias,
        out string? newAlias)
    {
        oldAlias = null;
        newAlias = null;
        var separator = value.IndexOf('=');
        if (separator <= 0 || separator == value.Length - 1 ||
            value.IndexOf('=', separator + 1) >= 0)
        {
            return false;
        }

        oldAlias = value[..separator].Trim();
        newAlias = value[(separator + 1)..].Trim();
        return oldAlias.Length > 0 && newAlias.Length > 0;
    }

    private static void WriteSummary(DependencyAliasRenamePlan plan)
    {
        Console.WriteLine($"Dependency alias rename plan: {plan.Status}");
        Console.WriteLine($"Root manifest: {plan.RootManifestPath}");
        Console.WriteLine($"Packages: {plan.Packages.Length}");
        Console.WriteLine($"Edits: {plan.TotalEditCount}");
        foreach (var package in plan.Packages)
        {
            var renames = package.AliasRenames.Count == 0
                ? "<none>"
                : string.Join(
                    ", ",
                    package.AliasRenames.Select(static rename => $"{rename.Key}->{rename.Value}"));
            Console.WriteLine(
                $"  {package.ManifestPath}: {renames}; source files={package.Sources.Length}; edits={package.TotalEditCount}");
        }
    }

    private static void WriteReport<TPlan>(string reportPath, TPlan plan)
    {
        var fullPath = Path.GetFullPath(reportPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            fullPath,
            JsonSerializer.Serialize(
                plan,
                new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"Report: {fullPath}");
    }
}
