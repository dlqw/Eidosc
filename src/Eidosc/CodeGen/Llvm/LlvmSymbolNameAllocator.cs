using Eidosc.Symbols;
using Eidosc.Semantic;

namespace Eidosc.CodeGen.Llvm;

internal sealed class LlvmSymbolNameAllocator(NameMangler nameMangler)
{
    public string AllocateFunctionName(
        LlvmFunctionNameAllocationRequest request,
        IReadOnlyDictionary<string, LlvmFunctionType> allocatedFunctionTypesByName)
    {
        if (request.HasStructuredIdentity)
        {
            var structuredSeed = !string.IsNullOrWhiteSpace(request.FunctionIdKey)
                ? $"{request.SignatureKey}_{request.FunctionIdKey}"
                : request.SignatureKey;
            return AllocateFunctionInstanceName(request, allocatedFunctionTypesByName, structuredSeed);
        }

        if (!request.HasStructuredIdentity &&
            !string.IsNullOrWhiteSpace(request.ExistingSourceSignatureName))
        {
            return request.ExistingSourceSignatureName;
        }

        var baseName = nameMangler.MangleFunctionName(request.ModuleName, request.SourceName);
        if (!allocatedFunctionTypesByName.TryGetValue(baseName, out var existingBaseType))
        {
            return baseName;
        }

        if (existingBaseType == request.FunctionType && !request.HasStructuredIdentity)
        {
            return baseName;
        }

        var instanceSeed = !string.IsNullOrWhiteSpace(request.FunctionIdKey)
            ? $"{request.SignatureKey}_{request.FunctionIdKey}"
            : request.SignatureKey;
        return AllocateFunctionInstanceName(request, allocatedFunctionTypesByName, instanceSeed);
    }

    public string AllocateSymbolFunctionName(
        LlvmFunctionNameAllocationRequest request,
        IReadOnlyDictionary<string, LlvmFunctionType> allocatedFunctionTypesByName,
        SymbolId symbolId)
    {
        var instanceSeed = $"{request.SignatureKey}_s{symbolId.Value}";
        return AllocateFunctionInstanceName(request, allocatedFunctionTypesByName, instanceSeed);
    }

    private string AllocateFunctionInstanceName(
        LlvmFunctionNameAllocationRequest request,
        IReadOnlyDictionary<string, LlvmFunctionType> allocatedFunctionTypesByName,
        string instanceSeed)
    {
        var instanceName = nameMangler.MangleFunctionInstanceName(
            request.ModuleName,
            request.SourceName,
            instanceSeed);
        var suffix = 1;
        while (allocatedFunctionTypesByName.TryGetValue(instanceName, out var existingType) &&
               (request.HasStructuredIdentity || existingType != request.FunctionType))
        {
            instanceName = nameMangler.MangleFunctionInstanceName(
                request.ModuleName,
                request.SourceName,
                $"{instanceSeed}_{suffix++}");
        }

        return instanceName;
    }
}

internal sealed record LlvmFunctionNameAllocationRequest
{
    public string ModuleName { get; init; } = "";

    public string SourceName { get; init; } = "";

    public string SignatureKey { get; init; } = "";

    public string FunctionIdKey { get; init; } = "";

    public bool HasStructuredIdentity { get; init; }

    public LlvmFunctionType FunctionType { get; init; } = null!;

    public LlvmLinkage Linkage { get; init; } = LlvmLinkage.External;

    public string? ExistingSourceSignatureName { get; init; }
}
