using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private bool TryRejectRuntimeComptimeFunctionCall(CallExpr call, out Type resultType)
    {
        resultType = CreateErrorRecoveryType();
        if (call.Function is not { } function ||
            !TryGetComptimeFunctionSymbol(function, out var name))
        {
            return false;
        }

        AddComptimeFunctionRuntimeUseError(function.Span, name);
        foreach (var arg in call.PositionalArgs)
        {
            SafeInferExpression(arg);
        }

        InferNamedArgumentValues(call.NamedArgs);
        return true;
    }

    private bool TryGetComptimeFunctionSymbol(EidosAstNode function, out string name)
    {
        name = string.Empty;
        var symbolId = function switch
        {
            IdentifierExpr identifier => identifier.SymbolId,
            PathExpr path => path.SymbolId,
            _ => SymbolId.None
        };

        if (!symbolId.IsValid ||
            _symbolTable.GetSymbol(symbolId) is not FuncSymbol { IsComptime: true } funcSymbol)
        {
            return false;
        }

        name = function switch
        {
            IdentifierExpr identifier when !string.IsNullOrWhiteSpace(identifier.Name) => identifier.Name,
            PathExpr path when path.Path.Count > 0 => FormatPath(path.Path),
            _ => funcSymbol.Name
        };
        return true;
    }

    private void AddComptimeFunctionRuntimeUseError(SourceSpan span, string name)
    {
        AddError(span, $"Cannot use comptime-only function '{name}' from runtime code.");
    }
}
