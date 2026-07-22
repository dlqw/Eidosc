using Eidosc.Ast.Declarations;
using Eidosc.Diagnostic;
using Eidosc.ProjectSystem;
using Eidosc.Semantic;
using Eidosc.Utils;

namespace Eidosc.Pipeline;

public sealed partial class CompilationPipeline
{
    private bool TryGetInputFilePath(out string inputFilePath)
    {
        inputFilePath = string.Empty;
        if (string.IsNullOrWhiteSpace(_options.InputFile))
        {
            return false;
        }

        var normalized = Path.GetFullPath(_options.InputFile);
        if (!File.Exists(normalized) && !_options.AllowVirtualInputFile)
        {
            return false;
        }

        inputFilePath = normalized;
        return true;
    }

    private bool TryParseModuleFile(
        string filePath,
        out ModuleDecl? moduleDecl,
        out List<Diagnostic.Diagnostic> diagnostics)
    {
        moduleDecl = null;
        diagnostics = [];

        string sourceText;
        try
        {
            sourceText = File.ReadAllText(filePath);
            _moduleSourceTextCache[NormalizeModuleSourcePath(filePath)] = sourceText;
            AddProfilingCounter("Build.importSourceText.fileReads", 1);
        }
        catch (Exception ex)
        {
            diagnostics.Add(Diagnostic.Diagnostic.Error(
                DiagnosticMessages.FailedToLoadImportedModuleFile(filePath, ex.Message),
                "E0002"));
            return false;
        }

        var languageVersion = GetModuleLanguageVersion(filePath);
        return TryParseModuleSource(
            sourceText,
            GetLogicalSourceName(filePath),
            languageVersion,
            out moduleDecl,
            out diagnostics);
    }

    private string GetModuleLanguageVersion(string filePath)
    {
        var cacheKey = NormalizeLanguageVersionCacheKey(filePath);
        if (_moduleLanguageVersionCache.TryGetValue(cacheKey, out var cachedLanguageVersion))
        {
            AddProfilingCounter("Build.importLanguageVersion.cacheHits", 1);
            return cachedLanguageVersion;
        }

        AddProfilingCounter("Build.importLanguageVersion.lookups", 1);
        var languageVersion = EidosProjectConfigurationLoader.TryLoadNearest(filePath)?.Configuration.LanguageVersion
            ?? _options.LanguageVersion;
        _moduleLanguageVersionCache[cacheKey] = languageVersion;
        return languageVersion;
    }

    private static string NormalizeLanguageVersionCacheKey(string filePath)
    {
        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(filePath));
            return string.IsNullOrWhiteSpace(directory)
                ? NormalizeModuleSourcePath(filePath)
                : directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
        }
        catch
        {
            return NormalizeModuleSourcePath(filePath);
        }
    }

    private bool TryParseModuleSource(
        string sourceText,
        string sourceName,
        string languageVersion,
        out ModuleDecl? moduleDecl,
        out List<Diagnostic.Diagnostic> diagnostics)
    {
        var result = _moduleParseService!.ParseSource(
            sourceText,
            sourceName,
            languageVersion);
        moduleDecl = result.Ast;
        diagnostics = result.Diagnostics;
        return result.Success;
    }

    private bool TryParsePrecompiledModuleSource(
        string sourceText,
        string sourceName,
        string languageVersion,
        out ModuleDecl? moduleDecl,
        out List<Diagnostic.Diagnostic> diagnostics)
    {
        diagnostics = [];
        moduleDecl = null;

        var cached = PrecompiledModuleCache.GetOrCreateTokens(
            PrecompiledTokenCacheKind.FullBody,
            sourceText,
            sourceName,
            _moduleParseService!);
        if (cached.CacheHit)
        {
            AddProfilingCounter("Build.precompiledFullTokenCache.hits", 1);
        }
        else
        {
            AddProfilingCounter("Build.precompiledFullTokenCache.misses", 1);
        }

        var parseResult = _moduleParseService!.ParseTokenList(
            cached.Tokens,
            sourceName,
            languageVersion,
            cached.LexerDiagnostics);
        moduleDecl = parseResult.Ast;
        diagnostics = parseResult.Diagnostics;
        return parseResult.Success;
    }

    private static bool TryGetPrecompiledModuleSource(IReadOnlyList<string> modulePath, out string source)
    {
        return PrecompiledModuleRegistry.TryGetSource(modulePath, out source);
    }
}
