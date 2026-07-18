using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using Eidosc.Ast.Declarations;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Hir;

internal static class HirGeneratedOriginPropagator
{
    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> StructuralProperties = new();

    public static void Propagate(ModuleDecl ast, HirModule hir)
    {
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(hir);

        var origins = BuildOriginMap(ast);
        if (origins.BySpan.Count == 0)
        {
            return;
        }

        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        Visit(hir, [], origins, visited);
    }

    private static OriginMap BuildOriginMap(ModuleDecl ast)
    {
        var bySpan = new Dictionary<SourceSpan, List<IReadOnlyList<GeneratedDeclarationOrigin>>>();
        var byDeclarationSymbol = new Dictionary<SymbolId, IReadOnlyList<GeneratedDeclarationOrigin>>();
        foreach (var entry in AstStableNodeTraversal.Enumerate(ast))
        {
            var chain = entry.Node.GeneratedOriginChain;
            if (chain.Count == 0)
            {
                continue;
            }

            if (!bySpan.TryGetValue(entry.Node.Span, out var spanChains))
            {
                spanChains = [];
                bySpan[entry.Node.Span] = spanChains;
            }

            if (!spanChains.Any(existing => HasSameIdentity(existing, chain)))
            {
                spanChains.Add(chain);
            }

            if (entry.Node is Declaration && entry.Node.SymbolId.IsValid)
            {
                byDeclarationSymbol[entry.Node.SymbolId] = chain;
            }
        }

        foreach (var spanChains in bySpan.Values)
        {
            spanChains.Sort(static (left, right) => left.Count.CompareTo(right.Count));
        }

        return new OriginMap(bySpan, byDeclarationSymbol);
    }

    private static void Visit(
        object? value,
        IReadOnlyList<GeneratedDeclarationOrigin> inheritedChain,
        OriginMap origins,
        HashSet<object> visited)
    {
        if (value == null || value is string || value.GetType().IsValueType || !visited.Add(value))
        {
            return;
        }

        if (value is IEnumerable sequence)
        {
            foreach (var item in sequence)
            {
                Visit(item, inheritedChain, origins, visited);
            }
            return;
        }

        var currentChain = inheritedChain;
        if (value is HirNode node)
        {
            currentChain = ResolveChain(
                node.Span,
                node is HirDecl declaration ? declaration.SymbolId : SymbolId.None,
                inheritedChain,
                origins);

            node.GeneratedOriginChain = currentChain.Count == 0 ? [] : currentChain.ToArray();
        }
        else if (value is HirPattern pattern)
        {
            currentChain = ResolveChain(pattern.Span, SymbolId.None, inheritedChain, origins);

            pattern.GeneratedOriginChain = currentChain.Count == 0 ? [] : currentChain.ToArray();
        }
        else if (!string.Equals(value.GetType().Namespace, typeof(HirNode).Namespace, StringComparison.Ordinal))
        {
            return;
        }

        foreach (var property in StructuralProperties.GetOrAdd(value.GetType(), CreateStructuralProperties))
        {
            Visit(property.GetValue(value), currentChain, origins, visited);
        }
    }

    private static IReadOnlyList<GeneratedDeclarationOrigin> ResolveChain(
        SourceSpan span,
        SymbolId declarationSymbol,
        IReadOnlyList<GeneratedDeclarationOrigin> inheritedChain,
        OriginMap origins)
    {
        if (declarationSymbol.IsValid && origins.ByDeclarationSymbol.TryGetValue(declarationSymbol, out var symbolChain))
        {
            return symbolChain;
        }

        if (!origins.BySpan.TryGetValue(span, out var candidates))
        {
            return inheritedChain;
        }

        var inheritedMatch = candidates.FirstOrDefault(candidate => HasSameIdentity(candidate, inheritedChain));
        if (inheritedMatch != null)
        {
            return inheritedMatch;
        }

        return candidates.FirstOrDefault(candidate => StartsWithIdentity(candidate, inheritedChain)) ?? inheritedChain;
    }

    private static bool StartsWithIdentity(
        IReadOnlyList<GeneratedDeclarationOrigin> candidate,
        IReadOnlyList<GeneratedDeclarationOrigin> prefix)
    {
        if (candidate.Count < prefix.Count)
        {
            return false;
        }

        for (var index = 0; index < prefix.Count; index++)
        {
            if (!string.Equals(
                    candidate[index].StableIdentity,
                    prefix[index].StableIdentity,
                    StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool HasSameIdentity(
        IReadOnlyList<GeneratedDeclarationOrigin> left,
        IReadOnlyList<GeneratedDeclarationOrigin> right) =>
        left.Count == right.Count && StartsWithIdentity(left, right);

    private static PropertyInfo[] CreateStructuralProperties(Type type) =>
        type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(static property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Where(static property => property.Name != nameof(HirNode.GeneratedOriginChain))
            .Where(static property =>
                typeof(HirNode).IsAssignableFrom(property.PropertyType) ||
                (property.PropertyType != typeof(string) && typeof(IEnumerable).IsAssignableFrom(property.PropertyType)) ||
                string.Equals(property.PropertyType.Namespace, typeof(HirNode).Namespace, StringComparison.Ordinal))
            .OrderBy(static property => property.MetadataToken)
            .ToArray();

    private sealed record OriginMap(
        IReadOnlyDictionary<SourceSpan, List<IReadOnlyList<GeneratedDeclarationOrigin>>> BySpan,
        IReadOnlyDictionary<SymbolId, IReadOnlyList<GeneratedDeclarationOrigin>> ByDeclarationSymbol);
}
