using Eidosc.ProjectSystem;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using Eidosc.Cli.Resources;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands.Pkg;

public static class PkgAddCommand
{
    public static Command Create()
    {
        var command = new Command("add", CliMessages.PkgAddCommandDescription)
        {
            new Argument<string>("name", CliMessages.PkgDependencyNameArgumentDescription),
            new Option<string?>("--path", CliMessages.PkgPathOptionDescription),
            new Option<string?>("--git", CliMessages.PkgGitOptionDescription),
            new Option<string?>("--tag", CliMessages.PkgTagOptionDescription),
            new Option<string?>("--branch", CliMessages.PkgBranchOptionDescription),
            new Option<string?>("--commit", CliMessages.PkgCommitOptionDescription),
            new Option<string?>("--version", CliMessages.PkgVersionOptionDescription),
        };

        command.Handler = CommandHandler.Create<AddOptions>(Execute);
        return command;
    }

    private sealed class AddOptions
    {
        public string Name { get; set; } = "";
        public string? Path { get; set; }
        public string? Git { get; set; }
        public string? Tag { get; set; }
        public string? Branch { get; set; }
        public string? Commit { get; set; }
        public string? Version { get; set; }
    }

    private static int Execute(AddOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();
        var dir = Directory.GetCurrentDirectory();
        var configPath = Path.Combine(dir, EidosProjectConfigurationLoader.DefaultFileName);
        var dependencyName = GetDisplayDependencyName(options);
        CliOutput.WriteAction(
            CliMessages.PkgAddingAction,
            CliMessages.PkgAddActionSubject(dependencyName, configPath),
            useColors: true);

        if (!File.Exists(configPath))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(CliMessages.PkgManifestMissingInitError);
            Console.ResetColor();
            commandStopwatch.Stop();
            CliOutput.WriteFinished(
                "pkg add",
                false,
                commandStopwatch.Elapsed,
                useColors: true,
                details: CliMessages.PkgManifestMissingDetail);
            return 1;
        }

        var manifest = EidosProjectManifestDocument.Load(configPath);
        var deps = manifest.GetOrCreateDependencies();
        var (name, spec) = CreateDependencySpec(options);

        deps[name] = spec;
        manifest.Save(configPath);
        CliOutput.WriteArtifact(CliMessages.ArtifactKindManifest, configPath, useColors: true);
        Console.WriteLine(CliMessages.PkgAddedDependency(name));
        commandStopwatch.Stop();
        CliOutput.WriteFinished(
            "pkg add",
            true,
            commandStopwatch.Elapsed,
            useColors: true,
            details: CliMessages.PkgDependencyDetail(name));

        return 0;
    }

    private static string GetDisplayDependencyName(AddOptions options)
    {
        if (options.Name.StartsWith("github:", StringComparison.OrdinalIgnoreCase) &&
            TryParseGitHubSpec(options.Name, out var parsed))
        {
            return parsed.Alias;
        }

        var atIndex = options.Name.LastIndexOf('@');
        return atIndex > 0 ? options.Name[..atIndex] : options.Name;
    }

    private static (string Name, EidosProjectDependencyManifestDocument Spec) CreateDependencySpec(AddOptions options)
    {
        if (options.Path != null)
        {
            return (options.Name, new EidosProjectDependencyManifestDocument { Path = options.Path });
        }

        if (options.Git != null)
        {
            return (options.Name, new EidosProjectDependencyManifestDocument
            {
                Git = options.Git,
                Tag = options.Tag,
                Branch = options.Branch,
                Commit = options.Commit
            });
        }

        if (options.Version != null)
        {
            return (options.Name, new EidosProjectDependencyManifestDocument { Version = options.Version });
        }

        if (options.Name.StartsWith("github:", StringComparison.OrdinalIgnoreCase) &&
            TryParseGitHubSpec(options.Name, out var github))
        {
            return (github.Alias, new EidosProjectDependencyManifestDocument
            {
                Git = github.GitUrl,
                Tag = github.Tag
            });
        }

        var atIndex = options.Name.LastIndexOf('@');
        if (atIndex > 0)
        {
            return (options.Name[..atIndex], new EidosProjectDependencyManifestDocument
            {
                Version = options.Name[(atIndex + 1)..]
            });
        }

        return (options.Name, new EidosProjectDependencyManifestDocument { Version = "*" });
    }

    private static bool TryParseGitHubSpec(string input, out GitHubDependencySpec spec)
    {
        spec = default;
        var rest = input["github:".Length..];
        var atIndex = rest.LastIndexOf('@');
        var repoPart = atIndex >= 0 ? rest[..atIndex] : rest;
        var tag = atIndex >= 0 ? rest[(atIndex + 1)..] : null;
        var segments = repoPart.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length != 2)
            return false;

        var owner = segments[0];
        var repo = segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase)
            ? segments[1][..^4]
            : segments[1];
        spec = new GitHubDependencySpec(
            ToPackageAlias(repo),
            $"https://github.com/{owner}/{repo}.git",
            tag);
        return true;
    }

    private static string ToPackageAlias(string repoName)
    {
        var parts = repoName.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(parts.Select(part =>
            char.ToUpperInvariant(part[0]) + (part.Length == 1 ? "" : part[1..])));
    }

    private readonly record struct GitHubDependencySpec(string Alias, string GitUrl, string? Tag);
}
