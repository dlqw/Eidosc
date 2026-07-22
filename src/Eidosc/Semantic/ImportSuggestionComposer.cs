using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Utils;

namespace Eidosc.Semantic;

internal static class ImportSuggestionComposer
{
    internal sealed record Suggestion(
        string Message,
        SourceSpan? Span,
        string? Replacement);

    public static Suggestion? TryCreateMemberSuggestion(
        ModuleDecl? module,
        string sourceText,
        SourceSpan referenceSpan,
        string modulePath,
        string name)
    {
        if (module == null)
        {
            return null;
        }

        if (FindExistingImport(module, modulePath, name) is { } existingImport)
        {
            if (existingImport.Kind == ImportKind.Wildcard ||
                ImportsLocalName(existingImport, name))
            {
                return null;
            }

            if (existingImport.Kind == ImportKind.Selective)
            {
                var mergedImportText = BuildMergedSelectiveImportText(existingImport, name);
                return new Suggestion(
                    $"Extend import '{mergedImportText}'",
                    existingImport.Span,
                    mergedImportText);
            }
        }

        var edit = TryCreateEditContext(module, sourceText, referenceSpan);
        const string importPrefix = "import ";
        var importPathText = FormatImportPath(modulePath);
        var importText = $"{importPrefix}{importPathText}::{{{name}}}";
        return new Suggestion(
            $"Add import '{importText}'",
            edit?.Span,
            edit == null ? null : edit.Indentation + importText + edit.LineBreak);
    }

    public static Suggestion? TryCreateModuleSuggestion(
        ModuleDecl? module,
        string sourceText,
        SourceSpan referenceSpan,
        string modulePath)
    {
        if (module == null)
        {
            return null;
        }

        var moduleLeafName = GetModuleLeafName(modulePath);
        if (string.IsNullOrWhiteSpace(moduleLeafName) ||
            FindExistingModuleImport(module, modulePath, moduleLeafName) != null)
        {
            return null;
        }

        var edit = TryCreateEditContext(module, sourceText, referenceSpan);
        var importText = $"import {FormatImportPath(modulePath)}";
        return new Suggestion(
            $"Add import '{importText}'",
            edit?.Span,
            edit == null ? null : edit.Indentation + importText + edit.LineBreak);
    }

    private sealed record EditContext(
        SourceSpan Span,
        string Indentation,
        string LineBreak);

    private static EditContext? TryCreateEditContext(
        ModuleDecl module,
        string sourceText,
        SourceSpan referenceSpan)
    {
        if (string.IsNullOrEmpty(sourceText))
        {
            return null;
        }

        var filePath = referenceSpan.FilePath ?? module.Span.FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        var declarations = module.Declarations
            .Where(static declaration => declaration is not ModuleDecl)
            .OrderBy(static declaration => declaration.Span.Location.Position)
            .ToList();
        if (declarations.Count == 0)
        {
            return null;
        }

        var imports = declarations.OfType<ImportDecl>().ToList();
        var (position, indentation) = imports.Count > 0
            ? (
                AdvanceToNextLineStart(sourceText, imports[^1].Span.EndPosition),
                GetLineIndentation(sourceText, imports[^1].Span.Location.Position))
            : (
                GetLineStart(sourceText, declarations[0].Span.Location.Position),
                GetLineIndentation(sourceText, declarations[0].Span.Location.Position));

        return new EditContext(
            CreateInsertionSpan(position, filePath),
            indentation,
            DetectPreferredLineBreak(sourceText));
    }

    private static ImportDecl? FindExistingImport(ModuleDecl module, string modulePath, string name)
    {
        var moduleKey = NormalizeImportModuleKey(modulePath);
        ImportDecl? fallbackSelectiveImport = null;
        foreach (var import in module.Declarations.OfType<ImportDecl>())
        {
            if (!string.Equals(GetImportModuleKey(import), moduleKey, StringComparison.Ordinal))
            {
                continue;
            }

            if (import.Kind == ImportKind.Wildcard || ImportsLocalName(import, name))
            {
                return import;
            }

            if (import.Kind == ImportKind.Selective)
            {
                fallbackSelectiveImport = import;
            }
        }

        return fallbackSelectiveImport;
    }

    private static ImportDecl? FindExistingModuleImport(ModuleDecl module, string modulePath, string localName)
    {
        var moduleKey = NormalizeImportModuleKey(modulePath);
        foreach (var import in module.Declarations.OfType<ImportDecl>())
        {
            if (!string.Equals(GetImportModuleKey(import), moduleKey, StringComparison.Ordinal) ||
                import.Kind != ImportKind.Module)
            {
                continue;
            }

            var importedLocalName = import.Alias ?? import.ModulePath[^1];
            if (string.Equals(importedLocalName, localName, StringComparison.Ordinal))
            {
                return import;
            }
        }

        return null;
    }

    private static bool ImportsLocalName(ImportDecl import, string name)
    {
        return import.Kind == ImportKind.Selective &&
               import.SelectiveImports.Any(item =>
                   string.Equals(item.Alias ?? item.Name, name, StringComparison.Ordinal));
    }

    private static string BuildMergedSelectiveImportText(ImportDecl import, string name)
    {
        var imports = new List<SelectiveImportNode>(import.SelectiveImports)
        {
            new() { Name = name }
        };

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var mergedItems = new List<string>(imports.Count);
        foreach (var item in imports)
        {
            var rendered = item.Alias != null
                ? $"{item.Name} as {item.Alias}"
                : item.Name;
            if (seen.Add(rendered))
            {
                mergedItems.Add(rendered);
            }
        }

        return $"import {FormatImportPath(import)}::{{{string.Join(", ", mergedItems)}}}";
    }

    private static string GetImportModuleKey(ImportDecl import)
    {
        return ModuleRegistry.ToModuleKey(import.PackageAlias, import.ModulePath);
    }

    private static string NormalizeImportModuleKey(string modulePath)
    {
        var normalized = modulePath.Replace('\\', '/');
        const string stdPrefix = "std/";
        if (!normalized.StartsWith(stdPrefix, StringComparison.Ordinal))
        {
            return normalized;
        }

        var moduleSegments = normalized[stdPrefix.Length..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return ModuleRegistry.ToModuleKey(WellKnownStrings.Std.Module, moduleSegments);
    }

    private static string FormatImportPath(ImportDecl import)
    {
        if (!string.IsNullOrWhiteSpace(import.PackageAlias))
        {
            return $"{import.PackageAlias}{WellKnownStrings.Separators.Path}{string.Join(WellKnownStrings.Operators.Divide, import.ModulePath)}";
        }

        return string.Join(WellKnownStrings.Operators.Divide, import.ModulePath);
    }

    private static string FormatImportPath(string modulePath)
    {
        var normalized = modulePath.Replace('\\', '/');
        const string stdPrefix = "std/";
        if (normalized.StartsWith(stdPrefix, StringComparison.Ordinal))
        {
            return $"std{WellKnownStrings.Separators.Path}{normalized[stdPrefix.Length..]}";
        }

        return normalized;
    }

    private static int AdvanceToNextLineStart(string sourceText, int position)
    {
        var clamped = Math.Clamp(position, 0, sourceText.Length);
        if (clamped == sourceText.Length)
        {
            return clamped;
        }

        if (IsAtLineStart(sourceText, clamped))
        {
            return clamped;
        }

        for (var index = clamped; index < sourceText.Length; index++)
        {
            if (sourceText[index] != '\n')
            {
                continue;
            }

            return index + 1;
        }

        return sourceText.Length;
    }

    private static int GetLineStart(string sourceText, int position)
    {
        var clamped = Math.Clamp(position, 0, sourceText.Length);
        while (clamped > 0 && sourceText[clamped - 1] != '\n')
        {
            clamped--;
        }

        return clamped;
    }

    private static string GetLineIndentation(string sourceText, int position)
    {
        var lineStart = GetLineStart(sourceText, position);
        var index = lineStart;
        while (index < sourceText.Length && (sourceText[index] == ' ' || sourceText[index] == '\t'))
        {
            index++;
        }

        return sourceText[lineStart..index];
    }

    private static bool IsAtLineStart(string sourceText, int position)
    {
        return position <= 0 ||
               position > sourceText.Length ||
               sourceText[position - 1] == '\n';
    }

    private static string DetectPreferredLineBreak(string sourceText)
    {
        _ = sourceText;
        return "\n";
    }

    private static SourceSpan CreateInsertionSpan(int position, string filePath)
    {
        return new SourceSpan(
            new SourceLocation(position, 1, 1, filePath),
            0);
    }

    private static string GetModuleLeafName(string modulePath)
    {
        if (string.IsNullOrWhiteSpace(modulePath))
        {
            return string.Empty;
        }

        var segments = modulePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length == 0 ? string.Empty : segments[^1];
    }
}
