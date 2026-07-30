namespace Eidosc.Mir;

/// <summary>
/// Selects nominal ADT types that can be represented solely by their runtime
/// constructor tag. Layout metadata from older record paths can omit named
/// fields, so observed payload-bearing constructor calls conservatively veto
/// scalarization.
/// </summary>
public static class PayloadlessAdtRepresentationAnalysis
{
    public static IReadOnlySet<int> Analyze(MirModule module)
    {
        var candidates = module.ConstructorLayouts
            .Where(static entry => entry.Value.Count > 0 &&
                                   entry.Value.All(static layout => layout.FieldTypeIds.Count == 0))
            .Select(static entry => entry.Key)
            .ToHashSet();
        var structurallyPayloadlessTypes = candidates.ToHashSet();
        var vetoedTypes = new HashSet<int>();
        var payloadBearingConstructorFamilies = module.ConstructorLayouts
            .Where(static entry => entry.Value.Any(static layout => layout.FieldTypeIds.Count > 0))
            .Select(entry => TryGetConstructorFamily(entry.Key))
            .Where(static family => family != null)
            .Select(static family => family!)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var function in module.Functions)
        {
            var instructions = function.BasicBlocks
                .SelectMany(static block => block.Instructions)
                .ToArray();
            var payloadBearingLocals = new HashSet<LocalId>();
            foreach (var call in instructions.OfType<MirCall>())
            {
                if (call.Arguments.Count == 0 ||
                    call.Function is not MirFunctionRef { SymbolKind: Symbols.SymbolKind.Constructor } constructor)
                {
                    continue;
                }

                var constructedType = call.Target is { TypeId.IsValid: true }
                    ? call.Target.TypeId
                    : constructor.TypeId;
                if (constructedType.IsValid)
                {
                    candidates.Remove(constructedType.Value);
                    vetoedTypes.Add(constructedType.Value);
                    AddPayloadBearingFamily(constructedType);
                }

                if (constructor.TypeId.IsValid)
                {
                    candidates.Remove(constructor.TypeId.Value);
                    vetoedTypes.Add(constructor.TypeId.Value);
                    AddPayloadBearingFamily(constructor.TypeId);
                }

                if (constructedType.IsValid &&
                    module.ConstructorLayouts.TryGetValue(constructedType.Value, out var layouts) &&
                    layouts.FirstOrDefault(layout =>
                        string.Equals(layout.ConstructorName, constructor.Name, StringComparison.Ordinal)) is { } layout)
                {
                    for (var index = 0; index < layout.FieldTypeIds.Count && index < call.Arguments.Count; index++)
                    {
                        var expectedTypeId = layout.FieldTypeIds[index];
                        var actualTypeId = call.Arguments[index].TypeId;
                        if (!expectedTypeId.IsValid ||
                            !structurallyPayloadlessTypes.Contains(expectedTypeId.Value) ||
                            !actualTypeId.IsValid ||
                            !module.TypeDescriptors.TryGetValue(actualTypeId.Value, out var actualDescriptor) ||
                            actualDescriptor is not Types.TypeDescriptor.Shared)
                        {
                            continue;
                        }

                        candidates.Remove(expectedTypeId.Value);
                        vetoedTypes.Add(expectedTypeId.Value);
                        AddPayloadBearingFamily(expectedTypeId);
                    }
                }

                if (call.Target is MirPlace { Kind: PlaceKind.Local, Local: var targetLocal })
                {
                    payloadBearingLocals.Add(targetLocal);
                }
            }

            var payloadChanged = true;
            while (payloadChanged)
            {
                payloadChanged = false;
                foreach (var instruction in instructions)
                {
                    LocalId? source = instruction switch
                    {
                        MirLoad
                        {
                            Source: MirPlace { Kind: PlaceKind.Local, Local: var local }
                        } => local,
                        MirMove
                        {
                            Source: MirPlace { Kind: PlaceKind.Local, Local: var local }
                        } => local,
                        MirAssign
                        {
                            Source: MirPlace { Kind: PlaceKind.Local, Local: var local }
                        } => local,
                        MirCaseInject
                        {
                            Operand: MirPlace { Kind: PlaceKind.Local, Local: var local }
                        } => local,
                        _ => null
                    };
                    if (source is not { } sourceLocal || !payloadBearingLocals.Contains(sourceLocal))
                    {
                        continue;
                    }

                    var target = instruction switch
                    {
                        MirLoad { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
                        MirMove { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
                        MirAssign { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
                        MirCaseInject { Target: MirPlace { Kind: PlaceKind.Local, Local: var local } } => local,
                        _ => (LocalId?)null
                    };
                    if (target is { } targetLocal)
                    {
                        payloadChanged |= payloadBearingLocals.Add(targetLocal);
                    }

                    if (instruction is MirCaseInject injection)
                    {
                        var sourceTypeIds = new HashSet<int>();
                        if (injection.SourceTypeId.IsValid)
                        {
                            sourceTypeIds.Add(injection.SourceTypeId.Value);
                        }

                        if (injection.Operand.TypeId.IsValid)
                        {
                            sourceTypeIds.Add(injection.Operand.TypeId.Value);
                        }

                        foreach (var sourceTypeId in sourceTypeIds)
                        {
                            candidates.Remove(sourceTypeId);
                            vetoedTypes.Add(sourceTypeId);
                            AddPayloadBearingFamily(new TypeId(sourceTypeId));
                        }

                        if (injection.TargetTypeId.IsValid)
                        {
                            candidates.Remove(injection.TargetTypeId.Value);
                            vetoedTypes.Add(injection.TargetTypeId.Value);
                        }

                        if (injection.Target is MirPlace { TypeId.IsValid: true } targetPlace)
                        {
                            candidates.Remove(targetPlace.TypeId.Value);
                            vetoedTypes.Add(targetPlace.TypeId.Value);
                        }
                    }
                }
            }
        }

        foreach (var candidate in candidates.ToArray())
        {
            if (TryGetConstructorFamily(candidate) is { } family &&
                payloadBearingConstructorFamilies.Contains(family))
            {
                candidates.Remove(candidate);
                vetoedTypes.Add(candidate);
            }
        }

        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var injection in module.Functions
                         .SelectMany(static function => function.BasicBlocks)
                         .SelectMany(static block => block.Instructions)
                         .OfType<MirCaseInject>())
            {
                var sourceTypeId = injection.SourceTypeId.IsValid
                    ? injection.SourceTypeId
                    : injection.Operand.TypeId;
                var targetTypeIds = new HashSet<int>();
                if (injection.TargetTypeId.IsValid)
                {
                    targetTypeIds.Add(injection.TargetTypeId.Value);
                }

                if (injection.Target is MirPlace { TypeId.IsValid: true } targetPlace)
                {
                    targetTypeIds.Add(targetPlace.TypeId.Value);
                }

                if (!sourceTypeId.IsValid || !candidates.Contains(sourceTypeId.Value))
                {
                    foreach (var targetTypeId in targetTypeIds)
                    {
                        changed |= candidates.Remove(targetTypeId);
                        vetoedTypes.Add(targetTypeId);
                    }

                    continue;
                }

                foreach (var targetTypeId in targetTypeIds.Where(targetTypeId => !vetoedTypes.Contains(targetTypeId)))
                {
                    changed |= candidates.Add(targetTypeId);
                }
            }
        }

        return candidates;

        string? TryGetConstructorFamily(int typeIdValue)
        {
            return module.TypeDescriptors.TryGetValue(typeIdValue, out var descriptor) &&
                   descriptor is Types.TypeDescriptor.TyCon tyCon
                ? tyCon.Constructor.ToDescriptorString()
                : null;
        }

        void AddPayloadBearingFamily(TypeId typeId)
        {
            if (TryGetConstructorFamily(typeId.Value) is { } family)
            {
                payloadBearingConstructorFamilies.Add(family);
            }
        }
    }
}
