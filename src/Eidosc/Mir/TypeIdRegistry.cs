using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Types;
using Eidosc.Semantic;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Types;
using DrivenType = Eidosc.Types.Type;

namespace Eidosc.Mir;

/// <summary>
/// Manages dynamic TypeId allocation, caching, and constructor layout collection.
/// Extracted from HirBuilder to separate type resolution concerns.
/// </summary>
internal sealed class TypeIdRegistry
{
    private readonly SymbolTable _symbolTable;
    private readonly TypeInferer? _typeInferer;

    private readonly Dictionary<string, TypeId> _typeIdCache = new();
    private readonly Dictionary<TypeDescriptor, TypeId> _typeIdByDescriptor = new(TypeDescriptorStructuralComparer.Instance);
    private readonly Dictionary<int, TypeDescriptor> _typeDescriptorById = new();
    private readonly Dictionary<SymbolId, TypeId> _typeParameterTypeIdsBySymbol = [];
    private readonly Dictionary<int, List<ConstructorTypeLayout>> _constructorLayouts = [];
    private readonly HashSet<int> _layoutCollectedAdtSymbols = [];
    private readonly HashSet<TypeId> _copyLikeTypeIds = [];
    private int _nextDynamicTypeId = 1000;

    public IReadOnlySet<TypeId> CopyLikeTypeIds => _copyLikeTypeIds;
    public IReadOnlyDictionary<TypeId, string> DynamicTypeKeys =>
        _typeIdCache.ToDictionary(entry => entry.Value, entry => entry.Key);
    public IReadOnlyDictionary<int, List<ConstructorTypeLayout>> ConstructorLayouts => _constructorLayouts;
    public IReadOnlyDictionary<int, TypeDescriptor> TypeDescriptors => _typeDescriptorById;

    public TypeIdRegistry(SymbolTable symbolTable, TypeInferer? typeInferer)
    {
        _symbolTable = symbolTable;
        _typeInferer = typeInferer;
        _nextDynamicTypeId = ComputeInitialDynamicTypeId(symbolTable);
    }

    public void Clear()
    {
        _copyLikeTypeIds.Clear();
        _constructorLayouts.Clear();
        _layoutCollectedAdtSymbols.Clear();
        _typeIdCache.Clear();
        _typeIdByDescriptor.Clear();
        _typeDescriptorById.Clear();
        _typeParameterTypeIdsBySymbol.Clear();
        _nextDynamicTypeId = ComputeInitialDynamicTypeId(_symbolTable);
    }

    public void RegisterTypeParameterTypeId(SymbolId symbolId, TypeId typeId)
    {
        if (symbolId.IsValid && typeId.IsValid)
        {
            _typeParameterTypeIdsBySymbol[symbolId] = typeId;
        }
    }

    private static int ComputeInitialDynamicTypeId(SymbolTable symbolTable)
    {
        var nextTypeId = 1000;
        foreach (var symbol in symbolTable.Symbols.Values)
        {
            if (symbol.Id.IsValid && symbol.Id.Value >= nextTypeId)
            {
                nextTypeId = symbol.Id.Value + 1;
            }

            if (symbol.TypeId.IsValid && symbol.TypeId.Value >= nextTypeId)
            {
                nextTypeId = symbol.TypeId.Value + 1;
            }
        }

        return nextTypeId;
    }

    public TypeId GetOrCreateDynamicTypeId(string typeKey)
    {
        if (TypeKeyParsing.TryParseTypeDescriptor(typeKey, out var descriptor))
        {
            return GetOrCreateDynamicTypeId(descriptor);
        }

        if (_typeIdCache.TryGetValue(typeKey, out var existingId))
        {
            return existingId;
        }

        var newId = new TypeId(_nextDynamicTypeId++);
        _typeIdCache[typeKey] = newId;
        return newId;
    }

    public TypeId GetOrCreateDynamicTypeId(TypeDescriptor descriptor)
    {
        if (_typeIdByDescriptor.TryGetValue(descriptor, out var existingId))
        {
            return existingId;
        }

        var typeKey = descriptor.ToString();
        if (_typeIdCache.TryGetValue(typeKey, out existingId))
        {
            _typeIdByDescriptor[descriptor] = existingId;
            _typeDescriptorById[existingId.Value] = descriptor;
            return existingId;
        }

        var newId = new TypeId(_nextDynamicTypeId++);
        _typeIdCache[typeKey] = newId;
        _typeIdByDescriptor[descriptor] = newId;
        _typeDescriptorById[newId.Value] = descriptor;
        return newId;
    }

    private void RegisterNominalTyConDescriptor(TypeId typeId, SymbolId symbolId, string? typeName)
    {
        if (!typeId.IsValid || BaseTypes.IsBuiltIn(typeId))
        {
            return;
        }

        var constructorKey = symbolId.IsValid
            ? TypeConstructorKey.FromSymbol(symbolId)
            : TypeConstructorKey.FromTypeId(typeId);
        var descriptor = new TypeDescriptor.TyCon(constructorKey, []);
        _typeDescriptorById.TryAdd(typeId.Value, descriptor);
        _typeIdByDescriptor.TryAdd(descriptor, typeId);
        _typeIdCache.TryAdd(descriptor.ToString(), typeId);
    }

    public TypeId ResolveDeclaredTypeId(SymbolId symbolId)
    {
        if (!symbolId.IsValid)
        {
            return TypeId.None;
        }

        return _symbolTable.GetSymbol(symbolId)?.TypeId is { } typeId && typeId.IsValid
            ? typeId
            : TypeId.None;
    }

    public TypeId GetTypeId(EidosAstNode? node)
    {
        if (node == null) return TypeId.None;

        if (TryGetResolvedInferredType(node, out var inferredType))
        {
            var typeId = GetTypeTypeId(inferredType);
            RegisterCopyLikeTypeId(inferredType, typeId);
            return typeId;
        }

        if (node is LiteralExpr lit)
        {
            return lit.Kind switch
            {
                Ast.Expressions.LiteralKind.Integer => new TypeId(BaseTypes.IntId),
                Ast.Expressions.LiteralKind.Float => new TypeId(BaseTypes.FloatId),
                Ast.Expressions.LiteralKind.String => new TypeId(BaseTypes.StringId),
                Ast.Expressions.LiteralKind.Char => new TypeId(BaseTypes.CharId),
                Ast.Expressions.LiteralKind.Boolean => new TypeId(BaseTypes.BoolId),
                Ast.Expressions.LiteralKind.Unit => new TypeId(BaseTypes.UnitId),
                _ => new TypeId(BaseTypes.UnitId)
            };
        }

        if (node is TypePath typePath)
        {
            return GetTypePathTypeId(typePath);
        }

        if (node is TupleType tupleType)
        {
            return GetTupleTypeNodeTypeId(tupleType);
        }

        if (node is ArrowType arrowType)
        {
            return GetArrowTypeNodeTypeId(arrowType);
        }

        return TypeId.None;
    }

    private TypeId GetTupleTypeNodeTypeId(TupleType tupleType)
    {
        if (tupleType.Elements.Count == 0)
        {
            return new TypeId(BaseTypes.UnitId);
        }

        var elementTypeIds = tupleType.Elements.Select(GetTypeId).ToArray();
        if (elementTypeIds.Any(static typeId => !typeId.IsValid))
        {
            return TypeId.None;
        }

        return GetOrCreateDynamicTypeId(new TypeDescriptor.Tuple(elementTypeIds));
    }

    private TypeId GetArrowTypeNodeTypeId(ArrowType arrowType)
    {
        var parameterType = GetTypeId(arrowType.ParamType);
        var returnType = GetTypeId(arrowType.ReturnType);
        if (!parameterType.IsValid || !returnType.IsValid)
        {
            return TypeId.None;
        }

        return GetOrCreateDynamicTypeId(new TypeDescriptor.Function([parameterType], returnType, null));
    }

    private TypeId GetTypePathTypeId(TypePath typePath)
    {
        if (typePath.SymbolId.IsValid &&
            _symbolTable.GetSymbol(typePath.SymbolId) is TypeParamSymbol)
        {
            if (_typeParameterTypeIdsBySymbol.TryGetValue(typePath.SymbolId, out var typeParameterTypeId))
            {
                return typeParameterTypeId;
            }

            return new TypeId(typePath.SymbolId.Value);
        }

        var typeArgIds = typePath.TypeArgs.Select(GetTypeId).ToArray();
        if (typeArgIds.Length > 0)
        {
            if (!TryGetTypePathConstructorKey(typePath, out var constructorKey))
            {
                return TypeId.None;
            }

            return GetOrCreateDynamicTypeId(new TypeDescriptor.TyCon(constructorKey, typeArgIds));
        }

        var builtInId = BaseTypes.GetBuiltInTypeId(typePath.TypeName);
        if (builtInId.IsValid)
        {
            return builtInId;
        }

        if (typePath.SymbolId.IsValid &&
            _symbolTable.GetSymbol(typePath.SymbolId) is { TypeId: { IsValid: true } symbolTypeId })
        {
            RegisterNominalTyConDescriptor(symbolTypeId, typePath.SymbolId, typePath.TypeName);
            return symbolTypeId;
        }

        if (!string.IsNullOrWhiteSpace(typePath.TypeName) &&
            _symbolTable.LookupType(typePath.TypeName) is { } typeSymbolId &&
            _symbolTable.GetSymbol(typeSymbolId) is { TypeId: { IsValid: true } namedTypeId })
        {
            RegisterNominalTyConDescriptor(namedTypeId, typeSymbolId, typePath.TypeName);
            return namedTypeId;
        }

        return TypeId.None;
    }

    public bool TryGetResolvedInferredType(EidosAstNode? node, out DrivenType resolvedType)
    {
        if (node?.InferredType is not DrivenType inferredType)
        {
            resolvedType = null!;
            return false;
        }

        resolvedType = _typeInferer != null
            ? _typeInferer.Substitution.Apply(inferredType)
            : inferredType;
        return true;
    }

    public TypeId GetTypeTypeId(DrivenType type)
    {
        var resolvedType = _typeInferer != null
            ? _typeInferer.Substitution.Apply(type)
            : type;
        return GetTypeTypeIdCore(resolvedType);
    }

    private TypeId GetTypeTypeIdCore(DrivenType type)
    {
        if (type is TyCon { Args.Count: 0 } builtinTyCon)
        {
            var builtInId = BaseTypes.GetBuiltInTypeId(builtinTyCon.Name);
            if (builtInId.IsValid)
            {
                return builtInId;
            }
        }

        if (type.Id.IsValid)
        {
            if (type is TyCon { } concreteTyCon && !NeedsDynamicTyConTypeId(concreteTyCon))
            {
                RegisterNominalTyConDescriptor(type.Id, concreteTyCon.Symbol, concreteTyCon.Name);
            }

            if (type is not TyCon tyCon || !NeedsDynamicTyConTypeId(tyCon))
            {
                return type.Id;
            }
        }

        return type switch
        {
            TyCon tyCon => GetTyConTypeId(tyCon),
            TyVar tyVar => GetTyVarTypeId(tyVar),
            TyFun tyFun => GetTyFunTypeId(tyFun),
            TyTuple tyTuple => GetTyTupleTypeId(tyTuple),
            TyRef tyRef => GetTyRefTypeId(tyRef),
            TyMutRef tyMutRef => GetTyMutRefTypeId(tyMutRef),
            TyShared tyShared => GetTySharedTypeId(tyShared),
            EffectRow => TypeId.None,
            EffectTag => TypeId.None,
            _ => throw new System.Diagnostics.UnreachableException()
        };
    }

    public TypeId GetFunctionTypeId(IReadOnlyList<TypeId> parameterTypes, TypeId returnType, string? abilities = null)
    {
        if (!returnType.IsValid || parameterTypes.Count == 0 || parameterTypes.Any(static typeId => !typeId.IsValid))
        {
            return TypeId.None;
        }

        return GetOrCreateDynamicTypeId(new TypeDescriptor.Function([.. parameterTypes], returnType, abilities));
    }

    public void CollectConstructorLayoutsFromAdtDef(AdtDef adt)
    {
        if (_layoutCollectedAdtSymbols.Contains(adt.SymbolId.Value))
            return;

        _layoutCollectedAdtSymbols.Add(adt.SymbolId.Value);

        var typeName = adt.Name;
        if (adt.TypeParams.Count > 0)
        {
            var paramNames = adt.TypeParams.Select(p => p.Name);
            typeName = $"{typeName}_{string.Join("_", paramNames)}";
        }

        var layouts = new List<ConstructorTypeLayout>(adt.Constructors.Count);
        var isMultiCtor = adt.Constructors.Count > 1;

        foreach (var ctor in adt.Constructors)
        {
            var tagValue = isMultiCtor
                ? (uint)AdtConstructorTypeId.Compute(ctor.Name)
                : 0u;

            var fieldTypeIds = new List<TypeId>();
            foreach (var arg in ctor.PositionalArgs)
            {
                fieldTypeIds.Add(GetTypeId(arg));
            }
            foreach (var arg in ctor.NamedArgs)
            {
                fieldTypeIds.Add(arg.Type != null ? GetTypeId(arg.Type) : new TypeId(BaseTypes.IntId));
            }

            layouts.Add(new ConstructorTypeLayout
            {
                TypeName = typeName,
                ConstructorName = ctor.Name,
                TagValue = tagValue,
                RuntimeTypeId = ComputeConstructorRuntimeTypeId(ctor.SymbolId, ctor.Name),
                FieldTypeIds = fieldTypeIds
            });
        }

        if (layouts.Count == 0)
            return;

        var adtTypeId = ResolveDeclaredTypeId(adt.SymbolId);
        if (adtTypeId.IsValid)
        {
            _constructorLayouts[adtTypeId.Value] = layouts;
        }

        foreach (var ctor in adt.Constructors)
        {
            var ctorTypeId = ResolveDeclaredTypeId(ctor.SymbolId);
            if (ctorTypeId.IsValid && !_constructorLayouts.ContainsKey(ctorTypeId.Value))
            {
                _constructorLayouts[ctorTypeId.Value] = layouts;
            }
        }

        var typeSymbolId = _symbolTable.LookupType(adt.Name);
        if (typeSymbolId != null)
        {
            var namedTypeId = ResolveDeclaredTypeId(typeSymbolId.Value);
            if (namedTypeId.IsValid && !_constructorLayouts.ContainsKey(namedTypeId.Value))
            {
                _constructorLayouts[namedTypeId.Value] = layouts;
            }
        }
    }

    private void RegisterCopyLikeTypeId(DrivenType type, TypeId typeId)
    {
        if (!typeId.IsValid || !IsCopyLikeGenericType(type))
        {
            return;
        }

        _copyLikeTypeIds.Add(typeId);
    }

    private bool IsCopyLikeGenericType(DrivenType type)
    {
        return type switch
        {
            TyVar { Instance: not null } tyVar => IsCopyLikeGenericType(tyVar.Instance),
            TyVar { Index: var index } => HasCopyLikeConstraint(index),
            _ => false
        };
    }

    private bool HasCopyLikeConstraint(int typeVarIndex)
    {
        if (_typeInferer == null)
        {
            return false;
        }

        var constraints = _typeInferer.ConstraintGenerator.Constraints.GetTraitConstraintsForVar(typeVarIndex);
        for (var i = 0; i < constraints.Count; i++)
        {
            var traitName = constraints[i].TraitName;
            if (string.Equals(traitName, "Copy", StringComparison.Ordinal) ||
                string.Equals(traitName, BuiltinTraits.TraitNames.Clone, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private void CollectConstructorLayouts(TypeId adtTypeId, TyCon tyCon)
    {
        AdtSymbol? adtSymbol = null;
        if (tyCon.Symbol.IsValid && _symbolTable.Symbols.TryGetValue(tyCon.Symbol, out var sym))
        {
            adtSymbol = sym as AdtSymbol;
        }

        if (adtSymbol == null && !string.IsNullOrEmpty(tyCon.Name))
        {
            var typeSymbolId = _symbolTable.LookupType(tyCon.Name);
            if (typeSymbolId != null &&
                _symbolTable.Symbols.TryGetValue(typeSymbolId.Value, out var typeSym))
            {
                adtSymbol = typeSym as AdtSymbol;
            }
        }

        if (adtSymbol == null || adtSymbol.Constructors.Count == 0)
            return;

        if (!_layoutCollectedAdtSymbols.Add(adtSymbol.Id.Value))
            return;

        var typeName = tyCon.Name;
        if (tyCon.Args.Count > 0)
        {
            var argNames = tyCon.Args.Select(a =>
            {
                if (a is TyCon argCon) return argCon.Name;
                return $"T{a.Id.Value}";
            });
            typeName = $"{typeName}_{string.Join("_", argNames)}";
        }

        var layouts = new List<ConstructorTypeLayout>(adtSymbol.Constructors.Count);
        var isMultiCtor = adtSymbol.Constructors.Count > 1;

        foreach (var ctorSymbolId in adtSymbol.Constructors)
        {
            if (_symbolTable.Symbols.TryGetValue(ctorSymbolId, out var ctorSym) &&
                ctorSym is CtorSymbol ctor)
            {
                var tagValue = isMultiCtor
                    ? (uint)AdtConstructorTypeId.Compute(ctor.Name)
                    : 0u;

                layouts.Add(new ConstructorTypeLayout
                {
                    TypeName = typeName,
                    ConstructorName = ctor.Name,
                    TagValue = tagValue,
                    RuntimeTypeId = ComputeConstructorRuntimeTypeId(ctor.Id, ctor.Name),
                    FieldTypeIds = [.. ctor.PositionalArgs]
                });
            }
        }

        if (layouts.Count > 0)
        {
            _constructorLayouts[adtTypeId.Value] = layouts;
        }
    }

    private int ComputeConstructorRuntimeTypeId(SymbolId constructorSymbolId, string constructorName)
    {
        return ConstructorRuntimeTypeId.Compute(_symbolTable, constructorSymbolId, constructorName);
    }

    private static bool NeedsDynamicTyConTypeId(TyCon tyCon)
    {
        return tyCon.ConstructorVarIndex.HasValue || tyCon.Args.Count > 0;
    }

    private TypeId GetTyConTypeId(TyCon tyCon)
    {
        if (NeedsDynamicTyConTypeId(tyCon))
        {
            var typeArgs = tyCon.Args.Select(GetTypeTypeId).ToArray();
            if (!TryGetTyConKey(tyCon, out var constructorKey))
            {
                return TypeId.None;
            }

            var typeId = GetOrCreateDynamicTypeId(new TypeDescriptor.TyCon(constructorKey, typeArgs));
            CollectConstructorLayouts(typeId, tyCon);
            return typeId;
        }

        var builtInId = BaseTypes.GetBuiltInTypeId(tyCon.Name);
        if (builtInId.IsValid)
        {
            return builtInId;
        }

        if (tyCon.Symbol.IsValid)
        {
            if (_symbolTable.Symbols.TryGetValue(tyCon.Symbol, out var symbol))
            {
                if (symbol.TypeId.IsValid)
                {
                    RegisterNominalTyConDescriptor(symbol.TypeId, tyCon.Symbol, tyCon.Name);
                    CollectConstructorLayouts(symbol.TypeId, tyCon);
                    return symbol.TypeId;
                }
            }
        }

        if (!string.IsNullOrEmpty(tyCon.Name))
        {
            var typeSymbol = _symbolTable.LookupType(tyCon.Name);
            if (typeSymbol != null && _symbolTable.Symbols.TryGetValue(typeSymbol.Value, out var sym))
            {
                if (sym.TypeId.IsValid)
                {
                    CollectConstructorLayouts(sym.TypeId, tyCon);
                    return sym.TypeId;
                }
            }
        }

        return TypeId.None;
    }

    private bool TryGetTypePathConstructorKey(TypePath typePath, out TypeConstructorKey key)
    {
        key = default;
        if (typePath.SymbolId.IsValid)
        {
            key = TypeConstructorKey.FromSymbol(typePath.SymbolId);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(typePath.TypeName) &&
            _symbolTable.LookupType(typePath.TypeName) is { } typeSymbolId)
        {
            key = TypeConstructorKey.FromSymbol(typeSymbolId);
            return true;
        }

        return false;
    }

    private bool TryGetTyConKey(TyCon tyCon, out TypeConstructorKey key)
    {
        key = default;
        if (tyCon.ConstructorVarIndex.HasValue)
        {
            key = TypeConstructorKey.FromVariable(tyCon.ConstructorVarIndex.Value);
            return true;
        }

        if (tyCon.Symbol.IsValid)
        {
            key = TypeConstructorKey.FromSymbol(tyCon.Symbol);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(tyCon.Name) &&
            _symbolTable.LookupType(tyCon.Name) is { } typeSymbolId)
        {
            key = TypeConstructorKey.FromSymbol(typeSymbolId);
            return true;
        }

        if (tyCon.Id.IsValid)
        {
            key = BaseTypes.IsBuiltIn(tyCon.Id)
                ? TypeConstructorKey.FromBuiltin(tyCon.Id)
                : TypeConstructorKey.FromTypeId(tyCon.Id);
            return true;
        }

        return false;
    }

    private TypeId GetTyVarTypeId(TyVar tyVar)
    {
        if (tyVar.Instance != null)
        {
            return GetTypeTypeId(tyVar.Instance);
        }

        return GetOrCreateDynamicTypeId(new TypeDescriptor.TypeVar(tyVar.Index));
    }

    private TypeId GetTyFunTypeId(TyFun tyFun)
    {
        var paramTypes = tyFun.Params.Select(GetTypeTypeId).ToArray();
        var resultType = GetTypeTypeId(tyFun.Result);
        var abilityStr = tyFun.Effects.ToString();

        return GetOrCreateDynamicTypeId(new TypeDescriptor.Function(paramTypes, resultType, abilityStr));
    }

    private TypeId GetTyTupleTypeId(TyTuple tyTuple)
    {
        var elementTypes = tyTuple.Elements.Select(GetTypeTypeId).ToArray();
        return GetOrCreateDynamicTypeId(new TypeDescriptor.Tuple(elementTypes));
    }

    private TypeId GetTyRefTypeId(TyRef tyRef)
    {
        var innerType = GetTypeTypeId(tyRef.Inner);
        return GetOrCreateDynamicTypeId(new TypeDescriptor.Ref(innerType));
    }

    private TypeId GetTyMutRefTypeId(TyMutRef tyMutRef)
    {
        var innerType = GetTypeTypeId(tyMutRef.Inner);
        return GetOrCreateDynamicTypeId(new TypeDescriptor.MutRef(innerType));
    }

    private TypeId GetTySharedTypeId(TyShared tyShared)
    {
        var innerType = GetTypeTypeId(tyShared.Inner);
        return GetOrCreateDynamicTypeId(new TypeDescriptor.Shared(innerType));
    }

}
