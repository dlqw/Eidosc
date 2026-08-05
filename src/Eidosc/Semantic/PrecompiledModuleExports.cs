using Eidosc.Symbols;

namespace Eidosc.Semantic;

public sealed record PrecompiledInstanceHead(
    string Name,
    string TraitName,
    PrecompiledTypePattern Target,
    PrecompiledTypePattern AppliedTarget,
    IReadOnlyList<PrecompiledInstanceRequirement> Requirements);

public enum PrecompiledTypePatternKind
{
    Parameter,
    Constructor,
    Tuple,
    Function,
    Element,
    Wildcard
}

public sealed record PrecompiledTypePattern(
    PrecompiledTypePatternKind Kind,
    string Name,
    int ParameterIndex,
    IReadOnlyList<PrecompiledTypePattern> Arguments,
    IReadOnlyList<string> ModulePath,
    string? PackageAlias);

public sealed record PrecompiledInstanceRequirement(
    int ParameterIndex,
    string TraitName,
    IReadOnlyList<string> TraitModulePath,
    IReadOnlyList<PrecompiledTypePattern> TraitArguments);

public sealed record PrecompiledResolvedRequirement(
    Eidosc.Types.Type Type,
    string TraitName,
    IReadOnlyList<Eidosc.Types.Type> TraitArguments);

public sealed record PrecompiledInstanceCandidate(
    string Identity,
    IReadOnlyList<PrecompiledResolvedRequirement> Requirements);

public sealed record PrecompiledContainerDecomposition(
    Eidosc.Types.TyCon TypeConstructor,
    Eidosc.Types.TyCon AppliedType,
    Eidosc.Types.Type ElementType,
    int ElementTypeArgumentIndex,
    PrecompiledInstanceCandidate Candidate);

public sealed record PrecompiledModuleExports(
    IReadOnlyList<string> Functions,
    IReadOnlyList<string> Types,
    IReadOnlyList<string> Traits,
    IReadOnlyList<string> Effects,
    IReadOnlyList<string> Constructors)
{
    public IReadOnlyList<string> Values { get; init; } = Array.Empty<string>();

    public IReadOnlyDictionary<string, string> Modules { get; init; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    public IReadOnlyList<PrecompiledInstanceHead> Instances { get; init; } = [];

    public static PrecompiledModuleExports Empty { get; } =
        new(Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    // C5: Lazy-cached HashSet for O(1) name lookup instead of 7× O(N) linear scans
    private HashSet<string>? _allExportNames;
    public bool ContainsName(string name)
    {
        if (_allExportNames == null)
        {
            var set = new HashSet<string>(StringComparer.Ordinal);
            AddRange(set, Values);
            AddRange(set, Functions);
            AddRange(set, Types);
            AddRange(set, Traits);
            AddRange(set, Effects);
            AddRange(set, Constructors);
            foreach (var key in Modules.Keys)
            {
                set.Add(key);
            }
            _allExportNames = set;
        }
        return _allExportNames.Contains(name);
    }

    private static void AddRange(HashSet<string> set, IReadOnlyList<string> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            set.Add(list[i]);
        }
    }
}
