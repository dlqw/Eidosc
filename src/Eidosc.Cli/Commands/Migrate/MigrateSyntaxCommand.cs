using System.CommandLine;
using System.CommandLine.NamingConventionBinder;
using System.Reflection;
using System.Text.Json;
using Eidosc;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Parsing.Handwritten;
using Eidosc.Parsing.Lexer;
using Eidosc.ProjectSystem;
using Eidosc.Utilities;
using Eidosc.Utils;
using AstAttribute = Eidosc.Ast.Attribute;

namespace Eidosc.Cli.Commands.Migrate;

public sealed class MigrateSyntaxOptions
{
    public string Path { get; set; } = ".";
    public string From { get; set; } = EidosLanguageVersions.Legacy;
    public string To { get; set; } = EidosLanguageVersions.Current;
    public bool DryRun { get; set; }
    public string? Report { get; set; }
}

public static class MigrateSyntaxCommand
{
    public static Command Create()
    {
        var pathArgument = new Argument<string>(
            "path",
            getDefaultValue: () => ".",
            description: "Project directory, eidos.toml, or .eidos source file to inspect.");
        var fromOption = new Option<string>("--from", () => EidosLanguageVersions.Legacy, "Source syntax version.");
        var toOption = new Option<string>("--to", () => EidosLanguageVersions.Current, "Target syntax version.");
        var dryRunOption = new Option<bool>("--dry-run", "Print and report the migration plan without rewriting files.");
        var reportOption = new Option<string?>("--report", "Write a JSON migration plan report.");

        var command = new Command("syntax", "Plan syntax-version migration for an Eidos project or source file.")
        {
            pathArgument,
            fromOption,
            toOption,
            dryRunOption,
            reportOption
        };

        command.Handler = CommandHandler.Create<MigrateSyntaxOptions>(Run);
        return command;
    }

    private static int Run(MigrateSyntaxOptions options)
    {
        if (!EidosLanguageVersions.IsMigrationVersion(options.From))
        {
            Console.Error.WriteLine($"Unsupported source syntax version '{options.From}'.");
            return 2;
        }

        if (!EidosLanguageVersions.IsMigrationVersion(options.To))
        {
            Console.Error.WriteLine($"Unsupported target syntax version '{options.To}'.");
            return 2;
        }

        if (string.Equals(options.From, options.To, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("Source and target syntax versions are identical.");
            return 2;
        }

        SyntaxMigrationPlan plan;
        try
        {
            plan = SyntaxMigrationPlanner.CreatePlan(options.Path, options.From, options.To);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine($"Syntax migration plan: {plan.FromSyntax} -> {plan.ToSyntax}");
        Console.WriteLine($"Root: {plan.RootPath}");
        Console.WriteLine($"Manifest: {(plan.ManifestPath ?? "<none>")}");
        Console.WriteLine($"Manifest update: {(plan.ManifestNeedsUpdate ? "yes" : "no")}");
        Console.WriteLine($"Source files: {plan.SourceFiles.Length}");
        Console.WriteLine($"Source rewrite status: {plan.SourceRewriteStatus}");
        Console.WriteLine($"Source edits: {plan.TotalEditCount}");

        if (!string.IsNullOrWhiteSpace(options.Report))
        {
            WriteReport(options.Report, plan);
            Console.WriteLine($"Report: {Path.GetFullPath(options.Report)}");
        }

        if (!options.DryRun)
        {
            try
            {
                SyntaxMigrationPlanner.ApplyPlan(plan);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                Console.Error.WriteLine(ex.Message);
                return 1;
            }
        }

        return 0;
    }

    private static void WriteReport(string reportPath, SyntaxMigrationPlan plan)
    {
        var fullPath = Path.GetFullPath(reportPath);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(
            plan,
            new JsonSerializerOptions
            {
                WriteIndented = true
            });
        File.WriteAllText(fullPath, json);
    }
}

public sealed record SyntaxMigrationPlan(
    string RootPath,
    string? ManifestPath,
    string FromSyntax,
    string ToSyntax,
    string CurrentManifestSyntax,
    bool ManifestNeedsUpdate,
    string SourceRewriteStatus,
    string[] SourceFiles,
    SyntaxMigrationFilePlan[] FilePlans)
{
    public int TotalEditCount => FilePlans.Sum(static file => file.Edits.Length);
}

public sealed record SyntaxMigrationFilePlan(
    string SourcePath,
    string Status,
    string[] Diagnostics,
    SyntaxMigrationEdit[] Edits);

public sealed record SyntaxMigrationEdit(
    int Start,
    int Length,
    string Replacement,
    string Kind,
    string Description);

public static class SyntaxMigrationPlanner
{
    private static readonly string[] IgnoredDirectoryNames =
    [
        ".git",
        ".eidos",
        "bin",
        "obj",
        "build",
        "debug",
        "tmp"
    ];

    public static SyntaxMigrationPlan CreatePlan(string inputPath, string fromSyntax, string toSyntax)
    {
        var fullPath = Path.GetFullPath(string.IsNullOrWhiteSpace(inputPath) ? "." : inputPath);
        if (File.Exists(fullPath) && string.Equals(Path.GetExtension(fullPath), ".eidos", StringComparison.OrdinalIgnoreCase))
        {
            return CreateSingleFilePlan(fullPath, fromSyntax, toSyntax);
        }

        var manifestPath = ResolveManifestPath(fullPath);
        if (manifestPath != null)
        {
            return CreateProjectPlan(manifestPath, fromSyntax, toSyntax);
        }

        if (Directory.Exists(fullPath))
        {
            return CreateDirectoryPlan(fullPath, fromSyntax, toSyntax);
        }

        throw new InvalidOperationException($"Migration input '{inputPath}' is not a project directory, eidos.toml, or .eidos source file.");
    }

    public static void ApplyPlan(SyntaxMigrationPlan plan)
    {
        foreach (var filePlan in plan.FilePlans)
        {
            if (!string.Equals(filePlan.Status, "ready", StringComparison.Ordinal) &&
                !string.Equals(filePlan.Status, "unchanged", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cannot apply migration because '{filePlan.SourcePath}' is '{filePlan.Status}'.");
            }
        }

        foreach (var filePlan in plan.FilePlans)
        {
            if (filePlan.Edits.Length == 0)
            {
                continue;
            }

            var source = File.ReadAllText(filePlan.SourcePath);
            File.WriteAllText(filePlan.SourcePath, ApplyEdits(source, filePlan.Edits));
        }

        if (plan.ManifestNeedsUpdate && plan.ManifestPath != null)
        {
            var manifest = EidosProjectManifestDocument.Load(plan.ManifestPath);
            manifest.Language ??= new EidosProjectLanguageManifestDocument();
            manifest.ManifestSchema = 3;
            manifest.Language.Version = plan.ToSyntax;
            manifest.Save(plan.ManifestPath);
        }
    }

    private static SyntaxMigrationPlan CreateProjectPlan(string manifestPath, string fromSyntax, string toSyntax)
    {
        var loaded = EidosProjectConfigurationLoader.LoadFromPath(manifestPath);
        var sourceFiles = CollectProjectSourceFiles(loaded)
            .Select(Path.GetFullPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var filePlans = CreateFilePlans(sourceFiles);

        return new SyntaxMigrationPlan(
            loaded.ProjectDirectory,
            loaded.FilePath,
            fromSyntax,
            toSyntax,
            loaded.Configuration.LanguageVersion,
            !string.Equals(loaded.Configuration.LanguageVersion, toSyntax, StringComparison.Ordinal),
            GetSourceRewriteStatus(filePlans),
            sourceFiles,
            filePlans);
    }

    private static SyntaxMigrationPlan CreateSingleFilePlan(string sourceFilePath, string fromSyntax, string toSyntax)
    {
        sourceFilePath = Path.GetFullPath(sourceFilePath);
        var filePlans = CreateFilePlans([sourceFilePath]);
        return new SyntaxMigrationPlan(
            Path.GetDirectoryName(sourceFilePath) ?? Directory.GetCurrentDirectory(),
            null,
            fromSyntax,
            toSyntax,
            EidosLanguageVersions.DefaultForExistingProjects,
            false,
            GetSourceRewriteStatus(filePlans),
            [sourceFilePath],
            filePlans);
    }

    private static SyntaxMigrationPlan CreateDirectoryPlan(string directoryPath, string fromSyntax, string toSyntax)
    {
        var sourceFiles = EnumerateEidosFiles(directoryPath)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var filePlans = CreateFilePlans(sourceFiles);

        return new SyntaxMigrationPlan(
            directoryPath,
            null,
            fromSyntax,
            toSyntax,
            EidosLanguageVersions.DefaultForExistingProjects,
            false,
            GetSourceRewriteStatus(filePlans),
            sourceFiles,
            filePlans);
    }

    private static SyntaxMigrationFilePlan[] CreateFilePlans(IReadOnlyList<string> sourceFiles)
    {
        return sourceFiles
            .Select(CreateFilePlan)
            .ToArray();
    }

    private static SyntaxMigrationFilePlan CreateFilePlan(string sourcePath)
    {
        var source = File.ReadAllText(sourcePath);
        var tokens = Tokenize(source, sourcePath);
        var (ast, diagnostics) = SyntaxParser.Parse(tokens, sourcePath);

        // Token-level cons-operator rewrite runs independently of AST parsing: a legacy
        // `head :: tail` (whitespace around ::) is no longer parseable as cons once `::`
        // leaves the operator table, so the AST may be null or skip the containing body.
        // Scanning the full token stream catches it regardless.
        var edits = new List<SyntaxMigrationEdit>();
        var fullSpan = new SourceSpan(new SourceLocation(0, 1, 1, sourcePath), source.Length);
        AddWhitespaceSeparatedConsEdits(source, tokens, fullSpan, edits);

        if (ast == null)
        {
            return new SyntaxMigrationFilePlan(
                sourcePath,
                "parse-error",
                diagnostics.Select(static diagnostic => diagnostic.ToString() ?? "").ToArray(),
                NormalizeEdits(edits));
        }
        var moduleLevelLetDecls = CollectModuleLevelLetDecls(ast);
        var functionsRequiringFfi = CollectFunctionsRequiringFfi(ast);
        var functionsRequiringIo = CollectFunctionsRequiringIo(ast);
        var migratedImplRanges = AddImplInstanceMigrationEdits(source, tokens, ast, edits);
        foreach (var node in EnumerateAst(ast))
        {
            if (IsInsideAnyRange(node.Span, migratedImplRanges))
            {
                continue;
            }

            switch (node)
            {
                case FuncDef funcDef:
                    AddFunctionHeaderEdits(source, tokens, funcDef.Span, edits);
                    var requiredEffectNames = new List<string>();
                    if (functionsRequiringFfi.Contains(funcDef))
                    {
                        requiredEffectNames.Add(WellKnownStrings.BuiltinAbilities.FFI);
                    }
                    if (functionsRequiringIo.Contains(funcDef))
                    {
                        requiredEffectNames.Add(WellKnownStrings.BuiltinAbilities.IO);
                    }
                    AddNeedEffectEdit(tokens, funcDef, requiredEffectNames, edits);
                    break;
                case FuncDecl funcDecl:
                    AddFunctionHeaderEdits(source, tokens, funcDecl.Span, edits);
                    break;
                case LetDecl letDecl:
                    if (moduleLevelLetDecls.Contains(letDecl) && AddModuleLetDeclEdits(source, tokens, letDecl, edits))
                    {
                        break;
                    }

                    if (!moduleLevelLetDecls.Contains(letDecl))
                    {
                        AddLetDeclEdits(tokens, letDecl, edits);
                    }
                    break;
                case Assignment assignment:
                    AddAssignmentEdit(tokens, assignment, edits);
                    break;
                case AdtDef adtDef:
                    AddStaticKindDeclarationEdits(source, tokens, adtDef.Span, "type", "type", edits);
                    break;
                case TraitDef traitDef:
                    AddStaticKindDeclarationEdits(source, tokens, traitDef.Span, "trait", "trait", edits);
                    break;
                case EffectDef effectDef:
                    AddStaticKindDeclarationEdits(source, tokens, effectDef.Span, "effect", "effect", edits);
                    break;
                case ModuleDecl moduleDecl:
                    AddModuleDeclarationEdits(tokens, moduleDecl, source.Length, edits);
                    break;
                case ImportDecl importDecl:
                    AddImportPathEdits(tokens, importDecl, edits);
                    AddImportModuleAliasEdits(tokens, importDecl, edits);
                    break;
            }
        }

        var normalizedEdits = NormalizeEdits(edits);
        return new SyntaxMigrationFilePlan(
            sourcePath,
            normalizedEdits.Length == 0 ? "unchanged" : "ready",
            diagnostics.Select(static diagnostic => diagnostic.ToString() ?? "").ToArray(),
            normalizedEdits);
    }

    private static HashSet<FuncDef> CollectFunctionsRequiringFfi(EidosAstNode ast)
    {
        return CollectFunctionsRequiringNativeEffect(ast, IsFfiOrNativeCall);
    }

    private static HashSet<FuncDef> CollectFunctionsRequiringIo(EidosAstNode ast)
    {
        return CollectFunctionsRequiringNativeEffect(
            ast,
            static (call, _) => IoNativeCallNames.Contains(call));
    }

    private static HashSet<FuncDef> CollectFunctionsRequiringNativeEffect(
        EidosAstNode ast,
        Func<string, HashSet<string>, bool> requiresEffect)
    {
        var functions = EnumerateAst(ast)
            .OfType<FuncDef>()
            .Where(static func => func.Body.Count > 0)
            .ToArray();
        var localFunctionsByName = functions
            .GroupBy(static func => func.Name, StringComparer.Ordinal)
            .Where(static group => !string.IsNullOrWhiteSpace(group.Key))
            .ToDictionary(static group => group.Key, static group => group.ToArray(), StringComparer.Ordinal);
        var ffiDeclarationNames = CollectFfiDeclarationNames(ast);

        var directFunctions = new HashSet<FuncDef>();
        var callsByFunction = new Dictionary<FuncDef, HashSet<string>>();
        foreach (var func in functions)
        {
            var calls = CollectCalledFunctionNames(func);
            callsByFunction[func] = calls;

            if (calls.Any(call => requiresEffect(call, ffiDeclarationNames)))
            {
                directFunctions.Add(func);
            }
        }

        var result = new HashSet<FuncDef>(directFunctions);
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var (func, calls) in callsByFunction)
            {
                if (result.Contains(func))
                {
                    continue;
                }

                if (!calls.Any(call =>
                        localFunctionsByName.TryGetValue(call, out var callees) &&
                        callees.Any(result.Contains)))
                {
                    continue;
                }

                result.Add(func);
                changed = true;
            }
        }

        return result;
    }

    private static HashSet<string> CollectFfiDeclarationNames(EidosAstNode ast)
    {
        return EnumerateAst(ast)
            .Select(static node => node switch
            {
                FuncDef func when HasFfiAttribute(func.Attributes) => func.Name,
                FuncDecl func when HasFfiAttribute(func.Attributes) => func.Name,
                _ => ""
            })
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool HasFfiAttribute(IEnumerable<AstAttribute> attributes)
    {
        return attributes.Any(static attr =>
            string.Equals(attr.Name, "ffi", StringComparison.Ordinal));
    }

    private static HashSet<string> CollectCalledFunctionNames(FuncDef func)
    {
        var calls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in EnumerateAst(func))
        {
            switch (node)
            {
                case CallExpr call:
                    AddCalleeName(call.Function, calls);
                    break;
                case MethodCallExpr methodCall when !string.IsNullOrWhiteSpace(methodCall.MethodName):
                    calls.Add(methodCall.MethodName);
                    break;
                case InfixCallExpr infixCall when !string.IsNullOrWhiteSpace(infixCall.FunctionName):
                    calls.Add(infixCall.FunctionName);
                    break;
            }
        }

        return calls;
    }

    private static void AddCalleeName(EidosAstNode? callee, HashSet<string> calls)
    {
        switch (callee)
        {
            case IdentifierExpr identifier when !string.IsNullOrWhiteSpace(identifier.Name):
                calls.Add(identifier.Name);
                break;
            case PathExpr path:
                if (!string.IsNullOrWhiteSpace(path.Name))
                {
                    calls.Add(path.Name);
                }

                if (IsFfiPath(path))
                {
                    calls.Add(BuildQualifiedCallName(path));
                }
                break;
            case CallExpr nestedCall:
                AddCalleeName(nestedCall.Function, calls);
                break;
            case MethodCallExpr methodCall when !string.IsNullOrWhiteSpace(methodCall.MethodName):
                calls.Add(methodCall.MethodName);
                break;
            case IndexExpr { IsTypeApplication: true } typeApplication:
                AddCalleeName(typeApplication.Object, calls);
                break;
        }
    }

    private static bool IsFfiOrNativeCall(string callName, HashSet<string> ffiDeclarationNames)
    {
        return ffiDeclarationNames.Contains(callName) ||
               FfiNativeCallNames.Contains(callName) ||
               callName.StartsWith("FFI::", StringComparison.Ordinal) ||
               callName.StartsWith("ptr_load_", StringComparison.Ordinal) ||
               callName.StartsWith("ptr_store_", StringComparison.Ordinal) ||
               callName.StartsWith("math_", StringComparison.Ordinal);
    }

    private static bool IsFfiPath(PathExpr path)
    {
        return string.Equals(path.PackageAlias, "FFI", StringComparison.Ordinal) ||
               path.ModulePath.Any(static part => string.Equals(part, "FFI", StringComparison.Ordinal)) ||
               (string.Equals(path.PackageAlias, "Std", StringComparison.Ordinal) &&
                path.ModulePath.Any(static part => string.Equals(part, "FFI", StringComparison.Ordinal)));
    }

    private static string BuildQualifiedCallName(PathExpr path)
    {
        var parts = path.Path;
        return string.Join("::", parts);
    }

    private static readonly HashSet<string> FfiNativeCallNames = new(StringComparer.Ordinal)
    {
        "cfn_from",
        "cfn_call",
        "ptr_add",
        "ptr_null",
        "ptr_is_null",
        "ptr_equals",
        "ptr_load_int",
        "ptr_load_float",
        "ptr_load_ptr",
        "ptr_load_i32",
        "ptr_load_i8",
        "ptr_load_bool",
        "ptr_load_as",
        "ptr_store_int",
        "ptr_store_float",
        "ptr_store_ptr",
        "ptr_store_i32",
        "ptr_store_i8",
        "ptr_store_bool",
        "ptr_store_as",
        "value_box",
        "value_unbox",
        "value_box_free",
        "string_to_cstr",
        "string_from_cstr",
        "string_from_cstr_raw",
    };

    private static readonly HashSet<string> IoNativeCallNames = new(StringComparer.Ordinal)
    {
        "sleep_ms",
        "print_string",
        "print_int",
        "print_float",
        "print_bool",
        "print_char",
        "print_newline",
        "read_char",
        "read_line",
        "file_exists",
        "file_read_all_text",
        "file_write_all_text",
        "io_last_error",
        "io_last_success"
    };

    private static List<SourceSpan> AddImplInstanceMigrationEdits(
        string source,
        IReadOnlyList<Token> tokens,
        EidosAstNode ast,
        List<SyntaxMigrationEdit> edits)
    {
        var implFunctions = EnumerateAst(ast)
            .OfType<FuncDef>()
            .Select(func => new ImplFunctionMigrationInfo(
                func,
                GetImplAttributes(func)
                    .Select(attr => ExtractAttributeArgumentText(source, attr))
                    .Where(static text => !string.IsNullOrWhiteSpace(text))
                    .Distinct(StringComparer.Ordinal)
                    .ToArray()))
            .Where(static info => info.TraitRefs.Length > 0)
            .OrderBy(static info => info.Function.Span.Position)
            .ToArray();

        var migratedRanges = new List<SourceSpan>();
        if (implFunctions.Length == 0)
        {
            return migratedRanges;
        }

        var currentRun = new List<ImplFunctionMigrationInfo>();
        foreach (var info in implFunctions)
        {
            if (currentRun.Count == 0 ||
                ContainsOnlyWhitespaceOrComments(source, currentRun[^1].Function.Span.EndPosition, GetImplReplacementStart(source, info.Function)))
            {
                currentRun.Add(info);
                continue;
            }

            AddImplRunMigrationEdit(source, tokens, currentRun, edits, migratedRanges);
            currentRun.Clear();
            currentRun.Add(info);
        }

        if (currentRun.Count > 0)
        {
            AddImplRunMigrationEdit(source, tokens, currentRun, edits, migratedRanges);
        }

        return migratedRanges;
    }

    private static void AddImplRunMigrationEdit(
        string source,
        IReadOnlyList<Token> tokens,
        IReadOnlyList<ImplFunctionMigrationInfo> run,
        List<SyntaxMigrationEdit> edits,
        List<SourceSpan> migratedRanges)
    {
        var start = GetImplReplacementStart(source, run[0].Function);
        var end = run[^1].Function.Span.EndPosition;
        if (start < 0 || end <= start)
        {
            return;
        }

        var indent = source[start..run[0].Function.Attributes.Min(static attr => attr.Span.Position)];
        var blocks = new List<string>();
        foreach (var traitRef in run.SelectMany(static info => info.TraitRefs).Distinct(StringComparer.Ordinal))
        {
            var methods = run
                .Where(info => info.TraitRefs.Contains(traitRef, StringComparer.Ordinal))
                .Select(info => BuildNameFirstMethodText(source, tokens, info.Function))
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .ToArray();
            if (methods.Length == 0)
            {
                continue;
            }

            var instanceName = BuildInstanceName(traitRef, source, run.First(info => info.TraitRefs.Contains(traitRef, StringComparer.Ordinal)).Function);
            var blockLines = new List<string>
            {
                $"{indent}{instanceName} :: instance {traitRef} {{"
            };
            foreach (var method in methods)
            {
                blockLines.AddRange(IndentLines(method.TrimEnd(), indent + "    "));
            }

            blockLines.Add($"{indent}}}");
            blocks.Add(string.Join(Environment.NewLine, blockLines));
        }

        if (blocks.Count == 0)
        {
            return;
        }

        edits.Add(new SyntaxMigrationEdit(
            start,
            end - start,
            string.Join(Environment.NewLine + Environment.NewLine, blocks),
            "trait-impl-instance",
            "Rewrite legacy @impl function declarations to named instance evidence blocks."));
        migratedRanges.Add(new SourceSpan(new SourceLocation(start, 0, 0), end - start));
    }

    private static List<AstAttribute> GetImplAttributes(FuncDef funcDef)
    {
        return funcDef.Attributes
            .Where(static attr => string.Equals(attr.Name, "impl", StringComparison.Ordinal))
            .ToList();
    }

    private static string ExtractAttributeArgumentText(string source, AstAttribute attribute)
    {
        if (attribute.ArgumentTexts.Count == 1 && !string.IsNullOrWhiteSpace(attribute.ArgumentTexts[0]))
        {
            return attribute.ArgumentTexts[0].Trim();
        }

        if (attribute.Span.Position < 0 || attribute.Span.EndPosition > source.Length)
        {
            return "";
        }

        var text = source[attribute.Span.Position..attribute.Span.EndPosition];
        var open = text.IndexOf('(');
        var close = text.LastIndexOf(')');
        return open >= 0 && close > open
            ? text[(open + 1)..close].Trim()
            : "";
    }

    private static int GetImplReplacementStart(string source, FuncDef funcDef)
    {
        var firstAttributeStart = funcDef.Attributes.Count > 0
            ? funcDef.Attributes.Min(static attr => attr.Span.Position)
            : funcDef.Span.Position;
        var lineStart = source.LastIndexOf('\n', Math.Max(0, firstAttributeStart - 1));
        return lineStart < 0 ? 0 : lineStart + 1;
    }

    private static bool ContainsOnlyWhitespaceOrComments(string source, int start, int end)
    {
        if (end < start)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(source[start..end]);
    }

    private static string BuildNameFirstMethodText(string source, IReadOnlyList<Token> tokens, FuncDef funcDef)
    {
        var funcIndex = FindTokenIndex(tokens, funcDef.Span, "func");
        if (funcIndex < 0)
        {
            return "";
        }

        var nameIndex = NextContentTokenIndex(tokens, funcIndex + 1, funcDef.Span);
        var colonIndex = FindTopLevelTokenIndex(tokens, funcDef.Span, ":");
        if (nameIndex < 0 || colonIndex < 0 || colonIndex <= nameIndex)
        {
            return "";
        }

        var start = tokens[funcIndex].Location.Position;
        var end = funcDef.Span.EndPosition;
        var methodText = source[start..end];
        var bodyEdits = new List<SyntaxMigrationEdit>();
        foreach (var child in EnumerateAst(funcDef).Skip(1))
        {
            switch (child)
            {
                case LetDecl letDecl:
                    AddLetDeclEdits(tokens, letDecl, bodyEdits);
                    break;
                case Assignment assignment:
                    AddAssignmentEdit(tokens, assignment, bodyEdits);
                    break;
            }
        }
        AddWhitespaceSeparatedConsEdits(source, tokens, funcDef.Span, bodyEdits);

        foreach (var edit in NormalizeEdits(bodyEdits).OrderByDescending(static edit => edit.Start))
        {
            var relativeStart = edit.Start - start;
            if (relativeStart < 0 || relativeStart > methodText.Length)
            {
                continue;
            }

            methodText = methodText
                .Remove(relativeStart, edit.Length)
                .Insert(relativeStart, edit.Replacement);
        }

        var header = source[tokens[nameIndex].Location.Position..tokens[colonIndex].Location.Position].Trim();
        var headerLength = tokens[colonIndex].Location.Position + tokens[colonIndex].Length - start;
        return methodText.Remove(0, headerLength).Insert(0, $"{header} ::");
    }

    private static string BuildInstanceName(string traitRef, string source, FuncDef funcDef)
    {
        var traitName = LastIdentifier(BeforeTypeArgs(traitRef));
        var typeArgs = BetweenTypeArgs(traitRef);
        var suffix = string.IsNullOrWhiteSpace(typeArgs)
            ? BuildTypeKeyFromFirstParameter(source, funcDef)
            : BuildIdentifierKey(typeArgs);
        var raw = traitName + suffix;
        return string.IsNullOrWhiteSpace(raw) ? "MigratedInstance" : raw;
    }

    private static string BuildTypeKeyFromFirstParameter(string source, FuncDef funcDef)
    {
        if (funcDef.Signature.FirstOrDefault() is ArrowType arrow &&
            arrow.ParamType.Span.Position >= 0 &&
            arrow.ParamType.Span.EndPosition <= source.Length)
        {
            return BuildIdentifierKey(source[arrow.ParamType.Span.Position..arrow.ParamType.Span.EndPosition]);
        }

        return "";
    }

    private static string BeforeTypeArgs(string text)
    {
        var bracket = text.IndexOf('[');
        return bracket < 0 ? text : text[..bracket];
    }

    private static string BetweenTypeArgs(string text)
    {
        var open = text.IndexOf('[');
        var close = text.LastIndexOf(']');
        return open >= 0 && close > open ? text[(open + 1)..close] : "";
    }

    private static string LastIdentifier(string text)
    {
        var identifiers = ExtractIdentifiers(text);
        return identifiers.Count == 0 ? "" : identifiers[^1];
    }

    private static string BuildIdentifierKey(string text)
    {
        return string.Concat(ExtractIdentifiers(text));
    }

    private static List<string> ExtractIdentifiers(string text)
    {
        var identifiers = new List<string>();
        for (var i = 0; i < text.Length;)
        {
            if (!IsIdentifierStart(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            i++;
            while (i < text.Length && IsIdentifierPart(text[i]))
            {
                i++;
            }

            identifiers.Add(text[start..i]);
        }

        return identifiers;
    }

    private static bool IsIdentifierStart(char c) => char.IsLetter(c) || c == '_';

    private static bool IsIdentifierPart(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static IEnumerable<string> IndentLines(string text, string indent)
    {
        using var reader = new StringReader(text);
        while (reader.ReadLine() is { } line)
        {
            yield return indent + line;
        }
    }

    private static HashSet<EidosAstNode> CollectModuleLevelLetDecls(EidosAstNode ast)
    {
        var result = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
        if (ast is ModuleDecl module)
        {
            CollectModuleLevelLetDecls(module, result);
        }

        return result;
    }

    private static void CollectModuleLevelLetDecls(ModuleDecl module, HashSet<EidosAstNode> result)
    {
        foreach (var declaration in module.Declarations)
        {
            switch (declaration)
            {
                case LetDecl letDecl:
                    result.Add(letDecl);
                    break;
                case ModuleDecl nestedModule:
                    CollectModuleLevelLetDecls(nestedModule, result);
                    break;
            }
        }
    }

    private static bool IsInsideAnyRange(SourceSpan span, IReadOnlyList<SourceSpan> ranges)
    {
        if (span.Position < 0 || ranges.Count == 0)
        {
            return false;
        }

        return ranges.Any(range => span.Position >= range.Position && span.EndPosition <= range.EndPosition);
    }

    private sealed record ImplFunctionMigrationInfo(FuncDef Function, string[] TraitRefs);

    private static IReadOnlyList<Token> Tokenize(string source, string sourcePath)
    {
        var (grammarData, scannerData) = LexerTableBuilder.Build();
        var stream = new SourceStream(source, 4, new SourceLocation(0, 0, 0, sourcePath));
        var context = new LexerContext(stream, scannerData, grammarData.Terminals);
        Scanner.Init(context);
        var tokens = new List<Token>();
        while (Scanner.GetToken(context) is { } token)
        {
            tokens.Add(token);
        }
        return tokens;
    }

    private static IEnumerable<EidosAstNode> EnumerateAst(EidosAstNode root)
    {
        var seen = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
        var pending = new Stack<EidosAstNode>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var node = pending.Pop();
            if (!seen.Add(node))
            {
                continue;
            }

            yield return node;
            foreach (var child in GetAstChildren(node))
            {
                pending.Push(child);
            }
        }
    }

    private static IEnumerable<EidosAstNode> GetAstChildren(EidosAstNode node)
    {
        foreach (var property in node.GetType().GetProperties(BindingFlags.Instance | BindingFlags.Public))
        {
            if (!property.CanRead || property.GetIndexParameters().Length != 0)
            {
                continue;
            }

            if (property.Name is nameof(EidosAstNode.InferredType))
            {
                continue;
            }

            var value = property.GetValue(node);
            switch (value)
            {
                case EidosAstNode child:
                    yield return child;
                    break;
                case IEnumerable<EidosAstNode> children:
                    foreach (var child in children)
                    {
                        yield return child;
                    }
                    break;
            }
        }
    }

    private static bool AddModuleLetDeclEdits(
        string source,
        IReadOnlyList<Token> tokens,
        LetDecl letDecl,
        List<SyntaxMigrationEdit> edits)
    {
        if (letDecl.IsMutable)
        {
            return false;
        }

        var letTokenIndex = FindTokenIndex(tokens, letDecl.Span, "let");
        if (letTokenIndex < 0)
        {
            return false;
        }

        var nameStartIndex = NextContentTokenIndex(tokens, letTokenIndex + 1, letDecl.Span);
        var colonIndex = FindTopLevelTokenIndex(tokens, letDecl.Span, ":");
        var equalsIndex = FindTopLevelTokenIndex(tokens, letDecl.Span, "=");
        if (nameStartIndex < 0 || equalsIndex < 0 || equalsIndex <= nameStartIndex)
        {
            return false;
        }

        var nameStart = tokens[nameStartIndex].Location.Position;
        var equalsStart = tokens[equalsIndex].Location.Position;
        var editStart = tokens[letTokenIndex].Location.Position;
        var editEnd = equalsStart + tokens[equalsIndex].Length;

        if (letDecl.TypeAnnotation != null)
        {
            if (colonIndex < 0 || colonIndex <= nameStartIndex || equalsIndex <= colonIndex)
            {
                return false;
            }

            var colonStart = tokens[colonIndex].Location.Position;
            var typeStart = colonStart + tokens[colonIndex].Length;
            var typedBindingText = source[nameStart..colonStart].Trim();
            var typeText = source[typeStart..equalsStart].Trim();
            if (typedBindingText.Length == 0 || typeText.Length == 0)
            {
                return false;
            }

            edits.Add(new SyntaxMigrationEdit(
                editStart,
                editEnd - editStart,
                $"{typedBindingText} :: {typeText} =",
                "module-let-binding",
                "Rewrite legacy module-level typed let declaration to name-first static value binding."));
            return true;
        }

        var bindingText = source[nameStart..equalsStart].Trim();
        if (bindingText.Length == 0)
        {
            return false;
        }

        edits.Add(new SyntaxMigrationEdit(
            editStart,
            editEnd - editStart,
            $"{bindingText} ::",
            "module-let-binding",
            "Rewrite legacy module-level inferred let declaration to name-first static value binding."));
        return true;
    }

    private static void AddLetDeclEdits(IReadOnlyList<Token> tokens, LetDecl letDecl, List<SyntaxMigrationEdit> edits)
    {
        var letTokenIndex = FindTokenIndex(tokens, letDecl.Span, "let");
        if (letTokenIndex < 0)
        {
            return;
        }

        var nextIndex = NextContentTokenIndex(tokens, letTokenIndex + 1, letDecl.Span);
        if (nextIndex < 0)
        {
            return;
        }

        edits.Add(new SyntaxMigrationEdit(
            tokens[letTokenIndex].Location.Position,
            tokens[nextIndex].Location.Position - tokens[letTokenIndex].Location.Position,
            "",
            "let-binding-keyword",
            "Remove legacy let keyword for name-first binding."));

        var equalsIndex = FindTopLevelTokenIndex(tokens, letDecl.Span, "=");
        if (equalsIndex >= 0)
        {
            edits.Add(new SyntaxMigrationEdit(
                tokens[equalsIndex].Location.Position,
                tokens[equalsIndex].Length,
                ":=",
                "let-binding-operator",
                "Rewrite legacy let initializer operator to name-first binding operator."));
        }
    }

    private static void AddFunctionHeaderEdits(
        string source,
        IReadOnlyList<Token> tokens,
        SourceSpan span,
        List<SyntaxMigrationEdit> edits)
    {
        var funcIndex = FindTokenIndex(tokens, span, "func");
        if (funcIndex < 0)
        {
            return;
        }

        var nameIndex = NextContentTokenIndex(tokens, funcIndex + 1, span);
        var colonIndex = FindTopLevelTokenIndex(tokens, span, ":");
        if (nameIndex < 0 || colonIndex < 0 || colonIndex <= nameIndex)
        {
            return;
        }

        var start = tokens[funcIndex].Location.Position;
        var end = tokens[colonIndex].Location.Position + tokens[colonIndex].Length;
        var header = source[tokens[nameIndex].Location.Position..tokens[colonIndex].Location.Position].Trim();
        if (header.Length == 0)
        {
            return;
        }

        edits.Add(new SyntaxMigrationEdit(
            start,
            end - start,
            $"{header} ::",
            "function-declaration",
            "Rewrite legacy func declaration header to name-first static binding."));
    }

    private static void AddNeedEffectEdit(
        IReadOnlyList<Token> tokens,
        FuncDef funcDef,
        IReadOnlyList<string> requiredEffectNames,
        List<SyntaxMigrationEdit> edits)
    {
        if (requiredEffectNames.Count == 0)
        {
            return;
        }

        var missingEffectNames = requiredEffectNames
            .Where(name => !funcDef.RequiredAbilities.Any(ability =>
                ability.Path.Any(part => string.Equals(part, name, StringComparison.Ordinal))))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missingEffectNames.Length == 0)
        {
            return;
        }

        var missingText = string.Join(", ", missingEffectNames);

        var bodyStartIndex = FindTopLevelTokenIndex(tokens, funcDef.Span, "{");
        if (bodyStartIndex < 0)
        {
            return;
        }

        var needIndex = FindTopLevelTokenIndex(tokens, funcDef.Span, "need");
        if (needIndex >= 0 && needIndex < bodyStartIndex)
        {
            var lastRequirementIndex = PreviousContentTokenIndex(tokens, bodyStartIndex - 1, funcDef.Span);
            if (lastRequirementIndex < 0 || lastRequirementIndex <= needIndex)
            {
                return;
            }

            var insertAfter = tokens[lastRequirementIndex].Location.Position + tokens[lastRequirementIndex].Length;
            edits.Add(new SyntaxMigrationEdit(
                insertAfter,
                0,
                $", {missingText}",
                "native-ability-requirement",
                "Add explicit ability requirement for migrated native/FFI call chain."));
            return;
        }

        var signatureEndIndex = PreviousContentTokenIndex(tokens, bodyStartIndex - 1, funcDef.Span);
        if (signatureEndIndex < 0)
        {
            return;
        }

        edits.Add(new SyntaxMigrationEdit(
            tokens[signatureEndIndex].Location.Position + tokens[signatureEndIndex].Length,
            0,
            $" need {missingText}",
            "native-ability-requirement",
            "Add explicit ability requirement for migrated native/FFI call chain."));
    }

    private static void AddStaticKindDeclarationEdits(
        string source,
        IReadOnlyList<Token> tokens,
        SourceSpan span,
        string legacyKeyword,
        string nameFirstKind,
        List<SyntaxMigrationEdit> edits)
    {
        var keywordIndex = FindTokenIndex(tokens, span, legacyKeyword);
        if (keywordIndex < 0)
        {
            return;
        }

        var nameIndex = NextContentTokenIndex(tokens, keywordIndex + 1, span);
        if (nameIndex < 0)
        {
            return;
        }

        var boundaryIndex = FindStaticKindBoundaryTokenIndex(tokens, span, keywordIndex + 1);
        if (boundaryIndex < 0 || boundaryIndex <= nameIndex)
        {
            return;
        }

        var start = tokens[keywordIndex].Location.Position;
        var boundaryStart = tokens[boundaryIndex].Location.Position;
        var header = source[tokens[nameIndex].Location.Position..boundaryStart].Trim();
        if (header.Length == 0)
        {
            return;
        }

        edits.Add(new SyntaxMigrationEdit(
            start,
            boundaryStart - start,
            $"{header} :: {nameFirstKind} ",
            $"{legacyKeyword}-declaration",
            $"Rewrite legacy {legacyKeyword} declaration header to name-first {nameFirstKind} binding."));
    }

    private static void AddModuleDeclarationEdits(
        IReadOnlyList<Token> tokens,
        ModuleDecl moduleDecl,
        int sourceLength,
        List<SyntaxMigrationEdit> edits)
    {
        var moduleIndex = FindTokenIndex(tokens, moduleDecl.Span, "module");
        if (moduleIndex < 0 || moduleDecl.Path.Count == 0)
        {
            return;
        }

        var boundaryIndex = FindModuleBoundaryTokenIndex(tokens, moduleDecl.Span, moduleIndex + 1);
        if (boundaryIndex < 0)
        {
            return;
        }

        var legacyPath = ExtractLegacyModulePath(tokens, moduleIndex + 1, boundaryIndex);
        if (!legacyPath.SequenceEqual(moduleDecl.Path, StringComparer.Ordinal))
        {
            return;
        }

        var modulePath = string.Join(".", moduleDecl.Path);
        var start = tokens[moduleIndex].Location.Position;
        var boundary = tokens[boundaryIndex];
        var boundaryText = GetTokenText(boundary);
        if (string.Equals(boundaryText, "{", StringComparison.Ordinal))
        {
            edits.Add(new SyntaxMigrationEdit(
                start,
                boundary.Location.Position - start,
                $"{modulePath} :: module ",
                "module-declaration",
                "Rewrite legacy module block header to name-first module binding."));
            return;
        }

        if (string.Equals(boundaryText, ";", StringComparison.Ordinal))
        {
            edits.Add(new SyntaxMigrationEdit(
                start,
                boundary.Location.Position + boundary.Length - start,
                $"{modulePath} :: module {{",
                "module-declaration",
                "Rewrite legacy module header to name-first module block."));
            edits.Add(new SyntaxMigrationEdit(
                sourceLength,
                0,
                Environment.NewLine + "}" + Environment.NewLine,
                "module-declaration-close",
                "Close synthesized name-first module block."));
        }
    }

    private static void AddImportPathEdits(
        IReadOnlyList<Token> tokens,
        ImportDecl importDecl,
        List<SyntaxMigrationEdit> edits)
    {
        // Module-alias imports are rewritten wholesale (including the path separator
        // conversion) by AddImportModuleAliasEdits; skip per-segment slash edits here to
        // avoid overlapping edits on the same import declaration.
        if (importDecl.Kind == ImportKind.Module && !string.IsNullOrEmpty(importDecl.Alias))
        {
            return;
        }

        if (importDecl.ModulePath.Count <= 1)
        {
            return;
        }

        var importIndex = FindTokenIndex(tokens, importDecl.Span, "import");
        if (importIndex < 0)
        {
            return;
        }

        var endIndex = FindImportPathEndTokenIndex(tokens, importDecl.Span, importIndex + 1);
        if (endIndex < 0)
        {
            return;
        }

        var converted = 0;
        for (var i = importIndex + 1; i < endIndex; i++)
        {
            if (!string.Equals(GetTokenText(tokens[i]), "/", StringComparison.Ordinal))
            {
                continue;
            }

            edits.Add(new SyntaxMigrationEdit(
                tokens[i].Location.Position,
                tokens[i].Length,
                ".",
                "import-module-path",
                "Rewrite legacy slash-separated import module path to 0.5.0-alpha.1 dot-separated module path."));
            converted++;
        }

        if (converted == 0)
        {
            return;
        }
    }

    /// <summary>
    /// Rewrites a legacy module-alias import <c>import PackageAlias::Mod.Path as Alias;</c>
    /// to the 0.5.0-alpha.1 name-first binding form <c>Alias :: import PackageAlias::Mod.Path;</c>.
    /// Only the module-kind aliased import is rewritten; selective/wildcard/unaliased imports
    /// and the leading <c>export</c> modifier are left untouched.
    /// </summary>
    private static void AddImportModuleAliasEdits(
        IReadOnlyList<Token> tokens,
        ImportDecl importDecl,
        List<SyntaxMigrationEdit> edits)
    {
        if (importDecl.Kind != ImportKind.Module || string.IsNullOrEmpty(importDecl.Alias))
        {
            return;
        }

        var importIndex = FindTokenIndex(tokens, importDecl.Span, "import");
        if (importIndex < 0)
        {
            return;
        }

        var asIndex = FindTokenIndex(tokens, importDecl.Span, "as");
        if (asIndex <= importIndex)
        {
            return;
        }

        var aliasIndex = NextContentTokenIndex(tokens, asIndex + 1, importDecl.Span);
        if (aliasIndex < 0)
        {
            return;
        }

        // Reconstruct the module path from AST so the rewrite is independent of whether the
        // slash-to-dot path edit ran first. Package alias and module path use `::` and `.`.
        var modulePathText = string.Join(".", importDecl.ModulePath);
        var qualifiedPath = string.IsNullOrEmpty(importDecl.PackageAlias)
            ? modulePathText
            : $"{importDecl.PackageAlias}::{modulePathText}";

        var start = tokens[importIndex].Location.Position;
        var aliasToken = tokens[aliasIndex];
        var replacementEnd = aliasToken.Location.Position + aliasToken.Length;

        edits.Add(new SyntaxMigrationEdit(
            start,
            replacementEnd - start,
            $"{importDecl.Alias} :: import {qualifiedPath}",
            "import-module-alias-name-first",
            "Rewrite legacy module-alias import to 0.5.0-alpha.1 name-first binding form."));
    }

    private static int FindImportPathEndTokenIndex(IReadOnlyList<Token> tokens, SourceSpan span, int startIndex)
    {
        for (var i = startIndex; i < tokens.Count; i++)
        {
            if (!IsInside(tokens[i], span))
            {
                break;
            }

            var text = GetTokenText(tokens[i]);
            if (text is "as" or ";" or "{" or "*")
            {
                return i;
            }
        }

        var lastInsideIndex = -1;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            if (IsInside(tokens[i], span))
            {
                lastInsideIndex = i;
            }
        }

        return lastInsideIndex + 1;
    }

    private static string[] ExtractLegacyModulePath(IReadOnlyList<Token> tokens, int startIndex, int boundaryIndex)
    {
        var parts = new List<string>();
        for (var i = startIndex; i < boundaryIndex; i++)
        {
            var text = GetTokenText(tokens[i]);
            if (text == "/")
            {
                continue;
            }

            if (tokens[i] is ContentToken && !string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }

        return parts.ToArray();
    }

    private static void AddAssignmentEdit(IReadOnlyList<Token> tokens, Assignment assignment, List<SyntaxMigrationEdit> edits)
    {
        var assignIndex = FindTopLevelTokenIndex(tokens, assignment.Span, ":=");
        if (assignIndex < 0)
        {
            return;
        }

        edits.Add(new SyntaxMigrationEdit(
            tokens[assignIndex].Location.Position,
            tokens[assignIndex].Length,
            "=",
            "assignment-operator",
            "Rewrite legacy assignment operator to 0.5.0-alpha.1 assignment operator."));
    }

    private static void AddWhitespaceSeparatedConsEdits(
        string source,
        IReadOnlyList<Token> tokens,
        SourceSpan span,
        List<SyntaxMigrationEdit> edits)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!IsInside(token, span) ||
                !string.Equals(GetTokenText(token), "::", StringComparison.Ordinal) ||
                LooksLikeNameFirstStaticBinding(source, tokens, i) ||
                !HasWhitespaceAround(source, token.Location.Position, token.Length))
            {
                continue;
            }

            edits.Add(new SyntaxMigrationEdit(
                token.Location.Position,
                token.Length,
                "+:",
                "cons-operator",
                "Rewrite legacy list cons operator to 0.5.0-alpha.1 cons operator."));
        }
    }

    private static bool HasWhitespaceAround(string source, int position, int length)
    {
        var before = position > 0 ? source[position - 1] : '\0';
        var afterPosition = position + length;
        var after = afterPosition < source.Length ? source[afterPosition] : '\0';
        return char.IsWhiteSpace(before) && char.IsWhiteSpace(after);
    }

    private static SyntaxMigrationEdit[] NormalizeEdits(List<SyntaxMigrationEdit> edits)
    {
        var ordered = edits
            .DistinctBy(static edit => (edit.Start, edit.Length, edit.Replacement))
            .OrderBy(static edit => edit.Start)
            .ToArray();

        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            if (current.Start < previous.Start + previous.Length)
            {
                throw new InvalidOperationException(
                    $"Overlapping syntax migration edits at offsets {previous.Start} and {current.Start}.");
            }
        }

        return ordered;
    }

    private static string ApplyEdits(string source, IReadOnlyList<SyntaxMigrationEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(static edit => edit.Start))
        {
            source = source.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        }

        return source;
    }

    private static string GetSourceRewriteStatus(IReadOnlyList<SyntaxMigrationFilePlan> filePlans)
    {
        if (filePlans.Any(static file => string.Equals(file.Status, "parse-error", StringComparison.Ordinal)))
        {
            return "parse-error";
        }

        return filePlans.Any(static file => file.Edits.Length > 0) ? "ready" : "unchanged";
    }

    private static int FindTokenIndex(IReadOnlyList<Token> tokens, SourceSpan span, string text)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (IsInside(tokens[i], span) && string.Equals(GetTokenText(tokens[i]), text, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    private static int FindTopLevelTokenIndex(IReadOnlyList<Token> tokens, SourceSpan span, string text)
    {
        var depth = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!IsInside(token, span))
            {
                continue;
            }

            var tokenText = GetTokenText(token);
            if (depth == 0 && string.Equals(tokenText, text, StringComparison.Ordinal))
            {
                return i;
            }

            depth += tokenText switch
            {
                "(" or "[" or "{" => 1,
                ")" or "]" or "}" => -1,
                _ => 0
            };
            depth = Math.Max(0, depth);
        }

        return -1;
    }


    private static int FindStaticKindBoundaryTokenIndex(IReadOnlyList<Token> tokens, SourceSpan span, int startIndex)
    {
        var depth = 0;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!IsInside(token, span))
            {
                continue;
            }

            var text = GetTokenText(token);
            if (depth == 0 && text is "{" or "=" or ":" or "requires" or ";")
            {
                return i;
            }

            depth += text switch
            {
                "(" or "[" => 1,
                ")" or "]" => -1,
                _ => 0
            };
            depth = Math.Max(0, depth);
        }

        return -1;
    }

    private static int FindModuleBoundaryTokenIndex(IReadOnlyList<Token> tokens, SourceSpan span, int startIndex)
    {
        for (var i = startIndex; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!IsInside(token, span))
            {
                continue;
            }

            if (GetTokenText(token) is "{" or ";")
            {
                return i;
            }
        }

        var lastInsideIndex = -1;
        for (var i = startIndex; i < tokens.Count; i++)
        {
            if (IsInside(tokens[i], span))
            {
                lastInsideIndex = i;
            }
        }

        if (lastInsideIndex >= 0)
        {
            var nextIndex = NextContentTokenIndex(
                tokens,
                lastInsideIndex + 1,
                new SourceSpan(tokens[lastInsideIndex].Location, int.MaxValue - tokens[lastInsideIndex].Location.Position));
            if (nextIndex >= 0 && GetTokenText(tokens[nextIndex]) is "{" or ";")
            {
                return nextIndex;
            }
        }

        return -1;
    }

    private static int NextContentTokenIndex(IReadOnlyList<Token> tokens, int startIndex, SourceSpan span)
    {
        for (var i = startIndex; i < tokens.Count; i++)
        {
            if (IsInside(tokens[i], span) && tokens[i] is not CommentToken)
            {
                return i;
            }
        }

        return -1;
    }

    private static int PreviousContentTokenIndex(IReadOnlyList<Token> tokens, int startIndex, SourceSpan span)
    {
        for (var i = Math.Min(startIndex, tokens.Count - 1); i >= 0; i--)
        {
            if (IsInside(tokens[i], span) && tokens[i] is not CommentToken)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsInside(Token token, SourceSpan span)
    {
        return token.Location.Position >= span.Position && token.Location.Position < span.EndPosition;
    }

    private static bool LooksLikeNameFirstStaticBinding(string source, IReadOnlyList<Token> tokens, int operatorIndex)
    {
        var op = tokens[operatorIndex];
        var lineStart = source.LastIndexOf('\n', Math.Max(0, op.Location.Position - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;
        var prefix = source[lineStart..op.Location.Position].TrimStart();
        if (prefix.StartsWith("export ", StringComparison.Ordinal))
        {
            prefix = prefix["export ".Length..].TrimStart();
        }

        if (prefix.Length == 0 || prefix.Contains(';') || prefix.Contains('{') || prefix.Contains('}'))
        {
            return false;
        }

        var nextIndex = NextContentTokenIndex(tokens, operatorIndex + 1, new SourceSpan(op.Location, Math.Max(0, source.Length - op.Location.Position)));
        if (nextIndex < 0)
        {
            return false;
        }

        var nextText = GetTokenText(tokens[nextIndex]);
        if (nextText is "module" or "trait" or "instance" or "effect" or "type" or "comptime")
        {
            return true;
        }

        var lineEnd = source.IndexOf('\n', op.Location.Position);
        lineEnd = lineEnd < 0 ? source.Length : lineEnd;
        var suffix = source[(op.Location.Position + op.Length)..lineEnd];
        return suffix.Contains("->", StringComparison.Ordinal) ||
               suffix.Contains("=>", StringComparison.Ordinal) ||
               suffix.TrimStart().StartsWith("comptime ", StringComparison.Ordinal);
    }

    private static string GetTokenText(Token token)
    {
        if (token is ContentToken contentToken)
        {
            return contentToken.Value switch
            {
                string text => text,
                StringId id => id.Resolve(),
                _ => contentToken.TextId.Resolve()
            };
        }

        return token switch
        {
            CommentToken comment => comment.Comment,
            EofToken => "<eof>",
            ErrorToken error => error.Message,
            _ => token.ToString() ?? ""
        };
    }

    private static string? ResolveManifestPath(string fullPath)
    {
        if (File.Exists(fullPath) &&
            string.Equals(Path.GetFileName(fullPath), EidosProjectConfigurationLoader.DefaultFileName, StringComparison.OrdinalIgnoreCase))
        {
            return fullPath;
        }

        if (Directory.Exists(fullPath))
        {
            var candidate = Path.Combine(fullPath, EidosProjectConfigurationLoader.DefaultFileName);
            return File.Exists(candidate) ? candidate : null;
        }

        return null;
    }

    private static IEnumerable<string> CollectProjectSourceFiles(LoadedEidosProjectConfiguration loaded)
    {
        foreach (var sourceRoot in loaded.Configuration.SourceRoots)
        {
            if (Directory.Exists(sourceRoot))
            {
                foreach (var sourceFile in EnumerateEidosFiles(sourceRoot))
                {
                    yield return sourceFile;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateEidosFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(Path.GetFullPath(root));

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            IEnumerable<string> files;
            IEnumerable<string> directories;
            try
            {
                files = Directory.EnumerateFiles(current, "*.eidos", SearchOption.TopDirectoryOnly);
                directories = Directory.EnumerateDirectories(current, "*", SearchOption.TopDirectoryOnly);
            }
            catch (IOException)
            {
                continue;
            }
            catch (UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var directory in directories)
            {
                if (!IgnoredDirectoryNames.Contains(Path.GetFileName(directory), StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(directory);
                }
            }
        }
    }
}
