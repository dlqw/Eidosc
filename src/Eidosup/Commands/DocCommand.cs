using System.CommandLine;
using System.CommandLine.Invocation;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Eidosup.Diagnostics;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosup.Commands;

internal static class DocCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static Command Create()
    {
        var command = new Command("doc", "Open version-matched local Eidos and Eidosc documentation.");
        var topic = new Argument<string?>("topic", () => null, "Documentation topic; defaults to index.");
        var pathOnly = new Option<bool>("--path", "Print the local document path without opening it.");
        var toolchain = new Option<string?>("--toolchain", "Use an installed selector instead of normal project/default selection.");
        var installRoot = new Option<string?>("--install-root", "Override EIDOS_HOME for this command.");
        command.AddArgument(topic);
        command.AddOption(pathOnly);
        command.AddOption(toolchain);
        command.AddOption(installRoot);
        command.SetHandler(async (InvocationContext context) =>
        {
            var options = new ToolchainManagementOptions(
                InstallRoot: context.ParseResult.GetValueForOption(installRoot));
            var selector = context.ParseResult.GetValueForOption(toolchain);
            var resolved = await new ToolchainManager().ResolveAsync(
                options,
                "eidosc",
                selector == null ? null : ToolchainSpec.Parse(selector),
                context.GetCancellationToken());
            if (resolved.IsCustom)
            {
                throw new EidosupException(
                    EidosupErrorCode.ToolchainUnavailable,
                    EidosupExitCodes.ToolchainUnavailable,
                    $"Custom toolchain '{resolved.Selector}' does not publish managed local documentation.");
            }

            var document = await ResolveAsync(
                resolved.ToolchainDirectory,
                context.ParseResult.GetValueForArgument(topic) ?? "index",
                context.GetCancellationToken());
            if (context.ParseResult.GetValueForOption(GlobalOptions.Json))
            {
                Console.WriteLine(JsonSerializer.Serialize(new
                {
                    schemaVersion = 1,
                    resolved.Selector,
                    document.Topic,
                    document.Path,
                    document.EidoscVersion
                }, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true }));
            }
            else if (context.ParseResult.GetValueForOption(pathOnly))
            {
                Console.WriteLine(document.Path);
            }
            else
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = document.Path,
                    UseShellExecute = true
                });
            }
        });
        return command;
    }

    internal static async Task<ResolvedDocument> ResolveAsync(
        string toolchainDirectory,
        string topic,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(topic) || !string.Equals(topic, topic.Trim(), StringComparison.Ordinal))
        {
            throw new FormatException("Documentation topic cannot be empty or contain surrounding whitespace.");
        }

        var manifest = await InstallManifest.TryReadAsync(toolchainDirectory, cancellationToken)
                       ?? throw Missing("The selected toolchain has no supported install manifest.");
        if (!manifest.Components.Any(static component => string.Equals(component.Name, "eidos-docs", StringComparison.Ordinal)))
        {
            throw Missing($"Toolchain '{manifest.ToolchainId}' does not contain the eidos-docs component.");
        }

        var docsRoot = Path.Combine(toolchainDirectory, "docs");
        var indexPath = Path.Combine(docsRoot, "index.json");
        if (!File.Exists(indexPath) || (File.GetAttributes(indexPath) & FileAttributes.ReparsePoint) != 0)
        {
            throw Missing($"Documentation component in '{toolchainDirectory}' has no regular docs/index.json.");
        }

        LocalDocumentationIndex index;
        try
        {
            await using var stream = File.OpenRead(indexPath);
            index = await JsonSerializer.DeserializeAsync<LocalDocumentationIndex>(stream, JsonOptions, cancellationToken)
                    ?? throw new JsonException("Documentation index is empty.");
        }
        catch (JsonException exception)
        {
            throw Missing("Local documentation index is invalid.", exception);
        }

        if (index.Schema != 1 ||
            !string.Equals(index.EidoscVersion, manifest.Version, StringComparison.Ordinal) ||
            index.Topics == null ||
            !index.Topics.TryGetValue(topic, out var relativePath) ||
            !InstallManifest.TryNormalizeRelativePath(relativePath, out var normalized) ||
            !string.Equals(relativePath, normalized, StringComparison.Ordinal))
        {
            throw Missing($"Documentation topic '{topic}' is unavailable or does not match Eidosc {manifest.Version}.");
        }

        var path = Path.GetFullPath(Path.Combine(docsRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!ToolInstallLayout.IsWithin(docsRoot, path) ||
            !File.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw Missing($"Documentation topic '{topic}' points to a missing or unsafe file.");
        }

        return new ResolvedDocument(topic, path, index.EidoscVersion);
    }

    private static EidosupException Missing(string message, Exception? inner = null) => new(
        EidosupErrorCode.ToolchainUnavailable,
        EidosupExitCodes.ToolchainUnavailable,
        message,
        "Run 'eidosup component add eidos-docs' for the selected toolchain, then retry offline.",
        inner);

    internal sealed record ResolvedDocument(string Topic, string Path, string EidoscVersion);

    private sealed record LocalDocumentationIndex(
        int Schema,
        string EidoscVersion,
        IReadOnlyDictionary<string, string> Topics);
}
