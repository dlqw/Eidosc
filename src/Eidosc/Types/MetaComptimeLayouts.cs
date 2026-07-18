using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Symbols;

namespace Eidosc.Types;

internal static partial class MetaComptimeIntrinsics
{
    private readonly record struct MetaLayoutFact(
        long Size,
        long Alignment,
        IReadOnlyList<long> Offsets,
        bool Complete);

    private static bool TryLayoutOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeTypeValue typeValue ||
            arguments[1] is not ComptimeStringValue target ||
            string.IsNullOrWhiteSpace(target.Value))
        {
            return Fail("meta.layout_of expects (Type, non-empty target triple)", out value, out reason);
        }

        var pointerSize = GetTargetPointerSize(target.Value);
        if (pointerSize == 0)
        {
            return Fail($"unsupported explicit layout target '{target.Value}'", out value, out reason);
        }

        var layout = GetLayout(typeValue.TypeRef, pointerSize, meta);
        if (!layout.Complete)
        {
            return Fail(
                $"layout fact for '{typeValue.TypeRef.Name}' is not complete in the reflection phase for target '{target.Value}'",
                out value,
                out reason);
        }

        value = Obj(
            "layout-info",
            ("target", new ComptimeStringValue(target.Value)),
            ("size", new ComptimeIntegerValue(layout.Size)),
            ("alignment", new ComptimeIntegerValue(layout.Alignment)),
            ("fieldOffsets", List(layout.Offsets.Select(static offset =>
                (ComptimeValue)new ComptimeIntegerValue(offset)))));
        reason = string.Empty;
        return true;
    }

    private static int GetTargetPointerSize(string target) =>
        target.Contains("64", StringComparison.Ordinal) ||
        target.Contains("aarch64", StringComparison.OrdinalIgnoreCase)
            ? 8
            : target.Contains("32", StringComparison.Ordinal) ||
              target.Contains("wasm32", StringComparison.OrdinalIgnoreCase)
                ? 4
                : 0;

    private static MetaLayoutFact GetLayout(MetaTypeRef type, int pointerSize, MetaComptimeContext meta)
    {
        var scalar = GetScalarLayout(type, pointerSize);
        if (scalar.Complete)
        {
            return scalar;
        }

        if (type.SymbolId.IsValid &&
            meta.SymbolTable.GetSymbol<AdtSymbol>(type.SymbolId) is { IsCStruct: true, CStructLayoutInfo: { } cLayout })
        {
            return new MetaLayoutFact(
                cLayout.TotalSize,
                cLayout.Alignment,
                cLayout.Fields.Select(static field => (long)field.Offset).ToArray(),
                true);
        }

        if (meta.Access.AvailableStage < ClauseStage.Layout)
        {
            return new MetaLayoutFact(0, 0, [], false);
        }

        if (type.SymbolId.IsValid && TryGetAdtAndCase(type.SymbolId, meta, out var root, out var casePath))
        {
            return GetAdtPayloadLayout(type, root, casePath, pointerSize, meta);
        }

        return new MetaLayoutFact(0, 0, [], false);
    }

    private static MetaLayoutFact GetScalarLayout(MetaTypeRef type, int pointerSize)
    {
        if (type.Kind is "reference" or "mutable-reference" or "shared-reference" or "raw-pointer" or "foreign-function" ||
            type.Name is "RawPtr" or "Ptr" or "Cfn")
        {
            return new MetaLayoutFact(pointerSize, pointerSize, [], true);
        }

        return type.Name switch
        {
            "Int" or "Int64" or "Float" or "Float64" => new MetaLayoutFact(8, 8, [], true),
            "Int32" or "Float32" or "Char" => new MetaLayoutFact(4, 4, [], true),
            "Int16" or "Float16" => new MetaLayoutFact(2, 2, [], true),
            "Int8" or "Bool" => new MetaLayoutFact(1, 1, [], true),
            "Unit" or "()" => new MetaLayoutFact(0, 1, [], true),
            _ => new MetaLayoutFact(0, 0, [], false)
        };
    }

    private static MetaLayoutFact GetAdtPayloadLayout(
        MetaTypeRef queriedType,
        AdtDef root,
        IReadOnlyList<CaseTypeDef> casePath,
        int pointerSize,
        MetaComptimeContext meta)
    {
        var variants = BuildAdtLayoutVariants(root, casePath);
        var typeArguments = BuildLayoutTypeArgumentMap(queriedType, meta.SymbolTable);
        if (variants.Count == 0)
        {
            return new MetaLayoutFact(0, 1, [], true);
        }

        var slotCount = variants.Max(static fields => fields.Count);
        var slotLayouts = new List<MetaLayoutFact>(slotCount);
        for (var slot = 0; slot < slotCount; slot++)
        {
            var candidates = variants
                .Where(fields => slot < fields.Count)
                .Select(fields => GetFieldStorageLayout(fields[slot], pointerSize, meta, typeArguments))
                .ToArray();
            if (candidates.Length == 0 || candidates.Any(static layout => !layout.Complete))
            {
                return new MetaLayoutFact(0, 0, [], false);
            }

            var size = candidates.Max(static layout => layout.Size);
            var alignment = candidates.Max(static layout => layout.Alignment);
            slotLayouts.Add(new MetaLayoutFact(size, alignment, [], true));
        }

        var offsets = new List<long>(slotLayouts.Count);
        long current = 0;
        long aggregateAlignment = 1;
        foreach (var slot in slotLayouts)
        {
            aggregateAlignment = Math.Max(aggregateAlignment, slot.Alignment);
            current = AlignUp(current, slot.Alignment);
            offsets.Add(current);
            current += slot.Size;
        }

        return new MetaLayoutFact(AlignUp(current, aggregateAlignment), aggregateAlignment, offsets, true);
    }

    private static List<List<TypeNode>> BuildAdtLayoutVariants(
        AdtDef root,
        IReadOnlyList<CaseTypeDef> casePath)
    {
        var inherited = root.Fields
            .Where(static field => field.Type != null)
            .Select(static field => field.Type!)
            .ToList();
        foreach (var caseType in casePath)
        {
            inherited.AddRange(caseType.Fields
                .Where(static field => field.Type != null)
                .Select(static field => field.Type!));
        }

        var activeCases = casePath.Count == 0 ? root.Cases : casePath[^1].Cases;
        if (activeCases.Count > 0)
        {
            var variants = new List<List<TypeNode>>();
            foreach (var child in activeCases)
            {
                CollectCaseLayoutVariants(child, inherited, variants);
            }
            return variants;
        }

        if (casePath.Count > 0)
        {
            return [BuildClosedCaseLeafLayoutVariant(casePath[^1], inherited)];
        }

        if (root.Constructors.Count > 0)
        {
            return root.Constructors.Select(constructor =>
            {
                var fields = new List<TypeNode>(inherited);
                fields.AddRange(constructor.PositionalArgs);
                fields.AddRange(constructor.NamedArgs
                    .Where(static field => field.Type != null)
                    .Select(static field => field.Type!));
                return fields;
            }).ToList();
        }

        return [inherited];
    }

    private static void CollectCaseLayoutVariants(
        CaseTypeDef current,
        IReadOnlyList<TypeNode> inherited,
        List<List<TypeNode>> variants)
    {
        var effective = new List<TypeNode>(inherited);
        effective.AddRange(current.Fields
            .Where(static field => field.Type != null)
            .Select(static field => field.Type!));
        if (current.Cases.Count == 0)
        {
            variants.Add(BuildClosedCaseLeafLayoutVariant(current, effective));
            return;
        }

        foreach (var child in current.Cases)
        {
            CollectCaseLayoutVariants(child, effective, variants);
        }
    }

    private static List<TypeNode> BuildClosedCaseLeafLayoutVariant(
        CaseTypeDef leaf,
        IReadOnlyList<TypeNode> effectiveNamedFields)
    {
        var variant = new List<TypeNode>(leaf.PositionalFields.Count + effectiveNamedFields.Count);
        variant.AddRange(leaf.PositionalFields);
        variant.AddRange(effectiveNamedFields);
        return variant;
    }

    private static MetaLayoutFact GetFieldStorageLayout(
        TypeNode fieldType,
        int pointerSize,
        MetaComptimeContext meta,
        IReadOnlyDictionary<SymbolId, MetaTypeRef> typeArguments)
    {
        var type = CreateTypeRef(fieldType, meta.SymbolTable);
        if (type.SymbolId.IsValid &&
            meta.SymbolTable.GetSymbol<TypeParamSymbol>(type.SymbolId) is
                { ParameterKind: GenericParameterKind.Type } &&
            typeArguments.TryGetValue(type.SymbolId, out var concreteType))
        {
            type = concreteType;
        }

        var scalar = GetScalarLayout(type, pointerSize);
        if (scalar.Complete)
        {
            return scalar;
        }

        if (type.SymbolId.IsValid &&
            meta.SymbolTable.GetSymbol<AdtSymbol>(type.SymbolId) is { IsCStruct: true, CStructLayoutInfo: { } cLayout })
        {
            return new MetaLayoutFact(cLayout.TotalSize, cLayout.Alignment, [], true);
        }

        return type.Kind is "nominal" or "alias" or "closed-sum" or "case" or "foreign-nominal"
            ? new MetaLayoutFact(pointerSize, pointerSize, [], true)
            : new MetaLayoutFact(0, 0, [], false);
    }

    private static IReadOnlyDictionary<SymbolId, MetaTypeRef> BuildLayoutTypeArgumentMap(
        MetaTypeRef queriedType,
        SymbolTable symbolTable)
    {
        if (!queriedType.SymbolId.IsValid || queriedType.GenericArguments is not { Count: > 0 } arguments)
        {
            return new Dictionary<SymbolId, MetaTypeRef>();
        }

        var parameterIds = symbolTable.GetClosedCaseEffectiveGenericParameterIds(queriedType.SymbolId);
        var mapped = new Dictionary<SymbolId, MetaTypeRef>();
        for (var index = 0; index < parameterIds.Count && index < arguments.Count; index++)
        {
            var parameterId = parameterIds[index];
            var argument = arguments[index];
            if (symbolTable.GetSymbol<TypeParamSymbol>(parameterId) is { ParameterKind: GenericParameterKind.Type } &&
                argument.Type is not null)
            {
                mapped[parameterId] = argument.Type;
            }
        }

        return mapped;
    }

    private static long AlignUp(long offset, long alignment) =>
        alignment <= 1 ? offset : checked((offset + alignment - 1) / alignment * alignment);
}
