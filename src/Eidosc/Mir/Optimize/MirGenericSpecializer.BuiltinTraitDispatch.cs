using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

public sealed partial class MirGenericSpecializer
{
    private bool ShouldKeepBuiltinTraitCall(
        MirFunc containingFunction,
        MirFunctionRef functionRef,
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes)
    {
        if (!IsBuiltinShowTraitCall(containingFunction, functionRef) ||
            call.Arguments.Count == 0)
        {
            return false;
        }

        var receiverTypeId = ResolveOperandType(call.Arguments[0], localTypes);
        if (BaseTypes.IsBuiltIn(receiverTypeId))
        {
            return !HasExplicitTraitImpl(containingFunction, functionRef, receiverTypeId);
        }

        return containingFunction.TraitInvokeHelper == TraitInvokeHelperKind.ShowValue &&
               containingFunction.Locals.FirstOrDefault(local => local.IsParameter) is { TypeId: var parameterTypeId } &&
               BaseTypes.IsBuiltIn(parameterTypeId) &&
               !HasExplicitTraitImpl(containingFunction, functionRef, parameterTypeId);
    }

    private static MirCall MarkBuiltinShowTraitCall(MirCall call, MirFunctionRef functionRef)
    {
        return functionRef.TraitMethodRole == TraitMethodRole.Show
            ? call
            : call with
            {
                Function = functionRef with { TraitMethodRole = TraitMethodRole.Show }
            };
    }

    private bool IsBuiltinShowTraitCall(MirFunc containingFunction, MirFunctionRef functionRef)
    {
        return HasMirTraitMethodRole(functionRef, TraitMethodRole.Show) ||
               containingFunction.TraitInvokeHelper == TraitInvokeHelperKind.ShowValue;
    }

    private bool HasMirTraitMethodRole(MirFunctionRef functionRef, TraitMethodRole role)
    {
        return functionRef.TraitMethodRole == role ||
               (TryGetTraitMethodInfo(functionRef, out var traitMethodInfo) &&
                traitMethodInfo.MethodRole == role);
    }

    private bool IsBuiltinCloneTraitCall(MirFunc containingFunction, MirFunctionRef functionRef)
    {
        return containingFunction.TraitInvokeHelper == TraitInvokeHelperKind.CloneValue &&
               string.Equals(functionRef.Name, "clone", StringComparison.Ordinal);
    }

    private bool HasExplicitTraitImpl(MirFunc containingFunction, MirFunctionRef functionRef, TypeId receiverTypeId)
    {
        if (!receiverTypeId.IsValid)
        {
            return false;
        }

        if (functionRef.TraitOwnerId.IsValid &&
            !string.IsNullOrWhiteSpace(functionRef.Name) &&
            TryResolveImplMethod(
                functionRef.TraitOwnerId,
                functionRef.Name,
                receiverTypeId,
                functionRef.SymbolId,
                out var metadataImplMethodId,
                out _))
        {
            return metadataImplMethodId != functionRef.SymbolId;
        }

        if (containingFunction.TraitInvokeHelperTraitId.IsValid &&
            !string.IsNullOrWhiteSpace(functionRef.Name) &&
            TryResolveImplMethod(
                containingFunction.TraitInvokeHelperTraitId,
                functionRef.Name,
                receiverTypeId,
                functionRef.SymbolId,
                out var helperImplMethodId,
                out _))
        {
            return helperImplMethodId != functionRef.SymbolId;
        }

        if (TryGetTraitMethodInfo(functionRef, out var traitMethodInfo) &&
            traitMethodInfo.TraitId.IsValid &&
            !string.IsNullOrWhiteSpace(traitMethodInfo.Name) &&
            TryResolveImplMethod(
                traitMethodInfo.TraitId,
                traitMethodInfo.Name,
                receiverTypeId,
                functionRef.SymbolId,
                out var methodInfoImplMethodId,
                out _))
        {
            return methodInfoImplMethodId != functionRef.SymbolId;
        }

        return false;
    }

    private bool TryRewriteBuiltinEqTraitCall(
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out MirInstruction rewrittenInstruction)
    {
        rewrittenInstruction = null!;
        if (call is not
            {
                Function: MirFunctionRef functionRef,
                Target: not null,
                Arguments.Count: >= 2
            })
        {
            return false;
        }

        if (!HasMirTraitMethodRole(functionRef, TraitMethodRole.Equality))
        {
            return false;
        }

        var receiverTypeId = ResolveOperandType(call.Arguments[0], localTypes);
        if (!BaseTypes.IsBuiltIn(receiverTypeId))
        {
            return false;
        }

        if (receiverTypeId.Value == BaseTypes.StringId)
        {
            var boolType = new TypeId(BaseTypes.BoolId);
            rewrittenInstruction = new MirCall
            {
                Target = call.Target,
                Function = MirRuntimeFunctions.CreateFunctionRef("string_equals", boolType, call.Span),
                Arguments = [call.Arguments[0], call.Arguments[1]],
                Span = call.Span,
                IsTailCall = call.IsTailCall
            };
            return true;
        }

        rewrittenInstruction = new MirBinOp
        {
            Target = call.Target,
            Operator = BinaryOp.Eq,
            Left = call.Arguments[0],
            Right = call.Arguments[1],
            Span = call.Span
        };
        return true;
    }

    private bool TryRewriteBoundBuiltinEqTraitCall(
        MirCall call,
        IReadOnlyDictionary<LocalId, LocalCallBinding> localCallBindings,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out MirInstruction rewrittenInstruction)
    {
        rewrittenInstruction = null!;
        if (call.Function is not MirPlace { Kind: PlaceKind.Local } localFunction ||
            !localCallBindings.TryGetValue(localFunction.Local, out var binding))
        {
            return false;
        }

        var combinedCall = call with
        {
            Function = binding.FunctionRef,
            Arguments = CombineBoundArguments(binding.BoundArguments, call.Arguments)
        };
        return TryRewriteBuiltinEqTraitCall(combinedCall, localTypes, out rewrittenInstruction);
    }

    private bool TryLowerBuiltinEqPartialCall(
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out MirInstruction loweredInstruction,
        out LocalCallBinding binding)
    {
        loweredInstruction = null!;
        binding = null!;
        if (call is not
            {
                Function: MirFunctionRef functionRef,
                Target: { Kind: PlaceKind.Local } target,
                Arguments.Count: 1
            } ||
            !HasMirTraitMethodRole(functionRef, TraitMethodRole.Equality))
        {
            return false;
        }

        var receiverTypeId = ResolveOperandType(call.Arguments[0], localTypes);
        if (!BaseTypes.IsBuiltIn(receiverTypeId))
        {
            return false;
        }

        loweredInstruction = new MirAssign
        {
            Target = target,
            Source = functionRef,
            Span = call.Span
        };
        binding = CreateLocalCallBinding(functionRef, call.Arguments);
        return true;
    }

    private bool TryRewriteBuiltinCloneTraitCall(
        MirFunc containingFunction,
        MirCall call,
        IReadOnlyDictionary<LocalId, TypeId> localTypes,
        out MirInstruction rewrittenInstruction)
    {
        rewrittenInstruction = null!;
        if (call is not
            {
                Function: MirFunctionRef functionRef,
                Target: not null,
                Arguments.Count: >= 1
            } ||
            !IsBuiltinCloneTraitCall(containingFunction, functionRef))
        {
            return false;
        }

        var receiverTypeId = ResolveOperandType(call.Arguments[0], localTypes);
        if (!BaseTypes.IsBuiltIn(receiverTypeId) ||
            HasExplicitTraitImpl(containingFunction, functionRef, receiverTypeId))
        {
            return false;
        }

        rewrittenInstruction = new MirAssign
        {
            Target = call.Target,
            Source = call.Arguments[0],
            Span = call.Span
        };
        return true;
    }
}
