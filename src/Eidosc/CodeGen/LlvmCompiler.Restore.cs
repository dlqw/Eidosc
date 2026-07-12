using System.Diagnostics;
using Eidosc.CodeGen.Llvm;

namespace Eidosc.CodeGen;

public sealed partial class LlvmCompiler
{
    public CodeGenResult CompileRestoredFragmentsToExecutable(
        LlvmModuleEnvelopeSnapshot envelope,
        LlvmFunctionFragmentSnapshot fragments,
        string outputPath)
    {
        _profile?.Record(
            "llvm",
            "native_full_module_restore_from_previous_fragments",
            "eidosc",
            TimeSpan.Zero,
            success: true,
            cacheHit: true,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["functions"] = fragments.Functions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["envelopeFingerprint"] = envelope.EnvelopeFingerprint,
                ["fragmentFingerprint"] = fragments.ModuleFingerprint
            });

        var recompositionSw = Stopwatch.StartNew();
        var recomposed = LlvmFunctionFingerprintBuilder.RecomposeModule(envelope, fragments);
        recompositionSw.Stop();
        _profile?.Record(
            "llvm",
            "recompose_restored_full_module_ir",
            "eidosc",
            recompositionSw.Elapsed,
            success: true,
            cacheHit: true,
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["functions"] = recomposed.FunctionCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["irBytes"] = System.Text.Encoding.UTF8.GetByteCount(recomposed.IrText).ToString(System.Globalization.CultureInfo.InvariantCulture)
            });

        var tempObjPath = CreateTemporaryPath("", _targetInfo.ObjectExtension);
        var tempEntrySourcePath = CreateTemporaryPath("entry_", ".c");
        var tempEntryObjPath = CreateTemporaryPath("entry_", _targetInfo.ObjectExtension);
        var tempNativeObjects = new List<string>();
        var tempRuntimeObjects = new List<string>();

        try
        {
            var objResult = CompileToObject(recomposed.IrText, tempObjPath);
            if (!objResult.Success)
            {
                return objResult;
            }

            var objectFiles = new List<string> { tempObjPath };
            var entryResult = TryCompileEntryShim(
                GetFunctionNames(fragments),
                tempEntrySourcePath,
                tempEntryObjPath);
            if (!entryResult.Success)
            {
                return entryResult;
            }

            if (File.Exists(tempEntryObjPath))
            {
                objectFiles.Add(tempEntryObjPath);
            }

            var nativeResults = CompileNativeSources(envelope.NativeSources, envelope.NativeIncludePaths);
            tempNativeObjects.AddRange(nativeResults.Select(static result => result.ObjectPath));
            foreach (var nativeResult in nativeResults)
            {
                if (!nativeResult.Result.Success)
                {
                    return nativeResult.Result;
                }

                objectFiles.Add(nativeResult.ObjectPath);
            }

            var runtimeResolveResult = TryResolveRuntimeLinkInputs(out var runtimeLinkInputs, tempRuntimeObjects);
            if (!runtimeResolveResult.Success)
            {
                return runtimeResolveResult;
            }

            objectFiles.AddRange(runtimeLinkInputs);
            var linkLibraries = envelope.LinkLibraries.Count > 0 ? envelope.LinkLibraries.ToArray() : null;
            var linkLibraryPaths = envelope.LinkLibraryPaths.Count > 0 ? envelope.LinkLibraryPaths.ToArray() : null;
            var linkerFlags = envelope.LinkerFlags.Count > 0 ? envelope.LinkerFlags.ToArray() : null;
            return LinkExecutable(objectFiles.ToArray(), outputPath, linkLibraries, linkLibraryPaths, linkerFlags);
        }
        finally
        {
            DeleteIfExists(tempObjPath);
            DeleteIfExists(tempEntryObjPath);
            DeleteIfExists(tempEntrySourcePath);

            foreach (var tempNativeObject in tempNativeObjects)
            {
                DeleteIfExists(tempNativeObject);
            }

            foreach (var tempRuntimeObject in tempRuntimeObjects)
            {
                DeleteIfExists(tempRuntimeObject);
            }
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
