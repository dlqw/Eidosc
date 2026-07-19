using Eidosc.Ast.Declarations;
using Eidosc.Diagnostic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Semantic;

internal sealed record FfiBindingInfo(string SymbolName, string? LibraryName);

internal sealed record IntrinsicBindingInfo(string Name, IReadOnlyList<string> Effects);

internal sealed record ClauseSemanticDiagnostic(SourceSpan Span, string Message);

internal sealed record DeclarationClauseSemanticBindingResult(
    FfiBindingInfo? Ffi,
    IntrinsicBindingInfo? Intrinsic,
    IReadOnlyList<string> Effects,
    IReadOnlyList<ClauseSemanticDiagnostic> Diagnostics);

internal sealed class DeclarationClauseSemanticBinder
{
    public DeclarationClauseSemanticBindingResult Bind(Declaration declaration, string declarationName)
    {
        var diagnostics = new List<ClauseSemanticDiagnostic>();
        var effects = declaration.Clauses
            .Where(static clause => clause.ClauseKind == DeclarationClauseKind.Need)
            .SelectMany(static clause => clause.ArgumentTokens)
            .Select(static argument => argument.Trim())
            .Where(static argument => !string.IsNullOrWhiteSpace(argument))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        FfiBindingInfo? ffi = null;
        if (ForeignContractIR.FromDeclaration(declaration) is { } foreignContract)
        {
            ffi = new FfiBindingInfo(
                foreignContract.Name ?? declarationName,
                foreignContract.Library);
        }

        IntrinsicBindingInfo? intrinsic = null;
        foreach (var clause in declaration.Clauses)
        {
            if (clause.ClauseKind == DeclarationClauseKind.Intrinsic)
            {
                var intrinsicName = clause.ArgumentTokens
                    .Select(static argument => NormalizeClauseArgumentText(argument))
                    .FirstOrDefault(static argument => !string.IsNullOrWhiteSpace(argument)) ?? declarationName;
                intrinsic = new IntrinsicBindingInfo(intrinsicName, effects);
            }
        }

        return new DeclarationClauseSemanticBindingResult(ffi, intrinsic, effects, diagnostics);
    }

    private static string NormalizeClauseArgumentText(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            return trimmed[1..^1];
        }

        return trimmed;
    }
}
