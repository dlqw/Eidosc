using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Utils;
using EidosAttribute = Eidosc.Ast.Attribute;

namespace Eidosc.Semantic;

internal sealed record FfiBindingInfo(string SymbolName, string? LibraryName);

internal sealed record IntrinsicBindingInfo(string Name, IReadOnlyList<string> Effects);

internal sealed record OperatorBindingInfo(
    CustomOperatorFixity Fixity,
    int Precedence,
    SourceSpan Span);

internal sealed record AttributeBindingDiagnostic(SourceSpan Span, string Message);

internal sealed record AttributeBindingResult(
    FfiBindingInfo? Ffi,
    IntrinsicBindingInfo? Intrinsic,
    IReadOnlyList<OperatorBindingInfo> Operators,
    IReadOnlyList<string> Effects,
    IReadOnlyList<AttributeBindingDiagnostic> Diagnostics);

internal sealed class AttributeBinder
{
    public AttributeBindingResult BindDeclarationAttributes(Declaration declaration, string declarationName)
    {
        var diagnostics = new List<AttributeBindingDiagnostic>();
        var operators = new List<OperatorBindingInfo>();
        var effects = new List<string>();
        FfiBindingInfo? ffi = null;
        IntrinsicBindingInfo? intrinsic = null;

        foreach (var attribute in declaration.Attributes)
        {
            switch (attribute.Name)
            {
                case WellKnownStrings.Keywords.Ffi:
                    ffi ??= BindFfiAttribute(attribute, declarationName);
                    break;
                case WellKnownStrings.SpecialNames.Intrinsic:
                    intrinsic ??= BindIntrinsicAttribute(attribute, declarationName, declaration.Attributes);
                    break;
                case WellKnownStrings.SpecialNames.Effects:
                    AddEffects(attribute, effects);
                    break;
                case WellKnownStrings.SpecialNames.LlvmAbi:
                    break;
                case "operator":
                    if (BindOperatorAttribute(attribute, diagnostics) is { } operatorInfo)
                    {
                        operators.Add(operatorInfo);
                    }
                    break;
            }
        }

        return new AttributeBindingResult(ffi, intrinsic, operators, effects, diagnostics);
    }

    private static FfiBindingInfo BindFfiAttribute(EidosAttribute attribute, string functionName)
    {
        if (!TryGetStringAttributeArgument(attribute, out var raw))
        {
            return new FfiBindingInfo(functionName, null);
        }

        var slashIndex = raw.IndexOf('/');
        if (slashIndex < 0)
        {
            return new FfiBindingInfo(raw, null);
        }

        var library = raw[..slashIndex];
        var symbol = raw[(slashIndex + 1)..];
        return new FfiBindingInfo(
            string.IsNullOrEmpty(symbol) ? functionName : symbol,
            library);
    }

    private static IntrinsicBindingInfo BindIntrinsicAttribute(
        EidosAttribute attribute,
        string functionName,
        IReadOnlyList<EidosAttribute> attributes)
    {
        var name = TryGetStringAttributeArgument(attribute, out var raw) ? raw : functionName;
        return new IntrinsicBindingInfo(name, CollectEffects(attributes));
    }

    private static IReadOnlyList<string> CollectEffects(IReadOnlyList<EidosAttribute> attributes)
    {
        var effects = new List<string>();
        foreach (var attribute in attributes)
        {
            if (string.Equals(attribute.Name, WellKnownStrings.SpecialNames.Effects, StringComparison.Ordinal))
            {
                AddEffects(attribute, effects);
            }
        }

        return effects;
    }

    private static void AddEffects(EidosAttribute attribute, List<string> effects)
    {
        foreach (var raw in attribute.ArgumentTexts)
        {
            foreach (var effect in SplitEffectText(raw))
            {
                if (!effects.Contains(effect, StringComparer.Ordinal))
                {
                    effects.Add(effect);
                }
            }
        }
    }

    private static IEnumerable<string> SplitEffectText(string raw)
    {
        var normalized = raw
            .Replace("[", "", StringComparison.Ordinal)
            .Replace("]", "", StringComparison.Ordinal)
            .Replace("{", "", StringComparison.Ordinal)
            .Replace("}", "", StringComparison.Ordinal);

        return normalized.Split([',', '|', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static OperatorBindingInfo? BindOperatorAttribute(
        EidosAttribute attribute,
        List<AttributeBindingDiagnostic> diagnostics)
    {
        if (attribute.ArgumentTexts.Count < 2)
        {
            return null;
        }

        var fixityText = attribute.ArgumentTexts[0].Trim();
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
            diagnostics.Add(new AttributeBindingDiagnostic(
                attribute.Span,
                DiagnosticMessages.OperatorUnsupportedFixity(fixityText)));
            return null;
        }

        if (!int.TryParse(attribute.ArgumentTexts[1].Trim(), out var precedence))
        {
            diagnostics.Add(new AttributeBindingDiagnostic(
                attribute.Span,
                DiagnosticMessages.OperatorPrecedenceMustBeInteger(attribute.ArgumentTexts[1])));
            return null;
        }

        if (precedence is < 0 or > 9)
        {
            diagnostics.Add(new AttributeBindingDiagnostic(
                attribute.Span,
                DiagnosticMessages.OperatorPrecedenceOutOfRange(precedence)));
            return null;
        }

        return new OperatorBindingInfo(fixity.Value, precedence, attribute.Span);
    }

    private static bool TryGetStringAttributeArgument(EidosAttribute attr, out string raw)
    {
        raw = string.Empty;
        if (attr.Arguments.Count > 0 && attr.Arguments[0] is LiteralExpr literal)
        {
            raw = literal.Value?.ToString() ?? string.Empty;
        }
        else if (attr.ArgumentTexts.Count > 0)
        {
            raw = NormalizeAttributeArgumentText(attr.ArgumentTexts[0]);
        }

        return !string.IsNullOrWhiteSpace(raw);
    }

    private static string NormalizeAttributeArgumentText(string text)
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
