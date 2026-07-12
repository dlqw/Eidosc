namespace Eidosc.Pipeline;

public sealed record ProjectModuleMirArtifactSnapshot(
    string SchemaVersion,
    IReadOnlyList<ProjectModuleMirArtifactNode> Nodes)
{
    public const string CurrentSchemaVersion = "module-mir-artifact-snapshot-v1";

    public static ProjectModuleMirArtifactSnapshot Create(
        ProjectModuleTypedSemanticSnapshot typedSemanticSnapshot,
        Mir.MirFunctionFingerprintSnapshot functionFingerprints)
    {
        var moduleFingerprint = functionFingerprints.ModuleFingerprint;
        return new ProjectModuleMirArtifactSnapshot(
            CurrentSchemaVersion,
            typedSemanticSnapshot.Nodes
                .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
                .Select(node => new ProjectModuleMirArtifactNode(
                    node.ModuleKey,
                    node.Dependencies,
                    node.TypedSemanticHash,
                    moduleFingerprint,
                    ModuleArtifactHash.ComputeJsonHash(new
                    {
                        node.ModuleKey,
                        node.TypedSemanticHash,
                        MirFunctionModuleFingerprint = moduleFingerprint
                    })))
                .ToArray());
    }
}

public sealed record ProjectModuleMirArtifactNode(
    string ModuleKey,
    IReadOnlyList<string> Dependencies,
    string TypedSemanticHash,
    string MirFunctionModuleFingerprint,
    string MirArtifactHash);
