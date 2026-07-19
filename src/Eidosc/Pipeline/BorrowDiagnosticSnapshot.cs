using Eidosc.Borrow;
using Eidosc.Mir;

namespace Eidosc.Pipeline;

public sealed record BorrowDiagnosticSnapshot(
    string SchemaVersion,
    string MirModuleFingerprint,
    string BorrowDependencyHash,
    IReadOnlyList<BorrowDiagnosticFunctionSnapshot> Functions)
{
    public const string CurrentSchemaVersion = "borrow-diagnostic-snapshot-v3";

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

    public ModuleBorrowCheckResult ToBorrowCheckResult()
    {
        var result = new ModuleBorrowCheckResult();
        foreach (var function in Functions)
        {
            var summary = function.LoanSummary?.Restore();
            result.AddResult(new BorrowCheckResult
            {
                FunctionName = summary?.FunctionName ?? function.FunctionKey,
                FunctionSymbolId = summary?.FunctionSymbol ?? SymbolId.None,
                LoanSignature = summary
            });
        }

        return result;
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
    public LoanSummarySnapshot? LoanSummary { get; init; }

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
            entries)
        {
            LoanSummary = result.LoanSignature == null
                ? null
                : LoanSummarySnapshot.FromSignature(result.LoanSignature)
        };
    }
}

public sealed record LoanSummarySnapshot(
    string SchemaVersion,
    string FunctionName,
    int FunctionSymbolId,
    MirStateOwnershipContractPayload OwnershipContract,
    IReadOnlyList<LoanLifetimeParamSnapshot> LifetimeParams,
    IReadOnlyList<LoanParameterRequirementSnapshot> Parameters,
    LoanReturnConstraintSnapshot ReturnConstraint,
    IReadOnlyList<LoanLifetimeConstraintSnapshot> LifetimeConstraints,
    SourceSpanPayload Span)
{
    public const string CurrentSchemaVersion = "loan-summary-v1";

    public static LoanSummarySnapshot FromSignature(LoanSignature signature) =>
        new(
            CurrentSchemaVersion,
            signature.FunctionName,
            signature.FunctionSymbol.Value,
            MirStateOwnershipContractPayload.Create(signature.OwnershipContract),
            signature.LifetimeParams.Select(LoanLifetimeParamSnapshot.FromParam).ToArray(),
            signature.ParamRequirements.Select(LoanParameterRequirementSnapshot.FromRequirement).ToArray(),
            LoanReturnConstraintSnapshot.FromConstraint(signature.ReturnConstraint),
            signature.LifetimeConstraints.Select(LoanLifetimeConstraintSnapshot.FromConstraint).ToArray(),
            SourceSpanPayload.Create(signature.Span));

    public LoanSignature Restore()
    {
        if (!string.Equals(SchemaVersion, CurrentSchemaVersion, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Unsupported loan summary schema '{SchemaVersion}'.");
        }

        return new LoanSignature
        {
            OwnershipContract = OwnershipContract.Restore(),
            FunctionName = FunctionName,
            FunctionSymbol = new SymbolId(FunctionSymbolId),
            LifetimeParams = LifetimeParams.Select(static parameter => parameter.Restore()).ToList(),
            ParamRequirements = Parameters.Select(static parameter => parameter.Restore()).ToList(),
            ReturnConstraint = ReturnConstraint.Restore(),
            LifetimeConstraints = LifetimeConstraints.Select(static constraint => constraint.Restore()).ToList(),
            Span = Span.ToSourceSpan()
        };
    }
}

public sealed record LoanLifetimeParamSnapshot(
    int Id,
    string Name,
    IReadOnlyList<int> Outlives,
    SourceSpanPayload Span)
{
    public static LoanLifetimeParamSnapshot FromParam(LifetimeParam parameter) =>
        new(
            parameter.Id.Value,
            parameter.Name,
            parameter.Outlives.Select(static lifetime => lifetime.Value).ToArray(),
            SourceSpanPayload.Create(parameter.Span));

    public LifetimeParam Restore() =>
        new()
        {
            Id = new LifetimeId { Value = Id },
            Name = Name,
            Outlives = Outlives.Select(static lifetime => new LifetimeId { Value = lifetime }).ToList(),
            Span = Span.ToSourceSpan()
        };
}

public sealed record LoanParameterRequirementSnapshot(
    int ParamIndex,
    string Name,
    string Mode,
    int Lifetime,
    SourceSpanPayload Span)
{
    public static LoanParameterRequirementSnapshot FromRequirement(ParamBorrowRequirement requirement) =>
        new(
            requirement.ParamIndex,
            requirement.Name,
            requirement.Mode.ToString(),
            requirement.Lifetime.Value,
            SourceSpanPayload.Create(requirement.Span));

    public ParamBorrowRequirement Restore() =>
        new()
        {
            ParamIndex = ParamIndex,
            Name = Name,
            Mode = Enum.Parse<ParamBorrowMode>(Mode),
            Lifetime = new LifetimeId { Value = Lifetime },
            Span = Span.ToSourceSpan()
        };
}

public sealed record LoanReturnConstraintSnapshot(
    bool IsBorrow,
    bool IsMutable,
    int Lifetime,
    IReadOnlyList<int> BoundToParams,
    string Confidence,
    IReadOnlyList<string> InternalNotes,
    SourceSpanPayload Span)
{
    public static LoanReturnConstraintSnapshot FromConstraint(ReturnBorrowConstraint constraint) =>
        new(
            constraint.IsBorrow,
            constraint.IsMutable,
            constraint.Lifetime.Value,
            constraint.BoundToParams.ToArray(),
            constraint.Confidence.ToString(),
            constraint.InternalNotes.ToArray(),
            SourceSpanPayload.Create(constraint.Span));

    public ReturnBorrowConstraint Restore() =>
        new()
        {
            IsBorrow = IsBorrow,
            IsMutable = IsMutable,
            Lifetime = new LifetimeId { Value = Lifetime },
            BoundToParams = BoundToParams.ToList(),
            Confidence = Enum.Parse<LoanInferenceConfidence>(Confidence),
            InternalNotes = InternalNotes.ToList(),
            Span = Span.ToSourceSpan()
        };
}

public sealed record LoanLifetimeConstraintSnapshot(int Sub, int Sup, SourceSpanPayload Span)
{
    public static LoanLifetimeConstraintSnapshot FromConstraint(LifetimeConstraint constraint) =>
        new(constraint.Sub.Value, constraint.Sup.Value, SourceSpanPayload.Create(constraint.Span));

    public LifetimeConstraint Restore() =>
        new()
        {
            Sub = new LifetimeId { Value = Sub },
            Sup = new LifetimeId { Value = Sup },
            Span = Span.ToSourceSpan()
        };
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
