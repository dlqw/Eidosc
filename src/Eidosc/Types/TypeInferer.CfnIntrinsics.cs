using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Diagnostic;
using Eidosc.Semantic;
using Eidosc.Symbols;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private bool TryInferCfnFromCall(CallExpr call, out Type resultType)
    {
        resultType = BaseTypes.Unit;
        var candidates = GetCompilerIntrinsicCalleeCandidates(call.Function, "cfn_from");
        if (candidates.Count == 0 || call.PositionalArgs.Count != 1)
        {
            return false;
        }

        var callbackType = SafeInferExpression(call.PositionalArgs[0]);
        if (InferNamedArgumentValues(call.NamedArgs))
        {
            resultType = CreateErrorRecoveryType();
            return true;
        }

        if (TryBuildCfnType(callbackType, out var cfnType) &&
            cfnType is TyCon cfnConstructor)
        {
            BindBuiltinIntrinsicCallee(
                call.Function,
                "cfn_from");
            resultType = cfnType;
            return true;
        }

        AddError(
            call.Span,
            DiagnosticMessages.CfnFromArgumentNotFunction,
            TypeErrorCode);
        resultType = CreateErrorRecoveryType();
        return true;
    }

    private bool TryInferCfnCall(CallExpr call, out Type resultType)
    {
        resultType = BaseTypes.Unit;
        var candidates = GetCompilerIntrinsicCalleeCandidates(call.Function, "cfn_call");
        if (candidates.Count == 0 || call.PositionalArgs.Count < 1)
        {
            return false;
        }

        var positionalArgTypes = call.PositionalArgs
            .Select(SafeInferExpression)
            .ToList();
        if (InferNamedArgumentValues(call.NamedArgs))
        {
            resultType = CreateErrorRecoveryType();
            return true;
        }

        var firstArgType = _substitution.Apply(positionalArgTypes[0]);
        if (firstArgType is not TyCon
            {
                Name: WellKnownStrings.BuiltinTypes.Cfn,
                Args.Count: > 0
            } cfnType)
        {
            AddError(
                call.Span,
                DiagnosticMessages.CfnCallFirstArgumentNotCfn,
                TypeErrorCode);
            resultType = CreateErrorRecoveryType();
            return true;
        }

        var expectedArgumentCount = cfnType.Args.Count - 1;
        var actualArgumentCount = positionalArgTypes.Count - 1;
        if (actualArgumentCount != expectedArgumentCount)
        {
            AddError(
                call.Span,
                DiagnosticMessages.CfnCallArgumentCountMismatch(
                    expectedArgumentCount,
                    actualArgumentCount),
                TypeErrorCode);
            resultType = CreateErrorRecoveryType();
            return true;
        }

        var hasArgumentTypeError = false;
        for (var i = 0; i < expectedArgumentCount; i++)
        {
            var unified = TryUnify(
                cfnType.Args[i],
                positionalArgTypes[i + 1],
                call.PositionalArgs[i + 1].Span,
                DiagnosticMessages.CfnCallArgumentTypeMismatch(i + 1));
            hasArgumentTypeError |= ContainsErrorRecoveryType(unified);
        }

        if (hasArgumentTypeError)
        {
            resultType = CreateErrorRecoveryType();
            return true;
        }

        BindBuiltinIntrinsicCallee(
            call.Function,
            "cfn_call");
        resultType = _substitution.Apply(cfnType.Args[^1]);
        return true;
    }

    private List<SymbolId> GetCompilerIntrinsicCalleeCandidates(
        EidosAstNode? callee,
        string intrinsicName)
    {
        var symbolIds = callee switch
        {
            IdentifierExpr identifier => identifier.ValueCandidateSymbolIds
                .Prepend(identifier.SymbolId),
            PathExpr path => path.ValueCandidateSymbolIds
                .Prepend(path.SymbolId),
            _ => []
        };

        return symbolIds
            .Where(static symbolId => symbolId.IsValid)
            .Distinct()
            .Where(symbolId =>
                _symbolTable.GetSymbol<FuncSymbol>(symbolId) is { IntrinsicName: { } candidateName } &&
                string.Equals(candidateName, intrinsicName, StringComparison.Ordinal))
            .ToList();
    }

    private void BindBuiltinIntrinsicCallee(
        EidosAstNode? callee,
        string intrinsicName)
    {
        var selected = _symbolTable.Symbols.Values
            .OfType<FuncSymbol>()
            .Where(static symbol =>
                symbol.Span.Equals(Eidosc.Utils.SourceSpan.Empty) &&
                !symbol.IsCompilerIntrinsic)
            .Where(symbol => string.Equals(symbol.Name, intrinsicName, StringComparison.Ordinal))
            .Select(static symbol => symbol.Id)
            .FirstOrDefault();

        if (!selected.IsValid)
        {
            return;
        }

        switch (callee)
        {
            case IdentifierExpr identifier:
                identifier.SymbolId = selected;
                break;
            case PathExpr path:
                path.SymbolId = selected;
                break;
        }
    }
}
