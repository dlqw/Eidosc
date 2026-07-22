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

public sealed record ModuleIdentityMovePlan(
    string SourcePath,
    string DestinationPath,
    string OriginalContentHash);

public sealed record ModuleIdentityRenamePackagePlan(
    string ManifestPath,
    DependencyAliasRenameFilePlan Manifest,
    DependencyAliasRenameFilePlan[] Sources,
    ModuleIdentityMovePlan[] Moves,
    string[] Diagnostics)
{
    public bool CanApply =>
        Diagnostics.Length == 0 &&
        IsApplicable(Manifest.Status) &&
        Sources.All(static source => IsApplicable(source.Status));

    public int TotalEditCount => Manifest.Edits.Length + Sources.Sum(static source => source.Edits.Length);

    private static bool IsApplicable(string status) =>
        string.Equals(status, "ready", StringComparison.Ordinal) ||
        string.Equals(status, "unchanged", StringComparison.Ordinal);
}

public sealed record ModuleIdentityRenamePlan(
    string RootManifestPath,
    string Status,
    ModuleIdentityRenamePackagePlan[] Packages,
    string[] Diagnostics)
{
    public bool CanApply =>
        (string.Equals(Status, "ready", StringComparison.Ordinal) ||
         string.Equals(Status, "unchanged", StringComparison.Ordinal)) &&
        Packages.All(static package => package.CanApply);

    public int TotalEditCount => Packages.Sum(static package => package.TotalEditCount);
    public int TotalMoveCount => Packages.Sum(static package => package.Moves.Length);
}

/// <summary>
/// Renames a module from its source-root-relative file identity outward. The
/// physical path, explicit module declaration, manifest target, and parsed
/// module-path references are planned and validated as one operation.
/// </summary>
public static class ModuleIdentityRenamePlanner
{
    private sealed record ModuleRename(
        string SourceRoot,
        string SourcePath,
        string DestinationPath,
        string RelativeSourcePath,
        string RelativeDestinationPath,
        string[] OldModulePath,
        string[] NewModulePath);

    private sealed record PreparedFileChange(
        string SourcePath,
        string DestinationPath,
        string BackupPath,
        string? PreparedPath,
        bool HasRewrite);

    public static ModuleIdentityRenamePlan CreatePlan(
        string inputPath,
        bool includePathDependencies = true,
        IReadOnlyDictionary<string, string>? documentOverrides = null)
    {
        var rootManifestPath = ResolveManifestPath(inputPath);
        var overrides = NormalizeDocumentOverrides(documentOverrides);
        var (grammarData, scannerData) = LexerTableBuilder.Build();
        var pending = new Queue<string>();
        var visited = new HashSet<string>(PathComparer);
        var packages = new List<ModuleIdentityRenamePackagePlan>();
        var diagnostics = new List<string>();
        pending.Enqueue(rootManifestPath);

        while (pending.Count > 0)
        {
            var manifestPath = Path.GetFullPath(pending.Dequeue());
            if (!visited.Add(manifestPath))
            {
                continue;
            }

            try
            {
                var manifestText = ReadDocumentText(manifestPath, overrides);
                var manifest = EidosProjectManifestDocument.Parse(manifestText, manifestPath);
                var package = CreatePackagePlan(
                    manifestPath,
                    manifestText,
                    manifest,
                    overrides,
                    grammarData,
                    scannerData);
                packages.Add(package);
                diagnostics.AddRange(package.Diagnostics);

                if (includePathDependencies)
                {
                    foreach (var dependencyManifest in EnumeratePathDependencyManifests(manifestPath, manifest))
                    {
                        pending.Enqueue(dependencyManifest);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                diagnostics.Add($"Unable to plan module identities for '{manifestPath}': {ex.Message}");
            }
        }

        var distinctDiagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToArray();
        var status = distinctDiagnostics.Length > 0 || packages.Any(static package => !package.CanApply)
            ? "blocked"
            : packages.Sum(static package => package.TotalEditCount + package.Moves.Length) == 0
                ? "unchanged"
                : "ready";
        return new ModuleIdentityRenamePlan(
            rootManifestPath,
            status,
            packages.ToArray(),
            distinctDiagnostics);
    }

    public static void ApplyPlan(ModuleIdentityRenamePlan plan)
    {
        if (!plan.CanApply)
        {
            throw new InvalidOperationException("Cannot apply a blocked module identity rename plan.");
        }

        var files = plan.Packages
            .SelectMany(static package => package.Sources.Prepend(package.Manifest))
            .Where(static file => file.Edits.Length > 0)
            .ToArray();
        var moves = plan.Packages.SelectMany(static package => package.Moves).ToArray();
        var currentText = new Dictionary<string, string>(PathComparer);
        var rewritten = new Dictionary<string, string>(PathComparer);

        foreach (var file in files)
        {
            var current = File.ReadAllText(file.FilePath);
            ValidateHash(file.FilePath, file.OriginalContentHash, current);
            currentText[file.FilePath] = current;
            rewritten[file.FilePath] = DependencyAliasRenamePlanner.ApplyEdits(current, file.Edits);
        }

        foreach (var move in moves)
        {
            var current = currentText.TryGetValue(move.SourcePath, out var alreadyRead)
                ? alreadyRead
                : File.ReadAllText(move.SourcePath);
            ValidateHash(move.SourcePath, move.OriginalContentHash, current);
            currentText[move.SourcePath] = current;
            if (File.Exists(move.DestinationPath) && !PathsEqual(move.SourcePath, move.DestinationPath))
            {
                throw new InvalidOperationException(
                    $"Cannot move module '{move.SourcePath}' because destination '{move.DestinationPath}' exists.");
            }
        }

        var moveBySource = moves.ToDictionary(static move => move.SourcePath, PathComparer);
        var affectedPaths = rewritten.Keys
            .Concat(moves.Select(static move => move.SourcePath))
            .Distinct(PathComparer)
            .ToArray();
        var preparedChanges = new List<PreparedFileChange>(affectedPaths.Length);
        try
        {
            foreach (var sourcePath in affectedPaths)
            {
                var destinationPath = moveBySource.TryGetValue(sourcePath, out var move)
                    ? move.DestinationPath
                    : sourcePath;
                var backupPath = CreateTransactionPath(sourcePath, "backup");
                string? preparedPath = null;
                if (rewritten.TryGetValue(sourcePath, out var content))
                {
                    preparedPath = CreateTransactionPath(sourcePath, "content");
                }

                var preparedChange = new PreparedFileChange(
                    sourcePath,
                    destinationPath,
                    backupPath,
                    preparedPath,
                    preparedPath != null);
                preparedChanges.Add(preparedChange);
                if (preparedPath != null)
                {
                    File.WriteAllText(preparedPath, content!);
                }
            }
        }
        catch
        {
            DeleteTransactionArtifacts(preparedChanges);
            throw;
        }

        var staged = new List<PreparedFileChange>(preparedChanges.Count);
        var committed = new List<PreparedFileChange>(preparedChanges.Count);
        var createdDirectories = new HashSet<string>(PathComparer);
        try
        {
            foreach (var change in preparedChanges)
            {
                File.Move(change.SourcePath, change.BackupPath);
                staged.Add(change);
            }

            foreach (var change in preparedChanges)
            {
                var destinationDirectory = Path.GetDirectoryName(change.DestinationPath);
                if (!string.IsNullOrWhiteSpace(destinationDirectory))
                {
                    TrackMissingDirectories(destinationDirectory, createdDirectories);
                    Directory.CreateDirectory(destinationDirectory);
                }

                File.Move(
                    change.HasRewrite ? change.PreparedPath! : change.BackupPath,
                    change.DestinationPath);
                committed.Add(change);
            }
        }
        catch (Exception applyError)
        {
            var rollbackErrors = new List<Exception>();
            foreach (var change in committed.AsEnumerable().Reverse())
            {
                try
                {
                    if (change.HasRewrite)
                    {
                        if (File.Exists(change.DestinationPath))
                        {
                            File.Delete(change.DestinationPath);
                        }
                    }
                    else if (File.Exists(change.DestinationPath))
                    {
                        File.Move(change.DestinationPath, change.BackupPath);
                    }
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            foreach (var change in staged.AsEnumerable().Reverse())
            {
                try
                {
                    if (!File.Exists(change.BackupPath))
                    {
                        continue;
                    }

                    var sourceDirectory = Path.GetDirectoryName(change.SourcePath);
                    if (!string.IsNullOrWhiteSpace(sourceDirectory))
                    {
                        Directory.CreateDirectory(sourceDirectory);
                    }
                    File.Move(change.BackupPath, change.SourcePath);
                }
                catch (Exception rollbackError)
                {
                    rollbackErrors.Add(rollbackError);
                }
            }

            DeleteTransactionArtifacts(preparedChanges);
            RemoveEmptyDirectories(createdDirectories);
            if (rollbackErrors.Count > 0)
            {
                throw new InvalidOperationException(
                    "Module identity rename failed and could not be rolled back completely.",
                    new AggregateException([applyError, .. rollbackErrors]));
            }
            throw;
        }

        DeleteTransactionArtifacts(preparedChanges);
        RemoveEmptySourceDirectories(moves);
    }

    private static ModuleIdentityRenamePackagePlan CreatePackagePlan(
        string manifestPath,
        string manifestText,
        EidosProjectManifestDocument manifest,
        IReadOnlyDictionary<string, string> overrides,
        GrammarData grammarData,
        ScannerData scannerData)
    {
        var projectDirectory = Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory();
        var diagnostics = new List<string>();
        var renames = DiscoverModuleRenames(projectDirectory, manifest, overrides.Keys, diagnostics);
        ValidateMoveSet(renames, diagnostics);

        var manifestEdits = FindTargetEntryEdits(manifestText, projectDirectory, renames).ToArray();
        var manifestPlan = new DependencyAliasRenameFilePlan(
            manifestPath,
            ComputeContentHash(manifestText),
            diagnostics.Count > 0 ? "blocked" : manifestEdits.Length == 0 ? "unchanged" : "ready",
            diagnostics.ToArray(),
            manifestEdits);

        var languageVersion = manifest.Language?.Version ?? EidosLanguageVersions.Current;
        var namespaceRoots = manifest.Dependencies?.Keys.ToArray() ?? [];
        var sourcePlans = EnumerateProjectSourceFiles(projectDirectory, manifest, overrides.Keys)
            .Select(sourcePath => CreateSourcePlan(
                sourcePath,
                ReadDocumentText(sourcePath, overrides),
                languageVersion,
                namespaceRoots,
                renames,
                grammarData,
                scannerData))
            .ToArray();
        diagnostics.AddRange(sourcePlans.SelectMany(static source => source.Diagnostics));

        return new ModuleIdentityRenamePackagePlan(
            manifestPath,
            manifestPlan,
            sourcePlans,
            renames
                .Where(rename => !string.Equals(
                    rename.SourcePath,
                    rename.DestinationPath,
                    StringComparison.Ordinal))
                .Select(rename => new ModuleIdentityMovePlan(
                    rename.SourcePath,
                    rename.DestinationPath,
                    ComputeContentHash(ReadDocumentText(rename.SourcePath, overrides))))
                .ToArray(),
            diagnostics.Distinct(StringComparer.Ordinal).ToArray());
    }

    private static DependencyAliasRenameFilePlan CreateSourcePlan(
        string sourcePath,
        string source,
        string languageVersion,
        IReadOnlyList<string> namespaceRoots,
        IReadOnlyList<ModuleRename> renames,
        GrammarData grammarData,
        ScannerData scannerData)
    {
        var diagnostics = new List<string>();
        var lex = ModuleParseUtilities.LexSource(source, sourcePath, scannerData, grammarData);
        var (ast, parserDiagnostics) = SyntaxParser.Parse(
            lex.Tokens,
            sourcePath,
            languageVersion,
            namespaceRoots);
        foreach (var diagnostic in lex.Diagnostics.Concat(parserDiagnostics)
                     .Where(static diagnostic => diagnostic.Level == DiagnosticLevel.Error))
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

        var edits = CollectModulePathEdits(ast, lex.Tokens, source, renames, diagnostics);
        if (diagnostics.Count == 0 && edits.Length > 0)
        {
            var rewritten = DependencyAliasRenamePlanner.ApplyEdits(source, edits);
            var rewrittenLex = ModuleParseUtilities.LexSource(rewritten, sourcePath, scannerData, grammarData);
            var (_, rewrittenDiagnostics) = SyntaxParser.Parse(
                rewrittenLex.Tokens,
                sourcePath,
                languageVersion,
                namespaceRoots);
            foreach (var diagnostic in rewrittenLex.Diagnostics.Concat(rewrittenDiagnostics)
                         .Where(static diagnostic => diagnostic.Level == DiagnosticLevel.Error))
            {
                diagnostics.Add($"rewritten source: {FormatDiagnostic(sourcePath, diagnostic)}");
            }
        }

        return new DependencyAliasRenameFilePlan(
            sourcePath,
            ComputeContentHash(source),
            diagnostics.Count > 0 ? "blocked" : edits.Length == 0 ? "unchanged" : "ready",
            diagnostics.ToArray(),
            edits);
    }

    private static DependencyAliasRenameEdit[] CollectModulePathEdits(
        ModuleDecl ast,
        IReadOnlyList<Token> tokens,
        string source,
        IReadOnlyList<ModuleRename> renames,
        List<string> diagnostics)
    {
        var edits = new List<DependencyAliasRenameEdit>();
        var positions = new HashSet<int>();
        foreach (var node in EnumerateAst(ast))
        {
            foreach (var rename in renames.OrderByDescending(static rename => rename.OldModulePath.Length))
            {
                switch (node)
                {
                    case ModuleDecl module when module.PackageAlias == null &&
                                                PathsEqual(module.Path, rename.OldModulePath):
                        AddPathEdits(module.Span, module.Path, rename, "module-declaration", tokens, source, edits, positions);
                        break;
                    case ImportDecl import when import.PackageAlias == null &&
                                                HasPrefix(import.ModulePath, rename.OldModulePath):
                        AddPathEdits(import.Span, import.ModulePath, rename, "module-import", tokens, source, edits, positions);
                        break;
                    case PathExpr path when path.PackageAlias == null &&
                                            HasPrefix(path.ModulePath, rename.OldModulePath):
                        AddPathEdits(path.Span, path.ModulePath, rename, "module-expression", tokens, source, edits, positions);
                        break;
                    case TypePath type when type.PackageAlias == null &&
                                            HasPrefix(type.ModulePath, rename.OldModulePath):
                        AddPathEdits(type.Span, type.ModulePath, rename, "module-type", tokens, source, edits, positions);
                        break;
                    case CtorPattern pattern when pattern.PackageAlias == null &&
                                                  HasPrefix(pattern.ModulePath, rename.OldModulePath):
                        AddPathEdits(pattern.Span, pattern.ModulePath, rename, "module-pattern", tokens, source, edits, positions);
                        break;
                    case TraitRef trait when HasPrefix(trait.ModulePath, rename.OldModulePath):
                        AddPathEdits(trait.Span, trait.ModulePath, rename, "module-trait", tokens, source, edits, positions);
                        break;
                    case MetaInvocationSyntax invocation when HasPrefix(invocation.GeneratorPath, rename.OldModulePath):
                        AddPathEdits(invocation.Span, invocation.GeneratorPath, rename, "module-meta", tokens, source, edits, positions);
                        break;
                }
            }
        }

        return NormalizeEdits(edits, diagnostics, source);
    }

    private static void AddPathEdits(
        SourceSpan span,
        IReadOnlyList<string> path,
        ModuleRename rename,
        string kind,
        IReadOnlyList<Token> tokens,
        string source,
        List<DependencyAliasRenameEdit> edits,
        HashSet<int> positions)
    {
        var identifiers = tokens
            .Where(token => token.Location.Position >= span.Position &&
                            token.Location.Position < span.EndPosition &&
                            TokenKind.IsAnyIdentifier(token))
            .ToArray();
        for (var start = 0; start + rename.OldModulePath.Length <= identifiers.Length; start++)
        {
            var matches = true;
            for (var index = 0; index < rename.OldModulePath.Length; index++)
            {
                if (!string.Equals(TokenText(source, identifiers[start + index]), rename.OldModulePath[index], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            for (var index = 0; index < rename.OldModulePath.Length; index++)
            {
                if (string.Equals(rename.OldModulePath[index], rename.NewModulePath[index], StringComparison.Ordinal))
                {
                    continue;
                }

                var token = identifiers[start + index];
                if (positions.Add(token.Location.Position))
                {
                    edits.Add(new DependencyAliasRenameEdit(
                        token.Location.Position,
                        token.Length,
                        rename.NewModulePath[index],
                        kind,
                        $"Rename module identity '{string.Join('/', rename.OldModulePath)}' to '{string.Join('/', rename.NewModulePath)}'."));
                }
            }

            return;
        }
    }

    private static List<ModuleRename> DiscoverModuleRenames(
        string projectDirectory,
        EidosProjectManifestDocument manifest,
        IEnumerable<string> overridePaths,
        List<string> diagnostics)
    {
        var roots = manifest.SourceRoots is { Length: > 0 } ? manifest.SourceRoots : ["src"];
        var renames = new List<ModuleRename>();
        foreach (var rootValue in roots)
        {
            var root = Path.GetFullPath(rootValue, projectDirectory);
            foreach (var sourcePath in EnumerateSourceFiles(root, overridePaths))
            {
                var relative = Path.GetRelativePath(root, sourcePath);
                if (Path.IsPathRooted(relative) || relative.StartsWith("..", StringComparison.Ordinal))
                {
                    continue;
                }

                var segments = relative.Replace('\\', '/').Split('/');
                var fileName = segments[^1];
                var stem = Path.GetFileNameWithoutExtension(fileName);
                var isModuleDirectoryFile = string.Equals(stem, "mod", StringComparison.OrdinalIgnoreCase);
                var oldModulePath = isModuleDirectoryFile
                    ? segments[..^1]
                    : segments[..^1].Append(stem).ToArray();
                var newSegments = segments
                    .Select((segment, index) => index == segments.Length - 1
                        ? ManifestNamingRules.NormalizeDependencyAlias(Path.GetFileNameWithoutExtension(segment)) + ".eidos"
                        : ManifestNamingRules.NormalizeDependencyAlias(segment))
                    .ToArray();
                if (isModuleDirectoryFile)
                {
                    newSegments[^1] = "mod.eidos";
                }

                var newModulePath = isModuleDirectoryFile
                    ? newSegments[..^1].Select(ManifestNamingRules.NormalizeModulePathSegment).ToArray()
                    : newSegments[..^1]
                        .Append(Path.GetFileNameWithoutExtension(newSegments[^1]))
                        .Select(ManifestNamingRules.NormalizeModulePathSegment)
                        .ToArray();
                if (oldModulePath.SequenceEqual(newModulePath, StringComparer.Ordinal) &&
                    segments.SequenceEqual(newSegments, StringComparer.Ordinal))
                {
                    continue;
                }

                if (oldModulePath.Length != newModulePath.Length || oldModulePath.Length == 0)
                {
                    diagnostics.Add($"Cannot derive module identity for '{sourcePath}'.");
                    continue;
                }

                renames.Add(new ModuleRename(
                    root,
                    Path.GetFullPath(sourcePath),
                    Path.GetFullPath(Path.Combine(root, Path.Combine(newSegments))),
                    relative.Replace('\\', '/'),
                    string.Join('/', newSegments),
                    oldModulePath,
                    newModulePath));
            }
        }

        return renames;
    }

    private static void ValidateMoveSet(IReadOnlyList<ModuleRename> renames, List<string> diagnostics)
    {
        foreach (var group in renames.GroupBy(static rename => rename.DestinationPath, PathComparer))
        {
            if (group.Count() > 1)
            {
                diagnostics.Add(
                    $"Module paths '{string.Join("', '", group.Select(static rename => rename.SourcePath))}' normalize to the same destination '{group.Key}'.");
            }
        }

        var sources = renames.Select(static rename => rename.SourcePath).ToHashSet(PathComparer);
        foreach (var rename in renames)
        {
            if (File.Exists(rename.DestinationPath) && !sources.Contains(rename.DestinationPath))
            {
                diagnostics.Add(
                    $"Module destination '{rename.DestinationPath}' already exists for '{rename.SourcePath}'.");
            }
        }
    }

    private static IEnumerable<DependencyAliasRenameEdit> FindTargetEntryEdits(
        string manifestText,
        string projectDirectory,
        IReadOnlyList<ModuleRename> renames)
    {
        foreach (var rename in renames)
        {
            var oldEntry = Path.GetRelativePath(projectDirectory, rename.SourcePath).Replace('\\', '/');
            var newEntry = Path.GetRelativePath(projectDirectory, rename.DestinationPath).Replace('\\', '/');
            var search = 0;
            while ((search = manifestText.IndexOf(oldEntry, search, StringComparison.Ordinal)) >= 0)
            {
                var before = search > 0 ? manifestText[search - 1] : '\0';
                var afterIndex = search + oldEntry.Length;
                var after = afterIndex < manifestText.Length ? manifestText[afterIndex] : '\0';
                if (before is '"' or '\'' && after == before)
                {
                    yield return new DependencyAliasRenameEdit(
                        search,
                        oldEntry.Length,
                        newEntry,
                        "module-target-entry",
                        $"Update target entry for module '{string.Join('/', rename.OldModulePath)}'.");
                }

                search += oldEntry.Length;
            }
        }
    }

    private static DependencyAliasRenameEdit[] NormalizeEdits(
        IEnumerable<DependencyAliasRenameEdit> edits,
        List<string> diagnostics,
        string source)
    {
        var normalized = new List<DependencyAliasRenameEdit>();
        foreach (var edit in edits.OrderBy(static edit => edit.Start).ThenBy(static edit => edit.Length))
        {
            if (normalized.Count > 0)
            {
                var previous = normalized[^1];
                if (previous.Start == edit.Start && previous.Length == edit.Length)
                {
                    if (!string.Equals(previous.Replacement, edit.Replacement, StringComparison.Ordinal))
                    {
                        diagnostics.Add($"Conflicting module identity edits at offset {edit.Start}.");
                    }
                    continue;
                }
                if (previous.Start + previous.Length > edit.Start)
                {
                    diagnostics.Add($"Overlapping module identity edits at offsets {previous.Start} and {edit.Start}.");
                    continue;
                }
            }

            if (edit.Start < 0 || edit.Start + edit.Length > source.Length)
            {
                diagnostics.Add($"Module identity edit at offset {edit.Start} is outside the source document.");
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

    private static IEnumerable<string> EnumerateProjectSourceFiles(
        string projectDirectory,
        EidosProjectManifestDocument manifest,
        IEnumerable<string> overridePaths)
    {
        var roots = manifest.SourceRoots is { Length: > 0 } ? manifest.SourceRoots : ["src"];
        var seen = new HashSet<string>(PathComparer);
        foreach (var rootValue in roots)
        {
            var root = Path.GetFullPath(rootValue, projectDirectory);
            foreach (var source in EnumerateSourceFiles(root, overridePaths))
            {
                if (seen.Add(source))
                {
                    yield return source;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateSourceFiles(string root, IEnumerable<string> overridePaths)
    {
        if (Directory.Exists(root))
        {
            foreach (var file in Directory.EnumerateFiles(root, "*.eidos", SearchOption.AllDirectories))
            {
                yield return Path.GetFullPath(file);
            }
        }

        foreach (var path in overridePaths)
        {
            if (string.Equals(Path.GetExtension(path), ".eidos", StringComparison.OrdinalIgnoreCase) &&
                IsUnderRoot(path, root))
            {
                yield return Path.GetFullPath(path);
            }
        }
    }

    private static IEnumerable<string> EnumeratePathDependencyManifests(
        string manifestPath,
        EidosProjectManifestDocument manifest)
    {
        if (manifest.Dependencies == null)
        {
            yield break;
        }
        var projectDirectory = Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory();
        foreach (var dependency in manifest.Dependencies.Values)
        {
            if (string.IsNullOrWhiteSpace(dependency.Path))
            {
                continue;
            }
            var candidate = Path.GetFullPath(dependency.Path, projectDirectory);
            if (Directory.Exists(candidate))
            {
                candidate = Path.Combine(candidate, EidosProjectConfigurationLoader.DefaultFileName);
            }
            if (File.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }

    private static void RemoveEmptySourceDirectories(IEnumerable<ModuleIdentityMovePlan> moves)
    {
        RemoveEmptyDirectories(moves
            .Select(move => Path.GetDirectoryName(move.SourcePath))
            .Where(static directory => !string.IsNullOrWhiteSpace(directory))
            .Select(static directory => directory!)
            .Distinct(PathComparer));
    }

    private static string CreateTransactionPath(string sourcePath, string purpose) =>
        sourcePath + $".eidos-rename-{purpose}-{Guid.NewGuid():N}.tmp";

    private static void DeleteTransactionArtifacts(IEnumerable<PreparedFileChange> changes)
    {
        foreach (var path in changes
                     .SelectMany(static change => new[] { change.PreparedPath, change.BackupPath })
                     .Where(static path => !string.IsNullOrWhiteSpace(path))
                     .Distinct(PathComparer))
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TrackMissingDirectories(
        string destinationDirectory,
        ISet<string> missingDirectories)
    {
        for (var current = Path.GetFullPath(destinationDirectory);
             !Directory.Exists(current);
             current = Path.GetDirectoryName(current) ?? string.Empty)
        {
            if (current.Length == 0)
            {
                break;
            }
            missingDirectories.Add(current);
        }
    }

    private static void RemoveEmptyDirectories(IEnumerable<string> directories)
    {
        foreach (var directory in directories.OrderByDescending(static path => path.Length))
        {
            try
            {
                if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
                {
                    Directory.Delete(directory);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static bool HasPrefix(IReadOnlyList<string> path, IReadOnlyList<string> prefix) =>
        path.Count >= prefix.Count && prefix.Select((segment, index) =>
            string.Equals(segment, path[index], StringComparison.Ordinal)).All(static matches => matches);

    private static bool PathsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right) =>
        left.SequenceEqual(right, StringComparer.Ordinal);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsUnderRoot(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !string.Equals(relative, "..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static void ValidateHash(string path, string expectedHash, string content)
    {
        if (!string.Equals(expectedHash, ComputeContentHash(content), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Cannot apply module identity rename because '{path}' changed after the plan was created.");
        }
    }

    private static string ResolveManifestPath(string inputPath)
    {
        var path = Path.GetFullPath(string.IsNullOrWhiteSpace(inputPath) ? "." : inputPath);
        if (Directory.Exists(path))
        {
            path = Path.Combine(path, EidosProjectConfigurationLoader.DefaultFileName);
        }
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"Project manifest not found: {path}");
        }
        return path;
    }

    private static IReadOnlyDictionary<string, string> NormalizeDocumentOverrides(
        IReadOnlyDictionary<string, string>? overrides)
    {
        var normalized = new Dictionary<string, string>(PathComparer);
        if (overrides != null)
        {
            foreach (var (path, text) in overrides)
            {
                normalized[Path.GetFullPath(path)] = text;
            }
        }
        return normalized;
    }

    private static string ReadDocumentText(string path, IReadOnlyDictionary<string, string> overrides)
    {
        var fullPath = Path.GetFullPath(path);
        return overrides.TryGetValue(fullPath, out var text) ? text : File.ReadAllText(fullPath);
    }

    private static string TokenText(string source, Token token) =>
        source.Substring(token.Location.Position, token.Length);

    private static string FormatDiagnostic(string sourcePath, Diagnostic.Diagnostic diagnostic)
    {
        var label = diagnostic.Labels.FirstOrDefault();
        return label == null
            ? $"{sourcePath}: {diagnostic.Code} {diagnostic.Message}"
            : $"{sourcePath}({label.Span.Location.Line + 1},{label.Span.Location.Column + 1}): {diagnostic.Code} {diagnostic.Message}";
    }

    private static string ComputeContentHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static StringComparer PathComparer =>
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
}
