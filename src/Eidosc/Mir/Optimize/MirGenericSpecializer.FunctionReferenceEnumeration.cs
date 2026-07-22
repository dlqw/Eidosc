using Eidosc.Symbols;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Mir.Optimize;

// Function reference enumeration across MIR instructions, terminators, operands, and places
public sealed partial class MirGenericSpecializer
{
    private FunctionRewriteSummary BuildFunctionRewriteSummary(
        MirFunc function,
        bool includeKnownTemplateReferences = false)
    {
        _stats.FunctionRewriteSummariesBuilt++;
        var needsFullFunctionScan = function.TraitInvokeHelper != TraitInvokeHelperKind.None;
        var candidateBlockCount = 0;
        var candidateInstructionCount = 0;
        var canUseCandidateBlockScan = !needsFullFunctionScan;
        List<int>? candidateBlockIndices = null;
        List<FunctionRewriteCandidateSite>? candidateInstructionSites = null;
        Dictionary<LocalId, TypeId>? localTypes = null;

        for (var blockIndex = 0; blockIndex < function.BasicBlocks.Count; blockIndex++)
        {
            var block = function.BasicBlocks[blockIndex];
            var blockHasCandidate = false;
            for (var instructionIndex = 0; instructionIndex < block.Instructions.Count; instructionIndex++)
            {
                var instruction = block.Instructions[instructionIndex];
                if (!InstructionHasReferenceRequiringSpecialization(
                        function,
                        instruction,
                        ref localTypes,
                        includeKnownTemplateReferences))
                {
                    continue;
                }

                blockHasCandidate = true;
                candidateInstructionCount++;
                var isDirectCompleteTemplateCall = IsDirectCompleteTemplateCallCandidate(instruction);
                canUseCandidateBlockScan &= isDirectCompleteTemplateCall;
                if (isDirectCompleteTemplateCall)
                {
                    candidateInstructionSites ??= [];
                    candidateInstructionSites.Add(new FunctionRewriteCandidateSite(blockIndex, instructionIndex));
                }
            }

            if (blockHasCandidate)
            {
                candidateBlockCount++;
                candidateBlockIndices ??= [];
                candidateBlockIndices.Add(blockIndex);
            }

            if (block.Terminator != null &&
                TerminatorHasReferenceRequiringSpecialization(block.Terminator, includeKnownTemplateReferences))
            {
                needsFullFunctionScan = true;
                canUseCandidateBlockScan = false;
            }
        }

        _stats.FunctionRewriteSummaryCandidateBlocks += candidateBlockCount;
        _stats.FunctionRewriteSummaryCandidateInstructions += candidateInstructionCount;
        return new FunctionRewriteSummary(
            needsFullFunctionScan || candidateInstructionCount > 0,
            needsFullFunctionScan,
            candidateBlockCount,
            candidateInstructionCount,
            canUseCandidateBlockScan && candidateInstructionCount > 0,
            candidateBlockIndices?.ToArray() ?? [],
            candidateInstructionSites?.ToArray() ?? []);
    }

    private bool IsDirectCompleteTemplateCallCandidate(MirInstruction instruction)
    {
        if (instruction is not MirCall { Function: MirFunctionRef functionRef } call ||
            RequiresSpecializationPass(functionRef) ||
            !TryResolveTemplateKey(functionRef, out var templateKey) ||
            !_templateRegistry.ByKeyDict.TryGetValue(templateKey, out var template))
        {
            return false;
        }

        return call.Arguments.Count >= GetTemplateParameterCount(template.TemplateSource);
    }

    private bool FunctionHasReferenceRequiringSpecialization(MirFunc function)
    {
        Dictionary<LocalId, TypeId>? localTypes = null;
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (InstructionHasReferenceRequiringSpecialization(function, instruction, ref localTypes))
                {
                    return true;
                }
            }

            if (block.Terminator != null &&
                TerminatorHasReferenceRequiringSpecialization(block.Terminator))
            {
                return true;
            }
        }

        return false;
    }

    private bool InstructionHasReferenceRequiringSpecialization(
        MirFunc function,
        MirInstruction instruction,
        ref Dictionary<LocalId, TypeId>? localTypes,
        bool includeKnownTemplateReferences = false)
    {
        return instruction switch
        {
            MirAssign assign =>
                OperandHasReferenceRequiringSpecialization(assign.Target, includeKnownTemplateReferences) ||
                OperandHasReferenceRequiringSpecialization(assign.Source, includeKnownTemplateReferences),
            MirCaseInject injection =>
                OperandHasReferenceRequiringSpecialization(injection.Target, includeKnownTemplateReferences) ||
                OperandHasReferenceRequiringSpecialization(injection.Operand, includeKnownTemplateReferences),
            MirCall call =>
                CallFunctionHasReferenceRequiringSpecialization(function, call, ref localTypes, includeKnownTemplateReferences) ||
                call.Target != null && OperandHasReferenceRequiringSpecialization(call.Target, includeKnownTemplateReferences) ||
                OperandsHaveReferenceRequiringSpecialization(call.Arguments, includeKnownTemplateReferences),
            MirBinOp binOp =>
                OperandHasReferenceRequiringSpecialization(binOp.Target, includeKnownTemplateReferences) ||
                OperandHasReferenceRequiringSpecialization(binOp.Left, includeKnownTemplateReferences) ||
                OperandHasReferenceRequiringSpecialization(binOp.Right, includeKnownTemplateReferences),
            MirUnaryOp unaryOp =>
                OperandHasReferenceRequiringSpecialization(unaryOp.Target, includeKnownTemplateReferences) ||
                OperandHasReferenceRequiringSpecialization(unaryOp.Operand, includeKnownTemplateReferences),
            MirLoad load =>
                OperandHasReferenceRequiringSpecialization(load.Target, includeKnownTemplateReferences) ||
                OperandHasReferenceRequiringSpecialization(load.Source, includeKnownTemplateReferences),
            MirStore store =>
                OperandHasReferenceRequiringSpecialization(store.Target, includeKnownTemplateReferences) ||
                OperandHasReferenceRequiringSpecialization(store.Value, includeKnownTemplateReferences),
            MirDrop drop => OperandHasReferenceRequiringSpecialization(drop.Value, includeKnownTemplateReferences),
            MirCopy copy =>
                OperandHasReferenceRequiringSpecialization(copy.Target, includeKnownTemplateReferences) ||
                OperandHasReferenceRequiringSpecialization(copy.Source, includeKnownTemplateReferences),
            MirMove move =>
                OperandHasReferenceRequiringSpecialization(move.Target, includeKnownTemplateReferences) ||
                OperandHasReferenceRequiringSpecialization(move.Source, includeKnownTemplateReferences),
            MirAlloc alloc => OperandHasReferenceRequiringSpecialization(alloc.Target, includeKnownTemplateReferences),
            _ => false
        };
    }

    private bool CallFunctionHasReferenceRequiringSpecialization(
        MirFunc function,
        MirCall call,
        ref Dictionary<LocalId, TypeId>? localTypes,
        bool includeKnownTemplateReferences = false)
    {
        if (call.Function is not MirFunctionRef functionRef)
        {
            return OperandHasReferenceRequiringSpecialization(call.Function, includeKnownTemplateReferences);
        }

        if (!FunctionRefRequiresSpecialization(functionRef, includeKnownTemplateReferences))
        {
            return false;
        }

        if (!IsPotentialKeptBuiltinTraitCall(function, functionRef, call))
        {
            return true;
        }

        localTypes ??= BuildLocalTypeMap(function);
        return !ShouldKeepBuiltinTraitCall(function, functionRef, call, localTypes);
    }

    private bool IsPotentialKeptBuiltinTraitCall(
        MirFunc function,
        MirFunctionRef functionRef,
        MirCall call)
    {
        return call.Arguments.Count > 0 &&
               IsBuiltinShowTraitCall(function, functionRef);
    }

    private bool TerminatorHasReferenceRequiringSpecialization(
        MirTerminator terminator,
        bool includeKnownTemplateReferences = false)
    {
        return terminator switch
        {
            MirReturn { Value: not null } ret => OperandHasReferenceRequiringSpecialization(ret.Value, includeKnownTemplateReferences),
            MirSwitch sw =>
                OperandHasReferenceRequiringSpecialization(sw.Discriminant, includeKnownTemplateReferences) ||
                SwitchBranchesHaveReferenceRequiringSpecialization(sw.Branches, includeKnownTemplateReferences),
            _ => false
        };
    }

    private bool OperandsHaveReferenceRequiringSpecialization(
        IReadOnlyList<MirOperand> operands,
        bool includeKnownTemplateReferences = false)
    {
        for (var i = 0; i < operands.Count; i++)
        {
            if (OperandHasReferenceRequiringSpecialization(operands[i], includeKnownTemplateReferences))
            {
                return true;
            }
        }

        return false;
    }

    private bool SwitchBranchesHaveReferenceRequiringSpecialization(
        IReadOnlyList<MirSwitchBranch> branches,
        bool includeKnownTemplateReferences = false)
    {
        for (var i = 0; i < branches.Count; i++)
        {
            if (OperandHasReferenceRequiringSpecialization(branches[i].Value, includeKnownTemplateReferences))
            {
                return true;
            }
        }

        return false;
    }

    private bool OperandHasReferenceRequiringSpecialization(
        MirOperand operand,
        bool includeKnownTemplateReferences = false)
    {
        return operand switch
        {
            MirFunctionRef functionRef => FunctionRefRequiresSpecialization(functionRef, includeKnownTemplateReferences),
            MirPlace place => PlaceHasReferenceRequiringSpecialization(place, includeKnownTemplateReferences),
            _ => false
        };
    }

    private bool PlaceHasReferenceRequiringSpecialization(
        MirPlace place,
        bool includeKnownTemplateReferences = false)
    {
        return place.Base != null && PlaceHasReferenceRequiringSpecialization(place.Base, includeKnownTemplateReferences) ||
               place.Index != null && OperandHasReferenceRequiringSpecialization(place.Index, includeKnownTemplateReferences);
    }

    private bool FunctionRefRequiresSpecialization(
        MirFunctionRef functionRef,
        bool includeKnownTemplateReferences = false)
    {
        return RequiresSpecializationPass(functionRef) ||
               ReferencesGenericTemplateCandidate(functionRef) ||
               includeKnownTemplateReferences && TryResolveTemplateKey(functionRef, out _) ||
               FunctionRefRequiresLateTraitDispatch(functionRef);
    }

    private bool FunctionRefRequiresLateTraitDispatch(MirFunctionRef functionRef)
    {
        return string.Equals(functionRef.Name, "apply", StringComparison.Ordinal) ||
               functionRef.Name.Contains("__apply", StringComparison.Ordinal) ||
               IsPureFunctionName(functionRef.Name) ||
               string.Equals(functionRef.Name, "append", StringComparison.Ordinal) ||
               string.Equals(functionRef.Name, "hash", StringComparison.Ordinal) ||
               functionRef.SymbolId.IsValid && _traitMethodInfoById.ContainsKey(functionRef.SymbolId);
    }

    private static void VisitFunctionRefs(MirFunc function, Action<MirFunctionRef> visitor)
    {
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                VisitFunctionRefs(instruction, visitor);
            }

            if (block.Terminator != null)
            {
                VisitFunctionRefs(block.Terminator, visitor);
            }
        }
    }

    private static void VisitFunctionRefs(MirInstruction instruction, Action<MirFunctionRef> visitor)
    {
        switch (instruction)
        {
            case MirAssign assign:
                VisitFunctionRefs(assign.Target, visitor);
                VisitFunctionRefs(assign.Source, visitor);
                break;
            case MirCaseInject injection:
                VisitFunctionRefs(injection.Target, visitor);
                VisitFunctionRefs(injection.Operand, visitor);
                break;
            case MirCall call:
                VisitFunctionRefs(call.Function, visitor);
                if (call.Target != null)
                {
                    VisitFunctionRefs(call.Target, visitor);
                }
                VisitFunctionRefs(call.Arguments, visitor);
                break;
            case MirBinOp binOp:
                VisitFunctionRefs(binOp.Target, visitor);
                VisitFunctionRefs(binOp.Left, visitor);
                VisitFunctionRefs(binOp.Right, visitor);
                break;
            case MirUnaryOp unaryOp:
                VisitFunctionRefs(unaryOp.Target, visitor);
                VisitFunctionRefs(unaryOp.Operand, visitor);
                break;
            case MirLoad load:
                VisitFunctionRefs(load.Target, visitor);
                VisitFunctionRefs(load.Source, visitor);
                break;
            case MirStore store:
                VisitFunctionRefs(store.Target, visitor);
                VisitFunctionRefs(store.Value, visitor);
                break;
            case MirDrop drop:
                VisitFunctionRefs(drop.Value, visitor);
                break;
            case MirCopy copy:
                VisitFunctionRefs(copy.Target, visitor);
                VisitFunctionRefs(copy.Source, visitor);
                break;
            case MirMove move:
                VisitFunctionRefs(move.Target, visitor);
                VisitFunctionRefs(move.Source, visitor);
                break;
            case MirAlloc alloc:
                VisitFunctionRefs(alloc.Target, visitor);
                break;
        }
    }

    private static void VisitFunctionRefs(MirTerminator terminator, Action<MirFunctionRef> visitor)
    {
        switch (terminator)
        {
            case MirReturn { Value: not null } ret:
                VisitFunctionRefs(ret.Value, visitor);
                break;
            case MirSwitch sw:
                VisitFunctionRefs(sw.Discriminant, visitor);
                foreach (var branch in sw.Branches)
                {
                    VisitFunctionRefs(branch.Value, visitor);
                }
                break;
        }
    }

    private static void VisitFunctionRefs(IReadOnlyList<MirOperand> operands, Action<MirFunctionRef> visitor)
    {
        for (var i = 0; i < operands.Count; i++)
        {
            VisitFunctionRefs(operands[i], visitor);
        }
    }

    private static void VisitFunctionRefs(MirOperand operand, Action<MirFunctionRef> visitor)
    {
        switch (operand)
        {
            case MirFunctionRef functionRef:
                visitor(functionRef);
                break;
            case MirPlace place:
                VisitFunctionRefs(place, visitor);
                break;
        }
    }

    private static void VisitFunctionRefs(MirPlace place, Action<MirFunctionRef> visitor)
    {
        if (place.Base != null)
        {
            VisitFunctionRefs(place.Base, visitor);
        }

        if (place.Index != null)
        {
            VisitFunctionRefs(place.Index, visitor);
        }
    }

    private static bool AnyFunctionRef(MirFunc function, Func<MirFunctionRef, bool> predicate)
    {
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (AnyFunctionRef(instruction, predicate))
                {
                    return true;
                }
            }

            if (block.Terminator != null && AnyFunctionRef(block.Terminator, predicate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyFunctionRef(MirInstruction instruction, Func<MirFunctionRef, bool> predicate)
    {
        return instruction switch
        {
            MirAssign assign => AnyFunctionRef(assign.Target, predicate) || AnyFunctionRef(assign.Source, predicate),
            MirCaseInject injection =>
                AnyFunctionRef(injection.Target, predicate) || AnyFunctionRef(injection.Operand, predicate),
            MirCall call =>
                AnyFunctionRef(call.Function, predicate) ||
                call.Target != null && AnyFunctionRef(call.Target, predicate) ||
                AnyFunctionRef(call.Arguments, predicate),
            MirBinOp binOp =>
                AnyFunctionRef(binOp.Target, predicate) ||
                AnyFunctionRef(binOp.Left, predicate) ||
                AnyFunctionRef(binOp.Right, predicate),
            MirUnaryOp unaryOp => AnyFunctionRef(unaryOp.Target, predicate) || AnyFunctionRef(unaryOp.Operand, predicate),
            MirLoad load => AnyFunctionRef(load.Target, predicate) || AnyFunctionRef(load.Source, predicate),
            MirStore store => AnyFunctionRef(store.Target, predicate) || AnyFunctionRef(store.Value, predicate),
            MirDrop drop => AnyFunctionRef(drop.Value, predicate),
            MirCopy copy => AnyFunctionRef(copy.Target, predicate) || AnyFunctionRef(copy.Source, predicate),
            MirMove move => AnyFunctionRef(move.Target, predicate) || AnyFunctionRef(move.Source, predicate),
            MirAlloc alloc => AnyFunctionRef(alloc.Target, predicate),
            _ => false
        };
    }

    private static bool AnyFunctionRef(MirTerminator terminator, Func<MirFunctionRef, bool> predicate)
    {
        return terminator switch
        {
            MirReturn { Value: not null } ret => AnyFunctionRef(ret.Value, predicate),
            MirSwitch sw => AnyFunctionRef(sw.Discriminant, predicate) || AnyFunctionRef(sw.Branches, predicate),
            _ => false
        };
    }

    private static bool AnyFunctionRef(IReadOnlyList<MirOperand> operands, Func<MirFunctionRef, bool> predicate)
    {
        for (var i = 0; i < operands.Count; i++)
        {
            if (AnyFunctionRef(operands[i], predicate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyFunctionRef(IReadOnlyList<MirSwitchBranch> branches, Func<MirFunctionRef, bool> predicate)
    {
        for (var i = 0; i < branches.Count; i++)
        {
            if (AnyFunctionRef(branches[i].Value, predicate))
            {
                return true;
            }
        }

        return false;
    }

    private static bool AnyFunctionRef(MirOperand operand, Func<MirFunctionRef, bool> predicate)
    {
        return operand switch
        {
            MirFunctionRef functionRef => predicate(functionRef),
            MirPlace place => AnyFunctionRef(place, predicate),
            _ => false
        };
    }

    private static bool AnyFunctionRef(MirPlace place, Func<MirFunctionRef, bool> predicate)
    {
        return place.Base != null && AnyFunctionRef(place.Base, predicate) ||
               place.Index != null && AnyFunctionRef(place.Index, predicate);
    }

    private bool AnyFunctionRefReferencesKnownTemplate(MirFunc function)
    {
        foreach (var block in function.BasicBlocks)
        {
            foreach (var instruction in block.Instructions)
            {
                if (AnyFunctionRefReferencesKnownTemplate(instruction))
                {
                    return true;
                }
            }

            if (block.Terminator != null &&
                AnyFunctionRefReferencesKnownTemplate(block.Terminator))
            {
                return true;
            }
        }

        return false;
    }

    private bool AnyFunctionRefReferencesKnownTemplate(MirInstruction instruction)
    {
        return instruction switch
        {
            MirAssign assign => AnyFunctionRefReferencesKnownTemplate(assign.Target) || AnyFunctionRefReferencesKnownTemplate(assign.Source),
            MirCaseInject injection =>
                AnyFunctionRefReferencesKnownTemplate(injection.Target) ||
                AnyFunctionRefReferencesKnownTemplate(injection.Operand),
            MirCall call =>
                AnyFunctionRefReferencesKnownTemplate(call.Function) ||
                call.Target != null && AnyFunctionRefReferencesKnownTemplate(call.Target) ||
                AnyFunctionRefReferencesKnownTemplate(call.Arguments),
            MirBinOp binOp =>
                AnyFunctionRefReferencesKnownTemplate(binOp.Target) ||
                AnyFunctionRefReferencesKnownTemplate(binOp.Left) ||
                AnyFunctionRefReferencesKnownTemplate(binOp.Right),
            MirUnaryOp unaryOp =>
                AnyFunctionRefReferencesKnownTemplate(unaryOp.Target) ||
                AnyFunctionRefReferencesKnownTemplate(unaryOp.Operand),
            MirLoad load => AnyFunctionRefReferencesKnownTemplate(load.Target) || AnyFunctionRefReferencesKnownTemplate(load.Source),
            MirStore store => AnyFunctionRefReferencesKnownTemplate(store.Target) || AnyFunctionRefReferencesKnownTemplate(store.Value),
            MirDrop drop => AnyFunctionRefReferencesKnownTemplate(drop.Value),
            MirCopy copy => AnyFunctionRefReferencesKnownTemplate(copy.Target) || AnyFunctionRefReferencesKnownTemplate(copy.Source),
            MirMove move => AnyFunctionRefReferencesKnownTemplate(move.Target) || AnyFunctionRefReferencesKnownTemplate(move.Source),
            MirAlloc alloc => AnyFunctionRefReferencesKnownTemplate(alloc.Target),
            _ => false
        };
    }

    private bool AnyFunctionRefReferencesKnownTemplate(MirTerminator terminator)
    {
        return terminator switch
        {
            MirReturn { Value: not null } ret => AnyFunctionRefReferencesKnownTemplate(ret.Value),
            MirSwitch sw => AnyFunctionRefReferencesKnownTemplate(sw.Discriminant) ||
                            AnyFunctionRefReferencesKnownTemplate(sw.Branches),
            _ => false
        };
    }

    private bool AnyFunctionRefReferencesKnownTemplate(IReadOnlyList<MirOperand> operands)
    {
        for (var i = 0; i < operands.Count; i++)
        {
            if (AnyFunctionRefReferencesKnownTemplate(operands[i]))
            {
                return true;
            }
        }

        return false;
    }

    private bool AnyFunctionRefReferencesKnownTemplate(IReadOnlyList<MirSwitchBranch> branches)
    {
        for (var i = 0; i < branches.Count; i++)
        {
            if (AnyFunctionRefReferencesKnownTemplate(branches[i].Value))
            {
                return true;
            }
        }

        return false;
    }

    private bool AnyFunctionRefReferencesKnownTemplate(MirOperand operand)
    {
        return operand switch
        {
            MirFunctionRef functionRef => TryResolveTemplateKey(functionRef, out _),
            MirPlace place => AnyFunctionRefReferencesKnownTemplate(place),
            _ => false
        };
    }

    private bool AnyFunctionRefReferencesKnownTemplate(MirPlace place)
    {
        return place.Base != null && AnyFunctionRefReferencesKnownTemplate(place.Base) ||
               place.Index != null && AnyFunctionRefReferencesKnownTemplate(place.Index);
    }
}
