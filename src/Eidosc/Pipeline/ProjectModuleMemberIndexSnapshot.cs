namespace Eidosc.Pipeline;

using Eidosc.Symbols;

public sealed record ProjectModuleMemberIndexSnapshot(
    string SchemaVersion,
    IReadOnlyList<ProjectModuleMemberIndexNode> Nodes)
{
    public const string CurrentSchemaVersion = "module-member-index-snapshot-v1";

    public static ProjectModuleMemberIndexSnapshot FromSymbolTable(
        SymbolTable symbolTable,
        ProjectModuleGraphSnapshot? graph = null)
    {
        var rawNodes = symbolTable.Modules.ModulePaths
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => CreateNode(symbolTable, pair.Key, pair.Value, "", "", ""))
            .ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal);
        var nodes = AddStableHashes(rawNodes, graph);

        return new ProjectModuleMemberIndexSnapshot(CurrentSchemaVersion, nodes);
    }

    private static ProjectModuleMemberIndexNode CreateNode(
        SymbolTable symbolTable,
        string moduleKey,
        SymbolId moduleId,
        string localIndexHash,
        string dependencyIndexHash,
        string memberIndexHash)
    {
        var module = symbolTable.Modules.GetModule(moduleId);
        var members = symbolTable.Modules.GetModuleMembers(moduleId)
            .Select(id => CreateBinding(symbolTable, id, GetSymbolName(symbolTable, id), GetResolutionKind(symbolTable, id)))
            .Where(static binding => !string.IsNullOrWhiteSpace(binding.Name))
            .OrderBy(static binding => binding.Name, StringComparer.Ordinal)
            .ThenBy(static binding => binding.Kind, StringComparer.Ordinal)
            .ThenBy(static binding => binding.CanonicalSymbol, StringComparer.Ordinal)
            .ToArray();
        var exports = (module?.ExportedBindings ?? [])
            .Select(binding => CreateBinding(symbolTable, binding.SymbolId, binding.Name, binding.Kind))
            .Where(static binding => !string.IsNullOrWhiteSpace(binding.Name))
            .OrderBy(static binding => binding.Name, StringComparer.Ordinal)
            .ThenBy(static binding => binding.Kind, StringComparer.Ordinal)
            .ThenBy(static binding => binding.CanonicalSymbol, StringComparer.Ordinal)
            .ToArray();
        var accessible = symbolTable.Modules.GetAccessibleBindings(moduleId, moduleId)
            .Select(binding => CreateBinding(symbolTable, binding.SymbolId, binding.Name, binding.Kind))
            .Where(static binding => !string.IsNullOrWhiteSpace(binding.Name))
            .Distinct()
            .OrderBy(static binding => binding.Name, StringComparer.Ordinal)
            .ThenBy(static binding => binding.Kind, StringComparer.Ordinal)
            .ThenBy(static binding => binding.CanonicalSymbol, StringComparer.Ordinal)
            .ToArray();

        return new ProjectModuleMemberIndexNode(
            moduleKey,
            module?.Identity.ToIdentityKey() ?? moduleKey,
            module?.UsesExplicitExports == true,
            localIndexHash,
            dependencyIndexHash,
            memberIndexHash,
            members,
            exports,
            accessible);
    }

    private static IReadOnlyList<ProjectModuleMemberIndexNode> AddStableHashes(
        IReadOnlyDictionary<string, ProjectModuleMemberIndexNode> rawNodes,
        ProjectModuleGraphSnapshot? graph)
    {
        var nodesByModule = graph?.Nodes.ToDictionary(static node => node.ModuleKey, StringComparer.Ordinal) ??
                            rawNodes.Keys.ToDictionary(
                                static moduleKey => moduleKey,
                                static moduleKey => new ProjectModuleGraphNode(moduleKey, [], [], []),
                                StringComparer.Ordinal);
        var layers = graph?.TopologicalLayers ?? [rawNodes.Keys.OrderBy(static key => key, StringComparer.Ordinal).ToArray()];
        var hashed = new Dictionary<string, ProjectModuleMemberIndexNode>(StringComparer.Ordinal);

        foreach (var layer in layers)
        {
            foreach (var moduleKey in layer.OrderBy(static key => key, StringComparer.Ordinal))
            {
                if (!rawNodes.TryGetValue(moduleKey, out var rawNode))
                {
                    continue;
                }

                nodesByModule.TryGetValue(moduleKey, out var graphNode);
                var localHash = ModuleArtifactHash.ComputeJsonHash(new
                {
                    rawNode.ModuleKey,
                    rawNode.ModuleIdentityKey,
                    rawNode.UsesExplicitExports,
                    rawNode.Members,
                    rawNode.Exports,
                    rawNode.AccessibleBindings
                });
                var dependencyHash = ModuleArtifactHash.ComputeDependencySignatureHash(
                    (graphNode?.Dependencies ?? [])
                    .Select(dependency => hashed.TryGetValue(dependency, out var dependencyNode)
                        ? dependencyNode.MemberIndexHash
                        : ModuleArtifactHash.ComputeTextHash($"missing-member-index:{dependency}")));
                var memberIndexHash = ModuleArtifactHash.ComputeJsonHash(new
                {
                    rawNode.ModuleKey,
                    LocalIndexHash = localHash,
                    DependencyIndexHash = dependencyHash,
                    SchemaVersion = CurrentSchemaVersion
                });

                hashed[moduleKey] = rawNode with
                {
                    LocalIndexHash = localHash,
                    DependencyIndexHash = dependencyHash,
                    MemberIndexHash = memberIndexHash
                };
            }
        }

        return rawNodes.Keys
            .Select(moduleKey => hashed.TryGetValue(moduleKey, out var node)
                ? node
                : CreateFallbackHashedNode(rawNodes[moduleKey]))
            .OrderBy(static node => node.ModuleKey, StringComparer.Ordinal)
            .ToArray();
    }

    private static ProjectModuleMemberIndexNode CreateFallbackHashedNode(
        ProjectModuleMemberIndexNode rawNode)
    {
        var localHash = ModuleArtifactHash.ComputeJsonHash(new
        {
            rawNode.ModuleKey,
            rawNode.ModuleIdentityKey,
            rawNode.UsesExplicitExports,
            rawNode.Members,
            rawNode.Exports,
            rawNode.AccessibleBindings
        });
        var dependencyHash = ModuleArtifactHash.ComputeDependencySignatureHash([]);
        var memberIndexHash = ModuleArtifactHash.ComputeJsonHash(new
        {
            rawNode.ModuleKey,
            LocalIndexHash = localHash,
            DependencyIndexHash = dependencyHash,
            SchemaVersion = CurrentSchemaVersion
        });

        return rawNode with
        {
            LocalIndexHash = localHash,
            DependencyIndexHash = dependencyHash,
            MemberIndexHash = memberIndexHash
        };
    }

    private static ProjectModuleMemberBinding CreateBinding(
        SymbolTable symbolTable,
        SymbolId symbolId,
        string name,
        ResolutionKind kind)
    {
        return new ProjectModuleMemberBinding(
            name,
            kind.ToString(),
            FormatCanonicalSymbol(symbolTable, symbolId),
            symbolTable.GetSymbol(symbolId)?.IsPublic == true);
    }

    private static string GetSymbolName(SymbolTable symbolTable, SymbolId symbolId) =>
        symbolTable.GetSymbol(symbolId)?.Name ?? "";

    private static ResolutionKind GetResolutionKind(SymbolTable symbolTable, SymbolId symbolId)
    {
        return symbolTable.GetSymbol(symbolId) switch
        {
            FuncSymbol => ResolutionKind.Value,
            VarSymbol => ResolutionKind.Value,
            AdtSymbol => ResolutionKind.Type,
            CtorSymbol => ResolutionKind.Constructor,
            TraitSymbol => ResolutionKind.Type,
            EffectSymbol => ResolutionKind.Effect,
            ModuleSymbol => ResolutionKind.Module,
            _ => ResolutionKind.Value
        };
    }

    private static string FormatCanonicalSymbol(SymbolTable symbolTable, SymbolId symbolId)
    {
        if (!symbolId.IsValid)
        {
            return "";
        }

        var symbol = symbolTable.GetSymbol(symbolId);
        if (symbol == null)
        {
            return $"missing-symbol:{symbolId.Value}";
        }

        var moduleName = symbolTable.Modules.TryGetOwningModule(symbolId, out var module)
            ? ModuleRegistry.FormatModuleFullName(module)
            : "";
        return string.IsNullOrWhiteSpace(moduleName)
            ? $"{symbol.Kind}:{symbol.Name}"
            : $"{moduleName}::{symbol.Kind}:{symbol.Name}";
    }
}

public sealed record ProjectModuleMemberIndexNode(
    string ModuleKey,
    string ModuleIdentityKey,
    bool UsesExplicitExports,
    string LocalIndexHash,
    string DependencyIndexHash,
    string MemberIndexHash,
    IReadOnlyList<ProjectModuleMemberBinding> Members,
    IReadOnlyList<ProjectModuleMemberBinding> Exports,
    IReadOnlyList<ProjectModuleMemberBinding> AccessibleBindings);

public sealed record ProjectModuleMemberBinding(
    string Name,
    string Kind,
    string CanonicalSymbol,
    bool IsPublic);
