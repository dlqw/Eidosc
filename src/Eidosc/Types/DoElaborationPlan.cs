using Eidosc.Symbols;
using Eidosc.Ast.Expressions;
using Eidosc.ProjectSystem;
using Eidosc.Utils;

namespace Eidosc.Types;

public enum DoElaborationStrategy
{
    Monad,
    ApplicativeThenJoin
}

public sealed record DoElaborationSegment(
    int StartIndex,
    int Count,
    DoElaborationStrategy Strategy,
    string ReasonCode);

public sealed record DoElaborationEvidence(
    string TraitName,
    SymbolId Trait,
    string CanonicalGoal,
    string InstanceIdentity,
    bool IsBuiltin,
    bool IsSupertrait);

public sealed record DoElaborationStep(
    int BindingIndex,
    DoBindingKind Kind,
    string InputTypeIdentity,
    string OutputTypeIdentity,
    string EffectIdentity);

public sealed record DoDependencyEdge(
    int ProducerBindingIndex,
    int ConsumerBindingIndex,
    SymbolId Symbol);

public sealed record DoElaborationPlan(
    string TypeConstructorIdentity,
    Type TypeConstructor,
    SymbolId MonadTrait,
    SymbolId FunctorTrait,
    SymbolId ApplicativeTrait,
    SymbolId AlternativeTrait,
    int ElementTypeArgumentIndex,
    IReadOnlyList<DoElaborationEvidence> Evidence,
    IReadOnlyList<DoElaborationStep> Steps,
    IReadOnlyList<DoDependencyEdge> DependencyEdges,
    IReadOnlyList<DoElaborationSegment> Segments,
    IReadOnlySet<int> RefutableBindingIndices,
    bool HasRefutablePattern,
    bool HasApplicativeEvidence,
    string Fingerprint)
{
    public bool IsCurrent(DoExpr expression) =>
        string.Equals(Fingerprint, DoElaborationPlanFingerprint.Create(expression), StringComparison.Ordinal);
}

internal sealed record DoElaborationDraft(
    Type TypeConstructor,
    SymbolId MonadTrait,
    SymbolId AlternativeTrait,
    bool HasRefutablePattern,
    int ElementTypeArgumentIndex);

internal static class DoElaborationPlanFingerprint
{
    public static string Create(DoExpr expression)
    {
        var builder = new System.Text.StringBuilder("do-plan-v1");
        builder.Append('|').Append(expression.Bindings.Count);
        for (var index = 0; index < expression.Bindings.Count; index++)
        {
            var binding = expression.Bindings[index];
            builder.Append('|').Append(index)
                .Append(':').Append((int)binding.Kind)
                .Append(':').Append(binding.SymbolId.Value)
                .Append(':').Append(binding.Span.FilePath)
                .Append(':').Append(binding.Span.Position)
                .Append(':').Append(binding.Pattern?.InferredType?.ToString() ?? "-")
                .Append(':').Append(binding.Value?.InferredType?.ToString() ?? "-")
                .Append(':').Append(binding.Value?.InferredEffects?.ToString() ?? "-");
        }

        return ContentHash.ComputeHash(builder.ToString());
    }
}
