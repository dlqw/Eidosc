using Eidosc.Utils;

namespace Eidosc.Diagnostic;

public enum DiagnosticLevel
{
    Error,
    Warning,
    Info,
    Note,
    Help
}

public enum ErrorCode
{
    E0001_TypeMismatch,
    E0002_UndefinedVariable,
    E0003_UndefinedType,
    E0004_CannotInferType,
    E0005_ArityMismatch,
    E0006_InvalidCast,
    E1001_UseOfMovedValue,
    E1002_MultipleMutableBorrows,
    E1003_BorrowConflict,
    E1004_LifetimeMismatch,
    E1011_ReadCapabilityRequired,
    E1012_WriteCapabilityRequired,
    E1013_MoveCapabilityRequired,
    E2000_TraitNotImplemented,
    E2001_MethodNotFound,
    E2002_AssociatedTypeNotFound,
    E2003_TraitBoundNotSatisfied,
    E3000_UndefinedSymbol,
    E3001_AmbiguousName,
    E3002_PrivateAccess,
    E3003_CapabilityAuthorizationRequired,
    E3004_OverlappingImplRegistration,
    E4000_UnexpectedToken,
    E4001_InvalidSyntax,
    E4002_UnterminatedString,
    E4010_RangePatternMissingBoundary,
    E4011_RangePatternInvalidOrder,
    E4012_RangePatternInvalidScrutineeType,
    E4013_AsPatternTypeMismatch,
    E5001_CircularImport,
    E5002_NestingDepthExceeded,
    E5300_ModuleValueCall,
    E5301_GenericCallEscapedSpecialization,
    E5302_GenericPartialApplicationEscapedSpecialization,
    E5303_GenericOperandEscapedSpecialization,
    E5304_UnresolvedFunctionReference,
    E5305_GenericCallLoweringFailed,
    E5308_DuplicateLlvmGlobalDefinition,
    E5310_GenericSpecializationRejected,
    E5311_GenericSpecializationLoopDidNotConverge,
    E5320_UnsupportedMIRConstruct,
    E5330_UnsupportedMIRNode,
    W4001_UnknownEscapeSequence,
    W4002_EmptySourceFile,
    E3053_CfnFromCapturingClosureNotSupported,
    E3054_DuplicateFfiBinding,
    E3055_ReservedInternalNameDeclaration,
    E3056_CyclicSupertrait,
    E3057_UndefinedSupertrait,
    E3058_SupertraitTypeArgumentCountMismatch,
    E3059_SelfReferentialSupertrait,
    E3060_DuplicateSupertrait,
    E3061_PublicClosedCaseContainsInternalDescendant,
}

public class DiagnosticLabel(SourceSpan span, string message = "")
{
    public SourceSpan Span { get; } = span;
    public string Message { get; } = message;
}

public class Suggestion
{
    public string Message { get; init; } = string.Empty;
    public SuggestionKind Kind { get; init; }
    public SourceSpan? Span { get; init; }
    public string? Replacement { get; init; }
    public string? HelpUrl { get; init; }
    public string Confidence { get; init; } = "high";
    public bool RequiresCleanTypes { get; init; }
    public int? OriginalSymbolId { get; init; }
}

public enum SuggestionKind
{
    AddImport,
    QualifySymbol,
    ChangeType,
    AddTraitImpl,
    FixBorrow,
    RenameVariable,
    RenameSymbol,
    GenericParameter,
    TypeCast,
    StyleRewrite
}

public class Diagnostic(DiagnosticLevel level, string message, string? code = null)
{
    private readonly List<DiagnosticLabel> _labels = [];
    private readonly List<string> _notes = [];
    private readonly List<string> _helps = [];
    private readonly List<Suggestion> _suggestions = [];
    private readonly List<Diagnostic> _related = [];
    private readonly Dictionary<string, string> _metadata = new(StringComparer.Ordinal);

    public DiagnosticLevel Level { get; } = level;
    public string Message { get; } = message;
    public string? Code { get; } = code;
    public IReadOnlyList<DiagnosticLabel> Labels => _labels;
    public IReadOnlyList<string> Notes => _notes;
    public IReadOnlyList<string> Helps => _helps;
    public IReadOnlyList<Suggestion> Suggestions => _suggestions;
    public IReadOnlyList<Diagnostic> Related => _related;

    /// <summary>
    /// Gets machine-readable diagnostic metadata for IDE, LSP, and test consumers.
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata => _metadata;

    public static Diagnostic Error(string message, string? code = null)
        => new(DiagnosticLevel.Error, message, code);

    public static Diagnostic Warning(string message, string? code = null)
        => new(DiagnosticLevel.Warning, message, code);

    public static Diagnostic Info(string message, string? code = null)
        => new(DiagnosticLevel.Info, message, code);

    public static Diagnostic Note(string message, string? code = null)
        => new(DiagnosticLevel.Note, message, code);

    public static Diagnostic Help(string message, string? code = null)
        => new(DiagnosticLevel.Help, message, code);

    public Diagnostic WithLabel(SourceSpan span, string message = "")
    {
        _labels.Add(new DiagnosticLabel(span, message));
        return this;
    }

    public Diagnostic WithNote(string note)
    {
        _notes.Add(note);
        return this;
    }

    public Diagnostic WithHelp(string help)
    {
        _helps.Add(help);
        return this;
    }

    public Diagnostic WithSuggestion(
        string message,
        SuggestionKind kind,
        SourceSpan? span = null,
        string? replacement = null,
        string? helpUrl = null,
        string confidence = "high",
        bool requiresCleanTypes = false,
        int? originalSymbolId = null)
    {
        _suggestions.Add(new Suggestion
        {
            Message = message,
            Kind = kind,
            Span = span,
            Replacement = replacement,
            HelpUrl = helpUrl,
            Confidence = confidence,
            RequiresCleanTypes = requiresCleanTypes,
            OriginalSymbolId = originalSymbolId
        });
        return this;
    }

    public Diagnostic WithRelated(Diagnostic related)
    {
        _related.Add(related);
        return this;
    }

    /// <summary>
    /// Adds or replaces a machine-readable diagnostic metadata entry.
    /// </summary>
    /// <param name="key">The stable metadata key.</param>
    /// <param name="value">The metadata value.</param>
    /// <returns>The current diagnostic instance.</returns>
    public Diagnostic WithMetadata(string key, string value)
    {
        if (!string.IsNullOrWhiteSpace(key))
        {
            _metadata[key] = value;
        }

        return this;
    }
}
