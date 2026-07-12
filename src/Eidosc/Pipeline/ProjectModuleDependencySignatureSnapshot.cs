using Eidosc.Semantic;

namespace Eidosc.Pipeline;

public sealed record ProjectModuleDependencySignatureSnapshot(
    string SchemaVersion,
    IReadOnlyList<ProjectModuleDependencySignatureNode> Nodes)
{
    public const string CurrentSchemaVersion = "project-module-dependency-signature-v3";

    public static ProjectModuleDependencySignatureSnapshot Create(
        ProjectModuleGraphSnapshot graph,
        ProjectModuleSemanticSignatureSnapshot? semantic,
        ProjectModuleTypedSemanticSnapshot? typed,
        ProjectModuleMemberIndexSnapshot? memberIndex,
        ProjectModuleMirArtifactSnapshot? mir,
        ProjectModuleSignatureSnapshot? graphSignatures = null,
        ImplOverlapCheckSnapshot? implOverlapChecks = null)
    {
        ArgumentNullException.ThrowIfNull(graph);

        var graphSignaturesByModule = graphSignatures?.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal) ??
                                      new Dictionary<string, ProjectModuleSignatureNode>(StringComparer.Ordinal);
        var semanticByModule = semantic?.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal) ??
                               new Dictionary<string, ProjectModuleSemanticSignatureNode>(StringComparer.Ordinal);
        var typedByModule = typed?.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal) ??
                            new Dictionary<string, ProjectModuleTypedSemanticNode>(StringComparer.Ordinal);
        var memberByModule = memberIndex?.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal) ??
                             new Dictionary<string, ProjectModuleMemberIndexNode>(StringComparer.Ordinal);
        var mirByModule = mir?.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal) ??
                          new Dictionary<string, ProjectModuleMirArtifactNode>(StringComparer.Ordinal);

        var globalCoherenceHash = ComputeGlobalCoherenceHash(implOverlapChecks);
        var nodes = graph.Nodes
            .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
            .Select(node => CreateNode(
                node,
                graphSignaturesByModule,
                semanticByModule,
                typedByModule,
                memberByModule,
                mirByModule,
                globalCoherenceHash))
            .ToArray();
        return new ProjectModuleDependencySignatureSnapshot(CurrentSchemaVersion, nodes);
    }

    public bool IsCompatibleWith(
        ProjectModuleDependencySignatureSnapshot previous,
        string moduleKey,
        ProjectModuleDependencySignatureRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(previous);

        var currentNode = Nodes.FirstOrDefault(node => string.Equals(node.ModuleKey, moduleKey, StringComparison.Ordinal));
        var previousNode = previous.Nodes.FirstOrDefault(node => string.Equals(node.ModuleKey, moduleKey, StringComparison.Ordinal));
        if (currentNode == null || previousNode == null)
        {
            return false;
        }

        return currentNode.IsCompatibleWith(previousNode, requirement);
    }

    private static ProjectModuleDependencySignatureNode CreateNode(
        ProjectModuleGraphNode graphNode,
        IReadOnlyDictionary<string, ProjectModuleSignatureNode> graphSignaturesByModule,
        IReadOnlyDictionary<string, ProjectModuleSemanticSignatureNode> semanticByModule,
        IReadOnlyDictionary<string, ProjectModuleTypedSemanticNode> typedByModule,
        IReadOnlyDictionary<string, ProjectModuleMemberIndexNode> memberByModule,
        IReadOnlyDictionary<string, ProjectModuleMirArtifactNode> mirByModule,
        string globalCoherenceHash)
    {
        var semantic = semanticByModule.TryGetValue(graphNode.ModuleKey, out var semanticNode)
            ? semanticNode
            : null;
        var typed = typedByModule.TryGetValue(graphNode.ModuleKey, out var typedNode)
            ? typedNode
            : null;
        var member = memberByModule.TryGetValue(graphNode.ModuleKey, out var memberNode)
            ? memberNode
            : null;
        var mir = mirByModule.TryGetValue(graphNode.ModuleKey, out var mirNode)
            ? mirNode
            : null;
        var sourceAvailable = graphSignaturesByModule.TryGetValue(graphNode.ModuleKey, out var graphSignature);
        var sourceHash = sourceAvailable
            ? graphSignature!.SourceHash
            : "";
        var inputSignatureHash = sourceAvailable
            ? graphSignature!.SignatureHash
            : "";
        var graphDependencyHash = sourceAvailable
            ? graphSignature!.DependencySignatureHash
            : ModuleArtifactHash.ComputeDependencySignatureHash(graphNode.Dependencies);
        var combinedHash = ModuleArtifactHash.ComputeJsonHash(new
        {
            graphNode.ModuleKey,
            graphNode.Dependencies,
            SourceHash = sourceHash,
            InputSignatureHash = inputSignatureHash,
            GraphDependencySignatureHash = graphDependencyHash,
            SemanticDependencySignatureHash = semantic?.DependencySemanticSignatureHash ?? "",
            TypedDependencySignatureHash = typed?.DependencyTypedSemanticHash ?? "",
            MemberDependencySignatureHash = member?.DependencyIndexHash ?? "",
            MirDependencySignatureHash = mir?.TypedSemanticHash ?? "",
            GlobalCoherenceHash = globalCoherenceHash,
            SemanticLocalSignatureHash = semantic?.ExportSurfaceHash ?? "",
            TypedLocalSignatureHash = typed?.LocalSurfaceHash ?? "",
            MemberLocalSignatureHash = member?.LocalIndexHash ?? "",
            MirLocalSignatureHash = mir?.MirFunctionModuleFingerprint ?? "",
            SchemaVersion = CurrentSchemaVersion
        });

        return new ProjectModuleDependencySignatureNode(
            graphNode.ModuleKey,
            graphNode.Dependencies,
            sourceHash,
            inputSignatureHash,
            graphDependencyHash,
            semantic?.DependencySemanticSignatureHash ?? "",
            typed?.DependencyTypedSemanticHash ?? "",
            member?.DependencyIndexHash ?? "",
            mir?.TypedSemanticHash ?? "",
            globalCoherenceHash,
            combinedHash,
            SourceAvailable: sourceAvailable,
            SemanticAvailable: semantic != null,
            TypedAvailable: typed != null,
            MemberIndexAvailable: member != null,
            MirAvailable: mir != null);
    }

    private static string ComputeGlobalCoherenceHash(ImplOverlapCheckSnapshot? implOverlapChecks)
    {
        if (implOverlapChecks == null)
        {
            return "";
        }

        return ModuleArtifactHash.ComputeJsonHash(new
        {
            implOverlapChecks.SchemaVersion,
            Entries = implOverlapChecks.Entries
                .OrderBy(static entry => entry.QueryKey, StringComparer.Ordinal)
                .ThenBy(static entry => entry.TraitKey, StringComparer.Ordinal)
                .ThenBy(static entry => entry.CanonicalImplementingType, StringComparer.Ordinal)
                .ThenBy(static entry => entry.CanonicalTraitTypeArgs, StringComparer.Ordinal)
                .Select(static entry => new
                {
                    entry.QueryKey,
                    entry.TraitKey,
                    entry.CanonicalImplementingType,
                    entry.CanonicalTraitTypeArgs,
                    entry.RequestedTraitTypeArgs,
                    entry.CandidateCount,
                    entry.CandidateSetFingerprint,
                    entry.NonOverlappingCandidateCount,
                    entry.SpecializationAllowedCandidateCount,
                    entry.HasConflict,
                    entry.ConflictingImplKey,
                    entry.SpecializationRelation
                })
        });
    }
}

public sealed record ProjectModuleDependencySignatureNode(
    string ModuleKey,
    IReadOnlyList<string> Dependencies,
    string SourceHash,
    string InputSignatureHash,
    string GraphDependencySignatureHash,
    string SemanticDependencySignatureHash,
    string TypedDependencySignatureHash,
    string MemberDependencySignatureHash,
    string MirDependencySignatureHash,
    string GlobalCoherenceHash,
    string CombinedDependencySignatureHash,
    bool SourceAvailable,
    bool SemanticAvailable,
    bool TypedAvailable,
    bool MemberIndexAvailable,
    bool MirAvailable)
{
    public bool IsCompatibleWith(
        ProjectModuleDependencySignatureNode previous,
        ProjectModuleDependencySignatureRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(previous);

        if (!SourceAvailable ||
            !previous.SourceAvailable ||
            !string.Equals(SourceHash, previous.SourceHash, StringComparison.Ordinal) ||
            !string.Equals(InputSignatureHash, previous.InputSignatureHash, StringComparison.Ordinal) ||
            !string.Equals(GraphDependencySignatureHash, previous.GraphDependencySignatureHash, StringComparison.Ordinal))
        {
            return false;
        }

        if (!SemanticAvailable ||
            !previous.SemanticAvailable ||
            !string.Equals(SemanticDependencySignatureHash, previous.SemanticDependencySignatureHash, StringComparison.Ordinal))
        {
            return false;
        }

        if (requirement == ProjectModuleDependencySignatureRequirement.SemanticOnly)
        {
            return true;
        }

        if (!TypedAvailable ||
            !previous.TypedAvailable ||
            !string.Equals(TypedDependencySignatureHash, previous.TypedDependencySignatureHash, StringComparison.Ordinal))
        {
            return false;
        }

        if (requirement == ProjectModuleDependencySignatureRequirement.SemanticTyped)
        {
            return true;
        }

        return MemberIndexAvailable &&
               previous.MemberIndexAvailable &&
               MirAvailable &&
               previous.MirAvailable &&
               string.Equals(MemberDependencySignatureHash, previous.MemberDependencySignatureHash, StringComparison.Ordinal) &&
               string.Equals(MirDependencySignatureHash, previous.MirDependencySignatureHash, StringComparison.Ordinal) &&
               string.Equals(GlobalCoherenceHash, previous.GlobalCoherenceHash, StringComparison.Ordinal);
    }
}

public enum ProjectModuleDependencySignatureRequirement
{
    SemanticOnly,
    SemanticTyped,
    SemanticTypedMemberAndMir
}
