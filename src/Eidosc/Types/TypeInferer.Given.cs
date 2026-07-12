using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private Type InferGiven(GivenExpr given)
    {
        if (given.Target == null)
        {
            return CreateMissingShapeRecoveryType(given.Span, DiagnosticMessages.CallExpressionMissingTarget);
        }

        if (!given.EvidenceSymbolId.IsValid ||
            _symbolTable.GetSymbol(given.EvidenceSymbolId) is not ImplSymbol evidence)
        {
            return SafeInferExpression(given.Target);
        }

        if (given.Target is CallExpr call &&
            TryGetGivenCallName(call.Function, out var callName))
        {
            if (TrySelectGivenImplementationMethod(evidence, callName, out var implementationMethod))
            {
                ApplyGivenImplementationMethod(call.Function, implementationMethod);
                return SafeInferExpression(call);
            }

            AddError(
                given.Span,
                $"Given evidence '{FormatGivenEvidencePath(given)}' does not implement callable '{callName}'.");
            return CreateErrorRecoveryType();
        }

        return SafeInferExpression(given.Target);
    }

    private bool TryGetGivenCallName(EidosAstNode? function, out string name)
    {
        name = function switch
        {
            IdentifierExpr identifier => identifier.Name,
            PathExpr path => path.Name,
            _ => ""
        };
        return !string.IsNullOrWhiteSpace(name);
    }

    private bool TrySelectGivenImplementationMethod(
        ImplSymbol evidence,
        string callName,
        out SymbolId implementationMethod)
    {
        implementationMethod = SymbolId.None;
        foreach (var (traitMethodId, methodId) in evidence.TraitMethodImplementations)
        {
            var traitMethod = _symbolTable.GetSymbol<FuncSymbol>(traitMethodId);
            var method = _symbolTable.GetSymbol<FuncSymbol>(methodId);
            if ((traitMethod != null && string.Equals(traitMethod.Name, callName, StringComparison.Ordinal)) ||
                (method != null && string.Equals(method.Name, callName, StringComparison.Ordinal)))
            {
                implementationMethod = methodId;
                return true;
            }
        }

        foreach (var methodId in evidence.Methods)
        {
            if (_symbolTable.GetSymbol<FuncSymbol>(methodId) is { } method &&
                string.Equals(method.Name, callName, StringComparison.Ordinal))
            {
                if (implementationMethod.IsValid && implementationMethod != methodId)
                {
                    implementationMethod = SymbolId.None;
                    return false;
                }

                implementationMethod = methodId;
            }
        }

        return implementationMethod.IsValid;
    }

    private static void ApplyGivenImplementationMethod(EidosAstNode? function, SymbolId implementationMethod)
    {
        if (function == null || !implementationMethod.IsValid)
        {
            return;
        }

        function.SymbolId = implementationMethod;
        if (function is IdentifierExpr identifier)
        {
            identifier.ClearValueCandidates();
        }
    }

    private static string FormatGivenEvidencePath(GivenExpr given)
        => given.EvidencePath.Count == 0
            ? "<missing>"
            : string.Join(WellKnownStrings.Separators.Path, given.EvidencePath);
}
