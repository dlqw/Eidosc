using Eidosc.Mir;

namespace Eidosc.Borrow;

internal sealed class BorrowPropagationBinding<TBorrow>
    where TBorrow : class
{
    public BorrowTarget BorrowTarget { get; init; }
    public LocalId Borrowee => BorrowTarget.BaseLocal;
    public bool IsMutable { get; init; }
    public TBorrow? SourceBorrow { get; init; }
}

internal static class BorrowPropagationAnalysis
{
    public static List<BorrowPropagationBinding<TBorrow>> CollectLoadBindings<TBorrow>(
        LocalId sourceLocal,
        Func<LocalId, List<TBorrow>> getBorrowsByBorrower,
        Func<TBorrow, BorrowTarget> getBorrowTarget)
        where TBorrow : class
    {
        var sourceBorrows = getBorrowsByBorrower(sourceLocal);
        if (sourceBorrows.Count == 0)
        {
            return
            [
                new BorrowPropagationBinding<TBorrow>
                {
                    BorrowTarget = BorrowTarget.ForLocal(sourceLocal),
                    IsMutable = false,
                    SourceBorrow = null
                }
            ];
        }

        var result = new List<BorrowPropagationBinding<TBorrow>>(sourceBorrows.Count);
        foreach (var sourceBorrow in sourceBorrows)
        {
            result.Add(new BorrowPropagationBinding<TBorrow>
            {
                BorrowTarget = getBorrowTarget(sourceBorrow),
                IsMutable = false,
                SourceBorrow = sourceBorrow
            });
        }

        return result;
    }

    public static List<BorrowPropagationBinding<TBorrow>> CollectTransferBindings<TBorrow>(
        LocalId sourceLocal,
        Func<LocalId, List<TBorrow>> getBorrowsByBorrower,
        Func<TBorrow, BorrowTarget> getBorrowTarget,
        Func<TBorrow, bool> getIsMutable)
        where TBorrow : class
    {
        var sourceBorrows = getBorrowsByBorrower(sourceLocal);
        if (sourceBorrows.Count == 0)
        {
            return [];
        }

        var result = new List<BorrowPropagationBinding<TBorrow>>(sourceBorrows.Count);
        foreach (var sourceBorrow in sourceBorrows)
        {
            result.Add(new BorrowPropagationBinding<TBorrow>
            {
                BorrowTarget = getBorrowTarget(sourceBorrow),
                IsMutable = getIsMutable(sourceBorrow),
                SourceBorrow = sourceBorrow
            });
        }

        return result;
    }
}
