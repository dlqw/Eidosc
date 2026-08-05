using Eidosc.Utils;
using Eidosc.ProjectSystem;
using Eidosc.Types;
using EidosType = Eidosc.Types.Type;

namespace Eidosc.Semantic;

public static class PreludeCoreImageRegistry
{
    internal const string PackageAlias = "__eidos_prelude_core";

    private static readonly HashSet<string> CoreModules = new(StringComparer.OrdinalIgnoreCase)
    {
        "Alternative",
        "Applicative",
        "Either",
        "Display",
        "Foldable",
        "Functions",
        "Functor",
        "Monad",
        "Monoid",
        "Option",
        "Ordering",
        "Predicate",
        "Prelude",
        "Result",
        "RuntimeArray",
        "Semigroup",
        "Seq",
        "TraitInvoke",
        "Traits",
        "Traversable"
    };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> InstanceModulesByTrait =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["Alternative"] = ["Alternative"],
            ["Applicative"] = ["Option", "Result", "Seq"],
            ["Foldable"] = ["Option", "Result", "Seq"],
            ["Functor"] = ["Option", "Result", "Seq"],
            ["Monad"] = ["Option", "Result", "Seq"],
            ["Traversable"] = ["Option", "Result", "Seq"]
        };

    private static readonly object InstanceHeadCacheGate = new();

    private static readonly Dictionary<string, IReadOnlyList<IndexedInstanceHead>> InstanceHeadCache =
        new(StringComparer.Ordinal);

    private sealed record IndexedInstanceHead(
        string Identity,
        PrecompiledInstanceHead Instance);

    public static IReadOnlyList<string> GetAvailableModulePaths() =>
        CoreModules.Order(StringComparer.Ordinal).ToArray();

    internal static bool IsCoreModuleName(string moduleName) => CoreModules.Contains(moduleName);

    public static bool TryGetSource(IReadOnlyList<string> effectiveModulePath, out string source)
    {
        source = string.Empty;
        return TryGetCoreModuleName(effectiveModulePath, out var moduleName) &&
               PrecompiledModuleRegistry.TryGetDistributionSource([WellKnownStrings.Std.Module, moduleName], out source);
    }

    public static bool TryGetSourceFilePath(IReadOnlyList<string> effectiveModulePath, out string filePath)
    {
        filePath = string.Empty;
        return TryGetCoreModuleName(effectiveModulePath, out var moduleName) &&
               PrecompiledModuleRegistry.TryGetDistributionSourceFilePath([WellKnownStrings.Std.Module, moduleName], out filePath);
    }

    public static bool TryGetModulePathFromSourcePath(string? path, out string[] effectiveModulePath)
    {
        effectiveModulePath = [];
        if (!PrecompiledModuleRegistry.TryGetModulePathFromSourcePath(path, out var distributionModulePath))
        {
            return false;
        }

        var segments = distributionModulePath.Split(
            WellKnownStrings.Operators.Divide,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length != 2 ||
            !string.Equals(segments[0], WellKnownStrings.Std.Module, StringComparison.OrdinalIgnoreCase) ||
            !CoreModules.Contains(segments[1]))
        {
            return false;
        }

        effectiveModulePath = [PackageAlias, segments[1]];
        return true;
    }

    public static string GetCoreImageFingerprint()
    {
        var builder = new System.Text.StringBuilder("prelude-core-image-v1:");
        foreach (var moduleName in CoreModules.Order(StringComparer.Ordinal))
        {
            if (PrecompiledModuleRegistry.TryGetDistributionSource([WellKnownStrings.Std.Module, moduleName], out var source))
            {
                builder.Append(moduleName)
                    .Append(':')
                    .AppendLine(ContentHash.ComputeHash(source));
            }
        }

        return ContentHash.ComputeHash(builder.ToString());
    }

    public static IReadOnlyList<string> GetInstanceCandidates(
        string traitName,
        TyCon typeConstructor)
    {
        var resolved = GetResolvedInstanceCandidates(traitName, typeConstructor);
        if (resolved.Count == 0)
        {
            return [];
        }

        var identities = new string[resolved.Count];
        for (var index = 0; index < resolved.Count; index++)
        {
            identities[index] = resolved[index].Identity;
        }

        return identities;
    }

    public static IReadOnlyList<PrecompiledInstanceCandidate> GetResolvedInstanceCandidates(
        string traitName,
        TyCon typeConstructor)
    {
        if (string.IsNullOrWhiteSpace(traitName) || string.IsNullOrWhiteSpace(typeConstructor.Name))
        {
            return [];
        }

        var instanceHeads = GetInstanceHeads(traitName);
        if (instanceHeads.Count == 0)
        {
            return [];
        }

        List<PrecompiledInstanceCandidate>? matches = null;
        foreach (var indexed in instanceHeads)
        {
            if (!TryMatchInstance(indexed.Instance, typeConstructor, out var requirements))
            {
                continue;
            }

            matches ??= new List<PrecompiledInstanceCandidate>();
            matches.Add(new PrecompiledInstanceCandidate(indexed.Identity, requirements));
        }

        return matches?.ToArray() ?? [];
    }

    public static bool TryResolveInstance(string traitName, TyCon typeConstructor, out string instanceIdentity)
    {
        var candidates = GetInstanceCandidates(traitName, typeConstructor);
        if (candidates.Count == 1)
        {
            instanceIdentity = candidates[0];
            return true;
        }

        instanceIdentity = string.Empty;
        return false;
    }

    public static bool TryDecomposeInstanceApplication(
        string traitName,
        TyCon appliedType,
        out PrecompiledContainerDecomposition decomposition)
    {
        var matches = new List<PrecompiledContainerDecomposition>();
        var instanceHeads = GetInstanceHeads(traitName);
        if (instanceHeads.Count == 0)
        {
            decomposition = null!;
            return false;
        }

        foreach (var indexed in instanceHeads)
        {
            var instance = indexed.Instance;
            var bindings = new Dictionary<int, EidosType>();
            EidosType? element = null;
            var elementArgumentIndex = -1;
            if (!TryMatchTypePattern(
                    instance.AppliedTarget,
                    appliedType,
                    bindings,
                    ref element,
                    ref elementArgumentIndex) ||
                element == null ||
                elementArgumentIndex < 0 ||
                !TryInstantiatePattern(instance.Target, bindings, out var constructorType) ||
                constructorType is not TyCon typeConstructor ||
                !TryResolveRequirements(instance, bindings, out var requirements))
            {
                continue;
            }

            matches.Add(new PrecompiledContainerDecomposition(
                typeConstructor,
                appliedType,
                element,
                elementArgumentIndex,
                new PrecompiledInstanceCandidate(indexed.Identity, requirements)));
        }

        if (matches.Count == 1)
        {
            decomposition = matches[0];
            return true;
        }

        decomposition = null!;
        return false;
    }

    internal static bool HasPotentialInstanceCandidate(string traitName, TyCon typeConstructor)
    {
        foreach (var indexed in GetInstanceHeads(traitName))
        {
            var target = indexed.Instance.Target;
            if (target.Kind != PrecompiledTypePatternKind.Constructor ||
                (string.Equals(target.Name, typeConstructor.Name, StringComparison.Ordinal) &&
                 target.Arguments.Count == typeConstructor.Args.Count))
            {
                return true;
            }
        }

        return false;
    }

    internal static IReadOnlyList<string> ValidateInstanceModuleIndex()
    {
        var actual = new HashSet<string>(StringComparer.Ordinal);
        foreach (var moduleName in CoreModules.Order(StringComparer.Ordinal))
        {
            foreach (var instance in PrecompiledModuleRegistry
                         .GetExports($"{WellKnownStrings.Std.Module}/{moduleName}")
                         .Instances)
            {
                actual.Add($"{instance.TraitName}:{moduleName}");
            }
        }

        var indexed = InstanceModulesByTrait
            .SelectMany(static pair => pair.Value.Select(moduleName => $"{pair.Key}:{moduleName}"))
            .ToHashSet(StringComparer.Ordinal);
        return actual
            .Except(indexed, StringComparer.Ordinal)
            .Select(static entry => $"missing {entry}")
            .Concat(indexed
                .Except(actual, StringComparer.Ordinal)
                .Select(static entry => $"stale {entry}"))
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<IndexedInstanceHead> GetInstanceHeads(string traitName)
    {
        if (!InstanceModulesByTrait.TryGetValue(traitName, out var moduleNames))
        {
            return [];
        }

        lock (InstanceHeadCacheGate)
        {
            if (InstanceHeadCache.TryGetValue(traitName, out var cached))
            {
                return cached;
            }

            var identities = new HashSet<string>(StringComparer.Ordinal);
            var instances = new List<IndexedInstanceHead>();
            foreach (var moduleName in moduleNames)
            {
                foreach (var instance in PrecompiledModuleRegistry
                             .GetExports($"{WellKnownStrings.Std.Module}/{moduleName}")
                             .Instances)
                {
                    if (!string.Equals(instance.TraitName, traitName, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var identity = $"{PackageAlias}.{moduleName}.{instance.Name}";
                    if (identities.Add(identity))
                    {
                        instances.Add(new IndexedInstanceHead(identity, instance));
                    }
                }
            }

            instances.Sort(static (left, right) =>
                StringComparer.Ordinal.Compare(left.Identity, right.Identity));
            cached = instances.ToArray();
            InstanceHeadCache.Add(traitName, cached);
            return cached;
        }
    }

    private static bool TryMatchInstance(
        PrecompiledInstanceHead instance,
        EidosType type,
        out IReadOnlyList<PrecompiledResolvedRequirement> requirements)
    {
        var bindings = new Dictionary<int, EidosType>();
        EidosType? element = null;
        var elementArgumentIndex = -1;
        if (!TryMatchTypePattern(
                instance.Target,
                type,
                bindings,
                ref element,
                ref elementArgumentIndex))
        {
            requirements = [];
            return false;
        }

        return TryResolveRequirements(instance, bindings, out requirements);
    }

    private static bool TryResolveRequirements(
        PrecompiledInstanceHead instance,
        IReadOnlyDictionary<int, EidosType> bindings,
        out IReadOnlyList<PrecompiledResolvedRequirement> requirements)
    {
        var resolved = new List<PrecompiledResolvedRequirement>(instance.Requirements.Count);
        foreach (var requirement in instance.Requirements)
        {
            if (!bindings.TryGetValue(requirement.ParameterIndex, out var requiredType))
            {
                requirements = [];
                return false;
            }

            var traitArguments = new List<EidosType>(requirement.TraitArguments.Count);
            foreach (var argument in requirement.TraitArguments)
            {
                if (!TryInstantiatePattern(argument, bindings, out var instantiated))
                {
                    requirements = [];
                    return false;
                }

                traitArguments.Add(instantiated);
            }

            resolved.Add(new PrecompiledResolvedRequirement(
                requiredType,
                requirement.TraitName,
                traitArguments));
        }

        requirements = resolved;
        return true;
    }

    private static bool TryMatchTypePattern(
        PrecompiledTypePattern pattern,
        EidosType type,
        Dictionary<int, EidosType> bindings,
        ref EidosType? element,
        ref int elementArgumentIndex,
        int topLevelArgumentIndex = -1)
    {
        switch (pattern.Kind)
        {
            case PrecompiledTypePatternKind.Wildcard:
                return true;
            case PrecompiledTypePatternKind.Element:
                if (element != null &&
                    !string.Equals(TypeIdentity(element), TypeIdentity(type), StringComparison.Ordinal))
                {
                    return false;
                }

                element = type;
                elementArgumentIndex = topLevelArgumentIndex;
                return true;
            case PrecompiledTypePatternKind.Parameter:
                if (bindings.TryGetValue(pattern.ParameterIndex, out var existing))
                {
                    return string.Equals(TypeIdentity(existing), TypeIdentity(type), StringComparison.Ordinal);
                }

                bindings[pattern.ParameterIndex] = type;
                return true;
            case PrecompiledTypePatternKind.Constructor:
                if (type is not TyCon constructor ||
                    !string.Equals(pattern.Name, constructor.Name, StringComparison.Ordinal) ||
                    pattern.Arguments.Count != constructor.Args.Count)
                {
                    return false;
                }

                for (var index = 0; index < pattern.Arguments.Count; index++)
                {
                    if (!TryMatchTypePattern(
                            pattern.Arguments[index],
                            constructor.Args[index],
                            bindings,
                            ref element,
                            ref elementArgumentIndex,
                            topLevelArgumentIndex >= 0 ? topLevelArgumentIndex : index))
                    {
                        return false;
                    }
                }

                return true;
            case PrecompiledTypePatternKind.Tuple:
                if (type is not TyTuple tuple || pattern.Arguments.Count != tuple.Elements.Count)
                {
                    return false;
                }

                for (var index = 0; index < pattern.Arguments.Count; index++)
                {
                    if (!TryMatchTypePattern(
                            pattern.Arguments[index],
                            tuple.Elements[index],
                            bindings,
                            ref element,
                            ref elementArgumentIndex,
                            topLevelArgumentIndex))
                    {
                        return false;
                    }
                }

                return true;
            case PrecompiledTypePatternKind.Function:
                return type is TyFun function &&
                       function.Params.Count == 1 &&
                       pattern.Arguments.Count == 2 &&
                       TryMatchTypePattern(
                           pattern.Arguments[0],
                           function.Params[0],
                           bindings,
                           ref element,
                           ref elementArgumentIndex,
                           topLevelArgumentIndex) &&
                       TryMatchTypePattern(
                           pattern.Arguments[1],
                           function.Result,
                           bindings,
                           ref element,
                           ref elementArgumentIndex,
                           topLevelArgumentIndex);
            default:
                return false;
        }
    }

    private static bool TryInstantiatePattern(
        PrecompiledTypePattern pattern,
        IReadOnlyDictionary<int, EidosType> bindings,
        out EidosType type)
    {
        switch (pattern.Kind)
        {
            case PrecompiledTypePatternKind.Parameter when bindings.TryGetValue(pattern.ParameterIndex, out type!):
                return true;
            case PrecompiledTypePatternKind.Constructor:
            {
                var arguments = new List<EidosType>(pattern.Arguments.Count);
                foreach (var argument in pattern.Arguments)
                {
                    if (!TryInstantiatePattern(argument, bindings, out var instantiated))
                    {
                        type = null!;
                        return false;
                    }

                    arguments.Add(instantiated);
                }

                type = new TyCon { Name = pattern.Name, Args = arguments };
                return true;
            }
            case PrecompiledTypePatternKind.Tuple:
            {
                var elements = new List<EidosType>(pattern.Arguments.Count);
                foreach (var element in pattern.Arguments)
                {
                    if (!TryInstantiatePattern(element, bindings, out var instantiated))
                    {
                        type = null!;
                        return false;
                    }

                    elements.Add(instantiated);
                }

                type = new TyTuple { Elements = elements };
                return true;
            }
            default:
                type = null!;
                return false;
        }
    }

    private static string TypeIdentity(EidosType type) => type switch
    {
        TyCon constructor => $"{constructor.Name}[{string.Join(",", constructor.Args.Select(TypeIdentity))}]",
        TyTuple tuple => $"({string.Join(",", tuple.Elements.Select(TypeIdentity))})",
        TyFun function => $"fn({string.Join(",", function.Params.Select(TypeIdentity))})->{TypeIdentity(function.Result)}",
        TyVar variable => $"?{variable.Index}",
        TyRef reference => $"ref({TypeIdentity(reference.Inner)})",
        TyMutRef reference => $"mref({TypeIdentity(reference.Inner)})",
        TyShared shared => $"shared({TypeIdentity(shared.Inner)})",
        _ => type.ToString() ?? type.GetType().Name
    };

    private static bool TryGetCoreModuleName(IReadOnlyList<string> effectiveModulePath, out string moduleName)
    {
        moduleName = string.Empty;
        if (effectiveModulePath.Count != 2 ||
            !string.Equals(effectiveModulePath[0], PackageAlias, StringComparison.Ordinal))
        {
            return false;
        }

        moduleName = effectiveModulePath[1];
        return CoreModules.Contains(moduleName);
    }
}
