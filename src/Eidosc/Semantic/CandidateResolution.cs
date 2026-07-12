using Eidosc.Symbols;

namespace Eidosc.Semantic;

[Flags]
internal enum CandidateResolutionChecks
{
    None = 0,
    CandidateCollection = 1 << 0,
    TypeFiltering = 1 << 1,
    AstPreview = 1 << 2,
    NameCheck = 1 << 3,
    TypeCheck = 1 << 4,
    Fingerprint = 1 << 5
}

internal readonly record struct CandidateResolution(
    SymbolId SelectedSymbolId,
    int CandidateCount,
    int ViableCandidateCount,
    int BestScore,
    bool IsResolved,
    CandidateResolutionChecks Checks)
{
    public static CandidateResolution NoMatch(
        int candidateCount,
        int viableCandidateCount,
        CandidateResolutionChecks checks = CandidateResolutionChecks.None) =>
        new(SymbolId.None, candidateCount, viableCandidateCount, int.MinValue, IsResolved: false, checks);

    public static CandidateResolution Resolved(
        SymbolId selectedSymbolId,
        int candidateCount,
        int viableCandidateCount,
        int bestScore,
        CandidateResolutionChecks checks = CandidateResolutionChecks.None) =>
        new(selectedSymbolId, candidateCount, viableCandidateCount, bestScore, IsResolved: true, checks);

    public static CandidateResolution ResolvedWithoutSymbol(
        int candidateCount,
        int viableCandidateCount,
        int bestScore = 0,
        CandidateResolutionChecks checks = CandidateResolutionChecks.None) =>
        new(SymbolId.None, candidateCount, viableCandidateCount, bestScore, IsResolved: true, checks);
}
