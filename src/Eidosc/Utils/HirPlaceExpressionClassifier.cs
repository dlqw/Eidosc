using Eidosc.Hir;

namespace Eidosc.Utilities;

internal enum HirPlaceExpressionKind
{
    None,
    Direct,
    Projection
}

internal readonly record struct HirPlaceExpressionClassification(
    HirPlaceExpressionKind Kind,
    bool IsPlaceShaped)
{
    public static HirPlaceExpressionClassification None => new(HirPlaceExpressionKind.None, false);
    public static HirPlaceExpressionClassification Direct => new(HirPlaceExpressionKind.Direct, true);
    public static HirPlaceExpressionClassification Projection => new(HirPlaceExpressionKind.Projection, true);
}

internal static class HirPlaceExpressionClassifier
{
    public static HirPlaceExpressionClassification Classify(HirNode? expr)
    {
        if (expr == null)
        {
            return HirPlaceExpressionClassification.None;
        }

        return expr switch
        {
            HirVar => HirPlaceExpressionClassification.Direct,
            HirUnaryOp { Operator: UnaryOp.Deref, Operand: not null } => HirPlaceExpressionClassification.Direct,
            HirFieldAccess { Target: not null } fieldAccess
                when Classify(fieldAccess.Target).IsPlaceShaped => HirPlaceExpressionClassification.Projection,
            HirIndexAccess { Target: not null, Index: not null } indexAccess
                when Classify(indexAccess.Target).IsPlaceShaped => HirPlaceExpressionClassification.Projection,
            _ => HirPlaceExpressionClassification.None
        };
    }

    public static bool IsPlaceShaped(HirNode? expr)
    {
        return Classify(expr).IsPlaceShaped;
    }
}
