using Eidosc.Utils;

namespace Eidosc.Hir;

public enum HirDecisionSourceKind
{
    If,
    Match,
    Selection,
    IfLet,
    WhileLet,
    FunctionPattern,
    Generated
}

public sealed record HirDecisionPlan
{
    public HirDecisionSourceKind SourceKind { get; init; }

    public int BranchCount { get; init; }

    public bool HasGuards { get; init; }

    public bool HasBindings { get; init; }

    public bool IsExhaustive { get; init; }

    public SourceSpan Span { get; init; }

    public static HirDecisionPlan ForIf(HirIf expression) => new()
    {
        SourceKind = HirDecisionSourceKind.If,
        BranchCount = expression.ElseBranch == null ? 1 : 2,
        IsExhaustive = expression.ElseBranch != null,
        Span = expression.Span
    };

    public static HirDecisionPlan ForMatch(
        HirMatch expression,
        HirDecisionSourceKind sourceKind) => new()
        {
            SourceKind = sourceKind,
            BranchCount = expression.Branches.Count,
            HasGuards = expression.Branches.Any(static branch => branch.Guard != null),
            HasBindings = expression.Branches.Any(static branch => PatternBindsValue(branch.Pattern)),
            IsExhaustive = expression.IsExhaustive,
            Span = expression.Span
        };

    private static bool PatternBindsValue(HirPattern pattern) => pattern switch
    {
        HirVarPattern variable => !variable.IsWildcard,
        HirCtorPattern constructor => constructor.Fields.Any(static field => PatternBindsValue(field.Pattern)),
        HirTuplePattern tuple => tuple.Elements.Any(PatternBindsValue),
        HirListPattern list => list.Elements.Any(PatternBindsValue) ||
                               list.SuffixElements.Any(PatternBindsValue) ||
                               list.RestPattern != null && PatternBindsValue(list.RestPattern),
        HirOrPattern orPattern => PatternBindsValue(orPattern.Left) || PatternBindsValue(orPattern.Right),
        HirAndPattern andPattern => PatternBindsValue(andPattern.Left) || PatternBindsValue(andPattern.Right),
        HirNotPattern notPattern => PatternBindsValue(notPattern.InnerPattern),
        HirViewPattern viewPattern => PatternBindsValue(viewPattern.InnerPattern),
        HirAsPattern => true,
        _ => false
    };
}
