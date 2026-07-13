using Eidosc.Symbols;
using Eidosc.ProjectSystem;
using Eidosc.Pipeline.TokenRewriting;
using Eidosc.Pipeline;
using Eidosc.Parsing.Lexer;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Patterns;
using Eidosc.Parsing.Handwritten;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public static class PrecompiledModuleRegistry
{
    private const string ResourceExtension = ".eidos";
    private const string InternalAttributeName = "internal";
    private const string StdPackageAlias = "Std";

    private static readonly Lazy<SemanticVersion> CachedStdlibVersion =
        new(LoadStdlibVersion, isThreadSafe: true);

    private static readonly Lazy<string> StdlibRoot =
        new(ResolveStdlibRoot, isThreadSafe: true);

    public static SemanticVersion StdlibVersion => CachedStdlibVersion.Value;

    private static readonly Lazy<IReadOnlyDictionary<string, string>> ModuleSources =
        new(LoadModuleSources, isThreadSafe: true);

    private static readonly Lazy<IReadOnlyDictionary<string, ModuleExportAnalysisResult>> ModuleExportAnalyses =
        new(LoadModuleExportAnalyses, isThreadSafe: true);

    private static readonly Lazy<IReadOnlyDictionary<string, PrecompiledModuleExports>> ModuleExports =
        new(LoadModuleExports, isThreadSafe: true);

    private static readonly Lazy<IReadOnlyDictionary<string, string>> ModuleSourceFiles =
        new(LoadModuleSourceFiles, isThreadSafe: true);

    private static readonly Lazy<ParserArtifacts> SharedParserArtifacts =
        new(LoadOrBuildParserArtifacts, isThreadSafe: true);

    private static readonly Lazy<string> StdlibImageFingerprint =
        new(BuildStdlibImageFingerprint, isThreadSafe: true);

    public static bool TryGetSource(IReadOnlyList<string> modulePath, out string source)
    {
        var key = NormalizeModulePath(modulePath);
        return TryGetSource(key, out source);
    }

    public static bool TryGetSource(string modulePath, out string source)
    {
        source = string.Empty;
        var key = NormalizeModulePath(modulePath);
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (!ModuleSources.Value.TryGetValue(key, out var moduleSource))
        {
            return false;
        }

        source = moduleSource;
        return true;
    }

    public static IReadOnlyList<string> GetAvailableModulePaths()
    {
        return ModuleSources.Value.Keys
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
    }

    public static string GetStdlibImageFingerprint() => StdlibImageFingerprint.Value;

    public static string GetStdlibRoot() => StdlibRoot.Value;

    public static bool IsStdlibSourcePath(string? path)
    {
        return TryGetStdlibRelativePath(path, out _);
    }

    public static bool TryGetModulePathFromSourcePath(string? path, out string modulePath)
    {
        modulePath = string.Empty;
        if (!TryGetStdlibRelativePath(path, out var relative))
        {
            return false;
        }

        if (!relative.EndsWith(ResourceExtension, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        modulePath = NormalizeModulePath(relative[..^ResourceExtension.Length]);
        return modulePath.Length != 0;
    }

    private static bool TryGetStdlibRelativePath(string? path, out string relative)
    {
        relative = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/');
        const string sourceTreeMarker = "/Stdlib/Precompiled/";
        var markerIndex = normalized.IndexOf(
            sourceTreeMarker,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (markerIndex >= 0)
        {
            relative = normalized[(markerIndex + sourceTreeMarker.Length)..];
            return relative.Length != 0;
        }

        try
        {
            var fullPath = Path.GetFullPath(path);
            var root = Path.GetFullPath(StdlibRoot.Value)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var rootPrefix = root + Path.DirectorySeparatorChar;
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!fullPath.StartsWith(rootPrefix, comparison))
            {
                return false;
            }

            relative = Path.GetRelativePath(root, fullPath)
                .Replace(Path.DirectorySeparatorChar, '/');
            return relative.Length != 0;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or NotSupportedException)
        {
            return false;
        }
    }

    public static bool TryGetSourceFilePath(IReadOnlyList<string> modulePath, out string filePath)
    {
        var key = NormalizeModulePath(modulePath);
        return TryGetSourceFilePath(key, out filePath);
    }

    public static bool TryGetSourceFilePath(string modulePath, out string filePath)
    {
        filePath = string.Empty;
        var key = NormalizeModulePath(modulePath);
        if (string.IsNullOrEmpty(key))
        {
            return false;
        }

        if (!ModuleSourceFiles.Value.TryGetValue(key, out var resolvedPath))
        {
            return false;
        }

        filePath = resolvedPath;
        return true;
    }

    public static IReadOnlyList<string> GetExportedValueNames(string modulePath)
    {
        return GetExports(modulePath).Values;
    }

    public static IReadOnlyList<string> GetExportedFunctionNames(string modulePath)
    {
        return GetExports(modulePath).Functions;
    }

    public static IReadOnlyList<string> GetExportedTypeNames(string modulePath)
    {
        return GetExports(modulePath).Types;
    }

    public static IReadOnlyList<string> GetExportedTraitNames(string modulePath)
    {
        return GetExports(modulePath).Traits;
    }

    public static IReadOnlyList<string> GetExportedEffectNames(string modulePath)
    {
        return GetExports(modulePath).Effects;
    }

    public static IReadOnlyList<string> GetExportedConstructorNames(string modulePath)
    {
        return GetExports(modulePath).Constructors;
    }

    public static IReadOnlyList<string> GetExportedModuleNames(string modulePath)
    {
        return GetExports(modulePath).Modules.Keys
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
    }

    public static bool TryGetExportedModulePath(
        string modulePath,
        string exportName,
        out string targetModulePath)
    {
        targetModulePath = string.Empty;
        var exports = GetExports(modulePath);
        if (!exports.Modules.TryGetValue(exportName, out var resolved))
        {
            return false;
        }

        targetModulePath = resolved;
        return true;
    }

    public static PrecompiledModuleExports GetExports(string modulePath)
    {
        var key = NormalizeModulePath(modulePath);
        if (string.IsNullOrEmpty(key))
        {
            return PrecompiledModuleExports.Empty;
        }

        return ModuleExports.Value.TryGetValue(key, out var exports)
            ? exports
            : PrecompiledModuleExports.Empty;
    }

    internal static PrecompiledModuleExports ExtractExportsForTest(string source)
    {
        return AnalyzeSource(source, modulePath: null, moduleSources: null, cache: null, resolutionStack: null).Exports;
    }

    internal static PrecompiledModuleExports ExtractExportsForTest(
        string source,
        string modulePath,
        IReadOnlyDictionary<string, string> moduleSources)
    {
        var cache = new Dictionary<string, ModuleExportAnalysisResult>(StringComparer.OrdinalIgnoreCase);
        var resolutionStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return AnalyzeSource(source, modulePath, moduleSources, cache, resolutionStack).Exports;
    }

    internal static bool TryGetExportedModulePathForTest(
        string source,
        string modulePath,
        IReadOnlyDictionary<string, string> moduleSources,
        string exportName,
        out string targetModulePath)
    {
        var cache = new Dictionary<string, ModuleExportAnalysisResult>(StringComparer.OrdinalIgnoreCase);
        var resolutionStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var analysis = AnalyzeSource(source, modulePath, moduleSources, cache, resolutionStack);
        if (!analysis.Exports.Modules.TryGetValue(exportName, out var resolved))
        {
            targetModulePath = string.Empty;
            return false;
        }

        targetModulePath = resolved;
        return true;
    }

    internal static bool ExportedOwnerDefinesMember(
        string modulePath,
        string ownerName,
        string memberName)
    {
        var key = NormalizeModulePath(modulePath);
        if (string.IsNullOrEmpty(key) ||
            !ModuleExportAnalyses.Value.TryGetValue(key, out var analysis))
        {
            return false;
        }

        return analysis.OwnerDefinesMember(ownerName, memberName, TryGetAnalysisByModulePath);
    }

    internal static bool ExportedOwnerDefinesMemberForTest(
        string source,
        string ownerName,
        string memberName)
    {
        return AnalyzeSource(source, modulePath: null, moduleSources: null, cache: null, resolutionStack: null)
            .OwnerDefinesMember(ownerName, memberName, static _ => null);
    }

    internal static bool ExportedOwnerDefinesMemberForTest(
        string source,
        string ownerName,
        string memberName,
        string modulePath,
        IReadOnlyDictionary<string, string> moduleSources)
    {
        var cache = new Dictionary<string, ModuleExportAnalysisResult>(StringComparer.OrdinalIgnoreCase);
        var resolutionStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var analysis = AnalyzeSource(source, modulePath, moduleSources, cache, resolutionStack);
        return analysis.OwnerDefinesMember(
            ownerName,
            memberName,
            targetModulePath =>
            {
                var normalized = NormalizeModulePath(targetModulePath);
                return cache.TryGetValue(normalized, out var cached)
                    ? cached
                    : null;
            });
    }

    internal static bool TryParseModuleDeclForTest(
        string source,
        string? sourceName,
        out ModuleDecl? moduleDecl)
    {
        return TryParseModuleDecl(source, sourceName, out moduleDecl);
    }

    private static IReadOnlyDictionary<string, string> LoadModuleSources()
    {
        var moduleSources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = StdlibRoot.Value;
        foreach (var path in Directory.EnumerateFiles(root, $"*{ResourceExtension}", SearchOption.AllDirectories)
                     .Order(StringComparer.Ordinal))
        {
            var relativePath = Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/');
            if (string.Equals(relativePath, "eidos.toml", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var modulePath = NormalizeModulePath(relativePath[..^ResourceExtension.Length]);
            if (string.IsNullOrEmpty(modulePath) || !moduleSources.TryAdd(modulePath, File.ReadAllText(path)))
            {
                throw new InvalidOperationException($"External Std component contains duplicate module '{modulePath}'.");
            }
        }

        if (moduleSources.Count == 0)
        {
            throw new InvalidOperationException($"External Std component '{root}' contains no .eidos modules.");
        }

        return moduleSources;
    }

    private static string BuildStdlibImageFingerprint()
    {
        var builder = new System.Text.StringBuilder();
        builder.Append("stdlib-image-v1:");
        builder.AppendLine(StdlibVersion.ToString());
        foreach (var (modulePath, source) in ModuleSources.Value
                     .OrderBy(static entry => entry.Key, StringComparer.Ordinal))
        {
            builder.Append(modulePath);
            builder.Append(':');
            builder.AppendLine(ContentHash.ComputeHash(source));
        }

        return ContentHash.ComputeHash(builder.ToString());
    }

    private static IReadOnlyDictionary<string, ModuleExportAnalysisResult> LoadModuleExportAnalyses()
    {
        var cache = new Dictionary<string, ModuleExportAnalysisResult>(StringComparer.OrdinalIgnoreCase);
        var resolutionStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (modulePath, source) in ModuleSources.Value)
        {
            _ = AnalyzeSource(source, modulePath, ModuleSources.Value, cache, resolutionStack);
        }

        return cache;
    }

    private static IReadOnlyDictionary<string, PrecompiledModuleExports> LoadModuleExports()
    {
        return ModuleExportAnalyses.Value.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.Exports,
            StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> LoadModuleSourceFiles()
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var root = StdlibRoot.Value;
        foreach (var modulePath in ModuleSources.Value.Keys)
        {
            var candidate = Path.Combine(
                root,
                modulePath.Replace('/', Path.DirectorySeparatorChar) + ResourceExtension);
            if (File.Exists(candidate))
            {
                result[modulePath] = Path.GetFullPath(candidate);
            }
        }

        return result;
    }

    private static ParserArtifacts LoadOrBuildParserArtifacts()
    {
        var cacheData = GrammarDataCache.LoadOrBuild(
            GetGrammarCachePath(),
            CompilationPipeline.GrammarCacheVersion);
        return new ParserArtifacts(cacheData.GrammarData, cacheData.ScannerData);
    }

    private static string ResolveStdlibRoot()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var configured = Environment.GetEnvironmentVariable("EIDOS_STDLIB_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredRoot = Path.GetFullPath(configured);
            if (!Directory.Exists(configuredRoot))
            {
                throw new DirectoryNotFoundException($"EIDOS_STDLIB_PATH '{configuredRoot}' does not exist.");
            }

            return configuredRoot;
        }

        foreach (var basePath in EnumerateSearchBasePaths())
        {
            if (string.IsNullOrWhiteSpace(basePath) || !Directory.Exists(basePath))
            {
                continue;
            }

            var current = Path.GetFullPath(basePath);
            while (!string.IsNullOrWhiteSpace(current))
            {
                foreach (var candidate in GetPrecompiledRootCandidates(current))
                {
                    if (Directory.Exists(candidate) && seen.Add(candidate))
                    {
                        return candidate;
                    }
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                current = parent;
            }
        }

        throw new DirectoryNotFoundException(
            "No external Eidos Std component was found. Set EIDOS_STDLIB_PATH or install the eidos-std component next to eidosc.");
    }

    private static IEnumerable<string> GetPrecompiledRootCandidates(string current)
    {
        yield return Path.Combine(current, "stdlib");
        yield return Path.Combine(current, "Stdlib", "Precompiled");
        yield return Path.Combine(current, "src", "Eidosc", "Stdlib", "Precompiled");
        yield return Path.Combine(current, "src", "Eidosc", "Eidosc", "Stdlib", "Precompiled");
    }

    private static IEnumerable<string> EnumerateSearchBasePaths()
    {
        yield return AppContext.BaseDirectory;
        yield return Directory.GetCurrentDirectory();
    }

    private static ModuleExportAnalysisResult AnalyzeSource(
        string source,
        string? modulePath,
        IReadOnlyDictionary<string, string>? moduleSources,
        Dictionary<string, ModuleExportAnalysisResult>? cache,
        HashSet<string>? resolutionStack)
    {
        var normalizedPath = NormalizeModulePath(modulePath);
        if (!string.IsNullOrEmpty(normalizedPath) &&
            cache != null &&
            cache.TryGetValue(normalizedPath, out var cached))
        {
            return cached;
        }

        if (!string.IsNullOrEmpty(normalizedPath) &&
            resolutionStack != null &&
            !resolutionStack.Add(normalizedPath))
        {
            return ModuleExportAnalysisResult.Empty;
        }

        try
        {
            if (!TryParseModuleDecl(source, normalizedPath, out var moduleDecl) || moduleDecl == null)
            {
                return ExtractExportsFallback(source);
            }

            moduleDecl = SelectPrimaryModuleDecl(moduleDecl, normalizedPath);

            var analysis = BuildExportAnalysis(
                moduleDecl,
                normalizedPath,
                moduleSources,
                cache,
                resolutionStack);

            if (!string.IsNullOrEmpty(normalizedPath) && cache != null)
            {
                cache[normalizedPath] = analysis;
            }

            return analysis;
        }
        finally
        {
            if (!string.IsNullOrEmpty(normalizedPath) && resolutionStack != null)
            {
                resolutionStack.Remove(normalizedPath);
            }
        }
    }

    private static ModuleDecl SelectPrimaryModuleDecl(ModuleDecl moduleDecl, string normalizedPath)
    {
        if (!string.IsNullOrWhiteSpace(normalizedPath))
        {
            var packageStrippedPath = StripStdPackageAlias(normalizedPath);
            if (!string.Equals(packageStrippedPath, normalizedPath, StringComparison.Ordinal))
            {
                var matched = FindModuleDeclByPath(moduleDecl, packageStrippedPath);
                if (matched != null)
                {
                    return matched;
                }
            }

            var exactMatched = FindModuleDeclByPath(moduleDecl, normalizedPath);
            if (exactMatched != null)
            {
                return exactMatched;
            }
        }

        if (moduleDecl.Path.Count == 0 ||
            (moduleDecl.Path.Count == 1 && string.Equals(moduleDecl.Path[0], "<memory>", StringComparison.Ordinal)))
        {
            var nestedModule = moduleDecl.Declarations
                .OfType<ModuleDecl>()
                .FirstOrDefault(candidate => candidate.Path.Count > 0);
            if (nestedModule != null)
            {
                return nestedModule;
            }
        }

        return moduleDecl;
    }

    private static ModuleDecl? FindModuleDeclByPath(ModuleDecl moduleDecl, string modulePath)
    {
        foreach (var nestedModule in moduleDecl.Declarations.OfType<ModuleDecl>())
        {
            var matched = FindModuleDeclByPath(nestedModule, modulePath);
            if (matched != null)
            {
                return matched;
            }
        }

        var currentPath = NormalizeModulePath(moduleDecl.Path);
        if (string.Equals(currentPath, modulePath, StringComparison.OrdinalIgnoreCase))
        {
            return moduleDecl;
        }

        return null;
    }

    private static ModuleExportAnalysisResult BuildExportAnalysis(
        ModuleDecl moduleDecl,
        string currentModulePath,
        IReadOnlyDictionary<string, string>? moduleSources,
        Dictionary<string, ModuleExportAnalysisResult>? cache,
        HashSet<string>? resolutionStack)
    {
        var accumulator = new ExportSurfaceAccumulator();
        foreach (var declaration in moduleDecl.Declarations)
        {
            switch (declaration)
            {
                case ModuleDecl:
                    continue;

                case ImportDecl import when import.IsExported:
                    ApplyExportedImport(
                        accumulator,
                        import,
                        currentModulePath,
                        moduleSources,
                        cache,
                        resolutionStack);
                    continue;

                case ImportDecl:
                    continue;
            }

            if (!ShouldExposeDirectDeclaration(moduleDecl, declaration))
            {
                continue;
            }

            AddDirectDeclaration(accumulator, declaration);
        }

        return accumulator.Build();
    }

    private static bool ShouldExposeDirectDeclaration(ModuleDecl moduleDecl, Declaration declaration)
    {
        if (moduleDecl.UsesExplicitExports)
        {
            return declaration.IsExported;
        }

        return !HasInternalAttribute(declaration);
    }

    private static bool HasInternalAttribute(Declaration declaration)
    {
        return declaration.Attributes.Any(attribute =>
            string.Equals(attribute.Name, InternalAttributeName, StringComparison.Ordinal));
    }

    private static void AddDirectDeclaration(ExportSurfaceAccumulator accumulator, Declaration declaration)
    {
        switch (declaration)
        {
            case LetDecl { Pattern: VarPattern { Name.Length: > 0 } varPattern }:
                accumulator.AddValue(varPattern.Name);
                break;

            case FuncDef func when !string.IsNullOrWhiteSpace(func.Name):
                accumulator.AddFunction(func.Name);
                break;

            case FuncDecl funcDecl when !string.IsNullOrWhiteSpace(funcDecl.Name):
                accumulator.AddFunction(funcDecl.Name);
                break;

            case TraitDef trait when !string.IsNullOrWhiteSpace(trait.Name):
                accumulator.AddTrait(trait.Name);
                accumulator.AddOwner(
                    trait.Name,
                    OwnerExportInfo.Create(
                        OwnerExportKind.Trait,
                        trait.Methods
                            .Select(method => method.Name)
                            .Where(name => !string.IsNullOrWhiteSpace(name))));
                break;

            case EffectDef ability when !string.IsNullOrWhiteSpace(ability.Name):
                accumulator.AddEffect(ability.Name);
                accumulator.AddOwner(
                    ability.Name,
                    OwnerExportInfo.Create(
                        OwnerExportKind.Effect,
                        []));
                break;

            case AdtDef adt when !string.IsNullOrWhiteSpace(adt.Name):
                accumulator.AddType(adt.Name);
                if (!adt.IsTypeAlias)
                {
                    accumulator.AddOwner(
                        adt.Name,
                        OwnerExportInfo.Create(
                            OwnerExportKind.Type,
                            adt.Constructors
                                .Select(ctor => ctor.Name)
                                .Where(name => !string.IsNullOrWhiteSpace(name))));
                }
                break;

            case InstanceDecl instance:
                foreach (var method in instance.Methods)
                {
                    if (!string.IsNullOrWhiteSpace(method.Name))
                    {
                        accumulator.AddFunction(method.Name);
                    }
                }

                break;
        }
    }

    private static void ApplyExportedImport(
        ExportSurfaceAccumulator accumulator,
        ImportDecl import,
        string currentModulePath,
        IReadOnlyDictionary<string, string>? moduleSources,
        Dictionary<string, ModuleExportAnalysisResult>? cache,
        HashSet<string>? resolutionStack)
    {
        var targetModulePath = ResolvePrecompiledImportModulePath(import, currentModulePath);
        switch (import.Kind)
        {
            case ImportKind.Module:
            {
                var exportName = import.Alias ?? import.ModulePath.LastOrDefault() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(exportName))
                {
                    accumulator.AddModule(exportName, targetModulePath);
                }

                if (!TryResolveImportedModuleAnalysis(
                        targetModulePath,
                        moduleSources,
                        cache,
                        resolutionStack,
                        out var importedAnalysis))
                {
                    return;
                }

                foreach (var owner in importedAnalysis.Owners.Values)
                {
                    if (owner.Kind != OwnerExportKind.Trait)
                    {
                        continue;
                    }

                    foreach (var memberName in owner.Members)
                    {
                        accumulator.AddFunction(memberName);
                    }
                }

                return;
            }

            case ImportKind.Selective:
            {
                if (!TryResolveImportedModuleAnalysis(
                        targetModulePath,
                        moduleSources,
                        cache,
                        resolutionStack,
                        out var importedAnalysis))
                {
                    return;
                }

                foreach (var item in import.SelectiveImports)
                {
                    var exportName = item.Alias ?? item.Name;
                    if (string.IsNullOrWhiteSpace(exportName))
                    {
                        continue;
                    }

                    ApplySelectiveReexport(accumulator, importedAnalysis, item.Name, exportName);
                }

                return;
            }

            case ImportKind.Wildcard:
            {
                if (!TryResolveImportedModuleAnalysis(
                        targetModulePath,
                        moduleSources,
                        cache,
                        resolutionStack,
                        out var importedAnalysis))
                {
                    return;
                }

                accumulator.Merge(importedAnalysis);
                return;
            }
        }
    }

    private static string ResolvePrecompiledImportModulePath(ImportDecl import, string currentModulePath)
    {
        if (!string.IsNullOrWhiteSpace(import.PackageAlias))
        {
            return NormalizeModulePath(import.ToQualifiedModulePath());
        }

        var currentPackage = GetPrecompiledPackageAlias(currentModulePath);
        if (!string.IsNullOrWhiteSpace(currentPackage))
        {
            var parts = new List<string> { currentPackage };
            parts.AddRange(import.ModulePath);
            return NormalizeModulePath(parts);
        }

        return NormalizeModulePath(import.ModulePath);
    }

    private static string? GetPrecompiledPackageAlias(string modulePath)
    {
        return IsStdPrecompiledModulePath(modulePath) ? StdPackageAlias : null;
    }

    private static bool IsStdPrecompiledModulePath(string modulePath)
    {
        var normalized = NormalizeModulePath(modulePath);
        return normalized.StartsWith($"{StdPackageAlias}/", StringComparison.Ordinal);
    }

    private static string StripStdPackageAlias(string modulePath)
    {
        var normalized = NormalizeModulePath(modulePath);
        return IsStdPrecompiledModulePath(normalized)
            ? normalized[(StdPackageAlias.Length + 1)..]
            : normalized;
    }

    private static void ApplySelectiveReexport(
        ExportSurfaceAccumulator accumulator,
        ModuleExportAnalysisResult importedAnalysis,
        string importedName,
        string exportName)
    {
        if (importedAnalysis.Exports.Modules.TryGetValue(importedName, out var targetModulePath))
        {
            accumulator.AddModule(exportName, targetModulePath);
            return;
        }

        if (importedAnalysis.Exports.Values.Contains(importedName, StringComparer.Ordinal))
        {
            accumulator.AddValue(exportName);
            return;
        }

        if (importedAnalysis.Exports.Functions.Contains(importedName, StringComparer.Ordinal))
        {
            accumulator.AddFunction(exportName);
            return;
        }

        if (importedAnalysis.Exports.Traits.Contains(importedName, StringComparer.Ordinal))
        {
            accumulator.AddTrait(exportName);
            if (importedAnalysis.Owners.TryGetValue(importedName, out var owner))
            {
                accumulator.AddOwner(exportName, owner);
            }

            return;
        }

        if (importedAnalysis.Exports.Effects.Contains(importedName, StringComparer.Ordinal))
        {
            accumulator.AddEffect(exportName);
            if (importedAnalysis.Owners.TryGetValue(importedName, out var owner))
            {
                accumulator.AddOwner(exportName, owner);
            }

            return;
        }

        if (importedAnalysis.Exports.Types.Contains(importedName, StringComparer.Ordinal))
        {
            accumulator.AddType(exportName);
            if (importedAnalysis.Owners.TryGetValue(importedName, out var owner))
            {
                accumulator.AddOwner(exportName, owner);
            }
        }
    }

    private static bool TryResolveImportedModuleAnalysis(
        string modulePath,
        IReadOnlyDictionary<string, string>? moduleSources,
        Dictionary<string, ModuleExportAnalysisResult>? cache,
        HashSet<string>? resolutionStack,
        out ModuleExportAnalysisResult analysis)
    {
        analysis = ModuleExportAnalysisResult.Empty;
        var normalizedPath = NormalizeModulePath(modulePath);
        if (string.IsNullOrEmpty(normalizedPath) || moduleSources == null)
        {
            return false;
        }

        if (cache != null &&
            cache.TryGetValue(normalizedPath, out var cachedAnalysis) &&
            cachedAnalysis != null)
        {
            analysis = cachedAnalysis;
            return true;
        }

        if (!moduleSources.TryGetValue(normalizedPath, out var importedSource))
        {
            return false;
        }

        analysis = AnalyzeSource(importedSource, normalizedPath, moduleSources, cache, resolutionStack);
        if (cache != null)
        {
            cache[normalizedPath] = analysis;
        }

        return true;
    }

    private static bool TryParseModuleDecl(
        string source,
        string? sourceName,
        out ModuleDecl? moduleDecl)
    {
        moduleDecl = null;
        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        var artifacts = SharedParserArtifacts.Value;
        var stream = new SourceStream(
            source,
            4,
            new SourceLocation(0, 0, 0, string.IsNullOrWhiteSpace(sourceName) ? "<memory>" : sourceName));
        var compileContext = new LexerContext(
            stream,
            artifacts.ScannerData,
            artifacts.GrammarData.Terminals);

        Scanner.Init(compileContext);
        var tokens = new List<Token>();
        while (compileContext.TokenStream!.MoveNext())
        {
            tokens.Add(compileContext.TokenStream.Current);
        }

        GuardTokenNormalizer.Normalize(tokens, compileContext, rewriteAnonymousLambdas: false);

        var (ast, diagnostics) = SyntaxParser.Parse(
            tokens,
            string.IsNullOrWhiteSpace(sourceName) ? "<memory>" : sourceName,
            EidosLanguageVersions.Current);
        if (ast == null || diagnostics.Any(diagnostic => diagnostic.Level == Diagnostic.DiagnosticLevel.Error))
        {
            return false;
        }

        moduleDecl = ast;
        return true;
    }

    private static bool IsTokenText(Token token, string expected)
    {
        return string.Equals(GetTokenText(token), expected, StringComparison.Ordinal);
    }

    private static string GetTokenText(Token token)
    {
        return token switch
        {
            ContentToken contentToken => contentToken.ToString(),
            EofToken => "<eof>",
            ErrorToken errorToken => errorToken.Message,
            _ => token.ToString() ?? string.Empty
        };
    }

    private static string GetGrammarCachePath()
    {
        var baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return Path.Combine(baseDir, "cache", "grammar.bin");
    }

    private static ModuleExportAnalysisResult ExtractExportsFallback(string source)
    {
        var functionNames = new HashSet<string>(StringComparer.Ordinal);
        var typeNames = new HashSet<string>(StringComparer.Ordinal);
        var traitNames = new HashSet<string>(StringComparer.Ordinal);
        var abilityNames = new HashSet<string>(StringComparer.Ordinal);
        var constructorNames = new HashSet<string>(StringComparer.Ordinal);
        var pendingAttributes = new List<string>();

        var braceDepth = 0;
        var traitDepth = -1;
        var adtDepth = -1;

        using var reader = new StringReader(source);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = StripLineComment(line).Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var lineAttributes = ExtractLeadingAttributes(ref trimmed);
            if (lineAttributes.Count > 0)
            {
                pendingAttributes.AddRange(lineAttributes);
            }

            if (trimmed.Length == 0)
            {
                continue;
            }

            var isInternal = pendingAttributes.Contains(InternalAttributeName, StringComparer.Ordinal);

            // Strip leading "export " keyword so subsequent checks see direct declarations.
            if (trimmed.StartsWith("export ", StringComparison.Ordinal))
            {
                trimmed = trimmed["export ".Length..].TrimStart();
            }

            var inDirectAdtBody = adtDepth >= 0 && braceDepth == adtDepth;
            if (inDirectAdtBody)
            {
                ExtractConstructorNames(trimmed, constructorNames);
            }

            if (braceDepth == 1 && traitDepth < 0 && adtDepth < 0)
            {
                var nameFirstSeparator = trimmed.IndexOf("::", StringComparison.Ordinal);
                var isNameFirstDeclaration = nameFirstSeparator > 0;
                var nameFirstName = isNameFirstDeclaration
                    ? trimmed[..nameFirstSeparator].Trim()
                    : string.Empty;
                var nameFirstTail = isNameFirstDeclaration
                    ? trimmed[(nameFirstSeparator + 2)..].TrimStart()
                    : string.Empty;

                if (trimmed.StartsWith("func ", StringComparison.Ordinal))
                {
                    if (!isInternal)
                    {
                        var functionName = ExtractDeclarationName(trimmed, WellKnownStrings.Keywords.Func);
                        if (!string.IsNullOrWhiteSpace(functionName))
                        {
                            functionNames.Add(functionName);
                        }
                    }

                    pendingAttributes.Clear();
                }
                else if (isNameFirstDeclaration && IsLikelyFunctionSignature(nameFirstTail))
                {
                    if (!isInternal && !string.IsNullOrWhiteSpace(nameFirstName))
                    {
                        functionNames.Add(ExtractNameFirstBaseName(nameFirstName));
                    }

                    pendingAttributes.Clear();
                }
                else if (trimmed.StartsWith("let ", StringComparison.Ordinal))
                {
                    if (!isInternal)
                    {
                        var letName = ExtractLetDeclarationName(trimmed);
                        if (!string.IsNullOrWhiteSpace(letName))
                        {
                            functionNames.Add(letName);
                        }
                    }

                    pendingAttributes.Clear();
                }
                else if (isNameFirstDeclaration &&
                         nameFirstTail.StartsWith("trait", StringComparison.Ordinal))
                {
                    if (!isInternal && !string.IsNullOrWhiteSpace(nameFirstName))
                    {
                        traitNames.Add(ExtractNameFirstBaseName(nameFirstName));
                    }

                    if (LineOpensUnclosedBlock(trimmed))
                    {
                        traitDepth = braceDepth + 1;
                    }

                    pendingAttributes.Clear();
                }
                else if (trimmed.StartsWith("trait ", StringComparison.Ordinal))
                {
                    if (!isInternal)
                    {
                        var traitName = ExtractDeclarationName(trimmed, WellKnownStrings.Keywords.Trait);
                        if (!string.IsNullOrWhiteSpace(traitName))
                        {
                            traitNames.Add(traitName);
                        }
                    }

                    if (LineOpensUnclosedBlock(trimmed))
                    {
                        traitDepth = braceDepth + 1;
                    }

                    pendingAttributes.Clear();
                }
                else if (isNameFirstDeclaration &&
                         nameFirstTail.StartsWith("ability", StringComparison.Ordinal))
                {
                    if (!isInternal && !string.IsNullOrWhiteSpace(nameFirstName))
                    {
                        abilityNames.Add(ExtractNameFirstBaseName(nameFirstName));
                    }

                    pendingAttributes.Clear();
                }
                else if (trimmed.StartsWith("ability ", StringComparison.Ordinal))
                {
                    if (!isInternal)
                    {
                        var abilityName = ExtractDeclarationName(trimmed, WellKnownStrings.Keywords.Effect);
                        if (!string.IsNullOrWhiteSpace(abilityName))
                        {
                            abilityNames.Add(abilityName);
                        }
                    }

                    pendingAttributes.Clear();
                }
                else if (isNameFirstDeclaration &&
                         nameFirstTail.StartsWith("type", StringComparison.Ordinal))
                {
                    if (!isInternal && !string.IsNullOrWhiteSpace(nameFirstName))
                    {
                        typeNames.Add(ExtractNameFirstBaseName(nameFirstName));
                    }

                    var bodyStart = trimmed.IndexOf('{');
                    if (bodyStart >= 0 && !trimmed.Contains('=', StringComparison.Ordinal))
                    {
                        var bodyEnd = trimmed.LastIndexOf('}');
                        var body = bodyEnd > bodyStart
                            ? trimmed[(bodyStart + 1)..bodyEnd]
                            : trimmed[(bodyStart + 1)..];
                        ExtractConstructorNames(body, constructorNames);

                        if (bodyEnd < bodyStart)
                        {
                            adtDepth = braceDepth + 1;
                        }
                    }

                    pendingAttributes.Clear();
                }
                else if (trimmed.StartsWith("type ", StringComparison.Ordinal) ||
                         trimmed.StartsWith("type ", StringComparison.Ordinal))
                {
                    if (!isInternal)
                    {
                        var keyword = trimmed.StartsWith("type ", StringComparison.Ordinal) ? "adt" : WellKnownStrings.Keywords.Type;
                        var typeName = ExtractDeclarationName(trimmed, keyword);
                        if (!string.IsNullOrWhiteSpace(typeName))
                        {
                            typeNames.Add(typeName);
                        }
                    }

                    var bodyStart = trimmed.IndexOf('{');
                    if (bodyStart >= 0 && !trimmed.Contains('=', StringComparison.Ordinal))
                    {
                        var bodyEnd = trimmed.LastIndexOf('}');
                        var body = bodyEnd > bodyStart
                            ? trimmed[(bodyStart + 1)..bodyEnd]
                            : trimmed[(bodyStart + 1)..];
                        ExtractConstructorNames(body, constructorNames);

                        if (bodyEnd < bodyStart)
                        {
                            adtDepth = braceDepth + 1;
                        }
                    }

                    pendingAttributes.Clear();
                }
                else if (lineAttributes.Count == 0)
                {
                    pendingAttributes.Clear();
                }
            }
            else if (lineAttributes.Count == 0)
            {
                pendingAttributes.Clear();
            }

            var nextBraceDepth = braceDepth + CountChar(trimmed, '{') - CountChar(trimmed, '}');
            if (adtDepth >= 0 && nextBraceDepth < adtDepth)
            {
                adtDepth = -1;
            }

            if (traitDepth >= 0 && nextBraceDepth < traitDepth)
            {
                traitDepth = -1;
            }

            braceDepth = nextBraceDepth;
        }

        var fallbackExports = new PrecompiledModuleExports(
            Order(functionNames),
            Order(typeNames),
            Order(traitNames),
            Order(abilityNames),
            Order(constructorNames));
        return new ModuleExportAnalysisResult(fallbackExports, new Dictionary<string, OwnerExportInfo>(StringComparer.Ordinal));
    }

    private static IReadOnlyList<string> Order(HashSet<string> values)
    {
        return values.OrderBy(name => name, StringComparer.Ordinal).ToArray();
    }

    private static string ExtractDeclarationName(string line, string keyword)
    {
        var start = keyword.Length;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        var end = FindDeclarationNameEnd(line, start);
        return end <= start ? string.Empty : line[start..end];
    }

    private static bool IsLikelyFunctionSignature(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        return text.Contains("->", StringComparison.Ordinal) ||
               text.StartsWith("need ", StringComparison.Ordinal) ||
               text.StartsWith("comptime ", StringComparison.Ordinal);
    }

    private static string ExtractNameFirstBaseName(string text)
    {
        var end = FindDeclarationNameEnd(text, 0);
        return end <= 0 ? text : text[..end];
    }

    private static string ExtractLetDeclarationName(string line)
    {
        var start = WellKnownStrings.Keywords.Let.Length;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
        {
            start++;
        }

        if (line.AsSpan(start).StartsWith(WellKnownStrings.Keywords.Mut.AsSpan(), StringComparison.Ordinal))
        {
            var next = start + WellKnownStrings.Keywords.Mut.Length;
            if (next >= line.Length || char.IsWhiteSpace(line[next]))
            {
                start = next;
                while (start < line.Length && char.IsWhiteSpace(line[start]))
                {
                    start++;
                }
            }
        }

        var end = FindDeclarationNameEnd(line, start);
        return end <= start ? string.Empty : line[start..end];
    }

    private static void ExtractConstructorNames(string sourceFragment, HashSet<string> constructorNames)
    {
        foreach (var segment in sourceFragment.Split('|', StringSplitOptions.TrimEntries))
        {
            var trimmed = segment
                .Trim()
                .TrimStart(',', '}')
                .Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var start = 0;
            while (start < trimmed.Length && !char.IsLetter(trimmed[start]))
            {
                start++;
            }

            if (start >= trimmed.Length || !char.IsUpper(trimmed[start]))
            {
                continue;
            }

            var end = FindDeclarationNameEnd(trimmed, start);
            if (end <= start)
            {
                continue;
            }

            constructorNames.Add(trimmed[start..end]);
        }
    }

    private static bool LineOpensUnclosedBlock(string line)
    {
        return CountChar(line, '{') > CountChar(line, '}');
    }

    private static List<string> ExtractLeadingAttributes(ref string line)
    {
        var attributes = new List<string>();
        var remaining = line;

        while (remaining.StartsWith('@'))
        {
            var end = FindAttributeEnd(remaining);
            if (end <= 1)
            {
                break;
            }

            var attributeText = remaining[1..end].Trim();
            if (attributeText.Length == 0)
            {
                break;
            }

            var parenIndex = attributeText.IndexOf('(');
            var attributeName = parenIndex >= 0 ? attributeText[..parenIndex] : attributeText;
            if (!string.IsNullOrWhiteSpace(attributeName))
            {
                attributes.Add(attributeName);
            }

            remaining = remaining[end..].TrimStart();
        }

        line = remaining;
        return attributes;
    }

    private static int FindAttributeEnd(string text)
    {
        if (text.Length < 2 || text[0] != '@')
        {
            return 0;
        }

        var depth = 0;
        for (var i = 1; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c == ')')
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            if (depth == 0 && char.IsWhiteSpace(c))
            {
                return i;
            }
        }

        return text.Length;
    }

    private static string StripLineComment(string line)
    {
        var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
        return commentIndex >= 0 ? line[..commentIndex] : line;
    }

    private static int CountChar(string line, char target)
    {
        var count = 0;
        foreach (var c in line)
        {
            if (c == target)
            {
                count++;
            }
        }

        return count;
    }

    private static int FindDeclarationNameEnd(string line, int start)
    {
        for (var i = start; i < line.Length; i++)
        {
            var c = line[i];
            if (char.IsWhiteSpace(c) || c == ':' || c == '[' || c == '(' || c == '{' || c == '=')
            {
                return i;
            }
        }

        return line.Length;
    }

    private static string NormalizeModulePath(IReadOnlyList<string> modulePath)
    {
        if (modulePath.Count == 0)
        {
            return string.Empty;
        }

        return NormalizeModulePath(string.Join(WellKnownStrings.Operators.Divide, modulePath));
    }

    private static string NormalizeModulePath(string? modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
        {
            return string.Empty;
        }

        var normalized = modulePath
            .Replace('\\', '/')
            .Replace(WellKnownStrings.Separators.Path, WellKnownStrings.Operators.Divide, StringComparison.Ordinal);

        while (normalized.Contains("//", StringComparison.Ordinal))
        {
            normalized = normalized.Replace("//", WellKnownStrings.Operators.Divide, StringComparison.Ordinal);
        }

        normalized = normalized.Trim('/');
        return normalized;
    }

    private static string GetModuleLeafName(string modulePath)
    {
        var normalized = NormalizeModulePath(modulePath);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }

    private static ModuleExportAnalysisResult? TryGetAnalysisByModulePath(string modulePath)
    {
        var normalized = NormalizeModulePath(modulePath);
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return ModuleExportAnalyses.Value.TryGetValue(normalized, out var analysis)
            ? analysis
            : null;
    }

    private sealed record ParserArtifacts(
        GrammarData GrammarData,
        ScannerData ScannerData);

    private sealed class ExportSurfaceAccumulator
    {
        private readonly HashSet<string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _functions = new(StringComparer.Ordinal);
        private readonly HashSet<string> _types = new(StringComparer.Ordinal);
        private readonly HashSet<string> _traits = new(StringComparer.Ordinal);
        private readonly HashSet<string> _abilities = new(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _modules = new(StringComparer.Ordinal);
        private readonly Dictionary<string, OwnerExportInfo> _owners = new(StringComparer.Ordinal);

        public void AddValue(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _values.Add(name);
            }
        }

        public void AddFunction(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _functions.Add(name);
            }
        }

        public void AddType(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _types.Add(name);
            }
        }

        public void AddTrait(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _traits.Add(name);
            }
        }

        public void AddEffect(string name)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                _abilities.Add(name);
            }
        }

        public void AddModule(string name, string targetModulePath)
        {
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(targetModulePath))
            {
                return;
            }

            _modules[name] = targetModulePath;
            _owners[name] = OwnerExportInfo.ForModule(targetModulePath);
        }

        public void AddOwner(string name, OwnerExportInfo owner)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            if (owner.Kind == OwnerExportKind.Module && string.IsNullOrWhiteSpace(owner.TargetModulePath))
            {
                return;
            }

            if (owner.Kind != OwnerExportKind.Module && owner.Members.Count == 0)
            {
                return;
            }

            _owners[name] = owner;
        }

        public void Merge(ModuleExportAnalysisResult analysis)
        {
            foreach (var name in analysis.Exports.Values)
            {
                AddValue(name);
            }

            foreach (var name in analysis.Exports.Functions)
            {
                AddFunction(name);
            }

            foreach (var name in analysis.Exports.Types)
            {
                AddType(name);
            }

            foreach (var name in analysis.Exports.Traits)
            {
                AddTrait(name);
            }

            foreach (var name in analysis.Exports.Effects)
            {
                AddEffect(name);
            }

            foreach (var (name, targetModulePath) in analysis.Exports.Modules)
            {
                AddModule(name, targetModulePath);
            }

            foreach (var (name, owner) in analysis.Owners)
            {
                AddOwner(name, owner);
            }
        }

        public ModuleExportAnalysisResult Build()
        {
            var constructors = _owners
                .Where(pair => pair.Value.Kind == OwnerExportKind.Type)
                .SelectMany(pair => pair.Value.Members)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            var orderedModules = _modules
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

            var exports = new PrecompiledModuleExports(
                _functions.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                _types.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                _traits.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                _abilities.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                constructors)
            {
                Values = _values.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
                Modules = orderedModules
            };

            return new ModuleExportAnalysisResult(exports, new Dictionary<string, OwnerExportInfo>(_owners, StringComparer.Ordinal));
        }
    }

    private sealed record ModuleExportAnalysisResult(
        PrecompiledModuleExports Exports,
        IReadOnlyDictionary<string, OwnerExportInfo> Owners)
    {
        public static ModuleExportAnalysisResult Empty { get; } =
            new(PrecompiledModuleExports.Empty, new Dictionary<string, OwnerExportInfo>(StringComparer.Ordinal));

        public bool ContainsTopLevelName(string name)
        {
            return Exports.Values.Contains(name, StringComparer.Ordinal) ||
                   Exports.Functions.Contains(name, StringComparer.Ordinal) ||
                   Exports.Types.Contains(name, StringComparer.Ordinal) ||
                   Exports.Traits.Contains(name, StringComparer.Ordinal) ||
                   Exports.Effects.Contains(name, StringComparer.Ordinal) ||
                   Exports.Modules.ContainsKey(name);
        }

        public bool OwnerDefinesMember(
            string ownerName,
            string memberName,
            Func<string, ModuleExportAnalysisResult?> moduleResolver)
        {
            if (!Owners.TryGetValue(ownerName, out var owner))
            {
                return false;
            }

            if (owner.Kind == OwnerExportKind.Module)
            {
                if (string.IsNullOrWhiteSpace(owner.TargetModulePath))
                {
                    return false;
                }

                var targetModule = moduleResolver(owner.TargetModulePath);
                return targetModule?.ContainsTopLevelName(memberName) == true ||
                       targetModule?.OwnerDefinesMember(
                           GetModuleLeafName(owner.TargetModulePath),
                           memberName,
                           moduleResolver) == true;
            }

            return owner.Members.Contains(memberName);
        }
    }

    private enum OwnerExportKind
    {
        Type,
        Trait,
        Effect,
        Module
    }

    private sealed record OwnerExportInfo(
        OwnerExportKind Kind,
        IReadOnlySet<string> Members,
        string? TargetModulePath = null)
    {
        public static OwnerExportInfo Create(OwnerExportKind kind, IEnumerable<string> members)
        {
            return new OwnerExportInfo(
                kind,
                new HashSet<string>(
                    members.Where(name => !string.IsNullOrWhiteSpace(name)),
                    StringComparer.Ordinal));
        }

        public static OwnerExportInfo ForModule(string targetModulePath)
        {
            return new OwnerExportInfo(
                OwnerExportKind.Module,
                new HashSet<string>(StringComparer.Ordinal),
                targetModulePath);
        }
    }

    private static SemanticVersion LoadStdlibVersion()
    {
        try
        {
            var manifestPath = Path.Combine(StdlibRoot.Value, "eidos.toml");
            if (!File.Exists(manifestPath)) return new SemanticVersion(0, 1, 0);

            var manifest = EidosProjectManifestDocument.Parse(File.ReadAllText(manifestPath), manifestPath);

            var versionStr = manifest.Package?.Version;
            if (versionStr != null &&
                SemanticVersion.TryParse(versionStr, out var version) &&
                version != null)
            {
                return version;
            }
        }
        catch
        {
        }

        return new SemanticVersion(0, 1, 0);
    }
}
