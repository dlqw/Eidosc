using Eidosc.Symbols;
using Eidosc.Ast.Patterns;
using Eidosc.Utils;

namespace Eidosc.Semantic.PatternCoverage;

internal enum PatternCoverageTargetKind
{
    None,
    Bool,
    Scalar,
    TupleBool,
    TupleAdt,
    List,
    Adt,
    Generic
}

internal enum PatternUnreachableKind
{
    ShadowedByIrrefutable,
    ConstantFalseGuard,
    EmptyFiniteCaseSet,
    CoveredByPreviousFiniteCases
}

internal enum PatternWitnessKind
{
    BoolLiteral,
    ScalarLiteral,
    TupleBool,
    TupleConstructor,
    ListShape,
    Constructor,
    Wildcard
}

internal enum PatternWitnessTraceProvenance
{
    Exact,
    LowerBound
}

internal sealed record PatternWitness(
    PatternWitnessKind Kind,
    string DisplayText,
    string? StableKey = null);

internal sealed record PatternWitnessTrace(
    PatternWitness Witness,
    int CoveringBranchIndex,
    PatternWitnessTraceProvenance Provenance = PatternWitnessTraceProvenance.Exact);

internal readonly record struct ListCoverageCase(
    bool IsAtLeast,
    int Length,
    string? BoolVectorKey = null);

internal enum ScalarCoverageKind
{
    None,
    Int,
    Float,
    String,
    Char
}

internal readonly record struct ScalarCoverageCase(
    string Key,
    string DisplayText,
    bool IsOther = false);

internal sealed record PatternUsefulnessBranchFact(
    int BranchIndex,
    SourceSpan Span,
    SourceSpan GuardSpan,
    Pattern Pattern,
    bool IsIrrefutable,
    bool IsGuarded,
    bool? GuardConstant,
    HashSet<bool> BoolCoverageCases,
    bool HasExactBoolCoverage,
    HashSet<string> TupleBoolCoverageCases,
    bool HasExactTupleBoolCoverage,
    int TupleBoolArity,
    HashSet<ListCoverageCase> ListCoverageCases,
    bool HasExactListCoverage,
    HashSet<SymbolId> AdtCoverageConstructors,
    bool HasExactAdtCoverage,
    SymbolId AdtCoverageAdt);

internal sealed record PatternUnreachableBranch(
    SourceSpan Span,
    int BranchIndex,
    PatternUnreachableKind Kind,
    int PreviousIrrefutableBranchIndex = -1,
    IReadOnlyList<int>? CoveringBranchIndices = null,
    IReadOnlyList<PatternWitness>? CoveringWitnesses = null,
    IReadOnlyList<PatternWitnessTrace>? CoveringWitnessTraces = null);

internal sealed record PatternUsefulnessSummary(
    bool IsExhaustive,
    bool HasGuardedBranches,
    PatternCoverageTargetKind CoverageTarget,
    IReadOnlyList<string> MissingCases,
    IReadOnlyList<PatternWitness> MissingWitnesses,
    IReadOnlyList<PatternUnreachableBranch> UnreachableBranches);

internal static partial class PatternUsefulnessAnalyzer
{
    private interface IPatternCoverageSpace
    {
        PatternCoverageObservation ObserveBranch(PatternUsefulnessBranchFact branch, bool isUnguarded, SymbolTable symbolTable);

        PatternUsefulnessSummary? BuildSummary(
            bool hasGuardedBranches,
            IReadOnlyList<PatternUnreachableBranch> unreachableBranches,
            SymbolTable symbolTable);
    }

    private readonly record struct PatternCoverageObservation(
        bool HasExactFiniteCases,
        bool AddsCoverage,
        bool IsUnsatisfiable,
        IReadOnlyList<int> CoveringBranchIndices,
        IReadOnlyList<PatternWitness> CoveringWitnesses,
        IReadOnlyList<PatternWitnessTrace> CoveringWitnessTraces)
    {
        public static PatternCoverageObservation NotApplicable =>
            new(
                HasExactFiniteCases: false,
                AddsCoverage: false,
                IsUnsatisfiable: false,
                CoveringBranchIndices: [],
                CoveringWitnesses: [],
                CoveringWitnessTraces: []);
    }

    private readonly record struct FiniteCoverageDelta(
        bool AddsCoverage,
        IReadOnlyList<int> CoveredByBranchIndices,
        IReadOnlyList<PatternWitness> CoveredWitnesses,
        IReadOnlyList<PatternWitnessTrace> CoveredWitnessTraces);

    private readonly record struct PatternSpecialization<TCase>(
        bool IsExactFiniteCases,
        IReadOnlyList<TCase> Cases,
        PatternWitnessTraceProvenance TraceProvenance = PatternWitnessTraceProvenance.Exact)
        where TCase : notnull;

    private readonly record struct PatternRemainder<TCase>(
        IReadOnlyList<TCase> MissingCases)
        where TCase : notnull;

    private sealed class FiniteCoverageTracker<TCase>(IEqualityComparer<TCase>? comparer = null)
        where TCase : notnull
    {
        private readonly record struct CoverageOrigin(
            int BranchIndex,
            PatternWitnessTraceProvenance Provenance);

        private readonly Dictionary<TCase, CoverageOrigin> _firstCoveredByBranch =
            new(comparer ?? EqualityComparer<TCase>.Default);

        public IEnumerable<TCase> CoveredCases => _firstCoveredByBranch.Keys;

        public int CoveredCount => _firstCoveredByBranch.Count;

        public bool Contains(TCase @case) => _firstCoveredByBranch.ContainsKey(@case);

        public FiniteCoverageDelta Observe(
            int branchIndex,
            IEnumerable<TCase> cases,
            PatternWitnessTraceProvenance provenance,
            Func<TCase, PatternWitness> witnessFactory)
        {
            var hasNewCoverage = false;
            var coveredByBranchIndices = new HashSet<int>();
            var coveredWitnesses = new List<PatternWitness>();
            var coveredWitnessTraces = new List<PatternWitnessTrace>();
            var seenInBranch = new HashSet<TCase>(_firstCoveredByBranch.Comparer);

            foreach (var @case in cases)
            {
                if (!seenInBranch.Add(@case))
                {
                    continue;
                }

                if (_firstCoveredByBranch.TryGetValue(@case, out var coveringOrigin))
                {
                    coveredByBranchIndices.Add(coveringOrigin.BranchIndex);
                    var witness = witnessFactory(@case);
                    coveredWitnesses.Add(witness);
                    coveredWitnessTraces.Add(new PatternWitnessTrace(
                        witness,
                        coveringOrigin.BranchIndex,
                        coveringOrigin.Provenance));
                    continue;
                }

                _firstCoveredByBranch[@case] = new CoverageOrigin(branchIndex, provenance);
                hasNewCoverage = true;
            }

            return new FiniteCoverageDelta(
                AddsCoverage: hasNewCoverage,
                CoveredByBranchIndices: coveredByBranchIndices
                    .OrderBy(index => index)
                    .ToList(),
                CoveredWitnesses: DistinctWitnesses(coveredWitnesses),
                CoveredWitnessTraces: DistinctWitnessTraces(coveredWitnessTraces));
        }
    }

    private sealed class FiniteCoverageKernel<TCase>(IEqualityComparer<TCase>? comparer = null)
        where TCase : notnull
    {
        private readonly FiniteCoverageTracker<TCase> _tracker = new(comparer);

        public int CoveredCount => _tracker.CoveredCount;

        public bool Contains(TCase @case) => _tracker.Contains(@case);

        public PatternCoverageObservation ObserveSpecialization(
            int branchIndex,
            PatternSpecialization<TCase> specialization,
            Func<TCase, PatternWitness> witnessFactory)
        {
            if (!specialization.IsExactFiniteCases)
            {
                return PatternCoverageObservation.NotApplicable;
            }

            if (specialization.Cases.Count == 0)
            {
                return new PatternCoverageObservation(
                    HasExactFiniteCases: true,
                    AddsCoverage: false,
                    IsUnsatisfiable: true,
                    CoveringBranchIndices: [],
                    CoveringWitnesses: [],
                    CoveringWitnessTraces: []);
            }

            var delta = _tracker.Observe(
                branchIndex,
                specialization.Cases,
                specialization.TraceProvenance,
                witnessFactory);
            return new PatternCoverageObservation(
                HasExactFiniteCases: true,
                AddsCoverage: delta.AddsCoverage,
                IsUnsatisfiable: false,
                CoveringBranchIndices: delta.CoveredByBranchIndices,
                CoveringWitnesses: delta.CoveredWitnesses,
                CoveringWitnessTraces: delta.CoveredWitnessTraces);
        }

        public PatternRemainder<TCase> ComputeRemainder(IEnumerable<TCase> universe)
        {
            var missingCases = universe
                .Where(@case => !_tracker.Contains(@case))
                .ToList();
            return new PatternRemainder<TCase>(missingCases);
        }
    }

    internal enum PatternSpecializationStatus
    {
        NotApplicable,
        ExactFinite,
        Untrackable
    }

    private abstract class FiniteCoverageSpace<TCase> : IPatternCoverageSpace
        where TCase : notnull
    {
        private readonly FiniteCoverageKernel<TCase> _coverage;
        private bool _trackable = true;
        private bool _sawFiniteSpecialization;

        protected FiniteCoverageSpace(IEqualityComparer<TCase>? comparer = null)
        {
            _coverage = new FiniteCoverageKernel<TCase>(comparer);
        }

        public PatternCoverageObservation ObserveBranch(
            PatternUsefulnessBranchFact branch,
            bool isUnguarded,
            SymbolTable symbolTable)
        {
            if (!_trackable)
            {
                return PatternCoverageObservation.NotApplicable;
            }

            if (!isUnguarded && !AllowSpecializationWhenGuarded)
            {
                return PatternCoverageObservation.NotApplicable;
            }

            var status = TrySpecializeBranch(branch, symbolTable, out var specialization);
            switch (status)
            {
                case PatternSpecializationStatus.NotApplicable:
                    return PatternCoverageObservation.NotApplicable;

                case PatternSpecializationStatus.Untrackable:
                    _trackable = false;
                    return PatternCoverageObservation.NotApplicable;

                case PatternSpecializationStatus.ExactFinite:
                    _sawFiniteSpecialization = true;
                    return _coverage.ObserveSpecialization(
                        branch.BranchIndex,
                        specialization,
                        @case => CreateMissingWitness(@case, symbolTable));

                default:
                    return PatternCoverageObservation.NotApplicable;
            }
        }

        public PatternUsefulnessSummary? BuildSummary(
            bool hasGuardedBranches,
            IReadOnlyList<PatternUnreachableBranch> unreachableBranches,
            SymbolTable symbolTable)
        {
            if (!_trackable || !_sawFiniteSpecialization)
            {
                return null;
            }

            if (!TryBuildUniverse(symbolTable, out var universe) || universe.Count == 0)
            {
                return null;
            }

            var remainder = _coverage.ComputeRemainder(universe);
            if (remainder.MissingCases.Count == 0)
            {
                return Exhaustive(hasGuardedBranches, unreachableBranches);
            }

            var missingWitnesses = remainder.MissingCases
                .Select(@case => CreateMissingWitness(@case, symbolTable))
                .ToList();
            var missingCases = remainder.MissingCases
                .Select(@case => FormatMissingCase(@case, symbolTable))
                .Where(caseText => !string.IsNullOrWhiteSpace(caseText))
                .ToList();
            if (missingCases.Count == 0)
            {
                missingCases = missingWitnesses
                    .Select(witness => witness.DisplayText)
                    .Where(caseText => !string.IsNullOrWhiteSpace(caseText))
                    .ToList();
            }

            return new PatternUsefulnessSummary(
                IsExhaustive: false,
                HasGuardedBranches: hasGuardedBranches,
                CoverageTarget: CoverageTarget,
                MissingCases: missingCases,
                MissingWitnesses: missingWitnesses,
                UnreachableBranches: unreachableBranches);
        }

        protected abstract PatternCoverageTargetKind CoverageTarget { get; }

        protected abstract PatternSpecializationStatus TrySpecializeBranch(
            PatternUsefulnessBranchFact branch,
            SymbolTable symbolTable,
            out PatternSpecialization<TCase> specialization);

        protected abstract bool TryBuildUniverse(SymbolTable symbolTable, out IReadOnlyList<TCase> universe);

        protected abstract string FormatMissingCase(TCase @case, SymbolTable symbolTable);

        protected abstract PatternWitness CreateMissingWitness(TCase @case, SymbolTable symbolTable);

        protected virtual bool AllowSpecializationWhenGuarded => false;
    }

    private sealed class BoolCoverageSpace : FiniteCoverageSpace<bool>
    {
        protected override PatternCoverageTargetKind CoverageTarget => PatternCoverageTargetKind.Bool;

        protected override bool AllowSpecializationWhenGuarded => true;

        protected override PatternSpecializationStatus TrySpecializeBranch(
            PatternUsefulnessBranchFact branch,
            SymbolTable symbolTable,
            out PatternSpecialization<bool> specialization)
        {
            if (!branch.HasExactBoolCoverage)
            {
                specialization = default;
                return PatternSpecializationStatus.NotApplicable;
            }

            specialization = new PatternSpecialization<bool>(
                IsExactFiniteCases: true,
                Cases: branch.BoolCoverageCases
                    .OrderByDescending(value => value)
                    .ToList());
            return PatternSpecializationStatus.ExactFinite;
        }

        protected override bool TryBuildUniverse(SymbolTable symbolTable, out IReadOnlyList<bool> universe)
        {
            universe = [true, false];
            return true;
        }

        protected override string FormatMissingCase(bool @case, SymbolTable symbolTable)
        {
            return @case ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False;
        }

        protected override PatternWitness CreateMissingWitness(bool @case, SymbolTable symbolTable)
        {
            return new PatternWitness(
                PatternWitnessKind.BoolLiteral,
                @case ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False,
                $"bool:{(@case ? WellKnownStrings.AdditionalKeywords.True : WellKnownStrings.AdditionalKeywords.False)}");
        }
    }

    private sealed class ScalarCoverageCaseComparer : IEqualityComparer<ScalarCoverageCase>
    {
        public static ScalarCoverageCaseComparer Instance { get; } = new();

        public bool Equals(ScalarCoverageCase x, ScalarCoverageCase y)
        {
            return string.Equals(x.Key, y.Key, StringComparison.Ordinal);
        }

        public int GetHashCode(ScalarCoverageCase obj)
        {
            return StringComparer.Ordinal.GetHashCode(obj.Key);
        }
    }

    private sealed class ScalarCoverageSpace : FiniteCoverageSpace<ScalarCoverageCase>
    {
        private readonly ScalarCoverageKind _kind;
        private readonly List<ScalarCoverageCase> _domainCases;

        public ScalarCoverageSpace(IReadOnlyList<PatternUsefulnessBranchFact> branches)
            : base(ScalarCoverageCaseComparer.Instance)
        {
            _kind = TryBuildScalarCoverageDomain(branches, out var resolvedKind, out var domainCases)
                ? resolvedKind
                : ScalarCoverageKind.None;
            _domainCases = domainCases;
        }

        protected override PatternCoverageTargetKind CoverageTarget => PatternCoverageTargetKind.Scalar;

        protected override PatternSpecializationStatus TrySpecializeBranch(
            PatternUsefulnessBranchFact branch,
            SymbolTable symbolTable,
            out PatternSpecialization<ScalarCoverageCase> specialization)
        {
            if (_kind == ScalarCoverageKind.None || _domainCases.Count == 0)
            {
                specialization = default;
                return PatternSpecializationStatus.NotApplicable;
            }

            if (!TryGetExactScalarCases(branch.Pattern, _kind, _domainCases, symbolTable, out var cases))
            {
                specialization = default;
                return PatternSpecializationStatus.NotApplicable;
            }

            specialization = new PatternSpecialization<ScalarCoverageCase>(
                IsExactFiniteCases: true,
                Cases: cases
                    .OrderBy(@case => @case.Key, StringComparer.Ordinal)
                    .ToList());
            return PatternSpecializationStatus.ExactFinite;
        }

        protected override bool TryBuildUniverse(SymbolTable symbolTable, out IReadOnlyList<ScalarCoverageCase> universe)
        {
            universe = _domainCases;
            return _kind != ScalarCoverageKind.None && _domainCases.Count > 0;
        }

        protected override string FormatMissingCase(ScalarCoverageCase @case, SymbolTable symbolTable)
        {
            return @case.DisplayText;
        }

        protected override PatternWitness CreateMissingWitness(ScalarCoverageCase @case, SymbolTable symbolTable)
        {
            return new PatternWitness(
                PatternWitnessKind.ScalarLiteral,
                @case.DisplayText,
                @case.Key);
        }
    }

    private sealed class TupleBoolCoverageSpace : FiniteCoverageSpace<string>
    {
        private int _arity = -1;

        public TupleBoolCoverageSpace() : base(StringComparer.Ordinal)
        {
        }

        protected override PatternCoverageTargetKind CoverageTarget => PatternCoverageTargetKind.TupleBool;

        protected override bool AllowSpecializationWhenGuarded => true;

        protected override PatternSpecializationStatus TrySpecializeBranch(
            PatternUsefulnessBranchFact branch,
            SymbolTable symbolTable,
            out PatternSpecialization<string> specialization)
        {
            if (branch.HasExactTupleBoolCoverage)
            {
                var branchArity = branch.TupleBoolArity;
                if (branchArity <= 0)
                {
                    specialization = default;
                    return PatternSpecializationStatus.Untrackable;
                }

                if (_arity < 0)
                {
                    _arity = branchArity;
                }
                else if (_arity != branchArity)
                {
                    specialization = default;
                    return PatternSpecializationStatus.Untrackable;
                }

                specialization = new PatternSpecialization<string>(
                    IsExactFiniteCases: true,
                    Cases: branch.TupleBoolCoverageCases
                        .OrderBy(value => value, StringComparer.Ordinal)
                        .ToList());
                return PatternSpecializationStatus.ExactFinite;
            }

            if (branch.IsGuarded && branch.GuardConstant == null)
            {
                if (branch.TupleBoolArity > 0 &&
                    branch.TupleBoolCoverageCases.Count > 0)
                {
                    if (_arity < 0)
                    {
                        _arity = branch.TupleBoolArity;
                    }
                    else if (_arity != branch.TupleBoolArity)
                    {
                        specialization = default;
                        return PatternSpecializationStatus.Untrackable;
                    }

                    // Guarded unresolved tuple-bool branches may still carry a
                    // provable lower-bound (guard=true on some tuple cases).
                    // Keep that lower-bound for covered diagnostics while the
                    // unresolved part remains conservative.
                    specialization = new PatternSpecialization<string>(
                        IsExactFiniteCases: true,
                        Cases: branch.TupleBoolCoverageCases
                            .OrderBy(value => value, StringComparer.Ordinal)
                            .ToList(),
                        TraceProvenance: PatternWitnessTraceProvenance.LowerBound);
                    return PatternSpecializationStatus.ExactFinite;
                }

                specialization = default;
                return PatternSpecializationStatus.NotApplicable;
            }

            var branchWitnesses = new HashSet<string>(StringComparer.Ordinal);
            if (!TryGetExactTupleBoolCases(branch.Pattern, out var arity, branchWitnesses))
            {
                specialization = default;
                return PatternSpecializationStatus.Untrackable;
            }

            if (_arity < 0)
            {
                _arity = arity;
            }
            else if (_arity != arity)
            {
                specialization = default;
                return PatternSpecializationStatus.Untrackable;
            }

            specialization = new PatternSpecialization<string>(
                IsExactFiniteCases: true,
                Cases: branchWitnesses
                    .OrderBy(value => value, StringComparer.Ordinal)
                    .ToList());
            return PatternSpecializationStatus.ExactFinite;
        }

        protected override bool TryBuildUniverse(SymbolTable symbolTable, out IReadOnlyList<string> universe)
        {
            if (_arity <= 0 || _arity > 6)
            {
                universe = [];
                return false;
            }

            universe = GenerateTupleBoolWitnesses(_arity);
            return true;
        }

        protected override string FormatMissingCase(string @case, SymbolTable symbolTable)
        {
            return @case;
        }

        protected override PatternWitness CreateMissingWitness(string @case, SymbolTable symbolTable)
        {
            return new PatternWitness(
                PatternWitnessKind.TupleBool,
                @case,
                $"tuple-bool:{@case}");
        }
    }

    private readonly record struct TupleAdtConstructorCase(
        string Key,
        IReadOnlyList<SymbolId> ConstructorIds);

    private sealed class TupleAdtConstructorCaseComparer : IEqualityComparer<TupleAdtConstructorCase>
    {
        public bool Equals(TupleAdtConstructorCase x, TupleAdtConstructorCase y)
        {
            return string.Equals(x.Key, y.Key, StringComparison.Ordinal);
        }

        public int GetHashCode(TupleAdtConstructorCase obj)
        {
            return StringComparer.Ordinal.GetHashCode(obj.Key);
        }
    }

    private sealed class TupleAdtCoverageSpace : FiniteCoverageSpace<TupleAdtConstructorCase>
    {
        private List<SymbolId> _elementAdts = [];

        public TupleAdtCoverageSpace(
            IReadOnlyList<PatternUsefulnessBranchFact> branches,
            SymbolTable symbolTable) : base(new TupleAdtConstructorCaseComparer())
        {
            _elementAdts = InferTupleAdtElementDomains(branches, symbolTable);
        }

        protected override PatternCoverageTargetKind CoverageTarget => PatternCoverageTargetKind.TupleAdt;

        protected override PatternSpecializationStatus TrySpecializeBranch(
            PatternUsefulnessBranchFact branch,
            SymbolTable symbolTable,
            out PatternSpecialization<TupleAdtConstructorCase> specialization)
        {
            if (branch.IsGuarded && branch.GuardConstant == null)
            {
                specialization = default;
                return PatternSpecializationStatus.NotApplicable;
            }

            var status = TryCollectExactTupleAdtCases(
                branch.Pattern,
                symbolTable,
                _elementAdts,
                out var branchElementAdts,
                out var branchCases);
            if (status is not PatternSpecializationStatus.ExactFinite)
            {
                specialization = default;
                return status;
            }

            if (!TryAdoptElementDomains(branchElementAdts))
            {
                specialization = default;
                return PatternSpecializationStatus.Untrackable;
            }

            specialization = new PatternSpecialization<TupleAdtConstructorCase>(
                IsExactFiniteCases: true,
                Cases: branchCases
                    .OrderBy(@case => @case.Key, StringComparer.Ordinal)
                    .ToList());
            return PatternSpecializationStatus.ExactFinite;
        }

        protected override bool TryBuildUniverse(SymbolTable symbolTable, out IReadOnlyList<TupleAdtConstructorCase> universe)
        {
            if (_elementAdts.Count == 0)
            {
                universe = [];
                return false;
            }

            var constructorDomains = new List<IReadOnlyList<SymbolId>>(_elementAdts.Count);
            foreach (var adtId in _elementAdts)
            {
                if (!AdtCoverageSpace.TryGetAdtConstructors(symbolTable, adtId, out var constructors))
                {
                    universe = [];
                    return false;
                }

                constructorDomains.Add(constructors);
            }

            var allCases = new List<TupleAdtConstructorCase>();
            BuildTupleAdtCasesRecursive(constructorDomains, 0, [], allCases);
            universe = allCases;
            return universe.Count > 0;
        }

        protected override string FormatMissingCase(TupleAdtConstructorCase @case, SymbolTable symbolTable)
        {
            return FormatTupleAdtCase(@case.ConstructorIds, symbolTable);
        }

        protected override PatternWitness CreateMissingWitness(TupleAdtConstructorCase @case, SymbolTable symbolTable)
        {
            return new PatternWitness(
                PatternWitnessKind.TupleConstructor,
                FormatTupleAdtCase(@case.ConstructorIds, symbolTable),
                $"tuple-adt:{@case.Key}");
        }

        private bool TryAdoptElementDomains(IReadOnlyList<SymbolId> candidateAdts)
        {
            if (candidateAdts.Count == 0 || candidateAdts.Any(adtId => !adtId.IsValid))
            {
                return false;
            }

            if (_elementAdts.Count == 0)
            {
                _elementAdts = candidateAdts.ToList();
                return true;
            }

            if (_elementAdts.Count != candidateAdts.Count)
            {
                return false;
            }

            for (var i = 0; i < _elementAdts.Count; i++)
            {
                if (_elementAdts[i] != candidateAdts[i])
                {
                    return false;
                }
            }

            return true;
        }

        private static List<SymbolId> InferTupleAdtElementDomains(
            IReadOnlyList<PatternUsefulnessBranchFact> branches,
            SymbolTable symbolTable)
        {
            var arity = -1;
            List<SymbolId>? domains = null;

            foreach (var branch in branches)
            {
                if (UnwrapTuplePattern(branch.Pattern) is not { } tuplePattern)
                {
                    continue;
                }

                if (arity < 0)
                {
                    arity = tuplePattern.Elements.Count;
                    domains = Enumerable.Repeat(SymbolId.None, arity).ToList();
                }
                else if (tuplePattern.Elements.Count != arity)
                {
                    return [];
                }

                for (var index = 0; index < tuplePattern.Elements.Count; index++)
                {
                    if (!TryInferElementAdtDomain(tuplePattern.Elements[index], symbolTable, domains![index], out var elementAdt))
                    {
                        continue;
                    }

                    if (!domains[index].IsValid)
                    {
                        domains[index] = elementAdt;
                    }
                    else if (domains[index] != elementAdt)
                    {
                        return [];
                    }
                }
            }

            return domains is { Count: > 0 } && domains.All(domain => domain.IsValid)
                ? domains
                : [];
        }

        private static TuplePattern? UnwrapTuplePattern(Pattern pattern)
        {
            while (pattern is AsPattern { InnerPattern: not null } asPattern)
            {
                pattern = asPattern.InnerPattern;
            }

            return pattern as TuplePattern;
        }

        private static bool TryInferElementAdtDomain(
            Pattern pattern,
            SymbolTable symbolTable,
            SymbolId preferredAdt,
            out SymbolId elementAdt)
        {
            elementAdt = SymbolId.None;
            var status = AdtCoverageSpace.TryCollectAdtConstructorCases(
                pattern,
                symbolTable,
                preferredAdt,
                out var resolvedAdt,
                out _);

            if (status is not PatternSpecializationStatus.ExactFinite || !resolvedAdt.IsValid)
            {
                return false;
            }

            elementAdt = resolvedAdt;
            return true;
        }
    }

    private const int MaxListBoolSplitLength = 6;
    private const int MaxListIntSplitLength = 2;
    private const int MaxListIntSplitDomainSize = 16;
    private const int MaxListIntSplitCaseCount = 512;
    private const string ListIntOtherToken = "i:*";
    private const int MaxListAdtSplitLength = MaxListBoolSplitLength;
    private const int MaxListAdtSplitDomainSize = 24;
    private const int MaxListAdtSplitCaseCount = 4096;

    private sealed class ListLengthCaseComparer : IComparer<ListCoverageCase>
    {
        public static ListLengthCaseComparer Instance { get; } = new();

        public int Compare(ListCoverageCase x, ListCoverageCase y)
        {
            var kindCompare = x.IsAtLeast.CompareTo(y.IsAtLeast);
            if (kindCompare != 0)
            {
                return kindCompare;
            }

            var lengthCompare = x.Length.CompareTo(y.Length);
            if (lengthCompare != 0)
            {
                return lengthCompare;
            }

            return string.Compare(x.BoolVectorKey, y.BoolVectorKey, StringComparison.Ordinal);
        }
    }

    private sealed class ListLengthCoverageSpace : FiniteCoverageSpace<ListCoverageCase>
    {
        private readonly int _maxTrackedLength;
        private readonly int _overflowStartLength;
        private readonly HashSet<int> _boolSplitLengths;
        private readonly bool _enableBoolElementSplits;
        private readonly Dictionary<int, IReadOnlyList<string>> _intSplitDomainsByLength;
        private readonly bool _enableIntElementSplits;
        private readonly IReadOnlyList<PatternUsefulnessBranchFact> _branches;
        private Dictionary<int, IReadOnlyList<string>> _adtSplitDomainsByLength = [];
        private bool _enableAdtElementSplits;
        private bool _adtElementSplitsInitialized;

        public ListLengthCoverageSpace(IReadOnlyList<PatternUsefulnessBranchFact> branches)
        {
            _branches = branches;
            _maxTrackedLength = DetermineMaxTrackedLength(branches);
            _overflowStartLength = _maxTrackedLength + 1;
            var hasIntrinsicSplits = TryDetermineListBoolSplitLengths(
                branches.Select(branch => branch.Pattern),
                _maxTrackedLength,
                out _boolSplitLengths);
            var explicitSplitLengths = CollectExplicitListSplitLengthsFromBranchFacts(branches, _maxTrackedLength);
            if (explicitSplitLengths.Count > 0)
            {
                _boolSplitLengths.UnionWith(explicitSplitLengths);
            }

            _enableBoolElementSplits = hasIntrinsicSplits || explicitSplitLengths.Count > 0;
            _enableIntElementSplits = TryDetermineListIntSplitDomains(
                branches.Select(branch => branch.Pattern),
                _maxTrackedLength,
                out _intSplitDomainsByLength);
        }

        protected override PatternCoverageTargetKind CoverageTarget => PatternCoverageTargetKind.List;
        protected override bool AllowSpecializationWhenGuarded => true;

        protected override PatternSpecializationStatus TrySpecializeBranch(
            PatternUsefulnessBranchFact branch,
            SymbolTable symbolTable,
            out PatternSpecialization<ListCoverageCase> specialization)
        {
            EnsureAdtElementSplitsInitialized(symbolTable);

            var coveredCases = new HashSet<ListCoverageCase>();
            if (branch.HasExactListCoverage)
            {
                if (!branch.IsGuarded)
                {
                    // Branch-local exact cases may be computed without the global
                    // split universe (for example a standalone view-pattern branch).
                    // Recompute against current split settings to avoid widening
                    // generic length cases into full split domains incorrectly.
                    if (!TryCollectExactListLengthCases(
                            branch.Pattern,
                            _maxTrackedLength,
                            _boolSplitLengths,
                            _enableBoolElementSplits,
                            _intSplitDomainsByLength,
                            _enableIntElementSplits,
                            _adtSplitDomainsByLength,
                            _enableAdtElementSplits,
                            symbolTable,
                            coveredCases))
                    {
                        specialization = default;
                        return PatternSpecializationStatus.NotApplicable;
                    }

                    specialization = new PatternSpecialization<ListCoverageCase>(
                        IsExactFiniteCases: true,
                        Cases: coveredCases
                            .OrderBy(@case => @case, ListLengthCaseComparer.Instance)
                            .ToList());
                    return PatternSpecializationStatus.ExactFinite;
                }

                var normalizedCases = NormalizeBranchExactListCases(branch.ListCoverageCases);
                specialization = new PatternSpecialization<ListCoverageCase>(
                    IsExactFiniteCases: true,
                    Cases: normalizedCases
                        .OrderBy(@case => @case, ListLengthCaseComparer.Instance)
                        .ToList());
                return PatternSpecializationStatus.ExactFinite;
            }

            if (branch.IsGuarded && branch.GuardConstant == null)
            {
                if (branch.ListCoverageCases.Count > 0)
                {
                    // Guarded branch facts may still contain a provable subset of
                    // finite cases where the guard is known true.
                    var normalizedLowerBoundCases = NormalizeBranchExactListCases(branch.ListCoverageCases);
                    if (normalizedLowerBoundCases.Count > 0)
                    {
                        specialization = new PatternSpecialization<ListCoverageCase>(
                            IsExactFiniteCases: true,
                            Cases: normalizedLowerBoundCases
                                .OrderBy(@case => @case, ListLengthCaseComparer.Instance)
                                .ToList(),
                            TraceProvenance: PatternWitnessTraceProvenance.LowerBound);
                        return PatternSpecializationStatus.ExactFinite;
                    }
                }

                specialization = default;
                return PatternSpecializationStatus.NotApplicable;
            }

            if (!TryCollectExactListLengthCases(
                    branch.Pattern,
                    _maxTrackedLength,
                    _boolSplitLengths,
                    _enableBoolElementSplits,
                    _intSplitDomainsByLength,
                    _enableIntElementSplits,
                    _adtSplitDomainsByLength,
                    _enableAdtElementSplits,
                    symbolTable,
                    coveredCases))
            {
                specialization = default;
                return PatternSpecializationStatus.NotApplicable;
            }

            specialization = new PatternSpecialization<ListCoverageCase>(
                IsExactFiniteCases: true,
                Cases: coveredCases
                    .OrderBy(@case => @case, ListLengthCaseComparer.Instance)
                    .ToList());
            return PatternSpecializationStatus.ExactFinite;
        }

        protected override bool TryBuildUniverse(SymbolTable symbolTable, out IReadOnlyList<ListCoverageCase> universe)
        {
            EnsureAdtElementSplitsInitialized(symbolTable);

            if (_maxTrackedLength < 0)
            {
                universe = [];
                return false;
            }

            universe = BuildListLengthUniverse(
                _maxTrackedLength,
                _boolSplitLengths,
                _enableBoolElementSplits,
                _intSplitDomainsByLength,
                _enableIntElementSplits,
                _adtSplitDomainsByLength,
                _enableAdtElementSplits,
                _overflowStartLength);
            return true;
        }

        protected override string FormatMissingCase(ListCoverageCase @case, SymbolTable symbolTable)
        {
            return FormatListLengthCaseDisplay(@case, symbolTable);
        }

        protected override PatternWitness CreateMissingWitness(ListCoverageCase @case, SymbolTable symbolTable)
        {
            return new PatternWitness(
                PatternWitnessKind.ListShape,
                FormatListLengthCaseDisplay(@case, symbolTable),
                GetListLengthCaseStableKey(@case));
        }

        private static int DetermineMaxTrackedLength(IReadOnlyList<PatternUsefulnessBranchFact> branches)
        {
            var maxLength = 0;
            var hasListShape = false;

            for (var i = 0; i < branches.Count; i++)
            {
                if (!TryGetListPatternMaxPrefixLength(branches[i].Pattern, out var branchMaxLength))
                {
                    continue;
                }

                hasListShape = true;
                if (branchMaxLength > maxLength)
                {
                    maxLength = branchMaxLength;
                }
            }

            return hasListShape ? maxLength : 0;
        }

        private HashSet<ListCoverageCase> NormalizeBranchExactListCases(IEnumerable<ListCoverageCase> inputCases)
        {
            var normalized = new HashSet<ListCoverageCase>();
            foreach (var @case in inputCases)
            {
                if (@case.IsAtLeast)
                {
                    var startLength = Math.Max(0, @case.Length);
                    for (var length = startLength; length <= _maxTrackedLength; length++)
                    {
                        _ = AddListCasesForLength(
                            length,
                            _boolSplitLengths,
                            hasBoolElementSets: true,
                            elementBoolCases: [],
                            _intSplitDomainsByLength,
                            hasIntElementSets: false,
                            elementIntTokenCases: [],
                            useIntSplitForPattern: true,
                            _adtSplitDomainsByLength,
                            hasAdtElementSets: false,
                            elementAdtTokenCases: [],
                            useAdtSplitForPattern: true,
                            requireSplitExactness: false,
                            normalized);
                    }

                    normalized.Add(new ListCoverageCase(IsAtLeast: true, Length: _overflowStartLength));
                    continue;
                }

                if (@case.IsAtLeast ||
                    !IsSplitLength(@case.Length) ||
                    !string.IsNullOrWhiteSpace(@case.BoolVectorKey))
                {
                    normalized.Add(@case);
                    continue;
                }

                _ = AddListCasesForLength(
                    @case.Length,
                    _boolSplitLengths,
                    hasBoolElementSets: true,
                    elementBoolCases: [],
                    _intSplitDomainsByLength,
                    hasIntElementSets: false,
                    elementIntTokenCases: [],
                    useIntSplitForPattern: true,
                    _adtSplitDomainsByLength,
                    hasAdtElementSets: false,
                    elementAdtTokenCases: [],
                    useAdtSplitForPattern: true,
                    requireSplitExactness: false,
                    normalized);
            }

            return normalized;
        }

        private bool IsSplitLength(int length)
        {
            if (_enableBoolElementSplits && _boolSplitLengths.Contains(length))
            {
                return true;
            }

            return _enableIntElementSplits && _intSplitDomainsByLength.ContainsKey(length);
        }

        private void EnsureAdtElementSplitsInitialized(SymbolTable symbolTable)
        {
            if (_adtElementSplitsInitialized)
            {
                return;
            }

            _enableAdtElementSplits = TryDetermineListAdtSplitDomains(
                _branches.Select(branch => branch.Pattern),
                _maxTrackedLength,
                symbolTable,
                out _adtSplitDomainsByLength);
            _adtElementSplitsInitialized = true;
        }

        private static HashSet<int> CollectExplicitListSplitLengthsFromBranchFacts(
            IReadOnlyList<PatternUsefulnessBranchFact> branches,
            int maxTrackedLength)
        {
            var splitLengths = new HashSet<int>();
            if (maxTrackedLength <= 0)
            {
                return splitLengths;
            }

            for (var i = 0; i < branches.Count; i++)
            {
                foreach (var @case in branches[i].ListCoverageCases)
                {
                    if (@case.IsAtLeast ||
                        string.IsNullOrWhiteSpace(@case.BoolVectorKey) ||
                        !IsBoolVectorEncoding(@case.BoolVectorKey) ||
                        @case.Length < 0 ||
                        @case.Length > maxTrackedLength ||
                        @case.Length > MaxListBoolSplitLength)
                    {
                        continue;
                    }

                    splitLengths.Add(@case.Length);
                }
            }

            return splitLengths;
        }

    }

    private readonly record struct AdtConstructorCase(
        SymbolId ConstructorId,
        string? WitnessText);

    private sealed class AdtConstructorCaseComparer : IEqualityComparer<AdtConstructorCase>
    {
        public bool Equals(AdtConstructorCase x, AdtConstructorCase y)
        {
            return x.ConstructorId == y.ConstructorId;
        }

        public int GetHashCode(AdtConstructorCase obj)
        {
            return obj.ConstructorId.GetHashCode();
        }
    }

    private sealed partial class AdtCoverageSpace : FiniteCoverageSpace<AdtConstructorCase>
    {
        private SymbolId _coveredAdt = SymbolId.None;

        public AdtCoverageSpace() : base(new AdtConstructorCaseComparer())
        {
        }

        protected override PatternCoverageTargetKind CoverageTarget => PatternCoverageTargetKind.Adt;

        protected override bool AllowSpecializationWhenGuarded => true;

        protected override PatternSpecializationStatus TrySpecializeBranch(
            PatternUsefulnessBranchFact branch,
            SymbolTable symbolTable,
            out PatternSpecialization<AdtConstructorCase> specialization)
        {
            if (branch.HasExactAdtCoverage)
            {
                if (!branch.AdtCoverageAdt.IsValid)
                {
                    specialization = default;
                    return PatternSpecializationStatus.Untrackable;
                }

                if (!_coveredAdt.IsValid)
                {
                    _coveredAdt = branch.AdtCoverageAdt;
                }
                else if (_coveredAdt != branch.AdtCoverageAdt)
                {
                    specialization = default;
                    return PatternSpecializationStatus.Untrackable;
                }

                specialization = new PatternSpecialization<AdtConstructorCase>(
                    IsExactFiniteCases: true,
                    Cases: branch.AdtCoverageConstructors
                        .OrderBy(ctorId => ctorId.Value)
                        .Select(ctorId => new AdtConstructorCase(ctorId, WitnessText: null))
                        .ToList());
                return PatternSpecializationStatus.ExactFinite;
            }

            if (branch.IsGuarded && branch.GuardConstant == null)
            {
                if (branch.AdtCoverageAdt.IsValid &&
                    branch.AdtCoverageConstructors.Count > 0)
                {
                    if (!_coveredAdt.IsValid)
                    {
                        _coveredAdt = branch.AdtCoverageAdt;
                    }
                    else if (_coveredAdt != branch.AdtCoverageAdt)
                    {
                        specialization = default;
                        return PatternSpecializationStatus.Untrackable;
                    }

                    // Guarded unresolved ADT branches may still carry a proven
                    // constructor lower-bound (guard=true on some constructors).
                    // Keep this lower-bound for covered diagnostics while the
                    // unresolved part remains conservative for exhaustiveness.
                    specialization = new PatternSpecialization<AdtConstructorCase>(
                        IsExactFiniteCases: true,
                        Cases: branch.AdtCoverageConstructors
                            .OrderBy(ctorId => ctorId.Value)
                            .Select(ctorId => new AdtConstructorCase(ctorId, WitnessText: null))
                            .ToList(),
                        TraceProvenance: PatternWitnessTraceProvenance.LowerBound);
                    return PatternSpecializationStatus.ExactFinite;
                }

                specialization = default;
                return PatternSpecializationStatus.NotApplicable;
            }

            var status = TryCollectAdtConstructorCases(
                branch.Pattern,
                symbolTable,
                _coveredAdt,
                out var branchAdt,
                out var constructorHints);
            if (status is not PatternSpecializationStatus.ExactFinite)
            {
                specialization = default;
                return status;
            }

            if (!_coveredAdt.IsValid)
            {
                _coveredAdt = branchAdt;
            }
            else if (_coveredAdt != branchAdt)
            {
                specialization = default;
                return PatternSpecializationStatus.Untrackable;
            }

            specialization = new PatternSpecialization<AdtConstructorCase>(
                IsExactFiniteCases: true,
                Cases: constructorHints
                    .OrderBy(entry => entry.Key.Value)
                    .Select(entry => new AdtConstructorCase(
                        entry.Key,
                        entry.Value))
                    .ToList());
            return PatternSpecializationStatus.ExactFinite;
        }

        protected override bool TryBuildUniverse(SymbolTable symbolTable, out IReadOnlyList<AdtConstructorCase> universe)
        {
            if (!_coveredAdt.IsValid ||
                symbolTable.GetSymbol<AdtSymbol>(_coveredAdt) is not { } adtSymbol ||
                adtSymbol.Constructors.Count == 0)
            {
                universe = [];
                return false;
            }

            universe = adtSymbol.Constructors
                .Select(ctorId => new AdtConstructorCase(ctorId, WitnessText: null))
                .ToList();
            return true;
        }

        protected override string FormatMissingCase(AdtConstructorCase @case, SymbolTable symbolTable)
        {
            return GetConstructorDisplayName(symbolTable, @case.ConstructorId);
        }

        protected override PatternWitness CreateMissingWitness(AdtConstructorCase @case, SymbolTable symbolTable)
        {
            var ctorName = GetConstructorDisplayName(symbolTable, @case.ConstructorId);
            return CreateConstructorWitness(symbolTable, @case.ConstructorId, ctorName, @case.WitnessText);
        }

    }

    public static PatternUsefulnessSummary Analyze(
        IReadOnlyList<PatternUsefulnessBranchFact> branches,
        SymbolTable symbolTable)
    {
        IPatternCoverageSpace[] spaces =
        [
            new BoolCoverageSpace(),
            new ScalarCoverageSpace(branches),
            new TupleBoolCoverageSpace(),
            new TupleAdtCoverageSpace(branches, symbolTable),
            new ListLengthCoverageSpace(branches),
            new AdtCoverageSpace()
        ];

        var hasGuardedBranches = false;
        var hasUnguardedCatchAll = false;
        var catchAllBranchIndex = -1;
        var unreachableBranches = new List<PatternUnreachableBranch>();

        foreach (var branch in branches)
        {
            if (hasUnguardedCatchAll)
            {
                unreachableBranches.Add(
                    new PatternUnreachableBranch(
                        branch.Span,
                        branch.BranchIndex,
                        PatternUnreachableKind.ShadowedByIrrefutable,
                        catchAllBranchIndex));
                continue;
            }

            if (branch.GuardConstant == false)
            {
                unreachableBranches.Add(
                    new PatternUnreachableBranch(
                        branch.Span,
                        branch.BranchIndex,
                        PatternUnreachableKind.ConstantFalseGuard));
                continue;
            }

            var isUnguarded = !branch.IsGuarded || branch.GuardConstant == true;

            var hasExactFiniteProjection = false;
            var addsFiniteCoverage = false;
            var hasUnsatisfiableFiniteProjection = false;
            var coveringBranchIndices = new HashSet<int>();
            var coveringWitnesses = new List<PatternWitness>();
            var coveringWitnessTraces = new List<PatternWitnessTrace>();

            foreach (var space in spaces)
            {
                var observation = space.ObserveBranch(branch, isUnguarded, symbolTable);
                if (!observation.HasExactFiniteCases)
                {
                    continue;
                }

                hasExactFiniteProjection = true;
                hasUnsatisfiableFiniteProjection |= observation.IsUnsatisfiable;
                if (!isUnguarded)
                {
                    continue;
                }

                addsFiniteCoverage |= observation.AddsCoverage;

                foreach (var coveringBranch in observation.CoveringBranchIndices)
                {
                    coveringBranchIndices.Add(coveringBranch);
                }

                coveringWitnesses.AddRange(observation.CoveringWitnesses);
                coveringWitnessTraces.AddRange(observation.CoveringWitnessTraces);
            }

            if (branch.IsGuarded && branch.GuardConstant == null && !hasExactFiniteProjection)
            {
                hasGuardedBranches = true;
            }

            if (hasExactFiniteProjection &&
                hasUnsatisfiableFiniteProjection &&
                (!isUnguarded || !addsFiniteCoverage))
            {
                unreachableBranches.Add(
                    new PatternUnreachableBranch(
                        branch.Span,
                        branch.BranchIndex,
                        PatternUnreachableKind.EmptyFiniteCaseSet));
                continue;
            }

            if (isUnguarded && hasExactFiniteProjection && !addsFiniteCoverage)
            {
                unreachableBranches.Add(
                    new PatternUnreachableBranch(
                        branch.Span,
                        branch.BranchIndex,
                        PatternUnreachableKind.CoveredByPreviousFiniteCases,
                        CoveringBranchIndices: coveringBranchIndices
                            .OrderBy(index => index)
                            .ToList(),
                        CoveringWitnesses: DistinctWitnesses(coveringWitnesses),
                        CoveringWitnessTraces: DistinctWitnessTraces(coveringWitnessTraces)));
                continue;
            }

            if (isUnguarded && branch.IsIrrefutable)
            {
                hasUnguardedCatchAll = true;
                catchAllBranchIndex = branch.BranchIndex;
            }
        }

        if (hasUnguardedCatchAll)
        {
            return Exhaustive(hasGuardedBranches, unreachableBranches);
        }

        for (var i = 0; i < spaces.Length; i++)
        {
            var summary = spaces[i].BuildSummary(hasGuardedBranches, unreachableBranches, symbolTable);
            if (summary != null)
            {
                return summary;
            }
        }

        return new PatternUsefulnessSummary(
            IsExhaustive: false,
            HasGuardedBranches: hasGuardedBranches,
            CoverageTarget: PatternCoverageTargetKind.Generic,
            MissingCases: [],
            MissingWitnesses:
            [
                new PatternWitness(PatternWitnessKind.Wildcard, "_", "wildcard:_")
            ],
            UnreachableBranches: unreachableBranches);
    }

    internal static bool TryGetExactAdtConstructorCases(
        Pattern pattern,
        SymbolTable symbolTable,
        out SymbolId adtId,
        out IReadOnlyList<SymbolId> constructorIds)
    {
        var status = AdtCoverageSpace.TryCollectAdtConstructorCases(
            pattern,
            symbolTable,
            SymbolId.None,
            out adtId,
            out var constructorHints);
        if (status is not PatternSpecializationStatus.ExactFinite || !adtId.IsValid)
        {
            adtId = SymbolId.None;
            constructorIds = [];
            return false;
        }

        constructorIds = constructorHints.Keys
            .OrderBy(id => id.Value)
            .ToList();
        return true;
    }

    private static PatternSpecializationStatus TryCollectExactTupleAdtCases(
        Pattern pattern,
        SymbolTable symbolTable,
        IReadOnlyList<SymbolId> preferredElementAdts,
        out IReadOnlyList<SymbolId> resolvedElementAdts,
        out IReadOnlyList<TupleAdtConstructorCase> cases)
    {
        switch (pattern)
        {
            case TuplePattern tuplePattern:
                return TryCollectTuplePatternAdtCases(
                    tuplePattern,
                    symbolTable,
                    preferredElementAdts,
                    out resolvedElementAdts,
                    out cases);

            case AsPattern { InnerPattern: not null } asPattern:
                return TryCollectExactTupleAdtCases(
                    asPattern.InnerPattern,
                    symbolTable,
                    preferredElementAdts,
                    out resolvedElementAdts,
                    out cases);

            case OrPattern { Alternatives.Count: > 0 } orPattern:
            {
                var mergedElementAdts = preferredElementAdts.Count > 0
                    ? preferredElementAdts.ToList()
                    : [];
                var mergedCases = new Dictionary<string, TupleAdtConstructorCase>(StringComparer.Ordinal);

                foreach (var alternative in orPattern.Alternatives)
                {
                    var alternativeStatus = TryCollectExactTupleAdtCases(
                        alternative,
                        symbolTable,
                        mergedElementAdts,
                        out var alternativeElementAdts,
                        out var alternativeCases);
                    if (alternativeStatus is not PatternSpecializationStatus.ExactFinite)
                    {
                        resolvedElementAdts = [];
                        cases = [];
                        return alternativeStatus;
                    }

                    if (mergedElementAdts.Count == 0)
                    {
                        mergedElementAdts = alternativeElementAdts.ToList();
                    }
                    else if (!HaveSameElementDomains(mergedElementAdts, alternativeElementAdts))
                    {
                        resolvedElementAdts = [];
                        cases = [];
                        return PatternSpecializationStatus.Untrackable;
                    }

                    foreach (var @case in alternativeCases)
                    {
                        mergedCases.TryAdd(@case.Key, @case);
                    }
                }

                resolvedElementAdts = mergedElementAdts;
                cases = mergedCases.Values
                    .OrderBy(@case => @case.Key, StringComparer.Ordinal)
                    .ToList();
                return cases.Count > 0
                    ? PatternSpecializationStatus.ExactFinite
                    : PatternSpecializationStatus.NotApplicable;
            }

            default:
                resolvedElementAdts = [];
                cases = [];
                return PatternSpecializationStatus.NotApplicable;
        }
    }

    private static PatternSpecializationStatus TryCollectTuplePatternAdtCases(
        TuplePattern tuplePattern,
        SymbolTable symbolTable,
        IReadOnlyList<SymbolId> preferredElementAdts,
        out IReadOnlyList<SymbolId> resolvedElementAdts,
        out IReadOnlyList<TupleAdtConstructorCase> cases)
    {
        if (tuplePattern.Elements.Count == 0)
        {
            resolvedElementAdts = [];
            cases = [];
            return PatternSpecializationStatus.NotApplicable;
        }

        var elementAdts = new List<SymbolId>(tuplePattern.Elements.Count);
        var elementConstructorDomains = new List<IReadOnlyList<SymbolId>>(tuplePattern.Elements.Count);

        for (var i = 0; i < tuplePattern.Elements.Count; i++)
        {
            var preferredAdt = i < preferredElementAdts.Count
                ? preferredElementAdts[i]
                : SymbolId.None;
            var status = AdtCoverageSpace.TryCollectAdtConstructorCases(
                tuplePattern.Elements[i],
                symbolTable,
                preferredAdt,
                out var resolvedAdt,
                out var constructorHints);
            if (status is not PatternSpecializationStatus.ExactFinite ||
                !resolvedAdt.IsValid ||
                constructorHints.Count == 0)
            {
                resolvedElementAdts = [];
                cases = [];
                return status;
            }

            elementAdts.Add(resolvedAdt);
            elementConstructorDomains.Add(constructorHints.Keys
                .OrderBy(id => id.Value)
                .ToList());
        }

        var builtCases = new List<TupleAdtConstructorCase>();
        BuildTupleAdtCasesRecursive(elementConstructorDomains, 0, [], builtCases);
        resolvedElementAdts = elementAdts;
        cases = builtCases;
        return cases.Count > 0
            ? PatternSpecializationStatus.ExactFinite
            : PatternSpecializationStatus.NotApplicable;
    }

    private static void BuildTupleAdtCasesRecursive(
        IReadOnlyList<IReadOnlyList<SymbolId>> constructorDomains,
        int index,
        List<SymbolId> current,
        ICollection<TupleAdtConstructorCase> output)
    {
        if (index >= constructorDomains.Count)
        {
            var constructorIds = current.ToList();
            output.Add(new TupleAdtConstructorCase(
                EncodeTupleAdtCaseKey(constructorIds),
                constructorIds));
            return;
        }

        var constructors = constructorDomains[index];
        for (var i = 0; i < constructors.Count; i++)
        {
            current.Add(constructors[i]);
            BuildTupleAdtCasesRecursive(constructorDomains, index + 1, current, output);
            current.RemoveAt(current.Count - 1);
        }
    }

    private static bool HaveSameElementDomains(
        IReadOnlyList<SymbolId> left,
        IReadOnlyList<SymbolId> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (left[i] != right[i])
            {
                return false;
            }
        }

        return true;
    }

    private static string EncodeTupleAdtCaseKey(IReadOnlyList<SymbolId> constructorIds)
    {
        return string.Join(WellKnownStrings.Punctuation.Pipe, constructorIds.Select(id => id.Value.ToString()));
    }

    private static PatternUsefulnessSummary Exhaustive(
        bool hasGuardedBranches,
        IReadOnlyList<PatternUnreachableBranch> unreachableBranches)
    {
        return new PatternUsefulnessSummary(
            IsExhaustive: true,
            HasGuardedBranches: hasGuardedBranches,
            CoverageTarget: PatternCoverageTargetKind.None,
            MissingCases: [],
            MissingWitnesses: [],
            UnreachableBranches: unreachableBranches);
    }

    private static PatternWitness CreateConstructorWitness(
        SymbolTable symbolTable,
        SymbolId ctorId,
        string ctorName,
        string? explicitWitnessText = null)
    {
        var stableKey = $"ctor:{ctorId.Value}";
        if (!string.IsNullOrWhiteSpace(explicitWitnessText))
        {
            return new PatternWitness(PatternWitnessKind.Constructor, explicitWitnessText, stableKey);
        }

        var ctorSymbol = symbolTable.GetSymbol<CtorSymbol>(ctorId);
        if (ctorSymbol == null || ctorSymbol.IsNullary)
        {
            return new PatternWitness(PatternWitnessKind.Constructor, ctorName, stableKey);
        }

        if (ctorSymbol.NamedFields.Count > 0 && ctorSymbol.PositionalArgs.Count == 0)
        {
            return new PatternWitness(PatternWitnessKind.Constructor, $"{ctorName}{{...}}", stableKey);
        }

        return new PatternWitness(PatternWitnessKind.Constructor, $"{ctorName}(...)", stableKey);
    }

    private static string FormatTupleAdtCase(IReadOnlyList<SymbolId> constructorIds, SymbolTable symbolTable)
    {
        var elements = constructorIds
            .Select(ctorId =>
            {
                var ctorName = GetConstructorDisplayName(symbolTable, ctorId);
                return CreateConstructorWitness(symbolTable, ctorId, ctorName).DisplayText;
            })
            .ToList();
        return $"({string.Join(", ", elements)})";
    }

    private static string GetConstructorDisplayName(SymbolTable symbolTable, SymbolId ctorId)
    {
        return symbolTable.GetSymbol(ctorId)?.Name ?? $"ctor#{ctorId.Value}";
    }

}
