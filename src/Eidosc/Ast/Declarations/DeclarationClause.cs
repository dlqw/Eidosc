using System.Xml;

namespace Eidosc.Ast.Declarations;


public enum DeclarationClauseKind
{
    Where,
    Case,
    Need,
    Repr,
    Extern,
    LinkLibrary,
    LinkName,
    Derive,
    Impl,
    Operator,
    Borrow,
    ProofUnfold,
    Transparent,
    Before,
    After,
    Expand,
    Requires,
    Internal,
    Intrinsic,
    LlvmAbi,
    Compiler
}

[Flags]
public enum DeclarationClauseTarget
{
    None = 0,
    Type = 1 << 0,
    Function = 1 << 1,
    Trait = 1 << 2,
    Instance = 1 << 3,
    Effect = 1 << 4,
    Module = 1 << 5,
    CaseType = 1 << 6,
    Value = 1 << 7,
    Import = 1 << 8,
    Proof = 1 << 9,
    AssociatedType = 1 << 10,
    AssociatedConst = 1 << 11,
    Field = 1 << 12,
    Constructor = 1 << 13,
    AnyDeclaration = Type | Function | Trait | Instance | Effect | Module | CaseType |
                     Value | Import | Proof | AssociatedType | AssociatedConst | Field | Constructor
}

public enum ClauseArgumentGrammar
{
    None,
    Path,
    PathList,
    String,
    Identifier,
    IdentifierList,
    MetaInvocation,
    TokenIsland
}

public enum ClauseStage
{
    Syntax,
    Semantic,
    Body,
    Layout
}

public enum ClauseCanonicalArgumentType
{
    None,
    Constraint,
    Type,
    Effect,
    String,
    Identifier,
    Abi,
    Trait,
    Operator,
    BorrowCapability,
    Declaration,
    Generator,
    Capability,
    MetaInvocation,
    CompilerDirective
}

public enum ClauseSourceOrderBehavior
{
    Preserve,
    GeneratorSequence,
    OrderingConstraint
}

public enum ClausePrivilegePolicy
{
    Public,
    ToolchainOwnedSource
}

public enum DeclarationAttachmentAdapterKind
{
    SignatureComponent,
    TypedTag,
    ForeignContract,
    DedicatedDeclaration,
    CompilerDirective,
    RemovedSurface
}

public sealed record ClauseMigrationRule(
    string RuleId,
    IReadOnlyList<string> LegacySpellings);

public sealed record DeclarationClauseSpec(
    string Keyword,
    DeclarationClauseKind Kind,
    DeclarationClauseTarget Targets,
    ClauseArgumentGrammar Arguments,
    ClauseCanonicalArgumentType CanonicalArgumentType,
    ClauseStage Stage,
    ClauseSourceOrderBehavior SourceOrder,
    bool Repeatable = false,
    ClausePrivilegePolicy Privilege = ClausePrivilegePolicy.Public,
    bool ProducesMetaInvocation = false,
    bool CompilerOwnedInvocation = false,
    bool MetaGeneratorOnly = false,
    IReadOnlyList<DeclarationClauseKind>? Requires = null,
    IReadOnlyList<DeclarationClauseKind>? Conflicts = null,
    ClauseMigrationRule? Migration = null,
    DeclarationAttachmentAdapterKind Adapter = DeclarationAttachmentAdapterKind.SignatureComponent);

/// <summary>
/// A source-order, typed declaration clause. Arguments remain lossless token
/// islands until the clause binder applies the versioned schema.
/// </summary>
public sealed record DeclarationClause : EidosAstNode
{
    public DeclarationClauseKind ClauseKind { get; private set; }

    public string Keyword { get; private set; } = "";

    public List<string> ArgumentTokens { get; private set; } = [];

    public MetaInvocationSyntax? MetaInvocation { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }

    internal void SetKind(DeclarationClauseKind kind, string keyword)
    {
        ClauseKind = kind;
        Keyword = keyword;
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void AddArgument(string argument) => ArgumentTokens.Add(argument);
    internal void SetMetaInvocation(MetaInvocationSyntax invocation) => MetaInvocation = invocation;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "DeclarationClause");
        element.SetAttribute("kind", ClauseKind.ToString());
        element.SetAttribute("keyword", Keyword);
        foreach (var argument in ArgumentTokens)
        {
            var child = doc.CreateElement("Argument");
            child.InnerText = argument;
            element.AppendChild(child);
        }

        return element;
    }
}

public sealed record MetaInvocationSyntax : EidosAstNode
{
    public List<string> GeneratorPath { get; private set; } = [];
    public List<EidosAstNode> ExplicitArguments { get; private set; } = [];

    public string GeneratorDisplayName => string.Join(WellKnownStrings.Separators.Path, GeneratorPath);

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node) => Span = node.Span;

    internal void SetGeneratorPath(IEnumerable<string> path) => GeneratorPath = [.. path];
    internal void AddExplicitArgument(EidosAstNode argument) => ExplicitArguments.Add(argument);
    internal void SetSpan(Utils.SourceSpan span) => Span = span;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "MetaInvocation");
        element.SetAttribute("generator", GeneratorDisplayName);
        foreach (var argument in ExplicitArguments)
        {
            element.AppendChild(argument.ToXmlElement(doc));
        }
        return element;
    }
}

/// <summary>
/// Closed core schema for the 0.7 declaration-clause surface.
/// </summary>
public static class ClauseSchema
{
    public const string Version = "clause-schema-v1";

    private static readonly ClauseMigrationRule NoLegacyMigration = new("none", []);

    private static readonly IReadOnlyDictionary<string, DeclarationClauseSpec> Specs =
        new Dictionary<string, DeclarationClauseSpec>(StringComparer.Ordinal)
        {
            ["where"] = new("where", DeclarationClauseKind.Where, DeclarationClauseTarget.Type | DeclarationClauseTarget.Function | DeclarationClauseTarget.Trait | DeclarationClauseTarget.Instance | DeclarationClauseTarget.CaseType, ClauseArgumentGrammar.TokenIsland, ClauseCanonicalArgumentType.Constraint, ClauseStage.Semantic, ClauseSourceOrderBehavior.Preserve, Repeatable: true, Migration: NoLegacyMigration),
            ["case"] = new("case", DeclarationClauseKind.Case, DeclarationClauseTarget.CaseType, ClauseArgumentGrammar.Path, ClauseCanonicalArgumentType.Type, ClauseStage.Syntax, ClauseSourceOrderBehavior.Preserve, Migration: NoLegacyMigration),
            ["need"] = new("need", DeclarationClauseKind.Need, DeclarationClauseTarget.Function, ClauseArgumentGrammar.PathList, ClauseCanonicalArgumentType.Effect, ClauseStage.Semantic, ClauseSourceOrderBehavior.Preserve, Repeatable: true, Migration: new("attribute-effects-to-need", ["effects"])),
            ["repr"] = new("repr", DeclarationClauseKind.Repr, DeclarationClauseTarget.Type, ClauseArgumentGrammar.Identifier, ClauseCanonicalArgumentType.Identifier, ClauseStage.Semantic, ClauseSourceOrderBehavior.Preserve, Migration: new("attribute-cstruct-to-repr-c", ["cstruct"]), Adapter: DeclarationAttachmentAdapterKind.TypedTag),
            ["extern"] = new("extern", DeclarationClauseKind.Extern, DeclarationClauseTarget.Function, ClauseArgumentGrammar.TokenIsland, ClauseCanonicalArgumentType.Abi, ClauseStage.Semantic, ClauseSourceOrderBehavior.Preserve, Requires: [DeclarationClauseKind.Need], Migration: new("attribute-ffi-to-extern-c", ["ffi"]), Adapter: DeclarationAttachmentAdapterKind.ForeignContract),
            ["derive"] = new("derive", DeclarationClauseKind.Derive, DeclarationClauseTarget.Type | DeclarationClauseTarget.CaseType, ClauseArgumentGrammar.PathList, ClauseCanonicalArgumentType.Trait, ClauseStage.Semantic, ClauseSourceOrderBehavior.GeneratorSequence, Repeatable: true, ProducesMetaInvocation: true, CompilerOwnedInvocation: true, Migration: new("attribute-derive", ["derive"]), Adapter: DeclarationAttachmentAdapterKind.TypedTag),
            ["impl"] = new("impl", DeclarationClauseKind.Impl, DeclarationClauseTarget.Function, ClauseArgumentGrammar.Path, ClauseCanonicalArgumentType.Trait, ClauseStage.Semantic, ClauseSourceOrderBehavior.Preserve, Migration: new("attribute-impl", ["impl"]), Adapter: DeclarationAttachmentAdapterKind.DedicatedDeclaration),
            ["borrow"] = new("borrow", DeclarationClauseKind.Borrow, DeclarationClauseTarget.Function, ClauseArgumentGrammar.IdentifierList, ClauseCanonicalArgumentType.BorrowCapability, ClauseStage.Semantic, ClauseSourceOrderBehavior.Preserve, Migration: new("attribute-borrow", ["borrow"]), Adapter: DeclarationAttachmentAdapterKind.RemovedSurface),
            ["proof_unfold"] = new("proof_unfold", DeclarationClauseKind.ProofUnfold, DeclarationClauseTarget.Function, ClauseArgumentGrammar.Path, ClauseCanonicalArgumentType.Declaration, ClauseStage.Semantic, ClauseSourceOrderBehavior.Preserve, Migration: new("attribute-proof-unfold", ["proof_unfold"]), Adapter: DeclarationAttachmentAdapterKind.TypedTag),
            ["transparent"] = new("transparent", DeclarationClauseKind.Transparent, DeclarationClauseTarget.Function, ClauseArgumentGrammar.None, ClauseCanonicalArgumentType.None, ClauseStage.Semantic, ClauseSourceOrderBehavior.Preserve, Migration: new("attribute-transparent", ["transparent"]), Adapter: DeclarationAttachmentAdapterKind.TypedTag),
            ["before"] = new("before", DeclarationClauseKind.Before, DeclarationClauseTarget.Function, ClauseArgumentGrammar.PathList, ClauseCanonicalArgumentType.Generator, ClauseStage.Syntax, ClauseSourceOrderBehavior.OrderingConstraint, Repeatable: true, MetaGeneratorOnly: true, Migration: NoLegacyMigration, Adapter: DeclarationAttachmentAdapterKind.RemovedSurface),
            ["after"] = new("after", DeclarationClauseKind.After, DeclarationClauseTarget.Function, ClauseArgumentGrammar.PathList, ClauseCanonicalArgumentType.Generator, ClauseStage.Syntax, ClauseSourceOrderBehavior.OrderingConstraint, Repeatable: true, MetaGeneratorOnly: true, Migration: NoLegacyMigration, Adapter: DeclarationAttachmentAdapterKind.RemovedSurface),
            ["expand"] = new("expand", DeclarationClauseKind.Expand, DeclarationClauseTarget.AnyDeclaration, ClauseArgumentGrammar.MetaInvocation, ClauseCanonicalArgumentType.MetaInvocation, ClauseStage.Semantic, ClauseSourceOrderBehavior.GeneratorSequence, Repeatable: true, ProducesMetaInvocation: true, Migration: new("attribute-generator-to-expand", ["generator"]), Adapter: DeclarationAttachmentAdapterKind.TypedTag),
            ["requires"] = new("requires", DeclarationClauseKind.Requires, DeclarationClauseTarget.Function, ClauseArgumentGrammar.PathList, ClauseCanonicalArgumentType.Generator, ClauseStage.Syntax, ClauseSourceOrderBehavior.OrderingConstraint, Repeatable: true, MetaGeneratorOnly: true, Migration: NoLegacyMigration, Adapter: DeclarationAttachmentAdapterKind.RemovedSurface),
            ["compiler"] = new("compiler", DeclarationClauseKind.Compiler, DeclarationClauseTarget.AnyDeclaration, ClauseArgumentGrammar.TokenIsland, ClauseCanonicalArgumentType.CompilerDirective, ClauseStage.Semantic, ClauseSourceOrderBehavior.Preserve, Repeatable: true, Privilege: ClausePrivilegePolicy.ToolchainOwnedSource, Migration: new("compiler-directive", ["internal", "intrinsic", "llvm_abi"]), Adapter: DeclarationAttachmentAdapterKind.CompilerDirective)
        };

    public static IReadOnlyDictionary<string, DeclarationClauseSpec> Entries => Specs;

    public static bool TryGetKind(string keyword, out DeclarationClauseKind kind) =>
        TryGet(keyword, out var spec, out kind);

    public static bool TryGet(string keyword, out DeclarationClauseSpec spec) =>
        Specs.TryGetValue(keyword, out spec!);

    public static bool TryGet(DeclarationClauseKind kind, out DeclarationClauseSpec spec)
    {
        spec = Specs.Values.FirstOrDefault(candidate => candidate.Kind == kind)!;
        return spec != null;
    }

    private static bool TryGet(string keyword, out DeclarationClauseSpec spec, out DeclarationClauseKind kind)
    {
        if (Specs.TryGetValue(keyword, out spec!))
        {
            kind = spec.Kind;
            return true;
        }

        kind = default;
        return false;
    }
}
