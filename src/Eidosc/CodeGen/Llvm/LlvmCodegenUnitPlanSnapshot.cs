using Eidosc.Pipeline;

namespace Eidosc.CodeGen.Llvm;

public sealed record LlvmCodegenUnitPlanSnapshot(
    string SchemaVersion,
    LlvmCodegenUnitPlanEnvelopeUnit EnvelopeUnit,
    IReadOnlyList<LlvmCodegenUnitPlanFunctionUnit> FunctionUnits,
    IReadOnlyList<LlvmCodegenUnitPlanObjectGroup> ObjectGroups)
{
    public const string CurrentSchemaVersion = "llvm-codegen-unit-plan-snapshot-v2";

    public static LlvmCodegenUnitPlanSnapshot Create(
        LlvmModuleEnvelopeSnapshot envelope,
        LlvmModule module,
        LlvmFunctionFragmentSnapshot functions,
        LlvmBackendConfiguration backendConfiguration)
    {
        return Create(
            envelope,
            module,
            functions,
            backendConfiguration.StableHash);
    }

    public static LlvmCodegenUnitPlanSnapshot Create(
        LlvmModuleEnvelopeSnapshot envelope,
        LlvmModule module,
        LlvmFunctionFragmentSnapshot functions,
        TargetInfo targetInfo,
        NativeLinkMode linkMode)
    {
        return Create(
            envelope,
            module,
            functions,
            LlvmBackendConfiguration.Create(
                targetInfo,
                optimizationLevel: -1,
                enableLto: false,
                linkMode,
                extraCFlags: null,
                extraLinkFlags: null));
    }

    public static LlvmCodegenUnitPlanSnapshot Create(
        LlvmModuleEnvelopeSnapshot envelope,
        LlvmModule module,
        LlvmFunctionFragmentSnapshot functions,
        string targetTriple,
        string dataLayout,
        int optimizationLevel,
        bool enableLto,
        string targetCpu,
        string targetFeatures,
        NativeLinkMode linkMode)
    {
        var configHash = ModuleArtifactHash.ComputeJsonHash(new
        {
            targetTriple,
            dataLayout,
            optimizationLevel,
            enableLto,
            targetCpu,
            targetFeatures,
            linkMode
        });
        return Create(envelope, module, functions, configHash);
    }

    private static LlvmCodegenUnitPlanSnapshot Create(
        LlvmModuleEnvelopeSnapshot envelope,
        LlvmModule module,
        LlvmFunctionFragmentSnapshot functions,
        string configHash)
    {
        var envelopeKey = ModuleArtifactHash.ComputeJsonHash(new
        {
            schema = "llvm-codegen-unit-envelope-v1",
            envelope.EnvelopeFingerprint,
            configHash
        });
        var functionByKey = module.Functions.ToDictionary(GetFunctionKey, StringComparer.Ordinal);
        var functionByName = module.Functions
            .Where(static function => !string.IsNullOrWhiteSpace(function.Name))
            .ToDictionary(static function => function.Name, StringComparer.Ordinal);
        var functionFragmentsByKey = functions.Functions.ToDictionary(static function => function.FunctionKey, StringComparer.Ordinal);
        var functionUnits = functions.Functions
            .Select(function =>
            {
                functionByKey.TryGetValue(function.FunctionKey, out var llvmFunction);
                var directCallees = llvmFunction == null
                    ? []
                    : CollectDirectCallees(llvmFunction);
                var referencedTypeNames = llvmFunction == null
                    ? []
                    : CollectReferencedTypeNames(llvmFunction);
                var eligibility = GetEligibility(llvmFunction, directCallees, functionByName);
                return new LlvmCodegenUnitPlanFunctionUnit(
                    function.FunctionKey,
                    function.BodyHash,
                    ModuleArtifactHash.ComputeJsonHash(new
                    {
                        schema = "llvm-codegen-unit-function-v1",
                        envelope.EnvelopeFingerprint,
                        function.FunctionKey,
                        function.BodyHash,
                        configHash,
                        eligibility.IsObjectUnitEligible
                    }),
                    llvmFunction?.Linkage.ToString() ?? "<missing>",
                    eligibility.IsObjectUnitEligible,
                    eligibility.Reason,
                    directCallees,
                    referencedTypeNames,
                    function.IrFragment.Length,
                    function.BasicBlockCount,
                    function.InstructionCount,
                    function.ParameterCount);
            })
            .OrderBy(static unit => unit.FunctionKey, StringComparer.Ordinal)
            .ToArray();
        var objectGroups = BuildObjectGroups(functionUnits, functionFragmentsByKey, envelope, configHash);

        return new LlvmCodegenUnitPlanSnapshot(
            CurrentSchemaVersion,
            new LlvmCodegenUnitPlanEnvelopeUnit(
                envelope.EnvelopeFingerprint,
                envelopeKey,
                envelope.FragmentLineCount,
                envelope.GlobalIr.Count,
                envelope.DeclarationIr.Count,
                envelope.TypeDefinitionIr.Count),
            functionUnits,
            objectGroups);
    }

    public static LlvmCodegenUnitPlanSnapshot CreateFromSelectedPlan(
        LlvmCodegenUnitPlanSnapshot previous,
        LlvmCodegenUnitPlanSnapshot selected,
        LlvmFunctionFragmentSnapshot currentFragments,
        LlvmFunctionFragmentRestorePlanSnapshot functionRestorePlan)
    {
        var fragmentByKey = currentFragments.Functions.ToDictionary(static fragment => fragment.FunctionKey, StringComparer.Ordinal);
        var selectedUnitByKey = selected.FunctionUnits.ToDictionary(static unit => unit.FunctionKey, StringComparer.Ordinal);
        var selectedGroupByRoot = selected.ObjectGroups.ToDictionary(static group => group.RootFunctionKey, StringComparer.Ordinal);
        var restoreActionByFunction = functionRestorePlan.Functions.ToDictionary(
            static entry => entry.FunctionKey,
            static entry => entry.Action,
            StringComparer.Ordinal);
        var functionUnits = previous.FunctionUnits
            .Select(unit =>
            {
                if (!fragmentByKey.TryGetValue(unit.FunctionKey, out var fragment) ||
                    !selectedUnitByKey.TryGetValue(unit.FunctionKey, out var selectedUnit))
                {
                    return unit;
                }

                if (!restoreActionByFunction.TryGetValue(unit.FunctionKey, out var action) ||
                    action == LlvmFunctionFragmentRestoreAction.Restore)
                {
                    return unit;
                }

                return selectedUnit;
            })
            .OrderBy(static unit => unit.FunctionKey, StringComparer.Ordinal)
            .ToArray();
        var objectGroups = previous.ObjectGroups
            .Select(group =>
            {
                var memberActions = group.MemberFunctionKeys
                    .Select(functionKey => restoreActionByFunction.TryGetValue(functionKey, out var action)
                        ? action
                        : LlvmFunctionFragmentRestoreAction.Rebuild)
                    .ToArray();
                if (memberActions.Length > 0 &&
                    memberActions.All(static action => action == LlvmFunctionFragmentRestoreAction.Restore))
                {
                    return group;
                }

                return selectedGroupByRoot.TryGetValue(group.RootFunctionKey, out var selectedGroup)
                    ? selectedGroup
                    : group;
            })
            .OrderBy(static group => group.RootFunctionKey, StringComparer.Ordinal)
            .ToArray();

        return new LlvmCodegenUnitPlanSnapshot(
            CurrentSchemaVersion,
            previous.EnvelopeUnit,
            functionUnits,
            objectGroups);
    }

    public string PlanFingerprint => ModuleArtifactHash.ComputeJsonHash(new
    {
        SchemaVersion,
        EnvelopeUnit,
        Functions = FunctionUnits.Select(static unit => new
        {
            unit.FunctionKey,
            unit.BodyHash,
            unit.UnitCacheKey,
            unit.IsObjectUnitEligible,
            unit.ObjectUnitIneligibilityReason,
            unit.DirectCallees,
            unit.ReferencedTypeNames
        }).ToArray(),
        ObjectGroups = ObjectGroups.Select(static group => new
        {
            group.GroupKey,
            group.RootFunctionKey,
            group.MemberFunctionKeys,
            group.ReferencedSymbols,
            group.ReferencedTypeNames,
            group.TotalIrBytes
        }).ToArray()
    });

    private static (bool IsObjectUnitEligible, string Reason) GetEligibility(
        LlvmFunction? function,
        IReadOnlyList<string> directCallees,
        IReadOnlyDictionary<string, LlvmFunction> functionByName)
    {
        if (function == null)
        {
            return (false, "missing-function-definition");
        }

        if (function.BasicBlocks.Count == 0 || function.IsDeclaration)
        {
            return (false, "declaration");
        }

        if (!IsCrossObjectVisible(function.Linkage))
        {
            return function.Linkage switch
            {
                LlvmLinkage.Private => (false, "private-linkage"),
                LlvmLinkage.Internal => (false, "internal-linkage"),
                _ => (false, $"unsupported-linkage:{function.Linkage}")
            };
        }

        foreach (var callee in directCallees)
        {
            if (!functionByName.TryGetValue(callee, out var calleeFunction))
            {
                continue;
            }

            if (!IsCrossObjectVisible(calleeFunction.Linkage))
            {
                return (false, $"depends-on-non-object-unit:{callee}");
            }
        }

        return (true, "");
    }

    private static string GetFunctionKey(LlvmFunction function) => string.IsNullOrWhiteSpace(function.Name)
        ? "anon:<unknown>"
        : $"name:{function.Name}";

    private static bool IsCrossObjectVisible(LlvmLinkage linkage) => linkage is
        LlvmLinkage.External or
        LlvmLinkage.LinkOnce or
        LlvmLinkage.Weak or
        LlvmLinkage.LinkOnceOdr or
        LlvmLinkage.WeakOdr;

    private static IReadOnlyList<string> CollectDirectCallees(LlvmFunction function)
    {
        var callees = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                AddInstructionReferences(callees, instruction);
            }

            AddTerminatorReferences(callees, block.Terminator);
        }

        return callees.ToArray();
    }

    private static void AddInstructionReferences(SortedSet<string> callees, LlvmInstruction instruction)
    {
        switch (instruction)
        {
            case LlvmBinOp binOp:
                AddValueReferences(callees, binOp.Left);
                AddValueReferences(callees, binOp.Right);
                break;
            case LlvmUnaryOp unaryOp:
                AddValueReferences(callees, unaryOp.Operand);
                break;
            case LlvmAlloca:
                break;
            case LlvmLoad load:
                AddValueReferences(callees, load.Pointer);
                break;
            case LlvmStore store:
                AddValueReferences(callees, store.Value);
                AddValueReferences(callees, store.Pointer);
                break;
            case LlvmCall call:
                AddValueReferences(callees, call.Function);
                foreach (var argument in call.Arguments)
                {
                    AddValueReferences(callees, argument);
                }

                break;
            case LlvmCast cast:
                AddValueReferences(callees, cast.Value);
                break;
            case LlvmZext zext:
                AddValueReferences(callees, zext.Value);
                break;
            case LlvmTrunc trunc:
                AddValueReferences(callees, trunc.Value);
                break;
            case LlvmFpExt fpExt:
                AddValueReferences(callees, fpExt.Value);
                break;
            case LlvmFpTrunc fpTrunc:
                AddValueReferences(callees, fpTrunc.Value);
                break;
            case LlvmIcmp icmp:
                AddValueReferences(callees, icmp.Left);
                AddValueReferences(callees, icmp.Right);
                break;
            case LlvmFcmp fcmp:
                AddValueReferences(callees, fcmp.Left);
                AddValueReferences(callees, fcmp.Right);
                break;
            case LlvmGetElementPtr gep:
                AddValueReferences(callees, gep.Pointer);
                AddValueReferences(callees, gep.Index);
                break;
            case LlvmExtractValue extractValue:
                AddValueReferences(callees, extractValue.Aggregate);
                break;
            case LlvmInsertValue insertValue:
                AddValueReferences(callees, insertValue.Aggregate);
                AddValueReferences(callees, insertValue.Element);
                break;
            case LlvmSelect select:
                AddValueReferences(callees, select.Condition);
                AddValueReferences(callees, select.TrueValue);
                AddValueReferences(callees, select.FalseValue);
                break;
            case LlvmPhi phi:
                foreach (var incoming in phi.IncomingValues)
                {
                    AddValueReferences(callees, incoming.Value);
                }

                break;
        }
    }

    private static void AddTerminatorReferences(SortedSet<string> callees, LlvmTerminator? terminator)
    {
        switch (terminator)
        {
            case null:
            case LlvmBr:
            case LlvmUnreachable:
                break;
            case LlvmRet ret:
                if (ret.Value != null)
                {
                    AddValueReferences(callees, ret.Value);
                }

                break;
            case LlvmCondBr condBr:
                AddValueReferences(callees, condBr.Condition);
                break;
            case LlvmSwitch @switch:
                AddValueReferences(callees, @switch.Value);
                foreach (var @case in @switch.Cases)
                {
                    AddValueReferences(callees, @case.Value);
                }

                break;
            case LlvmIndirectBr indirectBr:
                AddValueReferences(callees, indirectBr.Address);
                break;
            case LlvmInvoke invoke:
                AddValueReferences(callees, invoke.Function);
                foreach (var argument in invoke.Arguments)
                {
                    AddValueReferences(callees, argument);
                }

                break;
            case LlvmResume resume:
                AddValueReferences(callees, resume.Value);
                break;
        }
    }

    private static void AddValueReferences(SortedSet<string> callees, LlvmValue value)
    {
        switch (value)
        {
            case LlvmGlobal { Name: { Length: > 0 } name }:
                callees.Add(name);
                break;
            case LlvmPtrToInt ptrToInt:
                AddValueReferences(callees, ptrToInt.Pointer);
                break;
            case LlvmIntToPtr intToPtr:
                AddValueReferences(callees, intToPtr.Integer);
                break;
        }
    }

    private static IReadOnlyList<string> CollectReferencedTypeNames(LlvmFunction function)
    {
        var typeNames = new SortedSet<string>(StringComparer.Ordinal);
        AddTypeReferences(typeNames, function.ReturnType);
        foreach (var parameter in function.Parameters)
        {
            AddTypeReferences(typeNames, parameter.Type);
        }

        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                AddInstructionTypeReferences(typeNames, instruction);
            }

            AddTerminatorTypeReferences(typeNames, block.Terminator);
        }

        return typeNames.ToArray();
    }

    private static void AddInstructionTypeReferences(SortedSet<string> typeNames, LlvmInstruction instruction)
    {
        switch (instruction)
        {
            case LlvmBinOp binOp:
                AddTypeReferences(typeNames, binOp.ResultType);
                AddValueTypeReferences(typeNames, binOp.Left);
                AddValueTypeReferences(typeNames, binOp.Right);
                break;
            case LlvmUnaryOp unaryOp:
                AddTypeReferences(typeNames, unaryOp.ResultType);
                AddValueTypeReferences(typeNames, unaryOp.Operand);
                break;
            case LlvmAlloca alloca:
                AddTypeReferences(typeNames, alloca.AllocatedType);
                break;
            case LlvmLoad load:
                AddTypeReferences(typeNames, load.LoadType);
                AddValueTypeReferences(typeNames, load.Pointer);
                break;
            case LlvmStore store:
                AddValueTypeReferences(typeNames, store.Value);
                AddValueTypeReferences(typeNames, store.Pointer);
                break;
            case LlvmCall call:
                AddValueTypeReferences(typeNames, call.Function);
                AddTypeReferences(typeNames, call.ReturnType);
                foreach (var argument in call.Arguments)
                {
                    AddValueTypeReferences(typeNames, argument);
                }

                break;
            case LlvmCast cast:
                AddValueTypeReferences(typeNames, cast.Value);
                AddTypeReferences(typeNames, cast.TargetType);
                break;
            case LlvmZext zext:
                AddValueTypeReferences(typeNames, zext.Value);
                AddTypeReferences(typeNames, zext.TargetType);
                break;
            case LlvmTrunc trunc:
                AddValueTypeReferences(typeNames, trunc.Value);
                AddTypeReferences(typeNames, trunc.TargetType);
                break;
            case LlvmFpExt fpExt:
                AddValueTypeReferences(typeNames, fpExt.Value);
                AddTypeReferences(typeNames, fpExt.TargetType);
                break;
            case LlvmFpTrunc fpTrunc:
                AddValueTypeReferences(typeNames, fpTrunc.Value);
                AddTypeReferences(typeNames, fpTrunc.TargetType);
                break;
            case LlvmIcmp icmp:
                AddValueTypeReferences(typeNames, icmp.Left);
                AddValueTypeReferences(typeNames, icmp.Right);
                break;
            case LlvmFcmp fcmp:
                AddValueTypeReferences(typeNames, fcmp.Left);
                AddValueTypeReferences(typeNames, fcmp.Right);
                break;
            case LlvmGetElementPtr gep:
                AddTypeReferences(typeNames, gep.ElementType);
                if (gep.StructType != null)
                {
                    AddTypeReferences(typeNames, gep.StructType);
                }

                AddValueTypeReferences(typeNames, gep.Pointer);
                AddValueTypeReferences(typeNames, gep.Index);
                break;
            case LlvmExtractValue extractValue:
                AddValueTypeReferences(typeNames, extractValue.Aggregate);
                break;
            case LlvmInsertValue insertValue:
                AddValueTypeReferences(typeNames, insertValue.Aggregate);
                AddValueTypeReferences(typeNames, insertValue.Element);
                break;
            case LlvmSelect select:
                AddValueTypeReferences(typeNames, select.Condition);
                AddValueTypeReferences(typeNames, select.TrueValue);
                AddValueTypeReferences(typeNames, select.FalseValue);
                break;
            case LlvmPhi phi:
                AddTypeReferences(typeNames, phi.PhiType);
                foreach (var incoming in phi.IncomingValues)
                {
                    AddValueTypeReferences(typeNames, incoming.Value);
                }

                break;
        }
    }

    private static void AddTerminatorTypeReferences(SortedSet<string> typeNames, LlvmTerminator? terminator)
    {
        switch (terminator)
        {
            case null:
            case LlvmBr:
            case LlvmUnreachable:
                break;
            case LlvmRet ret:
                if (ret.Value != null)
                {
                    AddValueTypeReferences(typeNames, ret.Value);
                }

                break;
            case LlvmCondBr condBr:
                AddValueTypeReferences(typeNames, condBr.Condition);
                break;
            case LlvmSwitch @switch:
                AddValueTypeReferences(typeNames, @switch.Value);
                foreach (var @case in @switch.Cases)
                {
                    AddValueTypeReferences(typeNames, @case.Value);
                }

                break;
            case LlvmIndirectBr indirectBr:
                AddValueTypeReferences(typeNames, indirectBr.Address);
                break;
            case LlvmInvoke invoke:
                AddValueTypeReferences(typeNames, invoke.Function);
                AddTypeReferences(typeNames, invoke.ReturnType);
                foreach (var argument in invoke.Arguments)
                {
                    AddValueTypeReferences(typeNames, argument);
                }

                break;
            case LlvmResume resume:
                AddValueTypeReferences(typeNames, resume.Value);
                break;
        }
    }

    private static void AddValueTypeReferences(SortedSet<string> typeNames, LlvmValue value)
    {
        AddTypeReferences(typeNames, value.Type);
        switch (value)
        {
            case LlvmPtrToInt ptrToInt:
                AddValueTypeReferences(typeNames, ptrToInt.Pointer);
                AddTypeReferences(typeNames, ptrToInt.TargetType);
                break;
            case LlvmIntToPtr intToPtr:
                AddValueTypeReferences(typeNames, intToPtr.Integer);
                AddTypeReferences(typeNames, intToPtr.TargetType);
                break;
        }
    }

    private static void AddTypeReferences(SortedSet<string> typeNames, LlvmType type)
    {
        switch (type)
        {
            case LlvmStructType { IsLiteral: false, Name: { Length: > 0 } name } structType:
                typeNames.Add(name);
                foreach (var field in structType.Fields)
                {
                    AddTypeReferences(typeNames, field);
                }

                break;
            case LlvmStructType structType:
                foreach (var field in structType.Fields)
                {
                    AddTypeReferences(typeNames, field);
                }

                break;
            case LlvmArrayType arrayType:
                AddTypeReferences(typeNames, arrayType.Element);
                break;
            case LlvmVectorType vectorType:
                AddTypeReferences(typeNames, vectorType.ElementType);
                break;
            case LlvmPointerType { ElementType: { } elementType }:
                AddTypeReferences(typeNames, elementType);
                break;
            case LlvmFunctionType functionType:
                AddTypeReferences(typeNames, functionType.ReturnType);
                foreach (var parameterType in functionType.ParameterTypes)
                {
                    AddTypeReferences(typeNames, parameterType);
                }

                break;
        }
    }

    private static IReadOnlyList<LlvmCodegenUnitPlanObjectGroup> BuildObjectGroups(
        IReadOnlyList<LlvmCodegenUnitPlanFunctionUnit> functionUnits,
        IReadOnlyDictionary<string, LlvmFunctionFragment> functionFragmentsByKey,
        LlvmModuleEnvelopeSnapshot envelope,
        string configHash)
    {
        var unitByFunctionName = functionUnits.ToDictionary(
            static unit => FunctionNameFromKey(unit.FunctionKey),
            StringComparer.Ordinal);
        var groups = new List<LlvmCodegenUnitPlanObjectGroup>();
        foreach (var unit in functionUnits)
        {
            if (!IsCrossObjectVisible(ParseLinkage(unit.Linkage)))
            {
                continue;
            }

            var members = new SortedSet<string>(StringComparer.Ordinal) { unit.FunctionKey };
            AddPrivateInternalClosure(unit, unitByFunctionName, members);
            var memberKeys = members.ToArray();
            var referencedSymbols = new SortedSet<string>(unit.DirectCallees, StringComparer.Ordinal);
            var referencedTypeNames = new SortedSet<string>(unit.ReferencedTypeNames, StringComparer.Ordinal);
            foreach (var memberKey in memberKeys)
            {
                if (!functionFragmentsByKey.ContainsKey(memberKey))
                {
                    continue;
                }

                var memberName = FunctionNameFromKey(memberKey);
                if (unitByFunctionName.TryGetValue(memberName, out var memberUnit))
                {
                    referencedSymbols.UnionWith(memberUnit.DirectCallees);
                    referencedTypeNames.UnionWith(memberUnit.ReferencedTypeNames);
                }
            }

            foreach (var referencedSymbol in referencedSymbols)
            {
                if (unitByFunctionName.TryGetValue(referencedSymbol, out var referencedUnit))
                {
                    referencedTypeNames.UnionWith(referencedUnit.ReferencedTypeNames);
                }
            }

            var totalIrBytes = memberKeys.Sum(key =>
                functionFragmentsByKey.TryGetValue(key, out var fragment) ? fragment.IrFragment.Length : 0);
            var memberHashes = memberKeys
                .Select(key => functionFragmentsByKey.TryGetValue(key, out var fragment)
                    ? $"{key}:{fragment.BodyHash}"
                    : $"{key}:<missing>")
                .ToArray();
            var envelopeScopeFingerprint = ComputeObjectGroupEnvelopeScopeFingerprint(
                envelope,
                referencedSymbols,
                referencedTypeNames);
            var groupKey = ModuleArtifactHash.ComputeJsonHash(new
            {
                schema = "llvm-codegen-object-group-v2",
                envelopeScopeFingerprint,
                configHash,
                root = unit.FunctionKey,
                members = memberHashes,
                refs = referencedSymbols.ToArray(),
                types = referencedTypeNames.ToArray()
            });
            groups.Add(new LlvmCodegenUnitPlanObjectGroup(
                groupKey,
                unit.FunctionKey,
                memberKeys,
                referencedSymbols.ToArray(),
                referencedTypeNames.ToArray(),
                totalIrBytes,
                memberKeys.Length));
        }

        return groups
            .OrderBy(static group => group.RootFunctionKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static string ComputeObjectGroupEnvelopeScopeFingerprint(
        LlvmModuleEnvelopeSnapshot envelope,
        IReadOnlySet<string> referencedSymbols,
        IReadOnlySet<string> referencedTypeNames)
    {
        return ModuleArtifactHash.ComputeJsonHash(new
        {
            schema = "llvm-codegen-object-group-envelope-scope-v1",
            envelope.SchemaVersion,
            envelope.DataLayout,
            envelope.TargetTriple,
            envelope.HeaderIr,
            TypeDefinitionIr = envelope.GetObjectGroupTypeDefinitionIr(referencedTypeNames),
            GlobalIr = envelope.GetObjectGroupGlobalIr(referencedSymbols),
            DeclarationIr = envelope.GetObjectGroupDeclarationIr(referencedSymbols),
            envelope.AttributeGroupIr
        });
    }

    private static void AddPrivateInternalClosure(
        LlvmCodegenUnitPlanFunctionUnit unit,
        IReadOnlyDictionary<string, LlvmCodegenUnitPlanFunctionUnit> unitByFunctionName,
        ISet<string> members)
    {
        foreach (var callee in unit.DirectCallees)
        {
            if (!unitByFunctionName.TryGetValue(callee, out var calleeUnit))
            {
                continue;
            }

            var linkage = ParseLinkage(calleeUnit.Linkage);
            if (linkage is not LlvmLinkage.Private and not LlvmLinkage.Internal)
            {
                continue;
            }

            if (members.Add(calleeUnit.FunctionKey))
            {
                AddPrivateInternalClosure(calleeUnit, unitByFunctionName, members);
            }
        }
    }

    private static LlvmLinkage ParseLinkage(string linkage) =>
        Enum.TryParse<LlvmLinkage>(linkage, ignoreCase: false, out var parsed)
            ? parsed
            : LlvmLinkage.Private;

    private static string FunctionNameFromKey(string functionKey) =>
        functionKey.StartsWith("name:", StringComparison.Ordinal)
            ? functionKey["name:".Length..]
            : functionKey;
}

public sealed record LlvmCodegenUnitPlanEnvelopeUnit(
    string EnvelopeFingerprint,
    string UnitCacheKey,
    int LineCount,
    int GlobalCount,
    int DeclarationCount,
    int TypeDefinitionCount);

public sealed record LlvmCodegenUnitPlanFunctionUnit(
    string FunctionKey,
    string BodyHash,
    string UnitCacheKey,
    string Linkage,
    bool IsObjectUnitEligible,
    string ObjectUnitIneligibilityReason,
    IReadOnlyList<string> DirectCallees,
    IReadOnlyList<string> ReferencedTypeNames,
    int IrBytes,
    int BasicBlockCount,
    int InstructionCount,
    int ParameterCount);

public sealed record LlvmCodegenUnitPlanObjectGroup(
    string GroupKey,
    string RootFunctionKey,
    IReadOnlyList<string> MemberFunctionKeys,
    IReadOnlyList<string> ReferencedSymbols,
    IReadOnlyList<string> ReferencedTypeNames,
    int TotalIrBytes,
    int FunctionCount);
