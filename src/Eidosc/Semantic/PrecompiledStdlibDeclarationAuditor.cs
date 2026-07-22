using Eidosc.Ast.Declarations;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed record PrecompiledStdlibDeclarationAuditIssue(
    string ModulePath,
    string FunctionName,
    string Message);

public static class PrecompiledStdlibDeclarationAuditor
{
    public static IReadOnlyList<PrecompiledStdlibDeclarationAuditIssue> AuditEmbeddedStdlib()
    {
        var issues = new List<PrecompiledStdlibDeclarationAuditIssue>();
        foreach (var modulePath in PrecompiledModuleRegistry.GetAvailableModulePaths())
        {
            if (!PrecompiledModuleRegistry.TryGetSource(modulePath, out var source))
            {
                continue;
            }

            issues.AddRange(AuditSourceForTest(source, modulePath));
        }

        return issues;
    }

    internal static IReadOnlyList<PrecompiledStdlibDeclarationAuditIssue> AuditSourceForTest(
        string source,
        string modulePath)
    {
        if (!PrecompiledModuleRegistry.TryParseModuleDeclForTest(source, modulePath, out var moduleDecl) ||
            moduleDecl == null)
        {
            return
            [
                new PrecompiledStdlibDeclarationAuditIssue(
                    modulePath,
                    "",
                    "precompiled stdlib source must parse before declaration auditing")
            ];
        }

        var issues = new List<PrecompiledStdlibDeclarationAuditIssue>();
        AuditModule(moduleDecl, modulePath, issues);
        AuditCompilerImplementedHelperUses(source, moduleDecl, modulePath, issues);
        return issues;
    }

    private static void AuditModule(
        ModuleDecl module,
        string modulePath,
        List<PrecompiledStdlibDeclarationAuditIssue> issues)
    {
        foreach (var declaration in module.Declarations)
        {
            switch (declaration)
            {
                case ModuleDecl nested:
                    AuditModule(nested, modulePath, issues);
                    break;

                case FuncDecl funcDecl:
                    AuditTopLevelFuncDecl(funcDecl, modulePath, issues);
                    break;

                case FuncDef funcDef:
                    AuditTopLevelFuncDef(funcDef, modulePath, issues);
                    break;
            }
        }
    }

    private static void AuditTopLevelFuncDecl(
        FuncDecl funcDecl,
        string modulePath,
        List<PrecompiledStdlibDeclarationAuditIssue> issues)
    {
        if (!HasImplementationClause(funcDecl))
        {
            issues.Add(new PrecompiledStdlibDeclarationAuditIssue(
                modulePath,
                funcDecl.Name,
                "bodyless top-level precompiled std function declarations must declare their implementation with extern or intrinsic"));
        }
    }

    private static void AuditTopLevelFuncDef(
        FuncDef funcDef,
        string modulePath,
        List<PrecompiledStdlibDeclarationAuditIssue> issues)
    {
        if (funcDef.Body.Count == 0 && !HasImplementationClause(funcDef))
        {
            issues.Add(new PrecompiledStdlibDeclarationAuditIssue(
                modulePath,
                funcDef.Name,
                "bodyless top-level precompiled std function definitions must declare their implementation with extern or intrinsic"));
            return;
        }

        if (HasClause(funcDef, DeclarationClauseKind.Extern) && funcDef.Body.Count > 0)
        {
            issues.Add(new PrecompiledStdlibDeclarationAuditIssue(
                modulePath,
                funcDef.Name,
                "extern precompiled std declarations must not provide an Eidos function body"));
        }

        if (CompilerDirectiveIR.FromDeclaration(funcDef) is { Intrinsic: not null } && funcDef.Body.Count > 0)
        {
            issues.Add(new PrecompiledStdlibDeclarationAuditIssue(
                modulePath,
                funcDef.Name,
                "intrinsic precompiled std declarations must not provide an Eidos function body"));
        }
    }

    private static bool HasImplementationClause(Declaration declaration)
    {
        return HasClause(declaration, DeclarationClauseKind.Extern) ||
               CompilerDirectiveIR.FromDeclaration(declaration) is { Intrinsic: not null };
    }

    private static bool HasClause(Declaration declaration, DeclarationClauseKind kind) =>
        declaration.Clauses.Any(clause => clause.ClauseKind == kind);

    private static void AuditCompilerImplementedHelperUses(
        string source,
        ModuleDecl moduleDecl,
        string modulePath,
        List<PrecompiledStdlibDeclarationAuditIssue> issues)
    {
        var declaredHelpers = new HashSet<string>(StringComparer.Ordinal);
        CollectDeclaredCompilerImplementedHelpers(moduleDecl, declaredHelpers);

        foreach (var helperName in IntrinsicRegistry.EmbeddedStdlibDeclarations.Keys)
        {
            if (declaredHelpers.Contains(helperName) || !ContainsIdentifier(source, helperName))
            {
                continue;
            }

            issues.Add(new PrecompiledStdlibDeclarationAuditIssue(
                modulePath,
                helperName,
                "precompiled std uses compiler-implemented helper without a local extern or intrinsic declaration"));
        }

        foreach (var mathName in EnumerateMathHelperUses(source))
        {
            if (declaredHelpers.Contains(mathName))
            {
                continue;
            }

            issues.Add(new PrecompiledStdlibDeclarationAuditIssue(
                modulePath,
                mathName,
                "precompiled std uses compiler-implemented math helper without a local intrinsic declaration"));
        }
    }

    private static void CollectDeclaredCompilerImplementedHelpers(ModuleDecl module, HashSet<string> declaredHelpers)
    {
        foreach (var declaration in module.Declarations)
        {
            switch (declaration)
            {
                case ModuleDecl nested:
                    CollectDeclaredCompilerImplementedHelpers(nested, declaredHelpers);
                    break;

                case FuncDecl funcDecl when HasImplementationClause(funcDecl):
                    AddDeclaredHelperNames(funcDecl, declaredHelpers);
                    break;

                case FuncDef funcDef when HasImplementationClause(funcDef):
                    AddDeclaredHelperNames(funcDef, declaredHelpers);
                    break;
            }
        }
    }

    private static void AddDeclaredHelperNames(Declaration declaration, HashSet<string> declaredHelpers)
    {
        switch (declaration)
        {
            case FuncDecl funcDecl:
                declaredHelpers.Add(funcDecl.Name);
                break;

            case FuncDef funcDef:
                declaredHelpers.Add(funcDef.Name);
                break;
        }

        if (CompilerDirectiveIR.FromDeclaration(declaration) is { Intrinsic: { } intrinsic })
        {
            var helperName = TrimStringLiteralQuotes(intrinsic);
            if (!string.IsNullOrWhiteSpace(helperName))
            {
                declaredHelpers.Add(helperName);
            }
        }
    }

    private static string TrimStringLiteralQuotes(string text)
    {
        if (text.Length >= 2 &&
            text[0] == '"' &&
            text[^1] == '"')
        {
            return text[1..^1];
        }

        return text;
    }

    private static IEnumerable<string> EnumerateMathHelperUses(string source)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < source.Length;)
        {
            var found = source.IndexOf("math_", index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            if (!IsIdentifierBoundary(source, found - 1))
            {
                index = found + "math_".Length;
                continue;
            }

            var end = found + "math_".Length;
            while (end < source.Length && IsIdentifierPart(source[end]))
            {
                end++;
            }

            if (end > found + "math_".Length &&
                IsIdentifierBoundary(source, end))
            {
                result.Add(source[found..end]);
            }

            index = end;
        }

        return result;
    }

    private static bool ContainsIdentifier(string source, string name)
    {
        for (var index = 0; index < source.Length;)
        {
            var found = source.IndexOf(name, index, StringComparison.Ordinal);
            if (found < 0)
            {
                return false;
            }

            if (IsIdentifierBoundary(source, found - 1) &&
                IsIdentifierBoundary(source, found + name.Length))
            {
                return true;
            }

            index = found + name.Length;
        }

        return false;
    }

    private static bool IsIdentifierBoundary(string source, int index)
    {
        return index < 0 ||
               index >= source.Length ||
               !IsIdentifierPart(source[index]);
    }

    private static bool IsIdentifierPart(char ch)
    {
        return char.IsLetterOrDigit(ch) || ch == '_';
    }
}
