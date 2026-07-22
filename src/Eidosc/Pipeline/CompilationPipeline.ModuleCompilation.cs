namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private ProjectModuleInvalidationPlan ExpandInvalidationToCompilationUnits(
        ProjectModuleInvalidationPlan plan,
        ProjectModuleGraphSnapshot graph)
    {
        var affected = plan.AffectedModules.ToHashSet(StringComparer.Ordinal);
        var moduleDeclarations = CollectModuleDeclarationsForSemanticSignature();
        var sourceKeysByModule = graph.Nodes.ToDictionary(
            static node => node.ModuleKey,
            node => string.Join(
                "\0",
                (node.SourcePaths.Count > 0
                        ? node.SourcePaths
                        : moduleDeclarations.TryGetValue(node.ModuleKey, out var declaration) &&
                          !string.IsNullOrWhiteSpace(declaration.Span.FilePath)
                            ? [declaration.Span.FilePath]
                            : [])
                    .Select(NormalizeCompilationInputPath)
                    .Order(StringComparer.Ordinal)),
            StringComparer.Ordinal);
        var affectedSourceKeys = affected
            .Select(module => sourceKeysByModule.GetValueOrDefault(module, ""))
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .ToHashSet(StringComparer.Ordinal);
        var sharedUnitModules = sourceKeysByModule
            .Where(entry => affectedSourceKeys.Contains(entry.Value) && !affected.Contains(entry.Key))
            .Select(static entry => entry.Key)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (sharedUnitModules.Length == 0)
        {
            return plan;
        }

        affected.UnionWith(sharedUnitModules);
        return new ProjectModuleInvalidationPlan(
            plan.Changes
                .Concat(sharedUnitModules.Select(static module => new ProjectModuleInvalidationChange(
                    module,
                    ProjectModuleInvalidationReason.SourceCompilationUnitChanged)))
                .OrderBy(static change => change.ModuleKey, StringComparer.Ordinal)
                .ThenBy(static change => change.Reason)
                .ToArray(),
            affected.Order(StringComparer.Ordinal).ToArray(),
            graph.Nodes
                .Select(static node => node.ModuleKey)
                .Where(module => !affected.Contains(module))
                .Order(StringComparer.Ordinal)
                .ToArray());
    }

    private CompilationResult CompileModuleToPhase(string moduleKey, CompilationPhase phase)
    {
        var (sourceText, inputFile, allowVirtualInputFile) = ResolveModuleCompilationInput(moduleKey);
        var options = new CompilationOptions
        {
            InputFile = inputFile,
            LanguageVersion = _options.LanguageVersion,
            EntryFunctionName = _options.EntryFunctionName,
            AllowVirtualInputFile = allowVirtualInputFile,
            StopAtPhase = phase,
            EmitStyleSuggestions = _options.EmitStyleSuggestions,
            EnableMirOptimizations = _options.EnableMirOptimizations,
            UseColors = false,
            LlvmTargetTriple = _options.LlvmTargetTriple,
            NativeLinkMode = _options.NativeLinkMode,
            LlvmOptimizationLevel = _options.LlvmOptimizationLevel,
            LlvmEnableLto = _options.LlvmEnableLto,
            EnableDetailedProfiling = true,
            TraceComptime = _options.TraceComptime,
            ComptimeFuelBudget = _options.ComptimeFuelBudget,
            ComptimeAllocatedValueBytesBudget = _options.ComptimeAllocatedValueBytesBudget,
            ComptimeDiagnosticBudget = _options.ComptimeDiagnosticBudget,
            ToolchainOwnedSourcePaths = _options.ToolchainOwnedSourcePaths.ToArray(),
            EnableIncrementalCompilation = true,
            TreatWarningsAsErrors = _options.TreatWarningsAsErrors,
            DenyStyle = _options.DenyStyle,
            WarningCodesAsErrors = new HashSet<string>(_options.WarningCodesAsErrors, StringComparer.Ordinal),
            ImportSearchRoots = _options.ImportSearchRoots.ToArray(),
            PackageImportRoots = _options.PackageImportRoots.ToDictionary(
                static entry => entry.Key,
                static entry => entry.Value.ToArray(),
                StringComparer.Ordinal),
            MaxDegreeOfParallelism = 1,
            ConfigFfiLibraries = _options.ConfigFfiLibraries.ToArray(),
            ConfigFfiLibraryPaths = _options.ConfigFfiLibraryPaths.ToArray(),
            ConfigFfiIncludePaths = _options.ConfigFfiIncludePaths.ToArray(),
            ConfigFfiNativeSources = _options.ConfigFfiNativeSources.ToArray(),
            ConfigFfiLinkerFlags = _options.ConfigFfiLinkerFlags.ToArray(),
            NoImplicitPrelude = _options.NoImplicitPrelude
        };
        return new CompilationPipeline(sourceText, options).Run();
    }

    private (string SourceText, string InputFile, bool AllowVirtualInputFile) ResolveModuleCompilationInput(
        string moduleKey)
    {
        var sourcePaths = _moduleGraphSnapshot?.Nodes
            .FirstOrDefault(node => string.Equals(node.ModuleKey, moduleKey, StringComparison.Ordinal))?
            .SourcePaths ?? [];
        if (sourcePaths.Count == 1)
        {
            var sourcePath = sourcePaths[0];
            if (File.Exists(sourcePath))
            {
                return (File.ReadAllText(sourcePath), sourcePath, false);
            }
        }

        return (_sourceCode, _options.InputFile, _options.AllowVirtualInputFile || !File.Exists(_options.InputFile));
    }

    private string GetModuleCompilationCacheKey(string moduleKey)
    {
        var sourcePaths = _moduleGraphSnapshot?.Nodes
            .FirstOrDefault(node => string.Equals(node.ModuleKey, moduleKey, StringComparison.Ordinal))?
            .SourcePaths ?? [];
        if (sourcePaths.Count == 0)
        {
            return $"virtual:{NormalizeCompilationInputPath(_options.InputFile)}";
        }

        return string.Join(
            "\0",
            sourcePaths
                .Select(NormalizeCompilationInputPath)
                .Order(StringComparer.Ordinal));
    }

    private static string NormalizeCompilationInputPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "<virtual>";
        }

        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
    }

    private static string FormatSubcompilationFailure(CompilationResult result) =>
        string.Join(
            "; ",
            result.Diagnostics
                .Where(static diagnostic => diagnostic.Level == Eidosc.Diagnostic.DiagnosticLevel.Error)
                .Take(4)
                .Select(static diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
}
