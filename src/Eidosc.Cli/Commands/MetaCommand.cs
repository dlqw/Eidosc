using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Text;
using System.Text.Json;
using Eidosc.Cli.Resources;
using Eidosc.Ide;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

public static class MetaCommand
{
    public static Command Create()
    {
        var command = new Command("meta", CliMessages.MetaCommandDescription)
        {
            CreateExpandCommand()
        };
        return command;
    }

    private static Command CreateExpandCommand()
    {
        var command = new Command("expand", CliMessages.MetaExpandCommandDescription)
        {
            new Argument<string>("source", () => "", CliMessages.SourceArgumentDescription),
            new Option<string>("--project", CliMessages.ProjectOptionDescription),
            new Option<string>("--target-name", CliMessages.IdeTargetNameOptionDescription),
            ProjectCommandSourceInputResolver.CreateSourceTextOption(),
            ProjectCommandSourceInputResolver.CreateStdinOption(),
            new Option<string>("--format", () => "readable", CliMessages.MetaExpandFormatOptionDescription),
            new Option<string>("--emit-generated", CliMessages.MetaEmitGeneratedOptionDescription),
            new Option<bool>("--trace-comptime", CliMessages.MetaTraceComptimeOptionDescription),
            new Option<long?>("--comptime-budget", CliMessages.MetaComptimeBudgetOptionDescription),
            new Option<bool>("--no-color", CliMessages.CliNoColorOptionDescription),
            ImportRootOptions.Create()
        };
        command.Handler = CommandHandler.Create<MetaExpandOptions>(ExecuteExpandAsync);
        return command;
    }

    private sealed class MetaExpandOptions
    {
        public string Source { get; set; } = "";
        public string? Project { get; set; }
        public string? TargetName { get; set; }
        public string? SourceText { get; set; }
        public bool Stdin { get; set; }
        public string Format { get; set; } = "readable";
        public string? EmitGenerated { get; set; }
        public bool TraceComptime { get; set; }
        public long? ComptimeBudget { get; set; }
        public bool NoColor { get; set; }
        public string[] ImportRoot { get; set; } = [];
    }

    private static async Task<int> ExecuteExpandAsync(MetaExpandOptions options)
    {
        if (options.ComptimeBudget is <= 0)
        {
            Console.Error.WriteLine(CliMessages.MetaInvalidComptimeBudget);
            return 2;
        }

        ProjectCommandSourceInput sourceInput;
        try
        {
            sourceInput = await ProjectCommandSourceInputResolver.ResolveAndLoadAsync(
                options.Source,
                options.Project,
                options.TargetName,
                options.ImportRoot,
                options.SourceText,
                options.Stdin);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        var inputResolution = sourceInput.InputResolution;
        var result = new CompilationPipeline(sourceInput.SourceText, new CompilationOptions
        {
            InputFile = sourceInput.SourceFilePath,
            LanguageVersion = inputResolution.GetLanguageVersion(),
            StopAtPhase = CompilationPhase.Types,
            TraceComptime = options.TraceComptime,
            ComptimeFuelBudget = options.ComptimeBudget ?? new CompilationOptions().ComptimeFuelBudget,
            UseColors = !options.NoColor,
            AllowVirtualInputFile = sourceInput.IsInMemorySource,
            ImportSearchRoots = inputResolution.ProjectTarget?.EffectiveSearchRoots ??
                                inputResolution.ImportResolution.EffectiveSearchRoots,
            PackageImportRoots = inputResolution.ProjectTarget?.PackageImportRoots ??
                                 new Dictionary<string, string[]>(StringComparer.Ordinal)
        }).Run();

        CliOutput.RenderDiagnostics(result, !options.NoColor);
        if (options.TraceComptime)
        {
            RenderComptimeTrace(result.ComptimeTrace);
        }
        var snapshot = IdeSemanticSnapshotBuilder.Build(result);
        var generated = snapshot.Symbols
            .Where(static symbol => symbol.IsGenerated && symbol.GeneratedOrigin != null)
            .OrderBy(static symbol => symbol.GeneratedOrigin!.StableIdentity, StringComparer.Ordinal)
            .ThenBy(static symbol => symbol.SymbolId)
            .ToArray();
        var payload = new MetaExpansionOutput(
            "eidos-meta-expansion-v1",
            sourceInput.SourceFilePath,
            generated.Select(MetaGeneratedDeclarationOutput.Create).ToArray());

        if (string.Equals(options.Format, "json", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        else if (string.Equals(options.Format, "readable", StringComparison.OrdinalIgnoreCase))
        {
            Console.Write(RenderReadable(payload));
        }
        else
        {
            Console.Error.WriteLine($"unsupported meta expand format '{options.Format}'; expected readable or json");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(options.EmitGenerated))
        {
            await EmitGeneratedDocumentsAsync(
                Path.GetFullPath(options.EmitGenerated),
                payload,
                snapshot.GeneratedDocuments);
        }

        return result.Success ? 0 : 1;
    }

    private static void RenderComptimeTrace(IReadOnlyList<Eidosc.Types.ComptimeTraceEntry> entries)
    {
        foreach (var entry in entries)
        {
            Console.Error.Write("[comptime #");
            Console.Error.Write(entry.Sequence);
            Console.Error.Write("] ");
            Console.Error.Write(entry.Phase);
            Console.Error.Write(' ');
            Console.Error.Write(entry.Kind);
            Console.Error.Write(' ');
            Console.Error.Write(entry.Operation);
            Console.Error.Write(" -> ");
            Console.Error.Write(entry.Outcome);
            if (!string.IsNullOrWhiteSpace(entry.Detail))
            {
                Console.Error.Write(": ");
                Console.Error.Write(entry.Detail);
            }

            if (!string.IsNullOrWhiteSpace(entry.FilePath))
            {
                Console.Error.Write(" @ ");
                Console.Error.Write(entry.FilePath);
                Console.Error.Write(':');
                Console.Error.Write(entry.Position);
            }

            Console.Error.WriteLine();
        }
    }

    private static string RenderReadable(MetaExpansionOutput output)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"generated declarations: {output.Declarations.Count}");
        foreach (var declaration in output.Declarations)
        {
            builder.Append("- ");
            builder.Append(declaration.Name);
            builder.Append(" :: ");
            builder.AppendLine(string.IsNullOrWhiteSpace(declaration.TypeText) ? declaration.Kind : declaration.TypeText);
            builder.AppendLine($"  origin: {declaration.StableIdentity}");
            builder.AppendLine($"  generator: {declaration.GeneratorIdentity}");
            builder.AppendLine($"  target: {declaration.TargetIdentity}");
            builder.AppendLine($"  virtual-document: {declaration.VirtualDocumentPath}");
        }

        return builder.ToString();
    }

    private static async Task EmitGeneratedDocumentsAsync(
        string outputDirectory,
        MetaExpansionOutput output,
        IReadOnlyList<IdeGeneratedDocumentEntry> documents)
    {
        Directory.CreateDirectory(outputDirectory);
        var contentByUri = documents.ToDictionary(static document => document.Uri, StringComparer.Ordinal);
        foreach (var declaration in output.Declarations)
        {
            var path = Path.Combine(outputDirectory, $"{declaration.StableIdentity}.eidos");
            await File.WriteAllTextAsync(
                path,
                contentByUri.TryGetValue(declaration.VirtualDocumentPath, out var document)
                    ? document.Content
                    : RenderGeneratedDocument(declaration));
        }

        var manifestPath = Path.Combine(outputDirectory, "generated-manifest.json");
        await File.WriteAllTextAsync(
            manifestPath,
            JsonSerializer.Serialize(output, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string RenderGeneratedDocument(MetaGeneratedDeclarationOutput declaration)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"// generated by {declaration.GeneratorIdentity}");
        builder.AppendLine($"// target {declaration.TargetIdentity}");
        builder.AppendLine($"// stable identity {declaration.StableIdentity}");
        builder.Append(declaration.Name);
        builder.Append(" :: ");
        builder.Append(string.IsNullOrWhiteSpace(declaration.TypeText) ? declaration.Kind : declaration.TypeText);
        builder.AppendLine(";");
        return builder.ToString();
    }

    internal sealed record MetaExpansionOutput(
        string SchemaVersion,
        string Source,
        IReadOnlyList<MetaGeneratedDeclarationOutput> Declarations);

    internal sealed record MetaGeneratedDeclarationOutput(
        int SymbolId,
        string Name,
        string Kind,
        string? TypeText,
        string StableIdentity,
        string GeneratorIdentity,
        string TargetIdentity,
        int AttributeOccurrenceIndex,
        int ExpansionOutputIndex,
        string CanonicalArgumentsHash,
        int MetaSchemaVersion,
        string VirtualDocumentPath)
    {
        public static MetaGeneratedDeclarationOutput Create(IdeSymbolEntry symbol)
        {
            var origin = symbol.GeneratedOrigin!;
            return new MetaGeneratedDeclarationOutput(
                symbol.SymbolId,
                symbol.Name,
                symbol.Kind,
                symbol.TypeText,
                origin.StableIdentity,
                origin.GeneratorIdentity,
                origin.TargetIdentity,
                origin.AttributeOccurrenceIndex,
                origin.ExpansionOutputIndex,
                origin.CanonicalArgumentsHash,
                origin.MetaSchemaVersion,
                origin.VirtualDocumentPath);
        }
    }
}
