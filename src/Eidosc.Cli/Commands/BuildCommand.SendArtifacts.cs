using System.Text.Json;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    internal static void StoreLatestSendAnalysisSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.SendAnalysisSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestSendAnalysisKey(artifact),
            LatestSendAnalysisSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.SendAnalysisSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static SendAnalysisSnapshot? TryLoadLatestSendAnalysisSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestSendAnalysisKey(artifact),
                    LatestSendAnalysisSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<SendAnalysisSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Functions == null ||
                   !string.Equals(snapshot.SchemaVersion, SendAnalysisSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    private static ModuleArtifactKey CreateLatestSendAnalysisKey(FullBuildArtifact artifact)
    {
        return artifact.LatestMirFunctionFingerprintKey with
        {
            CacheSchema = "send-analysis-latest-v1"
        };
    }
}
