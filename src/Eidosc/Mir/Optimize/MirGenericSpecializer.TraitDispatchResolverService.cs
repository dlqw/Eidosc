using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private sealed class TraitDispatchResolverService(MirGenericSpecializer owner)
    {
        public bool TryRewriteTraitMethodCall(
            MirFunc containingFunction,
            MirCall call,
            IReadOnlyDictionary<LocalId, TypeId> localTypes,
            out MirCall rewrittenCall)
        {
            rewrittenCall = call;

            if (call.Function is not MirFunctionRef functionRef)
            {
                return false;
            }

            if (owner.ShouldKeepBuiltinTraitCall(containingFunction, functionRef, call, localTypes))
            {
                return false;
            }

            if (!owner.TryResolveTraitDispatchTarget(containingFunction, functionRef, call, localTypes, out var implMethodId, out var implMethodName))
            {
                if (ShouldRecordNoConcreteTraitDispatchType(containingFunction, functionRef, call, localTypes))
                {
                    RecordNoConcreteTraitDispatchType(containingFunction, functionRef, call, localTypes);
                }

                return false;
            }

            var signatureTypeId = owner.TryResolveConcreteFunctionRefType(functionRef, out var concreteFunctionTypeId)
                ? concreteFunctionTypeId
                : owner.TryResolveImplMethodSignatureTypeId(implMethodId, out var implMethodSignatureTypeId)
                    ? implMethodSignatureTypeId
                    : TypeId.None;

            rewrittenCall = call with
            {
                Function = owner.RewriteFunctionReference(
                    functionRef,
                    implMethodId,
                    implMethodName,
                    functionRef.TypeId,
                    signatureTypeId)
            };
            return true;
        }

        private bool ShouldRecordNoConcreteTraitDispatchType(
            MirFunc containingFunction,
            MirFunctionRef functionRef,
            MirCall call,
            IReadOnlyDictionary<LocalId, TypeId> localTypes)
        {
            return HasTraitDispatchIdentity(containingFunction, functionRef) &&
                   owner.EnumerateTraitDispatchTypeIds(containingFunction, functionRef, call, localTypes).Count == 0;
        }

        private bool HasTraitDispatchIdentity(MirFunc containingFunction, MirFunctionRef functionRef)
        {
            if (functionRef.TraitOwnerId.IsValid ||
                containingFunction.TraitInvokeHelperTraitId.IsValid ||
                HasFunctionRefTraitDispatchMetadata(functionRef) ||
                owner.TryGetTraitMethodInfo(functionRef, out _))
            {
                return true;
            }

            return false;
        }

        private static bool HasFunctionRefTraitDispatchMetadata(MirFunctionRef functionRef)
        {
            return functionRef.TraitSelfPosition != SelfPosition.Unknown ||
                   functionRef.TraitSelfParameterIndices.Count > 0 ||
                   functionRef.TraitSelfInResult ||
                   functionRef.TraitMethodRole != TraitMethodRole.None;
        }

        private void RecordNoConcreteTraitDispatchType(
            MirFunc containingFunction,
            MirFunctionRef functionRef,
            MirCall call,
            IReadOnlyDictionary<LocalId, TypeId> localTypes)
        {
            if (!TryBuildTraitDispatchFailureTemplateKey(containingFunction, functionRef, out var templateKey))
            {
                return;
            }

            var templateName = string.IsNullOrWhiteSpace(functionRef.Name)
                ? "trait-dispatch"
                : functionRef.Name;
            owner.RecordRejectedSpecialization(
                templateKey,
                templateName,
                functionRef.Span,
                $"trait-dispatch:{owner.BuildUnresolvedCallSignatureKey(call, call.Arguments, localTypes)}",
                $"trait-dispatch {owner.BuildUnresolvedCallSignatureDisplay(call, call.Arguments, localTypes)}",
                SpecializationFailureReason.NoConcreteDispatchType);
        }
    }
}
