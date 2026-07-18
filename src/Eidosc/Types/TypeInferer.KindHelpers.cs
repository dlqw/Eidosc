using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private Kind.KVar FreshKindVariable()
    {
        return new Kind.KVar { Id = _nextKindVarId++ };
    }

    private static Kind ResolveKind(Kind kind)
    {
        while (kind is Kind.KVar { Instance: not null } kindVar)
        {
            kind = kindVar.Instance;
        }

        return kind;
    }

    private Kind GetTypeParamExpectedKind(TypePath path)
    {
        if (_typeParamKindStack.Count > 0 &&
            _typeParamKindStack.Peek().TryGetValue(path.TypeName, out var kind))
        {
            return kind;
        }

        if (path.SymbolId.IsValid &&
            _symbolTable.GetSymbol(path.SymbolId) is TypeParamSymbol typeParamSymbol &&
            !string.IsNullOrWhiteSpace(typeParamSymbol.KindAnnotation) &&
            KindParser.TryParse(typeParamSymbol.KindAnnotation, out var parsedKind, out _))
        {
            return parsedKind;
        }

        return Kind.KStar.Instance;
    }

    private Kind GetTypeParamKindByVarIndex(int varIndex)
    {
        if (_typeParamVarKindStack.Count == 0)
        {
            return Kind.KStar.Instance;
        }

        return _typeParamVarKindStack.Peek().TryGetValue(varIndex, out var kind)
            ? kind
            : Kind.KStar.Instance;
    }

    private Kind InferTypeKind(Type type, SourceSpan span)
    {
        type = _substitution.Apply(type);
        return type switch
        {
            TyVar typeVar => GetTypeParamKindByVarIndex(typeVar.Index),
            TyFun or TyTuple => Kind.KStar.Instance,
            TyCon typeCon => InferTyConKind(typeCon, span),
            _ => Kind.KStar.Instance
        };
    }

    private Kind InferTyConKind(TyCon typeCon, SourceSpan span)
    {
        var constructorKind = typeCon.ConstructorVarIndex.HasValue
            ? GetTypeParamKindByVarIndex(typeCon.ConstructorVarIndex.Value)
            : GetTypeConstructorKind(typeCon.Symbol);
        if (typeCon.Args.Count == 0)
        {
            return constructorKind;
        }

        var argumentKinds = typeCon.Args
            .Select(arg => InferTypeKind(arg, span))
            .ToList();
        if (!KindParser.TryApply(constructorKind, argumentKinds, out var resultKind, out var kindError))
        {
            AddError(span, kindError ?? DiagnosticMessages.InvalidKindApplicationForType(typeCon));
            return Kind.KStar.Instance;
        }

        return resultKind;
    }

    private Kind GetTypeConstructorKind(SymbolId symbolId)
    {
        return CreateTypeConstructorKindResolver().GetConstructorKind(symbolId);
    }

    private int GetTypeConstructorArity(SymbolId symbolId)
    {
        return CreateTypeConstructorKindResolver().GetExpectedParamCount(symbolId);
    }

    private TypeConstructorKindResolver CreateTypeConstructorKindResolver()
    {
        return new TypeConstructorKindResolver(_symbolTable, _typeConstructorKindsBySymbol);
    }
}
