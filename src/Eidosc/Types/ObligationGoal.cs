using System.Globalization;
using System.Text;
using Eidosc.Semantic;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

public abstract record ObligationGoal
{
    public SourceSpan Span { get; init; }

    public string Reason { get; init; } = string.Empty;
}

public sealed record EqualGoal : ObligationGoal
{
    public required Type Left { get; init; }

    public required Type Right { get; init; }
}

public sealed record ImplementsGoal : ObligationGoal
{
    public required Type Type { get; init; }

    public required SymbolId Trait { get; init; }

    public string TraitName { get; init; } = string.Empty;

    public List<Type> TraitArgs { get; init; } = [];

    public List<ImplTypeRefKey> TraitArgKeys { get; init; } = [];
}

public sealed record HasKindGoal : ObligationGoal
{
    public required Type Type { get; init; }

    public required string ExpectedKind { get; init; }
}

public sealed record EffectSubsetGoal : ObligationGoal
{
    public required EffectRow Required { get; init; }

    public required EffectRow Allowed { get; init; }
}

public sealed record NormalizeProjectionGoal : ObligationGoal
{
    public required string ProjectionIdentity { get; init; }

    public required Type ExpectedType { get; init; }

    public Type? NormalizedType { get; init; }

    public SymbolId Instance { get; init; } = SymbolId.None;

    public SymbolId Member { get; init; } = SymbolId.None;
}

public sealed record AllGoal : ObligationGoal
{
    public IReadOnlyList<ObligationGoal> Goals { get; init; } = [];
}

public enum ObligationState
{
    Fresh,
    Evaluating,
    Proven,
    Ambiguous,
    Failed,
    Overflow
}

public abstract record ObligationEvidence;

public sealed record EqualityObligationEvidence(Type Left, Type Right) : ObligationEvidence;

public sealed record KindObligationEvidence(Type Type, string Kind) : ObligationEvidence;

public sealed record EffectInclusionObligationEvidence(
    EffectRow Required,
    EffectRow Allowed,
    IReadOnlyDictionary<int, EffectRow> Substitutions) : ObligationEvidence;

public sealed record ProjectionObligationEvidence(
    string ProjectionIdentity,
    SymbolId Instance,
    SymbolId Member,
    Type NormalizedType,
    ObligationEvidence EqualityEvidence) : ObligationEvidence;

public sealed record AllObligationEvidence(IReadOnlyList<ObligationEvidence> Children) : ObligationEvidence;

public sealed record TraitObligationEvidence(
    Type Type,
    SymbolId Trait,
    string TraitName,
    SymbolId Instance,
    string InstanceIdentity,
    bool IsBuiltin,
    bool IsSupertrait) : ObligationEvidence;

public sealed record DeferredObligationEvidence(string Blocker) : ObligationEvidence;

public sealed record ObligationResult(
    string CanonicalKey,
    ObligationGoal Goal,
    ObligationState State,
    ObligationEvidence? Evidence,
    string? Explanation);

internal static class ObligationGoalAdapter
{
    public static ObligationGoal FromConstraint(TypeConstraint constraint) => constraint switch
    {
        EqualityConstraint equality => new EqualGoal
        {
            Left = equality.Left,
            Right = equality.Right,
            Span = equality.Span,
            Reason = "type equality constraint"
        },
        TraitConstraint trait => new ImplementsGoal
        {
            Type = trait.Type,
            Trait = trait.Trait,
            TraitName = trait.TraitName,
            TraitArgs = trait.TraitArgs,
            TraitArgKeys = trait.TraitArgKeys,
            Span = trait.Span,
            Reason = "trait constraint"
        },
        KindConstraint kind => new HasKindGoal
        {
            Type = kind.Type,
            ExpectedKind = kind.ExpectedKind,
            Span = kind.Span,
            Reason = "kind constraint"
        },
        _ => throw new ArgumentOutOfRangeException(nameof(constraint), constraint, "Unsupported type constraint.")
    };
}

internal static class ObligationCanonicalizer
{
    public static string Build(ObligationGoal goal, Substitution substitution)
    {
        var builder = new StringBuilder();
        var context = new CanonicalizationContext(substitution, goal is AllGoal);
        AppendGoal(builder, goal, ref context);
        context.AppendInferenceContextIdentity(builder);
        return builder.ToString();
    }

    private static void AppendGoal(
        StringBuilder builder,
        ObligationGoal goal,
        ref CanonicalizationContext context)
    {
        if (!context.TryEnterGoal(goal, out var cycleSlot))
        {
            builder.Append("cycle-ref(").Append(cycleSlot).Append(')');
            return;
        }

        try
        {
            switch (goal)
            {
                case EqualGoal equality:
                    builder.Append("equal(");
                    context.AppendType(builder, equality.Left);
                    builder.Append(',');
                    context.AppendType(builder, equality.Right);
                    builder.Append(')');
                    break;
                case ImplementsGoal trait:
                    AppendTraitGoal(builder, trait, ref context);
                    break;
                case HasKindGoal kind:
                    builder.Append("kind(");
                    context.AppendType(builder, kind.Type);
                    builder.Append(',').Append(kind.ExpectedKind).Append(')');
                    break;
                case EffectSubsetGoal effect:
                    builder.Append("effect-subset(");
                    context.AppendType(builder, effect.Required);
                    builder.Append(',');
                    context.AppendType(builder, effect.Allowed);
                    builder.Append(')');
                    break;
                case NormalizeProjectionGoal projection:
                    builder.Append("normalize(").Append(projection.ProjectionIdentity).Append(',');
                    context.AppendType(builder, projection.ExpectedType);
                    builder.Append(')');
                    break;
                case AllGoal all:
                    builder.Append("all([");
                    for (var index = 0; index < all.Goals.Count; index++)
                    {
                        if (index > 0)
                        {
                            builder.Append(',');
                        }
                        AppendGoal(builder, all.Goals[index], ref context);
                    }
                    builder.Append("])" );
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(goal), goal, "Unsupported obligation goal.");
            }
        }
        finally
        {
            context.ExitGoal(goal);
        }
    }

    private static void AppendTraitGoal(
        StringBuilder builder,
        ImplementsGoal goal,
        ref CanonicalizationContext context)
    {
        builder.Append("implements(");
        context.AppendType(builder, goal.Type);
        builder.Append(',');
        if (goal.Trait.IsValid)
        {
            builder.Append(goal.Trait.Value.ToString(CultureInfo.InvariantCulture));
        }
        else
        {
            builder.Append(goal.TraitName);
        }

        builder.Append(",[");
        for (var index = 0; index < goal.TraitArgs.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }
            context.AppendType(builder, goal.TraitArgs[index]);
        }

        builder.Append("],[");
        for (var index = 0; index < goal.TraitArgKeys.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(',');
            }
            builder.Append(goal.TraitArgKeys[index]);
        }
        builder.Append("])");
    }

    private ref struct CanonicalizationContext
    {
        private readonly Substitution _substitution;
        private readonly bool _tracksGoalCycles;
        private Dictionary<int, int>? _typeVariables;
        private Dictionary<int, int>? _constructorVariables;
        private Dictionary<int, int>? _effectVariables;
        private Dictionary<ObligationGoal, int>? _goalSlots;
        private HashSet<ObligationGoal>? _activeGoals;

        public CanonicalizationContext(Substitution substitution, bool tracksGoalCycles)
        {
            _substitution = substitution;
            _tracksGoalCycles = tracksGoalCycles;
            _typeVariables = null;
            _constructorVariables = null;
            _effectVariables = null;
            _goalSlots = null;
            _activeGoals = null;
        }

        public void AppendInferenceContextIdentity(StringBuilder builder)
        {
            builder.Append(";ctx=t[");
            AppendContext(builder, _typeVariables);
            builder.Append("],c[");
            AppendContext(builder, _constructorVariables);
            builder.Append("],e[");
            AppendContext(builder, _effectVariables);
            builder.Append(']');
        }

        public void AppendType(StringBuilder builder, Type type)
        {
            var resolved = _substitution.Apply(type);
            switch (resolved)
            {
                case TyVar variable:
                    builder.Append('?').Append(Slot(ref _typeVariables, variable.Index));
                    break;
                case TyCon constructor:
                    AppendConstructor(builder, constructor);
                    break;
                case TyFun function:
                    AppendFunction(builder, function);
                    break;
                case TyTuple tuple:
                    builder.Append("tuple(");
                    AppendTypes(builder, tuple.Elements);
                    builder.Append(')');
                    break;
                case TyRef reference:
                    builder.Append("ref(");
                    AppendType(builder, reference.Inner);
                    builder.Append(')');
                    break;
                case TyMutRef reference:
                    builder.Append("mref(");
                    AppendType(builder, reference.Inner);
                    builder.Append(')');
                    break;
                case TyShared shared:
                    builder.Append("shared(");
                    AppendType(builder, shared.Inner);
                    builder.Append(')');
                    break;
                case TyReflProof proof:
                    builder.Append("refl(");
                    if (proof.WitnessType != null)
                    {
                        AppendType(builder, proof.WitnessType);
                    }
                    builder.Append(')');
                    break;
                case EffectRow row:
                    AppendEffect(builder, row);
                    break;
                default:
                    builder.Append(resolved.ToString() ?? resolved.GetType().Name);
                    break;
            }
        }

        public bool TryEnterGoal(ObligationGoal goal, out int cycleSlot)
        {
            cycleSlot = -1;
            if (!_tracksGoalCycles)
            {
                return true;
            }

            _goalSlots ??= new Dictionary<ObligationGoal, int>(ReferenceEqualityComparer.Instance);
            _activeGoals ??= new HashSet<ObligationGoal>(ReferenceEqualityComparer.Instance);
            if (!_goalSlots.TryGetValue(goal, out var slot))
            {
                slot = _goalSlots.Count;
                _goalSlots[goal] = slot;
            }

            cycleSlot = slot;
            return _activeGoals.Add(goal);
        }

        public void ExitGoal(ObligationGoal goal)
        {
            if (_tracksGoalCycles)
            {
                _activeGoals!.Remove(goal);
            }
        }

        private void AppendConstructor(StringBuilder builder, TyCon constructor)
        {
            builder.Append("con(");
            if (constructor.Symbol.IsValid)
            {
                builder.Append('#').Append(constructor.Symbol.Value.ToString(CultureInfo.InvariantCulture));
            }
            else if (constructor.ConstructorVarIndex is { } constructorVariable)
            {
                builder.Append('?').Append(Slot(ref _constructorVariables, constructorVariable));
            }
            else
            {
                builder.Append(constructor.Name);
            }

            builder.Append(";t=[");
            AppendTypes(builder, constructor.Args);
            builder.Append("];v=[");
            var valueIndex = 0;
            foreach (var argument in constructor.ValueArgs.OrderBy(static argument => argument.ParameterIndex))
            {
                if (valueIndex++ > 0)
                {
                    builder.Append(',');
                }
                builder.Append(argument.ParameterIndex).Append(':')
                    .Append(argument.CanonicalHash).Append(':')
                    .Append(argument.ReferencedParameterIndex).Append(':')
                    .Append(argument.ValueVariableIndex);
            }

            builder.Append("];e=[");
            var effectIndex = 0;
            foreach (var argument in constructor.EffectArgs.OrderBy(static argument => argument.ParameterIndex))
            {
                if (effectIndex++ > 0)
                {
                    builder.Append(',');
                }
                builder.Append(argument.ParameterIndex).Append(':');
                AppendType(builder, argument.Argument);
            }
            builder.Append("])");
        }

        private void AppendFunction(StringBuilder builder, TyFun function)
        {
            builder.Append("fn([");
            AppendTypes(builder, function.Params);
            builder.Append("],");
            AppendType(builder, function.Result);
            builder.Append(',');
            AppendEffect(builder, function.Effects);
            builder.Append(')');
        }

        private void AppendEffect(StringBuilder builder, EffectRow row)
        {
            builder.Append("effects([");
            var variableIndex = 0;
            foreach (var variable in row.Variables.OrderBy(static variable => variable.Id))
            {
                if (variableIndex++ > 0)
                {
                    builder.Append(',');
                }
                builder.Append('?').Append(Slot(ref _effectVariables, variable.Id));
            }

            builder.Append("],[");
            var effectIndex = 0;
            foreach (var effect in row.Effects.OrderBy(static effect => effect.Name, StringComparer.Ordinal))
            {
                if (effectIndex++ > 0)
                {
                    builder.Append(',');
                }
                builder.Append(effect);
            }
            builder.Append("])");
        }

        private void AppendTypes(StringBuilder builder, IReadOnlyList<Type> types)
        {
            for (var index = 0; index < types.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                AppendType(builder, types[index]);
            }
        }

        private static int Slot(ref Dictionary<int, int>? slots, int variable)
        {
            slots ??= [];
            if (slots.TryGetValue(variable, out var slot))
            {
                return slot;
            }

            slot = slots.Count;
            slots[variable] = slot;
            return slot;
        }

        private static void AppendContext(StringBuilder builder, Dictionary<int, int>? slots)
        {
            if (slots == null)
            {
                return;
            }

            var index = 0;
            foreach (var entry in slots.OrderBy(static entry => entry.Value))
            {
                if (index++ > 0)
                {
                    builder.Append(',');
                }
                builder.Append(entry.Key.ToString(CultureInfo.InvariantCulture));
            }
        }
    }
}
