using System.Text.Json;
using Eidosc.Semantic;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    internal static void StoreLatestModuleMemberIndexSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleMemberIndexSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestModuleMemberIndexKey(artifact),
            LatestModuleMemberIndexSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleMemberIndexSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static ProjectModuleMemberIndexSnapshot? TryLoadLatestModuleMemberIndexSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestModuleMemberIndexKey(artifact),
                    LatestModuleMemberIndexSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<ProjectModuleMemberIndexSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Nodes == null ||
                   !string.Equals(snapshot.SchemaVersion, ProjectModuleMemberIndexSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static void StoreLatestModuleMemberIndexRestorePlanSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.ModuleMemberIndexRestorePlan == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestModuleMemberIndexRestorePlanKey(artifact),
            LatestModuleMemberIndexRestorePlanSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.ModuleMemberIndexRestorePlan, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static ProjectModuleMemberIndexRestorePlan? TryLoadLatestModuleMemberIndexRestorePlanSnapshot(
        FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestModuleMemberIndexRestorePlanKey(artifact),
                    LatestModuleMemberIndexRestorePlanSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<ProjectModuleMemberIndexRestorePlan>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Modules == null ||
                   !string.Equals(snapshot.SchemaVersion, ProjectModuleMemberIndexRestorePlan.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static void StoreLatestImplOverlapCheckSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null ||
            result.ImplOverlapCheckSnapshot is not { Entries.Count: > 0 } snapshot)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestImplOverlapCheckKey(artifact),
            LatestImplOverlapCheckSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static ImplOverlapCheckSnapshot? TryLoadLatestImplOverlapCheckSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestImplOverlapCheckKey(artifact),
                    LatestImplOverlapCheckSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<ImplOverlapCheckSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Entries == null ||
                   !string.Equals(snapshot.SchemaVersion, ImplOverlapCheckSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    private static ModuleArtifactKey CreateLatestModuleMemberIndexKey(FullBuildArtifact artifact)
    {
        return artifact.LatestSemanticSignatureKey with
        {
            CacheSchema = "module-member-index-latest-v1"
        };
    }

    private static ModuleArtifactKey CreateLatestModuleMemberIndexRestorePlanKey(FullBuildArtifact artifact)
    {
        return artifact.LatestSemanticSignatureKey with
        {
            CacheSchema = "module-member-index-restore-plan-latest-v1"
        };
    }

    private static ModuleArtifactKey CreateLatestImplOverlapCheckKey(FullBuildArtifact artifact)
    {
        return artifact.LatestSemanticSignatureKey with
        {
            CacheSchema = "impl-overlap-check-latest-v2"
        };
    }
}
