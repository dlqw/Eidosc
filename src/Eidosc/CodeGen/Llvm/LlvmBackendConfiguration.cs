using Eidosc.Pipeline;

namespace Eidosc.CodeGen.Llvm;

public sealed record LlvmBackendConfiguration(
    string TargetTriple,
    string DataLayout,
    string TargetCpu,
    string TargetFeatures,
    int OptimizationLevel,
    bool EnableLto,
    NativeLinkMode LinkMode,
    IReadOnlyList<string> ClangObjectFlags,
    IReadOnlyList<string> LlcObjectFlags,
    string ExtraCFlags,
    string ExtraLinkFlags)
{
    public static LlvmBackendConfiguration Create(
        TargetInfo targetInfo,
        int optimizationLevel,
        bool enableLto,
        NativeLinkMode linkMode,
        string? extraCFlags,
        string? extraLinkFlags)
    {
        var relocationFlags = LlvmCompiler.GetDefaultObjectRelocationFlags(targetInfo, linkMode);
        return new LlvmBackendConfiguration(
            targetInfo.Triple,
            targetInfo.DataLayout,
            targetInfo.Cpu,
            targetInfo.Features,
            optimizationLevel,
            enableLto,
            linkMode,
            relocationFlags.ClangFlags.ToArray(),
            relocationFlags.LlcFlags.ToArray(),
            extraCFlags ?? Environment.GetEnvironmentVariable(WellKnownStrings.EnvVars.ExtraCFlags) ?? "",
            extraLinkFlags ?? Environment.GetEnvironmentVariable(WellKnownStrings.EnvVars.ExtraLdFlags) ?? "");
    }

    public string StableHash => ModuleArtifactHash.ComputeJsonHash(new
    {
        TargetTriple,
        DataLayout,
        TargetCpu,
        TargetFeatures,
        OptimizationLevel,
        EnableLto,
        LinkMode,
        ClangObjectFlags,
        LlcObjectFlags,
        ExtraCFlags,
        ExtraLinkFlags
    });
}
