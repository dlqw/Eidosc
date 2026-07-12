using System.Text.Json;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    internal static void StorePerModuleNamerStatePayloadArtifacts(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null ||
            result.ModuleNamerStatePayloads is not { Count: > 0 } payloads ||
            result.ModuleSemanticSignatureSnapshot == null)
        {
            return;
        }

        var semanticByModule = result.ModuleSemanticSignatureSnapshot.Nodes
            .ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        foreach (var payload in payloads)
        {
            if (!semanticByModule.TryGetValue(payload.ModuleKey, out var semantic))
            {
                continue;
            }

            StoreModuleStatePayload(
                artifact,
                payload,
                payload.ModuleKey,
                semantic.ExportSurfaceHash,
                semantic.DependencySemanticSignatureHash,
                ModuleNamerStatePayloadArtifactKind);
        }
    }

    internal static void StorePerModuleTypesStatePayloadArtifacts(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null ||
            result.ModuleTypesStatePayloads is not { Count: > 0 } payloads)
        {
            return;
        }

        foreach (var payload in payloads)
        {
            StoreModuleStatePayload(
                artifact,
                payload,
                payload.ModuleKey,
                payload.TypedSemantic.LocalSurfaceHash,
                payload.TypedSemantic.DependencyTypedSemanticHash,
                ModuleTypesStatePayloadArtifactKind);
        }
    }

    internal static void StorePerModuleHirStatePayloadArtifacts(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null ||
            result.ModuleHirStatePayloads is not { Count: > 0 } payloads)
        {
            return;
        }

        foreach (var payload in payloads)
        {
            StoreModuleStatePayload(
                artifact,
                payload,
                payload.ModuleKey,
                payload.TypedSemantic.LocalSurfaceHash,
                payload.TypedSemantic.DependencyTypedSemanticHash,
                ModuleHirStatePayloadArtifactKind);
        }
    }

    internal static void StorePerModuleMirStatePayloadArtifacts(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null ||
            result.ModuleMirStatePayloads is not { Count: > 0 } payloads)
        {
            return;
        }

        foreach (var payload in payloads)
        {
            StoreModuleStatePayload(
                artifact,
                payload,
                payload.ModuleKey,
                payload.TypedSemantic.LocalSurfaceHash,
                payload.TypedSemantic.DependencyTypedSemanticHash,
                ModuleMirStatePayloadArtifactKind);
        }
    }

    private static void StoreModuleStatePayload<TPayload>(
        FullBuildArtifact artifact,
        TPayload payload,
        string moduleKey,
        string sourceHash,
        string dependencySignatureHash,
        string artifactKind)
    {
        artifact.Cache.StoreArtifact(
            CreateModuleArtifactKey(
                artifact,
                moduleKey,
                sourceHash,
                dependencySignatureHash),
            artifactKind,
            ".json",
            JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }
}
