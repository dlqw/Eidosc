using System.Text.Json;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    internal static void StoreLatestBorrowDiagnosticSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.BorrowDiagnosticSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestBorrowDiagnosticKey(artifact),
            LatestBorrowDiagnosticSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.BorrowDiagnosticSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static void StoreLatestBorrowCodegenHintsSnapshotArtifact(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null || result.BorrowCodegenHintsSnapshot == null)
        {
            return;
        }

        artifact.Cache.StoreArtifact(
            CreateLatestBorrowCodegenHintsKey(artifact),
            LatestBorrowCodegenHintsSnapshotArtifactKind,
            ".json",
            JsonSerializer.Serialize(result.BorrowCodegenHintsSnapshot, new JsonSerializerOptions
            {
                WriteIndented = false
            }));
    }

    internal static BorrowDiagnosticSnapshot? TryLoadLatestBorrowDiagnosticSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestBorrowDiagnosticKey(artifact),
                    LatestBorrowDiagnosticSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<BorrowDiagnosticSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Functions == null ||
                   !string.Equals(snapshot.SchemaVersion, BorrowDiagnosticSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static BorrowCodegenHintsSnapshot? TryLoadLatestBorrowCodegenHintsSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestBorrowCodegenHintsKey(artifact),
                    LatestBorrowCodegenHintsSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<BorrowCodegenHintsSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Functions == null ||
                   !string.Equals(snapshot.SchemaVersion, BorrowCodegenHintsSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    private static ModuleArtifactKey CreateLatestBorrowDiagnosticKey(FullBuildArtifact artifact)
    {
        return artifact.LatestMirFunctionFingerprintKey with
        {
            CacheSchema = "borrow-diagnostic-latest-v2"
        };
    }

    private static ModuleArtifactKey CreateLatestBorrowCodegenHintsKey(FullBuildArtifact artifact)
    {
        return artifact.LatestMirFunctionFingerprintKey with
        {
            CacheSchema = "borrow-codegen-hints-latest-v2"
        };
    }
}
