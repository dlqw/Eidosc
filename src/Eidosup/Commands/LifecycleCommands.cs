using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Nodes;
using Eidosup.Configuration;
using Eidosup.Distribution;
using Eidosup.Installation;
using Eidosup.SelfManagement;
using Eidosup.Toolchains;

namespace Eidosup.Commands;

internal static class LifecycleCommands
{
    public static Command CreateSetCommand()
    {
        return new Command("set", "Configure Eidosup lifecycle and distribution behavior.")
        {
            CreateSetDefaultHostCommand(),
            CreateSetAutoSelfUpdateCommand(),
            CreateSetAutoInstallCommand(),
            CreateSetSourceCommand(),
            CreateSetProfileCommand()
        };
    }

    public static Command CreateSelfCommand()
    {
        var command = new Command("self", "Update or uninstall Eidosup itself.")
        {
            CreateSelfUpdateCommand(),
            CreateSelfUninstallCommand()
        };
        return command;
    }

    public static Command CreateCacheCommand()
    {
        return new Command("cache", "Manage verified artifacts and complete offline bundles.")
        {
            CreateCacheCleanCommand(),
            CreateCacheImportCommand(),
            CreateCacheExportCommand()
        };
    }

    public static Command CreateSourceCommand()
    {
        return new Command("source", "Configure named distribution source groups and mirrors.")
        {
            CreateSourceAddCommand(),
            CreateSourceRemoveCommand(),
            CreateSourceListCommand()
        };
    }

    public static Command CreateCompletionsCommand()
    {
        var command = new Command("completions", "Generate shell completion scripts.");
        var shellArgument = new Argument<string>("shell", "bash, fish, zsh, or powershell.");
        command.AddArgument(shellArgument);
        command.SetHandler((InvocationContext context) =>
        {
            var shell = context.ParseResult.GetValueForArgument(shellArgument).ToLowerInvariant();
            Console.Write(ShellCompletionGenerator.Generate(shell));
        });
        return command;
    }

    private static Command CreateSetDefaultHostCommand()
    {
        var command = new Command("default-host", "Set the default host RID for future installs.");
        var value = new Argument<string>("rid");
        var root = CreateInstallRootOption();
        var dryRun = CreateDryRunOption();
        command.AddArgument(value);
        command.AddOption(root);
        command.AddOption(dryRun);
        command.SetHandler(async (InvocationContext context) =>
        {
            var rid = context.ParseResult.GetValueForArgument(value);
            if (!PlatformContext.IsSupportedRid(rid))
            {
                throw new FormatException($"Unsupported host RID '{rid}'.");
            }

            await UpdateSettingsAsync(context, root, dryRun, settings => settings with { DefaultHost = rid });
        });
        return command;
    }

    private static Command CreateSetAutoSelfUpdateCommand()
    {
        var command = new Command("auto-self-update", "Set automatic self-update behavior.");
        var value = new Argument<string>("mode", "enable, disable, or check-only.");
        var root = CreateInstallRootOption();
        var dryRun = CreateDryRunOption();
        command.AddArgument(value);
        command.AddOption(root);
        command.AddOption(dryRun);
        command.SetHandler(async (InvocationContext context) =>
        {
            var mode = ParseEnum<AutoSelfUpdateMode>(context.ParseResult.GetValueForArgument(value));
            await UpdateSettingsAsync(context, root, dryRun, settings => settings with { AutoSelfUpdate = mode });
        });
        return command;
    }

    private static Command CreateSetAutoInstallCommand()
    {
        var command = new Command("auto-install", "Set missing-toolchain installation behavior.");
        var value = new Argument<string>("mode", "enable, disable, or prompt.");
        var root = CreateInstallRootOption();
        var dryRun = CreateDryRunOption();
        command.AddArgument(value);
        command.AddOption(root);
        command.AddOption(dryRun);
        command.SetHandler(async (InvocationContext context) =>
        {
            var mode = ParseEnum<AutoInstallMode>(context.ParseResult.GetValueForArgument(value));
            await UpdateSettingsAsync(context, root, dryRun, settings => settings with { AutoInstall = mode });
        });
        return command;
    }

    private static Command CreateSetProfileCommand()
    {
        var command = new Command("profile", "Create and activate an immutable variant using minimal, default, or complete profile metadata.");
        var value = new Argument<string>("profile");
        var toolchain = new Option<string?>("--toolchain", "Use an installed selector instead of the global default.");
        var root = CreateInstallRootOption();
        var downloadRoot = new Option<string?>("--download-root", "Override the verified artifact cache directory.");
        var dryRun = CreateDryRunOption();
        command.AddArgument(value);
        command.AddOption(toolchain);
        command.AddOption(root);
        command.AddOption(downloadRoot);
        command.AddOption(dryRun);
        command.SetHandler(async (InvocationContext context) =>
        {
            var profile = ToolchainComponentSolver.ParseProfile(context.ParseResult.GetValueForArgument(value));
            var selector = context.ParseResult.GetValueForOption(toolchain);
            var dryRunValue = context.ParseResult.GetValueForOption(dryRun);
            var result = await new ToolchainManager().SetProfileAsync(
                new ToolchainManagementOptions(
                    InstallRoot: context.ParseResult.GetValueForOption(root),
                    DownloadRoot: context.ParseResult.GetValueForOption(downloadRoot)),
                selector == null ? null : ToolchainSpec.Parse(selector),
                profile,
                dryRunValue,
                progress: null,
                context.GetCancellationToken());
            ToolchainCommands.WriteCompositionResult(
                result,
                context.ParseResult.GetValueForOption(GlobalOptions.Json),
                "set-profile");
        });
        return command;
    }

    private static Command CreateSetSourceCommand()
    {
        var command = new Command("source", "Select a GitHub, signed index, or complete offline distribution source.");
        var value = new Argument<string>("source", "github:owner/repo, index:https://..., or offline:<path>.");
        var root = CreateInstallRootOption();
        var dryRun = CreateDryRunOption();
        command.AddArgument(value);
        command.AddOption(root);
        command.AddOption(dryRun);
        command.SetHandler(async (InvocationContext context) =>
        {
            var configured = context.ParseResult.GetValueForArgument(value);
            string source;
            try
            {
                source = DistributionSourceDescriptor.Parse(configured).Canonical;
            }
            catch (FormatException) when (DistributionSourceCatalogStore.IsValidName(configured))
            {
                var layout = CreateLayout(context, root);
                _ = await new DistributionSourceCatalogStore().ResolveAsync(
                    layout,
                    configured,
                    context.GetCancellationToken());
                source = configured;
            }

            await UpdateSettingsAsync(context, root, dryRun, settings => settings with { Source = source });
        });
        return command;
    }

    private static Command CreateSelfUpdateCommand()
    {
        var command = new Command("update", "Check for or install a newer verified Eidosup release.");
        var checkOnly = new Option<bool>("--check-only", "Only report whether an update exists.");
        var root = CreateInstallRootOption();
        command.AddOption(checkOnly);
        command.AddOption(root);
        command.SetHandler(async (InvocationContext context) =>
        {
            var layout = CreateLayout(context, root);
            var result = await new SelfLifecycleManager().UpdateAsync(
                layout,
                context.ParseResult.GetValueForOption(checkOnly),
                context.GetCancellationToken());
            Write(context, result, result.Status switch
            {
                SelfUpdateStatus.Current => $"Eidosup is current at {result.CurrentVersion}.",
                SelfUpdateStatus.UpdateAvailable => $"Eidosup {result.AvailableVersion} is available (current {result.CurrentVersion}).",
                SelfUpdateStatus.Scheduled => $"Eidosup {result.AvailableVersion} replacement is scheduled after this process exits.",
                _ => $"Eidosup updated to {result.AvailableVersion}."
            });
        });
        return command;
    }

    private static Command CreateSourceAddCommand()
    {
        var command = new Command("add", "Add or update a mirror in a named source group.");
        var name = new Argument<string>("name");
        var descriptor = new Argument<string>("descriptor");
        var priority = new Option<int>("--priority", () => 100, "Priority within the same trust level (0..1000).");
        var root = CreateInstallRootOption();
        var dryRun = CreateDryRunOption();
        command.AddArgument(name);
        command.AddArgument(descriptor);
        command.AddOption(priority);
        command.AddOption(root);
        command.AddOption(dryRun);
        command.SetHandler(async (InvocationContext context) =>
        {
            var layout = CreateLayout(context, root);
            var catalog = await new DistributionSourceCatalogStore().AddAsync(
                layout,
                context.ParseResult.GetValueForArgument(name),
                DistributionSourceDescriptor.Parse(context.ParseResult.GetValueForArgument(descriptor)),
                context.ParseResult.GetValueForOption(priority),
                DateTimeOffset.UtcNow,
                context.ParseResult.GetValueForOption(dryRun),
                context.GetCancellationToken());
            Write(context, catalog, context.ParseResult.GetValueForOption(dryRun)
                ? "[dry-run] Distribution source mirror update validated."
                : "Distribution source mirror configured.");
        });
        return command;
    }

    private static Command CreateSourceRemoveCommand()
    {
        var command = new Command("remove", "Remove one mirror or an entire named source group.");
        var name = new Argument<string>("name");
        var descriptor = new Option<string?>("--descriptor", "Remove only this descriptor from the group.");
        var root = CreateInstallRootOption();
        var dryRun = CreateDryRunOption();
        command.AddArgument(name);
        command.AddOption(descriptor);
        command.AddOption(root);
        command.AddOption(dryRun);
        command.SetHandler(async (InvocationContext context) =>
        {
            var catalog = await new DistributionSourceCatalogStore().RemoveAsync(
                CreateLayout(context, root),
                context.ParseResult.GetValueForArgument(name),
                context.ParseResult.GetValueForOption(descriptor),
                context.ParseResult.GetValueForOption(dryRun),
                context.GetCancellationToken());
            Write(context, catalog, context.ParseResult.GetValueForOption(dryRun)
                ? "[dry-run] Distribution source mirror removal validated."
                : "Distribution source mirror removed.");
        });
        return command;
    }

    private static Command CreateSourceListCommand()
    {
        var command = new Command("list", "List named source groups, mirrors, priority, and trust level.");
        var root = CreateInstallRootOption();
        command.AddOption(root);
        command.SetHandler(async (InvocationContext context) =>
        {
            var catalog = await new DistributionSourceCatalogStore().ReadAsync(
                CreateLayout(context, root),
                context.GetCancellationToken());
            if (context.ParseResult.GetValueForOption(GlobalOptions.Json))
            {
                Write(context, catalog, string.Empty);
                return;
            }

            if (catalog.Sources.Count == 0)
            {
                Console.WriteLine("No named distribution source groups configured.");
                return;
            }

            foreach (var source in catalog.Sources)
            {
                var kind = DistributionSourceDescriptor.Parse(source.Descriptor).Kind;
                var trust = kind == DistributionSourceKind.GitHub ? "checksum" : "signed";
                Console.WriteLine($"{source.Name}\t{source.Priority}\t{trust}\t{source.Descriptor}");
            }
        });
        return command;
    }

    private static Command CreateSelfUninstallCommand()
    {
        var command = new Command("uninstall", "Remove Eidosup-owned integration and optionally its managed toolchains.");
        var yes = new Option<bool>("--yes", "Confirm the complete uninstall without an interactive prompt.");
        var keep = new Option<bool>("--keep-toolchains", "Preserve verified toolchains and their state for a future reinstall.");
        var root = CreateInstallRootOption();
        command.AddOption(yes);
        command.AddOption(keep);
        command.AddOption(root);
        command.SetHandler(async (InvocationContext context) =>
        {
            if (!context.ParseResult.GetValueForOption(yes))
            {
                throw new ArgumentException("self uninstall requires --yes.");
            }

            var preserve = context.ParseResult.GetValueForOption(keep);
            var scheduled = await new SelfLifecycleManager().UninstallAsync(
                CreateLayout(context, root),
                preserve,
                context.GetCancellationToken());
            Write(context, new { scheduled, keepToolchains = preserve }, scheduled
                ? "Eidosup uninstall is scheduled after this process exits."
                : "Eidosup-owned integration was removed.");
        });
        return command;
    }

    private static Command CreateCacheCleanCommand()
    {
        var command = new Command("clean", "Remove least-recently-used verified cache entries.");
        var maxSize = new Option<string?>("--max-size", "Maximum cache size, for example 512MiB or 2GiB.");
        var all = new Option<bool>("--all", "Remove all cached artifacts and imported offline bundles.");
        var dryRun = new Option<bool>("--dry-run");
        var root = CreateInstallRootOption();
        command.AddOption(maxSize);
        command.AddOption(all);
        command.AddOption(dryRun);
        command.AddOption(root);
        command.SetHandler((InvocationContext context) =>
        {
            long? maximum = context.ParseResult.GetValueForOption(maxSize) is { } value ? ParseSize(value) : null;
            var result = new ArtifactCacheManager().Clean(
                CreateLayout(context, root),
                maximum,
                context.ParseResult.GetValueForOption(all),
                context.ParseResult.GetValueForOption(dryRun));
            Write(context, result, $"Cache: {FormatSize(result.BytesBefore)} -> {FormatSize(result.BytesAfter)}; removed {result.FilesRemoved} files.");
        });
        return command;
    }

    private static Command CreateCacheImportCommand()
    {
        var command = new Command("import", "Verify and import a complete signed offline bundle.");
        var bundle = new Argument<string>("bundle");
        var root = CreateInstallRootOption();
        command.AddArgument(bundle);
        command.AddOption(root);
        command.SetHandler(async (InvocationContext context) =>
        {
            var result = await new OfflineBundleManager().ImportAsync(
                CreateLayout(context, root),
                context.ParseResult.GetValueForArgument(bundle),
                context.GetCancellationToken());
            Write(context, result, $"Imported offline bundle {result.Id}. Use 'eidosup set source {result.Source}'.");
        });
        return command;
    }

    private static Command CreateCacheExportCommand()
    {
        var command = new Command("export", "Export an imported verified offline bundle.");
        var id = new Argument<string>("id");
        var output = new Argument<string>("output");
        var root = CreateInstallRootOption();
        command.AddArgument(id);
        command.AddArgument(output);
        command.AddOption(root);
        command.SetHandler(async (InvocationContext context) =>
        {
            var outputPath = Path.GetFullPath(context.ParseResult.GetValueForArgument(output));
            await new OfflineBundleManager().ExportAsync(
                CreateLayout(context, root),
                context.ParseResult.GetValueForArgument(id),
                outputPath,
                context.GetCancellationToken());
            Write(context, new { output = outputPath }, $"Exported offline bundle to '{outputPath}'.");
        });
        return command;
    }

    private static async Task UpdateSettingsAsync(
        InvocationContext context,
        Option<string?> rootOption,
        Option<bool> dryRunOption,
        Func<EidosupSettings, EidosupSettings> update)
    {
        var layout = CreateLayout(context, rootOption);
        var store = new EidosupSettingsStore();
        var settings = update(await store.ReadAsync(layout, context.GetCancellationToken()));
        var dryRun = context.ParseResult.GetValueForOption(dryRunOption);
        if (!dryRun)
        {
            await store.WriteAsync(layout, settings, context.GetCancellationToken());
        }

        Write(context, settings, dryRun ? "[dry-run] Eidosup settings update validated." : "Eidosup settings updated.");
    }

    private static ToolInstallLayout CreateLayout(InvocationContext context, Option<string?> rootOption) =>
        ToolInstallLayout.Create(
            PlatformContext.Detect(),
            context.ParseResult.GetValueForOption(rootOption),
            null);

    private static Option<string?> CreateInstallRootOption() => new("--install-root", "Override EIDOS_HOME for this command.");

    private static Option<bool> CreateDryRunOption() =>
        new("--dry-run", "Validate the proposed change without writing files or state.");

    private static T ParseEnum<T>(string value) where T : struct, Enum
    {
        var normalized = value.Replace("-", string.Empty, StringComparison.Ordinal);
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (string.Equals(candidate.ToString(), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        throw new FormatException($"Unsupported {typeof(T).Name} value '{value}'.");
    }

    private static long ParseSize(string value)
    {
        var units = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase)
        {
            ["B"] = 1,
            ["KiB"] = 1024,
            ["MiB"] = 1024 * 1024,
            ["GiB"] = 1024L * 1024 * 1024
        };
        var split = value.TakeWhile(char.IsAsciiDigit).Count();
        if (split == 0 || !long.TryParse(value[..split], out var amount) ||
            !units.TryGetValue(value[split..], out var multiplier))
        {
            throw new FormatException($"Invalid cache size '{value}'. Use B, KiB, MiB, or GiB.");
        }

        return checked(amount * multiplier);
    }

    private static string FormatSize(long value) => value >= 1024L * 1024 * 1024
        ? $"{value / (1024d * 1024 * 1024):F1} GiB"
        : $"{value / (1024d * 1024):F1} MiB";

    private static void Write(InvocationContext context, object value, string text)
    {
        if (context.ParseResult.GetValueForOption(GlobalOptions.Json))
        {
            var options = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            };
            var node = JsonSerializer.SerializeToNode(value, options) as JsonObject
                       ?? throw new InvalidOperationException("Eidosup JSON command results must be objects.");
            node["schemaVersion"] = 1;
            Console.WriteLine(node.ToJsonString(options));
        }
        else if (!context.ParseResult.GetValueForOption(GlobalOptions.Quiet))
        {
            Console.WriteLine(text);
        }
    }
}

internal static class ShellCompletionGenerator
{
    private const string Commands = "setup doctor toolchain component target default update check show run which doc rollback override set source self cache completions";

    public static string Generate(string shell) => shell switch
    {
        "bash" => $"_eidosup() {{ local cur=\"${{COMP_WORDS[COMP_CWORD]}}\"; COMPREPLY=( $(compgen -W \"{Commands}\" -- \"$cur\") ); }}\ncomplete -F _eidosup eidosup eidosc\n",
        "fish" => string.Join('\n', Commands.Split(' ').Select(command => $"complete -c eidosup -f -a '{command}'")) + "\n",
        "zsh" => $"#compdef eidosup eidosc\n_arguments '1:command:({Commands})' '*::arg:->args'\n",
        "powershell" => $"Register-ArgumentCompleter -Native -CommandName eidosup,eidosc -ScriptBlock {{ param($wordToComplete) '{Commands}'.Split(' ') | Where-Object {{ $_ -like \"$wordToComplete*\" }} }}\n",
        _ => throw new FormatException($"Unsupported shell '{shell}'. Expected bash, fish, zsh, or powershell.")
    };
}
