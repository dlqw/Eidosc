using Eidosc.Types;

namespace Eidosc.CodeGen.Llvm;

public sealed partial class MirToLlvmConverter
{
    private sealed record ArrayElementPolicy(LlvmValue Retain, LlvmValue Release);

    private ArrayElementPolicy GetArrayElementPolicy(TypeId elementTypeId)
    {
        if (!elementTypeId.IsValid ||
            _typeLowering.IsOpenDynamicType(elementTypeId) ||
            !PayloadContainsManagedRc(elementTypeId))
        {
            return new ArrayElementPolicy(LlvmNullPointer.Instance, LlvmNullPointer.Instance);
        }

        if (_arrayElementPolicies.TryGetValue(elementTypeId, out var policy))
        {
            return policy;
        }

        var storageType = LowerStorageTypeIdOrReport(elementTypeId, "array element policy");
        var suffix = elementTypeId.Value.ToString("X8");
        var retainName = $"eidos_array_retain_elem__{suffix}";
        var releaseName = $"eidos_array_release_elem__{suffix}";
        var retain = GenerateArrayElementRetainThunk(retainName, elementTypeId, storageType);
        var release = GenerateArrayElementReleaseThunk(releaseName, elementTypeId, storageType);
        _currentModule?.Functions.Add(retain);
        _currentModule?.Functions.Add(release);

        policy = new ArrayElementPolicy(
            new LlvmGlobal { Name = retainName, Type = LlvmPointerType.VoidPtr() },
            new LlvmGlobal { Name = releaseName, Type = LlvmPointerType.VoidPtr() });
        _arrayElementPolicies[elementTypeId] = policy;
        return policy;
    }

    private LlvmFunction GenerateArrayElementRetainThunk(string name, TypeId elementTypeId, LlvmType storageType)
    {
        var function = new LlvmFunction
        {
            Name = name,
            ReturnType = LlvmVoidType.Instance,
            Linkage = LlvmLinkage.Internal
        };
        function.Parameters.Add(new LlvmParameter
        {
            Name = "element",
            Type = LlvmPointerType.VoidPtr()
        });

        var previousBlock = _currentBlock;
        var block = new LlvmBasicBlock { Label = WellKnownStrings.InternalNames.Entry };
        _currentBlock = block;
        var pointer = new LlvmLocal { Name = "element", Type = LlvmPointerType.VoidPtr() };
        var load = new LlvmLoad
        {
            Pointer = pointer,
            LoadType = storageType,
            ResultName = "value"
        };
        block.Instructions.Add(load);
        EmitRetainManagedPayloadValue(
            elementTypeId,
            new LlvmInstructionRef { Instruction = load, Type = storageType },
            storageType);
        block.Terminator = new LlvmRet();
        function.BasicBlocks.Add(block);
        _currentBlock = previousBlock;
        return function;
    }

    private LlvmFunction GenerateArrayElementReleaseThunk(string name, TypeId elementTypeId, LlvmType storageType)
    {
        var function = new LlvmFunction
        {
            Name = name,
            ReturnType = LlvmVoidType.Instance,
            Linkage = LlvmLinkage.Internal
        };
        function.Parameters.Add(new LlvmParameter
        {
            Name = "element",
            Type = LlvmPointerType.VoidPtr()
        });

        var block = new LlvmBasicBlock { Label = WellKnownStrings.InternalNames.Entry };
        EmitReleaseManagedPayloadFromPointer(
            block,
            new LlvmLocal { Name = "element", Type = LlvmPointerType.VoidPtr() },
            elementTypeId,
            storageType,
            "array_elem");
        block.Terminator = new LlvmRet();
        function.BasicBlocks.Add(block);
        return function;
    }
}
