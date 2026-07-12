using Eidosc.Symbols;
using Eidosc.ProjectSystem;
using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.Semantic;

namespace Eidosc.Query;

public sealed class PipelineQuerySession
{
    private readonly Dictionary<string, QueryEngine> _engines = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CompilationResult> _results = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CachedCompilationResult>> _resultHistory = new(StringComparer.Ordinal);
    private readonly Dictionary<string, QueryInputIdentity> _inputIdentities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Stamp, string SourceText)> _importSourceCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ModuleDependencyGraph _importGraph = new();
    private const int MaxResultHistoryPerSource = 4;

    public CompilationResult Compile(string sourcePath, string sourceText, CompilationOptions options)
    {
        return Compile(sourcePath, sourceText, options, null, CancellationToken.None);
    }

    public CompilationResult Compile(
        string sourcePath,
        string sourceText,
        CompilationOptions options,
        long? documentVersion,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var sourceKey = NormalizeSessionSourcePath(sourcePath);
        var identity = QueryInputIdentity.Create(sourceKey, sourceText, options, documentVersion);

        if (_inputIdentities.TryGetValue(sourceKey, out var previousIdentity) &&
            previousIdentity == identity)
        {
            if (_results.TryGetValue(sourceKey, out var cached))
                return cached;
        }

        if (TryGetCachedResult(sourceKey, identity, sourceText, out var exactCached))
        {
            _inputIdentities[sourceKey] = identity;
            _results[sourceKey] = exactCached;
            return exactCached;
        }

        if (_inputIdentities.TryGetValue(sourceKey, out previousIdentity) &&
            _results.TryGetValue(sourceKey, out var trailingTriviaCached) &&
            previousIdentity.IsSameInputExceptContent(identity) &&
            IsSpanPreservingTriviaOnlyChange(trailingTriviaCached.SourceText, sourceText))
        {
            _inputIdentities[sourceKey] = identity;
            _results[sourceKey] = trailingTriviaCached;
            return trailingTriviaCached;
        }

        if (!_engines.TryGetValue(sourceKey, out var engine))
        {
            engine = CreateEngine();
            _engines[sourceKey] = engine;
        }
        else if (_inputIdentities.ContainsKey(sourceKey))
        {
            engine.InvalidateKey(sourceKey, DepKind.ParseModule);
            _inputIdentities.Remove(sourceKey);
            _results.Remove(sourceKey);
        }

        cancellationToken.ThrowIfCancellationRequested();

        var pipeline = new QueryDrivenPipeline(sourceKey, sourceText, options, engine, _importSourceCache, cancellationToken);
        var result = pipeline.Run();
        cancellationToken.ThrowIfCancellationRequested();

        _inputIdentities[sourceKey] = identity;
        _results[sourceKey] = result;
        AddCachedResult(sourceKey, identity, result);

        if (result.Ast != null)
            RegisterImportsFromAst(sourceKey, result.Ast, options);

        return result;
    }

    public void InvalidateSource(string sourcePath)
    {
        var normalizedSourcePath = NormalizeSessionSourcePath(sourcePath);
        var sourceKey = ResolveKnownModuleKey(normalizedSourcePath);
        var affected = _importGraph.GetTransitiveDependents([sourceKey]);

        if (_engines.TryGetValue(normalizedSourcePath, out var engine))
            engine.InvalidateKey(normalizedSourcePath, DepKind.ParseModule);

        _inputIdentities.Remove(normalizedSourcePath);
        _results.Remove(normalizedSourcePath);
        _resultHistory.Remove(normalizedSourcePath);
        _importSourceCache.Remove(normalizedSourcePath);

        foreach (var dependent in ExpandGraphKeysToSourcePaths(affected))
        {
            if (dependent == normalizedSourcePath) continue;

            if (_engines.TryGetValue(dependent, out var depEngine))
                depEngine.InvalidateKey(dependent, DepKind.ParseModule);

            _inputIdentities.Remove(dependent);
            _results.Remove(dependent);
            _resultHistory.Remove(dependent);
        }
    }

    public IReadOnlySet<string> GetAffectedModules(string sourcePath)
    {
        var sourceKey = ResolveKnownModuleKey(NormalizeSessionSourcePath(sourcePath));
        var affectedKeys = _importGraph.GetTransitiveDependents([sourceKey]);
        return ExpandGraphKeysToSourcePaths(affectedKeys);
    }

    public bool IsUpToDate(string sourcePath, string sourceText)
    {
        var hash = ContentHash.ComputeHash(sourceText);
        return _inputIdentities.TryGetValue(NormalizeSessionSourcePath(sourcePath), out var identity) &&
               string.Equals(identity.ContentHash, hash, StringComparison.Ordinal);
    }

    public bool IsUpToDate(string sourcePath, string sourceText, CompilationOptions options)
    {
        return IsUpToDate(sourcePath, sourceText, options, documentVersion: null);
    }

    public bool IsUpToDate(
        string sourcePath,
        string sourceText,
        CompilationOptions options,
        long? documentVersion)
    {
        var sourceKey = NormalizeSessionSourcePath(sourcePath);
        var identity = QueryInputIdentity.Create(sourceKey, sourceText, options, documentVersion);
        return _inputIdentities.TryGetValue(sourceKey, out var previousIdentity) &&
               previousIdentity == identity;
    }

    public void ClearAll()
    {
        foreach (var engine in _engines.Values)
            engine.ClearAllCaches();
        _engines.Clear();
        _results.Clear();
        _resultHistory.Clear();
        _inputIdentities.Clear();
        _importSourceCache.Clear();
        _importGraph.Clear();
    }

    public DependencyGraph? GetDependencyGraph(string sourcePath)
    {
        return _engines.TryGetValue(NormalizeSessionSourcePath(sourcePath), out var engine)
            ? engine.DepGraph
            : null;
    }

    private void RegisterImportsFromAst(string sourcePath, ModuleDecl ast, CompilationOptions options)
    {
        var importerKey = ResolveSourceModuleKey(sourcePath, ast, options);
        if (_importGraph.TryGetModuleKeyForSourcePath(sourcePath, out var previousImporterKey) &&
            !string.Equals(previousImporterKey, importerKey, StringComparison.Ordinal))
        {
            _importGraph.SetDependencies(previousImporterKey, []);
        }

        _importGraph.RegisterModuleIdentity(sourcePath, importerKey);

        var importedModules = new List<string>();
        foreach (var import in EnumerateImports(ast))
        {
            if (import.ModulePath.Count == 0) continue;
            importedModules.Add(ResolveImportGraphKey(sourcePath, import, options));
        }

        _importGraph.SetDependencies(importerKey, importedModules);
    }

    private string ResolveImportGraphKey(string sourcePath, ImportDecl import, CompilationOptions options)
    {
        var resolved = WorkspaceModuleLocator.ResolveImportModule(
            sourcePath,
            import.ModulePath,
            options.ImportSearchRoots);
        if (resolved != null)
        {
            var moduleKey = CreateModuleKey(resolved.FilePath, resolved.ModulePath);
            _importGraph.RegisterModuleIdentity(resolved.FilePath, moduleKey);
            return moduleKey;
        }

        return CreateUnresolvedModuleKey(import.ModulePath);
    }

    public bool TryGetModuleKeyForSourcePath(string sourcePath, out string moduleKey) =>
        _importGraph.TryGetModuleKeyForSourcePath(NormalizeSessionSourcePath(sourcePath), out moduleKey);

    public IReadOnlySet<string> GetSourcePathsForModuleKey(string moduleKey) =>
        _importGraph.GetSourcePathsForModuleKey(moduleKey);

    private string ResolveKnownModuleKey(string sourcePath)
    {
        return _importGraph.TryGetModuleKeyForSourcePath(sourcePath, out var moduleKey)
            ? moduleKey
            : sourcePath;
    }

    private IReadOnlySet<string> ExpandGraphKeysToSourcePaths(IEnumerable<string> graphKeys)
    {
        var expanded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var graphKey in graphKeys)
        {
            var sourcePaths = _importGraph.GetSourcePathsForModuleKey(graphKey);
            if (sourcePaths.Count == 0)
            {
                expanded.Add(graphKey);
                continue;
            }

            foreach (var sourcePath in sourcePaths)
            {
                expanded.Add(sourcePath);
            }
        }

        return expanded;
    }

    private static string ResolveSourceModuleKey(string sourcePath, ModuleDecl ast, CompilationOptions options)
    {
        var modulePath = ast.Path.Count > 0
            ? string.Join("/", ast.Path)
            : ResolveModulePathFromSourcePath(sourcePath, options);
        return CreateModuleKey(sourcePath, modulePath);
    }

    private static string ResolveModulePathFromSourcePath(string sourcePath, CompilationOptions options)
    {
        foreach (var root in WorkspaceModuleLocator.EnumerateImportSearchRoots(sourcePath, options.ImportSearchRoots))
        {
            var modulePath = WorkspaceModuleLocator.TryGetModulePathFromRoot(root, sourcePath);
            if (!string.IsNullOrWhiteSpace(modulePath))
            {
                return modulePath;
            }
        }

        var fileName = Path.GetFileNameWithoutExtension(sourcePath);
        return string.IsNullOrWhiteSpace(fileName) ? sourcePath : fileName;
    }

    private static string CreateModuleKey(string sourcePath, string modulePath)
    {
        var normalizedSourcePath = NormalizeSessionSourcePath(sourcePath);
        return $"module:{modulePath}@{normalizedSourcePath}";
    }

    private static string CreateUnresolvedModuleKey(IReadOnlyList<string> modulePath)
    {
        var normalizedPath = string.Join("/", modulePath);
        return $"unresolved:{normalizedPath}";
    }

    private static IEnumerable<ImportDecl> EnumerateImports(ModuleDecl decl)
    {
        foreach (var d in decl.Declarations)
        {
            if (d is ImportDecl import) yield return import;
            else if (d is ModuleDecl child)
                foreach (var ci in EnumerateImports(child)) yield return ci;
        }
    }

    private static bool IsSpanPreservingTriviaOnlyChange(string previousSourceText, string currentSourceText)
    {
        if (string.Equals(previousSourceText, currentSourceText, StringComparison.Ordinal))
        {
            return true;
        }

        var commonPrefixLength = GetCommonPrefixLength(previousSourceText, currentSourceText);
        if (IsAllWhitespace(previousSourceText.AsSpan(commonPrefixLength)) &&
            IsAllWhitespace(currentSourceText.AsSpan(commonPrefixLength)))
        {
            return true;
        }

        if (previousSourceText.Length != currentSourceText.Length)
        {
            return false;
        }

        var commonSuffixLength = GetCommonSuffixLength(
            previousSourceText,
            currentSourceText,
            commonPrefixLength);
        var changedLength = previousSourceText.Length - commonPrefixLength - commonSuffixLength;
        if (changedLength <= 0)
        {
            return true;
        }

        return IsAllWhitespace(previousSourceText.AsSpan(commonPrefixLength, changedLength)) &&
               IsAllWhitespace(currentSourceText.AsSpan(commonPrefixLength, changedLength));
    }

    private static int GetCommonPrefixLength(string left, string right)
    {
        var commonLength = Math.Min(left.Length, right.Length);
        var index = 0;
        while (index < commonLength && left[index] == right[index])
        {
            index++;
        }

        return index;
    }

    private static int GetCommonSuffixLength(string left, string right, int commonPrefixLength)
    {
        var maxSuffixLength = Math.Min(left.Length, right.Length) - commonPrefixLength;
        var suffixLength = 0;
        while (suffixLength < maxSuffixLength &&
               left[left.Length - suffixLength - 1] == right[right.Length - suffixLength - 1])
        {
            suffixLength++;
        }

        return suffixLength;
    }

    private static bool IsAllWhitespace(ReadOnlySpan<char> text)
    {
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                return false;
            }
        }

        return true;
    }

    private static string NormalizeSessionSourcePath(string sourcePath)
    {
        return SourcePathNormalizer.Normalize(sourcePath);
    }

    private static QueryEngine CreateEngine()
    {
        var engine = new QueryEngine();
        engine.Register(new QueryDrivenPipeline.ParseDescriptor(), DepKind.ParseModule);
        engine.Register(new QueryDrivenPipeline.NameResolutionDescriptor(), DepKind.ResolveNames);
        engine.Register(new QueryDrivenPipeline.TypeInferenceDescriptor(), DepKind.InferTypes);
        engine.Register(new QueryDrivenPipeline.EffectInferenceDescriptor(), DepKind.InferAbilities);
        engine.Register(new QueryDrivenPipeline.HirDescriptor(), DepKind.BuildHir);
        engine.Register(new QueryDrivenPipeline.MirDescriptor(), DepKind.BuildMir);
        engine.Register(new QueryDrivenPipeline.BorrowDescriptor(), DepKind.CheckBorrow);
        engine.Register(new QueryDrivenPipeline.CodeGenDescriptor(), DepKind.CodeGen);
        return engine;
    }

    private bool TryGetCachedResult(
        string sourcePath,
        QueryInputIdentity identity,
        string sourceText,
        out CompilationResult result)
    {
        result = null!;
        if (!_resultHistory.TryGetValue(sourcePath, out var history))
        {
            return false;
        }

        for (var i = 0; i < history.Count; i++)
        {
            var cached = history[i];
            if (cached.Identity != identity ||
                !string.Equals(cached.Result.SourceText, sourceText, StringComparison.Ordinal))
            {
                continue;
            }

            result = cached.Result;
            if (i > 0)
            {
                history.RemoveAt(i);
                history.Insert(0, cached);
            }
            return true;
        }

        return false;
    }

    private void AddCachedResult(
        string sourcePath,
        QueryInputIdentity identity,
        CompilationResult result)
    {
        if (!_resultHistory.TryGetValue(sourcePath, out var history))
        {
            history = [];
            _resultHistory[sourcePath] = history;
        }

        for (var i = 0; i < history.Count; i++)
        {
            if (history[i].Identity == identity)
            {
                history[i] = new CachedCompilationResult(identity, result);
                return;
            }
        }

        history.Insert(0, new CachedCompilationResult(identity, result));
        if (history.Count > MaxResultHistoryPerSource)
        {
            history.RemoveAt(history.Count - 1);
        }
    }

    private readonly record struct QueryInputIdentity(
        string SourcePath,
        string ContentHash,
        string OptionsFingerprint)
    {
        public static QueryInputIdentity Create(
            string sourcePath,
            string sourceText,
            CompilationOptions options,
            long? documentVersion)
        {
            return new QueryInputIdentity(
                sourcePath,
                global::Eidosc.ProjectSystem.ContentHash.ComputeHash(sourceText),
                BuildOptionsFingerprint(options));
        }

        public bool IsSameInputExceptContent(QueryInputIdentity other) =>
            string.Equals(SourcePath, other.SourcePath, StringComparison.Ordinal) &&
            string.Equals(OptionsFingerprint, other.OptionsFingerprint, StringComparison.Ordinal);

        private static string BuildOptionsFingerprint(CompilationOptions options)
        {
            var material = string.Join('\0',
                "query-options-v1",
                options.LanguageVersion,
                options.Target.ToString(),
                options.StopAtPhase?.ToString() ?? "",
                options.EnableMirOptimizations.ToString(),
                options.TreatWarningsAsErrors.ToString(),
                string.Join(",", options.WarningCodesAsErrors.Order(StringComparer.Ordinal)),
                string.Join("|", options.ImportSearchRoots.Select(NormalizePathForFingerprint).Order(StringComparer.Ordinal)),
                string.Join("|", options.ConfigFfiLibraries.Order(StringComparer.Ordinal)),
                string.Join("|", options.ConfigFfiLibraryPaths.Select(NormalizePathForFingerprint).Order(StringComparer.Ordinal)),
                string.Join("|", options.ConfigFfiIncludePaths.Select(NormalizePathForFingerprint).Order(StringComparer.Ordinal)),
                string.Join("|", options.ConfigFfiNativeSources.Select(NormalizePathForFingerprint).Order(StringComparer.Ordinal)),
                string.Join("|", options.ConfigFfiLinkerFlags.Order(StringComparer.Ordinal)),
                options.NoImplicitPrelude.ToString());
            return global::Eidosc.ProjectSystem.ContentHash.ComputeHash(material);
        }

        private static string NormalizePathForFingerprint(string path)
        {
            return string.IsNullOrWhiteSpace(path)
                ? ""
                : NormalizeSessionSourcePath(path);
        }
    }

    private readonly record struct CachedCompilationResult(
        QueryInputIdentity Identity,
        CompilationResult Result);
}
