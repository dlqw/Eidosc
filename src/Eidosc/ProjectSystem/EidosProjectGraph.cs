using Eidosc.Pipeline;

namespace Eidosc.ProjectSystem;

public sealed record ResolvedEidosProjectExternalDependency(
    string Name,
    string NodeKey,
    string ProjectFilePath,
    string ProjectDirectory,
    string TargetName,
    string Kind,
    string EntryFilePath);

public sealed record ResolvedEidosProjectBuildGraphProjectDependency(
    string Name,
    string NodeKey,
    string ProjectFilePath,
    string TargetName,
    string EntryFilePath);

public sealed record ResolvedEidosProjectBuildGraphNode(
    string Key,
    string ProjectFilePath,
    string ProjectDirectory,
    string TargetName,
    string Kind,
    string EntryFilePath,
    string[] TargetDependencies,
    ResolvedEidosProjectBuildGraphProjectDependency[] ProjectDependencies);

public sealed record ResolvedEidosProjectBuildGraph(
    string RootNodeKey,
    string[] BuildOrder,
    ResolvedEidosProjectBuildGraphNode[] Nodes);

public sealed record ResolvedEidosProjectTarget(
    string ProjectFilePath,
    string ProjectDirectory,
    string TargetName,
    string Kind,
    string EntryFilePath,
    ProjectImportSearchResolution ImportResolution,
    string[] EffectiveSearchRoots,
    string[] DependencySearchRoots,
    Dictionary<string, string[]> PackageImportRoots,
    EidosFfiConfiguration? Ffi,
    string[] TargetDependencies,
    ResolvedEidosProjectExternalDependency[] ProjectDependencies,
    ResolvedEidosProjectBuildGraph BuildGraph);

public static class EidosProjectGraphResolver
{
    public static ResolvedEidosProjectTarget ResolveTarget(
        string projectPath,
        string? targetName = null,
        IReadOnlyList<string>? explicitImportRoots = null)
    {
        var loadedProject = EidosProjectConfigurationLoader.LoadFromPath(projectPath);
        return ResolveTarget(loadedProject, targetName, explicitImportRoots);
    }

    public static ResolvedEidosProjectTarget ResolveTarget(
        LoadedEidosProjectConfiguration projectConfiguration,
        string? targetName = null,
        IReadOnlyList<string>? explicitImportRoots = null)
    {
        var context = new ResolutionContext(projectConfiguration, targetName, explicitImportRoots);
        return context.Resolve();
    }

    private sealed class ResolutionContext
    {
        private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

        private readonly LoadedEidosProjectConfiguration _rootProject;
        private readonly string? _requestedTargetName;
        private readonly IReadOnlyList<string>? _explicitImportRoots;
        private readonly Dictionary<string, ProjectIndex> _projectIndices = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ResolvedTargetNode> _resolvedNodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _resolutionStack = [];

        public ResolutionContext(
            LoadedEidosProjectConfiguration rootProject,
            string? requestedTargetName,
            IReadOnlyList<string>? explicitImportRoots)
        {
            _rootProject = rootProject;
            _requestedTargetName = string.IsNullOrWhiteSpace(requestedTargetName)
                ? null
                : requestedTargetName.Trim();
            _explicitImportRoots = explicitImportRoots;
        }

        public ResolvedEidosProjectTarget Resolve()
        {
            var rootTargetName = ResolveSelectedTargetName(_rootProject, _requestedTargetName);
            var rootNode = ResolveTargetNode(_rootProject, rootTargetName);
            var importResolution = EidosProjectConfigurationLoader.ResolveImportSearchRoots(
                _rootProject.FilePath,
                _explicitImportRoots);
            var dependencySearchRoots = CollectDependencySearchRoots(rootNode);

            var packageGraph = ResolvePackageGraph(_rootProject);
            var packageSearchRoots = packageGraph.GetAllSearchRoots();
            var packageImportRoots = ResolvePackageImportRoots(packageGraph);
            var packageFfi = packageGraph.GetCombinedFfiConfiguration();

            var allDepRoots = EidosProjectConfigurationLoader.CombineSearchRoots(
                dependencySearchRoots,
                packageSearchRoots);

            var effectiveSearchRoots = EidosProjectConfigurationLoader.CombineSearchRoots(
                importResolution.EffectiveSearchRoots,
                allDepRoots);

            return new ResolvedEidosProjectTarget(
                _rootProject.FilePath,
                _rootProject.ProjectDirectory,
                rootNode.TargetName,
                rootNode.Kind,
                rootNode.EntryFilePath,
                importResolution,
                effectiveSearchRoots,
                dependencySearchRoots,
                packageImportRoots,
                CombineFfi(_rootProject.Configuration.Ffi, packageFfi),
                rootNode.TargetDependencies,
                rootNode.ProjectDependencies,
                BuildGraph(rootNode));
        }

        private ResolvedTargetNode ResolveTargetNode(
            LoadedEidosProjectConfiguration project,
            string targetName)
        {
            var projectIndex = GetProjectIndex(project);
            if (!projectIndex.Targets.TryGetValue(targetName, out var targetConfiguration))
            {
                throw new InvalidOperationException(PipelineMessages.ProjectDoesNotDefineTarget(
                    project.FilePath,
                    targetName,
                    FormatAvailableTargets(projectIndex)));
            }

            var nodeKey = CreateNodeKey(project.FilePath, targetConfiguration.Name);
            if (_resolvedNodes.TryGetValue(nodeKey, out var cachedNode))
            {
                return cachedNode;
            }

            var cycleStartIndex = FindResolutionStackIndex(nodeKey);
            if (cycleStartIndex >= 0)
            {
                var cycle = _resolutionStack
                    .Skip(cycleStartIndex)
                    .Append(nodeKey)
                    .Select(FormatNodeKey)
                    .ToArray();
                throw new InvalidOperationException(
                    PipelineMessages.ProjectBuildGraphDependencyCycleDetected(string.Join(" -> ", cycle)));
            }

            _resolutionStack.Add(nodeKey);
            try
            {
                var targetKind = NormalizeTargetKind(project, targetConfiguration);
                var entryFilePath = ResolveTargetEntryFile(project, targetConfiguration);
                var directTargetDependencies = NormalizeReferencedNames(
                    targetConfiguration.Dependencies,
                    project,
                    targetConfiguration.Name,
                    "target dependency");
                var directProjectDependencies = NormalizeReferencedNames(
                    targetConfiguration.ProjectDependencies,
                    project,
                    targetConfiguration.Name,
                    "project dependency");

                var targetDependencyNodes = new List<ResolvedTargetNode>(directTargetDependencies.Length);
                foreach (var dependencyName in directTargetDependencies)
                {
                    if (!projectIndex.Targets.ContainsKey(dependencyName))
                    {
                        throw new InvalidOperationException(PipelineMessages.TargetDependsOnMissingTarget(
                            targetConfiguration.Name,
                            project.FilePath,
                            dependencyName));
                    }

                    targetDependencyNodes.Add(ResolveTargetNode(project, dependencyName));
                }

                var resolvedProjectDependencies = new List<ResolvedEidosProjectExternalDependency>(directProjectDependencies.Length);
                var projectDependencyNodes = new List<ResolvedTargetNode>(directProjectDependencies.Length);
                foreach (var dependencyName in directProjectDependencies)
                {
                    if (!projectIndex.Dependencies.TryGetValue(dependencyName, out var dependencyConfiguration))
                    {
                        throw new InvalidOperationException(PipelineMessages.TargetDependsOnMissingProjectDependency(
                            targetConfiguration.Name,
                            project.FilePath,
                            dependencyName));
                    }

                    if (string.IsNullOrWhiteSpace(dependencyConfiguration.Path))
                    {
                        throw new InvalidOperationException(PipelineMessages.ProjectDependencyRequiresPath(
                            dependencyConfiguration.Name,
                            project.FilePath));
                    }

                    var dependencyProject = EidosProjectConfigurationLoader.LoadFromPath(dependencyConfiguration.Path);
                    var dependencyTargetName = ResolveSelectedTargetName(
                        dependencyProject,
                        dependencyConfiguration.Target);
                    var dependencyTargetNode = ResolveTargetNode(dependencyProject, dependencyTargetName);

                    projectDependencyNodes.Add(dependencyTargetNode);
                    resolvedProjectDependencies.Add(new ResolvedEidosProjectExternalDependency(
                        dependencyConfiguration.Name,
                        dependencyTargetNode.Key,
                        dependencyProject.FilePath,
                        dependencyProject.ProjectDirectory,
                        dependencyTargetNode.TargetName,
                        dependencyTargetNode.Kind,
                        dependencyTargetNode.EntryFilePath));
                }

                var node = new ResolvedTargetNode(
                    nodeKey,
                    project,
                    targetConfiguration.Name,
                    targetKind,
                    entryFilePath,
                    directTargetDependencies,
                    targetDependencyNodes,
                    resolvedProjectDependencies.ToArray(),
                    projectDependencyNodes);

                _resolvedNodes[nodeKey] = node;
                return node;
            }
            finally
            {
                _resolutionStack.RemoveAt(_resolutionStack.Count - 1);
            }
        }

        private ProjectIndex GetProjectIndex(LoadedEidosProjectConfiguration project)
        {
            if (_projectIndices.TryGetValue(project.FilePath, out var cachedIndex))
            {
                return cachedIndex;
            }

            var targets = new Dictionary<string, EidosProjectTargetConfiguration>(NameComparer);
            foreach (var target in project.Configuration.Targets)
            {
                if (string.IsNullOrWhiteSpace(target.Name))
                {
                    throw new InvalidOperationException(
                        PipelineMessages.ProjectContainsTargetWithEmptyName(project.FilePath));
                }

                if (!targets.TryAdd(target.Name, target))
                {
                    throw new InvalidOperationException(
                        PipelineMessages.ProjectDeclaresDuplicateTarget(project.FilePath, target.Name));
                }
            }

            var dependencies = new Dictionary<string, EidosProjectDependencyConfiguration>(NameComparer);
            foreach (var dependency in project.Configuration.Dependencies)
            {
                if (string.IsNullOrWhiteSpace(dependency.Name))
                {
                    throw new InvalidOperationException(
                        PipelineMessages.ProjectContainsDependencyWithEmptyName(project.FilePath));
                }

                if (!dependencies.TryAdd(dependency.Name, dependency))
                {
                    throw new InvalidOperationException(
                        PipelineMessages.ProjectDeclaresDuplicateDependency(project.FilePath, dependency.Name));
                }
            }

            var index = new ProjectIndex(project, targets, dependencies);
            _projectIndices[project.FilePath] = index;
            return index;
        }

        private string ResolveSelectedTargetName(
            LoadedEidosProjectConfiguration project,
            string? requestedTargetName)
        {
            var projectIndex = GetProjectIndex(project);
            if (!string.IsNullOrWhiteSpace(requestedTargetName))
            {
                if (!projectIndex.Targets.TryGetValue(requestedTargetName, out var selectedTarget))
                {
                    throw new InvalidOperationException(PipelineMessages.ProjectDoesNotDefineTarget(
                        project.FilePath,
                        requestedTargetName,
                        FormatAvailableTargets(projectIndex)));
                }

                return selectedTarget.Name;
            }

            if (!string.IsNullOrWhiteSpace(project.Configuration.DefaultTarget))
            {
                if (!projectIndex.Targets.TryGetValue(project.Configuration.DefaultTarget, out var defaultTarget))
                {
                    throw new InvalidOperationException(PipelineMessages.ProjectDefaultTargetMissing(
                        project.FilePath,
                        project.Configuration.DefaultTarget));
                }

                return defaultTarget.Name;
            }

            return project.Configuration.Targets.Length switch
            {
                0 => throw new InvalidOperationException(
                    PipelineMessages.ProjectDeclaresNoTargets(project.FilePath)),
                1 => project.Configuration.Targets[0].Name,
                _ => throw new InvalidOperationException(PipelineMessages.ProjectDeclaresMultipleTargets(
                    project.FilePath,
                    FormatAvailableTargets(projectIndex)))
            };
        }

        private static string ResolveTargetEntryFile(
            LoadedEidosProjectConfiguration project,
            EidosProjectTargetConfiguration target)
        {
            if (string.IsNullOrWhiteSpace(target.Entry))
            {
                throw new InvalidOperationException(
                    PipelineMessages.TargetRequiresEntryFile(target.Name, project.FilePath));
            }

            if (!File.Exists(target.Entry))
            {
                throw new InvalidOperationException(PipelineMessages.TargetReferencesMissingEntryFile(
                    target.Name,
                    project.FilePath,
                    target.Entry));
            }

            return Path.GetFullPath(target.Entry);
        }

        private static string NormalizeTargetKind(
            LoadedEidosProjectConfiguration project,
            EidosProjectTargetConfiguration target)
        {
            var normalizedKind = target.Kind?.Trim().ToLowerInvariant();
            return normalizedKind switch
            {
                null or "" or "executable" or "exe" => "executable",
                "library" or "lib" => "library",
                _ => throw new InvalidOperationException(PipelineMessages.TargetDeclaresUnsupportedKind(
                    target.Name,
                    project.FilePath,
                    target.Kind))
            };
        }

        private static string[] NormalizeReferencedNames(
            IReadOnlyList<string>? references,
            LoadedEidosProjectConfiguration project,
            string targetName,
            string referenceKind)
        {
            if (references == null || references.Count == 0)
            {
                return [];
            }

            var seen = new HashSet<string>(NameComparer);
            var normalized = new List<string>(references.Count);
            foreach (var reference in references)
            {
                if (string.IsNullOrWhiteSpace(reference))
                {
                    continue;
                }

                var trimmed = reference.Trim();
                if (!seen.Add(trimmed))
                {
                    throw new InvalidOperationException(PipelineMessages.TargetDeclaresDuplicateReference(
                        targetName,
                        project.FilePath,
                        referenceKind,
                        trimmed));
                }

                normalized.Add(trimmed);
            }

            return normalized.ToArray();
        }

        private string[] CollectDependencySearchRoots(ResolvedTargetNode rootNode)
        {
            var visitedNodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var visitedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var collectedRoots = new List<string>();
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            Visit(rootNode);
            return collectedRoots.ToArray();

            void Visit(ResolvedTargetNode node)
            {
                if (!visitedNodes.Add(node.Key))
                {
                    return;
                }

                if (!string.Equals(node.Project.FilePath, _rootProject.FilePath, StringComparison.OrdinalIgnoreCase) &&
                    visitedProjects.Add(node.Project.FilePath))
                {
                    var dependencyImportResolution = EidosProjectConfigurationLoader.ResolveImportSearchRoots(node.Project.FilePath);
                    foreach (var root in dependencyImportResolution.EffectiveSearchRoots)
                    {
                        if (seenRoots.Add(root))
                        {
                            collectedRoots.Add(root);
                        }
                    }
                }

                foreach (var projectDependencyNode in node.ProjectDependencyNodes)
                {
                    Visit(projectDependencyNode);
                }

                foreach (var targetDependencyNode in node.TargetDependencyNodes)
                {
                    Visit(targetDependencyNode);
                }
            }
        }

        private static ResolvedPackageGraph ResolvePackageGraph(LoadedEidosProjectConfiguration project)
        {
            var config = project.Configuration;
            if (config.VersionedDependencies == null || config.VersionedDependencies.Count == 0)
            {
                return new ResolvedPackageGraph();
            }

            var lockPath = Path.Combine(project.ProjectDirectory, "eidos.lock.json");
            EidosLockFile? lockFile = null;
            if (File.Exists(lockPath) && !EidosLockFile.TryLoad(lockPath, out lockFile))
            {
                throw new InvalidOperationException(PipelineMessages.FailedToDeserializeLockFile);
            }

            var resolver = new PackageDependencyResolver(project.ProjectDirectory);
            return resolver.Resolve(config, lockFile);
        }

        private static Dictionary<string, string[]> ResolvePackageImportRoots(ResolvedPackageGraph graph)
        {
            var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
            foreach (var (alias, package) in graph.Packages)
            {
                var roots = package.SourceRoots
                    .Concat(package.ImportRoots)
                    .Where(static root => !string.IsNullOrWhiteSpace(root))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                if (roots.Length > 0)
                {
                    result[alias] = roots;
                }
            }

            return result;
        }

        private static EidosFfiConfiguration? CombineFfi(EidosFfiConfiguration? first, EidosFfiConfiguration? second)
        {
            if (first == null)
                return second;
            if (second == null)
                return first;

            return new EidosFfiConfiguration
            {
                Libraries = DistinctOrdinal(first.Libraries.Concat(second.Libraries)),
                LibraryPaths = DistinctPath(first.LibraryPaths.Concat(second.LibraryPaths)),
                IncludePaths = DistinctPath(first.IncludePaths.Concat(second.IncludePaths)),
                NativeSources = DistinctPath(first.NativeSources.Concat(second.NativeSources)),
                LinkerFlags = DistinctOrdinal(first.LinkerFlags.Concat(second.LinkerFlags))
            };
        }

        private static string[] DistinctOrdinal(IEnumerable<string> values) =>
            values.Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();

        private static string[] DistinctPath(IEnumerable<string> values) =>
            values.Where(static value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

        private static ResolvedEidosProjectBuildGraph BuildGraph(ResolvedTargetNode rootNode)
        {
            var orderedNodes = new List<ResolvedTargetNode>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            VisitPreorder(rootNode);

            var buildOrder = new List<string>();
            seen.Clear();
            VisitPostorder(rootNode);

            return new ResolvedEidosProjectBuildGraph(
                rootNode.Key,
                buildOrder.ToArray(),
                orderedNodes.Select(ToBuildGraphNode).ToArray());

            void VisitPreorder(ResolvedTargetNode node)
            {
                if (!seen.Add(node.Key))
                {
                    return;
                }

                orderedNodes.Add(node);
                foreach (var targetDependencyNode in node.TargetDependencyNodes)
                {
                    VisitPreorder(targetDependencyNode);
                }

                foreach (var projectDependencyNode in node.ProjectDependencyNodes)
                {
                    VisitPreorder(projectDependencyNode);
                }
            }

            void VisitPostorder(ResolvedTargetNode node)
            {
                if (!seen.Add(node.Key))
                {
                    return;
                }

                foreach (var targetDependencyNode in node.TargetDependencyNodes)
                {
                    VisitPostorder(targetDependencyNode);
                }

                foreach (var projectDependencyNode in node.ProjectDependencyNodes)
                {
                    VisitPostorder(projectDependencyNode);
                }

                buildOrder.Add(node.Key);
            }
        }

        private static ResolvedEidosProjectBuildGraphNode ToBuildGraphNode(ResolvedTargetNode node)
        {
            return new ResolvedEidosProjectBuildGraphNode(
                node.Key,
                node.Project.FilePath,
                node.Project.ProjectDirectory,
                node.TargetName,
                node.Kind,
                node.EntryFilePath,
                node.TargetDependencies,
                node.ProjectDependencies
                    .Select(dependency => new ResolvedEidosProjectBuildGraphProjectDependency(
                        dependency.Name,
                        dependency.NodeKey,
                        dependency.ProjectFilePath,
                        dependency.TargetName,
                        dependency.EntryFilePath))
                    .ToArray());
        }

        private static string CreateNodeKey(string projectFilePath, string targetName)
        {
            return $"{projectFilePath}#{targetName}";
        }

        private int FindResolutionStackIndex(string nodeKey)
        {
            for (var i = 0; i < _resolutionStack.Count; i++)
            {
                if (string.Equals(_resolutionStack[i], nodeKey, StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static string FormatAvailableTargets(ProjectIndex projectIndex)
        {
            return projectIndex.Targets.Count == 0
                ? "<none>"
                : string.Join(", ", projectIndex.Project.Configuration.Targets.Select(target => target.Name));
        }

        private static string FormatNodeKey(string nodeKey)
        {
            var separatorIndex = nodeKey.LastIndexOf('#');
            if (separatorIndex <= 0 || separatorIndex >= nodeKey.Length - 1)
            {
                return nodeKey;
            }

            var projectFilePath = nodeKey[..separatorIndex];
            var targetName = nodeKey[(separatorIndex + 1)..];
            return $"{projectFilePath}::{targetName}";
        }
    }

    private sealed record ProjectIndex(
        LoadedEidosProjectConfiguration Project,
        Dictionary<string, EidosProjectTargetConfiguration> Targets,
        Dictionary<string, EidosProjectDependencyConfiguration> Dependencies);

    private sealed class ResolvedTargetNode
    {
        public ResolvedTargetNode(
            string key,
            LoadedEidosProjectConfiguration project,
            string targetName,
            string kind,
            string entryFilePath,
            string[] targetDependencies,
            List<ResolvedTargetNode> targetDependencyNodes,
            ResolvedEidosProjectExternalDependency[] projectDependencies,
            List<ResolvedTargetNode> projectDependencyNodes)
        {
            Key = key;
            Project = project;
            TargetName = targetName;
            Kind = kind;
            EntryFilePath = entryFilePath;
            TargetDependencies = targetDependencies;
            TargetDependencyNodes = targetDependencyNodes;
            ProjectDependencies = projectDependencies;
            ProjectDependencyNodes = projectDependencyNodes;
        }

        public string Key { get; }
        public LoadedEidosProjectConfiguration Project { get; }
        public string TargetName { get; }
        public string Kind { get; }
        public string EntryFilePath { get; }
        public string[] TargetDependencies { get; }
        public List<ResolvedTargetNode> TargetDependencyNodes { get; }
        public ResolvedEidosProjectExternalDependency[] ProjectDependencies { get; }
        public List<ResolvedTargetNode> ProjectDependencyNodes { get; }
    }
}
