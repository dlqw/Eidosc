using Eidosc.Ast;
using Eidosc.Ast.Expressions;

namespace Eidosc.Utilities;

internal enum BorrowablePlaceKind
{
    None,
    Direct,
    Projection
}

internal readonly record struct BorrowablePlaceClassification(
    BorrowablePlaceKind Kind,
    bool IsBorrowable)
{
    public static BorrowablePlaceClassification None => new(BorrowablePlaceKind.None, false);
    public static BorrowablePlaceClassification Direct => new(BorrowablePlaceKind.Direct, true);
    public static BorrowablePlaceClassification Projection => new(BorrowablePlaceKind.Projection, true);
}

internal static class BorrowablePlaceClassifier
{
    public static BorrowablePlaceClassification Classify(EidosAstNode? expr)
    {
        if (expr == null)
        {
            return BorrowablePlaceClassification.None;
        }

        return expr switch
        {
            IdentifierExpr => BorrowablePlaceClassification.Direct,
            UnaryExpr { Operator: UnaryOp.Deref, Operand: not null } => BorrowablePlaceClassification.Direct,
            IndexExpr { IsTypeApplication: false, Object: not null, Index: not null } index
                when Classify(index.Object).IsBorrowable => BorrowablePlaceClassification.Projection,
            MethodCallExpr
                {
                    ResolvedAsFieldAccess: true,
                    Receiver: not null,
                    HasExplicitCallSyntax: false,
                    PositionalArgs.Count: 0,
                    NamedArgs.Count: 0
                } fieldAccess
                when Classify(fieldAccess.Receiver).IsBorrowable => BorrowablePlaceClassification.Projection,
            _ => BorrowablePlaceClassification.None
        };
    }

    public static bool IsBorrowable(EidosAstNode? expr)
    {
        return Classify(expr).IsBorrowable;
    }
}
