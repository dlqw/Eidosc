using Eidosc.Utils;

namespace Eidosc.Ast.Declarations;

public sealed record CompilerDirectiveIR(
    bool IsInternal,
    string? Intrinsic,
    string? LlvmAbi,
    SourceSpan Span)
{
    public static bool TryCreate(
        IEnumerable<DeclarationClause> clauses,
        out CompilerDirectiveIR directive,
        out IReadOnlyList<string> errors)
    {
        var compilerClauses = clauses
            .Where(static clause => clause.ClauseKind == DeclarationClauseKind.Compiler)
            .ToArray();
        var diagnostics = new List<string>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var isInternal = false;
        string? intrinsic = null;
        string? llvmAbi = null;
        foreach (var clause in compilerClauses)
        {
            foreach (var raw in clause.ArgumentTokens)
            {
                var separator = raw.IndexOf(':');
                var label = (separator < 0 ? raw : raw[..separator]).Trim();
                var value = separator < 0 ? null : raw[(separator + 1)..].Trim();
                if (!labels.Add(label))
                {
                    diagnostics.Add($"compiler field '{label}' cannot be repeated");
                    continue;
                }

                switch (label)
                {
                    case "internal" when value == null:
                        isInternal = true;
                        break;
                    case "intrinsic" when IsQuoted(value):
                        intrinsic = NormalizeValue(value!);
                        break;
                    case "llvm_abi" when IsQuoted(value):
                        llvmAbi = NormalizeValue(value!);
                        break;
                    case "intrinsic" or "llvm_abi":
                        diagnostics.Add($"compiler field '{label}' requires a string literal");
                        break;
                    default:
                        diagnostics.Add($"unknown compiler field '{label}'");
                        break;
                }
            }
        }

        if (compilerClauses.Length > 0 && labels.Count == 0)
        {
            diagnostics.Add("compiler requires at least one directive field");
        }

        directive = new CompilerDirectiveIR(
            isInternal,
            intrinsic,
            llvmAbi,
            compilerClauses.FirstOrDefault()?.Span ?? SourceSpan.Empty);
        errors = diagnostics;
        return diagnostics.Count == 0;
    }

    public static CompilerDirectiveIR? FromDeclaration(Declaration declaration) => FromClauses(declaration.Clauses);

    public static CompilerDirectiveIR? FromClauses(IEnumerable<DeclarationClause> clauses)
    {
        var clauseArray = clauses.ToArray();
        return clauseArray.Any(static clause => clause.ClauseKind == DeclarationClauseKind.Compiler) &&
               TryCreate(clauseArray, out var directive, out _)
            ? directive
            : null;
    }

    private static bool IsQuoted(string? value) =>
        value is { Length: >= 2 } &&
        ((value[0] == '"' && value[^1] == '"') ||
         (value[0] == '\'' && value[^1] == '\''));

    private static string NormalizeValue(string value) => value[1..^1];
}
