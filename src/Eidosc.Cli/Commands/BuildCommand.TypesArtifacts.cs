using System.Text.Json;
using Eidosc.Pipeline;
using Eidosc.Types;

namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    internal static void StoreLatestTypeDirectedCallableResolutionSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null ||
            result.TypeDirectedCallableResolutionSnapshot is not { Entries.Count: > 0 } snapshot)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestTypeDirectedCallableResolutionKey(artifact),
            LatestTypeDirectedCallableResolutionSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static TypeDirectedCallableResolutionSnapshot? TryLoadLatestTypeDirectedCallableResolutionSnapshot(
        FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestTypeDirectedCallableResolutionKey(artifact),
                    LatestTypeDirectedCallableResolutionSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<TypeDirectedCallableResolutionSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Entries == null ||
                   !string.Equals(snapshot.SchemaVersion, TypeDirectedCallableResolutionSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static void StoreLatestTraitCheckSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null ||
            result.TraitCheckSnapshot is not { Entries.Count: > 0 } snapshot)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestTraitCheckKey(artifact),
            LatestTraitCheckSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static TraitCheckSnapshot? TryLoadLatestTraitCheckSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestTraitCheckKey(artifact),
                    LatestTraitCheckSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<TraitCheckSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Entries == null ||
                   !string.Equals(snapshot.SchemaVersion, TraitCheckSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static void StoreLatestAssociatedTypeProjectionSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null ||
            result.AssociatedTypeProjectionSnapshot is not { Entries.Count: > 0 } snapshot)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestAssociatedTypeProjectionKey(artifact),
            LatestAssociatedTypeProjectionSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static AssociatedTypeProjectionSnapshot? TryLoadLatestAssociatedTypeProjectionSnapshot(
        FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestAssociatedTypeProjectionKey(artifact),
                    LatestAssociatedTypeProjectionSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<AssociatedTypeProjectionSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Entries == null ||
                   !string.Equals(snapshot.SchemaVersion, AssociatedTypeProjectionSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static void StoreLatestAssociatedConstProjectionSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null ||
            result.AssociatedConstProjectionSnapshot is not { Entries.Count: > 0 } snapshot)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestAssociatedConstProjectionKey(artifact),
            LatestAssociatedConstProjectionSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static AssociatedConstProjectionSnapshot? TryLoadLatestAssociatedConstProjectionSnapshot(
        FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestAssociatedConstProjectionKey(artifact),
                    LatestAssociatedConstProjectionSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<AssociatedConstProjectionSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Entries == null ||
                   !string.Equals(snapshot.SchemaVersion, AssociatedConstProjectionSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    private static ModuleArtifactKey CreateLatestTypeDirectedCallableResolutionKey(FullBuildArtifact artifact)
    {
        return artifact.LatestTypedSemanticSignatureKey with
        {
            CacheSchema = "type-directed-callable-resolution-latest-v3"
        };
    }

    private static ModuleArtifactKey CreateLatestAssociatedTypeProjectionKey(FullBuildArtifact artifact)
    {
        return artifact.LatestTypedSemanticSignatureKey with
        {
            CacheSchema = "associated-type-projection-latest-v4"
        };
    }

    private static ModuleArtifactKey CreateLatestAssociatedConstProjectionKey(FullBuildArtifact artifact)
    {
        return artifact.LatestTypedSemanticSignatureKey with
        {
            CacheSchema = "associated-const-projection-latest-v1"
        };
    }

    private static ModuleArtifactKey CreateLatestTraitCheckKey(FullBuildArtifact artifact)
    {
        return artifact.LatestTypedSemanticSignatureKey with
        {
            CacheSchema = "trait-check-latest-v2"
        };
    }
}
