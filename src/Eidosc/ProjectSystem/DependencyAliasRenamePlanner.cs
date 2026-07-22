using System.Security.Cryptography;
using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Parsing.Handwritten;
using Eidosc.Parsing.Lexer;
using Eidosc.Pipeline;
using Eidosc.Utils;

namespace Eidosc.ProjectSystem;

public sealed record DependencyAliasRenameEdit(
    int Start,
    int Length,
    string Replacement,
    string Kind,
    string Description);

public sealed record DependencyAliasRenameFilePlan(
    string FilePath,
    string OriginalContentHash,
    string Status,
    string[] Diagnostics,
    DependencyAliasRenameEdit[] Edits);

public sealed record DependencyAliasRenamePackagePlan(
    string ManifestPath,
    IReadOnlyDictionary<string, string> AliasRenames,
    DependencyAliasRenameFilePlan Manifest,
    DependencyAliasRenameFilePlan[] Sources,
    string[] Diagnostics)
{
    public int TotalEditCount => Manifest.Edits.Length + Sources.Sum(static source => source.Edits.Length);

    public bool CanApply =>
        Diagnostics.Length == 0 &&
        IsApplicableStatus(Manifest.Status) &&
        Sources.All(static source => IsApplicableStatus(source.Status));

    private static bool IsApplicableStatus(string status) =>
        string.Equals(status, "ready", StringComparison.Ordinal) ||
        string.Equals(status, "unchanged", StringComparison.Ordinal);
}

public sealed record DependencyAliasRenamePlan(
    string RootManifestPath,
    string Status,
    DependencyAliasRenamePackagePlan[] Packages,
    string[] Diagnostics)
{
    public int TotalEditCount => Packages.Sum(static package => package.TotalEditCount);

    public bool CanApply =>
        string.Equals(Status, "ready", StringComparison.Ordinal) ||
        string.Equals(Status, "unchanged", StringComparison.Ordinal);
}

/// <summary>
/// Plans dependency-alias renames from the manifest identity outward. Source
/// occurrences are selected from parsed path-bearing AST nodes, never from a
/// global text replacement. Each local path dependency owns a separate alias
/// namespace and is therefore planned independently.
/// </summary>
public static class DependencyAliasRenamePlanner
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

    public static DependencyAliasRenamePlan CreatePlan(
        string inputPath,
        string? oldAlias = null,
        string? newAlias = null,
        bool includePathDependencies = true,
        IReadOnlyDictionary<string, string>? documentOverrides = null)
    {
        var rootManifestPath = ResolveManifestPath(inputPath);
        var normalizedOverrides = NormalizeDocumentOverrides(documentOverrides);
        var hasExplicitRename = !string.IsNullOrWhiteSpace(oldAlias) ||
                                !string.IsNullOrWhiteSpace(newAlias);
        if (hasExplicitRename &&
            (string.IsNullOrWhiteSpace(oldAlias) || string.IsNullOrWhiteSpace(newAlias)))
        {
            throw new InvalidOperationException(
                "An explicit dependency alias rename requires both the old and new alias.");
        }

        if (hasExplicitRename && !ManifestNamingRules.IsDependencyAlias(newAlias))
        {
            throw new InvalidOperationException(
                $"Dependency alias '{newAlias}' must use lower_snake_case.");
        }

        var (grammarData, scannerData) = LexerTableBuilder.Build();
        var pending = new Queue<string>();
        var visited = new HashSet<string>(PathComparer);
        var packagePlans = new List<DependencyAliasRenamePackagePlan>();
        var planDiagnostics = new List<string>();
        pending.Enqueue(rootManifestPath);

        while (pending.Count > 0)
        {
            var manifestPath = Path.GetFullPath(pending.Dequeue());
            if (!visited.Add(manifestPath))
            {
                continue;
            }

            EidosProjectManifestDocument manifest;
            string manifestText;
            try
            {
                manifestText = ReadDocumentText(manifestPath, normalizedOverrides);
                manifest = EidosProjectManifestDocument.Parse(manifestText, manifestPath);
            }
            catch (Exception ex)
            {
                var diagnostic = $"Unable to load manifest '{manifestPath}': {ex.Message}";
                planDiagnostics.Add(diagnostic);
                packagePlans.Add(CreateBlockedPackagePlan(manifestPath, diagnostic));
                continue;
            }

            var isRoot = PathsEqual(manifestPath, rootManifestPath);
            var aliases = CreateAliasRenameMap(
                manifest,
                isRoot && hasExplicitRename ? oldAlias : null,
                isRoot && hasExplicitRename ? newAlias : null,
                explicitMode: hasExplicitRename);
            var packagePlan = CreatePackagePlan(
                manifestPath,
                manifestText,
                manifest,
                aliases,
                grammarData,
                scannerData,
                normalizedOverrides);
            packagePlans.Add(packagePlan);
            planDiagnostics.AddRange(packagePlan.Diagnostics);

            if (!includePathDependencies)
            {
                continue;
            }

            foreach (var dependencyManifestPath in EnumeratePathDependencyManifests(manifestPath, manifest))
            {
                pending.Enqueue(dependencyManifestPath);
            }
        }

        var status = planDiagnostics.Count > 0 || packagePlans.Any(static package => !package.CanApply)
            ? "blocked"
            : packagePlans.Sum(static package => package.TotalEditCount) == 0
                ? "unchanged"
                : "ready";
        return new DependencyAliasRenamePlan(
            rootManifestPath,
            status,
            packagePlans.ToArray(),
            planDiagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }

    public static void ApplyPlan(DependencyAliasRenamePlan plan)
    {
        if (!plan.CanApply || plan.Packages.Any(static package => !package.CanApply))
        {
            throw new InvalidOperationException(
                "Cannot apply a blocked dependency alias rename plan.");
        }

        var filePlans = plan.Packages
            .SelectMany(static package => package.Sources.Prepend(package.Manifest))
            .Where(static file => file.Edits.Length > 0)
            .ToArray();
        var rewrittenFiles = new Dictionary<string, string>(PathComparer);

        foreach (var filePlan in filePlans)
        {
            var currentText = File.ReadAllText(filePlan.FilePath);
            var currentHash = ComputeContentHash(currentText);
            if (!string.Equals(currentHash, filePlan.OriginalContentHash, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Cannot apply dependency alias rename because '{filePlan.FilePath}' changed after the plan was created.");
            }

            rewrittenFiles[filePlan.FilePath] = ApplyEdits(currentText, filePlan.Edits);
        }

        foreach (var (filePath, rewrittenText) in rewrittenFiles)
        {
            File.WriteAllText(filePath, rewrittenText);
        }
    }

    public static string ApplyEdits(
        string source,
        IReadOnlyList<DependencyAliasRenameEdit> edits)
    {
        foreach (var edit in edits.OrderByDescending(static edit => edit.Start))
        {
            if (edit.Start < 0 || edit.Length < 0 || edit.Start + edit.Length > source.Length)
            {
                throw new InvalidOperationException(
                    $"Dependency alias rename edit [{edit.Start}, {edit.Length}] is outside the source document.");
            }

            source = source.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        }

        return source;
    }

    private static DependencyAliasRenamePackagePlan CreatePackagePlan(
        string manifestPath,
        string manifestText,
        EidosProjectManifestDocument manifest,
        IReadOnlyDictionary<string, string> aliases,
        GrammarData grammarData,
        ScannerData scannerData,
        IReadOnlyDictionary<string, string> documentOverrides)
    {
        var packageDiagnostics = ValidateAliasRenameMap(manifestPath, manifest, aliases);
        var manifestEdits = new List<DependencyAliasRenameEdit>();
        if (packageDiagnostics.Count == 0)
        {
            foreach (var (oldAlias, newAlias) in aliases)
            {
                manifestEdits.AddRange(FindManifestAliasEdits(manifestText, oldAlias, newAlias));
            }

            foreach (var oldAlias in aliases.Keys)
            {
                if (!manifestEdits.Any(edit =>
                        edit.Kind == "dependency-alias-manifest" &&
                        string.Equals(
                            manifestText.Substring(edit.Start, edit.Length),
                            oldAlias,
                            StringComparison.Ordinal)))
                {
                    packageDiagnostics.Add(
                        $"Manifest '{manifestPath}' declares dependency alias '{oldAlias}', but its TOML key could not be located losslessly.");
                }
            }
        }

        var normalizedManifestEdits = NormalizeEdits(manifestEdits, packageDiagnostics, manifestPath);
        var manifestStatus = packageDiagnostics.Count > 0
            ? "blocked"
            : normalizedManifestEdits.Length == 0 ? "unchanged" : "ready";
        if (manifestStatus == "ready")
        {
            ValidateRewrittenManifest(
                manifestPath,
                manifestText,
                normalizedManifestEdits,
                aliases,
                packageDiagnostics);
            if (packageDiagnostics.Count > 0)
            {
                manifestStatus = "blocked";
            }
        }

        var manifestPlan = new DependencyAliasRenameFilePlan(
            manifestPath,
            ComputeContentHash(manifestText),
            manifestStatus,
            packageDiagnostics.ToArray(),
            normalizedManifestEdits);

        var sourcePlans = aliases.Count == 0 || packageDiagnostics.Count > 0
            ? []
            : CreateSourcePlans(
                manifestPath,
                manifest,
                aliases,
                grammarData,
                scannerData,
                documentOverrides);

        var sourceDiagnostics = sourcePlans.SelectMany(static source => source.Diagnostics).ToArray();
        packageDiagnostics.AddRange(sourceDiagnostics.Select(diagnostic => $"{manifestPath}: {diagnostic}"));

        return new DependencyAliasRenamePackagePlan(
            manifestPath,
            aliases,
            manifestPlan,
            sourcePlans,
            packageDiagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static DependencyAliasRenameFilePlan[] CreateSourcePlans(
        string manifestPath,
        EidosProjectManifestDocument manifest,
        IReadOnlyDictionary<string, string> aliases,
        GrammarData grammarData,
        ScannerData scannerData,
        IReadOnlyDictionary<string, string> documentOverrides)
    {
        var projectDirectory = Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory();
        var languageVersion = manifest.Language?.Version ?? EidosLanguageVersions.Current;
        var namespaceRoots = manifest.Dependencies?.Keys.ToArray() ?? [];
        return EnumerateProjectSourceFiles(projectDirectory, manifest, documentOverrides.Keys)
            .Select(sourcePath => CreateSourcePlan(
                sourcePath,
                ReadDocumentText(sourcePath, documentOverrides),
                languageVersion,
                namespaceRoots,
                aliases,
                grammarData,
                scannerData))
            .ToArray();
    }

    private static DependencyAliasRenameFilePlan CreateSourcePlan(
        string sourcePath,
        string source,
        string languageVersion,
        IReadOnlyList<string> namespaceRoots,
        IReadOnlyDictionary<string, string> aliases,
        GrammarData grammarData,
        ScannerData scannerData)
    {
        var diagnostics = new List<string>();
        var lexResult = ModuleParseUtilities.LexSource(
            source,
            sourcePath,
            scannerData,
            grammarData);
        var (ast, parserDiagnostics) = SyntaxParser.Parse(
            lexResult.Tokens,
            sourcePath,
            languageVersion,
            namespaceRoots);
        var parseDiagnostics = lexResult.Diagnostics.Concat(parserDiagnostics).ToArray();
        foreach (var diagnostic in parseDiagnostics.Where(static diagnostic => diagnostic.Level == DiagnosticLevel.Error))
        {
            diagnostics.Add(FormatDiagnostic(sourcePath, diagnostic));
        }

        if (ast == null || diagnostics.Count > 0)
        {
            return new DependencyAliasRenameFilePlan(
                sourcePath,
                ComputeContentHash(source),
                "parse-error",
                diagnostics.ToArray(),
                []);
        }

        var edits = new List<DependencyAliasRenameEdit>();
        foreach (var (oldAlias, newAlias) in aliases)
        {
            CollectAstAliasEdits(ast, lexResult.Tokens, source, oldAlias, newAlias, edits);
        }

        var normalizedEdits = NormalizeEdits(edits, diagnostics, sourcePath);
        if (diagnostics.Count == 0 && normalizedEdits.Length > 0)
        {
            var rewritten = ApplyEdits(source, normalizedEdits);
            var rewrittenLex = ModuleParseUtilities.LexSource(
                rewritten,
                sourcePath,
                scannerData,
                grammarData);
            var (_, rewrittenParserDiagnostics) = SyntaxParser.Parse(
                rewrittenLex.Tokens,
                sourcePath,
                languageVersion,
                aliases.Values.Concat(namespaceRoots).Distinct(StringComparer.Ordinal).ToArray());
            foreach (var diagnostic in rewrittenLex.Diagnostics
                         .Concat(rewrittenParserDiagnostics)
                         .Where(static diagnostic => diagnostic.Level == DiagnosticLevel.Error))
            {
                diagnostics.Add($"rewritten source: {FormatDiagnostic(sourcePath, diagnostic)}");
            }
        }

        return new DependencyAliasRenameFilePlan(
            sourcePath,
            ComputeContentHash(source),
            diagnostics.Count > 0
                ? "blocked"
                : normalizedEdits.Length == 0 ? "unchanged" : "ready",
            diagnostics.ToArray(),
            normalizedEdits);
    }

    private static void CollectAstAliasEdits(
        EidosAstNode ast,
        IReadOnlyList<Token> tokens,
        string source,
        string oldAlias,
        string newAlias,
        List<DependencyAliasRenameEdit> edits)
    {
        var editPositions = edits.Select(static edit => edit.Start).ToHashSet();
        foreach (var node in EnumerateAst(ast))
        {
            switch (node)
            {
                case ImportDecl import:
                    AddPathAliasEdit(
                        import.PackageAlias,
                        import.ModulePath,
                        import.Span,
                        "dependency-alias-import",
                        import.PackageAlias != null,
                        tokens,
                        source,
                        oldAlias,
                        newAlias,
                        edits,
                        editPositions);
                    break;
                case PathExpr path:
                    AddPathAliasEdit(
                        path.PackageAlias,
                        path.ModulePath,
                        path.Span,
                        "dependency-alias-expression",
                        path.PackageAlias != null,
                        tokens,
                        source,
                        oldAlias,
                        newAlias,
                        edits,
                        editPositions);
                    break;
                case TypePath typePath:
                    AddPathAliasEdit(
                        typePath.PackageAlias,
                        typePath.ModulePath,
                        typePath.Span,
                        "dependency-alias-type",
                        typePath.PackageAlias != null,
                        tokens,
                        source,
                        oldAlias,
                        newAlias,
                        edits,
                        editPositions);
                    break;
                case CtorPattern pattern:
                    AddPathAliasEdit(
                        pattern.PackageAlias,
                        pattern.ModulePath,
                        pattern.Span,
                        "dependency-alias-pattern",
                        pattern.PackageAlias != null,
                        tokens,
                        source,
                        oldAlias,
                        newAlias,
                        edits,
                        editPositions);
                    break;
                case TraitRef traitRef:
                    AddPathAliasEdit(
                        null,
                        traitRef.ModulePath,
                        traitRef.Span,
                        "dependency-alias-trait",
                        explicitPackageAlias: false,
                        tokens,
                        source,
                        oldAlias,
                        newAlias,
                        edits,
                        editPositions);
                    break;
                case MetaInvocationSyntax invocation:
                    AddPathAliasEdit(
                        null,
                        invocation.GeneratorPath,
                        invocation.Span,
                        "dependency-alias-meta",
                        explicitPackageAlias: false,
                        tokens,
                        source,
                        oldAlias,
                        newAlias,
                        edits,
                        editPositions);
                    break;
                case GivenExpr given:
                    AddPathAliasEdit(
                        null,
                        given.EvidencePath,
                        given.Span,
                        "dependency-alias-evidence",
                        explicitPackageAlias: false,
                        tokens,
                        source,
                        oldAlias,
                        newAlias,
                        edits,
                        editPositions);
                    break;
                case EffectRequirementNode requirement:
                    AddPathAliasEdit(
                        null,
                        requirement.Path,
                        requirement.Span,
                        "dependency-alias-effect",
                        explicitPackageAlias: false,
                        tokens,
                        source,
                        oldAlias,
                        newAlias,
                        edits,
                        editPositions);
                    break;
                case EffectfulType effectful:
                    var paths = effectful.EnumerateEffectPaths().ToArray();
                    for (var index = 0; index < paths.Length; index++)
                    {
                        var span = index < effectful.EffectPathSpans.Count
                            ? effectful.EffectPathSpans[index]
                            : effectful.Span;
                        AddPathAliasEdit(
                            null,
                            paths[index],
                            span,
                            "dependency-alias-effect",
                            explicitPackageAlias: false,
                            tokens,
                            source,
                            oldAlias,
                            newAlias,
                            edits,
                            editPositions);
                    }
                    break;
                case DeclarationClause clause:
                    AddClauseAliasEdits(
                        clause,
                        tokens,
                        source,
                        oldAlias,
                        newAlias,
                        edits,
                        editPositions);
                    break;
            }
        }
    }

    private static void AddPathAliasEdit(
        string? packageAlias,
        IReadOnlyList<string> path,
        SourceSpan span,
        string kind,
        bool explicitPackageAlias,
        IReadOnlyList<Token> tokens,
        string source,
        string oldAlias,
        string newAlias,
        List<DependencyAliasRenameEdit> edits,
        HashSet<int> editPositions)
    {
        var matchesIdentity = string.Equals(packageAlias, oldAlias, StringComparison.Ordinal) ||
                              (string.IsNullOrWhiteSpace(packageAlias) &&
                               path.Count > 1 &&
                               string.Equals(path[0], oldAlias, StringComparison.Ordinal));
        if (!matchesIdentity)
        {
            return;
        }

        var token = FindAliasToken(tokens, source, span, oldAlias, explicitPackageAlias);
        if (token == null || !editPositions.Add(token.Location.Position))
        {
            return;
        }

        edits.Add(new DependencyAliasRenameEdit(
            token.Location.Position,
            token.Length,
            newAlias,
            kind,
            $"Rename dependency alias identity '{oldAlias}' to '{newAlias}'."));
    }

    private static void AddClauseAliasEdits(
        DeclarationClause clause,
        IReadOnlyList<Token> tokens,
        string source,
        string oldAlias,
        string newAlias,
        List<DependencyAliasRenameEdit> edits,
        HashSet<int> editPositions)
    {
        if (!ClauseSchema.TryGet(clause.Keyword, out var spec) ||
            spec.Arguments is not (ClauseArgumentGrammar.Path or
                ClauseArgumentGrammar.PathList or
                ClauseArgumentGrammar.MetaInvocation or
                ClauseArgumentGrammar.TokenIsland))
        {
            return;
        }

        var start = Math.Max(0, clause.Span.Position);
        var end = Math.Min(source.Length, clause.Span.EndPosition);
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Location.Position < start || token.Location.Position >= end ||
                !TokenKind.IsAnyIdentifier(token) ||
                !TokenTextEquals(source, token, oldAlias) ||
                !IsClausePathRoot(tokens, source, index, start, end) ||
                !editPositions.Add(token.Location.Position))
            {
                continue;
            }

            edits.Add(new DependencyAliasRenameEdit(
                token.Location.Position,
                token.Length,
                newAlias,
                "dependency-alias-clause",
                $"Rename dependency alias identity '{oldAlias}' in typed clause '{clause.Keyword}'."));
        }
    }

    private static Token? FindAliasToken(
        IReadOnlyList<Token> tokens,
        string source,
        SourceSpan span,
        string oldAlias,
        bool explicitPackageAlias)
    {
        var start = Math.Max(0, span.Position);
        var end = Math.Min(source.Length, Math.Max(start, span.EndPosition));
        for (var index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (token.Location.Position < start || token.Location.Position >= end ||
                !TokenKind.IsAnyIdentifier(token) ||
                !TokenTextEquals(source, token, oldAlias))
            {
                continue;
            }

            if (explicitPackageAlias || IsPathRoot(tokens, source, index, start, end))
            {
                return token;
            }
        }

        return null;
    }

    private static bool IsPathRoot(
        IReadOnlyList<Token> tokens,
        string source,
        int tokenIndex,
        int start,
        int end)
    {
        var previous = FindSignificantToken(tokens, tokenIndex, -1, start, end);
        if (previous != null && GetTokenText(source, previous) is "." or "::" or "/")
        {
            return false;
        }

        var next = FindSignificantToken(tokens, tokenIndex, 1, start, end);
        return next != null && GetTokenText(source, next) is "." or "::" or "/";
    }

    private static bool IsClausePathRoot(
        IReadOnlyList<Token> tokens,
        string source,
        int tokenIndex,
        int start,
        int end) => IsPathRoot(tokens, source, tokenIndex, start, end);

    private static Token? FindSignificantToken(
        IReadOnlyList<Token> tokens,
        int tokenIndex,
        int direction,
        int start,
        int end)
    {
        for (var index = tokenIndex + direction; index >= 0 && index < tokens.Count; index += direction)
        {
            var candidate = tokens[index];
            if (candidate.Location.Position < start || candidate.Location.Position >= end)
            {
                return null;
            }

            if (candidate is not CommentToken)
            {
                return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyDictionary<string, string> CreateAliasRenameMap(
        EidosProjectManifestDocument manifest,
        string? explicitOldAlias,
        string? explicitNewAlias,
        bool explicitMode)
    {
        var dependencies = manifest.Dependencies ??
                           new Dictionary<string, EidosProjectDependencyManifestDocument>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(explicitOldAlias))
        {
            if (!dependencies.ContainsKey(explicitOldAlias))
            {
                throw new InvalidOperationException(
                    $"Dependency alias '{explicitOldAlias}' is not declared in the root manifest.");
            }

            return string.Equals(explicitOldAlias, explicitNewAlias, StringComparison.Ordinal)
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    [explicitOldAlias] = explicitNewAlias!
                };
        }

        if (explicitMode)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var alias in dependencies.Keys)
        {
            if (ManifestNamingRules.IsDependencyAlias(alias))
            {
                continue;
            }

            var normalized = ManifestNamingRules.NormalizeDependencyAlias(alias);
            if (!string.Equals(alias, normalized, StringComparison.Ordinal))
            {
                result[alias] = normalized;
            }
        }

        return result;
    }

    private static List<string> ValidateAliasRenameMap(
        string manifestPath,
        EidosProjectManifestDocument manifest,
        IReadOnlyDictionary<string, string> aliases)
    {
        var diagnostics = new List<string>();
        var dependencies = manifest.Dependencies ??
                           new Dictionary<string, EidosProjectDependencyManifestDocument>(StringComparer.Ordinal);
        var renamedTargets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (oldAlias, newAlias) in aliases)
        {
            if (!dependencies.ContainsKey(oldAlias))
            {
                diagnostics.Add(
                    $"Manifest '{manifestPath}' does not declare dependency alias '{oldAlias}'.");
                continue;
            }

            if (!ManifestNamingRules.IsDependencyAlias(newAlias))
            {
                diagnostics.Add(
                    $"Dependency alias '{oldAlias}' normalizes to invalid name '{newAlias}' in '{manifestPath}'.");
                continue;
            }

            if (dependencies.ContainsKey(newAlias) &&
                !string.Equals(oldAlias, newAlias, StringComparison.Ordinal))
            {
                diagnostics.Add(
                    $"Dependency alias rename '{oldAlias}' -> '{newAlias}' conflicts with an existing alias in '{manifestPath}'.");
            }

            if (renamedTargets.TryGetValue(newAlias, out var existingOldAlias))
            {
                diagnostics.Add(
                    $"Dependency aliases '{existingOldAlias}' and '{oldAlias}' both normalize to '{newAlias}' in '{manifestPath}'.");
            }
            else
            {
                renamedTargets[newAlias] = oldAlias;
            }
        }

        return diagnostics;
    }

    private static void ValidateRewrittenManifest(
        string manifestPath,
        string manifestText,
        IReadOnlyList<DependencyAliasRenameEdit> edits,
        IReadOnlyDictionary<string, string> aliases,
        List<string> diagnostics)
    {
        try
        {
            var rewritten = ApplyEdits(manifestText, edits);
            var parsed = EidosProjectManifestDocument.Parse(rewritten, manifestPath);
            var dependencyKeys = parsed.Dependencies?.Keys.ToHashSet(StringComparer.Ordinal) ?? [];
            foreach (var (oldAlias, newAlias) in aliases)
            {
                if (dependencyKeys.Contains(oldAlias) || !dependencyKeys.Contains(newAlias))
                {
                    diagnostics.Add(
                        $"Manifest rewrite did not preserve dependency identity for '{oldAlias}' -> '{newAlias}' in '{manifestPath}'.");
                }
            }
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Rewritten manifest '{manifestPath}' is invalid: {ex.Message}");
        }
    }

    private static IEnumerable<DependencyAliasRenameEdit> FindManifestAliasEdits(
        string text,
        string oldAlias,
        string newAlias)
    {
        var section = string.Empty;
        var lineStart = 0;
        while (lineStart <= text.Length)
        {
            var lineEnd = text.IndexOf('\n', lineStart);
            if (lineEnd < 0)
            {
                lineEnd = text.Length;
            }

            var rawLineLength = lineEnd - lineStart;
            var rawLine = text.AsSpan(lineStart, rawLineLength);
            if (rawLine.Length > 0 && rawLine[^1] == '\r')
            {
                rawLine = rawLine[..^1];
            }

            var commentOffset = FindTomlCommentOffset(rawLine);
            var content = rawLine[..commentOffset];
            var trimmedStart = 0;
            while (trimmedStart < content.Length && char.IsWhiteSpace(content[trimmedStart]))
            {
                trimmedStart++;
            }

            var trimmedEnd = content.Length;
            while (trimmedEnd > trimmedStart && char.IsWhiteSpace(content[trimmedEnd - 1]))
            {
                trimmedEnd--;
            }

            var trimmed = content[trimmedStart..trimmedEnd];
            if (trimmed.Length >= 2 && trimmed[0] == '[' && trimmed[^1] == ']')
            {
                var doubleBracket = trimmed.Length >= 4 && trimmed[1] == '[' && trimmed[^2] == ']';
                var innerStart = doubleBracket ? 2 : 1;
                var innerLength = trimmed.Length - (doubleBracket ? 4 : 2);
                var inner = trimmed.Slice(innerStart, innerLength).Trim();
                section = inner.ToString();

                const string dependencyPrefix = "dependencies.";
                if (inner.StartsWith(dependencyPrefix, StringComparison.Ordinal))
                {
                    var aliasSlice = inner[dependencyPrefix.Length..].Trim();
                    if (TryReadTomlKey(aliasSlice, out var key, out var keyStart, out var keyLength) &&
                        string.Equals(key, oldAlias, StringComparison.Ordinal))
                    {
                        var keyOffset = FindManifestHeaderKeyOffset(
                            rawLine,
                            aliasSlice,
                            keyStart);
                        if (keyOffset >= 0)
                        {
                            yield return new DependencyAliasRenameEdit(
                                lineStart + keyOffset,
                                keyLength,
                                newAlias,
                                "dependency-alias-manifest",
                                $"Rename dependency manifest identity '{oldAlias}' to '{newAlias}'.");
                        }
                    }
                }
            }
            else if (string.Equals(section, "dependencies", StringComparison.Ordinal))
            {
                var equalsOffset = FindTomlEqualsOffset(trimmed);
                if (equalsOffset > 0)
                {
                    var keySlice = trimmed[..equalsOffset].Trim();
                    if (TryReadTomlKey(keySlice, out var key, out var keyStart, out var keyLength) &&
                        string.Equals(key, oldAlias, StringComparison.Ordinal))
                    {
                        var keySliceOffset = IndexOfSlice(trimmed[..equalsOffset], keySlice);
                        yield return new DependencyAliasRenameEdit(
                            lineStart + trimmedStart + keySliceOffset + keyStart,
                            keyLength,
                            newAlias,
                            "dependency-alias-manifest",
                            $"Rename dependency manifest identity '{oldAlias}' to '{newAlias}'.");
                    }
                }
            }

            if (lineEnd == text.Length)
            {
                break;
            }

            lineStart = lineEnd + 1;
        }
    }

    private static int FindManifestHeaderKeyOffset(
        ReadOnlySpan<char> rawLine,
        ReadOnlySpan<char> aliasSlice,
        int keyStart)
    {
        // Locate the exact key slice captured from the parsed header instead
        // of reconstructing its spelling. This preserves literal TOML keys
        // (`'Alias'`) as well as basic quoted keys (`"Alias"`) and keeps the
        // replacement limited to the key contents.
        var aliasOffset = rawLine.IndexOf(aliasSlice, StringComparison.Ordinal);
        return aliasOffset >= 0 ? aliasOffset + keyStart : -1;
    }

    private static int FindTomlCommentOffset(ReadOnlySpan<char> line)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\' && quote == '"')
                {
                    escaped = true;
                    continue;
                }

                if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '#')
            {
                return index;
            }
        }

        return line.Length;
    }

    private static int FindTomlEqualsOffset(ReadOnlySpan<char> line)
    {
        var quote = '\0';
        var escaped = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (quote != '\0')
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\' && quote == '"')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    quote = '\0';
                }

                continue;
            }

            if (character is '"' or '\'')
            {
                quote = character;
            }
            else if (character == '=')
            {
                return index;
            }
        }

        return -1;
    }

    private static bool TryReadTomlKey(
        ReadOnlySpan<char> text,
        out string value,
        out int contentStart,
        out int contentLength)
    {
        value = string.Empty;
        contentStart = 0;
        contentLength = 0;
        if (text.Length == 0)
        {
            return false;
        }

        if (text[0] is '"' or '\'')
        {
            var quote = text[0];
            var escaped = false;
            for (var index = 1; index < text.Length; index++)
            {
                var character = text[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (character == '\\' && quote == '"')
                {
                    escaped = true;
                    continue;
                }

                if (character != quote)
                {
                    continue;
                }

                contentStart = 1;
                contentLength = index - 1;
                value = text.Slice(contentStart, contentLength).ToString();
                return true;
            }

            return false;
        }

        var length = 0;
        while (length < text.Length && !char.IsWhiteSpace(text[length]) && text[length] != '.')
        {
            length++;
        }

        if (length == 0)
        {
            return false;
        }

        contentLength = length;
        value = text[..length].ToString();
        return true;
    }

    private static int IndexOfSlice(ReadOnlySpan<char> container, ReadOnlySpan<char> slice)
    {
        if (slice.Length == 0)
        {
            return 0;
        }

        return container.IndexOf(slice, StringComparison.Ordinal);
    }

    private static DependencyAliasRenameEdit[] NormalizeEdits(
        IEnumerable<DependencyAliasRenameEdit> edits,
        List<string> diagnostics,
        string sourcePath)
    {
        var ordered = edits
            .OrderBy(static edit => edit.Start)
            .ThenBy(static edit => edit.Length)
            .ToArray();
        var normalized = new List<DependencyAliasRenameEdit>(ordered.Length);
        foreach (var edit in ordered)
        {
            if (normalized.Count == 0)
            {
                normalized.Add(edit);
                continue;
            }

            var previous = normalized[^1];
            if (previous.Start == edit.Start && previous.Length == edit.Length)
            {
                if (!string.Equals(previous.Replacement, edit.Replacement, StringComparison.Ordinal))
                {
                    diagnostics.Add(
                        $"Conflicting dependency alias edits at offset {edit.Start} in '{sourcePath}'.");
                }

                continue;
            }

            if (previous.Start + previous.Length > edit.Start)
            {
                diagnostics.Add(
                    $"Overlapping dependency alias edits at offsets {previous.Start} and {edit.Start} in '{sourcePath}'.");
                continue;
            }

            normalized.Add(edit);
        }

        return normalized.ToArray();
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
            foreach (var child in AstStableNodeTraversal.GetStructuralChildren(node))
            {
                pending.Push(child);
            }
        }
    }

    private static IEnumerable<string> EnumeratePathDependencyManifests(
        string manifestPath,
        EidosProjectManifestDocument manifest)
    {
        if (manifest.Dependencies is not { Count: > 0 } dependencies)
        {
            yield break;
        }

        var projectDirectory = Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory();
        foreach (var dependency in dependencies.Values)
        {
            if (string.IsNullOrWhiteSpace(dependency.Path))
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.GetFullPath(dependency.Path, projectDirectory);
            }
            catch
            {
                continue;
            }

            if (Directory.Exists(candidate))
            {
                candidate = Path.Combine(candidate, EidosProjectConfigurationLoader.DefaultFileName);
            }

            if (File.Exists(candidate) &&
                string.Equals(
                    Path.GetFileName(candidate),
                    EidosProjectConfigurationLoader.DefaultFileName,
                    StringComparison.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(candidate);
            }
        }
    }

    private static IEnumerable<string> EnumerateProjectSourceFiles(
        string projectDirectory,
        EidosProjectManifestDocument manifest,
        IEnumerable<string> openDocumentPaths)
    {
        var sourceRoots = manifest.SourceRoots is { Length: > 0 }
            ? manifest.SourceRoots
            : ["src"];
        var seen = new HashSet<string>(PathComparer);
        var normalizedRoots = new List<string>(sourceRoots.Length);
        foreach (var sourceRoot in sourceRoots)
        {
            string root;
            try
            {
                root = Path.GetFullPath(sourceRoot, projectDirectory);
            }
            catch
            {
                continue;
            }

            normalizedRoots.Add(root);
            if (!Directory.Exists(root))
            {
                continue;
            }

            foreach (var file in EnumerateEidosFiles(root))
            {
                if (seen.Add(file))
                {
                    yield return file;
                }
            }
        }

        foreach (var openDocumentPath in openDocumentPaths.Order(PathComparer))
        {
            if (!string.Equals(
                    Path.GetExtension(openDocumentPath),
                    ".eidos",
                    StringComparison.OrdinalIgnoreCase) ||
                !normalizedRoots.Any(root => IsSourcePathUnderRoot(openDocumentPath, root)) ||
                !seen.Add(openDocumentPath))
            {
                continue;
            }

            yield return openDocumentPath;
        }
    }

    private static bool IsSourcePathUnderRoot(string sourcePath, string sourceRoot)
    {
        var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
        if (Path.IsPathRooted(relativePath) ||
            string.Equals(relativePath, "..", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            return false;
        }

        return !relativePath
            .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => IgnoredDirectoryNames.Contains(segment, StringComparer.OrdinalIgnoreCase));
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
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files.Order(StringComparer.OrdinalIgnoreCase))
            {
                yield return Path.GetFullPath(file);
            }

            foreach (var directory in directories.Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!IgnoredDirectoryNames.Contains(
                        Path.GetFileName(directory),
                        StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(directory);
                }
            }
        }
    }

    private static DependencyAliasRenamePackagePlan CreateBlockedPackagePlan(
        string manifestPath,
        string diagnostic)
    {
        var manifestPlan = new DependencyAliasRenameFilePlan(
            manifestPath,
            string.Empty,
            "blocked",
            [diagnostic],
            []);
        return new DependencyAliasRenamePackagePlan(
            manifestPath,
            new Dictionary<string, string>(StringComparer.Ordinal),
            manifestPlan,
            [],
            [diagnostic]);
    }

    private static string ResolveManifestPath(string inputPath)
    {
        var fullPath = Path.GetFullPath(string.IsNullOrWhiteSpace(inputPath) ? "." : inputPath);
        if (Directory.Exists(fullPath))
        {
            fullPath = Path.Combine(fullPath, EidosProjectConfigurationLoader.DefaultFileName);
        }

        if (!File.Exists(fullPath) ||
            !string.Equals(
                Path.GetFileName(fullPath),
                EidosProjectConfigurationLoader.DefaultFileName,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Project manifest not found: {fullPath}");
        }

        return Path.GetFullPath(fullPath);
    }

    private static string FormatDiagnostic(string sourcePath, Diagnostic.Diagnostic diagnostic)
    {
        var label = diagnostic.Labels.FirstOrDefault();
        return label == null
            ? $"{sourcePath}: {diagnostic.Code} {diagnostic.Message}"
            : $"{sourcePath}({label.Span.Location.Line + 1},{label.Span.Location.Column + 1}): {diagnostic.Code} {diagnostic.Message}";
    }

    private static bool TokenTextEquals(string source, Token token, string value) =>
        string.Equals(GetTokenText(source, token), value, StringComparison.Ordinal);

    private static string GetTokenText(string source, Token token)
    {
        var position = token.Location.Position;
        return position >= 0 && token.Length >= 0 && position + token.Length <= source.Length
            ? source.Substring(position, token.Length)
            : token.ToString() ?? string.Empty;
    }

    private static string ComputeContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static IReadOnlyDictionary<string, string> NormalizeDocumentOverrides(
        IReadOnlyDictionary<string, string>? documentOverrides)
    {
        if (documentOverrides == null || documentOverrides.Count == 0)
        {
            return new Dictionary<string, string>(PathComparer);
        }

        var normalized = new Dictionary<string, string>(PathComparer);
        foreach (var (path, text) in documentOverrides)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            try
            {
                normalized[Path.GetFullPath(path)] = text;
            }
            catch
            {
            }
        }

        return normalized;
    }

    private static string ReadDocumentText(
        string path,
        IReadOnlyDictionary<string, string> documentOverrides)
    {
        var fullPath = Path.GetFullPath(path);
        return documentOverrides.TryGetValue(fullPath, out var text)
            ? text
            : File.ReadAllText(fullPath);
    }

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
