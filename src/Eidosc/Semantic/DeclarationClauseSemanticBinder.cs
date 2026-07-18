using Eidosc.Ast.Declarations;
using Eidosc.Diagnostic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Semantic;

internal sealed record FfiBindingInfo(string SymbolName, string? LibraryName);

internal sealed record IntrinsicBindingInfo(string Name, IReadOnlyList<string> Effects);

internal sealed record OperatorBindingInfo(
    CustomOperatorFixity Fixity,
    int Precedence,
    SourceSpan Span);

internal sealed record ClauseSemanticDiagnostic(SourceSpan Span, string Message);

internal sealed record DeclarationClauseSemanticBindingResult(
    FfiBindingInfo? Ffi,
    IntrinsicBindingInfo? Intrinsic,
    IReadOnlyList<OperatorBindingInfo> Operators,
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
        if (declaration.Clauses.Any(static clause => clause.ClauseKind == DeclarationClauseKind.Extern))
        {
            ffi = new FfiBindingInfo(
                GetClauseArgument(declaration, DeclarationClauseKind.LinkName) ?? declarationName,
                GetClauseArgument(declaration, DeclarationClauseKind.LinkLibrary));
        }

        IntrinsicBindingInfo? intrinsic = null;
        var operators = new List<OperatorBindingInfo>();
        foreach (var clause in declaration.Clauses)
        {
            if (clause.ClauseKind == DeclarationClauseKind.Operator)
            {
                if (BindOperatorClause(clause, diagnostics) is { } operatorInfo)
                {
                    operators.Add(operatorInfo);
                }
            }
            else if (clause.ClauseKind == DeclarationClauseKind.Intrinsic)
            {
                var intrinsicName = clause.ArgumentTokens
                    .Select(static argument => NormalizeClauseArgumentText(argument))
                    .FirstOrDefault(static argument => !string.IsNullOrWhiteSpace(argument)) ?? declarationName;
                intrinsic = new IntrinsicBindingInfo(intrinsicName, effects);
            }
        }

        return new DeclarationClauseSemanticBindingResult(ffi, intrinsic, operators, effects, diagnostics);
    }

    private static string? GetClauseArgument(Declaration declaration, DeclarationClauseKind kind)
    {
        return declaration.Clauses
            .Where(clause => clause.ClauseKind == kind)
            .SelectMany(static clause => clause.ArgumentTokens)
            .Select(static argument => NormalizeClauseArgumentText(argument))
            .FirstOrDefault(static argument => !string.IsNullOrWhiteSpace(argument));
    }

    private static OperatorBindingInfo? BindOperatorClause(
        DeclarationClause clause,
        List<ClauseSemanticDiagnostic> diagnostics)
    {
        if (clause.ArgumentTokens.Count < 2)
        {
            return null;
        }

        var fixityText = clause.ArgumentTokens[0].Trim();
        var fixity = fixityText switch
        {
            "infixl" => CustomOperatorFixity.InfixL,
            "infixr" => CustomOperatorFixity.InfixR,
            "prefix" => CustomOperatorFixity.Prefix,
            "postfix" => CustomOperatorFixity.Postfix,
            _ => (CustomOperatorFixity?)null
        };

        if (fixity == null)
        {
            diagnostics.Add(new ClauseSemanticDiagnostic(
                clause.Span,
                DiagnosticMessages.OperatorUnsupportedFixity(fixityText)));
            return null;
        }

        if (!int.TryParse(clause.ArgumentTokens[1].Trim(), out var precedence))
        {
            diagnostics.Add(new ClauseSemanticDiagnostic(
                clause.Span,
                DiagnosticMessages.OperatorPrecedenceMustBeInteger(clause.ArgumentTokens[1])));
            return null;
        }

        if (precedence is < 0 or > 9)
        {
            diagnostics.Add(new ClauseSemanticDiagnostic(
                clause.Span,
                DiagnosticMessages.OperatorPrecedenceOutOfRange(precedence)));
            return null;
        }

        return new OperatorBindingInfo(fixity.Value, precedence, clause.Span);
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
