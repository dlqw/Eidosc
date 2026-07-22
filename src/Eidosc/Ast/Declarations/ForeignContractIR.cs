using Eidosc.Utils;

namespace Eidosc.Ast.Declarations;

public sealed record ForeignContractIR(
    string Abi,
    string? Library,
    string? Name,
    SourceSpan Span)
{
    public static bool TryCreate(
        DeclarationClause clause,
        out ForeignContractIR contract,
        out IReadOnlyList<string> errors)
    {
        var diagnostics = new List<string>();
        var abi = clause.ArgumentTokens.FirstOrDefault()?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(abi) || abi.Contains(':', StringComparison.Ordinal))
        {
            diagnostics.Add("extern requires an ABI as its first positional argument");
        }

        string? library = null;
        string? name = null;
        var labels = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in clause.ArgumentTokens.Skip(1))
        {
            var separator = raw.IndexOf(':');
            if (separator <= 0)
            {
                diagnostics.Add($"extern argument '{raw}' must use a named field");
                continue;
            }

            var label = raw[..separator].Trim();
            var value = raw[(separator + 1)..].Trim();
            if (!labels.Add(label))
            {
                diagnostics.Add($"extern field '{label}' cannot be repeated");
                continue;
            }

            switch (label)
            {
                case "library":
                    if (string.IsNullOrWhiteSpace(value))
                    {
                        diagnostics.Add("extern field 'library' requires a value");
                    }
                    else
                    {
                        library = NormalizeValue(value);
                    }
                    break;
                case "name":
                    if (!IsQuoted(value))
                    {
                        diagnostics.Add("extern field 'name' requires a string literal");
                    }
                    else
                    {
                        name = NormalizeValue(value);
                    }
                    break;
                default:
                    diagnostics.Add($"unknown extern field '{label}'");
                    break;
            }
        }

        contract = new ForeignContractIR(abi, library, name, clause.Span);
        errors = diagnostics;
        return diagnostics.Count == 0;
    }

    public static ForeignContractIR? FromDeclaration(Declaration declaration)
    {
        var clause = declaration.Clauses.FirstOrDefault(static clause =>
            clause.ClauseKind == DeclarationClauseKind.Extern);
        return clause != null && TryCreate(clause, out var contract, out _)
            ? contract
            : null;
    }

    private static bool IsQuoted(string value) =>
        value.Length >= 2 &&
        ((value[0] == '"' && value[^1] == '"') ||
         (value[0] == '\'' && value[^1] == '\''));

    private static string NormalizeValue(string value) => IsQuoted(value) ? value[1..^1] : value;
}
