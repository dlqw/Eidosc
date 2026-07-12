using System.Text.Json;
using Eidosc.CodeGen.Llvm;
using Eidosc.Mir;
using Eidosc.Pipeline;

namespace Eidosc.Cli.Commands;

public static partial class BuildCommand
{
    internal static void StoreLatestFunctionFingerprintSnapshotArtifacts(
        FullBuildArtifact? artifact,
        CompilationResult result)
    {
        if (artifact == null)
        {
            return;
        }

        if (result.MirFunctionFingerprints != null)
        {
            artifact.Cache.StoreArtifact(
                CreateLatestMirFunctionFingerprintKey(artifact),
                LatestMirFunctionFingerprintSnapshotArtifactKind,
                ".json",
                JsonSerializer.Serialize(result.MirFunctionFingerprints, new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
        }

        if (result.LlvmFunctionFingerprints != null)
        {
            artifact.Cache.StoreArtifact(
                CreateLatestLlvmFunctionFingerprintKey(artifact),
                LatestLlvmFunctionFingerprintSnapshotArtifactKind,
                ".json",
                JsonSerializer.Serialize(result.LlvmFunctionFingerprints, new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
        }

        if (result.LlvmFunctionFragments != null)
        {
            artifact.Cache.StoreArtifact(
                CreateLatestLlvmFunctionFragmentKey(artifact),
                LatestLlvmFunctionFragmentSnapshotArtifactKind,
                ".json",
                JsonSerializer.Serialize(result.LlvmFunctionFragments, new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
        }

        if (result.LlvmModuleEnvelope != null)
        {
            artifact.Cache.StoreArtifact(
                CreateLatestLlvmModuleEnvelopeKey(artifact),
                LlvmModuleEnvelopeSnapshotArtifactKind,
                ".json",
                JsonSerializer.Serialize(result.LlvmModuleEnvelope, new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
        }

        if (result.LlvmCodegenUnitPlan != null)
        {
            artifact.Cache.StoreArtifact(
                CreateLatestLlvmCodegenUnitPlanKey(artifact),
                LlvmCodegenUnitPlanSnapshotArtifactKind,
                ".json",
                JsonSerializer.Serialize(result.LlvmCodegenUnitPlan, new JsonSerializerOptions
                {
                    WriteIndented = false
                }));
        }
    }

    internal static MirFunctionFingerprintSnapshot? TryLoadLatestMirFunctionFingerprintSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestMirFunctionFingerprintKey(artifact),
                    LatestMirFunctionFingerprintSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<MirFunctionFingerprintSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Functions == null ||
                   !string.Equals(snapshot.SchemaVersion, MirFunctionFingerprintSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static LlvmFunctionFingerprintSnapshot? TryLoadLatestLlvmFunctionFingerprintSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestLlvmFunctionFingerprintKey(artifact),
                    LatestLlvmFunctionFingerprintSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<LlvmFunctionFingerprintSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Functions == null ||
                   !string.Equals(snapshot.SchemaVersion, LlvmFunctionFingerprintSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static LlvmFunctionFragmentSnapshot? TryLoadLatestLlvmFunctionFragmentSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestLlvmFunctionFragmentKey(artifact),
                    LatestLlvmFunctionFragmentSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<LlvmFunctionFragmentSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.Functions == null ||
                   !string.Equals(snapshot.SchemaVersion, LlvmFunctionFragmentSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static LlvmModuleEnvelopeSnapshot? TryLoadLatestLlvmModuleEnvelopeSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestLlvmModuleEnvelopeKey(artifact),
                    LlvmModuleEnvelopeSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<LlvmModuleEnvelopeSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.HeaderIr == null ||
                   !string.Equals(snapshot.SchemaVersion, LlvmModuleEnvelopeSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }

    internal static LlvmCodegenUnitPlanSnapshot? TryLoadLatestLlvmCodegenUnitPlanSnapshot(FullBuildArtifact? artifact)
    {
        if (artifact == null)
        {
            return null;
        }

        try
        {
            if (!artifact.Cache.TryGetArtifact(
                    CreateLatestLlvmCodegenUnitPlanKey(artifact),
                    LlvmCodegenUnitPlanSnapshotArtifactKind,
                    out var manifest) ||
                manifest == null ||
                !File.Exists(manifest.PayloadPath))
            {
                return null;
            }

            var json = File.ReadAllText(manifest.PayloadPath);
            var snapshot = JsonSerializer.Deserialize<LlvmCodegenUnitPlanSnapshot>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            return snapshot?.ObjectGroups == null ||
                   !string.Equals(snapshot.SchemaVersion, LlvmCodegenUnitPlanSnapshot.CurrentSchemaVersion, StringComparison.Ordinal)
                ? null
                : snapshot;
        }
        catch
        {
            return null;
        }
    }
}
