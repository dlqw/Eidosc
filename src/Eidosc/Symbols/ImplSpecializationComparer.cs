using Eidosc.Ast.Types;

namespace Eidosc.Symbols;

public enum ImplSpecializationRelation
{
    Equivalent,
    MoreSpecific,
    LessSpecific,
    Incomparable
}

public abstract record ImplTypeShapeNode
{
    public static ImplTypeShapeNode FromTypeNode(TypeNode node)
    {
        return node switch
        {
            TypePath typePath => FromTypePath(typePath),
            TupleType tuple => new ImplTupleShapeNode(tuple.Elements.Select(FromTypeNode).ToList()),
            ArrowType arrow => new ImplArrowShapeNode(FromTypeNode(arrow.ParamType), FromTypeNode(arrow.ReturnType)),
            EffectfulType effectful => new ImplEffectfulShapeNode(
                FromTypeNode(effectful.InputType),
                effectful.EnumerateEffectPaths()
                    .Select(path => string.Join(WellKnownStrings.Separators.Path, path))
                    .Where(path => !string.IsNullOrWhiteSpace(path))
                    .OrderBy(path => path, StringComparer.Ordinal)
                    .ToList(),
                effectful.OutputType == null ? null : FromTypeNode(effectful.OutputType)),
            WildcardType => ImplWildcardShapeNode.Instance,
            _ => new ImplConstructorShapeNode(node.GetType().Name, [])
        };
    }

    private static ImplTypeShapeNode FromTypePath(TypePath typePath)
    {
        if (typePath.ModulePath.Count == 0 &&
            typePath.TypeArgs.Count == 0 &&
            !string.IsNullOrWhiteSpace(typePath.TypeName) &&
            IsVariableLikeName(typePath.TypeName))
        {
            return new ImplVariableShapeNode(typePath.TypeName);
        }

        var name = typePath.ModulePath.Count > 0
            ? string.Join(WellKnownStrings.Separators.Path, typePath.ModulePath) + WellKnownStrings.Separators.Path + typePath.TypeName
            : typePath.TypeName;
        return new ImplConstructorShapeNode(name, typePath.TypeArgs.Select(FromTypeNode).ToList())
        {
            SymbolId = typePath.SymbolId
        };
    }

    private static bool IsVariableLikeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.Length == 1 && char.IsLetter(name[0]))
        {
            return true;
        }

        return char.IsLower(name[0]);
    }
}

public sealed record ImplWildcardShapeNode : ImplTypeShapeNode
{
    public static ImplWildcardShapeNode Instance { get; } = new();

    private ImplWildcardShapeNode()
    {
    }
}

public sealed record ImplVariableShapeNode(string Name) : ImplTypeShapeNode;

public sealed record ImplValueVariableShapeNode(string Name, TypeId TypeId) : ImplTypeShapeNode;

public sealed record ImplConcreteValueShapeNode(string CanonicalPayload, TypeId TypeId) : ImplTypeShapeNode;

public sealed record ImplConstructorShapeNode(string Name, IReadOnlyList<ImplTypeShapeNode> Args) : ImplTypeShapeNode
{
    public SymbolId SymbolId { get; init; } = SymbolId.None;

    public TypeId TypeId { get; init; } = TypeId.None;
}

public sealed record ImplTupleShapeNode(IReadOnlyList<ImplTypeShapeNode> Elements) : ImplTypeShapeNode;

public sealed record ImplArrowShapeNode(ImplTypeShapeNode ParamType, ImplTypeShapeNode ReturnType) : ImplTypeShapeNode;

public sealed record ImplEffectfulShapeNode(
    ImplTypeShapeNode InputType,
    IReadOnlyList<string> EffectPaths,
    ImplTypeShapeNode? OutputType) : ImplTypeShapeNode;

public sealed record ImplHeadShape(
    SymbolId Trait,
    IReadOnlyList<ImplTypeShapeNode> TraitArgs,
    ImplTypeShapeNode ImplementingType);

public static class ImplSpecializationComparer
{
    private const char LeftOverlapSide = 'L';
    private const char RightOverlapSide = 'R';

    private readonly record struct OverlapVariableKey(char Side, bool IsValueDomain, string Name);

    private readonly record struct OverlapTerm(char Side, ImplTypeShapeNode Node);

    public static ImplTypeShapeNode ParseCanonicalShape(string? text)
    {
        var trimmed = text?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return ImplWildcardShapeNode.Instance;
        }

        if (string.Equals(trimmed, "_", StringComparison.Ordinal))
        {
            return ImplWildcardShapeNode.Instance;
        }

        if (TrySplitTopLevelArrow(trimmed, out var paramText, out var returnText))
        {
            return new ImplArrowShapeNode(
                ParseCanonicalShape(paramText),
                ParseCanonicalShape(returnText));
        }

        if (trimmed.StartsWith("(", StringComparison.Ordinal) &&
            trimmed.EndsWith(")", StringComparison.Ordinal))
        {
            var tuplePayload = trimmed[1..^1];
            var tupleParts = SplitTopLevelCommaSeparated(tuplePayload);
            if (tupleParts.Count > 1)
            {
                return new ImplTupleShapeNode(tupleParts.Select(ParseCanonicalShape).ToList());
            }
        }

        var bracketIndex = trimmed.IndexOf('[');
        if (bracketIndex <= 0 || !trimmed.EndsWith("]", StringComparison.Ordinal))
        {
            return IsVariableLikeCanonicalName(trimmed)
                ? new ImplVariableShapeNode(trimmed)
                : new ImplConstructorShapeNode(trimmed, []);
        }

        var name = trimmed[..bracketIndex];
        var payload = trimmed.Substring(bracketIndex + 1, trimmed.Length - bracketIndex - 2);
        var parts = SplitTopLevelCommaSeparated(payload);
        return new ImplConstructorShapeNode(name, parts.Select(ParseCanonicalShape).ToList());
    }

    public static ImplSpecializationRelation CompareHeads(ImplHeadShape left, ImplHeadShape right)
    {
        if (left.Trait != right.Trait || left.TraitArgs.Count != right.TraitArgs.Count)
        {
            return ImplSpecializationRelation.Incomparable;
        }

        var relation = CompareNodes(left.ImplementingType, right.ImplementingType);
        if (relation == ImplSpecializationRelation.Incomparable)
        {
            return relation;
        }

        for (var i = 0; i < left.TraitArgs.Count; i++)
        {
            relation = MergeRelations(relation, CompareNodes(left.TraitArgs[i], right.TraitArgs[i]));
            if (relation == ImplSpecializationRelation.Incomparable)
            {
                return relation;
            }
        }

        return relation;
    }

    /// <summary>
    /// Determines whether two impl heads can match at least one common concrete instantiation.
    /// </summary>
    /// <param name="left">The first impl head.</param>
    /// <param name="right">The second impl head.</param>
    /// <returns><see langword="true" /> if the heads can overlap; otherwise, <see langword="false" />.</returns>
    public static bool MayOverlap(ImplHeadShape left, ImplHeadShape right)
    {
        if (left.Trait != right.Trait || left.TraitArgs.Count != right.TraitArgs.Count)
        {
            return false;
        }

        var bindings = new Dictionary<OverlapVariableKey, OverlapTerm>();
        if (!MayOverlap(
                new OverlapTerm(LeftOverlapSide, left.ImplementingType),
                new OverlapTerm(RightOverlapSide, right.ImplementingType),
                bindings))
        {
            return false;
        }

        for (var i = 0; i < left.TraitArgs.Count; i++)
        {
            if (!MayOverlap(
                    new OverlapTerm(LeftOverlapSide, left.TraitArgs[i]),
                    new OverlapTerm(RightOverlapSide, right.TraitArgs[i]),
                    bindings))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Determines whether two impl type shapes can match at least one common concrete instantiation.
    /// </summary>
    /// <param name="left">The first impl type shape.</param>
    /// <param name="right">The second impl type shape.</param>
    /// <returns><see langword="true" /> if the shapes can overlap; otherwise, <see langword="false" />.</returns>
    public static bool MayOverlap(ImplTypeShapeNode left, ImplTypeShapeNode right)
    {
        return MayOverlap(
            new OverlapTerm(LeftOverlapSide, left),
            new OverlapTerm(RightOverlapSide, right),
            new Dictionary<OverlapVariableKey, OverlapTerm>());
    }

    private static bool MayOverlap(
        OverlapTerm left,
        OverlapTerm right,
        Dictionary<OverlapVariableKey, OverlapTerm> bindings)
    {
        left = ResolveOverlapTerm(left, bindings);
        right = ResolveOverlapTerm(right, bindings);

        if (left.Node is ImplWildcardShapeNode || right.Node is ImplWildcardShapeNode)
        {
            return true;
        }

        if (left.Node is ImplVariableShapeNode leftVariable)
        {
            return TryBindOverlapVariable(
                new OverlapVariableKey(left.Side, IsValueDomain: false, leftVariable.Name),
                right,
                bindings);
        }

        if (right.Node is ImplVariableShapeNode rightVariable)
        {
            return TryBindOverlapVariable(
                new OverlapVariableKey(right.Side, IsValueDomain: false, rightVariable.Name),
                left,
                bindings);
        }

        if (left.Node is ImplValueVariableShapeNode leftValueVariable)
        {
            return ValueTypesCompatible(leftValueVariable.TypeId, GetValueTypeId(right.Node)) &&
                   TryBindOverlapVariable(
                       new OverlapVariableKey(left.Side, IsValueDomain: true, leftValueVariable.Name),
                       right,
                       bindings);
        }

        if (right.Node is ImplValueVariableShapeNode rightValueVariable)
        {
            return ValueTypesCompatible(rightValueVariable.TypeId, GetValueTypeId(left.Node)) &&
                   TryBindOverlapVariable(
                       new OverlapVariableKey(right.Side, IsValueDomain: true, rightValueVariable.Name),
                       left,
                       bindings);
        }

        return (left.Node, right.Node) switch
        {
            (ImplConstructorShapeNode leftCtor, ImplConstructorShapeNode rightCtor) =>
                ConstructorsMayOverlap(left.Side, leftCtor, right.Side, rightCtor, bindings),
            (ImplTupleShapeNode leftTuple, ImplTupleShapeNode rightTuple) =>
                ShapeListsMayOverlap(left.Side, leftTuple.Elements, right.Side, rightTuple.Elements, requireSameLength: true, bindings),
            (ImplArrowShapeNode leftArrow, ImplArrowShapeNode rightArrow) =>
                MayOverlap(new OverlapTerm(left.Side, leftArrow.ParamType), new OverlapTerm(right.Side, rightArrow.ParamType), bindings) &&
                MayOverlap(new OverlapTerm(left.Side, leftArrow.ReturnType), new OverlapTerm(right.Side, rightArrow.ReturnType), bindings),
            (ImplEffectfulShapeNode leftEffectful, ImplEffectfulShapeNode rightEffectful) =>
                EffectfulShapesMayOverlap(left.Side, leftEffectful, right.Side, rightEffectful, bindings),
            (ImplConcreteValueShapeNode leftValue, ImplConcreteValueShapeNode rightValue) =>
                ConcreteValuesEquivalent(leftValue, rightValue),
            _ => false
        };
    }

    public static ImplSpecializationRelation CompareHeadsForSelection(ImplHeadShape left, ImplHeadShape right)
    {
        var relation = CompareHeads(left, right);
        if (relation != ImplSpecializationRelation.Equivalent)
        {
            return relation;
        }

        var leftConstraintScore = CountVariableReuseConstraints(left);
        var rightConstraintScore = CountVariableReuseConstraints(right);
        if (leftConstraintScore > rightConstraintScore)
        {
            return ImplSpecializationRelation.MoreSpecific;
        }

        return leftConstraintScore < rightConstraintScore
            ? ImplSpecializationRelation.LessSpecific
            : ImplSpecializationRelation.Equivalent;
    }

    private static OverlapTerm ResolveOverlapTerm(
        OverlapTerm term,
        Dictionary<OverlapVariableKey, OverlapTerm> bindings)
    {
        while (TryGetOverlapVariableKey(term, out var key) &&
               bindings.TryGetValue(key, out var bound))
        {
            term = bound;
        }

        return term;
    }

    private static bool TryBindOverlapVariable(
        OverlapVariableKey key,
        OverlapTerm value,
        Dictionary<OverlapVariableKey, OverlapTerm> bindings)
    {
        if (string.IsNullOrWhiteSpace(key.Name))
        {
            return false;
        }

        value = ResolveOverlapTerm(value, bindings);
        if (TryGetOverlapVariableKey(value, out var valueKey) && key.Equals(valueKey))
        {
            return true;
        }

        if (key.IsValueDomain != IsValueShape(value.Node))
        {
            return false;
        }

        if (bindings.TryGetValue(key, out var existing))
        {
            return MayOverlap(existing, value, bindings);
        }

        bindings[key] = value;
        return true;
    }

    private static bool TryGetOverlapVariableKey(OverlapTerm term, out OverlapVariableKey key)
    {
        switch (term.Node)
        {
            case ImplVariableShapeNode variable:
                key = new OverlapVariableKey(term.Side, IsValueDomain: false, variable.Name);
                return true;
            case ImplValueVariableShapeNode variable:
                key = new OverlapVariableKey(term.Side, IsValueDomain: true, variable.Name);
                return true;
            default:
                key = default;
                return false;
        }
    }

    private static bool ConstructorsMayOverlap(
        char leftSide,
        ImplConstructorShapeNode left,
        char rightSide,
        ImplConstructorShapeNode right,
        Dictionary<OverlapVariableKey, OverlapTerm> bindings)
    {
        if (!ConstructorIdentitiesCompatible(left, right))
        {
            return false;
        }

        if (!HasComparableConstructorIdentity(left, right) &&
            !string.Equals(left.Name, right.Name, StringComparison.Ordinal))
        {
            return false;
        }

        var sharedArgumentCount = Math.Min(left.Args.Count, right.Args.Count);
        for (var i = 0; i < sharedArgumentCount; i++)
        {
            if (!MayOverlap(
                    new OverlapTerm(leftSide, left.Args[i]),
                    new OverlapTerm(rightSide, right.Args[i]),
                    bindings))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ShapeListsMayOverlap(
        char leftSide,
        IReadOnlyList<ImplTypeShapeNode> left,
        char rightSide,
        IReadOnlyList<ImplTypeShapeNode> right,
        bool requireSameLength,
        Dictionary<OverlapVariableKey, OverlapTerm> bindings)
    {
        if (requireSameLength && left.Count != right.Count)
        {
            return false;
        }

        var sharedCount = Math.Min(left.Count, right.Count);
        for (var i = 0; i < sharedCount; i++)
        {
            if (!MayOverlap(
                    new OverlapTerm(leftSide, left[i]),
                    new OverlapTerm(rightSide, right[i]),
                    bindings))
            {
                return false;
            }
        }

        return true;
    }

    private static bool EffectfulShapesMayOverlap(
        char leftSide,
        ImplEffectfulShapeNode left,
        char rightSide,
        ImplEffectfulShapeNode right,
        Dictionary<OverlapVariableKey, OverlapTerm> bindings)
    {
        return left.EffectPaths.SequenceEqual(right.EffectPaths, StringComparer.Ordinal) &&
               MayOverlap(new OverlapTerm(leftSide, left.InputType), new OverlapTerm(rightSide, right.InputType), bindings) &&
               ((left.OutputType == null && right.OutputType == null) ||
                (left.OutputType != null &&
                 right.OutputType != null &&
                 MayOverlap(new OverlapTerm(leftSide, left.OutputType), new OverlapTerm(rightSide, right.OutputType), bindings)));
    }

    public static bool IsApplicableTo(ImplHeadShape requested, ImplHeadShape candidate)
    {
        if (requested.Trait != candidate.Trait ||
            requested.TraitArgs.Count != candidate.TraitArgs.Count)
        {
            return false;
        }

        var bindings = new Dictionary<string, ImplTypeShapeNode>(StringComparer.Ordinal);
        if (!IsApplicableTo(requested.ImplementingType, candidate.ImplementingType, bindings))
        {
            return false;
        }

        for (var i = 0; i < requested.TraitArgs.Count; i++)
        {
            if (!IsApplicableTo(requested.TraitArgs[i], candidate.TraitArgs[i], bindings))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsApplicableTo(ImplTypeShapeNode requested, ImplTypeShapeNode candidate)
    {
        return IsApplicableTo(
            requested,
            candidate,
            new Dictionary<string, ImplTypeShapeNode>(StringComparer.Ordinal));
    }

    private static bool IsApplicableTo(
        ImplTypeShapeNode requested,
        ImplTypeShapeNode candidate,
        Dictionary<string, ImplTypeShapeNode> bindings)
    {
        if (candidate is ImplWildcardShapeNode)
        {
            return true;
        }

        if (candidate is ImplVariableShapeNode variable)
        {
            return !IsValueShape(requested) &&
                   TryBindCandidateVariable(variable.Name, requested, bindings);
        }

        if (candidate is ImplValueVariableShapeNode valueVariable)
        {
            return IsValueShape(requested) &&
                   ValueTypesCompatible(valueVariable.TypeId, GetValueTypeId(requested)) &&
                   TryBindCandidateVariable(valueVariable.Name, requested, bindings);
        }

        if (requested is ImplWildcardShapeNode)
        {
            return false;
        }

        return (requested, candidate) switch
        {
            (ImplConstructorShapeNode requestedCtor, ImplConstructorShapeNode candidateCtor) =>
                IsConstructorApplicableTo(requestedCtor, candidateCtor, bindings),
            (ImplTupleShapeNode requestedTuple, ImplTupleShapeNode candidateTuple) =>
                AreNodeListsApplicableTo(requestedTuple.Elements, candidateTuple.Elements, bindings),
            (ImplArrowShapeNode requestedArrow, ImplArrowShapeNode candidateArrow) =>
                IsApplicableTo(requestedArrow.ParamType, candidateArrow.ParamType, bindings) &&
                IsApplicableTo(requestedArrow.ReturnType, candidateArrow.ReturnType, bindings),
            (ImplEffectfulShapeNode requestedEffectful, ImplEffectfulShapeNode candidateEffectful) =>
                IsEffectfulApplicableTo(requestedEffectful, candidateEffectful, bindings),
            (ImplConcreteValueShapeNode requestedValue, ImplConcreteValueShapeNode candidateValue) =>
                ConcreteValuesEquivalent(requestedValue, candidateValue),
            _ => false
        };
    }

    private static bool TryBindCandidateVariable(
        string name,
        ImplTypeShapeNode requested,
        Dictionary<string, ImplTypeShapeNode> bindings)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (!bindings.TryGetValue(name, out var existing))
        {
            bindings[name] = requested;
            return true;
        }

        return AreBoundShapesEquivalent(existing, requested);
    }

    private static bool AreBoundShapesEquivalent(ImplTypeShapeNode left, ImplTypeShapeNode right)
    {
        if (ReferenceEquals(left, right) || Equals(left, right))
        {
            return true;
        }

        return (left, right) switch
        {
            (ImplVariableShapeNode l, ImplVariableShapeNode r) =>
                string.Equals(l.Name, r.Name, StringComparison.Ordinal),
            (ImplValueVariableShapeNode l, ImplValueVariableShapeNode r) =>
                string.Equals(l.Name, r.Name, StringComparison.Ordinal) &&
                ValueTypesCompatible(l.TypeId, r.TypeId),
            (ImplConcreteValueShapeNode l, ImplConcreteValueShapeNode r) =>
                ConcreteValuesEquivalent(l, r),
            (ImplConstructorShapeNode l, ImplConstructorShapeNode r) =>
                CompareConstructorNodes(l, r) == ImplSpecializationRelation.Equivalent,
            (ImplTupleShapeNode l, ImplTupleShapeNode r) =>
                l.Elements.Count == r.Elements.Count &&
                l.Elements.Zip(r.Elements).All(pair => AreBoundShapesEquivalent(pair.First, pair.Second)),
            (ImplArrowShapeNode l, ImplArrowShapeNode r) =>
                AreBoundShapesEquivalent(l.ParamType, r.ParamType) &&
                AreBoundShapesEquivalent(l.ReturnType, r.ReturnType),
            (ImplEffectfulShapeNode l, ImplEffectfulShapeNode r) =>
                l.EffectPaths.SequenceEqual(r.EffectPaths, StringComparer.Ordinal) &&
                ((l.OutputType == null && r.OutputType == null) ||
                 (l.OutputType != null &&
                  r.OutputType != null &&
                  AreBoundShapesEquivalent(l.OutputType, r.OutputType))) &&
                AreBoundShapesEquivalent(l.InputType, r.InputType),
            _ => false
        };
    }

    private static bool IsConstructorApplicableTo(
        ImplConstructorShapeNode requested,
        ImplConstructorShapeNode candidate,
        Dictionary<string, ImplTypeShapeNode> bindings)
    {
        if (!ConstructorIdentitiesCompatible(requested, candidate))
        {
            return false;
        }

        if (!HasComparableConstructorIdentity(requested, candidate) &&
            !string.Equals(requested.Name, candidate.Name, StringComparison.Ordinal))
        {
            return false;
        }

        if (requested.Args.Count < candidate.Args.Count)
        {
            return false;
        }

        if (candidate.Args.Count == 0)
        {
            return true;
        }

        for (var i = 0; i < candidate.Args.Count; i++)
        {
            if (!IsApplicableTo(requested.Args[i], candidate.Args[i], bindings))
            {
                return false;
            }
        }

        return requested.Args.Count == candidate.Args.Count || requested.Args.Count > candidate.Args.Count;
    }

    private static int CountVariableReuseConstraints(ImplHeadShape head)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        CountVariableOccurrences(head.ImplementingType, occurrences);
        foreach (var traitArg in head.TraitArgs)
        {
            CountVariableOccurrences(traitArg, occurrences);
        }

        var score = 0;
        foreach (var count in occurrences.Values)
        {
            if (count > 1)
            {
                score += count - 1;
            }
        }

        return score;
    }

    private static void CountVariableOccurrences(
        ImplTypeShapeNode node,
        Dictionary<string, int> occurrences)
    {
        switch (node)
        {
            case ImplVariableShapeNode variable when !string.IsNullOrWhiteSpace(variable.Name):
                occurrences.TryGetValue(variable.Name, out var count);
                occurrences[variable.Name] = count + 1;
                break;
            case ImplValueVariableShapeNode variable when !string.IsNullOrWhiteSpace(variable.Name):
                occurrences.TryGetValue(variable.Name, out var valueCount);
                occurrences[variable.Name] = valueCount + 1;
                break;
            case ImplConstructorShapeNode constructor:
                foreach (var arg in constructor.Args)
                {
                    CountVariableOccurrences(arg, occurrences);
                }
                break;
            case ImplTupleShapeNode tuple:
                foreach (var element in tuple.Elements)
                {
                    CountVariableOccurrences(element, occurrences);
                }
                break;
            case ImplArrowShapeNode arrow:
                CountVariableOccurrences(arrow.ParamType, occurrences);
                CountVariableOccurrences(arrow.ReturnType, occurrences);
                break;
            case ImplEffectfulShapeNode effectful:
                CountVariableOccurrences(effectful.InputType, occurrences);
                if (effectful.OutputType != null)
                {
                    CountVariableOccurrences(effectful.OutputType, occurrences);
                }
                break;
        }
    }

    private static bool AreNodeListsApplicableTo(
        IReadOnlyList<ImplTypeShapeNode> requested,
        IReadOnlyList<ImplTypeShapeNode> candidate,
        Dictionary<string, ImplTypeShapeNode> bindings)
    {
        if (requested.Count != candidate.Count)
        {
            return false;
        }

        for (var i = 0; i < requested.Count; i++)
        {
            if (!IsApplicableTo(requested[i], candidate[i], bindings))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsEffectfulApplicableTo(
        ImplEffectfulShapeNode requested,
        ImplEffectfulShapeNode candidate,
        Dictionary<string, ImplTypeShapeNode> bindings)
    {
        if (!requested.EffectPaths.SequenceEqual(candidate.EffectPaths, StringComparer.Ordinal) ||
            !IsApplicableTo(requested.InputType, candidate.InputType, bindings))
        {
            return false;
        }

        if (requested.OutputType == null && candidate.OutputType == null)
        {
            return true;
        }

        return requested.OutputType != null &&
               candidate.OutputType != null &&
               IsApplicableTo(requested.OutputType, candidate.OutputType, bindings);
    }

    public static ImplSpecializationRelation CompareNodes(ImplTypeShapeNode left, ImplTypeShapeNode right)
    {
        if (ReferenceEquals(left, right) || Equals(left, right))
        {
            return ImplSpecializationRelation.Equivalent;
        }

        if (left is not ImplWildcardShapeNode &&
            right is not ImplWildcardShapeNode &&
            IsValueShape(left) != IsValueShape(right))
        {
            return ImplSpecializationRelation.Incomparable;
        }

        if (IsOpen(left) && IsOpen(right))
        {
            return ImplSpecializationRelation.Equivalent;
        }

        if (IsOpen(left))
        {
            return ImplSpecializationRelation.LessSpecific;
        }

        if (IsOpen(right))
        {
            return ImplSpecializationRelation.MoreSpecific;
        }

        return (left, right) switch
        {
            (ImplConstructorShapeNode l, ImplConstructorShapeNode r) => CompareConstructorNodes(l, r),
            (ImplTupleShapeNode l, ImplTupleShapeNode r) => CompareNodeLists(l.Elements, r.Elements),
            (ImplArrowShapeNode l, ImplArrowShapeNode r) => MergeRelations(
                CompareNodes(l.ParamType, r.ParamType),
                CompareNodes(l.ReturnType, r.ReturnType)),
            (ImplEffectfulShapeNode l, ImplEffectfulShapeNode r) => CompareEffectfulNodes(l, r),
            (ImplConcreteValueShapeNode l, ImplConcreteValueShapeNode r) =>
                ConcreteValuesEquivalent(l, r)
                    ? ImplSpecializationRelation.Equivalent
                    : ImplSpecializationRelation.Incomparable,
            _ => ImplSpecializationRelation.Incomparable
        };
    }

    private static ImplSpecializationRelation CompareConstructorNodes(
        ImplConstructorShapeNode left,
        ImplConstructorShapeNode right)
    {
        if (!ConstructorIdentitiesCompatible(left, right))
        {
            return ImplSpecializationRelation.Incomparable;
        }

        if (!HasComparableConstructorIdentity(left, right) &&
            !string.Equals(left.Name, right.Name, StringComparison.Ordinal))
        {
            return ImplSpecializationRelation.Incomparable;
        }

        // Higher-kinded type matching: a fully-applied type (e.g., DeepBoxedResult[String, Int])
        // matches against a partially-applied type constructor from the impl declaration
        // (e.g., DeepBoxedResult[String]). The prefix args must be compatible.
        if (left.Args.Count > right.Args.Count)
        {
            return right.Args.Count == 0
                ? ImplSpecializationRelation.MoreSpecific
                : ComparePrefixAndWrap(left.Args, right.Args, ImplSpecializationRelation.MoreSpecific);
        }

        if (left.Args.Count < right.Args.Count)
        {
            return left.Args.Count == 0
                ? ImplSpecializationRelation.LessSpecific
                : ComparePrefixAndWrap(right.Args, left.Args, ImplSpecializationRelation.LessSpecific);
        }

        return CompareNodeLists(left.Args, right.Args);
    }

    private static bool ConstructorIdentitiesCompatible(
        ImplConstructorShapeNode left,
        ImplConstructorShapeNode right)
    {
        if (left.TypeId.IsValid && right.TypeId.IsValid)
        {
            return left.TypeId == right.TypeId;
        }

        if (left.SymbolId.IsValid && right.SymbolId.IsValid)
        {
            return left.SymbolId == right.SymbolId;
        }

        if (HasConstructorIdentity(left) && HasConstructorIdentity(right))
        {
            return false;
        }

        return true;
    }

    private static bool HasComparableConstructorIdentity(
        ImplConstructorShapeNode left,
        ImplConstructorShapeNode right)
    {
        return (left.SymbolId.IsValid && right.SymbolId.IsValid) ||
               (left.TypeId.IsValid && right.TypeId.IsValid);
    }

    private static bool HasConstructorIdentity(ImplConstructorShapeNode node)
    {
        return node.SymbolId.IsValid || node.TypeId.IsValid;
    }

    /// <summary>
    /// Compare the shorter list as a prefix of the longer list.
    /// Returns <paramref name="defaultRelation"/> if the prefix matches, Incomparable otherwise.
    /// </summary>
    private static ImplSpecializationRelation ComparePrefixAndWrap(
        IReadOnlyList<ImplTypeShapeNode> longer,
        IReadOnlyList<ImplTypeShapeNode> shorter,
        ImplSpecializationRelation defaultRelation)
    {
        for (var i = 0; i < shorter.Count; i++)
        {
            var childRelation = CompareNodes(longer[i], shorter[i]);
            if (childRelation is ImplSpecializationRelation.Incomparable)
            {
                return ImplSpecializationRelation.Incomparable;
            }

            if (childRelation is not ImplSpecializationRelation.Equivalent)
            {
                return ImplSpecializationRelation.Incomparable;
            }
        }

        return defaultRelation;
    }

    private static ImplSpecializationRelation CompareEffectfulNodes(
        ImplEffectfulShapeNode left,
        ImplEffectfulShapeNode right)
    {
        if (!left.EffectPaths.SequenceEqual(right.EffectPaths, StringComparer.Ordinal))
        {
            return ImplSpecializationRelation.Incomparable;
        }

        var relation = CompareNodes(left.InputType, right.InputType);
        if (relation == ImplSpecializationRelation.Incomparable)
        {
            return relation;
        }

        if (left.OutputType == null && right.OutputType == null)
        {
            return relation;
        }

        if (left.OutputType == null || right.OutputType == null)
        {
            return ImplSpecializationRelation.Incomparable;
        }

        return MergeRelations(relation, CompareNodes(left.OutputType, right.OutputType));
    }

    private static ImplSpecializationRelation CompareNodeLists(
        IReadOnlyList<ImplTypeShapeNode> left,
        IReadOnlyList<ImplTypeShapeNode> right)
    {
        if (left.Count != right.Count)
        {
            return ImplSpecializationRelation.Incomparable;
        }

        var relation = ImplSpecializationRelation.Equivalent;
        for (var i = 0; i < left.Count; i++)
        {
            relation = MergeRelations(relation, CompareNodes(left[i], right[i]));
            if (relation == ImplSpecializationRelation.Incomparable)
            {
                return relation;
            }
        }

        return relation;
    }

    private static ImplSpecializationRelation MergeRelations(
        ImplSpecializationRelation left,
        ImplSpecializationRelation right)
    {
        if (left == ImplSpecializationRelation.Incomparable ||
            right == ImplSpecializationRelation.Incomparable)
        {
            return ImplSpecializationRelation.Incomparable;
        }

        if (left == ImplSpecializationRelation.Equivalent)
        {
            return right;
        }

        if (right == ImplSpecializationRelation.Equivalent)
        {
            return left;
        }

        if (left == right)
        {
            return left;
        }

        return ImplSpecializationRelation.Incomparable;
    }

    private static bool IsOpen(ImplTypeShapeNode node)
    {
        return node is ImplWildcardShapeNode or ImplVariableShapeNode or ImplValueVariableShapeNode;
    }

    private static bool IsValueShape(ImplTypeShapeNode node) =>
        node is ImplValueVariableShapeNode or ImplConcreteValueShapeNode;

    private static TypeId GetValueTypeId(ImplTypeShapeNode node) => node switch
    {
        ImplValueVariableShapeNode value => value.TypeId,
        ImplConcreteValueShapeNode value => value.TypeId,
        _ => TypeId.None
    };

    private static bool ConcreteValuesEquivalent(
        ImplConcreteValueShapeNode left,
        ImplConcreteValueShapeNode right)
    {
        return ValueTypesCompatible(left.TypeId, right.TypeId) &&
               string.Equals(left.CanonicalPayload, right.CanonicalPayload, StringComparison.Ordinal);
    }

    private static bool ValueTypesCompatible(TypeId left, TypeId right)
    {
        return !left.IsValid || !right.IsValid || left == right;
    }

    private static bool IsVariableLikeCanonicalName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name.Length == 1 && char.IsLetter(name[0]))
        {
            return true;
        }

        return char.IsLower(name[0]);
    }

    private static bool TrySplitTopLevelArrow(string text, out string left, out string right)
    {
        left = string.Empty;
        right = string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var depth = 0;
        for (var i = 0; i < text.Length - 1; i++)
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
            }

            if (depth == 0 &&
                text[i] == '-' &&
                text[i + 1] == '>')
            {
                left = text[..i].Trim();
                right = text[(i + 2)..].Trim();
                return left.Length > 0 && right.Length > 0;
            }
        }

        return false;
    }

    private static List<string> SplitTopLevelCommaSeparated(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var parts = new List<string>();
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
                    var part = text[start..i].Trim();
                    if (part.Length > 0)
                    {
                        parts.Add(part);
                    }

                    start = i + 1;
                    break;
            }
        }

        var tail = text[start..].Trim();
        if (tail.Length > 0)
        {
            parts.Add(tail);
        }

        return parts;
    }
}
