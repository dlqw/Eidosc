namespace Eidosc.Pipeline;

using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Mir;
using Eidosc.Symbols;

internal sealed record AstStableNodeEntry(
    EidosAstNode Node,
    AstInferredTypeStableKeyPayload StableIdentity,
    int Ordinal);

internal static class AstStableNodeTraversal
{
    private const int CompilationRootPathMarker = -1;
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> StructuralProperties = new();

    public static IReadOnlyList<AstStableNodeEntry> Enumerate(ModuleDecl ast)
    {
        ArgumentNullException.ThrowIfNull(ast);

        var result = new List<AstStableNodeEntry>();
        var visited = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
        Visit(
            ast,
            result,
            visited,
            currentModuleKey: ToAstStableModuleKey(ast),
            currentModuleIdentityKey: ToAstStableModuleIdentityKey(ast),
            siblingPath: [],
            isCompilationRoot: true);
        return result;
    }

    public static IReadOnlyList<AstStableNodeEntry> EnumerateModule(
        ModuleDecl ast,
        string moduleKey,
        string? moduleIdentityKey = null,
        IReadOnlyList<string>? sourcePaths = null)
    {
        return EnumerateModule(Enumerate(ast), moduleKey, moduleIdentityKey, sourcePaths);
    }

    public static IReadOnlyList<AstStableNodeEntry> EnumerateModule(
        IReadOnlyList<AstStableNodeEntry> allNodes,
        string moduleKey,
        string? moduleIdentityKey = null,
        IReadOnlyList<string>? sourcePaths = null)
    {
        ArgumentNullException.ThrowIfNull(allNodes);
        var normalizedSourcePaths = (sourcePaths ?? [])
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeSourcePath)
            .ToHashSet(SourcePathComparer);
        var matched = allNodes
            .Where(entry => MatchesModule(entry.StableIdentity, moduleKey, moduleIdentityKey))
            .ToArray();
        var hasExplicitModuleMatch = matched.Any(static entry =>
            entry.Node is ModuleDecl &&
            !IsCompilationRoot(entry));
        if (hasExplicitModuleMatch)
        {
            return matched;
        }

        var ownsRootNodes = OwnsRootNodes(moduleKey, sourcePaths);
        return ownsRootNodes
            ? allNodes
                .Where(entry =>
                    normalizedSourcePaths.Count == 0 ||
                    normalizedSourcePaths.Contains(NormalizeSourcePath(entry.StableIdentity.Span.FilePath ?? "")))
                .ToArray()
            : matched;
    }

    public static bool MatchesModule(
        AstInferredTypeStableKeyPayload identity,
        string moduleKey,
        string? moduleIdentityKey = null)
    {
        if (!string.IsNullOrWhiteSpace(moduleIdentityKey) &&
            string.Equals(identity.ModuleIdentityKey, moduleIdentityKey, StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(identity.ModuleKey, moduleKey, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(moduleKey, WellKnownStrings.SpecialNames.Main, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(identity.ModuleKey, "<root>", StringComparison.Ordinal);
    }

    public static bool MatchesCompilationRoot(
        ModuleDecl ast,
        string moduleKey,
        string? moduleIdentityKey = null)
    {
        ArgumentNullException.ThrowIfNull(ast);
        if (!string.IsNullOrWhiteSpace(moduleIdentityKey) &&
            string.Equals(ToAstStableModuleIdentityKey(ast), moduleIdentityKey, StringComparison.Ordinal))
        {
            return true;
        }

        return string.Equals(ToAstStableModuleKey(ast), moduleKey, StringComparison.Ordinal);
    }

    public static IEnumerable<EidosAstNode> GetStructuralChildren(EidosAstNode node)
    {
        foreach (var property in StructuralProperties.GetOrAdd(node.GetType(), CreateStructuralProperties))
        {
            if (IsSemanticAttachment(node, property.Name))
            {
                continue;
            }

            var value = property.GetValue(node);
            if (value is EidosAstNode child)
            {
                yield return child;
                continue;
            }

            if (value is not IEnumerable sequence || value is string)
            {
                continue;
            }

            foreach (var item in sequence)
            {
                if (item is EidosAstNode itemNode)
                {
                    yield return itemNode;
                }
            }
        }
    }

    private static void Visit(
        EidosAstNode node,
        List<AstStableNodeEntry> result,
        HashSet<EidosAstNode> visited,
        string currentModuleKey,
        string currentModuleIdentityKey,
        IReadOnlyList<int> siblingPath,
        bool isCompilationRoot)
    {
        if (!visited.Add(node))
        {
            return;
        }

        var stableSiblingPath = siblingPath;
        var childSiblingPath = siblingPath;
        if (node is ModuleDecl module)
        {
            currentModuleKey = ToAstStableModuleKey(module);
            currentModuleIdentityKey = ToAstStableModuleIdentityKey(module);
            stableSiblingPath = isCompilationRoot ? [CompilationRootPathMarker] : [];
            childSiblingPath = [];
        }

        var stableIdentity = AstInferredTypeStableKeyPayload.Create(
            currentModuleKey,
            currentModuleIdentityKey,
            node,
            MirFormatter.GetAstNodeDetails(node),
            stableSiblingPath);
        result.Add(new AstStableNodeEntry(node, stableIdentity, result.Count));

        var childIndex = 0;
        foreach (var child in GetStructuralChildren(node))
        {
            Visit(
                child,
                result,
                visited,
                currentModuleKey,
                currentModuleIdentityKey,
                [.. childSiblingPath, childIndex],
                isCompilationRoot: false);
            childIndex++;
        }
    }

    private static PropertyInfo[] CreateStructuralProperties(Type nodeType) =>
        nodeType.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.GetIndexParameters().Length == 0 && property.CanRead)
            .Where(static property =>
                typeof(EidosAstNode).IsAssignableFrom(property.PropertyType) ||
                (property.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(property.PropertyType)))
            .OrderBy(static property => GetInheritanceDepth(property.DeclaringType))
            .ThenBy(static property => property.MetadataToken)
            .ToArray();

    private static int GetInheritanceDepth(Type? type)
    {
        var depth = 0;
        while (type != null)
        {
            depth++;
            type = type.BaseType;
        }

        return depth;
    }

    private static bool IsSemanticAttachment(EidosAstNode node, string propertyName) =>
        (node is BlockExpr && propertyName == nameof(BlockExpr.ResultExpression)) ||
        (node is AssociatedConstExpr && propertyName == nameof(AssociatedConstExpr.ImplementationValue)) ||
        (node is ContextualRecordLiteralExpr && propertyName == nameof(ContextualRecordLiteralExpr.DesugaredCtor)) ||
        (node is RecordUpdateExpr &&
         propertyName is nameof(RecordUpdateExpr.DesugaredCtor) or nameof(RecordUpdateExpr.DesugaredMatch));

    private static string ToAstStableModuleKey(ModuleDecl module)
    {
        if (module.Path.Count == 0)
        {
            return string.IsNullOrWhiteSpace(module.PackageAlias)
                ? "<root>"
                : $"{module.PackageAlias}::<root>";
        }

        return ModuleRegistry.ToModuleKey(module.PackageAlias, module.Path);
    }

    private static string ToAstStableModuleIdentityKey(ModuleDecl module)
    {
        var package = string.IsNullOrWhiteSpace(module.PackageInstanceKey)
            ? module.PackageAlias ?? ModuleIdentity.CurrentPackageInstanceKey
            : string.IsNullOrWhiteSpace(module.PackageAlias)
                ? module.PackageInstanceKey
                : $"{module.PackageAlias}@{module.PackageInstanceKey}";
        var path = module.Path.Count == 0
            ? "<root>"
            : string.Join(WellKnownStrings.Separators.Path, module.Path);
        return $"{package}::{path}";
    }

    private static string NormalizeSourcePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith('<'))
        {
            return path.Replace('\\', '/');
        }

        try
        {
            return Path.GetFullPath(path).Replace('\\', '/');
        }
        catch
        {
            return path.Replace('\\', '/');
        }
    }

    private static StringComparer SourcePathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static bool IsCompilationRoot(AstStableNodeEntry entry) =>
        entry.StableIdentity.SiblingPath is [CompilationRootPathMarker];

    private static bool OwnsRootNodes(string moduleKey, IReadOnlyList<string>? sourcePaths)
    {
        var modulePath = moduleKey;
        var packageSeparator = modulePath.LastIndexOf("::", StringComparison.Ordinal);
        if (packageSeparator >= 0)
        {
            modulePath = modulePath[(packageSeparator + 2)..];
        }

        var pathSeparator = Math.Max(modulePath.LastIndexOf('/'), modulePath.LastIndexOf('\\'));
        var moduleName = pathSeparator >= 0 ? modulePath[(pathSeparator + 1)..] : modulePath;
        foreach (var sourcePath in sourcePaths ?? [])
        {
            var sourceName = Path.GetFileNameWithoutExtension(sourcePath);
            if (!string.IsNullOrWhiteSpace(sourceName))
            {
                return string.Equals(moduleName, sourceName, StringComparison.OrdinalIgnoreCase);
            }
        }
        return false;
    }
}
