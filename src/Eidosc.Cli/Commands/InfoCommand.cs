using Eidosc.Symbols;
using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Diagnostics;
using System.Reflection;
using Eidosc.Cli.Resources;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.ProjectSystem;

namespace Eidosc.Cli.Commands;

/// <summary>
/// 信息命令 - 显示编译器信息
/// </summary>
public static class InfoCommand
{
    private const string FunctionalCategory = "functional";
    private const string MathCategory = "math";
    private const string ContainersCategory = "containers";
    private const string FileIoCategory = "file-io";
    private const string ConsoleIoCategory = "console-io";
    private const string NetworkCategory = "network";
    private const string SerializationCategory = "serialization";
    private const string BasicsCategory = "basics";
    private const string OtherCategory = "other";

    private static readonly IReadOnlyDictionary<string, StdlibCategoryMetadata> StdlibCategoryMetadataMap =
        new Dictionary<string, StdlibCategoryMetadata>(StringComparer.Ordinal)
        {
            [FunctionalCategory] = new(
                CliMessages.InfoStdlibCategoryFunctional,
                CliMessages.InfoStdlibSummaryFunctional,
                ["Functions.compose", "Predicate.accept", "Predicate.is", "Option.map", "Option.apply", "Option.traverse", "Seq.traverse", "Result.and_then", "Ordering.show"]),
            [MathCategory] = new(
                CliMessages.InfoStdlibCategoryMath,
                CliMessages.InfoStdlibSummaryMath,
                ["Math.abs", "Math.wrap", "FloatMath.smoothstep", "FloatMath.move_toward", "GameMath.ivec2", "GameMath.grid_cell_rect", "GameMath.move_toward"]),
            [ContainersCategory] = new(
                CliMessages.InfoStdlibCategoryContainers,
                CliMessages.InfoStdlibSummaryContainers,
                ["Seq.head", "SeqBuilder.filled", "SeqBuilder.push", "HashMap.insert", "HashSet.contains", "Deque.push_back", "Queue.dequeue", "Stack.pop", "BinaryHeap.pop", "PriorityQueue.min_enqueue", "PriorityQueue.dequeue", "TreeMap.keys", "TreeSet.to_seq", "PersistentMap.insert", "PersistentSet.contains"]),
            [FileIoCategory] = new(
                CliMessages.InfoStdlibCategoryFileIo,
                CliMessages.InfoStdlibSummaryFileIo,
                ["File.exists", "File.read_text_or_empty", "File.write_text"]),
            [ConsoleIoCategory] = new(
                CliMessages.InfoStdlibCategoryConsoleIo,
                CliMessages.InfoStdlibSummaryConsoleIo,
                ["Console.write_line", "Console.write", "Console.write_line", "Console.read_line"]),
            [NetworkCategory] = new(
                CliMessages.InfoStdlibCategoryNetwork,
                CliMessages.InfoStdlibSummaryNetwork,
                ["Network.http_get_text_or_empty", "Network.http_request_text", "Network.http_request_bytes"]),
            [SerializationCategory] = new(
                CliMessages.InfoStdlibCategorySerialization,
                CliMessages.InfoStdlibSummarySerialization,
                ["Binary.encode_u32_le", "Binary.bytes_to_string", "Json.array", "Json.object"]),
            [BasicsCategory] = new(
                CliMessages.InfoStdlibCategoryBasics,
                CliMessages.InfoStdlibSummaryBasics,
                ["Text.from_int", "Text.char_code_at_or", "Text.char_at_or", "Text.index_of_or", "Range.make", "Range.contains", "Shared.clone"]),
            [OtherCategory] = new(
                CliMessages.InfoStdlibCategoryOther,
                CliMessages.InfoStdlibSummaryOther,
                [])
        };

    public static Command Create()
    {
        var command = new Command("info", CliMessages.InfoCommandDescription)
        {
            new Option<bool>("--version", CliMessages.InfoVersionOptionDescription),
            new Option<bool>("--phases", CliMessages.InfoPhasesOptionDescription),
            new Option<bool>("--stdlib", CliMessages.InfoStdlibOptionDescription),
        };

        command.Handler = CommandHandler.Create<InfoOptions>(Execute);

        return command;
    }

    private sealed class InfoOptions
    {
        public bool Version { get; set; }
        public bool Phases { get; set; }
        public bool Stdlib { get; set; }
    }

    private static Task<int> Execute(InfoOptions options)
    {
        var commandStopwatch = Stopwatch.StartNew();
        CliOutput.WriteAction(CliMessages.InfoInspectingAction, CliMessages.InfoCompilerMetadataSubject, useColors: true);
        var printAll = !options.Version && !options.Phases && !options.Stdlib;
        if (options.Version || printAll)
        {
            PrintVersion();
        }

        if (options.Phases || printAll)
        {
            PrintPhases();
        }

        if (options.Stdlib || printAll)
        {
            PrintStdlib();
        }

        commandStopwatch.Stop();
        CliOutput.WriteFinished("info", true, commandStopwatch.Elapsed, useColors: true);
        return Task.FromResult(0);
    }

    private static void PrintVersion()
    {
        var assembly = Assembly.GetExecutingAssembly();
        var informationalVersion = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        var version = string.IsNullOrWhiteSpace(informationalVersion)
            ? assembly.GetName().Version?.ToString()
            : informationalVersion;
        Console.WriteLine(CliMessages.InfoCompilerTitle);
        Console.WriteLine(CliMessages.InfoVersionLine(version ?? CliMessages.InfoVersionUnknown));
        Console.WriteLine($"language: {EidosLanguageVersions.Current}");
        Console.WriteLine($"stdlib: {EidosStdVersions.Current}");
        Console.WriteLine("manifest schemas: 3");
        Console.WriteLine(CliMessages.InfoTargetFrameworkLine(".NET 10"));
        Console.WriteLine();
    }

    private static void PrintPhases()
    {
        Console.WriteLine(CliMessages.InfoSupportedPhasesHeader);
        Console.WriteLine();

        var phases = new (string Phase, string Description)[]
        {
            ("Lexer", CliMessages.InfoPhaseLexerDescription),
            ("Parser", CliMessages.InfoPhaseParserDescription),
            ("Namer", CliMessages.InfoPhaseNamerDescription),
            ("Types", CliMessages.InfoPhaseTypesDescription),
            ("Effects", CliMessages.InfoPhaseAbilitiesDescription),
            ("Hir", CliMessages.InfoPhaseHirDescription),
            ("Mir", CliMessages.InfoPhaseMirDescription),
            ("Borrow", CliMessages.InfoPhaseBorrowDescription),
            ("Llvm", CliMessages.InfoPhaseLlvmDescription),
        };

        foreach (var (phase, description) in phases)
        {
            Console.WriteLine(CliMessages.InfoPhaseLine(phase, description));
        }

        Console.WriteLine();
        Console.WriteLine(CliMessages.InfoCompileTargetsHeader);
        Console.WriteLine();

        var targets = new (CompileTarget Target, string Description)[]
        {
            (CompileTarget.Tokens, CliMessages.InfoTargetTokensDescription),
            (CompileTarget.Ast, CliMessages.InfoTargetAstDescription),
            (CompileTarget.Resolved, CliMessages.InfoTargetResolvedDescription),
            (CompileTarget.Typed, CliMessages.InfoTargetTypedDescription),
            (CompileTarget.Hir, CliMessages.InfoTargetHirDescription),
            (CompileTarget.Mir, CliMessages.InfoTargetMirDescription),
            (CompileTarget.LlvmIr, CliMessages.InfoTargetLlvmIrDescription),
            (CompileTarget.Native, CliMessages.InfoTargetNativeDescription),
            (CompileTarget.Cil, CliMessages.InfoTargetCilDescription),
        };

        foreach (var (target, description) in targets)
        {
            Console.WriteLine(CliMessages.InfoTargetLine(target, description));
        }
    }

    private static void PrintStdlib()
    {
        Console.Write(RenderStdlibText());
    }

    private static string GetStdlibCategory(string module)
    {
        var moduleName = module.Contains(WellKnownStrings.Operators.Divide, StringComparison.Ordinal)
            ? module[(module.LastIndexOf(WellKnownStrings.Operators.Divide, StringComparison.Ordinal) + 1)..]
            : module;
        return moduleName switch
        {
            "Alternative" or "Applicative" or "Either" or "Foldable" or "Functions" or
            "Functor" or "Monad" or "Monoid" or "Option" or "Ordering" or "Prelude" or
            "Result" or "Semigroup" or "Traits" or "TraitInvoke" or "Traversable" => FunctionalCategory,
            "Math" or "FloatMath" or "GameMath" => MathCategory,
            "Deque" or "BinaryHeap" or "HashMap" or "HashSet" or "Seq" or
            "PersistentMap" or "PersistentSet" or "PriorityQueue" or "Queue" or "Stack" or
            "TreeMap" or "TreeSet" or "SeqBuilder" => ContainersCategory,
            "File" => FileIoCategory,
            "Console" => ConsoleIoCategory,
            "Network" => NetworkCategory,
            "Binary" or "Json" => SerializationCategory,
            "Text" or "Range" or "Shared" => BasicsCategory,
            _ => OtherCategory
        };
    }

    internal static string RenderStdlibText()
    {
        using var writer = new StringWriter();
        WriteStdlib(writer);
        return writer.ToString();
    }

    internal static void WriteStdlib(TextWriter writer)
    {
        writer.WriteLine();
        writer.WriteLine(CliMessages.InfoStdlibHeader);
        writer.WriteLine();

        var preludeModules = PreludeCoreImageRegistry.GetAvailableModulePaths()
            .Select(static module => $"{WellKnownStrings.Std.Module}/{module}")
            .ToArray();
        var stdModules = PrecompiledModuleRegistry.GetAvailableModulePaths();
        if (preludeModules.Length == 0 && stdModules.Count == 0)
        {
            writer.WriteLine(CliMessages.InfoStdlibNone);
            return;
        }

        writer.WriteLine("Prelude Core Image（自动导入，无 package）:");
        WriteModuleGroups(writer, preludeModules, isPrelude: true);
        writer.WriteLine();
        writer.WriteLine("Std package（显式依赖与 import）:");
        WriteModuleGroups(writer, stdModules, isPrelude: false);
    }

    private static void WriteModuleGroups(
        TextWriter writer,
        IReadOnlyList<string> modules,
        bool isPrelude)
    {
        var grouped = modules
            .GroupBy(GetStdlibCategory, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal);

        foreach (var group in grouped)
        {
            var metadata = GetCategoryMetadata(group.Key);
            writer.WriteLine(CliMessages.InfoStdlibCategoryHeader(metadata.Label));
            writer.WriteLine(CliMessages.InfoStdlibSummaryLine(metadata.Summary));
            writer.WriteLine(CliMessages.InfoStdlibRepresentativeApisLine(FormatExportSummary(metadata.RepresentativeApis)));

            foreach (var module in group.OrderBy(module => module, StringComparer.Ordinal))
            {
                var exports = PrecompiledModuleRegistry.GetExports(module);
                writer.WriteLine(CliMessages.InfoStdlibModuleLine(FormatLibraryModulePath(module, isPrelude)));
                writer.WriteLine(CliMessages.InfoStdlibValuesLine(FormatExportSummary(exports.Values)));
                writer.WriteLine(CliMessages.InfoStdlibFunctionsLine(FormatExportSummary(exports.Functions)));
                writer.WriteLine(CliMessages.InfoStdlibTypesLine(FormatExportSummary(exports.Types)));
                writer.WriteLine(CliMessages.InfoStdlibTraitsLine(FormatExportSummary(exports.Traits)));
                writer.WriteLine(CliMessages.InfoStdlibModulesLine(FormatExportSummary(exports.Modules.Keys.OrderBy(name => name, StringComparer.Ordinal).ToArray())));
                writer.WriteLine(CliMessages.InfoStdlibConstructorsLine(FormatExportSummary(exports.Constructors)));
            }
        }
    }

    private static StdlibCategoryMetadata GetCategoryMetadata(string category)
    {
        return StdlibCategoryMetadataMap.TryGetValue(category, out var metadata)
            ? metadata
            : StdlibCategoryMetadataMap[OtherCategory];
    }

    private static string FormatExportSummary(IReadOnlyList<string> values)
    {
        return values.Count == 0 ? "-" : string.Join(", ", values);
    }

    private static string FormatLibraryModulePath(string modulePath, bool isPrelude)
    {
        const string stdPrefix = "std/";
        var relative = modulePath.StartsWith(stdPrefix, StringComparison.Ordinal)
            ? modulePath[stdPrefix.Length..]
            : modulePath;
        return isPrelude ? $"Prelude.{relative}" : $"std.{relative}";
    }

    internal sealed record StdlibCategoryMetadata(
        string Label,
        string Summary,
        IReadOnlyList<string> RepresentativeApis);
}
