using Eidosc.ProjectSystem;

namespace Eidosc.Cli.Lsp;

/// <summary>
/// Projects manifest naming diagnostics into the same LSP diagnostic shape used
/// by source documents.  Manifest parsing deliberately stays in the project
/// system; this adapter only adds document ranges and editor metadata.
/// </summary>
internal static class LspManifestNamingMapper
{
    public static List<LspDiagnostic> Map(
        string text,
        string manifestPath)
    {
        EidosProjectManifestDocument manifest;
        try
        {
            manifest = EidosProjectManifestDocument.Parse(text, manifestPath);
        }
        catch (Exception ex) when (IsManifestParseException(ex))
        {
            // TOML syntax diagnostics are produced by the project loader. Do
            // not hide them behind a style-only adapter.
            return [];
        }

        var projectDirectory = Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory();
        var diagnostics = new List<LspDiagnostic>();
        foreach (var diagnostic in ManifestNamingRules.Analyze(manifest, projectDirectory))
        {
            var offset = FindSubjectOffset(text, diagnostic.Subject);
            diagnostics.Add(new LspDiagnostic
            {
                Range = CreateRange(text, offset, Math.Max(1, diagnostic.Subject.Length)),
                Severity = LspDiagnosticSeverity.Warning,
                Code = diagnostic.Code,
                Source = "eidosc",
                Message = diagnostic.Message,
                Data = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["style"] = "naming",
                    ["naming.category"] = "manifest",
                    ["naming.subject"] = diagnostic.Subject,
                    ["naming.expected"] = diagnostic.SuggestedName ?? string.Empty
                }
            });
        }

        return diagnostics;
    }

    public static List<LspCodeAction> MapCodeActions(
        string text,
        string manifestPath,
        string uri,
        LspRange requestedRange,
        IReadOnlyDictionary<string, string>? documentOverrides = null)
    {
        EidosProjectManifestDocument manifest;
        try
        {
            manifest = EidosProjectManifestDocument.Parse(text, manifestPath);
        }
        catch (Exception ex) when (IsManifestParseException(ex))
        {
            return [];
        }

        var projectDirectory = Path.GetDirectoryName(manifestPath) ?? Directory.GetCurrentDirectory();
        var actions = new List<LspCodeAction>();
        foreach (var diagnostic in ManifestNamingRules.Analyze(manifest, projectDirectory))
        {
            if (string.IsNullOrWhiteSpace(diagnostic.SuggestedName))
            {
                continue;
            }

            var offset = FindSubjectOffset(text, diagnostic.Subject);
            var range = CreateRange(text, offset, Math.Max(1, diagnostic.Subject.Length));
            if (!RangesIntersect(range, requestedRange))
            {
                continue;
            }

            var semanticWorkspaceEdit = TryCreateSemanticWorkspaceEdit(
                diagnostic,
                text,
                manifestPath,
                uri,
                documentOverrides);
            if (diagnostic.Code is "S1105" or "S1110" &&
                File.Exists(manifestPath))
            {
                semanticWorkspaceEdit ??= TryCreateModuleIdentityWorkspaceEdit(
                    manifestText: text,
                    manifestPath,
                    manifestUri: uri,
                    documentOverrides);
            }
            if (diagnostic.Code is "S1105" or "S1108" or "S1110" &&
                File.Exists(manifestPath) &&
                semanticWorkspaceEdit == null)
            {
                // A dependency alias is a package identity. Never offer a
                // manifest-only fix when the source graph could not be
                // planned losslessly.
                continue;
            }

            actions.Add(new LspCodeAction
            {
                Title = $"Rename {diagnostic.Subject} to {diagnostic.SuggestedName}",
                Kind = "quickfix",
                IsPreferred = true,
                Edit = semanticWorkspaceEdit ?? new LspWorkspaceEdit
                    {
                        Changes = new Dictionary<string, List<LspTextEdit>>(StringComparer.Ordinal)
                        {
                            [uri] =
                            [
                                new LspTextEdit
                                {
                                    Range = range,
                                    NewText = diagnostic.SuggestedName
                                }
                            ]
                        }
                    }
            });
        }

        return actions;
    }

    private static LspWorkspaceEdit? TryCreateModuleIdentityWorkspaceEdit(
        string manifestText,
        string manifestPath,
        string manifestUri,
        IReadOnlyDictionary<string, string>? documentOverrides)
    {
        ModuleIdentityRenamePlan plan;
        try
        {
            var comparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var overrides = documentOverrides == null
                ? new Dictionary<string, string>(comparer)
                : new Dictionary<string, string>(documentOverrides, comparer);
            overrides[Path.GetFullPath(manifestPath)] = manifestText;
            plan = ModuleIdentityRenamePlanner.CreatePlan(
                manifestPath,
                includePathDependencies: true,
                documentOverrides: overrides);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!plan.CanApply || plan.TotalMoveCount == 0)
        {
            return null;
        }

        var documentChanges = new List<object>();
        foreach (var package in plan.Packages)
        {
            AddFileEdits(package.Manifest, manifestText, manifestUri, documentChanges);
            foreach (var source in package.Sources)
            {
                AddFileEdits(
                    source,
                    ReadDocumentText(source.FilePath, documentOverrides),
                    ToFileUri(source.FilePath),
                    documentChanges);
            }

            foreach (var move in package.Moves)
            {
                documentChanges.Add(new LspRenameFile
                {
                    OldUri = ToFileUri(move.SourcePath),
                    NewUri = ToFileUri(move.DestinationPath)
                });
            }
        }

        return new LspWorkspaceEdit
        {
            DocumentChanges = documentChanges
        };

        static void AddFileEdits(
            DependencyAliasRenameFilePlan filePlan,
            string text,
            string uri,
            List<object> documentChanges)
        {
            if (filePlan.Edits.Length == 0)
            {
                return;
            }

            documentChanges.Add(new LspTextDocumentEdit
            {
                TextDocument = new LspVersionedTextDocumentIdentifier
                {
                    Uri = uri,
                    Version = null
                },
                Edits = filePlan.Edits.Select(edit => new LspTextEdit
                {
                    Range = CreateRange(text, edit.Start, edit.Length),
                    NewText = edit.Replacement
                }).ToList()
            });
        }

        static string ToFileUri(string path) => new Uri(Path.GetFullPath(path)).AbsoluteUri;

        static string ReadDocumentText(
            string path,
            IReadOnlyDictionary<string, string>? overrides)
        {
            var fullPath = Path.GetFullPath(path);
            if (overrides != null)
            {
                foreach (var (overridePath, overrideText) in overrides)
                {
                    if (string.Equals(
                            Path.GetFullPath(overridePath),
                            fullPath,
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal))
                    {
                        return overrideText;
                    }
                }
            }
            return File.ReadAllText(fullPath);
        }
    }

    private static LspWorkspaceEdit? TryCreateSemanticWorkspaceEdit(
        ManifestNamingRules.Diagnostic diagnostic,
        string manifestText,
        string manifestPath,
        string manifestUri,
        IReadOnlyDictionary<string, string>? documentOverrides)
    {
        if (!string.Equals(diagnostic.Code, "S1108", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(diagnostic.SuggestedName) ||
            !File.Exists(manifestPath))
        {
            return null;
        }

        DependencyAliasRenamePlan plan;
        try
        {
            var pathComparer = OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
            var plannerOverrides = documentOverrides == null
                ? new Dictionary<string, string>(pathComparer)
                : new Dictionary<string, string>(documentOverrides, pathComparer);
            // The manifest text supplied by LSP is authoritative for this
            // request, even when the editor has not flushed it to disk yet.
            plannerOverrides[Path.GetFullPath(manifestPath)] = manifestText;
            plan = DependencyAliasRenamePlanner.CreatePlan(
                manifestPath,
                diagnostic.Subject,
                diagnostic.SuggestedName,
                includePathDependencies: true,
                documentOverrides: plannerOverrides);
        }
        catch (Exception ex) when (
            ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        if (!plan.CanApply)
        {
            return null;
        }

        var changes = new Dictionary<string, List<LspTextEdit>>(StringComparer.Ordinal);
        foreach (var package in plan.Packages)
        {
            AddFileEdits(package.Manifest, manifestPath, manifestText, manifestUri, changes);
            foreach (var source in package.Sources)
            {
                if (source.Edits.Length == 0)
                {
                    continue;
                }

                AddFileEdits(
                    source,
                    source.FilePath,
                    ReadDocumentText(source.FilePath, documentOverrides),
                    ToFileUri(source.FilePath),
                    changes);
            }
        }

        return changes.Count == 0 ? null : new LspWorkspaceEdit { Changes = changes };

        static void AddFileEdits(
            DependencyAliasRenameFilePlan filePlan,
            string filePath,
            string text,
            string uri,
            Dictionary<string, List<LspTextEdit>> changes)
        {
            if (filePlan.Edits.Length == 0)
            {
                return;
            }

            if (!changes.TryGetValue(uri, out var edits))
            {
                edits = [];
                changes[uri] = edits;
            }

            foreach (var edit in filePlan.Edits)
            {
                edits.Add(new LspTextEdit
                {
                    Range = CreateRange(text, edit.Start, edit.Length),
                    NewText = edit.Replacement
                });
            }
        }

        static string ToFileUri(string path) =>
            new Uri(Path.GetFullPath(path)).AbsoluteUri;

        static string ReadDocumentText(
            string path,
            IReadOnlyDictionary<string, string>? overrides)
        {
            var fullPath = Path.GetFullPath(path);
            if (overrides != null)
            {
                foreach (var (overridePath, overrideText) in overrides)
                {
                    if (string.Equals(
                            Path.GetFullPath(overridePath),
                            fullPath,
                            OperatingSystem.IsWindows()
                                ? StringComparison.OrdinalIgnoreCase
                                : StringComparison.Ordinal))
                    {
                        return overrideText;
                    }
                }
            }

            return File.ReadAllText(fullPath);
        }
    }

    private static int FindSubjectOffset(string text, string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
        {
            return 0;
        }

        var quoted = text.IndexOf($"\"{subject}\"", StringComparison.Ordinal);
        if (quoted >= 0)
        {
            return quoted + 1;
        }

        var bare = text.IndexOf(subject, StringComparison.Ordinal);
        return bare >= 0 ? bare : 0;
    }

    private static bool IsManifestParseException(Exception exception) =>
        exception is InvalidOperationException ||
        string.Equals(exception.GetType().Name, "TomlException", StringComparison.Ordinal);

    private static LspRange CreateRange(string text, int offset, int length)
    {
        offset = Math.Clamp(offset, 0, text.Length);
        length = Math.Clamp(length, 1, Math.Max(1, text.Length - offset));
        return new LspRange
        {
            Start = ToPosition(text, offset),
            End = ToPosition(text, offset + length)
        };
    }

    private static LspPosition ToPosition(string text, int offset)
    {
        var line = 0;
        var lineStart = 0;
        for (var index = 0; index < offset; index++)
        {
            if (text[index] == '\n')
            {
                line++;
                lineStart = index + 1;
            }
        }

        return new LspPosition
        {
            Line = line,
            Character = offset - lineStart
        };
    }

    private static bool RangesIntersect(LspRange left, LspRange right)
    {
        return Compare(left.Start, right.End) <= 0 && Compare(right.Start, left.End) <= 0;
    }

    private static int Compare(LspPosition left, LspPosition right)
    {
        var line = left.Line.CompareTo(right.Line);
        return line != 0 ? line : left.Character.CompareTo(right.Character);
    }
}
