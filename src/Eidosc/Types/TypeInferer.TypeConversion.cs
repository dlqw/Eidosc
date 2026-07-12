using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Semantic;

namespace Eidosc.Types;

public sealed partial class TypeInferer
{
    private List<Type> ConvertTypeArgs(
        IEnumerable<TypeNode> typeArgs,
        Dictionary<string, Type> typeVarEnv)
    {
        return [.. typeArgs.Select(arg => 
            ConvertType(arg, typeVarEnv, allowTypeConstructorReference: true))];
    }

    /// <summary>
    /// 将 AST 类型节点转换为内部类型表示
    /// </summary>
    private Type ConvertType(
        TypeNode typeNode,
        Dictionary<string, Type> typeVarEnv,
        bool allowTypeConstructorReference = false)
    {
        return typeNode switch
        {
            TypePath path => ConvertTypePath(path, typeVarEnv, allowTypeConstructorReference),
            AssociatedTypeProjection projection => ConvertAssociatedTypeProjection(projection, typeVarEnv, allowTypeConstructorReference),
            ArrowType arrow => ConvertArrowType(arrow, typeVarEnv),
            EffectfulType effectful => ConvertEffectfulType(effectful, typeVarEnv),
            TupleType tuple => ConvertTupleType(tuple, typeVarEnv),
            WildcardType => _substitution.FreshTypeVariable(),
            _ => ConvertUnsupportedTypeNode(typeNode)
        };
    }

    private Type ConvertAssociatedTypeProjection(
        AssociatedTypeProjection projection,
        Dictionary<string, Type> typeVarEnv,
        bool allowTypeConstructorReference)
    {
        if (projection.Target == null)
        {
            return ConvertUnsupportedTypeNode(projection);
        }

        var targetType = ConvertType(projection.Target, typeVarEnv, allowTypeConstructorReference: true);
        if (TryReduceAssociatedTypeProjection(projection, typeVarEnv, allowTypeConstructorReference, out var reducedType))
        {
            projection.InferredType = reducedType;
            return reducedType;
        }

        var targetName = projection.Target switch
        {
            TypePath path => FormatTypePathForAssociatedProjection(path),
            _ => targetType.ToString() ?? "<target>"
        };
        var projected = new TyCon
        {
            Name = $"{targetName}.{projection.MemberName}",
            Symbol = projection.SymbolId,
            Args = [targetType]
        };
        projection.InferredType = projected;
        return projected;
    }

    private bool TryReduceAssociatedTypeProjection(
        AssociatedTypeProjection projection,
        Dictionary<string, Type> typeVarEnv,
        bool allowTypeConstructorReference,
        out Type reducedType)
    {
        reducedType = null!;
        if (projection.Target is not TypePath { SymbolId.IsValid: true } traitPath ||
            traitPath.TypeArgs.Count == 0 ||
            string.IsNullOrWhiteSpace(projection.MemberName))
        {
            return false;
        }

        var traitSymbol = _symbolTable.GetSymbol<TraitSymbol>(traitPath.SymbolId);
        if (traitSymbol == null)
        {
            return false;
        }

        var traitArgTypes = traitPath.TypeArgs
            .Select(typeArg => _substitution.Apply(ConvertType(typeArg, typeVarEnv, allowTypeConstructorReference: true)))
            .ToList();
        if (traitArgTypes.Count == 0 || traitArgTypes[0] is not TyCon)
        {
            return false;
        }

        if (!TryCreateAssociatedProjectionLookupRequest(traitSymbol, traitArgTypes, out var lookupRequest))
        {
            return false;
        }

        if (!TryCreateAssociatedTypeProjectionCacheKey(
                traitSymbol,
                projection.MemberName,
                lookupRequest.ImplementingTypeKey,
                lookupRequest.TraitArgKeys,
                allowTypeConstructorReference,
                out var cacheKey))
        {
            IncrementProfilingCounter("Types.associatedTypeProjectionCache.skipped");
            return TryReduceAssociatedTypeProjectionUncached(
                projection,
                typeVarEnv,
                allowTypeConstructorReference,
                traitSymbol,
                lookupRequest.ImplementingTypeId,
                lookupRequest.ImplementingTypeKey,
                lookupRequest.TraitArgKeys,
                cacheKey: null,
                previous: null,
                out reducedType);
        }

        if (_associatedTypeProjectionCache.TryGetValue(cacheKey, out var cached))
        {
            IncrementProfilingCounter("Types.associatedTypeProjectionCache.hits");
            reducedType = cached.ReducedType;
            return true;
        }

        IncrementProfilingCounter("Types.associatedTypeProjectionCache.misses");
        AssociatedTypeProjectionSnapshotEntry? previous = null;
        if (_previousAssociatedTypeProjectionCache.TryGetValue(cacheKey, out previous))
        {
            IncrementProfilingCounter("Types.associatedTypeProjectionPreviousCache.hits");
            IncrementProfilingCounter("Types.associatedTypeProjectionPreviousCache.reducedTypeBytes", previous.ReducedType.Length);
            if (TryRestoreAssociatedTypeProjectionFromPrevious(
                    projection,
                    typeVarEnv,
                    allowTypeConstructorReference,
                    traitSymbol,
                    lookupRequest.ImplementingTypeId,
                    lookupRequest.ImplementingTypeKey,
                    lookupRequest.TraitArgKeys,
                    cacheKey,
                    previous,
                    out reducedType))
            {
                return true;
            }
        }
        else
        {
            IncrementProfilingCounter("Types.associatedTypeProjectionPreviousCache.misses");
        }

        return TryReduceAssociatedTypeProjectionUncached(
            projection,
            typeVarEnv,
            allowTypeConstructorReference,
            traitSymbol,
            lookupRequest.ImplementingTypeId,
            lookupRequest.ImplementingTypeKey,
            lookupRequest.TraitArgKeys,
            cacheKey,
            previous,
            out reducedType);
    }

    private bool TryRestoreAssociatedTypeProjectionFromPrevious(
        AssociatedTypeProjection projection,
        Dictionary<string, Type> typeVarEnv,
        bool allowTypeConstructorReference,
        TraitSymbol traitSymbol,
        TypeId implementingTypeId,
        ImplTypeRefKey implementingTypeKey,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys,
        AssociatedTypeProjectionCacheKey cacheKey,
        AssociatedTypeProjectionSnapshotEntry previous,
        out Type reducedType)
    {
        reducedType = null!;
        var impl = _symbolTable.LookupImplForTraitByKeys(
            implementingTypeId,
            traitSymbol.Id,
            implementingTypeKey,
            traitArgKeys);
        if (impl == null ||
            !_associatedTypeImplementations.TryGetValue((impl.Id, projection.MemberName), out var valueTypeNode))
        {
            IncrementProfilingCounter("Types.associatedTypeProjectionPreviousCache.restoreMisses");
            return false;
        }

        var currentValueTypeSignature = BuildAssociatedTypeValueSignature(valueTypeNode);
        if (!string.Equals(previous.ValueTypeSignature, currentValueTypeSignature, StringComparison.Ordinal))
        {
            IncrementProfilingCounter("Types.associatedTypeProjectionPreviousCache.restoreStaleValueSignatures");
            return false;
        }

        if (!TryRestoreAssociatedProjectionReducedType(previous, out reducedType))
        {
            IncrementProfilingCounter("Types.associatedTypeProjectionPreviousCache.restoreSkipped");
            return false;
        }

        _associatedTypeProjectionCache[cacheKey] = new AssociatedTypeProjectionCacheEntry(
            reducedType,
            previous.ReducedType);
        _associatedTypeProjectionSnapshotEntries[cacheKey] = CreateAssociatedTypeProjectionSnapshotEntry(
            cacheKey,
            currentValueTypeSignature,
            reducedType,
            previous.ReducedType);
        projection.InferredType = reducedType;
        IncrementProfilingCounter("Types.associatedTypeProjectionPreviousCache.restoreHits");
        IncrementProfilingCounter("Types.associatedTypeProjectionPreviousCache.validatedHits");
        return true;
    }

    private bool TryReduceAssociatedTypeProjectionUncached(
        AssociatedTypeProjection projection,
        Dictionary<string, Type> typeVarEnv,
        bool allowTypeConstructorReference,
        TraitSymbol traitSymbol,
        TypeId implementingTypeId,
        ImplTypeRefKey implementingTypeKey,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys,
        AssociatedTypeProjectionCacheKey? cacheKey,
        AssociatedTypeProjectionSnapshotEntry? previous,
        out Type reducedType)
    {
        reducedType = null!;
        var impl = _symbolTable.LookupImplForTraitByKeys(
            implementingTypeId,
            traitSymbol.Id,
            implementingTypeKey,
            traitArgKeys);
        if (impl == null ||
            !_associatedTypeImplementations.TryGetValue((impl.Id, projection.MemberName), out var valueTypeNode))
        {
            return false;
        }

        reducedType = ConvertType(valueTypeNode, typeVarEnv, allowTypeConstructorReference);
        if (cacheKey.HasValue && !ContainsTypeVariable(reducedType))
        {
            var reducedTypeKey = _substitution.Apply(reducedType).ToString();
            if (previous != null)
            {
                IncrementProfilingCounter(
                    string.Equals(previous.ReducedType, reducedTypeKey, StringComparison.Ordinal)
                        ? "Types.associatedTypeProjectionPreviousCache.validatedHits"
                        : "Types.associatedTypeProjectionPreviousCache.staleHits");
            }

            _associatedTypeProjectionCache[cacheKey.Value] = new AssociatedTypeProjectionCacheEntry(
                reducedType,
                reducedTypeKey);
            _associatedTypeProjectionSnapshotEntries[cacheKey.Value] =
                CreateAssociatedTypeProjectionSnapshotEntry(
                    cacheKey.Value,
                    BuildAssociatedTypeValueSignature(valueTypeNode),
                    reducedType,
                    reducedTypeKey);
        }

        return true;
    }

    private AssociatedTypeProjectionSnapshotEntry CreateAssociatedTypeProjectionSnapshotEntry(
        AssociatedTypeProjectionCacheKey cacheKey,
        string valueTypeSignature,
        Type reducedType,
        string reducedTypeKey)
    {
        var (kind, name, args) = GetAssociatedProjectionReducedTypeShape(reducedType);
        return new AssociatedTypeProjectionSnapshotEntry(
            cacheKey.TraitKey,
            cacheKey.TraitName,
            cacheKey.MemberName,
            cacheKey.ImplementingTypeKey,
            cacheKey.TraitArgKeys,
            cacheKey.AllowTypeConstructorReference,
            valueTypeSignature,
            kind,
            name,
            args,
            reducedTypeKey,
            BuildAssociatedProjectionReducedTypeShape(reducedType));
    }

    private bool TryRestoreAssociatedProjectionReducedType(
        AssociatedTypeProjectionSnapshotEntry previous,
        out Type reducedType)
    {
        reducedType = null!;
        var canonicalKey = previous.ReducedTypeShape?.CanonicalKey ?? string.Empty;
        if (string.IsNullOrWhiteSpace(canonicalKey))
        {
            IncrementProfilingCounter("Types.associatedTypeProjectionPreviousCache.restoreMissingCanonicalShape");
            return false;
        }

        if (previous.ReducedTypeShape != null)
        {
            return TryRestoreAssociatedProjectionReducedTypeShape(previous.ReducedTypeShape, out reducedType) &&
                   string.Equals(BuildAssociatedProjectionCanonicalTypeKey(reducedType), canonicalKey, StringComparison.Ordinal);
        }

        if (!string.Equals(previous.ReducedTypeKind, nameof(TyCon), StringComparison.Ordinal) ||
            !string.IsNullOrWhiteSpace(previous.ReducedTypeArgs))
        {
            return false;
        }

        return false;
    }

    private bool TryRestoreAssociatedProjectionReducedTypeShape(
        AssociatedTypeProjectionReducedTypeShape shape,
        out Type reducedType)
    {
        reducedType = null!;
        return shape.Kind switch
        {
            nameof(TyCon) => TryRestoreAssociatedProjectionTyCon(shape, out reducedType),
            nameof(TyRef) when shape.Args.Count == 1 &&
                                TryRestoreAssociatedProjectionReducedTypeShape(shape.Args[0], out var inner)
                => RestoreType(new TyRef { Inner = inner }, out reducedType),
            nameof(TyMutRef) when shape.Args.Count == 1 &&
                                   TryRestoreAssociatedProjectionReducedTypeShape(shape.Args[0], out var inner)
                => RestoreType(new TyMutRef { Inner = inner }, out reducedType),
            nameof(TyShared) when shape.Args.Count == 1 &&
                                  TryRestoreAssociatedProjectionReducedTypeShape(shape.Args[0], out var inner)
                => RestoreType(new TyShared { Inner = inner }, out reducedType),
            nameof(TyTuple) => TryRestoreAssociatedProjectionTypeList(shape.Args, out var elements) &&
                               RestoreType(new TyTuple { Elements = elements }, out reducedType),
            nameof(TyFun) when shape.Args.Count == 2 &&
                                TryRestoreAssociatedProjectionReducedTypeShape(shape.Args[0], out var param) &&
                                TryRestoreAssociatedProjectionReducedTypeShape(shape.Args[1], out var result)
                => RestoreType(new TyFun { Params = [param], Result = result }, out reducedType),
            _ => false
        };

        static bool RestoreType(Type type, out Type restored)
        {
            restored = type;
            return true;
        }
    }

    private bool TryRestoreAssociatedProjectionTyCon(
        AssociatedTypeProjectionReducedTypeShape shape,
        out Type reducedType)
    {
        reducedType = null!;
        if (string.IsNullOrWhiteSpace(shape.Name) ||
            !TryRestoreAssociatedProjectionTypeList(shape.Args, out var args))
        {
            return false;
        }

        var typeId = shape.TypeId > 0
            ? new TypeId(shape.TypeId)
            : BaseTypes.GetBuiltInTypeId(shape.Name);
        var symbolId = shape.SymbolId > 0
            ? new SymbolId(shape.SymbolId)
            : SymbolId.None;

        if (symbolId.IsValid &&
            _symbolTable.GetSymbol(symbolId) is { TypeId.IsValid: true } symbol)
        {
            if (!typeId.IsValid)
            {
                typeId = symbol.TypeId;
            }
        }
        else if (_symbolTable.LookupType(shape.Name) is { IsValid: true } lookedUpType)
        {
            symbolId = lookedUpType;
            if (!typeId.IsValid &&
                _symbolTable.GetSymbol(lookedUpType) is { TypeId.IsValid: true } lookedUpSymbol)
            {
                typeId = lookedUpSymbol.TypeId;
            }
        }

        if (!typeId.IsValid && !shape.ConstructorVarIndex.HasValue)
        {
            return false;
        }

        reducedType = new TyCon
        {
            Id = typeId,
            Symbol = symbolId,
            Name = shape.Name,
            Args = args,
            ConstructorVarIndex = shape.ConstructorVarIndex
        };
        return string.IsNullOrWhiteSpace(shape.CanonicalKey) ||
               string.Equals(BuildAssociatedProjectionCanonicalTypeKey(reducedType), shape.CanonicalKey, StringComparison.Ordinal);
    }

    private bool TryRestoreAssociatedProjectionTypeList(
        IReadOnlyList<AssociatedTypeProjectionReducedTypeShape> shapes,
        out List<Type> types)
    {
        types = new List<Type>(shapes.Count);
        foreach (var shape in shapes)
        {
            if (!TryRestoreAssociatedProjectionReducedTypeShape(shape, out var type))
            {
                types = [];
                return false;
            }

            types.Add(type);
        }

        return true;
    }

    private static (string Kind, string Name, string Args) GetAssociatedProjectionReducedTypeShape(Type type)
    {
        return type switch
        {
            TyCon con => (
                nameof(TyCon),
                con.Name,
                con.Args.Count == 0
                    ? ""
                    : string.Join(",", con.Args.Select(static arg => arg.ToString()))),
            TyRef reference => (nameof(TyRef), WellKnownStrings.BuiltinTypes.Ref, reference.Inner.ToString()),
            TyMutRef reference => (nameof(TyMutRef), WellKnownStrings.BuiltinTypes.MRef, reference.Inner.ToString()),
            TyTuple tuple => (nameof(TyTuple), "", string.Join(",", tuple.Elements.Select(static element => element.ToString()))),
            TyFun function => (nameof(TyFun), "", function.ToString()),
            _ => (type.GetType().Name, "", type.ToString())
        };
    }

    private AssociatedTypeProjectionReducedTypeShape BuildAssociatedProjectionReducedTypeShape(Type type)
    {
        return type switch
        {
            TyCon con => new AssociatedTypeProjectionReducedTypeShape(
                nameof(TyCon),
                con.Name,
                con.Args.Select(BuildAssociatedProjectionReducedTypeShape).ToArray(),
                BuildAssociatedProjectionCanonicalTypeKey(con),
                con.Symbol.Value,
                ResolveAssociatedProjectionCurrentTypeId(con).Value,
                con.ConstructorVarIndex),
            TyRef reference => new AssociatedTypeProjectionReducedTypeShape(
                nameof(TyRef),
                WellKnownStrings.BuiltinTypes.Ref,
                [BuildAssociatedProjectionReducedTypeShape(reference.Inner)],
                BuildAssociatedProjectionCanonicalTypeKey(reference)),
            TyMutRef reference => new AssociatedTypeProjectionReducedTypeShape(
                nameof(TyMutRef),
                WellKnownStrings.BuiltinTypes.MRef,
                [BuildAssociatedProjectionReducedTypeShape(reference.Inner)],
                BuildAssociatedProjectionCanonicalTypeKey(reference)),
            TyShared shared => new AssociatedTypeProjectionReducedTypeShape(
                nameof(TyShared),
                WellKnownStrings.BuiltinTypes.Shared,
                [BuildAssociatedProjectionReducedTypeShape(shared.Inner)],
                BuildAssociatedProjectionCanonicalTypeKey(shared)),
            TyTuple tuple => new AssociatedTypeProjectionReducedTypeShape(
                nameof(TyTuple),
                "",
                tuple.Elements.Select(BuildAssociatedProjectionReducedTypeShape).ToArray(),
                BuildAssociatedProjectionCanonicalTypeKey(tuple)),
            TyFun { Params.Count: 1 } function => new AssociatedTypeProjectionReducedTypeShape(
                nameof(TyFun),
                "",
                [
                    BuildAssociatedProjectionReducedTypeShape(function.Params[0]),
                    BuildAssociatedProjectionReducedTypeShape(function.Result)
                ],
                BuildAssociatedProjectionCanonicalTypeKey(function)),
            _ => new AssociatedTypeProjectionReducedTypeShape(
                type.GetType().Name,
                type.ToString() ?? type.GetType().Name,
                [],
                BuildAssociatedProjectionCanonicalTypeKey(type))
        };
    }

    private string BuildAssociatedProjectionCanonicalTypeKey(Type type)
    {
        return TypeCanonicalKeyBuilder.Build(type, ResolveAssociatedProjectionCurrentTypeId);
    }

    private TypeId ResolveAssociatedProjectionCurrentTypeId(TyCon con)
    {
        if (con.Id.IsValid)
        {
            return con.Id;
        }

        if (con.Symbol.IsValid &&
            _symbolTable.GetSymbol(con.Symbol) is { TypeId.IsValid: true } symbol)
        {
            return symbol.TypeId;
        }

        if (!string.IsNullOrWhiteSpace(con.Name) &&
            _symbolTable.LookupType(con.Name) is { IsValid: true } lookedUp &&
            _symbolTable.GetSymbol(lookedUp) is { TypeId.IsValid: true } lookedUpSymbol)
        {
            return lookedUpSymbol.TypeId;
        }

        return BaseTypes.GetBuiltInTypeId(con.Name);
    }

    private static string BuildAssociatedTypeValueSignature(TypeNode typeNode)
    {
        return typeNode switch
        {
            TypePath path => BuildTypePathSignature(path),
            AssociatedTypeProjection projection => $"assoc({BuildAssociatedTypeValueSignature(projection.Target!)}.{projection.MemberName})",
            ArrowType arrow => $"arrow({BuildAssociatedTypeValueSignature(arrow.ParamType)}->{BuildAssociatedTypeValueSignature(arrow.ReturnType)})",
            EffectfulType effectful => BuildEffectfulTypeSignature(effectful),
            TupleType tuple => $"tuple({string.Join(",", tuple.Elements.Select(BuildAssociatedTypeValueSignature))})",
            WildcardType => "_",
            _ => typeNode.GetType().Name
        };
    }

    private static string BuildTypePathSignature(TypePath path)
    {
        var package = string.IsNullOrWhiteSpace(path.PackageAlias) ? "" : $"{path.PackageAlias}:";
        var module = path.ModulePath.Count == 0 ? "" : $"{string.Join(".", path.ModulePath)}.";
        var args = path.TypeArgs.Count == 0
            ? ""
            : $"[{string.Join(",", path.TypeArgs.Select(BuildAssociatedTypeValueSignature))}]";
        return $"path({package}{module}{path.TypeName}{args})";
    }

    private static string BuildEffectfulTypeSignature(EffectfulType effectful)
    {
        var effects = string.Join(
            ",",
            effectful.EnumerateEffectPaths()
                .Select(static path => string.Join(".", path)));
        var output = effectful.OutputType == null
            ? WellKnownStrings.BuiltinTypes.Unit
            : BuildAssociatedTypeValueSignature(effectful.OutputType);
        return $"effect({BuildAssociatedTypeValueSignature(effectful.InputType)}|{effects}|{output})";
    }

    private bool TryCreateAssociatedTypeProjectionCacheKey(
        TraitSymbol traitSymbol,
        string memberName,
        ImplTypeRefKey implementingTypeKey,
        IReadOnlyList<ImplTypeRefKey> traitArgKeys,
        bool allowTypeConstructorReference,
        out AssociatedTypeProjectionCacheKey key)
    {
        key = default;
        if (string.IsNullOrWhiteSpace(memberName) ||
            !traitSymbol.Id.IsValid ||
            implementingTypeKey.ToString().Contains("TyVar", StringComparison.Ordinal) ||
            traitArgKeys.Any(static arg => arg.ToString().Contains("TyVar", StringComparison.Ordinal)))
        {
            return false;
        }

        key = new AssociatedTypeProjectionCacheKey(
            traitSymbol.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            traitSymbol.Name,
            memberName,
            implementingTypeKey.ToString(),
            string.Join(",", traitArgKeys.Select(static arg => arg.ToString())),
            allowTypeConstructorReference);
        return true;
    }

    private static string FormatTypePathForAssociatedProjection(TypePath path)
    {
        var parts = path.ToQualifiedPathParts();
        var display = parts.Count == 0 ? path.TypeName : string.Join(WellKnownStrings.Separators.Path, parts);
        if (path.TypeArgs.Count == 0)
        {
            return display;
        }

        return $"{display}[{string.Join(", ", path.TypeArgs.Select(static _ => "_"))}]";
    }

    private Type ConvertUnsupportedTypeNode(TypeNode typeNode)
    {
        AddError(typeNode.Span, DiagnosticMessages.UnsupportedTypeNodeKind(typeNode.GetType().Name));
        var recovered = CreateErrorRecoveryType();
        typeNode.InferredType = recovered;
        return recovered;
    }

    private Type ConvertTypePath(
        TypePath path,
        Dictionary<string, Type> typeVarEnv,
        bool allowTypeConstructorReference)
    {
        if (path.ModulePath.Count == 0 && typeVarEnv.TryGetValue(path.TypeName, out var typeVar))
        {
            var expectedKind = ResolveKind(GetTypeParamExpectedKind(path));
            var typeArgs = ConvertTypeArgs(path.TypeArgs, typeVarEnv);

            if (typeArgs.Count == 0)
            {
                if (expectedKind is Kind.KVar { Instance: null } inferredKindVar)
                {
                    inferredKindVar.Instance = Kind.KStar.Instance;
                    return typeVar;
                }

                if (expectedKind is Kind.KStar)
                {
                    return typeVar;
                }

                var expectedArityWithoutArgs = KindParser.GetTopLevelArity(expectedKind);
                if (!allowTypeConstructorReference)
                {
                    AddError(
                        path.Span,
                        DiagnosticMessages.TypeParameterExpectsTypeArguments(path.TypeName, expectedArityWithoutArgs, 0));
                }

                return new TyCon
                {
                    Name = path.TypeName,
                    Symbol = path.SymbolId,
                    ConstructorVarIndex = typeVar is TyVar directTyVar ? directTyVar.Index : null,
                    Args = typeArgs
                };
            }

            if (typeVar is not TyVar typeConstructorVar)
            {
                AddError(path.Span, DiagnosticMessages.TypeParameterCannotBeUsedAsTypeConstructor(path.TypeName));
                return CreateErrorRecoveryType();
            }

            if (expectedKind is Kind.KStar)
            {
                AddError(path.Span, DiagnosticMessages.TypeParameterDoesNotAcceptTypeArguments(path.TypeName));
                return CreateErrorRecoveryType();
            }

            var argumentKinds = typeArgs
                .Select(arg => InferTypeKind(arg, path.Span))
                .ToList();

            if (expectedKind is Kind.KVar kindVar && kindVar.Instance == null)
            {
                Kind inferredResultKind = allowTypeConstructorReference
                    ? FreshKindVariable()
                    : Kind.KStar.Instance;

                for (var i = argumentKinds.Count - 1; i >= 0; i--)
                {
                    inferredResultKind = new Kind.KArrow(argumentKinds[i], inferredResultKind);
                }

                kindVar.Instance = inferredResultKind;
                expectedKind = ResolveKind(inferredResultKind);
            }

            expectedKind = ResolveKind(expectedKind);
            var expectedArity = KindParser.GetTopLevelArity(expectedKind);
            if (!KindParser.TryApply(expectedKind, argumentKinds, out var resultKind, out var kindError))
            {
                AddError(path.Span, kindError ?? DiagnosticMessages.InvalidHigherKindedTypeApplication(path.TypeName));
                return CreateErrorRecoveryType();
            }
            else if (!allowTypeConstructorReference)
            {
                resultKind = ResolveKind(resultKind);
                if (resultKind is Kind.KVar resultKindVar && resultKindVar.Instance == null)
                {
                    resultKindVar.Instance = Kind.KStar.Instance;
                    resultKind = resultKindVar.Instance;
                }

                if (resultKind is not Kind.KStar)
                {
                    AddError(
                        path.Span,
                        DiagnosticMessages.TypeParameterExpectsTypeArguments(path.TypeName, expectedArity, typeArgs.Count));
                    return CreateErrorRecoveryType();
                }
            }

            return new TyCon
            {
                Name = path.TypeName,
                Symbol = path.SymbolId,
                ConstructorVarIndex = typeConstructorVar.Index,
                Args = typeArgs
            };
        }

        if (path.TypeName == WellKnownStrings.BuiltinTypes.Shared &&
            (path.ModulePath.Count == 0 || path.ModulePath.SequenceEqual(["Std", "Shared"]) || path.ModulePath.SequenceEqual(["Shared"])))
        {
            if (path.TypeArgs.Count != 1)
            {
                AddError(
                    path.Span,
                    DiagnosticMessages.TypeExpectsTypeArguments(path.TypeName, 1, path.TypeArgs.Count));
                return CreateErrorRecoveryType();
            }

            var innerType = ConvertType(path.TypeArgs[0], typeVarEnv, allowTypeConstructorReference: true);
            return new TyShared { Inner = innerType };
        }

        if (path.ModulePath.Count == 0 && path.TypeName is WellKnownStrings.BuiltinTypes.Ref or WellKnownStrings.BuiltinTypes.MRef or WellKnownStrings.BuiltinTypes.MutRef)
        {
            if (path.TypeArgs.Count != 1)
            {
                AddError(
                    path.Span,
                    DiagnosticMessages.TypeExpectsTypeArguments(path.TypeName, 1, path.TypeArgs.Count));
                return CreateErrorRecoveryType();
            }

            var innerType = ConvertType(path.TypeArgs[0], typeVarEnv, allowTypeConstructorReference: true);
            return path.TypeName == WellKnownStrings.BuiltinTypes.Ref
                ? new TyRef { Inner = innerType }
                : new TyMutRef { Inner = innerType };
        }

        if (path.ModulePath.Count == 0 && path.TypeName == WellKnownStrings.BuiltinTypes.TypeEq)
        {
            if (path.TypeArgs.Count != 2)
            {
                AddError(
                    path.Span,
                    DiagnosticMessages.TypeExpectsTypeArguments(path.TypeName, 2, path.TypeArgs.Count));
                return CreateErrorRecoveryType();
            }

            var left = ConvertType(path.TypeArgs[0], typeVarEnv, allowTypeConstructorReference: true);
            var right = ConvertType(path.TypeArgs[1], typeVarEnv, allowTypeConstructorReference: true);
            return new TyCon
            {
                Name = WellKnownStrings.BuiltinTypes.TypeEq,
                Id = new TypeId(BaseTypes.TypeEqId),
                Args = [left, right]
            };
        }

        var name = path.TypeName;
        var args = ConvertTypeArgs(path.TypeArgs, typeVarEnv);
        var arityIsValid = ValidateTypePathArity(path, args.Count, allowTypeConstructorReference);
        var kindArgumentsAreValid = ValidateTypePathKindArguments(path, args);
        if (!arityIsValid || !kindArgumentsAreValid)
        {
            return CreateErrorRecoveryType();
        }

        ApplyTypePathTraitConstraints(path, args);

        if (TryExpandTypeAlias(path, args, typeVarEnv, allowTypeConstructorReference, out var expandedAliasType))
        {
            return expandedAliasType;
        }

        return new TyCon
        {
            Name = name,
            Symbol = path.SymbolId,
            Args = args
        };
    }

    private bool TryExpandTypeAlias(
        TypePath path,
        IReadOnlyList<Type> args,
        Dictionary<string, Type> typeVarEnv,
        bool allowTypeConstructorReference,
        out Type expandedAliasType)
    {
        expandedAliasType = null!;
        if (!path.SymbolId.IsValid ||
            !_adtDefinitionsBySymbol.TryGetValue(path.SymbolId, out var adtDefinition) ||
            !IsTypeAliasDefinition(adtDefinition) ||
            adtDefinition.AliasTarget == null)
        {
            return false;
        }

        var aliasTypeVarEnv = CreateAliasTypeVarEnv(typeVarEnv, adtDefinition, args);
        var aliasKindEnvByName = CreateTypeParamKindMapForOwner(path.SymbolId, GetAdtTypeParamNames(adtDefinition));
        expandedAliasType = ConvertTypeWithAdditionalKindContext(
            adtDefinition.AliasTarget,
            aliasTypeVarEnv,
            aliasKindEnvByName,
            allowTypeConstructorReference);
        return true;
    }

    private Dictionary<string, Type> CreateAliasTypeVarEnv(
        Dictionary<string, Type> typeVarEnv,
        AdtDef adtDefinition,
        IReadOnlyList<Type> args)
    {
        var aliasTypeVarEnv = new Dictionary<string, Type>(typeVarEnv, StringComparer.Ordinal);
        for (var i = 0; i < adtDefinition.TypeParams.Count; i++)
        {
            var typeParamName = adtDefinition.TypeParams[i].Name;
            if (string.IsNullOrWhiteSpace(typeParamName))
            {
                continue;
            }

            aliasTypeVarEnv[typeParamName] = i < args.Count
                ? args[i]
                : _substitution.FreshTypeVariable();
        }

        return aliasTypeVarEnv;
    }

    private bool ValidateTypePathArity(
        TypePath path,
        int actualTypeArgCount,
        bool allowTypeConstructorReference)
    {
        if (!path.SymbolId.IsValid)
        {
            return true;
        }

        if (path.ModulePath.Count == 0 &&
            string.Equals(path.TypeName, WellKnownStrings.BuiltinTypes.Cfn, StringComparison.Ordinal))
        {
            if (actualTypeArgCount == 0 && !allowTypeConstructorReference)
            {
                AddError(
                    path.Span,
                    DiagnosticMessages.TypeExpectsAtLeastTypeArguments(path.TypeName, 1, 0));
                return false;
            }

            return true;
        }

        var symbol = _symbolTable.GetSymbol(path.SymbolId);
        int? expectedTypeArgCount = symbol switch
        {
            null => null,
            TypeParamSymbol => 0,
            _ => GetTypeConstructorArity(path.SymbolId)
        };

        if (!expectedTypeArgCount.HasValue || expectedTypeArgCount.Value == actualTypeArgCount)
        {
            return true;
        }

        if (allowTypeConstructorReference &&
            actualTypeArgCount <= expectedTypeArgCount.Value)
        {
            return true;
        }

        AddError(
            path.Span,
            DiagnosticMessages.TypeExpectsTypeArguments(path.TypeName, expectedTypeArgCount.Value, actualTypeArgCount));
        return false;
    }

    private bool ValidateTypePathKindArguments(TypePath path, IReadOnlyList<Type> typeArgs)
    {
        if (!path.SymbolId.IsValid || typeArgs.Count == 0)
        {
            return true;
        }

        if (!_typeParamKindBindingsBySymbol.TryGetValue(path.SymbolId, out var kindBinding))
        {
            return true;
        }

        var isValid = true;
        var matchCount = Math.Min(typeArgs.Count, kindBinding.ExpectedKinds.Count);
        for (var i = 0; i < matchCount; i++)
        {
            var expectedKind = kindBinding.ExpectedKinds[i];
            var actualKind = InferTypeKind(typeArgs[i], path.Span);
            if (Kind.IsCompatible(expectedKind, actualKind))
            {
                continue;
            }

            var typeParamName = i < kindBinding.TypeParamNames.Count
                ? kindBinding.TypeParamNames[i]
                : $"T{i + 1}";

            AddError(
                path.Span,
                DiagnosticMessages.KindMismatchForTypeArgument(
                    i + 1,
                    typeParamName,
                    path.TypeName,
                    KindParser.ToKindText(expectedKind),
                    KindParser.ToKindText(actualKind)));
            isValid = false;
        }

        return isValid;
    }

    private void ApplyTypePathTraitConstraints(TypePath path, IReadOnlyList<Type> typeArgs)
    {
        if (!path.SymbolId.IsValid ||
            !_adtTypeParamConstraintBindings.TryGetValue(path.SymbolId, out var adtBinding))
        {
            return;
        }

        var matchCount = Math.Min(typeArgs.Count, adtBinding.TraitRequirementsByIndex.Count);
        for (var i = 0; i < matchCount; i++)
        {
            var typeArg = typeArgs[i];
            foreach (var requirement in adtBinding.TraitRequirementsByIndex[i])
            {
                _constraintGenerator.Constraints.AddTrait(
                    typeArg,
                    requirement.TraitId,
                    requirement.TraitName,
                    path.Span);
            }
        }
    }

    private Type ConvertArrowType(ArrowType arrow, Dictionary<string, Type> typeVarEnv)
    {
        var hasRecovery = false;
        var paramType = arrow.ParamType != null
            ? ConvertType(arrow.ParamType, typeVarEnv)
            : CreateMissingShapeRecoveryType(arrow.Span, DiagnosticMessages.ArrowTypeRequiresParameterType);
        hasRecovery |= ContainsErrorRecoveryType(paramType);

        var returnType = arrow.ReturnType != null
            ? ConvertType(arrow.ReturnType, typeVarEnv)
            : CreateMissingShapeRecoveryType(arrow.Span, DiagnosticMessages.ArrowTypeRequiresReturnType);
        hasRecovery |= ContainsErrorRecoveryType(returnType);

        if (hasRecovery)
        {
            return CreateErrorRecoveryType();
        }

        var effects = ResolveRequiredAbilities(arrow.RequiredEffects, typeVarEnv);
        return new TyFun
        {
            Params = [paramType],
            Result = returnType,
            Effects = effects
        };
    }

    private Type ConvertEffectfulType(EffectfulType effectful, Dictionary<string, Type> typeVarEnv)
    {
        var inputType = effectful.InputType != null
            ? ConvertType(effectful.InputType, typeVarEnv)
            : CreateMissingShapeRecoveryType(effectful.Span, DiagnosticMessages.EffectfulTypeRequiresInputType);

        var abilities = ResolveEffectfulAbilities(effectful, typeVarEnv);

        var outputType = effectful.OutputType != null
            ? ConvertType(effectful.OutputType, typeVarEnv)
            : BaseTypes.Unit;

        if (ContainsErrorRecoveryType(inputType) || ContainsErrorRecoveryType(outputType))
        {
            return CreateErrorRecoveryType();
        }

        return new TyFun
        {
            Params = [inputType],
            Result = outputType,
            Effects = abilities
        };
    }

    private EffectRow ResolveEffectfulAbilities(
        EffectfulType effectful,
        Dictionary<string, Type> typeVarEnv)
    {
        var declaredEffectPaths = effectful.EnumerateEffectPaths()
            .Select(NormalizeEffectPath)
            .Where(path => path.Count > 0)
            .ToList();
        if (declaredEffectPaths.Count == 0)
        {
            return EffectRow.Pure;
        }

        var resolvedAbilities = new List<EffectTag>(declaredEffectPaths.Count);
        var resolvedVariables = new List<EffectVariable>();
        for (var i = 0; i < declaredEffectPaths.Count; i++)
        {
            var path = declaredEffectPaths[i];
            if (path.Count == 1 &&
                typeVarEnv.TryGetValue(path[0], out var typeVar) &&
                typeVar is TyVar tyVar)
            {
                resolvedVariables.Add(new EffectVariable { Id = tyVar.Index });
                continue;
            }

            var abilitySymbolId = i < effectful.EffectSymbolIds.Count
                ? effectful.EffectSymbolIds[i]
                : (i == 0 ? effectful.SymbolId : SymbolId.None);
            resolvedAbilities.Add(ResolveEffectTag(abilitySymbolId, path));
        }

        return new EffectRow(resolvedAbilities, resolvedVariables);
    }

    private EffectRow ResolveRequiredAbilities(
        IReadOnlyList<EffectRequirementNode> requirements,
        Dictionary<string, Type> typeVarEnv)
    {
        var declaredPaths = requirements
            .Select(requirement => NormalizeEffectPath(requirement.Path))
            .Where(path => path.Count > 0)
            .ToList();
        if (declaredPaths.Count == 0)
        {
            return EffectRow.Pure;
        }

        var resolvedAbilities = new List<EffectTag>(declaredPaths.Count);
        var resolvedVariables = new List<EffectVariable>();
        for (var i = 0; i < declaredPaths.Count; i++)
        {
            var path = declaredPaths[i];
            if (path.Count == 1 &&
                typeVarEnv.TryGetValue(path[0], out var typeVar) &&
                typeVar is TyVar tyVar)
            {
                resolvedVariables.Add(new EffectVariable { Id = tyVar.Index });
                continue;
            }

            var abilitySymbolId = i < requirements.Count
                ? requirements[i].SymbolId
                : SymbolId.None;
            resolvedAbilities.Add(ResolveEffectTag(abilitySymbolId, path));
        }

        return new EffectRow(resolvedAbilities, resolvedVariables);
    }

    private Type ApplyRequiredAbilitiesToFunction(Type functionType, EffectRow abilities)
    {
        if (abilities.IsPure)
        {
            return functionType;
        }

        if (functionType is not TyFun fun)
        {
            return functionType;
        }

        var result = _substitution.Apply(fun.Result);
        if (result is TyFun nested)
        {
            return fun with
            {
                Result = ApplyRequiredAbilitiesToFunction(nested, abilities)
            };
        }

        return fun with
        {
            Effects = fun.Effects.Union(abilities)
        };
    }

    private EffectTag ResolveEffectTag(IReadOnlyList<string> abilityPath)
    {
        var normalizedPath = NormalizeEffectPath(abilityPath);
        if (normalizedPath.Count == 0)
        {
            return new EffectTag();
        }

        if (normalizedPath.Count > 1)
        {
            var resolved = _symbolTable.ResolvePathWithResult(normalizedPath);
            if (resolved.IsSuccess &&
                resolved.Kind == ResolutionKind.Effect &&
                resolved.SymbolId.IsValid &&
                _symbolTable.GetSymbol(resolved.SymbolId) is EffectSymbol resolvedEffect)
            {
                return new EffectTag(resolved.SymbolId, resolvedEffect.Name);
            }
        }

        var fullName = string.Join(WellKnownStrings.Separators.Path, normalizedPath);
        return new EffectTag { Name = fullName };
    }

    private static List<string> NormalizeEffectPath(IEnumerable<string> abilityPath)
    {
        return abilityPath
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim())
            .ToList();
    }

    private EffectTag ResolveEffectTag(SymbolId abilitySymbolId, IReadOnlyList<string> abilityPath)
    {
        if (abilitySymbolId.IsValid &&
            _symbolTable.GetSymbol(abilitySymbolId) is EffectSymbol abilitySymbol)
        {
            return new EffectTag(abilitySymbolId, abilitySymbol.Name);
        }

        return ResolveEffectTag(abilityPath);
    }

    private Type ConvertTupleType(TupleType tuple, Dictionary<string, Type> typeVarEnv)
    {
        var elements = tuple.Elements.Select(e => ConvertType(e, typeVarEnv)).ToList();
        return new TyTuple { Elements = elements };
    }
}
