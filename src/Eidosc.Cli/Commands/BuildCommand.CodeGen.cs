using Eidosc.CodeGen;
using Eidosc.CodeGen.Llvm;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    private static CodeGenResult CompileNativeFullModule(
        LlvmCompiler llvmCompiler,
        CompilationResult result,
        string outputPath)
    {
        if (result.LlvmModule != null)
        {
            return llvmCompiler.CompileToExecutable(result.LlvmModule, outputPath);
        }

        if (result.LlvmFunctionFragments != null &&
            result.LlvmModuleEnvelope != null)
        {
            return llvmCompiler.CompileRestoredFragmentsToExecutable(
                result.LlvmModuleEnvelope,
                result.LlvmFunctionFragments,
                outputPath);
        }

        return new CodeGenResult
        {
            Success = false,
            ErrorMessage = "Native full-module restore did not provide a full LLVM module or restorable previous fragments."
        };
    }

    private static CodeGenResult CompileNativeWithObjectGroups(
        LlvmCompiler llvmCompiler,
        CompilationResult result,
        CompilationOptions compileOptions,
        string outputPath,
        int maxObjectGroups)
    {
        if (TryCompileNativeFromPreviousObjectGroupArtifacts(
                llvmCompiler,
                result,
                compileOptions,
                outputPath,
                maxObjectGroups,
                out var restoredResult))
        {
            return restoredResult;
        }

        if (result.LlvmFunctionFragmentRestoreResult?.Applied == true &&
            result.LlvmFunctionFragments != null &&
            result.LlvmModuleEnvelope != null &&
            result.LlvmCodegenUnitPlan != null &&
            result.LlvmObjectGroupRestorePlan != null)
        {
            if (result.LlvmModule == null)
            {
                return llvmCompiler.CompileRestoredFragmentsToExecutableWithObjectGroups(
                    result.LlvmModuleEnvelope,
                    result.LlvmFunctionFragments,
                    result.LlvmCodegenUnitPlan,
                    outputPath,
                    maxObjectGroups,
                    result.LlvmObjectGroupRestorePlan);
            }

            return llvmCompiler.CompileToExecutableWithObjectGroups(
                result.LlvmModule!,
                outputPath,
                maxObjectGroups,
                restoredFragments: result.LlvmFunctionFragments,
                restorePlan: result.LlvmObjectGroupRestorePlan);
        }

        if (result.LlvmFunctionFragments != null &&
            result.LlvmModuleEnvelope != null &&
            result.LlvmCodegenUnitPlan != null)
        {
            return llvmCompiler.CompileRestoredFragmentsToExecutableWithObjectGroups(
                result.LlvmModuleEnvelope,
                result.LlvmFunctionFragments,
                result.LlvmCodegenUnitPlan,
                outputPath,
                maxObjectGroups);
        }

        if (result.LlvmModule == null)
        {
            return new CodeGenResult
            {
                Success = false,
                ErrorMessage = "Native object-groups restore did not provide a full LLVM module or restorable previous artifacts."
            };
        }

        return llvmCompiler.CompileToExecutableWithObjectGroups(
            result.LlvmModule,
            outputPath,
            maxObjectGroups);
    }

    private static bool TryCompileNativeFromPreviousObjectGroupArtifacts(
        LlvmCompiler llvmCompiler,
        CompilationResult result,
        CompilationOptions compileOptions,
        string outputPath,
        int maxObjectGroups,
        out CodeGenResult codeGenResult)
    {
        codeGenResult = new CodeGenResult { Success = false };
        var previousMir = result.MirFunctionFingerprints;
        if (previousMir == null ||
            compileOptions.PreviousMirFunctionFingerprintSnapshot == null ||
            compileOptions.PreviousLlvmFunctionFragmentSnapshot == null ||
            compileOptions.PreviousLlvmModuleEnvelopeSnapshot == null ||
            compileOptions.PreviousLlvmCodegenUnitPlanSnapshot == null ||
            !string.Equals(
                previousMir.ModuleFingerprint,
                compileOptions.PreviousMirFunctionFingerprintSnapshot.ModuleFingerprint,
                StringComparison.Ordinal))
        {
            return false;
        }

        var restorePlan = LlvmObjectGroupRestorePlanSnapshot.Create(
            compileOptions.PreviousLlvmCodegenUnitPlanSnapshot.ObjectGroups,
            LlvmFunctionFragmentRestorePlanSnapshot.Create(
                compileOptions.PreviousLlvmFunctionFragmentSnapshot,
                compileOptions.PreviousLlvmFunctionFragmentSnapshot));
        codeGenResult = llvmCompiler.CompileRestoredFragmentsToExecutableWithObjectGroups(
            compileOptions.PreviousLlvmModuleEnvelopeSnapshot,
            compileOptions.PreviousLlvmFunctionFragmentSnapshot,
            compileOptions.PreviousLlvmCodegenUnitPlanSnapshot,
            outputPath,
            maxObjectGroups,
            restorePlan);
        return true;
    }
}
