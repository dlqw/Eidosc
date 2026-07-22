using Eidosc.Hir;
using Eidosc.Types;

namespace Eidosc.Mir;

/// <summary>
/// ADT constructor field layout collection and named field metadata registration.
/// </summary>
public sealed partial class MirBuilder
{
    private void CollectConstructorFieldLayouts(HirModule hirModule)
    {
        foreach (var adt in hirModule.Declarations.OfType<HirAdt>())
        {
            var adtNamedFieldOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
            var adtAmbiguousFields = new HashSet<string>(StringComparer.Ordinal);
            var adtFieldCoverage = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

            foreach (var ctor in adt.Constructors)
            {
                if (string.IsNullOrWhiteSpace(ctor.Name))
                {
                    continue;
                }

                var fields = new Dictionary<string, int>(StringComparer.Ordinal);
                for (var i = 0; i < ctor.Fields.Count; i++)
                {
                    var ordinalName = $"_{i}";
                    fields[ordinalName] = i;

                    var namedField = ctor.Fields[i].Name;
                    if (string.IsNullOrWhiteSpace(namedField) || namedField.StartsWith('_'))
                    {
                        continue;
                    }

                    fields[namedField] = i;
                    RegisterUniqueNamedFieldOrdinal(namedField, i);
                    RegisterNamedFieldOrdinalForAdt(
                        namedField,
                        i,
                        ctor.Name,
                        adtNamedFieldOrdinals,
                        adtAmbiguousFields,
                        adtFieldCoverage);
                }

                _constructorFieldOrderByName[ctor.Name] = fields;
            }

            RegisterAdtNamedFieldMetadata(adt, adtNamedFieldOrdinals, adtAmbiguousFields, adtFieldCoverage);

            // Generate ConstructorTypeLayout entries for LLVM struct-typed GEP
            CollectConstructorTypeLayouts(adt, adt.Constructors);
        }
    }

    /// <summary>
    /// 从 HirAdt 生成 ConstructorTypeLayout 并存入 _constructorLayouts。
    /// 这确保了即使 ADT 定义来自导入的标准库模块，
    /// MirModule.ConstructorLayouts 也能包含完整的布局信息。
    /// </summary>
    private void CollectConstructorTypeLayouts(HirAdt adt, List<HirCtor> constructors)
    {
        if (constructors.Count == 0)
            return;

        if (!adt.TypeId.IsValid)
            return;

        // Skip if already populated (e.g. from HirBuilder)
        if (_constructorLayouts.ContainsKey(adt.TypeId.Value))
            return;

        var typeName = adt.Name;
        if (adt.TypeParams.Count > 0)
        {
            var paramNames = adt.TypeParams.Select(p => p.Name);
            typeName = $"{typeName}_{string.Join("_", paramNames)}";
        }

        var isMultiCtor = constructors.Count > 1;

        var layouts = new List<ConstructorTypeLayout>(constructors.Count);
        foreach (var ctor in constructors)
        {
            if (string.IsNullOrWhiteSpace(ctor.Name))
                continue;

            var tagValue = isMultiCtor
                ? (uint)AdtConstructorTypeId.Compute(ctor.Name)
                : 0u;

            var fieldTypeIds = new List<TypeId>(ctor.Fields.Count);
            foreach (var field in ctor.Fields)
            {
                fieldTypeIds.Add(field.TypeId.IsValid ? field.TypeId : new TypeId(BaseTypes.IntId));
            }

            layouts.Add(new ConstructorTypeLayout
            {
                TypeName = typeName,
                ConstructorName = ctor.Name,
                TagValue = tagValue,
                RuntimeTypeId = ConstructorRuntimeTypeId.Compute(_symbolTable, ctor.SymbolId, ctor.Name),
                FieldTypeIds = fieldTypeIds
            });
        }

        if (layouts.Count == 0)
            return;

        // Store under the ADT's TypeId
        _constructorLayouts[adt.TypeId.Value] = layouts;

        // Also store under each constructor's SymbolId-resolved TypeId
        if (_symbolTable != null)
        {
            foreach (var ctor in constructors)
            {
                if (string.IsNullOrWhiteSpace(ctor.Name))
                    continue;

                var ctorTypeId = ResolveTypeIdFromSymbol(ctor.SymbolId);
                if (ctorTypeId.IsValid && !_constructorLayouts.ContainsKey(ctorTypeId.Value))
                {
                    _constructorLayouts[ctorTypeId.Value] = layouts;
                }
            }
        }
    }

    private TypeId ResolveTypeIdFromSymbol(SymbolId symbolId)
    {
        if (!symbolId.IsValid || _symbolTable == null)
            return TypeId.None;

        var symbol = _symbolTable.GetSymbol(symbolId);
        if (symbol?.TypeId is { IsValid: true } typeId)
        {
            return typeId;
        }

        return symbol is Symbols.CtorSymbol { OwnerAdt.IsValid: true } constructor &&
               _symbolTable.GetSymbol<Symbols.AdtSymbol>(constructor.OwnerAdt) is { TypeId.IsValid: true } owner
            ? owner.TypeId
            : TypeId.None;
    }

    private void RegisterUniqueNamedFieldOrdinal(string fieldName, int ordinal)
    {
        if (_ambiguousNamedField.Contains(fieldName))
        {
            return;
        }

        if (_uniqueNamedFieldOrdinal.TryGetValue(fieldName, out var existing) && existing != ordinal)
        {
            _uniqueNamedFieldOrdinal.Remove(fieldName);
            _ambiguousNamedField.Add(fieldName);
            return;
        }

        _uniqueNamedFieldOrdinal[fieldName] = ordinal;
    }

    private static void RegisterNamedFieldOrdinalForAdt(
        string fieldName,
        int ordinal,
        string ctorName,
        Dictionary<string, int> adtNamedFieldOrdinals,
        HashSet<string> adtAmbiguousFields,
        Dictionary<string, HashSet<string>> adtFieldCoverage)
    {
        if (!adtAmbiguousFields.Contains(fieldName))
        {
            if (adtNamedFieldOrdinals.TryGetValue(fieldName, out var existing) && existing != ordinal)
            {
                adtNamedFieldOrdinals.Remove(fieldName);
                adtAmbiguousFields.Add(fieldName);
            }
            else if (!adtNamedFieldOrdinals.ContainsKey(fieldName))
            {
                adtNamedFieldOrdinals[fieldName] = ordinal;
            }
        }

        if (!adtFieldCoverage.TryGetValue(fieldName, out var constructors))
        {
            constructors = new HashSet<string>(StringComparer.Ordinal);
            adtFieldCoverage[fieldName] = constructors;
        }

        constructors.Add(ctorName);
    }

    private void RegisterAdtNamedFieldMetadata(
        HirAdt adt,
        Dictionary<string, int> adtNamedFieldOrdinals,
        HashSet<string> adtAmbiguousFields,
        Dictionary<string, HashSet<string>> adtFieldCoverage)
    {
        if (!adt.TypeId.IsValid)
        {
            return;
        }

        var totalConstructors = adt.Constructors.Count(constructor => !string.IsNullOrWhiteSpace(constructor.Name));
        var partialFields = new HashSet<string>(StringComparer.Ordinal);
        var allFields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (fieldName, constructors) in adtFieldCoverage)
        {
            allFields.Add(fieldName);
            if (totalConstructors > 1 && constructors.Count < totalConstructors)
            {
                partialFields.Add(fieldName);
                adtNamedFieldOrdinals.Remove(fieldName);
            }
        }

        if (adtNamedFieldOrdinals.Count > 0)
        {
            _uniqueNamedFieldOrdinalByAdtType[adt.TypeId] = adtNamedFieldOrdinals;
        }

        if (adtAmbiguousFields.Count > 0)
        {
            _ambiguousNamedFieldByAdtType[adt.TypeId] = adtAmbiguousFields;
            foreach (var ambiguousField in adtAmbiguousFields)
            {
                allFields.Add(ambiguousField);
            }
        }

        if (partialFields.Count > 0)
        {
            _partialNamedFieldByAdtType[adt.TypeId] = partialFields;
            foreach (var partialField in partialFields)
            {
                allFields.Add(partialField);
            }
        }

        if (allFields.Count > 0)
        {
            _allNamedFieldByAdtType[adt.TypeId] = allFields;
            _adtDisplayNameByType[adt.TypeId] = string.IsNullOrWhiteSpace(adt.Name)
                ? adt.TypeId.ToString()
                : adt.Name;
        }
    }
}
