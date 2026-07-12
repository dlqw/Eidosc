using System.Collections.Immutable;
using System.ComponentModel;

namespace Eidosc.Symbols;

/// <summary>
/// Identifies a trait implementation bucket by trait, implementing type, and structured trait arguments.
/// </summary>
public readonly record struct ImplLookupKey(
    SymbolId TraitId,
    TypeId TypeId,
    ImmutableArray<ImplTypeRefKey> TraitTypeArgs) : IEquatable<ImplLookupKey>
{
    public bool Equals(ImplLookupKey other)
    {
        if (TraitId != other.TraitId || TypeId != other.TypeId)
        {
            return false;
        }

        if (TraitTypeArgs.IsDefaultOrEmpty && other.TraitTypeArgs.IsDefaultOrEmpty)
        {
            return true;
        }

        if (TraitTypeArgs.IsDefault || other.TraitTypeArgs.IsDefault || TraitTypeArgs.Length != other.TraitTypeArgs.Length)
        {
            return false;
        }

        for (var i = 0; i < TraitTypeArgs.Length; i++)
        {
            if (!TraitTypeArgs[i].Equals(other.TraitTypeArgs[i]))
            {
                return false;
            }
        }

        return true;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(TraitId);
        hash.Add(TypeId);
        if (!TraitTypeArgs.IsDefaultOrEmpty)
        {
            foreach (var arg in TraitTypeArgs)
            {
                hash.Add(arg);
            }
        }
        return hash.ToHashCode();
    }
}

public sealed partial class SymbolTable
{
    private string GetTypeName(TypeId typeId)
    {
        return typeId.Value switch
        {
            WellKnownTypeIds.IntId => WellKnownStrings.BuiltinTypes.Int,
            WellKnownTypeIds.FloatId => WellKnownStrings.BuiltinTypes.Float,
            WellKnownTypeIds.BoolId => WellKnownStrings.BuiltinTypes.Bool,
            WellKnownTypeIds.StringId => WellKnownStrings.BuiltinTypes.String,
            WellKnownTypeIds.CharId => WellKnownStrings.BuiltinTypes.Char,
            WellKnownTypeIds.UnitId => WellKnownStrings.BuiltinTypes.Unit,
            WellKnownTypeIds.ErasedCallableId => "ErasedCallable",
            WellKnownTypeIds.NeverId => WellKnownStrings.BuiltinTypes.Never,
            _ => ResolveNamedTypeName(typeId)
        };
    }

    private string ResolveNamedTypeName(TypeId typeId)
    {
        return GetSymbolByTypeId(typeId)?.Name ?? $"type:{typeId.Value}";
    }

    private string InferCanonicalImplementingType(TypeId implementingType)
    {
        if (TryBuildCanonicalTypeHeadFromSymbol(implementingType, out var canonicalTypeHead))
        {
            return canonicalTypeHead;
        }

        return implementingType.IsValid ? $"type:{implementingType.Value}" : "";
    }

    private ImplTypeRefKey BuildDefaultImplementingTypeKey(TypeId implementingType)
    {
        if (!implementingType.IsValid)
        {
            return ImplTypeRefKey.Empty;
        }

        var builtinName = ImplLookupCanonicalizer.ResolveBuiltinCanonicalTypeName(implementingType);
        if (!string.IsNullOrWhiteSpace(builtinName))
        {
            return new ImplTypeRefKey(SymbolId.None, implementingType, builtinName, []);
        }

        var symbol = GetSymbolByTypeId(implementingType);
        return symbol is not null
            ? new ImplTypeRefKey(symbol.Id, implementingType, symbol.Name, [])
            : new ImplTypeRefKey(SymbolId.None, implementingType, $"type:{implementingType.Value}", []);
    }

    private static bool ShouldBuildDefaultImplementingTypeKey(
        string canonicalImplementingType,
        string implementingTypeDisplay)
    {
        var text = !string.IsNullOrWhiteSpace(canonicalImplementingType)
            ? canonicalImplementingType
            : implementingTypeDisplay;
        return !string.IsNullOrWhiteSpace(text) &&
               text.IndexOf('[') < 0 &&
               text.IndexOf('(') < 0 &&
               text.IndexOf('-') < 0;
    }

    private ImplTypeShapeNode? BuildImplementingTypeShapeFromKeyOrText(
        ImplTypeRefKey implementingTypeKey,
        string canonicalImplementingType,
        TypeId implementingType)
    {
        return !implementingTypeKey.IsEmpty
            ? BuildImplTypeShapeNode(implementingTypeKey)
            : BuildDefaultImplementingTypeShape(canonicalImplementingType, implementingType);
    }

    private ImplTypeShapeNode? BuildDefaultImplementingTypeShape(
        string canonicalImplementingType,
        TypeId implementingType)
    {
        if (!implementingType.IsValid)
        {
            return null;
        }

        return new ImplConstructorShapeNode($"type:{implementingType.Value}", [])
        {
            SymbolId = ResolveTypeSymbolId(implementingType),
            TypeId = implementingType
        };
    }

    private List<ImplTypeShapeNode> BuildTraitArgShapesFromKeysOrText(
        IReadOnlyList<ImplTypeRefKey> traitTypeArgKeys,
        IReadOnlyList<ImplTypeRefKey> explicitCanonicalTraitTypeArgKeys,
        IReadOnlyList<string> canonicalTraitTypeArgs)
    {
        if (explicitCanonicalTraitTypeArgKeys.Any(static key => !key.IsEmpty))
        {
            return explicitCanonicalTraitTypeArgKeys.Select(BuildImplTypeShapeNode).ToList();
        }

        if (traitTypeArgKeys.Any(static key => !key.IsEmpty))
        {
            return traitTypeArgKeys.Select(BuildImplTypeShapeNode).ToList();
        }

        return BuildDefaultTraitArgShapes(canonicalTraitTypeArgs);
    }

    private List<ImplTypeShapeNode> BuildDefaultTraitArgShapes(IReadOnlyList<string> canonicalTraitTypeArgs)
    {
        if (canonicalTraitTypeArgs.Count == 0)
        {
            return [];
        }

        return canonicalTraitTypeArgs
            .Where(static text => !string.IsNullOrWhiteSpace(text))
            .Select(text => AttachKnownConstructorIdentities(ImplSpecializationComparer.ParseCanonicalShape(text)))
            .ToList();
    }

    private ImplTypeShapeNode AttachKnownConstructorIdentities(ImplTypeShapeNode shape)
    {
        return shape switch
        {
            ImplConstructorShapeNode constructor => AttachKnownConstructorIdentity(constructor),
            ImplTupleShapeNode tuple => new ImplTupleShapeNode(tuple.Elements.Select(AttachKnownConstructorIdentities).ToList()),
            ImplArrowShapeNode arrow => new ImplArrowShapeNode(
                AttachKnownConstructorIdentities(arrow.ParamType),
                AttachKnownConstructorIdentities(arrow.ReturnType)),
            ImplEffectfulShapeNode effectful => new ImplEffectfulShapeNode(
                AttachKnownConstructorIdentities(effectful.InputType),
                effectful.EffectPaths,
                effectful.OutputType == null ? null : AttachKnownConstructorIdentities(effectful.OutputType)),
            _ => shape
        };
    }

    private ImplConstructorShapeNode AttachKnownConstructorIdentity(ImplConstructorShapeNode constructor)
    {
        var args = constructor.Args.Select(AttachKnownConstructorIdentities).ToList();
        var symbolId = SymbolId.None;
        var typeId = TypeId.None;

        if (TryResolveTypeIdentity(constructor.Name, out var resolvedSymbolId, out var resolvedTypeId))
        {
            symbolId = resolvedSymbolId;
            typeId = resolvedTypeId;
        }

        return new ImplConstructorShapeNode(constructor.Name, args)
        {
            SymbolId = symbolId,
            TypeId = typeId
        };
    }

    private bool TryResolveTypeIdentity(string name, out SymbolId symbolId, out TypeId typeId)
    {
        symbolId = SymbolId.None;
        typeId = ResolveBuiltinTypeId(name);
        if (typeId.IsValid)
        {
            return true;
        }

        var typeSymbolId = LookupType(name);
        if (typeSymbolId is not { IsValid: true } resolvedSymbolId ||
            GetSymbol(resolvedSymbolId) is not { TypeId: { IsValid: true } resolvedTypeId })
        {
            return false;
        }

        symbolId = resolvedSymbolId;
        typeId = resolvedTypeId;
        return true;
    }

    private SymbolId ResolveTypeSymbolId(TypeId typeId)
    {
        return GetSymbolByTypeId(typeId)?.Id ?? SymbolId.None;
    }

    private static TypeId ResolveBuiltinTypeId(string name)
    {
        return name switch
        {
            WellKnownStrings.BuiltinTypes.Int => new TypeId(WellKnownTypeIds.IntId),
            WellKnownStrings.BuiltinTypes.Float => new TypeId(WellKnownTypeIds.FloatId),
            WellKnownStrings.BuiltinTypes.Bool => new TypeId(WellKnownTypeIds.BoolId),
            WellKnownStrings.BuiltinTypes.String => new TypeId(WellKnownTypeIds.StringId),
            WellKnownStrings.BuiltinTypes.Char => new TypeId(WellKnownTypeIds.CharId),
            WellKnownStrings.BuiltinTypes.Unit => new TypeId(WellKnownTypeIds.UnitId),
            WellKnownStrings.BuiltinTypes.Never => new TypeId(WellKnownTypeIds.NeverId),
            _ => TypeId.None
        };
    }

    private bool TryBuildCanonicalTypeHeadFromSymbol(TypeId implementingType, out string canonicalTypeHead)
    {
        canonicalTypeHead = string.Empty;
        var symbol = GetSymbolByTypeId(implementingType);
        if (symbol is not AdtSymbol and not TraitSymbol and not EffectSymbol)
        {
            return false;
        }

        var typeParamNames = GetTypeParameterNames(symbol);
        canonicalTypeHead = typeParamNames.Count == 0
            ? symbol.Name
            : $"{symbol.Name}[{string.Join(",", typeParamNames)}]";
        return !string.IsNullOrWhiteSpace(canonicalTypeHead);
    }

    private IReadOnlyList<string> GetTypeParameterNames(Symbol symbol)
    {
        return symbol switch
        {
            AdtSymbol adt => ResolveTypeParameterNames(adt.TypeParams),
            TraitSymbol trait => ResolveTypeParameterNames(trait.TypeParams),
            EffectSymbol => [],
            _ => []
        };
    }

    private List<string> ResolveTypeParameterNames(IReadOnlyList<SymbolId> typeParams)
    {
        if (typeParams.Count == 0)
        {
            return [];
        }

        var names = new List<string>(typeParams.Count);
        foreach (var typeParamId in typeParams)
        {
            if (GetSymbol(typeParamId) is { Name.Length: > 0 } typeParamSymbol)
            {
                names.Add(typeParamSymbol.Name);
            }
        }

        return names;
    }

    private static string[] NormalizeAndValidateTraitTypeArgs(IReadOnlyList<string>? traitTypeArgs)
    {
        if (traitTypeArgs == null || traitTypeArgs.Count == 0)
        {
            return Array.Empty<string>();
        }

        var normalized = new List<string>(traitTypeArgs.Count);
        foreach (var traitTypeArg in traitTypeArgs)
        {
            var text = RemoveInsignificantTypeWhitespace(traitTypeArg);
            if (!string.IsNullOrWhiteSpace(text))
            {
                normalized.Add(text);
            }
        }

        return normalized.ToArray();
    }

    private ImmutableArray<ImplTypeRefKey> NormalizeTraitTypeArgKeys(
        IReadOnlyList<ImplTypeRefKey>? traitTypeArgKeys,
        IReadOnlyList<string>? fallbackTraitTypeArgs)
    {
        if (traitTypeArgKeys is { Count: > 0 })
        {
            return traitTypeArgKeys
                .Where(static key => !key.IsEmpty)
                .ToImmutableArray();
        }

        var normalizedTexts = NormalizeAndValidateTraitTypeArgs(fallbackTraitTypeArgs);
        return normalizedTexts.Length == 0
            ? ImmutableArray<ImplTypeRefKey>.Empty
            : normalizedTexts.Select(BuildTypeRefKeyFromCanonicalText).ToImmutableArray();
    }

    private static string RemoveInsignificantTypeWhitespace(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (!char.IsWhiteSpace(ch))
            {
                builder.Append(ch);
            }
        }

        return builder.ToString();
    }

    private List<ImplTypeArgTraitRequirement> NormalizeImplementingTypeRequirements(
        IReadOnlyList<ImplTypeArgTraitRequirement>? implementingTypeRequirements)
    {
        if (implementingTypeRequirements == null || implementingTypeRequirements.Count == 0)
        {
            return [];
        }

        var normalized = new List<ImplTypeArgTraitRequirement>(implementingTypeRequirements.Count);
        foreach (var requirement in implementingTypeRequirements)
        {
            var normalizedTraitTypeArgs = NormalizeAndValidateTraitTypeArgs(requirement.TraitTypeArgs);
            normalized.Add(requirement with
            {
                TraitTypeArgs = normalizedTraitTypeArgs.ToList(),
                TraitTypeArgKeys = NormalizeTraitTypeArgKeys(
                    requirement.TraitTypeArgKeys,
                    normalizedTraitTypeArgs).ToList()
            });
        }

        return normalized;
    }

    private ImplTypeRefKey BuildTypeRefKeyFromCanonicalText(string text)
    {
        var trimmed = text.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return ImplTypeRefKey.Empty;
        }

        var bracketIndex = trimmed.IndexOf('[');
        if (bracketIndex <= 0 || !trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            return BuildSimpleTypeRefKeyFromName(trimmed);
        }

        var head = trimmed[..bracketIndex];
        var payload = trimmed.Substring(bracketIndex + 1, trimmed.Length - bracketIndex - 2);
        var typeArguments = SplitTopLevelCommaSeparated(payload)
            .Select(BuildTypeRefKeyFromCanonicalText)
            .ToImmutableArray();
        var headKey = BuildSimpleTypeRefKeyFromName(head);
        return new ImplTypeRefKey(headKey.SymbolId, headKey.TypeId, headKey.Text, typeArguments);
    }

    private ImplTypeRefKey BuildSimpleTypeRefKeyFromName(string name)
    {
        if (TryResolveTypeIdentity(name, out var symbolId, out var typeId))
        {
            return new ImplTypeRefKey(symbolId, typeId, name, []);
        }

        return ImplTypeRefKey.FromText(name);
    }

    private static List<string> SplitTopLevelCommaSeparated(string text)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '[':
                case '(':
                case '{':
                    depth++;
                    break;
                case ']':
                case ')':
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    AddPart(text, start, i, result);
                    start = i + 1;
                    break;
            }
        }

        AddPart(text, start, text.Length, result);
        return result;
    }

    private static void AddPart(string text, int start, int end, List<string> result)
    {
        var part = text[start..end].Trim();
        if (part.Length > 0)
        {
            result.Add(part);
        }
    }

    public List<ImplSymbol> LookupImpls(TypeId type)
    {
        if (!type.IsValid)
        {
            return [];
        }

        var result = new List<ImplSymbol>();
        foreach (var (key, impls) in _impls)
        {
            if (key.TypeId.Equals(type))
            {
                result.AddRange(impls);
            }
        }

        return result;
    }

    /// <summary>
    /// Legacy text-based impl lookup kept for compatibility tests and old hand-written fixtures.
    /// Production callers should prefer <see cref="LookupImplForTraitByKeys(TypeId, SymbolId, ImplTypeRefKey, IReadOnlyList{ImplTypeRefKey}?)" />.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public List<ImplSymbol> LookupImplCandidatesForTrait(
        TypeId type,
        SymbolId trait,
        IReadOnlyList<string>? traitTypeArgs = null)
    {
        if (!type.IsValid || !trait.IsValid)
        {
            return [];
        }

        var normalizedTraitTypeArgs = NormalizeAndValidateTraitTypeArgs(traitTypeArgs);
        var key = new ImplLookupKey(
            trait,
            type,
            NormalizeTraitTypeArgKeys(traitTypeArgKeys: null, normalizedTraitTypeArgs));
        if (_impls.TryGetValue(key, out var candidates))
        {
            return [.. candidates];
        }

        if (normalizedTraitTypeArgs.Length == 0)
        {
            return [];
        }

        return LookupAllImplCandidatesForTrait(type, trait)
            .Where(candidate => CandidateMatchesRequestedTraitTypeArgs(candidate, normalizedTraitTypeArgs))
            .ToList();
    }

    /// <summary>
    /// Compatibility candidate enumeration that cannot validate a requested implementing head.
    /// Production callers should prefer <see cref="LookupImplForTraitByKeys(TypeId, SymbolId, ImplTypeRefKey, IReadOnlyList{ImplTypeRefKey}?)" />.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public List<ImplSymbol> LookupImplCandidatesForTraitByKeys(
        TypeId type,
        SymbolId trait,
        IReadOnlyList<ImplTypeRefKey>? traitTypeArgKeys)
    {
        if (!type.IsValid || !trait.IsValid)
        {
            return [];
        }

        var normalizedTraitTypeArgKeys = NormalizeTraitTypeArgKeys(traitTypeArgKeys, fallbackTraitTypeArgs: null);
        var key = new ImplLookupKey(trait, type, normalizedTraitTypeArgKeys);
        if (_impls.TryGetValue(key, out var candidates))
        {
            return [.. candidates];
        }

        if (normalizedTraitTypeArgKeys.IsDefaultOrEmpty)
        {
            return [];
        }

        return LookupAllImplCandidatesForTrait(type, trait)
            .Where(candidate => CandidateMatchesRequestedTraitTypeArgKeys(candidate, normalizedTraitTypeArgKeys))
            .ToList();
    }

    /// <summary>
    /// Legacy impl lookup that cannot validate a requested implementing head.
    /// Production callers should prefer <see cref="LookupImplForTraitByKeys(TypeId, SymbolId, ImplTypeRefKey, IReadOnlyList{ImplTypeRefKey}?)" />.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ImplSymbol? LookupImplForTrait(
        TypeId type,
        SymbolId trait,
        IReadOnlyList<string>? traitTypeArgs = null)
    {
        if (!type.IsValid || !trait.IsValid)
        {
            return null;
        }

        var normalizedTraitTypeArgs = NormalizeAndValidateTraitTypeArgs(traitTypeArgs);
        var candidates = LookupImplCandidatesForTrait(type, trait, normalizedTraitTypeArgs);
        if (candidates.Count == 0 && normalizedTraitTypeArgs.Length == 0)
        {
            candidates = LookupImplCandidatesForTrait(type, trait, []);
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.Count == 1
            ? candidates[0]
            : null;
    }

    /// <summary>
    /// Compatibility impl lookup that cannot validate a requested implementing head.
    /// Production callers should prefer <see cref="LookupImplForTraitByKeys(TypeId, SymbolId, ImplTypeRefKey, IReadOnlyList{ImplTypeRefKey}?)" />.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ImplSymbol? LookupImplForTraitByKeys(
        TypeId type,
        SymbolId trait,
        IReadOnlyList<ImplTypeRefKey>? traitTypeArgKeys)
    {
        if (!type.IsValid || !trait.IsValid)
        {
            return null;
        }

        var candidates = LookupImplCandidatesForTraitByKeys(type, trait, traitTypeArgKeys);
        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates.Count == 1
            ? candidates[0]
            : TryChooseMostSpecificImpl(candidates);
    }

    /// <summary>
    /// Looks up a single applicable implementation using structured implementing type and trait type argument keys.
    /// </summary>
    public ImplSymbol? LookupImplForTraitByKeys(
        TypeId type,
        SymbolId trait,
        ImplTypeRefKey requestedImplementingTypeKey,
        IReadOnlyList<ImplTypeRefKey>? traitTypeArgKeys)
    {
        if (!type.IsValid || !trait.IsValid)
        {
            return null;
        }

        var requestedTraitTypeArgKeys = NormalizeTraitTypeArgKeys(traitTypeArgKeys, fallbackTraitTypeArgs: null);
        var candidates = LookupImplCandidatesForTraitByKeys(type, trait, requestedTraitTypeArgKeys);
        if (candidates.Count == 0)
        {
            return null;
        }

        var applicable = new List<ImplSymbol>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (!CandidateCanSatisfyStructuredRequest(
                    candidate,
                    requestedImplementingTypeKey,
                    requestedTraitTypeArgKeys))
            {
                continue;
            }

            var requestedImplementingShape = BuildRequestedImplementingShapeForCandidate(
                requestedImplementingTypeKey,
                candidate);
            var requestedHead = requestedTraitTypeArgKeys.IsDefaultOrEmpty
                ? null
                : new ImplHeadShape(
                    trait,
                    BuildRequestedTraitArgShapesForCandidate(requestedTraitTypeArgKeys, candidate),
                    requestedImplementingShape);
            var isApplicable = requestedHead == null
                ? ImplSpecializationComparer.IsApplicableTo(
                    requestedImplementingShape,
                    ParseImplImplementingShape(candidate))
                : ImplSpecializationComparer.IsApplicableTo(
                    requestedHead,
                    BuildImplHeadShape(candidate));
            if (isApplicable)
            {
                applicable.Add(candidate);
            }
        }

        if (applicable.Count == 0)
        {
            return null;
        }

        return applicable.Count == 1
            ? applicable[0]
            : TryChooseMostSpecificImpl(applicable);
    }

    private static bool CandidateCanSatisfyStructuredRequest(
        ImplSymbol candidate,
        ImplTypeRefKey requestedImplementingTypeKey,
        IReadOnlyList<ImplTypeRefKey> requestedTraitTypeArgKeys)
    {
        if (requestedImplementingTypeKey.HasStructuredIdentity() &&
            !CandidateHasStructuredImplementingIdentity(candidate))
        {
            return false;
        }

        if (requestedTraitTypeArgKeys.Count == 0)
        {
            return true;
        }

        var candidateTraitTypeArgKeys = GetCandidateTraitTypeArgKeysForMatching(candidate);
        for (var i = 0; i < requestedTraitTypeArgKeys.Count; i++)
        {
            if (!requestedTraitTypeArgKeys[i].HasStructuredIdentity())
            {
                continue;
            }

            if (i < candidateTraitTypeArgKeys.Count &&
                candidateTraitTypeArgKeys[i].HasStructuredIdentity())
            {
                continue;
            }

            if (i < candidate.TraitTypeArgShapes.Count &&
                ShapeHasStructuredConstructorIdentity(candidate.TraitTypeArgShapes[i]))
            {
                continue;
            }

            return false;
        }

        return true;
    }

    private static bool CandidateHasStructuredImplementingIdentity(ImplSymbol candidate)
    {
        return (!candidate.ImplementingTypeKey.IsEmpty &&
                candidate.ImplementingTypeKey.HasStructuredIdentity()) ||
               (candidate.ImplementingTypeShape != null &&
                ShapeHasStructuredConstructorIdentity(candidate.ImplementingTypeShape));
    }

    private static bool ShapeHasStructuredConstructorIdentity(ImplTypeShapeNode shape)
    {
        return shape switch
        {
            ImplConstructorShapeNode constructor => constructor.SymbolId.IsValid || constructor.TypeId.IsValid,
            ImplTupleShapeNode tuple => tuple.Elements.All(ShapeHasStructuredConstructorIdentity),
            ImplArrowShapeNode arrow => ShapeHasStructuredConstructorIdentity(arrow.ParamType) &&
                                        ShapeHasStructuredConstructorIdentity(arrow.ReturnType),
            ImplEffectfulShapeNode effectful => ShapeHasStructuredConstructorIdentity(effectful.InputType) &&
                                                (effectful.OutputType == null ||
                                                 ShapeHasStructuredConstructorIdentity(effectful.OutputType)),
            _ => false
        };
    }

    /// <summary>
    /// Legacy text-based impl lookup kept only for old tests and compatibility fixtures.
    /// Production callers should construct an <see cref="ImplTypeRefKey" /> and use <see cref="LookupImplForTraitByKeys(TypeId, SymbolId, ImplTypeRefKey, IReadOnlyList{ImplTypeRefKey}?)" />.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public ImplSymbol? LookupImplForTrait(
        TypeId type,
        SymbolId trait,
        string requestedCanonicalImplementingType,
        IReadOnlyList<string>? traitTypeArgs = null)
    {
        if (!type.IsValid || !trait.IsValid)
        {
            return null;
        }

        var requestedTraitTypeArgs = traitTypeArgs == null
            ? null
            : NormalizeAndValidateTraitTypeArgs(traitTypeArgs);
        var candidates = requestedTraitTypeArgs == null
            ? LookupAllImplCandidatesForTrait(type, trait)
            : LookupImplCandidatesForTrait(type, trait, requestedTraitTypeArgs);
        if (candidates.Count == 0)
        {
            return null;
        }

        var requestedImplementingShape = ImplSpecializationComparer.ParseCanonicalShape(requestedCanonicalImplementingType);
        var requestedHead = requestedTraitTypeArgs == null
            ? null
            : new ImplHeadShape(
                trait,
                requestedTraitTypeArgs.Select(ImplSpecializationComparer.ParseCanonicalShape).ToList(),
                requestedImplementingShape);
        var applicable = new List<ImplSymbol>(candidates.Count);
        foreach (var candidate in candidates)
        {
            if (requestedTraitTypeArgs != null &&
                !CandidateMatchesRequestedTraitTypeArgs(candidate, requestedTraitTypeArgs))
            {
                continue;
            }

            var isApplicable = requestedHead == null
                ? ImplSpecializationComparer.IsApplicableTo(
                    requestedImplementingShape,
                    ParseImplImplementingShape(candidate))
                : ImplSpecializationComparer.IsApplicableTo(
                    requestedHead,
                    BuildImplHeadShape(candidate));
            if (isApplicable)
            {
                applicable.Add(candidate);
            }
        }

        if (applicable.Count == 0)
        {
            return null;
        }

        if (applicable.Count == 1)
        {
            return applicable[0];
        }

        return TryChooseMostSpecificImpl(applicable);
    }

    private List<ImplSymbol> LookupAllImplCandidatesForTrait(TypeId type, SymbolId trait)
    {
        if (!type.IsValid || !trait.IsValid)
        {
            return [];
        }

        var candidates = new List<ImplSymbol>();
        foreach (var (key, impls) in _impls)
        {
            if (key.TraitId == trait && key.TypeId == type)
            {
                candidates.AddRange(impls);
            }
        }

        return candidates;
    }

    private ImplSymbol? TryChooseMostSpecificImpl(IReadOnlyList<ImplSymbol> candidates)
    {
        var maximal = new List<ImplSymbol>();
        foreach (var candidate in candidates)
        {
            var candidateShape = BuildImplHeadShape(candidate);
            var isDominated = false;
            for (var i = 0; i < candidates.Count; i++)
            {
                if (ReferenceEquals(candidates[i], candidate) || candidates[i].Id == candidate.Id)
                {
                    continue;
                }

                var otherShape = BuildImplHeadShape(candidates[i]);
                if (ImplSpecializationComparer.CompareHeadsForSelection(otherShape, candidateShape) ==
                    ImplSpecializationRelation.MoreSpecific)
                {
                    isDominated = true;
                    break;
                }
            }

            if (!isDominated)
            {
                maximal.Add(candidate);
            }
        }

        return maximal.Count == 1 ? maximal[0] : null;
    }

    private ImplHeadShape BuildImplHeadShape(ImplSymbol impl)
    {
        return new ImplHeadShape(
            impl.Trait,
            GetCandidateTraitArgShapesForMatching(impl),
            ParseImplImplementingShape(impl));
    }

    private static bool CandidateMatchesRequestedTraitTypeArgs(
        ImplSymbol candidate,
        IReadOnlyList<string> requestedTraitTypeArgs)
    {
        var candidateTraitTypeArgs = GetCandidateTraitTypeArgsForMatching(candidate);
        if (candidateTraitTypeArgs.Count != requestedTraitTypeArgs.Count)
        {
            return false;
        }

        for (var i = 0; i < candidateTraitTypeArgs.Count; i++)
        {
            if (!ImplSpecializationComparer.IsApplicableTo(
                    ImplSpecializationComparer.ParseCanonicalShape(requestedTraitTypeArgs[i]),
                    ImplSpecializationComparer.ParseCanonicalShape(candidateTraitTypeArgs[i])))
            {
                return false;
            }
        }

        return true;
    }

    private bool CandidateMatchesRequestedTraitTypeArgKeys(
        ImplSymbol candidate,
        IReadOnlyList<ImplTypeRefKey> requestedTraitTypeArgKeys)
    {
        var candidateTraitTypeArgKeys = GetCandidateTraitTypeArgKeysForMatching(candidate);
        var candidateTraitArgShapes = GetCandidateTraitArgShapesForMatching(candidate);
        if (candidateTraitTypeArgKeys.Count != requestedTraitTypeArgKeys.Count ||
            candidateTraitArgShapes.Count != requestedTraitTypeArgKeys.Count)
        {
            return false;
        }

        for (var i = 0; i < candidateTraitTypeArgKeys.Count; i++)
        {
            var requestedShape = BuildRequestedTraitArgShapeForCandidate(
                requestedTraitTypeArgKeys[i],
                candidate,
                candidateTraitTypeArgKeys,
                i);
            if (!IsTypeParameterKey(candidateTraitTypeArgKeys[i]) &&
                candidateTraitTypeArgKeys[i].HasStructuredIdentity() &&
                requestedTraitTypeArgKeys[i].HasStructuredIdentity() &&
                !candidateTraitTypeArgKeys[i].Equals(requestedTraitTypeArgKeys[i]))
            {
                if (candidate.TraitTypeArgShapes.Count != requestedTraitTypeArgKeys.Count ||
                    !ImplSpecializationComparer.IsApplicableTo(
                        requestedShape,
                        candidateTraitArgShapes[i]))
                {
                    return false;
                }

                continue;
            }

            if (!ImplSpecializationComparer.IsApplicableTo(
                    requestedShape,
                    candidateTraitArgShapes[i]))
            {
                return false;
            }
        }

        return true;
    }

    private ImplTypeShapeNode BuildRequestedImplementingShapeForCandidate(
        ImplTypeRefKey requestedKey,
        ImplSymbol candidate)
    {
        return BuildImplTypeShapeNode(requestedKey);
    }

    private List<ImplTypeShapeNode> BuildRequestedTraitArgShapesForCandidate(
        IReadOnlyList<ImplTypeRefKey> requestedTraitTypeArgKeys,
        ImplSymbol candidate)
    {
        var candidateTraitTypeArgKeys = GetCandidateTraitTypeArgKeysForMatching(candidate);
        var shapes = new List<ImplTypeShapeNode>(requestedTraitTypeArgKeys.Count);
        for (var i = 0; i < requestedTraitTypeArgKeys.Count; i++)
        {
            shapes.Add(BuildRequestedTraitArgShapeForCandidate(
                requestedTraitTypeArgKeys[i],
                candidate,
                candidateTraitTypeArgKeys,
                i));
        }

        return shapes;
    }

    private ImplTypeShapeNode BuildRequestedTraitArgShapeForCandidate(
        ImplTypeRefKey requestedKey,
        ImplSymbol candidate,
        IReadOnlyList<ImplTypeRefKey> candidateTraitTypeArgKeys,
        int index)
    {
        if (candidate.TraitTypeArgShapes.Count > 0)
        {
            return BuildImplTypeShapeNode(requestedKey);
        }

        return BuildImplTypeShapeNode(requestedKey);
    }

    private static bool HasStructuredImplementingHead(ImplSymbol impl)
    {
        return impl.ImplementingTypeShape != null || !impl.ImplementingTypeKey.IsEmpty;
    }

    private bool IsTypeParameterKey(ImplTypeRefKey key)
    {
        return key.SymbolId.IsValid && GetSymbol(key.SymbolId) is TypeParamSymbol;
    }

    private static IReadOnlyList<string> GetCandidateTraitTypeArgsForMatching(ImplSymbol impl)
    {
        return impl.CanonicalTraitTypeArgs.Count > 0
            ? impl.CanonicalTraitTypeArgs
            : impl.TraitTypeArgs;
    }

    private static IReadOnlyList<ImplTypeRefKey> GetCandidateTraitTypeArgKeysForMatching(ImplSymbol impl)
    {
        return impl.GetMatchingTraitTypeArgKeys();
    }

    private IReadOnlyList<ImplTypeShapeNode> GetCandidateTraitArgShapesForMatching(ImplSymbol impl)
    {
        return impl.TraitTypeArgShapes.Count > 0
            ? impl.TraitTypeArgShapes
            : GetCandidateTraitTypeArgKeysForMatching(impl) is { Count: > 0 } keys
                ? keys.Select(BuildImplTypeShapeNode).ToList()
                : GetCandidateTraitTypeArgsForMatching(impl)
                    .Select(ImplSpecializationComparer.ParseCanonicalShape)
                    .ToList();
    }

    private ImplTypeShapeNode ParseImplImplementingShape(ImplSymbol impl)
    {
        if (impl.ImplementingTypeShape != null)
        {
            return impl.ImplementingTypeShape;
        }

        if (!impl.ImplementingTypeKey.IsEmpty)
        {
            return BuildImplTypeShapeNode(impl.ImplementingTypeKey);
        }

        if (impl.ImplementingType.IsValid)
        {
            return new ImplConstructorShapeNode($"type:{impl.ImplementingType.Value}", [])
            {
                SymbolId = ResolveTypeSymbolId(impl.ImplementingType),
                TypeId = impl.ImplementingType
            };
        }

        throw new InvalidOperationException(
            $"Impl '{impl.Name}' has no structured implementing type shape or key.");
    }

    private ImplTypeShapeNode BuildImplTypeShapeNode(ImplTypeRefKey key)
    {
        return ImplTypeShapeFactory.BuildFromKey(
            key,
            symbolId => GetSymbol(symbolId) is TypeParamSymbol typeParam
                ? typeParam.Name
                : null,
            ResolveImplTypeRefKeyTypeId);
    }

    private TypeId ResolveImplTypeRefKeyTypeId(ImplTypeRefKey key)
    {
        if (key.TypeId.IsValid)
        {
            return key.TypeId;
        }

        if (key.SymbolId.IsValid &&
            GetSymbol(key.SymbolId) is Symbol { TypeId: { IsValid: true } typeId })
        {
            return typeId;
        }

        return TypeId.None;
    }

}
