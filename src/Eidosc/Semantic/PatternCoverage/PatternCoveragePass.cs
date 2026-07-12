using Eidosc.Symbols;
using Eidosc.Ast.Patterns;
using Eidosc.Utils;

namespace Eidosc.Semantic.PatternCoverage;

internal sealed record PatternCoverageRequest(
    IReadOnlyList<PatternBranch> Branches,
    SourceSpan OwnerSpan,
    string OwnerDescription,
    string? GuardSubjectName);

internal sealed class PatternCoveragePass
{
    private readonly Func<IReadOnlyList<PatternBranch>, SourceSpan, string, string?, bool> _analyze;

    public PatternCoveragePass(Func<IReadOnlyList<PatternBranch>, SourceSpan, string, string?, bool> analyze)
    {
        _analyze = analyze;
    }

    public bool Analyze(PatternCoverageRequest request)
    {
        return _analyze(
            request.Branches,
            request.OwnerSpan,
            request.OwnerDescription,
            request.GuardSubjectName);
    }
}
