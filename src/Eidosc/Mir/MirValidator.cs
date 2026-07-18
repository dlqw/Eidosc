using Eidosc.Utils;
using Eidosc.Types;
using DiagnosticMessages = Eidosc.Diagnostic.DiagnosticMessages;
using EidoscDiagnostic = Eidosc.Diagnostic.Diagnostic;
using EidoscDiagnosticLevel = Eidosc.Diagnostic.DiagnosticLevel;

namespace Eidosc.Mir;

/// <summary>
/// Validates MIR invariants that must hold before later backend phases consume the module.
/// </summary>
public sealed class MirValidator
{
    public const string PoisonOperandCode = "E5334";
    public const string UnsupportedMirNodeCode = "E5335";
    public const string MissingTerminatorCode = "E5336";
    public const string UnknownTypeIdCode = "E5337";
    public const string MissingFunctionIdentityCode = "E5338";
    public const string HandlerScopeBalanceCode = "E5339";
    public const string InvalidDropCode = "E5340";
    public const string DropAfterMoveCode = "E5341";

    private readonly List<EidoscDiagnostic> _diagnostics = [];
    private readonly HashSet<string> _reportedPoisonSites = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedUnknownTypeSites = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reportedFunctionIdentitySites = new(StringComparer.Ordinal);

    public IReadOnlyList<EidoscDiagnostic> Diagnostics => _diagnostics;

    public bool Validate(MirModule module)
    {
        _diagnostics.Clear();
        _reportedPoisonSites.Clear();
        _reportedUnknownTypeSites.Clear();
        _reportedFunctionIdentitySites.Clear();

        foreach (var function in module.Functions)
        {
            ValidateFunction(module, function);
        }

        return _diagnostics.All(static diagnostic => diagnostic.Level != EidoscDiagnosticLevel.Error);
    }

    private void ValidateFunction(MirModule module, MirFunc function)
    {
        var blockIds = function.BasicBlocks.Select(static block => block.Id).ToHashSet();
        var allowOpenSignatureTypes = AllowsTypeErasedSignature(function, module.TypeDescriptors, module.DynamicTypeKeys);
        var localTypes = function.Locals.ToDictionary(static local => local.Id);
        var allowedTypeErasedCallableLocals = CollectAllowedTypeErasedCallableLocals(module, function, allowOpenSignatureTypes);

        ValidateFunctionReturnType(module, function, allowOpenSignatureTypes);
        foreach (var local in function.Locals)
        {
            ValidateLocalTypeId(
                local,
                module,
                function,
                allowOpenSignatureTypes,
                allowedTypeErasedCallableLocals);
        }

        if (function.IsExternal || IsIntrinsicDeclaration(function))
        {
            return;
        }

        ValidateEntryBlock(function, blockIds);
        ValidateMoveDropState(function);

        foreach (var block in function.BasicBlocks)
        {
            for (var index = 0; index < block.Instructions.Count; index++)
            {
                var instruction = block.Instructions[index];
                ValidateInstructionKind(instruction, function, block, index);
                ValidateInstructionOperandShapes(instruction, function, block, index);
                ValidateInstructionTypeMetadata(instruction, module, function, block, index);
                foreach (var operand in EnumerateInstructionOperands(instruction))
                {
                    ValidateOperand(
                        operand,
                        module,
                        function,
                        block,
                        index,
                        "instruction operand",
                        allowOpenSignatureTypes,
                        allowedTypeErasedCallableLocals,
                        localTypes);
                }
            }

            if (block.Terminator != null)
            {
                ValidateTerminatorKind(block.Terminator, function, block);
                ValidateTerminatorTargets(block.Terminator, function, block, blockIds);
                foreach (var operand in EnumerateTerminatorOperands(block.Terminator))
                {
                    ValidateOperand(
                        operand,
                        module,
                        function,
                        block,
                        null,
                        "terminator operand",
                        allowOpenSignatureTypes,
                        allowedTypeErasedCallableLocals,
                        localTypes);
                }
            }
            else
            {
                ReportMissingTerminator(function, block);
            }
        }
    }

    private static bool IsIntrinsicDeclaration(MirFunc function)
    {
        return function.IntrinsicName != null && function.BasicBlocks.Count == 0;
    }

    private void ValidateFunctionReturnType(
        MirModule module,
        MirFunc function,
        bool allowOpenSignatureTypes)
    {
        if (!function.ReturnType.IsValid && allowOpenSignatureTypes)
        {
            return;
        }

        ValidateRequiredTypeId(
            function.ReturnType,
            module,
            function,
            null,
            null,
            "function return type",
            function.Span,
            DiagnosticMessages.MirFunctionRequiresConcreteReturnType);
    }

    private void ValidateLocalTypeId(
        MirLocal local,
        MirModule module,
        MirFunc function,
        bool allowOpenSignatureTypes,
        IReadOnlySet<LocalId> allowedTypeErasedCallableLocals)
    {
        var role = $"local '{local.Name}' type";
        if (!local.TypeId.IsValid)
        {
            if ((allowOpenSignatureTypes && local.IsParameter) ||
                allowedTypeErasedCallableLocals.Contains(local.Id))
            {
                return;
            }

            ReportMissingTypeId(
                role,
                local.Span,
                function,
                null,
                null,
                DiagnosticMessages.OnlyGenericPartialPlaceholdersMayRemainTypeErased);
            return;
        }

        ValidateTypeId(local.TypeId, module, function, null, null, role, local.Span);
    }

    private void ValidateEntryBlock(MirFunc function, IReadOnlySet<BlockId> blockIds)
    {
        if (blockIds.Contains(function.EntryBlockId))
        {
            return;
        }

        ReportMissingBlockTarget(
            "entry block",
            function.EntryBlockId,
            function.Span,
            function,
            null);
    }

    private void ValidateTerminatorTargets(
        MirTerminator terminator,
        MirFunc function,
        MirBasicBlock block,
        IReadOnlySet<BlockId> blockIds)
    {
        switch (terminator)
        {
            case MirGoto jump:
                ValidateBlockTarget(jump.Target, "goto target block", jump.Span, function, block, blockIds);
                break;
            case MirSwitch sw:
                for (var index = 0; index < sw.Branches.Count; index++)
                {
                    ValidateBlockTarget(
                        sw.Branches[index].Target,
                        $"switch branch {index} target block",
                        sw.Span,
                        function,
                        block,
                        blockIds);
                }

                if (sw.DefaultTarget.HasValue)
                {
                    ValidateBlockTarget(
                        sw.DefaultTarget.Value,
                        "switch default target block",
                        sw.Span,
                        function,
                        block,
                        blockIds);
                }

                break;
        }
    }

    private void ValidateBlockTarget(
        BlockId target,
        string role,
        SourceSpan span,
        MirFunc function,
        MirBasicBlock block,
        IReadOnlySet<BlockId> blockIds)
    {
        if (blockIds.Contains(target))
        {
            return;
        }

        ReportMissingBlockTarget(role, target, span, function, block);
    }

    private void ValidateInstructionOperandShapes(
        MirInstruction instruction,
        MirFunc function,
        MirBasicBlock block,
        int instructionIndex)
    {
        switch (instruction)
        {
            case MirBinOp { Target: not (MirPlace or MirTemp) } binOp:
                ReportUnsupportedTargetOperand(binOp.Target, binOp.Span, function, block, instructionIndex);
                break;
            case MirBinOp { Target: MirPlace { Kind: not PlaceKind.Local } target } binOp:
                ReportUnsupportedPlaceRole(target, binOp.Span, function, block, instructionIndex, "binary operation target place");
                break;
            case MirUnaryOp { Target: not (MirPlace or MirTemp) } unaryOp:
                ReportUnsupportedTargetOperand(unaryOp.Target, unaryOp.Span, function, block, instructionIndex);
                break;
            case MirUnaryOp { Target: MirPlace { Kind: not PlaceKind.Local } target } unaryOp:
                ReportUnsupportedPlaceRole(target, unaryOp.Span, function, block, instructionIndex, "unary operation target place");
                break;
            case MirAssign { Target.Kind: not PlaceKind.Local } assign:
                ReportUnsupportedPlaceRole(assign.Target, assign.Span, function, block, instructionIndex, "assign target place");
                break;
            case MirCaseInject { Target: not MirPlace } injection:
                ReportUnsupportedTargetOperand(injection.Target, injection.Span, function, block, instructionIndex);
                break;
            case MirCaseInject { Target: MirPlace { Kind: not PlaceKind.Local } target } injection:
                ReportUnsupportedPlaceRole(target, injection.Span, function, block, instructionIndex, "case injection target place");
                break;
            case MirCall { Target: { Kind: not PlaceKind.Local } } call:
                ReportUnsupportedPlaceRole(call.Target, call.Span, function, block, instructionIndex, "call target place");
                break;
            case MirAlloc { Target.Kind: not PlaceKind.Local } alloc:
                ReportUnsupportedPlaceRole(alloc.Target, alloc.Span, function, block, instructionIndex, "alloc target place");
                break;
            case MirLoad { Target.Kind: not PlaceKind.Local } load:
                ReportUnsupportedPlaceRole(load.Target, load.Span, function, block, instructionIndex, "load target place");
                break;
            case MirCopy { Target.Kind: not PlaceKind.Local } copy:
                ReportUnsupportedPlaceRole(copy.Target, copy.Span, function, block, instructionIndex, "copy target place");
                break;
            case MirCopy { Source.Kind: not PlaceKind.Local } copy:
                ReportUnsupportedPlaceRole(copy.Source, copy.Span, function, block, instructionIndex, "copy source place");
                break;
            case MirMove { Target.Kind: not PlaceKind.Local } move:
                ReportUnsupportedPlaceRole(move.Target, move.Span, function, block, instructionIndex, "move target place");
                break;
            case MirMove { Source.Kind: not PlaceKind.Local } move:
                ReportUnsupportedPlaceRole(move.Source, move.Span, function, block, instructionIndex, "move source place");
                break;
        }
    }

    private void ValidateInstructionTypeMetadata(
        MirInstruction instruction,
        MirModule module,
        MirFunc function,
        MirBasicBlock block,
        int instructionIndex)
    {
        switch (instruction)
        {
            case MirCaseInject injection:
                ValidateRequiredTypeId(
                    injection.SourceTypeId,
                    module,
                    function,
                    block,
                    instructionIndex,
                    "case injection source type",
                    injection.Span,
                    "case injection requires a concrete source type");
                ValidateRequiredTypeId(
                    injection.TargetTypeId,
                    module,
                    function,
                    block,
                    instructionIndex,
                    "case injection target type",
                    injection.Span,
                    "case injection requires a concrete target type");
                break;
            case MirAlloc alloc:
                ValidateRequiredTypeId(
                    alloc.TypeId,
                    module,
                    function,
                    block,
                    instructionIndex,
                    "alloc type",
                    alloc.Span,
                    DiagnosticMessages.MirAllocRequiresConcreteAllocationType);
                break;
            case MirDrop drop:
                ValidateDrop(drop, module, function, block, instructionIndex);
                break;
        }
    }

    private void ValidateDrop(
        MirDrop drop,
        MirModule module,
        MirFunc function,
        MirBasicBlock block,
        int instructionIndex)
    {
        if (drop.Value.TypeId.IsValid &&
            MirGenericAnalysis.ContainsOpenTypeVariable(drop.Value.TypeId, module.TypeDescriptors, module.DynamicTypeKeys))
        {
            ReportMirInvariant(
                InvalidDropCode,
                "MirDrop cannot release an open type variable",
                drop.Span,
                function,
                block,
                instructionIndex,
                "drop operand");
        }
    }

    private static IEnumerable<BlockId> EnumerateSuccessorBlocks(MirTerminator? terminator)
    {
        switch (terminator)
        {
            case MirGoto jump:
                yield return jump.Target;
                break;
            case MirSwitch sw:
                foreach (var branch in sw.Branches)
                {
                    yield return branch.Target;
                }

                if (sw.DefaultTarget.HasValue)
                {
                    yield return sw.DefaultTarget.Value;
                }

                break;
        }
    }

    private void ValidateMoveDropState(MirFunc function)
    {
        var blockMap = function.BasicBlocks.ToDictionary(static block => block.Id);
        if (!blockMap.TryGetValue(function.EntryBlockId, out var entryBlock))
        {
            return;
        }

        var entryStates = new Dictionary<BlockId, HashSet<LocalId>>
        {
            [entryBlock.Id] = []
        };
        var queue = new Queue<MirBasicBlock>();
        var enqueued = new HashSet<BlockId> { entryBlock.Id };
        queue.Enqueue(entryBlock);

        while (queue.Count > 0)
        {
            var block = queue.Dequeue();
            enqueued.Remove(block.Id);

            var state = new HashSet<LocalId>(entryStates[block.Id]);
            ValidateMoveDropStateInBlock(function, block, state);

            foreach (var successorId in EnumerateSuccessorBlocks(block.Terminator))
            {
                if (!blockMap.TryGetValue(successorId, out var successor))
                {
                    continue;
                }

                if (!entryStates.TryGetValue(successorId, out var existingState))
                {
                    entryStates[successorId] = new HashSet<LocalId>(state);
                    if (enqueued.Add(successorId))
                    {
                        queue.Enqueue(successor);
                    }

                    continue;
                }

                var changed = false;
                foreach (var local in state)
                {
                    changed |= existingState.Add(local);
                }

                if (changed && enqueued.Add(successorId))
                {
                    queue.Enqueue(successor);
                }
            }
        }
    }

    private void ValidateMoveDropStateInBlock(MirFunc function, MirBasicBlock block, HashSet<LocalId> movedLocals)
    {
        for (var index = 0; index < block.Instructions.Count; index++)
        {
            var instruction = block.Instructions[index];
            if (instruction is MirDrop drop &&
                TryGetLocalOperand(drop.Value, out var droppedLocal))
            {
                if (movedLocals.Contains(droppedLocal))
                {
                    ReportMirInvariant(
                        DropAfterMoveCode,
                        "MirDrop cannot release a local after ownership has moved",
                        drop.Span,
                        function,
                        block,
                        index,
                        "drop operand");
                }

                movedLocals.Add(droppedLocal);
                continue;
            }

            if (instruction is MirMove move)
            {
                if (TryGetDefinedLocal(instruction, out var moveTarget) &&
                    (!TryGetLocalOperand(move.Source, out var moveSource) || moveTarget != moveSource))
                {
                    movedLocals.Remove(moveTarget);
                }

                if (TryGetLocalOperand(move.Source, out var movedLocal))
                {
                    movedLocals.Add(movedLocal);
                }

                continue;
            }

            if (TryGetDefinedLocal(instruction, out var definedLocal))
            {
                movedLocals.Remove(definedLocal);
            }
        }

    }

    private void ValidateRequiredTypeId(
        TypeId typeId,
        MirModule module,
        MirFunc function,
        MirBasicBlock? block,
        int? instructionIndex,
        string role,
        SourceSpan span,
        string missingNote)
    {
        if (typeId.IsValid)
        {
            ValidateTypeId(typeId, module, function, block, instructionIndex, role, span);
            return;
        }

        ReportMissingTypeId(role, span, function, block, instructionIndex, missingNote);
    }

    private void ValidateInstructionKind(MirInstruction instruction, MirFunc function, MirBasicBlock block, int instructionIndex)
    {
        if (instruction is MirAssign or
            MirCaseInject or
            MirCall or
            MirBinOp or
            MirUnaryOp or
            MirLoad or
            MirStore or
            MirDrop or
            MirCopy or
            MirMove or
            MirAlloc)
        {
            return;
        }

        ReportUnsupportedMirNode(
            instruction.GetType().Name,
            instruction.Span,
            function,
            block,
            instructionIndex,
            "instruction");
    }

    private void ValidateTerminatorKind(MirTerminator terminator, MirFunc function, MirBasicBlock block)
    {
        if (terminator is MirReturn or MirGoto or MirSwitch or MirUnreachable)
        {
            return;
        }

        ReportUnsupportedMirNode(
            terminator.GetType().Name,
            terminator.Span,
            function,
            block,
            null,
            "terminator");
    }

    private void ReportUnsupportedTargetOperand(
        MirOperand? operand,
        SourceSpan instructionSpan,
        MirFunc function,
        MirBasicBlock block,
        int instructionIndex)
    {
        ReportUnsupportedMirNode(
            operand?.GetType().Name ?? "null",
            operand?.Span ?? instructionSpan,
            function,
            block,
            instructionIndex,
            "target operand");
    }

    private void ReportUnsupportedPlaceRole(
        MirPlace? place,
        SourceSpan instructionSpan,
        MirFunc function,
        MirBasicBlock block,
        int instructionIndex,
        string role)
    {
        ReportUnsupportedMirNode(
            place?.Kind.ToString() ?? "null",
            place?.Span ?? instructionSpan,
            function,
            block,
            instructionIndex,
            role);
    }

    private void ReportUnsupportedMirNode(
        string typeName,
        SourceSpan span,
        MirFunc function,
        MirBasicBlock block,
        int? instructionIndex,
        string role)
    {
        var diagnostic = EidoscDiagnostic.Error(
            DiagnosticMessages.MirUnsupportedNode(role, typeName),
            UnsupportedMirNodeCode);
        if (HasSpan(span))
        {
            diagnostic.WithLabel(span, DiagnosticMessages.MirUnsupportedNodeLabel(role));
        }

        diagnostic.WithNote(DiagnosticMessages.FunctionNote(function.Name));
        diagnostic.WithNote(DiagnosticMessages.MirLocationNote(
            block.Id.Value,
            instructionIndex?.ToString() ?? "terminator"));
        diagnostic.WithNote(DiagnosticMessages.MirUnsupportedNodeHelp);
        _diagnostics.Add(diagnostic);
    }

    private void ReportMirInvariant(
        string code,
        string message,
        SourceSpan span,
        MirFunc function,
        MirBasicBlock block,
        int instructionIndex,
        string role)
    {
        var diagnostic = EidoscDiagnostic.Error(message, code);
        if (HasSpan(span))
        {
            diagnostic.WithLabel(span, $"Invalid MIR {role}.");
        }

        diagnostic.WithNote(DiagnosticMessages.FunctionNote(function.Name));
        diagnostic.WithNote(DiagnosticMessages.MirLocationNote(block.Id.Value, instructionIndex.ToString()));
        diagnostic.WithNote("MIR resource lifetime invariants must hold before LLVM lowering.");
        _diagnostics.Add(diagnostic);
    }

    private static bool TryGetDefinedLocal(MirInstruction instruction, out LocalId localId)
    {
        localId = default;
        var place = instruction switch
        {
            MirAssign assign => assign.Target,
            MirCall call => call.Target,
            MirBinOp { Target: MirPlace target } => target,
            MirUnaryOp { Target: MirPlace target } => target,
            MirLoad load => load.Target,
            MirCopy copy => copy.Target,
            MirMove move => move.Target,
            MirAlloc alloc => alloc.Target,
            _ => null
        };

        return TryGetLocalOperand(place, out localId);
    }

    private static bool TryGetLocalOperand(MirOperand? operand, out LocalId localId)
    {
        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var operandLocal })
        {
            localId = operandLocal;
            return true;
        }

        localId = default;
        return false;
    }

    private static IEnumerable<LocalId> EnumerateUsedLocals(MirInstruction instruction)
    {
        foreach (var operand in EnumerateInstructionUseOperands(instruction))
        {
            foreach (var local in EnumerateLocalUses(operand))
            {
                yield return local;
            }
        }
    }

    private static IEnumerable<LocalId> EnumerateUsedLocals(MirTerminator terminator)
    {
        foreach (var operand in EnumerateTerminatorOperands(terminator))
        {
            foreach (var local in EnumerateLocalUses(operand))
            {
                yield return local;
            }
        }
    }

    private static IEnumerable<MirOperand?> EnumerateInstructionUseOperands(MirInstruction instruction)
    {
        switch (instruction)
        {
            case MirAssign assign:
                yield return assign.Source;
                yield return assign.Target.Base;
                yield return assign.Target.Index;
                break;
            case MirCall call:
                yield return call.Function;
                foreach (var argument in call.Arguments)
                {
                    yield return argument;
                }

                break;
            case MirBinOp binOp:
                yield return binOp.Left;
                yield return binOp.Right;
                break;
            case MirUnaryOp unaryOp:
                yield return unaryOp.Operand;
                break;
            case MirLoad load:
                yield return load.Source;
                break;
            case MirStore store:
                yield return store.Target;
                yield return store.Value;
                break;
            case MirDrop drop:
                yield return drop.Value;
                break;
            case MirCopy copy:
                yield return copy.Source;
                break;
            case MirMove move:
                yield return move.Source;
                break;
        }
    }

    private static IEnumerable<LocalId> EnumerateLocalUses(MirOperand? operand)
    {
        if (operand == null)
        {
            yield break;
        }

        if (operand is MirPlace { Kind: PlaceKind.Local, Local: var local })
        {
            yield return local;
        }

        if (operand is not MirPlace place)
        {
            yield break;
        }

        foreach (var baseLocal in EnumerateLocalUses(place.Base))
        {
            yield return baseLocal;
        }

        foreach (var indexLocal in EnumerateLocalUses(place.Index))
        {
            yield return indexLocal;
        }
    }

    private void ReportMissingTerminator(MirFunc function, MirBasicBlock block)
    {
        var diagnostic = EidoscDiagnostic.Error(
            DiagnosticMessages.MirMissingTerminator,
            MissingTerminatorCode);
        if (HasSpan(block.Span))
        {
            diagnostic.WithLabel(block.Span, DiagnosticMessages.MirMissingTerminatorLabel);
        }

        diagnostic.WithNote(DiagnosticMessages.FunctionNote(function.Name));
        diagnostic.WithNote(DiagnosticMessages.MirLocationNote(block.Id.Value, DiagnosticMessages.MirTerminatorRole));
        diagnostic.WithNote(DiagnosticMessages.MirMissingTerminatorHelp);
        _diagnostics.Add(diagnostic);
    }

    private void ReportMissingBlockTarget(
        string role,
        BlockId target,
        SourceSpan span,
        MirFunc function,
        MirBasicBlock? block)
    {
        var diagnostic = EidoscDiagnostic.Error(
            DiagnosticMessages.MirMissingBlockTarget(role, target),
            UnsupportedMirNodeCode);
        if (HasSpan(span))
        {
            diagnostic.WithLabel(span, DiagnosticMessages.MirMissingBlockTargetLabel);
        }

        diagnostic.WithNote(DiagnosticMessages.FunctionNote(function.Name));
        if (block != null)
        {
            diagnostic.WithNote(DiagnosticMessages.MirLocationNote(block.Id.Value, DiagnosticMessages.MirTerminatorRole));
        }

        diagnostic.WithNote(DiagnosticMessages.TargetNote(target));
        diagnostic.WithNote(DiagnosticMessages.MirMissingBlockTargetHelp);
        _diagnostics.Add(diagnostic);
    }

    private void ValidateOperand(
        MirOperand? operand,
        MirModule module,
        MirFunc function,
        MirBasicBlock block,
        int? instructionIndex,
        string role,
        bool allowOpenSignatureTypes,
        IReadOnlySet<LocalId> allowedTypeErasedCallableLocals,
        IReadOnlyDictionary<LocalId, MirLocal> localTypes)
    {
        if (operand == null)
        {
            return;
        }

        foreach (var nested in EnumerateOperandTree(operand))
        {
            if (!nested.TypeId.IsValid)
            {
                if (!IsAllowedTypeErasedOperand(
                        nested,
                        module,
                        function,
                        allowOpenSignatureTypes,
                        allowedTypeErasedCallableLocals,
                        localTypes))
                {
                    ReportMissingTypeId(
                        role,
                        nested.Span,
                        function,
                        block,
                        instructionIndex,
                        DiagnosticMessages.OnlyGenericPartialPlaceholdersMayRemainTypeErased);
                }
            }
            else
            {
                ValidateTypeId(nested.TypeId, module, function, block, instructionIndex, role, nested.Span);
            }

            if (nested is MirPlace place)
            {
                ValidatePlaceKind(place, function, block, instructionIndex);
            }

            if (nested is MirFunctionRef functionRef)
            {
                ValidateFunctionRefIdentity(functionRef, function, block, instructionIndex);
            }

            if (nested is not MirPoison poison)
            {
                continue;
            }

            ReportPoisonOperand(poison, function, block, instructionIndex, role);
        }
    }

    private void ValidateFunctionRefIdentity(
        MirFunctionRef functionRef,
        MirFunc function,
        MirBasicBlock block,
        int? instructionIndex)
    {
        if (HasStructuredFunctionRefIdentity(functionRef))
        {
            return;
        }

        var blockText = block.Id.Value.ToString();
        var functionName = string.IsNullOrWhiteSpace(functionRef.Name)
            ? "<anonymous>"
            : functionRef.Name;
        var site = $"{function.Name}:{blockText}:{instructionIndex?.ToString() ?? "term"}:{functionName}:identity";
        if (!_reportedFunctionIdentitySites.Add(site))
        {
            return;
        }

        var diagnostic = EidoscDiagnostic.Error(
            DiagnosticMessages.MirMissingFunctionIdentity(functionName),
            MissingFunctionIdentityCode);
        if (HasSpan(functionRef.Span))
        {
            diagnostic.WithLabel(functionRef.Span, DiagnosticMessages.MirMissingFunctionIdentityLabel);
        }

        diagnostic.WithNote(DiagnosticMessages.FunctionNote(function.Name));
        diagnostic.WithNote(DiagnosticMessages.MirLocationNote(
            block.Id.Value,
            instructionIndex?.ToString() ?? "terminator"));
        diagnostic.WithNote(DiagnosticMessages.MirMissingFunctionIdentityHelp);
        _diagnostics.Add(diagnostic);
    }

    private static bool HasStructuredFunctionRefIdentity(MirFunctionRef functionRef)
    {
        if (functionRef.SymbolId.IsValid)
        {
            return true;
        }

        var functionId = functionRef.FunctionId;
        return functionId.SymbolId.IsValid ||
               !string.IsNullOrWhiteSpace(functionId.QualifiedName) ||
               (!string.IsNullOrWhiteSpace(functionId.ModuleIdentityKey) &&
                !string.IsNullOrWhiteSpace(functionId.Name)) ||
               (!string.IsNullOrWhiteSpace(functionId.Module) &&
                !string.IsNullOrWhiteSpace(functionId.Name));
    }

    private static HashSet<LocalId> CollectAllowedTypeErasedCallableLocals(
        MirModule module,
        MirFunc function,
        bool allowOpenSignatureTypes)
    {
        var result = new HashSet<LocalId>();
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var call in function.BasicBlocks.SelectMany(static block => block.Instructions).OfType<MirCall>())
            {
                if (call.Target is not { Kind: PlaceKind.Local, TypeId.IsValid: false } target)
                {
                    continue;
                }

                if (IsAllowedTypeErasedCallableSource(module, function, call.Function, allowOpenSignatureTypes, result) &&
                    result.Add(target.Local))
                {
                    changed = true;
                }
            }
        }

        return result;
    }

    private static bool IsAllowedTypeErasedOperand(
        MirOperand operand,
        MirModule module,
        MirFunc function,
        bool allowOpenSignatureTypes,
        IReadOnlySet<LocalId> allowedTypeErasedCallableLocals,
        IReadOnlyDictionary<LocalId, MirLocal> localTypes)
    {
        return operand switch
        {
            MirPoison => true,
            MirFunctionRef functionRef => IsAllowedTypeErasedFunctionRef(module, functionRef, allowOpenSignatureTypes),
            MirPlace { Kind: PlaceKind.Local } place => IsAllowedTypeErasedLocal(
                place.Local,
                allowOpenSignatureTypes,
                allowedTypeErasedCallableLocals,
                localTypes),
            _ => false
        };
    }

    private static bool IsAllowedTypeErasedCallableSource(
        MirModule module,
        MirFunc function,
        MirOperand callable,
        bool allowOpenSignatureTypes,
        IReadOnlySet<LocalId> allowedTypeErasedCallableLocals)
    {
        return callable switch
        {
            MirFunctionRef functionRef => IsAllowedTypeErasedFunctionRef(module, functionRef, allowOpenSignatureTypes),
            MirPlace { Kind: PlaceKind.Local } place => allowedTypeErasedCallableLocals.Contains(place.Local),
            _ => false
        };
    }

    private static bool IsAllowedTypeErasedLocal(
        LocalId localId,
        bool allowOpenSignatureTypes,
        IReadOnlySet<LocalId> allowedTypeErasedCallableLocals,
        IReadOnlyDictionary<LocalId, MirLocal> localTypes)
    {
        if (allowedTypeErasedCallableLocals.Contains(localId))
        {
            return true;
        }

        return allowOpenSignatureTypes &&
               localTypes.TryGetValue(localId, out var local) &&
               local.IsParameter;
    }

    private static bool IsAllowedTypeErasedFunctionRef(
        MirModule module,
        MirFunctionRef functionRef,
        bool allowOpenSignatureTypes)
    {
        if (TryResolveFunction(module, functionRef, out var referencedFunction))
        {
            return true;
        }

        return allowOpenSignatureTypes && !string.IsNullOrWhiteSpace(functionRef.Name);
    }

    private static bool TryResolveFunction(MirModule module, MirFunctionRef functionRef, out MirFunc function)
    {
        if (functionRef.SymbolId.IsValid)
        {
            foreach (var candidate in module.Functions)
            {
                if (candidate.SymbolId == functionRef.SymbolId)
                {
                    function = candidate;
                    return true;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(functionRef.Name))
        {
            foreach (var candidate in module.Functions)
            {
                if (string.Equals(candidate.Name, functionRef.Name, StringComparison.Ordinal))
                {
                    function = candidate;
                    return true;
                }
            }
        }

        function = null!;
        return false;
    }

    private void ValidatePlaceKind(
        MirPlace place,
        MirFunc function,
        MirBasicBlock block,
        int? instructionIndex)
    {
        if (place.Kind is PlaceKind.Local or PlaceKind.Field or PlaceKind.Index or PlaceKind.Deref)
        {
            ValidateIndexAccessKind(place, function, block, instructionIndex);
            ValidatePlaceShape(place, function, block, instructionIndex);
            return;
        }

        ReportUnsupportedMirNode(
            ((int)place.Kind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            place.Span,
            function,
            block,
            instructionIndex,
            "place kind");
    }

    private void ValidatePlaceShape(
        MirPlace place,
        MirFunc function,
        MirBasicBlock block,
        int? instructionIndex)
    {
        switch (place.Kind)
        {
            case PlaceKind.Local:
                if (!place.Local.IsValid)
                {
                    ReportInvalidPlaceShape("local place id", place, function, block, instructionIndex);
                }

                break;
            case PlaceKind.Field:
                if (place.Base == null)
                {
                    ReportInvalidPlaceShape("field place base", place, function, block, instructionIndex);
                }

                if (string.IsNullOrWhiteSpace(place.FieldName))
                {
                    ReportInvalidPlaceShape("field place name", place, function, block, instructionIndex);
                }

                break;
            case PlaceKind.Index:
                if (place.Base == null)
                {
                    ReportInvalidPlaceShape("index place base", place, function, block, instructionIndex);
                }

                if (place.Index == null)
                {
                    ReportInvalidPlaceShape("index place operand", place, function, block, instructionIndex);
                }

                break;
            case PlaceKind.Deref:
                if (place.Base == null)
                {
                    ReportInvalidPlaceShape("deref place base", place, function, block, instructionIndex);
                }

                break;
        }
    }

    private void ReportInvalidPlaceShape(
        string role,
        MirPlace place,
        MirFunc function,
        MirBasicBlock block,
        int? instructionIndex)
    {
        ReportUnsupportedMirNode(
            "missing",
            place.Span,
            function,
            block,
            instructionIndex,
            role);
    }

    private void ValidateIndexAccessKind(
        MirPlace place,
        MirFunc function,
        MirBasicBlock block,
        int? instructionIndex)
    {
        if (place.Kind != PlaceKind.Index ||
            place.IndexAccessKind is MirIndexAccessKind.Aggregate or MirIndexAccessKind.RuntimeArray)
        {
            return;
        }

        ReportUnsupportedMirNode(
            ((int)place.IndexAccessKind).ToString(System.Globalization.CultureInfo.InvariantCulture),
            place.Span,
            function,
            block,
            instructionIndex,
            "index access kind");
    }

    private void ValidateTypeId(
        TypeId typeId,
        MirModule module,
        MirFunc function,
        MirBasicBlock? block,
        int? instructionIndex,
        string role,
        SourceSpan span)
    {
        if (!typeId.IsValid || IsKnownLoweringTypeId(typeId, module))
        {
            return;
        }

        var blockText = block?.Id.Value.ToString() ?? "function";
        var site = $"{function.Name}:{blockText}:{instructionIndex?.ToString() ?? "meta"}:{role}:{typeId.Value}";
        if (!_reportedUnknownTypeSites.Add(site))
        {
            return;
        }

        var diagnostic = EidoscDiagnostic.Error(
            DiagnosticMessages.MirUnknownTypeId(typeId),
            UnknownTypeIdCode);
        if (HasSpan(span))
        {
            diagnostic.WithLabel(span, DiagnosticMessages.MirUnknownTypeIdLabel);
        }

        diagnostic.WithNote(DiagnosticMessages.FunctionNote(function.Name));
        if (block != null)
        {
            diagnostic.WithNote(DiagnosticMessages.MirLocationNote(
                block.Id.Value,
                instructionIndex?.ToString() ?? "terminator"));
        }

        diagnostic.WithNote(DiagnosticMessages.RoleNote(role));
        diagnostic.WithNote(DiagnosticMessages.MirExpectedKnownTypeIdNote);
        _diagnostics.Add(diagnostic);
    }

    private void ReportMissingTypeId(
        string role,
        SourceSpan span,
        MirFunc function,
        MirBasicBlock? block,
        int? instructionIndex,
        string note)
    {
        var blockText = block?.Id.Value.ToString() ?? "function";
        var site = $"{function.Name}:{blockText}:{instructionIndex?.ToString() ?? "meta"}:{role}:missing";
        if (!_reportedUnknownTypeSites.Add(site))
        {
            return;
        }

        var diagnostic = EidoscDiagnostic.Error(
            DiagnosticMessages.MirMissingTypeId(role),
            UnknownTypeIdCode);
        if (HasSpan(span))
        {
            diagnostic.WithLabel(span, DiagnosticMessages.MirMissingTypeIdLabel);
        }

        diagnostic.WithNote(DiagnosticMessages.FunctionNote(function.Name));
        if (block != null)
        {
            diagnostic.WithNote(DiagnosticMessages.MirLocationNote(
                block.Id.Value,
                instructionIndex?.ToString() ?? "terminator"));
        }

        diagnostic.WithNote(DiagnosticMessages.RoleNote(role));
        diagnostic.WithNote(note);
        _diagnostics.Add(diagnostic);
    }

    private static bool IsKnownLoweringTypeId(TypeId typeId, MirModule module)
    {
        return IsBuiltinLoweringType(typeId) ||
               module.TypeDescriptors.ContainsKey(typeId.Value) ||
               module.DynamicTypeKeys.ContainsKey(typeId.Value) ||
               module.ConstructorLayouts.ContainsKey(typeId.Value);
    }

    private static bool IsBuiltinLoweringType(TypeId typeId)
    {
        return typeId.Value is
            BaseTypes.IntId or
            BaseTypes.FloatId or
            BaseTypes.BoolId or
            BaseTypes.StringId or
            BaseTypes.CharId or
            BaseTypes.UnitId or
            BaseTypes.TypeEqId or
            BaseTypes.NeverId or
            BaseTypes.ErasedCallableId or
            BaseTypes.RawPtrId or
            BaseTypes.CfnId;
    }

    private static bool AllowsTypeErasedSignature(
        MirFunc function,
        IReadOnlyDictionary<int, TypeDescriptor> typeDescriptors,
        IReadOnlyDictionary<int, string> dynamicTypeKeys)
    {
        if (function.GenericParameterCount > 0)
        {
            return true;
        }

        if (MirGenericAnalysis.ContainsOpenTypeVariable(function.ReturnType, typeDescriptors, dynamicTypeKeys))
        {
            return true;
        }

        return function.Locals.Any(local =>
            local.IsParameter &&
            MirGenericAnalysis.ContainsOpenTypeVariable(local.TypeId, typeDescriptors, dynamicTypeKeys));
    }

    private void ReportPoisonOperand(
        MirPoison poison,
        MirFunc function,
        MirBasicBlock block,
        int? instructionIndex,
        string role)
    {
        var site = $"{function.Name}:{block.Id.Value}:{instructionIndex?.ToString() ?? "term"}:{poison.Span.Location.Position}:{poison.Reason}";
        if (!_reportedPoisonSites.Add(site))
        {
            return;
        }

        var diagnostic = EidoscDiagnostic.Error(
            DiagnosticMessages.MirPoisonOperand,
            PoisonOperandCode);
        if (HasSpan(poison.Span))
        {
            diagnostic.WithLabel(poison.Span, DiagnosticMessages.MirPoisonOperandLabel);
        }

        diagnostic.WithNote(DiagnosticMessages.FunctionNote(function.Name));
        diagnostic.WithNote(DiagnosticMessages.MirLocationNote(
            block.Id.Value,
            instructionIndex?.ToString() ?? "terminator"));
        diagnostic.WithNote(DiagnosticMessages.RoleNote(role));
        if (!string.IsNullOrWhiteSpace(poison.Reason))
        {
            diagnostic.WithNote(DiagnosticMessages.ReasonNote(poison.Reason));
        }

        _diagnostics.Add(diagnostic);
    }

    private static IEnumerable<MirOperand?> EnumerateInstructionOperands(MirInstruction instruction)
    {
        switch (instruction)
        {
            case MirAssign assign:
                yield return assign.Target;
                yield return assign.Source;
                break;
            case MirCaseInject injection:
                yield return injection.Target;
                yield return injection.Operand;
                break;
            case MirCall call:
                yield return call.Target;
                yield return call.Function;
                foreach (var argument in call.Arguments)
                {
                    yield return argument;
                }

                break;
            case MirBinOp binOp:
                yield return binOp.Target;
                yield return binOp.Left;
                yield return binOp.Right;
                break;
            case MirUnaryOp unaryOp:
                yield return unaryOp.Target;
                yield return unaryOp.Operand;
                break;
            case MirLoad load:
                yield return load.Target;
                yield return load.Source;
                break;
            case MirStore store:
                yield return store.Target;
                yield return store.Value;
                break;
            case MirDrop drop:
                yield return drop.Value;
                break;
            case MirCopy copy:
                yield return copy.Target;
                yield return copy.Source;
                break;
            case MirMove move:
                yield return move.Target;
                yield return move.Source;
                break;
            case MirAlloc alloc:
                yield return alloc.Target;
                break;
        }
    }

    private static IEnumerable<MirOperand?> EnumerateTerminatorOperands(MirTerminator terminator)
    {
        switch (terminator)
        {
            case MirReturn ret:
                yield return ret.Value;
                break;
            case MirSwitch sw:
                yield return sw.Discriminant;
                foreach (var branch in sw.Branches)
                {
                    yield return branch.Value;
                }

                break;
        }
    }

    private static IEnumerable<MirOperand> EnumerateOperandTree(MirOperand operand)
    {
        yield return operand;

        if (operand is not MirPlace place)
        {
            yield break;
        }

        if (place.Base != null)
        {
            foreach (var nested in EnumerateOperandTree(place.Base))
            {
                yield return nested;
            }
        }

        if (place.Index != null)
        {
            foreach (var nested in EnumerateOperandTree(place.Index))
            {
                yield return nested;
            }
        }
    }

    private static bool HasSpan(SourceSpan span)
    {
        return span.Length > 0 ||
               span.Location.Position > 0 ||
               span.Location.Line > 0 ||
               span.Location.Column > 0;
    }
}
