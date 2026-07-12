using Eidosc.Borrow;
using Eidosc.Mir;

namespace Eidosc.Pipeline;

public sealed record BorrowDiagnosticSnapshot(
    string SchemaVersion,
    string MirModuleFingerprint,
    string BorrowDependencyHash,
    IReadOnlyList<BorrowDiagnosticFunctionSnapshot> Functions)
{
    public const string CurrentSchemaVersion = "borrow-diagnostic-snapshot-v2";

    public static BorrowDiagnosticSnapshot Create(
        MirFunctionFingerprintSnapshot mirFingerprints,
        string borrowDependencyHash,
        ModuleBorrowCheckResult result)
    {
        var identity = BorrowSnapshotFunctionIdentity.Create(mirFingerprints);
        var functions = result.ResultsByFunctionKey
            .Select(pair =>
            {
                var stableKey = identity.ResolveFunctionKey(pair.Value, pair.Key);
                var bodyHash = identity.ResolveBodyHash(pair.Value, stableKey);
                return BorrowDiagnosticFunctionSnapshot.FromResult(
                    stableKey,
                    bodyHash ?? "",
                    pair.Value);
            })
            .OrderBy(static function => function.FunctionKey, StringComparer.Ordinal)
            .ToArray();

        return new BorrowDiagnosticSnapshot(
            CurrentSchemaVersion,
            mirFingerprints.ModuleFingerprint,
            borrowDependencyHash,
            functions);
    }
}

public sealed record BorrowDiagnosticFunctionSnapshot(
    string FunctionKey,
    string BodyHash,
    int AffineDiagnostics,
    int BorrowDiagnostics,
    int LoanSignatureDiagnostics,
    int LoanVerifierDiagnostics,
    int LoanConstraintFailures,
    IReadOnlyList<BorrowDiagnosticEntrySnapshot> Diagnostics)
{
    public static BorrowDiagnosticFunctionSnapshot FromResult(
        string functionKey,
        string bodyHash,
        BorrowCheckResult result)
    {
        var entries = new List<BorrowDiagnosticEntrySnapshot>();
        if (result.AffineTypeChecker != null)
        {
            entries.AddRange(result.AffineTypeChecker.Diagnostics.Select(static diagnostic =>
                BorrowDiagnosticEntrySnapshot.FromAffine(diagnostic)));
        }

        if (result.BorrowChecker != null)
        {
            entries.AddRange(result.BorrowChecker.Diagnostics.Select(static diagnostic =>
                BorrowDiagnosticEntrySnapshot.FromBorrow("borrow", diagnostic)));
        }

        if (result.LoanConstraintVerifier != null)
        {
            entries.AddRange(result.LoanConstraintVerifier.Diagnostics.Select(static diagnostic =>
                BorrowDiagnosticEntrySnapshot.FromBorrow("loan-verifier", diagnostic)));
        }

        return new BorrowDiagnosticFunctionSnapshot(
            functionKey,
            bodyHash,
            result.AffineTypeChecker?.Diagnostics.Count ?? 0,
            result.BorrowChecker?.Diagnostics.Count ?? 0,
            0,
            result.LoanConstraintVerifier?.Diagnostics.Count ?? 0,
            result.LoanConstraintResults.Count(static loan => !loan.IsValid),
            entries);
    }
}

public sealed record BorrowDiagnosticEntrySnapshot(
    string Source,
    string Kind,
    string Message,
    int Block,
    int InstructionIndex,
    int RelatedBlock,
    int RelatedInstructionIndex)
{
    public static BorrowDiagnosticEntrySnapshot FromAffine(AffineDiagnostic diagnostic) =>
        new(
            "affine",
            diagnostic.Kind.ToString(),
            diagnostic.Message,
            diagnostic.FirstLocation.Block.Value,
            diagnostic.FirstLocation.Index,
            diagnostic.SecondLocation.Block.Value,
            diagnostic.SecondLocation.Index);

    public static BorrowDiagnosticEntrySnapshot FromBorrow(string source, BorrowDiagnostic diagnostic) =>
        new(
            source,
            diagnostic.Kind.ToString(),
            diagnostic.Message,
            diagnostic.Location.Block.Value,
            diagnostic.Location.Index,
            diagnostic.RelatedLocation.Block.Value,
            diagnostic.RelatedLocation.Index);
}
