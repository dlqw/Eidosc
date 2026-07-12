using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Symbols;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private bool TryCreateAssociatedProjectionLookupRequest(
        TraitSymbol traitSymbol,
        IReadOnlyList<Type> traitArgTypes,
        out AssociatedProjectionLookupRequest request)
    {
        request = default;
        if (traitArgTypes.Count == 0 || traitArgTypes[0] is not TyCon implementingType)
        {
            return false;
        }

        var implementingTypeId = ImplLookupCanonicalizer.ResolveLookupTypeId(_symbolTable, implementingType);
        if (!implementingTypeId.IsValid)
        {
            return false;
        }

        var implementingTypeKey = ImplLookupCanonicalizer.BuildTypeRefKey(
            _symbolTable,
            implementingType,
            _substitution.Apply);
        var traitArgKeys = traitArgTypes
            .Select(type => ImplLookupCanonicalizer.BuildTypeRefKey(_symbolTable, type, _substitution.Apply))
            .ToList();
        request = new AssociatedProjectionLookupRequest(
            traitSymbol,
            implementingTypeId,
            implementingTypeKey,
            traitArgKeys);
        return true;
    }

    private Type InferAssociatedConstExpr(AssociatedConstExpr associatedConst)
    {
        if (!TryResolveAssociatedConstImplementation(associatedConst, out var implementation))
        {
            return CreateErrorRecoveryType();
        }

        var constType = ConvertType(implementation.Type!, []);
        associatedConst.SetImplementationValue(implementation.Value);

        if (implementation.Value != null)
        {
            var valueType = SafeInferExpression(implementation.Value);
            var unified = TryUnify(
                constType,
                valueType,
                implementation.Value.Span,
                DiagnosticMessages.ValueTypeMismatch($"associated const '{implementation.Name}'"));
            if (ContainsErrorRecoveryType(unified))
            {
                return CreateErrorRecoveryType();
            }
        }

        return _substitution.Apply(constType);
    }

    private bool TryResolveAssociatedConstImplementation(
        AssociatedConstExpr associatedConst,
        out AssociatedConstDecl implementation)
    {
        implementation = null!;
        if (associatedConst.Target is not TypePath { SymbolId.IsValid: true } traitPath ||
            traitPath.TypeArgs.Count == 0 ||
            string.IsNullOrWhiteSpace(associatedConst.MemberName))
        {
            AddError(associatedConst.Span, $"Associated const projection '.{associatedConst.MemberName}' requires a concrete trait application.");
            return false;
        }

        if (_symbolTable.GetSymbol<TraitSymbol>(traitPath.SymbolId) is not { } traitSymbol)
        {
            AddError(associatedConst.Span, $"Associated const projection '.{associatedConst.MemberName}' requires a trait type target.");
            return false;
        }

        var traitArgTypes = traitPath.TypeArgs
            .Select(typeArg => _substitution.Apply(ConvertType(typeArg, [], allowTypeConstructorReference: true)))
            .ToList();
        if (traitArgTypes.Count == 0 || traitArgTypes[0] is not TyCon)
        {
            AddError(associatedConst.Span, $"Associated const projection '{traitSymbol.Name}.{associatedConst.MemberName}' requires a concrete first trait argument.");
            return false;
        }

        if (!TryCreateAssociatedProjectionLookupRequest(traitSymbol, traitArgTypes, out var lookupRequest))
        {
            AddError(associatedConst.Span, $"Associated const projection '{traitSymbol.Name}.{associatedConst.MemberName}' could not resolve its implementing type.");
            return false;
        }

        AssociatedConstProjectionCacheKey? cacheKey = null;
        if (TryCreateAssociatedConstProjectionCacheKey(
                traitSymbol,
                associatedConst.MemberName,
                lookupRequest.ImplementingTypeKey,
                lookupRequest.TraitArgKeys,
                out var createdCacheKey))
        {
            cacheKey = createdCacheKey;
            if (_associatedConstProjectionCache.TryGetValue(createdCacheKey, out var cached))
            {
                IncrementProfilingCounter("Types.associatedConstProjectionCache.hits");
                implementation = cached.Implementation;
                StoreAssociatedConstProjectionSnapshotEntry(
                    createdCacheKey,
                    cached.ConstTypeSignature,
                    cached.ConstValueSignature);
                return true;
            }

            IncrementProfilingCounter("Types.associatedConstProjectionCache.misses");
        }
        else
        {
            IncrementProfilingCounter("Types.associatedConstProjectionCache.skipped");
        }

        var impl = _symbolTable.LookupImplForTraitByKeys(
            lookupRequest.ImplementingTypeId,
            traitSymbol.Id,
            lookupRequest.ImplementingTypeKey,
            lookupRequest.TraitArgKeys);
        if (impl == null)
        {
            AddError(associatedConst.Span, $"Associated const projection '{traitSymbol.Name}.{associatedConst.MemberName}' has no matching instance evidence.");
            return false;
        }

        if (!_associatedConstImplementations.TryGetValue((impl.Id, associatedConst.MemberName), out var foundImplementation))
        {
            AddError(associatedConst.Span, $"Instance '{impl.Name}' does not implement associated const '{associatedConst.MemberName}'.");
            return false;
        }

        implementation = foundImplementation;
        if (cacheKey.HasValue)
        {
            var constTypeSignature = foundImplementation.Type == null
                ? ""
                : BuildAssociatedTypeValueSignature(foundImplementation.Type);
            var constValueSignature = foundImplementation.Value == null
                ? ""
                : BuildAssociatedConstValueSignature(foundImplementation.Value);
            if (_previousAssociatedConstProjectionCache.TryGetValue(cacheKey.Value, out var previous))
            {
                IncrementProfilingCounter("Types.associatedConstProjectionPreviousCache.hits");
                IncrementProfilingCounter(
                    string.Equals(previous.ConstTypeSignature, constTypeSignature, StringComparison.Ordinal) &&
                    string.Equals(previous.ConstValueSignature, constValueSignature, StringComparison.Ordinal)
                        ? "Types.associatedConstProjectionPreviousCache.validatedHits"
                        : "Types.associatedConstProjectionPreviousCache.staleHits");
            }
            else
            {
                IncrementProfilingCounter("Types.associatedConstProjectionPreviousCache.misses");
            }

            _associatedConstProjectionCache[cacheKey.Value] = new AssociatedConstProjectionCacheEntry(
                foundImplementation,
                constTypeSignature,
                constValueSignature);
            StoreAssociatedConstProjectionSnapshotEntry(cacheKey.Value, constTypeSignature, constValueSignature);
        }

        return true;
    }

    private bool TryCreateAssociatedConstProjectionCacheKey(
        TraitSymbol traitSymbol,
        string memberName,
        ImplTypeRefKey implementingTypeKey,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys,
        out AssociatedConstProjectionCacheKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(memberName) ||
            !traitSymbol.Id.IsValid ||
            implementingTypeKey.ToString().Contains("TyVar", StringComparison.Ordinal) ||
            traitArgKeys.Any(static arg => arg.ToString().Contains("TyVar", StringComparison.Ordinal)))
        {
            return false;
        }

        key = new AssociatedConstProjectionCacheKey(
            traitSymbol.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            traitSymbol.Name,
            memberName,
            implementingTypeKey.ToString(),
            string.Join(",", traitArgKeys.Select(static arg => arg.ToString())));
        return true;
    }

    private void StoreAssociatedConstProjectionSnapshotEntry(
        AssociatedConstProjectionCacheKey cacheKey,
        string constTypeSignature,
        string constValueSignature)
    {
        _associatedConstProjectionSnapshotEntries[cacheKey] = new AssociatedConstProjectionSnapshotEntry(
            cacheKey.TraitKey,
            cacheKey.TraitName,
            cacheKey.MemberName,
            cacheKey.ImplementingTypeKey,
            cacheKey.TraitArgKeys,
            constTypeSignature,
            constValueSignature);
    }

    private static string BuildAssociatedConstValueSignature(EidosAstNode value)
    {
        return value switch
        {
            Ast.Expressions.LiteralExpr literal => $"literal({literal.Kind}:{literal.RawText})",
            Ast.Expressions.IdentifierExpr identifier => $"id({identifier.Name})",
            Ast.Expressions.CtorExpr ctor => $"ctor({ctor.ConstructorName}:{ctor.PositionalArgs.Count}:{ctor.NamedArgs.Count})",
            Ast.Expressions.CallExpr call => $"call({(call.Function == null ? "<missing>" : BuildAssociatedConstValueSignature(call.Function))}:{call.PositionalArgs.Count}:{call.NamedArgs.Count})",
            _ => $"{value.GetType().Name}@{value.Span.Position}:{value.Span.Length}"
        };
    }
}
