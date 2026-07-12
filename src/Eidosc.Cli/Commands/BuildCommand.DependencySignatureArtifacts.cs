using System.Text.Json;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    internal static void StoreLatestModuleDependencySignatureSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleDependencySignatureSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestModuleDependencySignatureKey(artifact),
            LatestModuleDependencySignatureSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleDependencySignatureSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static ProjectModuleDependencySignatureSnapshot? TryLoadLatestModuleDependencySignatureSnapshot(
        FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestModuleDependencySignatureKey(artifact),
                    LatestModuleDependencySignatureSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<ProjectModuleDependencySignatureSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Nodes == null ||
                   !string.Equals(
                       snapshot.SchemaVersion,
                       ProjectModuleDependencySignatureSnapshot.CurrentSchemaVersion,
                       StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    private static ModuleArtifactKey CreateLatestModuleDependencySignatureKey(FullBuildArtifact artifact)
    {
        return artifact.LatestTypedSemanticSignatureKey with
        {
            CacheSchema = "module-dependency-signature-latest-v2"
        };
    }
}
