using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Installation;
using Eidosup.Proxies;
using Eidosup.Toolchains;

namespace Eidosup.Commands;

internal static class ToolchainCommands
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static Command CreateToolchainCommand()
    {
        var command = new Command("toolchain", "Install, inspect, and uninstall Eidos toolchains.")
        {
            CreateInstallCommand(),
            CreateListCommand(),
            CreateUninstallCommand()
        };
        return command;
    }

    public static Command CreateDefaultCommand()
    {
        var command = new Command("default", "Show, set, or clear the global default toolchain.");
        var specArgument = new Argument<string?>(
            "spec",
            () => null,
            "Installed selector to activate, or 'none' to clear the default.");
        var installRootOption = CreateInstallRootOption();
        var dryRunOption = CreateDryRunOption();
        command.AddArgument(specArgument);
        command.AddOption(installRootOption);
        command.AddOption(dryRunOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            var value = context.ParseResult.GetValueForArgument(specArgument);
            var options = CreateOptions(context, installRootOption);
            var manager = new ToolchainManager();
            if (value == null)
            {
                var state = await manager.ListAsync(options, context.GetCancellationToken());
                WriteDefault(state, context.ParseResult.GetValueForOption(GlobalOptions.Json));
                return;
            }

            var clear = string.Equals(value, "none", StringComparison.OrdinalIgnoreCase);
            var spec = clear ? null : ToolchainSpec.Parse(value);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var updated = await manager.SetDefaultAsync(
                options,
                spec,
                dryRun,
                context.GetCancellationToken());
            if (context.ParseResult.GetValueForOption(GlobalOptions.Json))
            {
                WriteJson(new
                {
                    action = clear ? "clear" : "set",
                    selector = spec?.Canonical,
                    dryRun,
                    state = updated.Default
                });
            }
            else
            {
                Console.WriteLine(dryRun
                    ? clear
                        ? "[dry-run] Would clear the global default toolchain."
                        : $"[dry-run] Would set the global default to '{spec!.Canonical}'."
                    : clear
                        ? "Global default toolchain cleared."
                        : $"Global default toolchain set to '{spec!.Canonical}'.");
            }
        });
        return command;
    }

    public static Command CreateUpdateCommand()
    {
        var command = new Command("update", "Update installed channel selectors or install the requested specifications.");
        var specsArgument = new Argument<string[]>("spec", () => [], "Selectors to update; defaults to all installed channels.")
        {
            Arity = ArgumentArity.ZeroOrMore
        };
        var repositoryOption = CreateRepositoryOption();
        var installRootOption = CreateInstallRootOption();
        var downloadRootOption = CreateDownloadRootOption();
        var dryRunOption = CreateDryRunOption();
        command.AddArgument(specsArgument);
        command.AddOption(repositoryOption);
        command.AddOption(installRootOption);
        command.AddOption(downloadRootOption);
        command.AddOption(dryRunOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            var specs = ParseSpecs(context.ParseResult.GetValueForArgument(specsArgument));
            var options = CreateOptions(context, installRootOption, downloadRootOption, repositoryOption);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var manager = new ToolchainManager();
            var results = await manager.UpdateAsync(
                options,
                specs,
                dryRun,
                new ConsoleDownloadProgressReporter(),
                context.GetCancellationToken());
            WriteInstallResults(results, context.ParseResult.GetValueForOption(GlobalOptions.Json), "update");
        });
        return command;
    }

    public static Command CreateCheckCommand()
    {
        var command = new Command("check", "Check installed selectors against the current release source without changing them.");
        var specsArgument = new Argument<string[]>("spec", () => [], "Selectors to check; defaults to all installed channels.")
        {
            Arity = ArgumentArity.ZeroOrMore
        };
        var repositoryOption = CreateRepositoryOption();
        var installRootOption = CreateInstallRootOption();
        command.AddArgument(specsArgument);
        command.AddOption(repositoryOption);
        command.AddOption(installRootOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            var manager = new ToolchainManager();
            var results = await manager.CheckAsync(
                CreateOptions(context, installRootOption, repositoryOption: repositoryOption),
                ParseSpecs(context.ParseResult.GetValueForArgument(specsArgument)),
                context.GetCancellationToken());
            if (context.ParseResult.GetValueForOption(GlobalOptions.Json))
            {
                WriteJson(new { toolchains = results });
            }
            else if (results.Count == 0)
            {
                Console.WriteLine("No installed channel selectors to check.");
            }
            else
            {
                foreach (var result in results)
                {
                    Console.WriteLine(result.Status switch
                    {
                        ToolchainCheckStatus.Current => $"{result.Spec.Canonical}: current ({result.InstalledVersion})",
                        ToolchainCheckStatus.UpdateAvailable => $"{result.Spec.Canonical}: update available {result.InstalledVersion} -> {result.AvailableVersion}",
                        ToolchainCheckStatus.Conflict => $"{result.Spec.Canonical}: installed exact selector conflicts with the selected release source",
                        _ => $"{result.Spec.Canonical}: not installed; available {result.AvailableVersion}"
                    });
                }
            }
        });
        return command;
    }

    public static Command CreateShowCommand()
    {
        var command = new Command("show", "Show Eidosup home, installed toolchains, and active selection.");
        var installRootOption = CreateInstallRootOption();
        command.AddOption(installRootOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            var options = CreateOptions(context, installRootOption);
            var manager = new ToolchainManager();
            var state = await manager.ListAsync(options, context.GetCancellationToken());
            var layout = ToolInstallLayout.Create(PlatformContext.Detect(), options.InstallRoot, options.DownloadRoot);
            WriteShow(
                state,
                layout,
                context.ParseResult.GetValueForOption(GlobalOptions.Verbose),
                context.ParseResult.GetValueForOption(GlobalOptions.Json));
        });
        command.AddCommand(CreateShowActiveCommand());
        command.AddCommand(CreateShowHomeCommand());
        command.AddCommand(CreateShowProfileCommand());
        return command;
    }

    public static Command CreateRunCommand()
    {
        var command = new Command("run", "Run a command from an explicitly selected toolchain.");
        var specArgument = new Argument<string>("spec", "Installed toolchain selector.");
        var commandArgument = new Argument<string[]>("command", "Command and arguments after '--'.")
        {
            Arity = ArgumentArity.OneOrMore
        };
        var installRootOption = CreateInstallRootOption();
        command.AddArgument(specArgument);
        command.AddArgument(commandArgument);
        command.AddOption(installRootOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            var invocation = context.ParseResult.GetValueForArgument(commandArgument);
            var manager = new ToolchainManager();
            context.ExitCode = await manager.RunAsync(
                CreateOptions(context, installRootOption),
                ToolchainSpec.Parse(context.ParseResult.GetValueForArgument(specArgument)),
                invocation[0],
                invocation.Skip(1).ToArray(),
                context.GetCancellationToken());
        });
        return command;
    }

    public static Command CreateWhichCommand()
    {
        var command = new Command("which", "Resolve a toolchain command to its immutable executable path.");
        var commandArgument = new Argument<string>("command", "Toolchain command name, currently 'eidosc'.");
        var toolchainOption = new Option<string?>("--toolchain", "Resolve through an explicit installed selector.");
        var installRootOption = CreateInstallRootOption();
        command.AddArgument(commandArgument);
        command.AddOption(toolchainOption);
        command.AddOption(installRootOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            var explicitSpec = context.ParseResult.GetValueForOption(toolchainOption);
            var manager = new ToolchainManager();
            var resolved = await manager.ResolveAsync(
                CreateOptions(context, installRootOption),
                context.ParseResult.GetValueForArgument(commandArgument),
                explicitSpec == null ? null : ToolchainSpec.Parse(explicitSpec),
                context.GetCancellationToken());
            if (context.ParseResult.GetValueForOption(GlobalOptions.Json))
            {
                WriteJson(resolved);
            }
            else
            {
                Console.WriteLine(resolved.CommandPath);
                Console.WriteLine($"toolchain: {resolved.ToolchainId}");
                Console.WriteLine($"selector: {resolved.Selector} ({ToKebabCase(resolved.SelectionSource.ToString())})");
            }
        });
        return command;
    }

    public static Command CreateRollbackCommand()
    {
        var command = new Command("rollback", "Move a channel selector to its previous installed verified toolchain.");
        var selectorArgument = new Argument<string?>("selector", () => null, "Channel selector; defaults to the active default selector.");
        var installRootOption = CreateInstallRootOption();
        var dryRunOption = CreateDryRunOption();
        command.AddArgument(selectorArgument);
        command.AddOption(installRootOption);
        command.AddOption(dryRunOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            var value = context.ParseResult.GetValueForArgument(selectorArgument);
            var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
            var manager = new ToolchainManager();
            var result = await manager.RollbackAsync(
                CreateOptions(context, installRootOption),
                value == null ? null : ToolchainSpec.Parse(value),
                dryRun,
                context.GetCancellationToken());
            if (context.ParseResult.GetValueForOption(GlobalOptions.Json))
            {
                WriteJson(result);
            }
            else
            {
                Console.WriteLine(dryRun
                    ? $"[dry-run] Would roll back '{result.Selector}' from '{result.FromToolchainId}' to '{result.ToToolchainId}'."
                    : $"Rolled back '{result.Selector}' from '{result.FromToolchainId}' to '{result.ToToolchainId}'.");
            }
        });
        return command;
    }

    private static Command CreateInstallCommand()
    {
        var command = new Command("install", "Install a stable, preview, or exact-version Eidos toolchain.");
        var specArgument = new Argument<string>("spec", "Toolchain specification: stable, preview, or exact Eidosc SemVer.");
        var repositoryOption = CreateRepositoryOption();
        var installRootOption = CreateInstallRootOption();
        var downloadRootOption = CreateDownloadRootOption();
        var forceOption = new Option<bool>("--force", "Reinstall the immutable toolchain through the verified transaction path.");
        var dryRunOption = CreateDryRunOption();
        command.AddArgument(specArgument);
        command.AddOption(repositoryOption);
        command.AddOption(installRootOption);
        command.AddOption(downloadRootOption);
        command.AddOption(forceOption);
        command.AddOption(dryRunOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            var manager = new ToolchainManager();
            var result = await manager.InstallAsync(
                CreateOptions(context, installRootOption, downloadRootOption, repositoryOption),
                ToolchainSpec.Parse(context.ParseResult.GetValueForArgument(specArgument)),
                context.ParseResult.GetValueForOption(forceOption),
                context.ParseResult.GetValueForOption(dryRunOption),
                new ConsoleDownloadProgressReporter(),
                context.GetCancellationToken());
            WriteInstallResults([result], context.ParseResult.GetValueForOption(GlobalOptions.Json), "install");
        });
        return command;
    }

    private static Command CreateListCommand()
    {
        var command = new Command("list", "List verified installed toolchains and their selectors.");
        var installRootOption = CreateInstallRootOption();
        command.AddOption(installRootOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            var manager = new ToolchainManager();
            var state = await manager.ListAsync(
                CreateOptions(context, installRootOption),
                context.GetCancellationToken());
            WriteList(
                state,
                context.ParseResult.GetValueForOption(GlobalOptions.Verbose),
                context.ParseResult.GetValueForOption(GlobalOptions.Json));
        });
        return command;
    }

    private static Command CreateUninstallCommand()
    {
        var command = new Command("uninstall", "Uninstall one or more inactive immutable toolchains.");
        var specsArgument = new Argument<string[]>("spec", "Installed selectors to uninstall.")
        {
            Arity = ArgumentArity.OneOrMore
        };
        var installRootOption = CreateInstallRootOption();
        var dryRunOption = CreateDryRunOption();
        command.AddArgument(specsArgument);
        command.AddOption(installRootOption);
        command.AddOption(dryRunOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            var manager = new ToolchainManager();
            var result = await manager.UninstallAsync(
                CreateOptions(context, installRootOption),
                ParseSpecs(context.ParseResult.GetValueForArgument(specsArgument)),
                context.ParseResult.GetValueForOption(dryRunOption),
                context.GetCancellationToken());
            if (context.ParseResult.GetValueForOption(GlobalOptions.Json))
            {
                WriteJson(result);
            }
            else
            {
                foreach (var id in result.ToolchainIds)
                {
                    Console.WriteLine(result.DryRun
                        ? $"[dry-run] Would uninstall '{id}'."
                        : $"Uninstalled '{id}'.");
                }
            }
        });
        return command;
    }

    private static Command CreateShowActiveCommand()
    {
        var command = new Command("active-toolchain", "Show the active default toolchain and selector source.");
        var installRootOption = CreateInstallRootOption();
        command.AddOption(installRootOption);
        command.SetHandler(async (InvocationContext context) =>
        {
            var manager = new ToolchainManager();
            var resolved = await manager.ResolveAsync(
                CreateOptions(context, installRootOption),
                "eidosc",
                spec: null,
                context.GetCancellationToken());
            if (context.ParseResult.GetValueForOption(GlobalOptions.Json))
            {
                WriteJson(resolved);
            }
            else
            {
                Console.WriteLine($"{resolved.Selector} ({resolved.ToolchainId})");
                Console.WriteLine($"source: {ToKebabCase(resolved.SelectionSource.ToString())}");
                Console.WriteLine($"path: {resolved.ToolchainDirectory}");
                if (context.ParseResult.GetValueForOption(GlobalOptions.Verbose))
                {
                    Console.WriteLine($"command: {resolved.CommandPath}");
                    Console.WriteLine($"runtime: {resolved.RuntimePath}");
                    Console.WriteLine($"home: {resolved.RootDirectory}");
                }
            }
        });
        return command;
    }

    private static Command CreateShowHomeCommand()
    {
        var command = new Command("home", "Show EIDOS_HOME used by Eidosup.");
        var installRootOption = CreateInstallRootOption();
        command.AddOption(installRootOption);
        command.SetHandler((InvocationContext context) =>
        {
            var options = CreateOptions(context, installRootOption);
            var layout = ToolInstallLayout.Create(PlatformContext.Detect(), options.InstallRoot, options.DownloadRoot);
            if (context.ParseResult.GetValueForOption(GlobalOptions.Json))
            {
                WriteJson(new { home = layout.RootDirectory });
            }
            else
            {
                Console.WriteLine(layout.RootDirectory);
            }
        });
        return command;
    }

    private static Command CreateShowProfileCommand()
    {
        var command = new Command("profile", "Show the fixed bootstrap profile used before component profiles are introduced.");
        command.SetHandler((InvocationContext context) =>
        {
            if (context.ParseResult.GetValueForOption(GlobalOptions.Json))
            {
                WriteJson(new { profile = "default", configurable = false });
            }
            else
            {
                Console.WriteLine("default");
            }
        });
        return command;
    }

    private static void WriteInstallResults(
        IReadOnlyList<ToolchainInstallOutcome> results,
        bool json,
        string action)
    {
        if (json)
        {
            WriteJson(new
            {
                action,
                toolchains = results.Select(result => new
                {
                    selector = result.Spec.Canonical,
                    release = result.Release.TagName,
                    dryRun = result.DryRun,
                    disposition = result.Install?.Disposition,
                    toolchainId = result.Install?.ToolchainId,
                    path = result.Install?.ToolchainDirectory
                })
            });
            return;
        }

        if (results.Count == 0)
        {
            Console.WriteLine("No installed channel selectors to update.");
            return;
        }

        foreach (var result in results)
        {
            if (result.DryRun)
            {
                Console.WriteLine($"[dry-run] Would install '{result.Spec.Canonical}' from '{result.Release.TagName}' using '{result.BundleAsset.Name}'.");
                continue;
            }

            Console.WriteLine(result.Install!.Disposition switch
            {
                InstallDisposition.AlreadyInstalled => $"{result.Spec.Canonical}: already current at '{result.Install.ToolchainDirectory}'.",
                InstallDisposition.Replaced => $"{result.Spec.Canonical}: reinstalled '{result.Install.ToolchainId}'.",
                _ => $"{result.Spec.Canonical}: installed '{result.Install.ToolchainId}'."
            });
        }
    }

    private static void WriteList(ToolchainState state, bool verbose, bool json)
    {
        if (json)
        {
            WriteJson(new
            {
                state.Schema,
                state.Revision,
                state.UpdatedAt,
                state.Default,
                state.Toolchains,
                state.Selectors,
                activationHistory = verbose ? state.ActivationHistory : null,
                state.UnmanagedDirectories
            });
            return;
        }

        if (state.Toolchains.Count == 0)
        {
            Console.WriteLine("No verified toolchains installed.");
            return;
        }

        foreach (var toolchain in state.Toolchains)
        {
            var selectors = state.Selectors
                .Where(selector => string.Equals(selector.ToolchainId, toolchain.Id, StringComparison.Ordinal))
                .Select(static selector => selector.Selector)
                .OrderBy(static selector => selector, StringComparer.Ordinal)
                .ToArray();
            var active = state.Default != null &&
                         string.Equals(state.Default.ToolchainId, toolchain.Id, StringComparison.Ordinal);
            Console.WriteLine($"{(active ? "*" : " ")} {toolchain.Version} ({toolchain.Rid}) [{string.Join(", ", selectors)}]");
            if (verbose)
            {
                Console.WriteLine($"    id: {toolchain.Id}");
                Console.WriteLine($"    source: {toolchain.Source} {toolchain.ReleaseTag}");
                Console.WriteLine($"    installed: {toolchain.InstalledAt:O}");
            }
        }
    }

    private static void WriteDefault(ToolchainState state, bool json)
    {
        if (json)
        {
            WriteJson(new { state.Default });
        }
        else if (state.Default == null)
        {
            Console.WriteLine("none");
        }
        else
        {
            Console.WriteLine($"{state.Default.Selector} ({state.Default.ToolchainId})");
        }
    }

    private static void WriteShow(
        ToolchainState state,
        ToolInstallLayout layout,
        bool verbose,
        bool json)
    {
        if (json)
        {
            WriteJson(new
            {
                home = layout.RootDirectory,
                profile = "default",
                state.Default,
                installed = state.Toolchains.Count,
                state.Toolchains,
                state.Selectors,
                activationHistory = verbose ? state.ActivationHistory : null,
                unmanagedDirectories = verbose ? state.UnmanagedDirectories : null
            });
            return;
        }

        Console.WriteLine($"home: {layout.RootDirectory}");
        Console.WriteLine("profile: default");
        Console.WriteLine(state.Default == null
            ? "default: none"
            : $"default: {state.Default.Selector} ({state.Default.ToolchainId})");
        Console.WriteLine($"installed toolchains: {state.Toolchains.Count}");
        if (verbose)
        {
            foreach (var toolchain in state.Toolchains)
            {
                Console.WriteLine($"  {toolchain.Id}");
            }

            Console.WriteLine($"activation history entries: {state.ActivationHistory.Count}");
            Console.WriteLine($"unmanaged directories: {state.UnmanagedDirectories.Count}");
        }
    }

    private static IReadOnlyList<ToolchainSpec> ParseSpecs(IEnumerable<string> values) =>
        values.Select(ToolchainSpec.Parse)
            .DistinctBy(static spec => spec.Canonical, StringComparer.Ordinal)
            .ToArray();

    private static ToolchainManagementOptions CreateOptions(
        InvocationContext context,
        Option<string?> installRootOption,
        Option<string?>? downloadRootOption = null,
        Option<string>? repositoryOption = null) => new(
        repositoryOption == null
            ? "dlqw/Eidosc"
            : context.ParseResult.GetValueForOption(repositoryOption) ?? "dlqw/Eidosc",
        context.ParseResult.GetValueForOption(installRootOption),
        downloadRootOption == null ? null : context.ParseResult.GetValueForOption(downloadRootOption));

    private static Option<string> CreateRepositoryOption() =>
        new("--repo", () => "dlqw/Eidosc", "GitHub repository that hosts Eidos release assets.");

    private static Option<string?> CreateInstallRootOption() =>
        new("--install-root", "Override EIDOS_HOME for this command.");

    private static Option<string?> CreateDownloadRootOption() =>
        new("--download-root", "Override the verified artifact cache directory.");

    private static Option<bool> CreateDryRunOption() =>
        new("--dry-run", "Print the resolved operation without changing files or state.");

    private static void WriteJson(object value) =>
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));

    private static string ToKebabCase(string value) =>
        string.Concat(value.SelectMany((character, index) =>
            index > 0 && char.IsUpper(character)
                ? new[] { '-', char.ToLowerInvariant(character) }
                : new[] { char.ToLowerInvariant(character) }));

    private sealed class ConsoleDownloadProgressReporter : IProgress<DownloadProgress>
    {
        private long _lastReportedBytes;

        public void Report(DownloadProgress value)
        {
            var completed = value.TotalBytes is { } total && value.BytesReceived >= total;
            if (!completed && value.BytesReceived - _lastReportedBytes < 8L * 1024 * 1024)
            {
                return;
            }

            _lastReportedBytes = value.BytesReceived;
            var totalText = value.TotalBytes is { } length ? $"/{FormatBytes(length)}" : string.Empty;
            Console.WriteLine($"Downloading {value.AssetName}: {FormatBytes(value.BytesReceived)}{totalText}{(value.Resumed ? " (resumed)" : string.Empty)}");
        }

        private static string FormatBytes(long bytes) => bytes >= 1024 * 1024
            ? $"{bytes / (1024d * 1024d):F1} MiB"
            : $"{bytes / 1024d:F1} KiB";
    }
}
