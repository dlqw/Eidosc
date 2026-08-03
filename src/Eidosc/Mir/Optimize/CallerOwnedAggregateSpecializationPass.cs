using Eidosc.Borrow;
using Eidosc.Semantic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

/// <summary>
/// Builds private caller-owned record variants when an aggregate and every
/// alias remain inside a statically known direct-call closure. Unknown calls,
/// FFI, copies, captures, projected stores and escaping returns keep the
/// ordinary heap ABI.
/// </summary>
public sealed class CallerOwnedAggregateSpecializationPass : IMirOptimizationPass
{
    private const int MaxVariants = 256;
    private const long MaxInlineArrayStorageBytes = 4096;
    private const long RuntimeArrayStorageOverheadBytes = 64;

    public string Name => "CallerOwnedAggregateSpecialization";

    public MirModule Run(MirModule module)
    {
        var functions = module.Functions
            .Where(static function => !function.IsExternal && function.BasicBlocks.Count > 0)
            .GroupBy(MirFunctionIdentity.GetStableKey, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.First(), StringComparer.Ordinal);
        var outCandidates = functions
            .Where(pair => pair.Value.CallerOwnedAggregateAbi.IsEmpty &&
                           TryGetOutReturnLocals(module, pair.Value, out _))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
        if (outCandidates.Count == 0)
        {
            return module;
        }

        var snapshot = module.Functions.ToArray();
        var committedParamVariants = new Dictionary<string, MirFunc>(StringComparer.Ordinal);
        var committedOutVariants = new Dictionary<string, MirFunc>(StringComparer.Ordinal);
        var variantsCreated = 0;
        var storageTypeIds = PlanningTransaction.CollectStorageTypeIds(module, outCandidates);

        foreach (var caller in snapshot)
        {
            if (caller.IsExternal || caller.BasicBlocks.Count == 0 ||
                caller.CallerOwnedAggregateAbi.HasOutReturn ||
                caller.CallerOwnedAggregateAbi.LocalGroups.Any(static group => group.ParameterIndex >= 0))
            {
                continue;
            }

            var rejectedSeeds = new HashSet<MirCall>(ReferenceEqualityComparer.Instance);
            while (variantsCreated < MaxVariants)
            {
                var seed = FindOutCandidateCall(caller, outCandidates, rejectedSeeds);
                if (seed == null)
                {
                    break;
                }

                var transaction = new PlanningTransaction(
                    module,
                    functions,
                    outCandidates,
                    committedParamVariants,
                    committedOutVariants,
                    MaxVariants - variantsCreated,
                    storageTypeIds);
                if (!transaction.TryPlanTopLevel(caller, seed))
                {
                    rejectedSeeds.Add(seed);
                    continue;
                }

                variantsCreated += transaction.Commit(module, committedParamVariants, committedOutVariants);
            }
        }

        // The module is mutated in place; wrap it in a fresh object when
        // variants were created so the optimizer's reference-identity change
        // detection reports the specialization.
        return variantsCreated > 0 ? module.WithFunctions(module.Functions) : module;
    }

    private static MirCall? FindOutCandidateCall(
        MirFunc function,
        IReadOnlyDictionary<string, MirFunc> outCandidates,
        IReadOnlySet<MirCall>? excluded = null)
    {
        foreach (var call in function.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>())
        {
            if (call.Target is MirPlace { Kind: PlaceKind.Local } &&
                call.Function is MirFunctionRef functionRef &&
                (excluded == null || !excluded.Contains(call)) &&
                outCandidates.ContainsKey(MirFunctionIdentity.GetStableKey(functionRef)))
            {
                return call;
            }
        }

        return null;
    }

    private static bool TryGetOutReturnLocals(
        MirModule module,
        MirFunc function,
        out HashSet<LocalId> returnLocals)
    {
        returnLocals = [];
        if (!TypeSemantics.IsManagedType(function.ReturnType) ||
            module.CopyLikeTypeIds.Contains(function.ReturnType.Value) ||
            !module.ConstructorLayouts.TryGetValue(function.ReturnType.Value, out var layouts) ||
            layouts.Count != 1)
        {
            return false;
        }

        var definitions = function.BasicBlocks
            .SelectMany(static block => block.Instructions)
            .Select(instruction => (instruction, target: GetDefinedLocal(instruction)))
            .Where(static item => item.target.HasValue)
            .GroupBy(static item => item.target!.Value)
            .ToDictionary(static group => group.Key, static group => group.Select(static item => item.instruction).ToArray());
        var sawReturn = false;
        foreach (var block in function.BasicBlocks)
        {
            if (block.Terminator is not MirReturn { Value: MirPlace { Kind: PlaceKind.Local } returned })
            {
                continue;
            }

            sawReturn = true;
            if (!definitions.TryGetValue(returned.Local, out var localDefinitions) ||
                localDefinitions.Length != 1 ||
                localDefinitions[0] is not MirCall
                {
                    Function: MirFunctionRef constructor,
                    RecordUpdate: null
                } ||
                !TypeSemantics.IsAdtConstructorCall(constructor))
            {
                return false;
            }

            returnLocals.Add(returned.Local);
        }

        return sawReturn && returnLocals.Count > 0;
    }

    private static LocalId? GetDefinedLocal(MirInstruction instruction) => instruction switch
    {
        MirAssign { Target.Kind: PlaceKind.Local } assign => assign.Target.Local,
        MirCaseInject { Target: MirPlace { Kind: PlaceKind.Local } target } => target.Local,
        MirCall { Target: { Kind: PlaceKind.Local } target } => target.Local,
        MirBinOp { Target: MirPlace { Kind: PlaceKind.Local } target } => target.Local,
        MirUnaryOp { Target: MirPlace { Kind: PlaceKind.Local } target } => target.Local,
        MirLoad { Target.Kind: PlaceKind.Local } load => load.Target.Local,
        MirStore { Target.Kind: PlaceKind.Local } store => store.Target.Local,
        MirCopy copy => copy.Target.Local,
        MirMove move => move.Target.Local,
        MirAlloc alloc => alloc.Target.Local,
        _ => null
    };

    private sealed class PlanningTransaction
    {
        private readonly MirModule _module;
        private readonly IReadOnlyDictionary<string, MirFunc> _functions;
        private readonly IReadOnlyDictionary<string, MirFunc> _outCandidates;
        private readonly IReadOnlyDictionary<string, MirFunc> _committedParamVariants;
        private readonly IReadOnlyDictionary<string, MirFunc> _committedOutVariants;
        private readonly int _variantBudget;
        private readonly IReadOnlySet<int> _storageTypeIds;
        private readonly Dictionary<string, MirFunc> _newParamVariants = new(StringComparer.Ordinal);
        private readonly Dictionary<string, MirFunc> _newOutVariants = new(StringComparer.Ordinal);
        private readonly Dictionary<MirFunc, FunctionPlan> _plans = new(ReferenceEqualityComparer.Instance);
        private readonly HashSet<string> _planningVariants = new(StringComparer.Ordinal);
        private readonly Dictionary<(string, int), CallerOwnedParamEscapeInfo> _paramEscapeCache = [];
        private readonly HashSet<(string, int)> _paramEscapeVisiting = [];

        public PlanningTransaction(
            MirModule module,
            IReadOnlyDictionary<string, MirFunc> functions,
            IReadOnlyDictionary<string, MirFunc> outCandidates,
            IReadOnlyDictionary<string, MirFunc> committedParamVariants,
            IReadOnlyDictionary<string, MirFunc> committedOutVariants,
            int variantBudget,
            IReadOnlySet<int> storageTypeIds)
        {
            _module = module;
            _functions = functions;
            _outCandidates = outCandidates;
            _committedParamVariants = committedParamVariants;
            _committedOutVariants = committedOutVariants;
            _variantBudget = variantBudget;
            _storageTypeIds = storageTypeIds;
        }

        /// <summary>
        /// The concrete Seq types backed by caller-owned inline storages across the
        /// module's out candidates. A value of one of these types stored in a
        /// record field is a pointer into the owning frame's stack blob, so
        /// projections of those fields must not escape the frame.
        /// </summary>
        public static HashSet<int> CollectStorageTypeIds(
            MirModule module,
            IReadOnlyDictionary<string, MirFunc> outCandidates)
        {
            var storageTypeIds = new HashSet<int>();
            foreach (var candidate in outCandidates.Values)
            {
                if (!TryGetOutReturnLocals(module, candidate, out var returnLocals))
                {
                    continue;
                }

                foreach (var storage in FindCallerOwnedOutArrayStorages(candidate, returnLocals))
                {
                    storageTypeIds.Add(storage.ArrayTypeId.Value);
                }
            }

            return storageTypeIds;
        }

        public bool TryPlanTopLevel(MirFunc caller, MirCall seed)
        {
            if (_variantBudget <= 0 ||
                seed.Target is not MirPlace { Kind: PlaceKind.Local } target)
            {
                return false;
            }

            return TryPlanFunction(caller, [target.Local], parameterIndex: -1, allowOwnedReturn: false);
        }

        public int Commit(
            MirModule module,
            IDictionary<string, MirFunc> committedParamVariants,
            IDictionary<string, MirFunc> committedOutVariants)
        {
            PropagateArrayStoragesToFixedPoint();
            foreach (var plan in _plans.Values)
            {
                ApplyPlan(plan);
            }

            foreach (var (key, variant) in _newParamVariants)
            {
                committedParamVariants[key] = variant;
                module.Functions.Add(variant);
            }

            foreach (var (key, variant) in _newOutVariants)
            {
                committedOutVariants[key] = variant;
                module.Functions.Add(variant);
            }

            return _newParamVariants.Count + _newOutVariants.Count;
        }

        private void PropagateArrayStoragesToFixedPoint()
        {
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var plan in _plans.Values)
                {
                    foreach (var rewrite in plan.Rewrites)
                    {
                        changed |= AddArrayStorages(
                            plan,
                            rewrite.Target.CallerOwnedAggregateAbi.OutArrayStorages);
                        changed |= AddArrayStorages(
                            plan,
                            rewrite.Target.CallerOwnedAggregateAbi.LocalGroups
                                .SelectMany(static group => group.ArrayStorages));
                        if (_plans.TryGetValue(rewrite.Target, out var targetPlan))
                        {
                            changed |= AddArrayStorages(plan, targetPlan.ArrayStorages.Values);
                            changed |= AddArrayStorages(targetPlan, plan.ArrayStorages.Values);
                        }
                    }
                }
            }

            static bool AddArrayStorages(
                FunctionPlan plan,
                IEnumerable<MirCallerOwnedArrayStorage> storages)
            {
                var previousCount = plan.ArrayStorages.Count;
                plan.AddArrayStorages(storages);
                return plan.ArrayStorages.Count != previousCount;
            }
        }

        private bool TryPlanFunction(
            MirFunc function,
            IReadOnlyCollection<LocalId> seeds,
            int parameterIndex,
            bool allowOwnedReturn)
        {
            if (_plans.ContainsKey(function))
            {
                return true;
            }

            var group = BuildAliasGroup(function, seeds);
            if (group.Count == 0)
            {
                return false;
            }

            var typeId = function.Locals.FirstOrDefault(local => group.Contains(local.Id))?.TypeId ?? TypeId.None;
            if (!typeId.IsValid ||
                group.Any(local => function.Locals.FirstOrDefault(candidate => candidate.Id == local)?.TypeId != typeId))
            {
                return false;
            }

            var plan = new FunctionPlan(function, group, typeId, parameterIndex);
            _plans[function] = plan;
            BuildProjectedLocals(plan);
            foreach (var block in function.BasicBlocks)
            {
                for (var index = 0; index < block.Instructions.Count; index++)
                {
                    if (!ValidateInstruction(plan, block, index))
                    {
                        _plans.Remove(function);
                        return false;
                    }
                }

                if (block.Terminator is not MirReturn { Value: MirPlace returnPlace } terminal)
                {
                    continue;
                }

                var returnedLocal = returnPlace.Kind == PlaceKind.Local ? returnPlace.Local : LocalId.None;
                if ((!allowOwnedReturn && group.Contains(returnedLocal)) ||
                    plan.ProjectedLocals.Contains(returnedLocal) ||
                    IsStorageTypedGroupProjection(returnPlace, plan))
                {
                    _plans.Remove(function);
                    return false;
                }
            }

            return true;
        }

        private bool ValidateInstruction(FunctionPlan plan, MirBasicBlock block, int instructionIndex)
        {
            var instruction = block.Instructions[instructionIndex];
            var group = plan.Group;
            switch (instruction)
            {
                case MirCopy copy when IsDirectGroupLocal(copy.Source, group):
                case MirCaseInject injection when IsDirectGroupLocal(injection.Operand, group) ||
                                                   OperandContainsProjected(injection.Operand, plan):
                case MirAlloc alloc when group.Contains(alloc.Target.Local):
                    return false;

                case MirStore store when IsDirectGroupLocal(store.Value, group) ||
                                         IsProjectedOperandValue(store.Value, plan):
                    return IsGroupOwnedStoreTarget(store, plan) ||
                           TryGetDestructiveLocalCarrierMoveTarget(
                               plan.Function,
                               block,
                               instructionIndex,
                               store,
                               out _);

                case MirBinOp binary when OperandContainsGroup(binary.Left, group) || OperandContainsGroup(binary.Right, group) ||
                                           OperandContainsProjected(binary.Left, plan) || OperandContainsProjected(binary.Right, plan):
                case MirUnaryOp unary when OperandContainsGroup(unary.Operand, group) ||
                                            OperandContainsProjected(unary.Operand, plan):
                    return false;

                case MirCall call:
                    return ValidateCall(plan, block, instructionIndex, call);

                default:
                    return true;
            }
        }

        private bool ValidateCall(FunctionPlan plan, MirBasicBlock block, int instructionIndex, MirCall call)
        {
            var group = plan.Group;
            if (IsDirectGroupLocal(call.Function, group))
            {
                return false;
            }

            var directGroupArguments = call.Arguments
                .Select((argument, index) => (argument, index))
                .Where(pair => IsDirectGroupLocal(pair.argument, group))
                .Select(static pair => pair.index)
                .ToArray();
            var targetInGroup = call.Target is MirPlace { Kind: PlaceKind.Local, Local: var target } && group.Contains(target);

            if (call.Function is MirFunctionRef functionRef &&
                _outCandidates.TryGetValue(MirFunctionIdentity.GetStableKey(functionRef), out var outTemplate))
            {
                if (!targetInGroup)
                {
                    return directGroupArguments.Length == 0;
                }

                if (directGroupArguments.Length != 0)
                {
                    return false;
                }

                var outVariant = GetOrCreateOutVariant(outTemplate);
                if (outVariant == null)
                {
                    return false;
                }

                plan.Rewrites.Add(new CallRewrite(block, instructionIndex, outVariant));
                plan.AddArrayStorages(outVariant.CallerOwnedAggregateAbi.OutArrayStorages);
                return true;
            }

            if (directGroupArguments.Length == 0)
            {
                return ValidateNoGroupArgumentCall(plan, call, targetInGroup);
            }

            if (call.Function is not MirFunctionRef directRef)
            {
                return false;
            }

            if (TypeSemantics.IsAdtConstructorCall(directRef))
            {
                return call.RecordUpdate is { IsKnownUnique: true } update &&
                       IsDirectGroupLocal(update.Source, group) &&
                       targetInGroup;
            }

            if (MirRuntimeFunctions.HasIdentity(directRef, WellKnownStrings.InternalNames.TypeId))
            {
                return directGroupArguments.Length == 1 && HasExactSingleConstructor(plan.TypeId);
            }

            if (!TryResolveFunction(directRef, out var template) ||
                template.IsExternal || template.BasicBlocks.Count == 0 || template.IsRuntimeWordAbi)
            {
                return false;
            }

            var parameters = template.Locals.Where(static local => local.IsParameter).ToArray();
            if (call.Arguments.Count != parameters.Length)
            {
                return false;
            }


            if (directGroupArguments.All(index =>
                    index < parameters.Length && IsReferenceTo(parameters[index].TypeId, plan.TypeId)))
            {
                return !targetInGroup;
            }

            if (directGroupArguments.Any(index => index >= parameters.Length || parameters[index].TypeId != plan.TypeId))
            {
                return false;
            }

            var variant = GetOrCreateParamVariant(template, directGroupArguments);
            if (variant == null)
            {
                return false;
            }

            plan.Rewrites.Add(new CallRewrite(block, instructionIndex, variant));
            if (_plans.TryGetValue(variant, out var variantPlan))
            {
                plan.AddArrayStorages(variantPlan.ArrayStorages.Values);
                variantPlan.AddArrayStorages(plan.ArrayStorages.Values);
            }
            return true;
        }

        /// <summary>
        /// Validates a call that passes no whole-group argument but may pass a
        /// projection of a group local whose value is a pointer into the owning
        /// frame's stack blob. Such a pointer must not reach an external
        /// function, a retaining callee, or an escaping return; known-safe
        /// array intrinsics and in-module callees proven not to retain the
        /// value are accepted, and array-returning intrinsics/callees taint the
        /// call target so later escapes of the result stay checked.
        /// </summary>
        private bool ValidateNoGroupArgumentCall(FunctionPlan plan, MirCall call, bool targetInGroup)
        {
            var projectedArgumentIndices = call.Arguments
                .Select((argument, index) => (argument, index))
                .Where(pair => IsProjectedArgument(pair.argument, plan))
                .Select(static pair => pair.index)
                .ToArray();
            if (projectedArgumentIndices.Length == 0)
            {
                return !targetInGroup;
            }

            if (call.Function is not MirFunctionRef projectedRef)
            {
                return false;
            }

            if (TypeSemantics.IsAdtConstructorCall(projectedRef))
            {
                // A record update / fresh record whose field holds the array
                // stays inside the frame when the target is group-owned.
                return targetInGroup;
            }

            if (IsReadOnlyArrayIntrinsic(projectedRef))
            {
                return !targetInGroup;
            }

            if (IsArrayReturningIntrinsic(projectedRef))
            {
                TaintCallTarget(plan, call);
                return !targetInGroup;
            }

            if (TryResolveFunction(projectedRef, out var template) &&
                !template.IsExternal &&
                !template.IsRuntimeWordAbi &&
                (template.BasicBlocks.Count > 0 || IsKnownSafeIntrinsicTemplate(template)) &&
                projectedArgumentIndices[0] < template.Locals.Count(static local => local.IsParameter))
            {
                var info = AnalyzeParamEscape(template, projectedArgumentIndices[0]);
                if (info.EscapesToMemory)
                {
                    return false;
                }

                if (info.ReturnsParamDerived)
                {
                    TaintCallTarget(plan, call);
                }

                return !targetInGroup;
            }

            return false;
        }

        private static void TaintCallTarget(FunctionPlan plan, MirCall call)
        {
            if (call.Target is MirPlace { Kind: PlaceKind.Local, Local: var target })
            {
                plan.ProjectedLocals.Add(target);
            }
        }

        /// <summary>
        /// Builds the set of locals in the current function whose value is (or
        /// is derived from) a projection of a group local that is backed by a
        /// caller-owned inline array storage. Those values are pointers into
        /// the owning frame's stack blob and must not escape it.
        /// </summary>
        private void BuildProjectedLocals(FunctionPlan plan)
        {
            var projected = plan.ProjectedLocals;
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in plan.Function.BasicBlocks)
                {
                    foreach (var instruction in block.Instructions)
                    {
                        changed |= instruction switch
                        {
                            MirLoad
                            {
                                Source: MirPlace source,
                                Target: { Kind: PlaceKind.Local } target
                            } when IsStorageTypedGroupProjection(source, plan) => projected.Add(target.Local),

                            MirStore
                            {
                                Value: MirPlace { Kind: PlaceKind.Local, Local: var stored },
                                Target: { Kind: PlaceKind.Local, Local: var target }
                            } when projected.Contains(stored) => projected.Add(target),

                            MirCopy { Source: MirPlace { Kind: PlaceKind.Local, Local: var from }, Target.Kind: PlaceKind.Local } copy
                                when projected.Contains(from) => projected.Add(copy.Target.Local),
                            MirMove { Source: MirPlace { Kind: PlaceKind.Local, Local: var from }, Target.Kind: PlaceKind.Local } move
                                when projected.Contains(from) => projected.Add(move.Target.Local),
                            MirAssign { Source: MirPlace { Kind: PlaceKind.Local, Local: var from }, Target.Kind: PlaceKind.Local } assign
                                when projected.Contains(from) => projected.Add(assign.Target.Local),

                            MirCall
                            {
                                Target: { Kind: PlaceKind.Local } target,
                                Function: MirFunctionRef functionRef
                            } call when IsArrayReturningIntrinsic(functionRef) &&
                                          call.Arguments.Any(argument => IsProjectedArgument(argument, plan)) =>
                                projected.Add(target.Local),

                            MirCall
                            {
                                Target: { Kind: PlaceKind.Local } target,
                                Function: MirFunctionRef functionRef
                            } call when TryResolveFunction(functionRef, out var callee) &&
                                          !callee.IsExternal &&
                                          call.Arguments.Any(argument => IsProjectedArgument(argument, plan)) =>
                                TaintFromCalleeReturn(plan, callee, call.Arguments, target.Local),

                            _ => false
                        };
                    }
                }
            }
        }

        private bool TaintFromCalleeReturn(
            FunctionPlan plan,
            MirFunc callee,
            IReadOnlyList<MirOperand> arguments,
            LocalId target)
        {
            for (var index = 0; index < arguments.Count; index++)
            {
                if (!IsProjectedArgument(arguments[index], plan))
                {
                    continue;
                }

                var parameters = callee.Locals.Count(static local => local.IsParameter);
                if (index >= parameters)
                {
                    return false;
                }

                if (AnalyzeParamEscape(callee, index).ReturnsParamDerived)
                {
                    return plan.ProjectedLocals.Add(target);
                }

                return false;
            }

            return false;
        }

        /// <summary>
        /// True when the place is a Field/Index/Deref projection of a group (or
        /// projected) local whose value type is one of the module's inline
        /// storage types — i.e. the place names a blob-interior pointer.
        /// </summary>
        private bool IsStorageTypedGroupProjection(MirPlace place, FunctionPlan plan)
        {
            if (place.Kind == PlaceKind.Local)
            {
                return false;
            }

            if (!_storageTypeIds.Contains(place.TypeId.Value))
            {
                return false;
            }

            return ResolvePlaceRootLocal(place) is { } root &&
                   (plan.Group.Contains(root) || plan.ProjectedLocals.Contains(root));
        }

        private static LocalId? ResolvePlaceRootLocal(MirPlace? place)
        {
            while (place != null)
            {
                switch (place.Kind)
                {
                    case PlaceKind.Local:
                        return place.Local;
                    case PlaceKind.Deref:
                        place = place.Base;
                        continue;
                    case PlaceKind.Field:
                        place = place.Base;
                        continue;
                    case PlaceKind.Index:
                        place = place.Base;
                        continue;
                    default:
                        return null;
                }
            }

            return null;
        }

        private bool IsProjectedArgument(MirOperand operand, FunctionPlan plan) => operand switch
        {
            MirPlace { Kind: PlaceKind.Local, Local: var local } => plan.ProjectedLocals.Contains(local),
            MirPlace place => IsStorageTypedGroupProjection(place, plan),
            _ => false
        };

        private bool IsProjectedOperandValue(MirOperand operand, FunctionPlan plan) => operand switch
        {
            MirPlace { Kind: PlaceKind.Local, Local: var local } => plan.ProjectedLocals.Contains(local),
            MirPlace place => IsStorageTypedGroupProjection(place, plan),
            _ => false
        };

        private static bool OperandContainsProjected(MirOperand? operand, FunctionPlan plan) => operand switch
        {
            MirPlace { Kind: PlaceKind.Local, Local: var local } => plan.ProjectedLocals.Contains(local),
            MirPlace place => OperandContainsProjected(place.Base, plan) || OperandContainsProjected(place.Index, plan),
            _ => false
        };

        private bool IsGroupOwnedStoreTarget(MirStore store, FunctionPlan plan)
        {
            if (store.Target is MirPlace { Kind: PlaceKind.Local, Local: var target } &&
                plan.Group.Contains(target))
            {
                return true;
            }

            return store.Target is MirPlace targetPlace &&
                   IsStorageTypedGroupProjection(targetPlace, plan);
        }

        /// <summary>
        /// Empty-bodied templates (array intrinsics lowered from prelude
        /// wrappers) are safe to analyze: their parameter cannot escape.
        /// Matched by the builtin name because the template carries a valid
        /// symbol id, which the runtime identity check rejects.
        /// </summary>
        private static bool IsKnownSafeIntrinsicTemplate(MirFunc template) =>
            template.FunctionId.Name is WellKnownStrings.InternalNames.ArrayLength or
                WellKnownStrings.InternalNames.ArrayGet or
                WellKnownStrings.InternalNames.ArrayRangeLength or
                WellKnownStrings.InternalNames.ArrayRangeGet or
                WellKnownStrings.InternalNames.ArrayTake or
                WellKnownStrings.InternalNames.ArraySlice or
                WellKnownStrings.InternalNames.ArrayPush or
                WellKnownStrings.InternalNames.ArrayPrepend or
                WellKnownStrings.InternalNames.ArrayShiftPrepend or
                WellKnownStrings.InternalNames.ArrayTailShiftPrepend or
                WellKnownStrings.InternalNames.ArrayTailShiftPrependUnique or
                WellKnownStrings.InternalNames.ArrayExtend or
                WellKnownStrings.InternalNames.TypeId;

        private static bool IsReadOnlyArrayIntrinsic(MirFunctionRef functionRef) =>
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayLength) ||
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayGet) ||
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayRangeLength) ||
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayRangeGet) ||
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.TypeId);

        private static bool IsArrayReturningIntrinsic(MirFunctionRef functionRef) =>
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayTake) ||
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArraySlice) ||
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayPush) ||
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayPrepend) ||
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayShiftPrepend) ||
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayTailShiftPrepend) ||
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayTailShiftPrependUnique) ||
            MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayExtend);

        /// <summary>
        /// Analyzes whether a callee lets a value received at the given
        /// parameter position escape its own frame. EscapesToMemory means the
        /// value reaches an external function or a non-local store (the caller
        /// must reject the call); ReturnsParamDerived means the value (or a
        /// value derived from it) may be returned, so the caller must taint its
        /// own call target.
        /// </summary>
        private CallerOwnedParamEscapeInfo AnalyzeParamEscape(MirFunc function, int parameterIndex)
        {
            var key = (MirFunctionIdentity.GetStableKey(function), parameterIndex);
            if (_paramEscapeCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            if (!_paramEscapeVisiting.Add(key))
            {
                // A call cycle: be conservative and reject the call.
                return CallerOwnedParamEscapeInfo.Unknown;
            }

            try
            {
                var analyzer = new CallerOwnedParamProvenanceAnalyzer(
                    ResolveAnalyzedFunction,
                    AnalyzeParamEscape,
                    IsKnownSafeIntrinsicTemplate,
                    IsReadOnlyArrayIntrinsic,
                    IsArrayReturningIntrinsic);
                var result = analyzer.Analyze(function, parameterIndex);
                _paramEscapeCache[key] = result;
                return result;
            }
            finally
            {
                _paramEscapeVisiting.Remove(key);
            }
        }

        private MirFunc? ResolveAnalyzedFunction(MirFunctionRef functionRef)
        {
            return TryResolveFunction(functionRef, out var function) ? function : null;
        }

        private MirFunc? GetOrCreateOutVariant(MirFunc template)
        {
            var templateKey = MirFunctionIdentity.GetStableKey(template);
            if (_committedOutVariants.TryGetValue(templateKey, out var committed))
            {
                return committed;
            }

            if (_newOutVariants.TryGetValue(templateKey, out var existing))
            {
                return existing;
            }

            if (_newOutVariants.Count + _newParamVariants.Count >= _variantBudget ||
                !TryGetOutReturnLocals(_module, template, out var returnLocals))
            {
                return null;
            }

            var variant = CloneVariant(template, "out", "caller-out");
            variant.CallerOwnedAggregateAbi = new MirCallerOwnedAggregateAbi
            {
                OutReturnType = template.ReturnType,
                OutReturnLocals = returnLocals,
                OutArrayStorages = FindCallerOwnedOutArrayStorages(template, returnLocals)
            };
            foreach (var block in variant.BasicBlocks)
            {
                for (var index = 0; index < block.Instructions.Count; index++)
                {
                    if (block.Instructions[index] is MirCall { IsTailCall: true } call)
                    {
                        block.Instructions[index] = call with { IsTailCall = false };
                    }
                }
            }

            _newOutVariants[templateKey] = variant;
            return variant;
        }

        private MirFunc? GetOrCreateParamVariant(MirFunc template, IReadOnlyList<int> parameterIndices)
        {
            var sorted = parameterIndices.Distinct().Order().ToArray();
            var key = $"{MirFunctionIdentity.GetStableKey(template)}|caller-owned:{string.Join(',', sorted)}";
            if (_committedParamVariants.TryGetValue(key, out var committed))
            {
                return committed;
            }

            if (_newParamVariants.TryGetValue(key, out var existing))
            {
                return existing;
            }

            if (_newOutVariants.Count + _newParamVariants.Count >= _variantBudget)
            {
                return null;
            }

            var suffix = $"caller_owned_{string.Join('_', sorted)}";
            var variant = CloneVariant(template, suffix, $"caller-owned:{string.Join(',', sorted)}");
            _newParamVariants[key] = variant;
            if (!_planningVariants.Add(key))
            {
                return variant;
            }

            var parameters = variant.Locals.Where(static local => local.IsParameter).ToArray();
            var seeds = sorted.Select(index => parameters[index].Id).ToArray();
            var planned = TryPlanFunction(
                variant,
                seeds,
                sorted.Length == 1 ? sorted[0] : -1,
                allowOwnedReturn: true);
            _planningVariants.Remove(key);
            if (!planned)
            {
                _newParamVariants.Remove(key);
                return null;
            }

            return variant;
        }

        private bool TryResolveFunction(MirFunctionRef functionRef, out MirFunc function)
        {
            if (_functions.TryGetValue(MirFunctionIdentity.GetStableKey(functionRef), out function!))
            {
                return true;
            }

            // Cloned call sites may carry a symbol-id key while the function
            // entry is keyed by its stable identity (specialized clones lose
            // the stable identity key on the reference side); fall back to a
            // name-then-symbol scan over the module's functions, preferring
            // the name match so a specialized clone is not shadowed by the
            // original template that shares its symbol id.
            foreach (var candidate in _module.Functions)
            {
                if (string.Equals(candidate.Name, functionRef.Name, StringComparison.Ordinal))
                {
                    function = candidate;
                    return true;
                }
            }

            foreach (var candidate in _module.Functions)
            {
                if (functionRef.SymbolId.IsValid && candidate.SymbolId == functionRef.SymbolId)
                {
                    function = candidate;
                    return true;
                }
            }

            return false;
        }

        private bool HasExactSingleConstructor(TypeId typeId) =>
            _module.ConstructorLayouts.TryGetValue(typeId.Value, out var layouts) && layouts.Count == 1;

        private static IReadOnlyList<MirCallerOwnedArrayStorage> FindCallerOwnedOutArrayStorages(
            MirFunc function,
            IReadOnlySet<LocalId> returnLocals)
        {
            var returnedConstructorArguments = function.BasicBlocks
                .SelectMany(static block => block.Instructions)
                .OfType<MirCall>()
                .Where(call => call.Target is MirPlace { Kind: PlaceKind.Local, Local: var target } &&
                               returnLocals.Contains(target) &&
                               call.Function is MirFunctionRef constructor &&
                               TypeSemantics.IsAdtConstructorCall(constructor))
                .SelectMany(static call => call.Arguments)
                .OfType<MirPlace>()
                .Where(static place => place.Kind == PlaceKind.Local)
                .Select(static place => place.Local)
                .ToHashSet();
            if (returnedConstructorArguments.Count == 0)
            {
                return [];
            }

            var storages = new List<MirCallerOwnedArrayStorage>();
            foreach (var call in function.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>())
            {
                if (call.Target is not MirPlace { Kind: PlaceKind.Local } target ||
                    call.Function is not MirFunctionRef functionRef ||
                    !MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArrayNew) ||
                    call.Arguments.Count < 2 ||
                    !TryGetNonNegativeConstant(call.Arguments[0], out var capacity) ||
                    !TryGetPositiveConstant(call.Arguments[1], out var elementSize))
                {
                    continue;
                }

                var aliases = BuildDirectLocalAliasComponent(function, target.Local);
                if (!aliases.Overlaps(returnedConstructorArguments) ||
                    !IsSafeNestedArrayCandidate(function, call, aliases))
                {
                    continue;
                }

                long storageBytes;
                try
                {
                    storageBytes = checked(RuntimeArrayStorageOverheadBytes + checked(capacity * elementSize));
                }
                catch (OverflowException)
                {
                    continue;
                }

                if (storageBytes > MaxInlineArrayStorageBytes)
                {
                    continue;
                }

                storages.Add(new MirCallerOwnedArrayStorage
                {
                    Key = $"{MirFunctionIdentity.GetStableKey(function)}|array:{target.Local.Value}",
                    ArrayLocal = target.Local,
                    ArrayTypeId = target.TypeId,
                    Capacity = capacity,
                    ElementSize = elementSize,
                    StorageBytes = storageBytes,
                    // Constant capacity plus the alias-group proof make the
                    // storage eligible for inline header/data lowering; the
                    // converter applies the final element-layout gate.
                    PromoteInline = true
                });
            }

            return storages.OrderBy(static storage => storage.Key, StringComparer.Ordinal).ToArray();
        }

        private static HashSet<LocalId> BuildDirectLocalAliasComponent(MirFunc function, LocalId seed)
        {
            var aliases = new HashSet<LocalId> { seed };
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var instruction in function.BasicBlocks.SelectMany(static block => block.Instructions))
                {
                    (LocalId Source, LocalId Target)? edge = instruction switch
                    {
                        MirAssign
                        {
                            Source: MirPlace { Kind: PlaceKind.Local, Local: var source },
                            Target: { Kind: PlaceKind.Local, Local: var target }
                        } => (source, target),
                        MirMove
                        {
                            Source: { Kind: PlaceKind.Local, Local: var source },
                            Target: { Kind: PlaceKind.Local, Local: var target }
                        } => (source, target),
                        MirLoad
                        {
                            Source: MirPlace { Kind: PlaceKind.Local, Local: var source },
                            Target: { Kind: PlaceKind.Local, Local: var target }
                        } => (source, target),
                        MirStore
                        {
                            Value: MirPlace { Kind: PlaceKind.Local, Local: var source },
                            Target: { Kind: PlaceKind.Local, Local: var target }
                        } => (source, target),
                        _ => null
                    };
                    if (edge is not { } localEdge)
                    {
                        continue;
                    }

                    if (aliases.Contains(localEdge.Source))
                    {
                        changed |= aliases.Add(localEdge.Target);
                    }
                    if (aliases.Contains(localEdge.Target))
                    {
                        changed |= aliases.Add(localEdge.Source);
                    }
                }
            }

            return aliases;
        }

        private static bool IsSafeNestedArrayCandidate(
            MirFunc function,
            MirCall allocation,
            IReadOnlySet<LocalId> aliases)
        {
            foreach (var block in function.BasicBlocks)
            {
                if (block.Terminator is MirReturn { Value: MirPlace { Kind: PlaceKind.Local, Local: var returned } } &&
                    aliases.Contains(returned))
                {
                    return false;
                }

                foreach (var instruction in block.Instructions)
                {
                    if (ReferenceEquals(instruction, allocation))
                    {
                        continue;
                    }

                    if (instruction is MirCopy { Source: MirPlace { Kind: PlaceKind.Local, Local: var copied } } &&
                        aliases.Contains(copied))
                    {
                        return false;
                    }

                    if (instruction is MirStore
                        {
                            Value: MirPlace { Kind: PlaceKind.Local, Local: var stored },
                            Target.Kind: not PlaceKind.Local
                        } && aliases.Contains(stored))
                    {
                        return false;
                    }

                    if (instruction is not MirCall call ||
                        !call.Arguments.OfType<MirPlace>().Any(argument =>
                            argument.Kind == PlaceKind.Local && aliases.Contains(argument.Local)))
                    {
                        continue;
                    }

                    if (call.Function is not MirFunctionRef functionRef ||
                        (!TypeSemantics.IsAdtConstructorCall(functionRef) &&
                         !MirRuntimeFunctions.HasIdentity(functionRef, WellKnownStrings.InternalNames.ArraySet)))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool TryGetNonNegativeConstant(MirOperand operand, out long value)
        {
            value = operand is MirConstant { Value: MirConstantValue.IntValue(var constant) } ? constant : -1;
            return value >= 0;
        }

        private static bool TryGetPositiveConstant(MirOperand operand, out long value)
        {
            value = operand is MirConstant { Value: MirConstantValue.IntValue(var constant) } ? constant : 0;
            return value > 0;
        }

        private bool IsReferenceTo(TypeId parameterTypeId, TypeId valueTypeId)
        {
            if (!_module.TypeDescriptors.TryGetValue(parameterTypeId.Value, out var descriptor))
            {
                return false;
            }

            return descriptor switch
            {
                TypeDescriptor.Ref reference => reference.Inner == valueTypeId,
                TypeDescriptor.MutRef reference => reference.Inner == valueTypeId,
                _ => false
            };
        }

        private static bool TryGetDestructiveLocalCarrierMoveTarget(
            MirFunc function,
            MirBasicBlock storeBlock,
            int storeIndex,
            MirStore store,
            out LocalId moveTarget)
        {
            moveTarget = default;
            if (store.Target is not { Kind: not PlaceKind.Local, Base: MirPlace { Kind: PlaceKind.Local, Local: var carrier } })
            {
                return false;
            }

            MirLoad? matchingMove = null;
            for (var index = storeIndex + 1; index < storeBlock.Instructions.Count; index++)
            {
                if (storeBlock.Instructions[index] is MirLoad { MovesOutOfSource: true } load &&
                    load.Source is MirPlace source &&
                    HaveSamePlacePath(source, store.Target))
                {
                    matchingMove = load;
                    break;
                }
            }

            if (matchingMove == null)
            {
                return false;
            }

            foreach (var block in function.BasicBlocks)
            {
                if (block.Terminator is MirReturn { Value: MirPlace { Kind: PlaceKind.Local, Local: var returned } } &&
                    returned == carrier)
                {
                    return false;
                }

                foreach (var instruction in block.Instructions)
                {
                    if ((instruction is MirCall call &&
                         call.Arguments.Any(argument => IsDirectLocal(argument, carrier))) ||
                        (instruction is MirCopy { Source: { Kind: PlaceKind.Local, Local: var copied } } && copied == carrier) ||
                        (instruction is MirStore
                        {
                            Value: MirPlace { Kind: PlaceKind.Local, Local: var stored },
                            Target.Kind: not PlaceKind.Local
                        } && stored == carrier))
                    {
                        return false;
                    }
                }
            }

            moveTarget = matchingMove.Target.Local;
            return true;
        }

        private static bool IsDirectLocal(MirOperand? operand, LocalId local) =>
            operand is MirPlace { Kind: PlaceKind.Local, Local: var candidate } && candidate == local;

        private static bool HaveSamePlacePath(MirPlace left, MirPlace right)
        {
            if (left.Kind != right.Kind)
            {
                return false;
            }

            return left.Kind switch
            {
                PlaceKind.Local => left.Local == right.Local,
                PlaceKind.Field => string.Equals(left.FieldName, right.FieldName, StringComparison.Ordinal) &&
                                   left.Base != null && right.Base != null &&
                                   HaveSamePlacePath(left.Base, right.Base),
                PlaceKind.Index => HaveSameIndexOperand(left.Index, right.Index) &&
                                   left.Base != null && right.Base != null &&
                                   HaveSamePlacePath(left.Base, right.Base),
                PlaceKind.Deref => left.Base != null && right.Base != null &&
                                   HaveSamePlacePath(left.Base, right.Base),
                _ => false
            };
        }

        private static bool HaveSameIndexOperand(MirOperand? left, MirOperand? right) => (left, right) switch
        {
            (MirConstant leftConstant, MirConstant rightConstant) => leftConstant.Value == rightConstant.Value,
            (MirPlace leftPlace, MirPlace rightPlace) => HaveSamePlacePath(leftPlace, rightPlace),
            (null, null) => true,
            _ => false
        };

        private static HashSet<LocalId> BuildAliasGroup(MirFunc function, IReadOnlyCollection<LocalId> seeds)
        {
            var result = seeds.ToHashSet();
            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var instruction in function.BasicBlocks.SelectMany(static block => block.Instructions))
                {
                    LocalId? source = null;
                    LocalId? target = null;
                    switch (instruction)
                    {
                        case MirAssign { Source: MirPlace { Kind: PlaceKind.Local } from, Target.Kind: PlaceKind.Local } assign:
                            source = from.Local;
                            target = assign.Target.Local;
                            break;
                        case MirMove { Source: { Kind: PlaceKind.Local } from, Target.Kind: PlaceKind.Local } move:
                            source = from.Local;
                            target = move.Target.Local;
                            break;
                        case MirLoad { Source: MirPlace { Kind: PlaceKind.Local } from, Target.Kind: PlaceKind.Local } load:
                            source = from.Local;
                            target = load.Target.Local;
                            break;
                        case MirStore { Value: MirPlace { Kind: PlaceKind.Local } from, Target.Kind: PlaceKind.Local } store:
                            source = from.Local;
                            target = store.Target.Local;
                            break;
                        case MirCall
                        {
                            Target: { Kind: PlaceKind.Local } callTarget,
                            RecordUpdate.Source: { Kind: PlaceKind.Local } updateSource
                        }:
                            source = updateSource.Local;
                            target = callTarget.Local;
                            break;
                        case MirCall { Target: { Kind: PlaceKind.Local } callTarget } call:
                            var sameTypeArgument = call.Arguments.OfType<MirPlace>().FirstOrDefault(argument =>
                                argument.Kind == PlaceKind.Local && argument.TypeId == callTarget.TypeId);
                            if (sameTypeArgument != null)
                            {
                                source = sameTypeArgument.Local;
                                target = callTarget.Local;
                            }
                            break;
                    }

                    if (source is not { } sourceLocal || target is not { } targetLocal)
                    {
                        continue;
                    }

                    if (result.Contains(sourceLocal))
                    {
                        changed |= result.Add(targetLocal);
                    }
                    if (result.Contains(targetLocal))
                    {
                        changed |= result.Add(sourceLocal);
                    }
                }

                foreach (var block in function.BasicBlocks)
                {
                    for (var index = 0; index < block.Instructions.Count; index++)
                    {
                        if (block.Instructions[index] is MirStore store &&
                            IsDirectGroupLocal(store.Value, result) &&
                            TryGetDestructiveLocalCarrierMoveTarget(
                                function,
                                block,
                                index,
                                store,
                                out var moveTarget))
                        {
                            changed |= result.Add(moveTarget);
                        }
                    }
                }
            }

            return result;
        }

        private static bool IsDirectGroupLocal(MirOperand? operand, IReadOnlySet<LocalId> group) =>
            operand is MirPlace { Kind: PlaceKind.Local, Local: var local } && group.Contains(local);

        private static bool OperandContainsGroup(MirOperand? operand, IReadOnlySet<LocalId> group) => operand switch
        {
            MirPlace { Kind: PlaceKind.Local, Local: var local } => group.Contains(local),
            MirPlace place => OperandContainsGroup(place.Base, group) || OperandContainsGroup(place.Index, group),
            _ => false
        };

        private static MirFunc CloneVariant(MirFunc template, string suffix, string identitySuffix)
        {
            var sourceName = string.IsNullOrWhiteSpace(template.SourceName) ? template.Name : template.SourceName;
            var functionId = template.FunctionId with
            {
                SymbolId = SymbolId.None,
                StableIdentityKey = $"{MirFunctionIdentity.GetStableKey(template)}|{identitySuffix}",
                Name = $"{template.FunctionId.Name}__{suffix}",
                QualifiedName = $"{template.FunctionId.QualifiedName}__{suffix}",
                MangledName = string.Empty
            };
            var clone = new MirFunc
            {
                Name = $"{template.Name}__{suffix}",
                SourceName = $"{sourceName}__{suffix}",
                Locals = template.Locals.ToList(),
                BasicBlocks = template.BasicBlocks.Select(CloneBlock).ToList(),
                EntryBlockId = template.EntryBlockId,
                ReturnType = template.ReturnType,
                GenericParameterCount = template.GenericParameterCount,
                GenericParameters = template.GenericParameters.ToList(),
                GenericTypeParameterIds = template.GenericTypeParameterIds.ToList(),
                IsRuntimeWordAbi = template.IsRuntimeWordAbi,
                IsExternal = false,
                Span = template.Span,
                SymbolId = SymbolId.None,
                FunctionId = functionId,
                IsEntry = false,
                TraitInvokeHelper = template.TraitInvokeHelper,
                TraitInvokeHelperTraitId = template.TraitInvokeHelperTraitId,
                IntrinsicName = template.IntrinsicName,
                BuiltinIntrinsicRole = template.BuiltinIntrinsicRole
            };
            clone.OwnershipContract = template.OwnershipContract;
            return clone;
        }

        private static MirBasicBlock CloneBlock(MirBasicBlock block) => new()
        {
            Id = block.Id,
            Instructions = block.Instructions.Select(CloneInstruction).ToList(),
            Terminator = block.Terminator,
            Span = block.Span,
            IsEntry = block.IsEntry
        };

        private static MirInstruction CloneInstruction(MirInstruction instruction) => instruction switch
        {
            MirCall call => call with
            {
                Arguments = call.Arguments.ToList(),
                BorrowedArgumentIndices = call.BorrowedArgumentIndices.ToHashSet(),
                RecordUpdate = call.RecordUpdate == null
                    ? null
                    : call.RecordUpdate with { UpdatedFieldIndices = call.RecordUpdate.UpdatedFieldIndices.ToList() }
            },
            _ => instruction
        };

        private static void ApplyPlan(FunctionPlan plan)
        {
            foreach (var rewrite in plan.Rewrites)
            {
                if (rewrite.InstructionIndex >= rewrite.Block.Instructions.Count ||
                    rewrite.Block.Instructions[rewrite.InstructionIndex] is not MirCall call)
                {
                    continue;
                }

                rewrite.Block.Instructions[rewrite.InstructionIndex] = call with
                {
                    Function = RewriteFunctionRef((MirFunctionRef)call.Function, rewrite.Target),
                    IsTailCall = false
                };
            }

            var canonical = plan.ParameterIndex >= 0
                ? plan.Function.Locals.Where(static local => local.IsParameter).ElementAt(plan.ParameterIndex).Id
                : plan.Group.OrderBy(static local => local.Value).First();
            var existingGroups = plan.Function.CallerOwnedAggregateAbi.LocalGroups.ToList();
            existingGroups.Add(new MirCallerOwnedAggregateGroup
            {
                CanonicalLocal = canonical,
                TypeId = plan.TypeId,
                Locals = new HashSet<LocalId>(plan.Group),
                ArrayStorages = plan.ArrayStorages.Values
                    .OrderBy(static storage => storage.Key, StringComparer.Ordinal)
                    .ToArray(),
                ParameterIndex = plan.ParameterIndex
            });
            plan.Function.CallerOwnedAggregateAbi = plan.Function.CallerOwnedAggregateAbi with
            {
                LocalGroups = existingGroups
            };

            MoveResetDropsBeforeOutCalls(plan);
        }

        private static void MoveResetDropsBeforeOutCalls(FunctionPlan plan)
        {
            foreach (var block in plan.Function.BasicBlocks)
            {
                for (var callIndex = 0; callIndex < block.Instructions.Count; callIndex++)
                {
                    if (block.Instructions[callIndex] is not MirCall
                        {
                            Target: MirPlace { Kind: PlaceKind.Local, Local: var target },
                            Function: MirFunctionRef functionRef
                        } ||
                        !plan.Group.Contains(target) ||
                        !functionRef.FunctionId.StableIdentityKey.Contains("|caller-out", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    for (var search = callIndex + 1; search < Math.Min(block.Instructions.Count, callIndex + 5); search++)
                    {
                        if (block.Instructions[search] is MirDrop
                            {
                                Value: MirPlace { Kind: PlaceKind.Local, Local: var dropped }
                            } drop &&
                            plan.Group.Contains(dropped) &&
                            IsDefinitelyInitializedBeforeOutCall(
                                plan.Function,
                                plan.Group,
                                block.Id,
                                callIndex,
                                dropped))
                        {
                            block.Instructions.RemoveAt(search);
                            block.Instructions.Insert(callIndex, drop);
                            callIndex++;
                            break;
                        }
                    }
                }
            }
        }

        private static bool IsDefinitelyInitializedBeforeOutCall(
            MirFunc function,
            IReadOnlySet<LocalId> trackedLocals,
            BlockId callBlock,
            int callIndex,
            LocalId local)
        {
            if (!trackedLocals.Contains(local))
            {
                return false;
            }

            var blocksById = function.BasicBlocks.ToDictionary(static block => block.Id);
            if (!blocksById.TryGetValue(callBlock, out var targetBlock) ||
                callIndex < 0 ||
                callIndex >= targetBlock.Instructions.Count)
            {
                return false;
            }

            var controlFlow = new ControlFlowGraph(function);
            var reachable = new HashSet<BlockId>();
            var pending = new Queue<BlockId>();
            pending.Enqueue(function.EntryBlockId);
            while (pending.TryDequeue(out var blockId))
            {
                if (!reachable.Add(blockId))
                {
                    continue;
                }

                foreach (var successor in controlFlow.GetSuccessors(blockId))
                {
                    pending.Enqueue(successor);
                }
            }

            if (!reachable.Contains(callBlock))
            {
                return false;
            }

            var entryInitialized = function.Locals
                .Where(candidate => candidate.IsParameter && trackedLocals.Contains(candidate.Id))
                .Select(static candidate => candidate.Id)
                .ToHashSet();
            var initializedIn = new Dictionary<BlockId, HashSet<LocalId>>();
            var initializedOut = new Dictionary<BlockId, HashSet<LocalId>>();
            foreach (var blockId in reachable)
            {
                initializedIn[blockId] = new HashSet<LocalId>(trackedLocals);
                initializedOut[blockId] = new HashSet<LocalId>(trackedLocals);
            }

            var changed = true;
            while (changed)
            {
                changed = false;
                foreach (var block in function.BasicBlocks)
                {
                    if (!reachable.Contains(block.Id))
                    {
                        continue;
                    }

                    HashSet<LocalId>? nextIn = null;
                    if (block.Id == function.EntryBlockId)
                    {
                        nextIn = new HashSet<LocalId>(entryInitialized);
                    }

                    foreach (var predecessor in controlFlow.GetPredecessors(block.Id))
                    {
                        if (!reachable.Contains(predecessor) ||
                            !initializedOut.TryGetValue(predecessor, out var predecessorOut))
                        {
                            continue;
                        }

                        if (nextIn == null)
                        {
                            nextIn = new HashSet<LocalId>(predecessorOut);
                        }
                        else
                        {
                            nextIn.IntersectWith(predecessorOut);
                        }
                    }

                    nextIn ??= [];
                    var nextOut = new HashSet<LocalId>(nextIn);
                    foreach (var instruction in block.Instructions)
                    {
                        ApplyDefiniteInitializationTransfer(instruction, nextOut, trackedLocals);
                    }

                    if (!initializedIn[block.Id].SetEquals(nextIn))
                    {
                        initializedIn[block.Id] = nextIn;
                        changed = true;
                    }

                    if (!initializedOut[block.Id].SetEquals(nextOut))
                    {
                        initializedOut[block.Id] = nextOut;
                        changed = true;
                    }
                }
            }

            var stateAtCall = new HashSet<LocalId>(initializedIn[callBlock]);
            for (var index = 0; index < callIndex; index++)
            {
                ApplyDefiniteInitializationTransfer(
                    targetBlock.Instructions[index],
                    stateAtCall,
                    trackedLocals);
            }

            return stateAtCall.Contains(local);
        }

        private static void ApplyDefiniteInitializationTransfer(
            MirInstruction instruction,
            HashSet<LocalId> initialized,
            IReadOnlySet<LocalId> trackedLocals)
        {
            void Consume(MirOperand? operand)
            {
                if (TryGetRootLocal(operand, out var root) && trackedLocals.Contains(root))
                {
                    initialized.Remove(root);
                }
            }

            void Define(MirOperand? operand)
            {
                if (operand is MirPlace { Kind: PlaceKind.Local, Local: var target } &&
                    trackedLocals.Contains(target))
                {
                    initialized.Add(target);
                }
            }

            switch (instruction)
            {
                case MirAssign assign:
                    Consume(assign.Source);
                    Define(assign.Target);
                    break;
                case MirCaseInject injection:
                    Consume(injection.Operand);
                    Define(injection.Target);
                    break;
                case MirCall call:
                    if (call.RecordUpdate != null)
                    {
                        Consume(call.RecordUpdate.Source);
                    }

                    for (var index = 0; index < call.Arguments.Count; index++)
                    {
                        if (!call.BorrowedArgumentIndices.Contains(index))
                        {
                            Consume(call.Arguments[index]);
                        }
                    }

                    Define(call.Target);
                    break;
                case MirBinOp binary:
                    Consume(binary.Left);
                    Consume(binary.Right);
                    Define(binary.Target);
                    break;
                case MirUnaryOp unary:
                    Consume(unary.Operand);
                    Define(unary.Target);
                    break;
                case MirLoad load:
                    if (load.MovesOutOfSource)
                    {
                        Consume(load.Source);
                    }

                    Define(load.Target);
                    break;
                case MirStore store:
                    Consume(store.Value);
                    Define(store.Target);
                    break;
                case MirDrop drop:
                    Consume(drop.Value);
                    break;
                case MirCopy copy:
                    Define(copy.Target);
                    break;
                case MirMove move:
                    Consume(move.Source);
                    Define(move.Target);
                    break;
                case MirAlloc allocation:
                    Define(allocation.Target);
                    break;
            }
        }

        private static bool TryGetRootLocal(MirOperand? operand, out LocalId local)
        {
            if (operand is MirPlace place)
            {
                while (place.Kind != PlaceKind.Local && place.Base != null)
                {
                    place = place.Base;
                }

                if (place.Kind == PlaceKind.Local)
                {
                    local = place.Local;
                    return true;
                }
            }

            local = default;
            return false;
        }

        private static MirFunctionRef RewriteFunctionRef(MirFunctionRef functionRef, MirFunc target) => functionRef with
        {
            Name = target.Name,
            SymbolId = target.SymbolId,
            FunctionId = target.FunctionId
        };

        private sealed record FunctionPlan(
            MirFunc Function,
            HashSet<LocalId> Group,
            TypeId TypeId,
            int ParameterIndex)
        {
            public List<CallRewrite> Rewrites { get; } = [];

            /// <summary>
            /// Locals whose value is (or is derived from) a projection of a
            /// group local backed by an inline array storage — pointers into
            /// the owning frame's stack blob that must not escape the frame.
            /// Grows during validation as array-returning calls are accepted.
            /// </summary>
            public HashSet<LocalId> ProjectedLocals { get; } = [];

            public Dictionary<string, MirCallerOwnedArrayStorage> ArrayStorages { get; } =
                new(StringComparer.Ordinal);

            public void AddArrayStorages(IEnumerable<MirCallerOwnedArrayStorage> storages)
            {
                foreach (var storage in storages)
                {
                    ArrayStorages.TryAdd(storage.Key, storage);
                }
            }
        }

        private sealed record CallRewrite(MirBasicBlock Block, int InstructionIndex, MirFunc Target);
    }
}
