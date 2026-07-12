using System.Xml;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// Represents a compiler-checked proof declaration.
/// </summary>
public partial record ProofDecl : Declaration
{
    /// <summary>
    /// Gets the proof declaration name.
    /// </summary>
    public string Name { get; private set; } = "";

    /// <summary>
    /// Gets the proof-local type parameters.
    /// </summary>
    public List<TypeParam> TypeParams { get; private set; } = [];

    /// <summary>
    /// Gets the explicitly quantified value parameters used by the proposition.
    /// </summary>
    public List<ProofParameter> Parameters { get; private set; } = [];

    /// <summary>
    /// Gets the parsed source proposition.
    /// </summary>
    public ProofPropositionClause? SourceProposition { get; private set; }

    /// <summary>
    /// Gets the left-hand side of the equality proposition.
    /// </summary>
    public EidosAstNode? Left { get; private set; }

    /// <summary>
    /// Gets the right-hand side of the equality proposition.
    /// </summary>
    public EidosAstNode? Right { get; private set; }

    /// <summary>
    /// Gets a value indicating whether the proof proposition is the built-in true proposition.
    /// </summary>
    public bool IsTrueProposition { get; private set; }

    /// <summary>
    /// Gets the proof term accepted by the checker.
    /// </summary>
    public ProofTermKind BodyKind { get; private set; } = ProofTermKind.Refl;

    /// <summary>
    /// Gets the parsed source proof term tree when the body contains nested proof evidence.
    /// </summary>
    public ProofTermClause? BodyTerm { get; private set; }

    /// <summary>
    /// Gets the expression scrutinized by a proof case split.
    /// </summary>
    public EidosAstNode? CaseExpression { get; private set; }

    /// <summary>
    /// Gets the proof branches used by a proof case split.
    /// </summary>
    public List<ProofCase> Cases { get; private set; } = [];

    /// <summary>
    /// Gets the explicit rewrite clause used by this proof body.
    /// </summary>
    public ProofRewriteClause? RewriteClause { get; private set; }

    /// <summary>
    /// Gets a value that indicates whether the proof declaration provides proof evidence.
    /// </summary>
    public bool HasBody { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        ExtractAttributes(context, node);
        ExtractExportModifier(node);

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        var propositionCandidates = new List<EidosAstNode>();
        foreach (var child in ntNode.Children)
        {
            CollectFromNode(child, propositionCandidates, inProofCasesBody: false);
        }

        var equality = propositionCandidates
            .OfType<BinaryExpr>()
            .FirstOrDefault(binary => binary.Operator == BinaryOp.Equal);
        if (equality is { Left: not null, Right: not null })
        {
            SetProposition(equality.Left, equality.Right);
        }
        else if (propositionCandidates.Count == 1 &&
                 propositionCandidates[0] is IdentifierExpr { Name: WellKnownStrings.Keywords.TrueProposition })
        {
            SetTrueProposition();
        }
        else if (propositionCandidates.Count >= 2)
        {
            SetProposition(propositionCandidates[0], propositionCandidates[1]);
        }

        if (Parameters.Count == 0 || Parameters.Any(static parameter => parameter.TypeAnnotation == null))
        {
            RepairProofParametersFromTokens(ntNode);
        }

        RepairProofBodyKindFromTokens(ntNode);
    }

    private void CollectFromNode(
        ConcreteSyntaxNode node,
        List<EidosAstNode> propositionCandidates,
        bool inProofCasesBody)
    {
        switch (node)
        {
            case TerminalCstNode term:
            {
                var text = GetTokenText(term);
                if (inProofCasesBody &&
                    CaseExpression == null &&
                    IsIdentifierTerminal(term) &&
                    text is not WellKnownStrings.Keywords.By
                        and not WellKnownStrings.Keywords.Cases
                        and not WellKnownStrings.Keywords.Induction
                        and not WellKnownStrings.Keywords.Refl)
                {
                    var identifier = new IdentifierExpr();
                    identifier.SetSpan(term.Span);
                    identifier.SetName(text);
                    CaseExpression = identifier;
                    BodyKind = ProofTermKind.Cases;
                    HasBody = true;
                    break;
                }

                if (string.IsNullOrWhiteSpace(Name) &&
                    !IsPunctuation(text) &&
                    text is not WellKnownStrings.Keywords.Proof
                        and not WellKnownStrings.Keywords.Export
                        and not WellKnownStrings.Keywords.Forall
                        and not WellKnownStrings.Keywords.Refl
                        and not WellKnownStrings.Keywords.Rewrite
                        and not WellKnownStrings.Keywords.Simp
                        and not WellKnownStrings.Keywords.TodoProof
                        and not WellKnownStrings.Keywords.Exact
                        and not WellKnownStrings.Keywords.Apply
                        and not WellKnownStrings.Keywords.Symm
                        and not WellKnownStrings.Keywords.Trans
                        and not WellKnownStrings.Keywords.Congr
                        and not WellKnownStrings.Keywords.Ext
                        and not WellKnownStrings.Keywords.Have
                        and not WellKnownStrings.Keywords.Calc
                        and not WellKnownStrings.Keywords.Trivial
                        and not WellKnownStrings.Keywords.Intro
                        and not WellKnownStrings.Keywords.Constructor
                        and not WellKnownStrings.Keywords.First
                        and not WellKnownStrings.Keywords.Second
                        and not WellKnownStrings.Keywords.Contradiction
                        and not WellKnownStrings.Keywords.TrueProposition
                        and not WellKnownStrings.Keywords.FalseProposition
                        and not WellKnownStrings.Keywords.AndProposition
                        and not WellKnownStrings.Keywords.OrProposition
                        and not WellKnownStrings.Keywords.By
                        and not WellKnownStrings.Keywords.Cases
                        and not WellKnownStrings.Keywords.Induction)
                {
                    Name = text;
                }

                if (text == WellKnownStrings.Keywords.Refl)
                {
                    BodyKind = ProofTermKind.Refl;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Rewrite)
                {
                    BodyKind = ProofTermKind.Rewrite;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Simp)
                {
                    BodyKind = ProofTermKind.Simp;
                    HasBody = true;
                }

                if (text is "_" or WellKnownStrings.Keywords.TodoProof)
                {
                    BodyKind = ProofTermKind.Hole;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Exact)
                {
                    BodyKind = ProofTermKind.Exact;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Apply)
                {
                    BodyKind = ProofTermKind.Apply;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Symm)
                {
                    BodyKind = ProofTermKind.Symm;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Trans)
                {
                    BodyKind = ProofTermKind.Trans;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Congr)
                {
                    BodyKind = ProofTermKind.Congr;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Ext)
                {
                    BodyKind = ProofTermKind.Ext;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Have)
                {
                    BodyKind = ProofTermKind.Have;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Calc)
                {
                    BodyKind = ProofTermKind.Calc;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Trivial)
                {
                    BodyKind = ProofTermKind.Trivial;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Constructor)
                {
                    BodyKind = ProofTermKind.Constructor;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Contradiction)
                {
                    BodyKind = ProofTermKind.Contradiction;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.First)
                {
                    BodyKind = ProofTermKind.First;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Second)
                {
                    BodyKind = ProofTermKind.Second;
                    HasBody = true;
                }

                if (text == WellKnownStrings.Keywords.Induction)
                {
                    BodyKind = ProofTermKind.Induction;
                    HasBody = true;
                }

                break;
            }

            case NonTerminalCstNode { AstNode: TypeParam typeParam }:
                TypeParams.Add(typeParam);
                break;

            case NonTerminalCstNode { AstNode: ProofParameter parameter }:
                Parameters.Add(parameter);
                break;

            case NonTerminalCstNode { AstNode: ProofCase proofCase }:
                Cases.Add(proofCase);
                if (BodyKind != ProofTermKind.Induction)
                {
                    BodyKind = ProofTermKind.Cases;
                }
                HasBody = true;
                break;

            case NonTerminalCstNode { AstNode: ProofRewriteClause rewriteClause }:
                RewriteClause = rewriteClause;
                BodyKind = ProofTermKind.Rewrite;
                HasBody = true;
                break;

            case NonTerminalCstNode { AstNode: ProofTermClause proofTerm }:
                BodyTerm = proofTerm;
                BodyKind = proofTerm.Kind;
                HasBody = true;
                break;

            case NonTerminalCstNode { AstNode: EidosAstNode astNode } when astNode is not TypeNode:
                if (inProofCasesBody)
                {
                    CaseExpression ??= astNode;
                    if (BodyKind != ProofTermKind.Induction)
                    {
                        BodyKind = ProofTermKind.Cases;
                    }
                    HasBody = true;
                }
                else
                {
                    propositionCandidates.Add(astNode);
                }
                break;

            case NonTerminalCstNode ntNode:
                var isProofCasesBody = string.Equals(
                    ntNode.NonTerminal?.DebugName,
                    "proofCasesBody",
                    StringComparison.Ordinal);
                foreach (var child in ntNode.Children)
                {
                    CollectFromNode(child, propositionCandidates, inProofCasesBody || isProofCasesBody);
                }
                break;
        }
    }

    internal void SetName(string name) => Name = name;
    internal void SetTypeParams(List<TypeParam> typeParams) => TypeParams = typeParams;
    internal void SetParameters(List<ProofParameter> parameters) => Parameters = parameters;
    internal void SetProposition(EidosAstNode left, EidosAstNode right)
    {
        Left = left;
        Right = right;
        IsTrueProposition = false;
        var proposition = new ProofPropositionClause();
        proposition.SetSpan(Span);
        proposition.SetEquality(left, right);
        SourceProposition = proposition;
    }
    internal void SetTrueProposition()
    {
        Left = null;
        Right = null;
        IsTrueProposition = true;
        var proposition = new ProofPropositionClause();
        proposition.SetSpan(Span);
        proposition.SetTrue();
        SourceProposition = proposition;
    }
    internal void SetSourceProposition(ProofPropositionClause proposition)
    {
        SourceProposition = proposition;
        IsTrueProposition = proposition.Kind == ProofPropositionKind.True;
        if (proposition.Kind == ProofPropositionKind.Equality)
        {
            Left = proposition.LeftExpression;
            Right = proposition.RightExpression;
        }
        else
        {
            Left = null;
            Right = null;
        }
    }
    internal void SetBodyKind(ProofTermKind bodyKind) => BodyKind = bodyKind;
    internal void SetBodyTerm(ProofTermClause? bodyTerm) => BodyTerm = bodyTerm;
    internal void SetHasBody(bool hasBody) => HasBody = hasBody;
    internal void SetCaseExpression(EidosAstNode? caseExpression) => CaseExpression = caseExpression;
    internal void SetCases(List<ProofCase> cases) => Cases = cases;
    internal void SetRewriteClause(ProofRewriteClause? rewriteClause) => RewriteClause = rewriteClause;

    private void RepairProofParametersFromTokens(NonTerminalCstNode node)
    {
        var tokens = new List<(string Text, Utils.SourceSpan Span)>();
        CollectTerminalTokens(node, tokens);
        var forallIndex = tokens.FindIndex(token => TokenTextMatches(token.Text, WellKnownStrings.Keywords.Forall));
        if (forallIndex < 0)
        {
            return;
        }

        var repaired = new List<ProofParameter>();
        for (var i = forallIndex + 1; i < tokens.Count;)
        {
            if (TokenTextMatches(tokens[i].Text, "."))
            {
                break;
            }

            var nameToken = tokens[i++];
            if (IsProofPunctuation(nameToken.Text) || TokenTextMatches(nameToken.Text, ","))
            {
                continue;
            }

            while (i < tokens.Count &&
                   !TokenTextMatches(tokens[i].Text, ":") &&
                   !TokenTextMatches(tokens[i].Text, "."))
            {
                i++;
            }

            if (i >= tokens.Count || !TokenTextMatches(tokens[i].Text, ":"))
            {
                break;
            }

            i++;
            while (i < tokens.Count &&
                   IsProofPunctuation(tokens[i].Text) &&
                   !TokenTextMatches(tokens[i].Text, "."))
            {
                i++;
            }

            if (i >= tokens.Count || TokenTextMatches(tokens[i].Text, "."))
            {
                break;
            }

            var typeToken = tokens[i++];
            var typePath = new TypePath();
            typePath.SetTypeName(typeToken.Text);
            typePath.SetSpan(typeToken.Span);

            var parameter = new ProofParameter();
            parameter.SetSpan(nameToken.Span);
            parameter.SetName(nameToken.Text);
            parameter.SetTypeAnnotation(typePath);
            repaired.Add(parameter);

            while (i < tokens.Count &&
                   !TokenTextMatches(tokens[i].Text, ",") &&
                   !TokenTextMatches(tokens[i].Text, "."))
            {
                i++;
            }

            if (i < tokens.Count && TokenTextMatches(tokens[i].Text, ","))
            {
                i++;
            }
        }

        if (repaired.Count > 0)
        {
            Parameters = repaired;
        }
    }

    private void RepairProofBodyKindFromTokens(NonTerminalCstNode node)
    {
        if (!HasBody)
        {
            return;
        }

        var tokens = new List<(string Text, Utils.SourceSpan Span)>();
        CollectTerminalTokens(node, tokens);
        for (var i = 0; i < tokens.Count - 1; i++)
        {
            if (!TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.By))
            {
                continue;
            }

            if (TokenTextMatches(tokens[i + 1].Text, WellKnownStrings.Keywords.Induction))
            {
                BodyKind = ProofTermKind.Induction;
            }
            else if (TokenTextMatches(tokens[i + 1].Text, WellKnownStrings.Keywords.Cases))
            {
                BodyKind = ProofTermKind.Cases;
            }

            return;
        }

        for (var i = 0; i < tokens.Count; i++)
        {
            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.Rewrite))
            {
                BodyKind = ProofTermKind.Rewrite;
                return;
            }

            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.Exact))
            {
                BodyKind = ProofTermKind.Exact;
                return;
            }

            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.Apply))
            {
                BodyKind = ProofTermKind.Apply;
                return;
            }

            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.Symm))
            {
                BodyKind = ProofTermKind.Symm;
                return;
            }

            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.Trans))
            {
                BodyKind = ProofTermKind.Trans;
                return;
            }

            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.Congr))
            {
                BodyKind = ProofTermKind.Congr;
                return;
            }

            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.Ext))
            {
                BodyKind = ProofTermKind.Ext;
                return;
            }

            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.Have))
            {
                BodyKind = ProofTermKind.Have;
                return;
            }

            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.Calc))
            {
                BodyKind = ProofTermKind.Calc;
                return;
            }

            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.Trivial))
            {
                BodyKind = ProofTermKind.Trivial;
                return;
            }

            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.First))
            {
                BodyKind = ProofTermKind.First;
                return;
            }

            if (TokenTextMatches(tokens[i].Text, WellKnownStrings.Keywords.Second))
            {
                BodyKind = ProofTermKind.Second;
                return;
            }
        }
    }

    private static bool IsProofPunctuation(string text)
    {
        return IsPunctuation(text) ||
               text is "colon" or "comma" or "dot" or "lparen" or "rparen" or "lbrack" or "rbrack" or "lbrace" or "rbrace";
    }

    private static bool IsIdentifierTerminal(TerminalCstNode term)
    {
        return term.Terminal?.ToString() == WellKnownStrings.Terminals.Identifier;
    }

    private static bool TokenTextMatches(string text, string expected)
    {
        return string.Equals(text, expected, StringComparison.Ordinal) ||
               text.EndsWith(expected, StringComparison.Ordinal) ||
               text.Contains($"Token:{expected}", StringComparison.Ordinal);
    }
}

/// <summary>
/// Represents a parsed proof proposition before proof elaboration.
/// </summary>
public record ProofPropositionClause : EidosAstNode
{
    /// <summary>
    /// Gets the source proposition kind.
    /// </summary>
    public ProofPropositionKind Kind { get; private set; } = ProofPropositionKind.Equality;

    /// <summary>
    /// Gets the left expression of an equality proposition.
    /// </summary>
    public EidosAstNode? LeftExpression { get; private set; }

    /// <summary>
    /// Gets the right expression of an equality proposition.
    /// </summary>
    public EidosAstNode? RightExpression { get; private set; }

    /// <summary>
    /// Gets the premise of an implication proposition.
    /// </summary>
    public ProofPropositionClause? Premise { get; private set; }

    /// <summary>
    /// Gets the conclusion of an implication proposition.
    /// </summary>
    public ProofPropositionClause? Conclusion { get; private set; }

    /// <summary>
    /// Gets the parameter bound by an existential proposition.
    /// </summary>
    public ProofParameter? ExistsParameter { get; private set; }

    /// <summary>
    /// Gets the body of an existential proposition.
    /// </summary>
    public ProofPropositionClause? ExistsBody { get; private set; }

    /// <summary>
    /// Gets the parameter bound by a universal proposition.
    /// </summary>
    public ProofParameter? ForallParameter { get; private set; }

    /// <summary>
    /// Gets the body of a universal proposition.
    /// </summary>
    public ProofPropositionClause? ForallBody { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        _ = context;
        Span = node.Span;
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;

    internal void SetTrue()
    {
        Kind = ProofPropositionKind.True;
        LeftExpression = null;
        RightExpression = null;
        Premise = null;
        Conclusion = null;
        ExistsParameter = null;
        ExistsBody = null;
        ForallParameter = null;
        ForallBody = null;
    }

    internal void SetFalse()
    {
        Kind = ProofPropositionKind.False;
        LeftExpression = null;
        RightExpression = null;
        Premise = null;
        Conclusion = null;
        ExistsParameter = null;
        ExistsBody = null;
        ForallParameter = null;
        ForallBody = null;
    }

    internal void SetEquality(EidosAstNode left, EidosAstNode right)
    {
        Kind = ProofPropositionKind.Equality;
        LeftExpression = left;
        RightExpression = right;
        Premise = null;
        Conclusion = null;
        ExistsParameter = null;
        ExistsBody = null;
        ForallParameter = null;
        ForallBody = null;
    }

    internal void SetImplication(ProofPropositionClause premise, ProofPropositionClause conclusion)
    {
        Kind = ProofPropositionKind.Implies;
        LeftExpression = null;
        RightExpression = null;
        Premise = premise;
        Conclusion = conclusion;
        ExistsParameter = null;
        ExistsBody = null;
        ForallParameter = null;
        ForallBody = null;
    }

    internal void SetAnd(ProofPropositionClause left, ProofPropositionClause right)
    {
        Kind = ProofPropositionKind.And;
        LeftExpression = null;
        RightExpression = null;
        Premise = left;
        Conclusion = right;
        ExistsParameter = null;
        ExistsBody = null;
        ForallParameter = null;
        ForallBody = null;
    }

    internal void SetOr(ProofPropositionClause left, ProofPropositionClause right)
    {
        Kind = ProofPropositionKind.Or;
        LeftExpression = null;
        RightExpression = null;
        Premise = left;
        Conclusion = right;
        ExistsParameter = null;
        ExistsBody = null;
        ForallParameter = null;
        ForallBody = null;
    }

    internal void SetExists(ProofParameter parameter, ProofPropositionClause body)
    {
        Kind = ProofPropositionKind.Exists;
        LeftExpression = null;
        RightExpression = null;
        Premise = null;
        Conclusion = null;
        ExistsParameter = parameter;
        ExistsBody = body;
        ForallParameter = null;
        ForallBody = null;
    }

    internal void SetForall(ProofParameter parameter, ProofPropositionClause body)
    {
        Kind = ProofPropositionKind.Forall;
        LeftExpression = null;
        RightExpression = null;
        Premise = null;
        Conclusion = null;
        ExistsParameter = null;
        ExistsBody = null;
        ForallParameter = parameter;
        ForallBody = body;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "ProofProposition");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Kind, Kind.ToString());
        return element;
    }
}

/// <summary>
/// Identifies a source proof proposition form.
/// </summary>
public enum ProofPropositionKind
{
    /// <summary>
    /// Represents an equality proposition.
    /// </summary>
    Equality,

    /// <summary>
    /// Represents the built-in true proposition.
    /// </summary>
    True,

    /// <summary>
    /// Represents the built-in false proposition.
    /// </summary>
    False,

    /// <summary>
    /// Represents an implication proposition.
    /// </summary>
    Implies,

    /// <summary>
    /// Represents a conjunction proposition.
    /// </summary>
    And,

    /// <summary>
    /// Represents a disjunction proposition.
    /// </summary>
    Or,

    /// <summary>
    /// Represents an existential proposition.
    /// </summary>
    Exists,

    /// <summary>
    /// Represents a universal proposition.
    /// </summary>
    Forall
}

/// <summary>
/// Represents a source proof term, including nested proof evidence.
/// </summary>
public record ProofTermClause : EidosAstNode
{
    /// <summary>
    /// Gets the source proof term kind.
    /// </summary>
    public ProofTermKind Kind { get; private set; } = ProofTermKind.Refl;

    /// <summary>
    /// Gets the referenced lemma name for exact or rewrite proof terms.
    /// </summary>
    public string LemmaName { get; private set; } = "";

    /// <summary>
    /// Gets explicit type arguments supplied to an exact, apply, projection, case, exists-elim, or rewrite lemma.
    /// </summary>
    public List<TypeNode> TypeArguments { get; private set; } = [];

    /// <summary>
    /// Gets explicit value arguments supplied to an exact or rewrite lemma.
    /// </summary>
    public List<EidosAstNode> ValueArguments { get; private set; } = [];

    /// <summary>
    /// Gets the rewrite direction for rewrite proof terms.
    /// </summary>
    public ProofRewriteDirectionKind Direction { get; private set; } = ProofRewriteDirectionKind.Forward;

    /// <summary>
    /// Gets the side of the current goal targeted by rewrite proof terms.
    /// </summary>
    public ProofRewriteSideKind TargetSide { get; private set; } = ProofRewriteSideKind.WholeGoal;

    /// <summary>
    /// Gets the selected 1-based rewrite occurrence, or <see langword="null" /> for the first occurrence.
    /// </summary>
    public int? OccurrenceIndex { get; private set; }

    /// <summary>
    /// Gets the inner proof term for symmetry.
    /// </summary>
    public ProofTermClause? Inner { get; private set; }

    /// <summary>
    /// Gets the premise proofs supplied to an apply proof term.
    /// </summary>
    public List<ProofTermClause> ArgumentProofs { get; private set; } = [];

    /// <summary>
    /// Gets the local assumption name introduced by implication introduction.
    /// </summary>
    public string IntroName { get; private set; } = "";

    /// <summary>
    /// Gets the symbol bound by a universally quantified intro term.
    /// </summary>
    public Eidosc.SymbolId IntroSymbolId { get; private set; } = Eidosc.SymbolId.None;

    /// <summary>
    /// Gets the proof body checked after implication introduction.
    /// </summary>
    public ProofTermClause? IntroBody { get; private set; }

    /// <summary>
    /// Gets the variable introduced by function extensionality.
    /// </summary>
    public string ExtName { get; private set; } = "";

    /// <summary>
    /// Gets the proof body checked after function extensionality.
    /// </summary>
    public ProofTermClause? ExtBody { get; private set; }

    /// <summary>
    /// Gets the continuation proof term for rewrite.
    /// </summary>
    public ProofTermClause? Then { get; private set; }

    /// <summary>
    /// Gets the middle expression for transitivity.
    /// </summary>
    public EidosAstNode? MiddleExpression { get; private set; }

    /// <summary>
    /// Gets the local assumption name introduced by a have proof term.
    /// </summary>
    public string HaveName { get; private set; } = "";

    /// <summary>
    /// Gets the local assumption name introduced for the left branch of an or-elimination proof term.
    /// </summary>
    public string LeftAssumptionName { get; private set; } = "";

    /// <summary>
    /// Gets the local assumption name introduced for the right branch of an or-elimination proof term.
    /// </summary>
    public string RightAssumptionName { get; private set; } = "";

    /// <summary>
    /// Gets the local witness name introduced by an existential elimination proof term.
    /// </summary>
    public string ExistsElimWitnessName { get; private set; } = "";

    /// <summary>
    /// Gets the local expression name introduced by a proof let term.
    /// </summary>
    public string LetName { get; private set; } = "";

    /// <summary>
    /// Gets the expression bound by a proof let term.
    /// </summary>
    public EidosAstNode? LetValueExpression { get; private set; }

    /// <summary>
    /// Gets the body checked after a proof let term.
    /// </summary>
    public ProofTermClause? LetBody { get; private set; }

    /// <summary>
    /// Gets the local proof assumption name introduced by an existential elimination proof term.
    /// </summary>
    public string ExistsElimProofName { get; private set; } = "";

    /// <summary>
    /// Gets the body checked after existential elimination.
    /// </summary>
    public ProofTermClause? ExistsElimBody { get; private set; }

    /// <summary>
    /// Gets the left-hand side of a have proposition.
    /// </summary>
    public EidosAstNode? HaveLeft { get; private set; }

    /// <summary>
    /// Gets the right-hand side of a have proposition.
    /// </summary>
    public EidosAstNode? HaveRight { get; private set; }

    /// <summary>
    /// Gets the parsed proposition introduced by a have proof term.
    /// </summary>
    public ProofPropositionClause? HaveSourceProposition { get; private set; }

    /// <summary>
    /// Gets the proof for a have proposition.
    /// </summary>
    public ProofTermClause? HaveProof { get; private set; }

    /// <summary>
    /// Gets the body proof checked with the have assumption in scope.
    /// </summary>
    public ProofTermClause? HaveBody { get; private set; }

    /// <summary>
    /// Gets the starting expression of a calc chain.
    /// </summary>
    public EidosAstNode? CalcStart { get; private set; }

    /// <summary>
    /// Gets the ordered equality steps of a calc chain.
    /// </summary>
    public List<ProofCalcStep> CalcSteps { get; private set; } = [];

    /// <summary>
    /// Gets the left proof for transitivity.
    /// </summary>
    public ProofTermClause? LeftProof { get; private set; }

    /// <summary>
    /// Gets the right proof for transitivity.
    /// </summary>
    public ProofTermClause? RightProof { get; private set; }

    /// <summary>
    /// Gets the witness expression supplied to an existential introduction.
    /// </summary>
    public EidosAstNode? WitnessExpression { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        _ = context;
        Span = node.Span;
        var tokens = new List<string>();
        CollectTokenText(node, tokens);
        if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Apply, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Apply;
            LemmaName = FindFirstIdentifierAfter(tokens, WellKnownStrings.Keywords.Apply);
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Exact, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Exact;
            LemmaName = FindFirstIdentifierAfter(tokens, WellKnownStrings.Keywords.Exact);
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Symm, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Symm;
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Trans, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Trans;
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Congr, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Congr;
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Ext, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Ext;
            ExtName = FindFirstIdentifierAfter(tokens, WellKnownStrings.Keywords.Ext);
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Intro, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Intro;
            IntroName = FindFirstIdentifierAfter(tokens, WellKnownStrings.Keywords.Intro);
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Constructor, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Constructor;
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.First, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.First;
            LemmaName = FindFirstIdentifierAfter(tokens, WellKnownStrings.Keywords.First);
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Second, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Second;
            LemmaName = FindFirstIdentifierAfter(tokens, WellKnownStrings.Keywords.Second);
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Contradiction, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Contradiction;
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Exists, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Exists;
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Let, StringComparison.Ordinal)))
        {
            Kind = tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Exists, StringComparison.Ordinal))
                ? ProofTermKind.ExistsLet
                : ProofTermKind.Let;
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Left, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Left;
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Right, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Right;
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Cases, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.OrCases;
            LemmaName = FindFirstIdentifierAfter(tokens, WellKnownStrings.Keywords.Cases);
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Have, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Have;
            HaveName = FindFirstIdentifierAfter(tokens, WellKnownStrings.Keywords.Have);
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Calc, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Calc;
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Rewrite, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Rewrite;
            LemmaName = FindFirstIdentifierAfter(tokens, WellKnownStrings.Keywords.Rewrite);
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Simp, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Simp;
            LemmaName = FindFirstIdentifierAfter(tokens, WellKnownStrings.Keywords.Simp);
        }
        else if (tokens.Any(token => string.Equals(token, "_", StringComparison.Ordinal) ||
                                     string.Equals(token, WellKnownStrings.Keywords.TodoProof, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Hole;
        }
        else if (tokens.Any(token => string.Equals(token, WellKnownStrings.Keywords.Trivial, StringComparison.Ordinal)))
        {
            Kind = ProofTermKind.Trivial;
        }
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetKind(ProofTermKind kind) => Kind = kind;
    internal void SetLemmaName(string lemmaName) => LemmaName = lemmaName;
    internal void SetTypeArguments(List<TypeNode> typeArguments) => TypeArguments = typeArguments;
    internal void SetValueArguments(List<EidosAstNode> valueArguments) => ValueArguments = valueArguments;
    internal void SetDirection(ProofRewriteDirectionKind direction) => Direction = direction;
    internal void SetTargetSide(ProofRewriteSideKind targetSide) => TargetSide = targetSide;
    internal void SetOccurrenceIndex(int? occurrenceIndex) => OccurrenceIndex = occurrenceIndex;
    internal void SetInner(ProofTermClause? inner) => Inner = inner;
    internal void SetArgumentProofs(List<ProofTermClause> argumentProofs) => ArgumentProofs = argumentProofs;
    internal void SetIntroName(string introName) => IntroName = introName;
    internal void SetIntroSymbolId(Eidosc.SymbolId introSymbolId) => IntroSymbolId = introSymbolId;
    internal void SetIntroBody(ProofTermClause? introBody) => IntroBody = introBody;
    internal void SetExtName(string extName) => ExtName = extName;
    internal void SetExtBody(ProofTermClause? extBody) => ExtBody = extBody;
    internal void SetThen(ProofTermClause? then) => Then = then;
    internal void SetMiddleExpression(EidosAstNode? middleExpression) => MiddleExpression = middleExpression;
    internal void SetHaveName(string haveName) => HaveName = haveName;
    internal void SetLeftAssumptionName(string leftAssumptionName) => LeftAssumptionName = leftAssumptionName;
    internal void SetRightAssumptionName(string rightAssumptionName) => RightAssumptionName = rightAssumptionName;
    internal void SetLetName(string letName) => LetName = letName;
    internal void SetLetValueExpression(EidosAstNode? letValueExpression) => LetValueExpression = letValueExpression;
    internal void SetLetBody(ProofTermClause? letBody) => LetBody = letBody;
    internal void SetExistsElimWitnessName(string existsElimWitnessName) => ExistsElimWitnessName = existsElimWitnessName;
    internal void SetExistsElimProofName(string existsElimProofName) => ExistsElimProofName = existsElimProofName;
    internal void SetExistsElimBody(ProofTermClause? existsElimBody) => ExistsElimBody = existsElimBody;
    internal void SetHaveProposition(EidosAstNode left, EidosAstNode right)
    {
        HaveLeft = left;
        HaveRight = right;
        var proposition = new ProofPropositionClause();
        proposition.SetSpan(Span);
        proposition.SetEquality(left, right);
        HaveSourceProposition = proposition;
    }
    internal void SetHaveProposition(ProofPropositionClause proposition)
    {
        HaveSourceProposition = proposition;
        if (proposition.Kind == ProofPropositionKind.Equality)
        {
            HaveLeft = proposition.LeftExpression;
            HaveRight = proposition.RightExpression;
        }
        else
        {
            HaveLeft = null;
            HaveRight = null;
        }
    }
    internal void SetHaveProof(ProofTermClause? haveProof) => HaveProof = haveProof;
    internal void SetHaveBody(ProofTermClause? haveBody) => HaveBody = haveBody;
    internal void SetCalcStart(EidosAstNode? calcStart) => CalcStart = calcStart;
    internal void SetCalcSteps(List<ProofCalcStep> calcSteps) => CalcSteps = calcSteps;
    internal void SetLeftProof(ProofTermClause? leftProof) => LeftProof = leftProof;
    internal void SetRightProof(ProofTermClause? rightProof) => RightProof = rightProof;
    internal void SetWitnessExpression(EidosAstNode? witnessExpression) => WitnessExpression = witnessExpression;

    private static string FindFirstIdentifierAfter(IReadOnlyList<string> tokens, string marker)
    {
        var markerIndex = tokens.ToList().FindIndex(token => string.Equals(token, marker, StringComparison.Ordinal));
        if (markerIndex < 0)
        {
            return "";
        }

        for (var i = markerIndex + 1; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (!IsPunctuation(token) &&
                token is not WellKnownStrings.Punctuation.LeftArrow)
            {
                return token;
            }
        }

        return "";
    }

    private static void CollectTokenText(ConcreteSyntaxNode node, List<string> tokens)
    {
        switch (node)
        {
            case TerminalCstNode terminal:
                tokens.Add(GetTokenText(terminal));
                break;
            case NonTerminalCstNode nonTerminal:
                foreach (var child in nonTerminal.Children)
                {
                    CollectTokenText(child, tokens);
                }
                break;
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "ProofTermClause");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Kind, Kind.ToString());
        if (!string.IsNullOrWhiteSpace(LemmaName))
        {
            element.SetAttribute(WellKnownStrings.XmlAttributes.Name, LemmaName);
        }

        if (TypeArguments.Count > 0)
        {
            var typeArgsElement = doc.CreateElement("TypeArguments");
            foreach (var argument in TypeArguments)
            {
                typeArgsElement.AppendChild(argument.ToXmlElement(doc));
            }
            element.AppendChild(typeArgsElement);
        }

        if (ValueArguments.Count > 0)
        {
            var argsElement = doc.CreateElement("ValueArguments");
            foreach (var argument in ValueArguments)
            {
                argsElement.AppendChild(argument.ToXmlElement(doc));
            }
            element.AppendChild(argsElement);
        }

        if (OccurrenceIndex != null)
        {
            element.SetAttribute("occurrence", OccurrenceIndex.Value.ToString());
        }
        element.SetAttribute("targetSide", TargetSide.ToString());

        if (MiddleExpression != null)
        {
            var middleElement = doc.CreateElement("Middle");
            middleElement.AppendChild(MiddleExpression.ToXmlElement(doc));
            element.AppendChild(middleElement);
        }

        if (!string.IsNullOrWhiteSpace(HaveName))
        {
            element.SetAttribute("haveName", HaveName);
        }

        if (!string.IsNullOrWhiteSpace(IntroName))
        {
            element.SetAttribute("introName", IntroName);
        }

        if (!string.IsNullOrWhiteSpace(ExtName))
        {
            element.SetAttribute("extName", ExtName);
        }

        if (!string.IsNullOrWhiteSpace(LetName))
        {
            element.SetAttribute("letName", LetName);
        }

        if (LetValueExpression != null)
        {
            var letValueElement = doc.CreateElement("LetValue");
            letValueElement.AppendChild(LetValueExpression.ToXmlElement(doc));
            element.AppendChild(letValueElement);
        }

        if (HaveLeft != null)
        {
            var haveLeftElement = doc.CreateElement("HaveLeft");
            haveLeftElement.AppendChild(HaveLeft.ToXmlElement(doc));
            element.AppendChild(haveLeftElement);
        }

        if (HaveRight != null)
        {
            var haveRightElement = doc.CreateElement("HaveRight");
            haveRightElement.AppendChild(HaveRight.ToXmlElement(doc));
            element.AppendChild(haveRightElement);
        }

        if (CalcStart != null)
        {
            var calcStartElement = doc.CreateElement("CalcStart");
            calcStartElement.AppendChild(CalcStart.ToXmlElement(doc));
            element.AppendChild(calcStartElement);
        }

        if (CalcSteps.Count > 0)
        {
            var calcStepsElement = doc.CreateElement("CalcSteps");
            foreach (var step in CalcSteps)
            {
                calcStepsElement.AppendChild(step.ToXmlElement(doc));
            }
            element.AppendChild(calcStepsElement);
        }

        AppendChildTerm(doc, element, "Inner", Inner);
        if (ArgumentProofs.Count > 0)
        {
            var argumentProofsElement = doc.CreateElement("ArgumentProofs");
            foreach (var argumentProof in ArgumentProofs)
            {
                argumentProofsElement.AppendChild(argumentProof.ToXmlElement(doc));
            }

            element.AppendChild(argumentProofsElement);
        }

        AppendChildTerm(doc, element, "IntroBody", IntroBody);
        AppendChildTerm(doc, element, "ExtBody", ExtBody);
        AppendChildTerm(doc, element, "LetBody", LetBody);
        AppendChildTerm(doc, element, "Then", Then);
        AppendChildTerm(doc, element, "HaveProof", HaveProof);
        AppendChildTerm(doc, element, "HaveBody", HaveBody);
        AppendChildTerm(doc, element, "LeftProof", LeftProof);
        AppendChildTerm(doc, element, "RightProof", RightProof);
        if (WitnessExpression != null)
        {
            var witnessElement = doc.CreateElement("Witness");
            witnessElement.AppendChild(WitnessExpression.ToXmlElement(doc));
            element.AppendChild(witnessElement);
        }
        return element;
    }

    private static void AppendChildTerm(
        XmlDocument doc,
        XmlElement parent,
        string elementName,
        ProofTermClause? child)
    {
        if (child == null)
        {
            return;
        }

        var element = doc.CreateElement(elementName);
        element.AppendChild(child.ToXmlElement(doc));
        parent.AppendChild(element);
    }
}

/// <summary>
/// Represents one source-level step in a calc proof chain.
/// </summary>
public record ProofCalcStep : EidosAstNode
{
    /// <summary>
    /// Gets the expression reached by this calc step.
    /// </summary>
    public EidosAstNode? Target { get; private set; }

    /// <summary>
    /// Gets the proof lemma used to justify this step.
    /// </summary>
    public string LemmaName { get; private set; } = "";

    /// <summary>
    /// Gets explicit type arguments supplied to the step lemma.
    /// </summary>
    public List<TypeNode> TypeArguments { get; private set; } = [];

    /// <summary>
    /// Gets explicit value arguments supplied to the step lemma.
    /// </summary>
    public List<EidosAstNode> ValueArguments { get; private set; } = [];

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetTarget(EidosAstNode? target) => Target = target;
    internal void SetLemmaName(string lemmaName) => LemmaName = lemmaName;
    internal void SetTypeArguments(List<TypeNode> typeArguments) => TypeArguments = typeArguments;
    internal void SetValueArguments(List<EidosAstNode> valueArguments) => ValueArguments = valueArguments;

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        _ = context;
        Span = node.Span;
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "ProofCalcStep");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, LemmaName);
        if (Target != null)
        {
            var targetElement = doc.CreateElement("Target");
            targetElement.AppendChild(Target.ToXmlElement(doc));
            element.AppendChild(targetElement);
        }

        if (TypeArguments.Count > 0)
        {
            var typeArgsElement = doc.CreateElement("TypeArguments");
            foreach (var argument in TypeArguments)
            {
                typeArgsElement.AppendChild(argument.ToXmlElement(doc));
            }
            element.AppendChild(typeArgsElement);
        }

        if (ValueArguments.Count > 0)
        {
            var argsElement = doc.CreateElement("ValueArguments");
            foreach (var argument in ValueArguments)
            {
                argsElement.AppendChild(argument.ToXmlElement(doc));
            }
            element.AppendChild(argsElement);
        }

        return element;
    }
}

/// <summary>
/// Represents an explicit proof rewrite followed by a continuation proof term.
/// </summary>
public record ProofRewriteClause : EidosAstNode
{
    /// <summary>
    /// Gets the referenced lemma name.
    /// </summary>
    public string LemmaName { get; private set; } = "";

    /// <summary>
    /// Gets explicit type arguments supplied to the rewrite lemma.
    /// </summary>
    public List<TypeNode> TypeArguments { get; private set; } = [];

    /// <summary>
    /// Gets explicit value arguments supplied to the rewrite lemma.
    /// </summary>
    public List<EidosAstNode> ValueArguments { get; private set; } = [];

    /// <summary>
    /// Gets the continuation proof term.
    /// </summary>
    public ProofTermKind ThenKind { get; private set; } = ProofTermKind.Refl;

    /// <summary>
    /// Gets the rewrite direction.
    /// </summary>
    public ProofRewriteDirectionKind Direction { get; private set; } = ProofRewriteDirectionKind.Forward;

    /// <summary>
    /// Gets the side of the current goal targeted by the rewrite.
    /// </summary>
    public ProofRewriteSideKind TargetSide { get; private set; } = ProofRewriteSideKind.WholeGoal;

    /// <summary>
    /// Gets the selected 1-based rewrite occurrence, or <see langword="null" /> for the first occurrence.
    /// </summary>
    public int? OccurrenceIndex { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        var tokens = new List<string>();
        CollectTokenText(node, tokens);
        var rewriteIndex = tokens.FindIndex(static token =>
            string.Equals(token, WellKnownStrings.Keywords.Rewrite, StringComparison.Ordinal));
        if (rewriteIndex >= 0)
        {
            var i = rewriteIndex + 1;
            if (i < tokens.Count &&
                string.Equals(tokens[i], WellKnownStrings.Punctuation.LeftArrow, StringComparison.Ordinal))
            {
                Direction = ProofRewriteDirectionKind.Reverse;
                i++;
            }

            for (; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (string.Equals(token, WellKnownStrings.Keywords.Refl, StringComparison.Ordinal))
                {
                    ThenKind = ProofTermKind.Refl;
                    break;
                }

                if (!IsPunctuation(token))
                {
                    LemmaName = token;
                    break;
                }
            }

            var atIndex = tokens.FindIndex(static token =>
                string.Equals(token, WellKnownStrings.Keywords.At, StringComparison.Ordinal));
            if (atIndex >= 0 && atIndex + 1 < tokens.Count)
            {
                var targetToken = tokens[atIndex + 1];
                if (TryParseRewriteSide(targetToken, out var targetSide))
                {
                    TargetSide = targetSide;
                    if (atIndex + 2 < tokens.Count &&
                        int.TryParse(tokens[atIndex + 2], out var sideOccurrence))
                    {
                        OccurrenceIndex = sideOccurrence;
                    }
                }
                else if (int.TryParse(targetToken, out var occurrence))
                {
                    OccurrenceIndex = occurrence;
                }
            }
        }
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetLemmaName(string lemmaName) => LemmaName = lemmaName;
    internal void SetTypeArguments(List<TypeNode> typeArguments) => TypeArguments = typeArguments;
    internal void SetValueArguments(List<EidosAstNode> valueArguments) => ValueArguments = valueArguments;
    internal void SetThenKind(ProofTermKind thenKind) => ThenKind = thenKind;
    internal void SetDirection(ProofRewriteDirectionKind direction) => Direction = direction;
    internal void SetTargetSide(ProofRewriteSideKind targetSide) => TargetSide = targetSide;
    internal void SetOccurrenceIndex(int? occurrenceIndex) => OccurrenceIndex = occurrenceIndex;

    private static bool TryParseRewriteSide(string text, out ProofRewriteSideKind targetSide)
    {
        switch (text)
        {
            case "left":
                targetSide = ProofRewriteSideKind.LeftSide;
                return true;
            case "right":
                targetSide = ProofRewriteSideKind.RightSide;
                return true;
            case "whole":
            case "goal":
                targetSide = ProofRewriteSideKind.WholeGoal;
                return true;
            default:
                targetSide = ProofRewriteSideKind.WholeGoal;
                return false;
        }
    }

    private static void CollectTokenText(ConcreteSyntaxNode node, List<string> tokens)
    {
        switch (node)
        {
            case TerminalCstNode terminal:
                tokens.Add(GetTokenText(terminal));
                break;
            case NonTerminalCstNode nonTerminal:
                foreach (var child in nonTerminal.Children)
                {
                    CollectTokenText(child, tokens);
                }
                break;
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "ProofRewriteClause");
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, LemmaName);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Kind, ThenKind.ToString());
        element.SetAttribute("direction", Direction.ToString());
        element.SetAttribute("targetSide", TargetSide.ToString());
        if (TypeArguments.Count > 0)
        {
            var typeArgsElement = doc.CreateElement("TypeArguments");
            foreach (var argument in TypeArguments)
            {
                typeArgsElement.AppendChild(argument.ToXmlElement(doc));
            }
            element.AppendChild(typeArgsElement);
        }

        if (ValueArguments.Count > 0)
        {
            var argsElement = doc.CreateElement("ValueArguments");
            foreach (var argument in ValueArguments)
            {
                argsElement.AppendChild(argument.ToXmlElement(doc));
            }
            element.AppendChild(argsElement);
        }

        if (OccurrenceIndex != null)
        {
            element.SetAttribute("occurrence", OccurrenceIndex.Value.ToString());
        }
        return element;
    }
}

/// <summary>
/// Represents a single branch in a proof case split.
/// </summary>
public record ProofCase : EidosAstNode
{
    /// <summary>
    /// Gets the pattern that refines the case-split scrutinee.
    /// </summary>
    public Pattern? Pattern { get; private set; }

    /// <summary>
    /// Gets the proof term used by this branch.
    /// </summary>
    public ProofTermKind BodyKind { get; private set; } = ProofTermKind.Refl;

    /// <summary>
    /// Gets the parsed source proof term used by this branch.
    /// </summary>
    public ProofTermClause? BodyTerm { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        foreach (var child in ntNode.Children)
        {
            switch (child)
            {
                case TerminalCstNode term when GetTokenText(term) == WellKnownStrings.Keywords.Refl:
                    BodyKind = ProofTermKind.Refl;
                    break;

                case NonTerminalCstNode { AstNode: Pattern pattern }:
                    Pattern = Eidosc.Ast.Patterns.Pattern.NormalizePatternNode(pattern);
                    break;

                case NonTerminalCstNode { AstNode: ProofTermClause proofTerm }:
                    BodyTerm = proofTerm;
                    BodyKind = proofTerm.Kind;
                    break;

                case NonTerminalCstNode childNode:
                    CollectProofCasePattern(childNode);
                    break;
            }
        }
    }

    private void CollectProofCasePattern(NonTerminalCstNode node)
    {
        if (Pattern != null)
        {
            return;
        }

        if (node.AstNode is Pattern pattern)
        {
            Pattern = Eidosc.Ast.Patterns.Pattern.NormalizePatternNode(pattern);
            return;
        }

        foreach (var child in node.Children.OfType<NonTerminalCstNode>())
        {
            CollectProofCasePattern(child);
        }
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetPattern(Pattern pattern) => Pattern = Eidosc.Ast.Patterns.Pattern.NormalizePatternNode(pattern);
    internal void SetBodyKind(ProofTermKind bodyKind) => BodyKind = bodyKind;
    internal void SetBodyTerm(ProofTermClause? bodyTerm)
    {
        BodyTerm = bodyTerm;
        if (bodyTerm != null)
        {
            BodyKind = bodyTerm.Kind;
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.ProofCase);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Kind, BodyKind.ToString());

        if (Pattern != null)
        {
            var patternElement = doc.CreateElement(WellKnownStrings.XmlElements.Pattern);
            patternElement.AppendChild(Pattern.ToXmlElement(doc));
            element.AppendChild(patternElement);
        }

        if (BodyTerm != null)
        {
            element.AppendChild(BodyTerm.ToXmlElement(doc));
        }

        return element;
    }
}

/// <summary>
/// Represents a quantified value parameter in a proof proposition.
/// </summary>
public record ProofParameter : EidosAstNode
{
    /// <summary>
    /// Gets the quantified parameter name.
    /// </summary>
    public string Name { get; private set; } = "";

    /// <summary>
    /// Gets the required type annotation for the quantified parameter.
    /// </summary>
    public TypeNode? TypeAnnotation { get; private set; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is not NonTerminalCstNode ntNode)
        {
            return;
        }

        foreach (var child in ntNode.Children)
        {
            switch (child)
            {
                case TerminalCstNode term:
                {
                    var text = GetTokenText(term);
                    if (string.IsNullOrWhiteSpace(Name) && !IsPunctuation(text))
                    {
                        Name = text;
                    }
                    else if (TypeAnnotation == null &&
                             !string.IsNullOrWhiteSpace(text) &&
                             !IsPunctuation(text))
                    {
                        var typePath = new TypePath();
                        typePath.SetTypeName(text);
                        typePath.SetSpan(term.Span);
                        TypeAnnotation = typePath;
                    }
                    break;
                }
                case NonTerminalCstNode { AstNode: TypeNode typeNode }:
                    TypeAnnotation = typeNode;
                    break;
                case NonTerminalCstNode childNt:
                    CollectTypeAnnotation(childNt);
                    break;
            }
        }
    }

    private void CollectTypeAnnotation(NonTerminalCstNode node)
    {
        if (TypeAnnotation != null)
        {
            return;
        }

        if (node.AstNode is TypeNode typeNode)
        {
            TypeAnnotation = typeNode;
            return;
        }

        foreach (var child in node.Children)
        {
            switch (child)
            {
                case NonTerminalCstNode childNode:
                    CollectTypeAnnotation(childNode);
                    break;
                case TerminalCstNode term:
                {
                    var text = GetTokenText(term);
                    if (!string.IsNullOrWhiteSpace(text) &&
                        !IsPunctuation(text) &&
                        !string.Equals(text, Name, StringComparison.Ordinal))
                    {
                        var typePath = new TypePath();
                        typePath.SetTypeName(text);
                        typePath.SetSpan(term.Span);
                        TypeAnnotation = typePath;
                    }
                    break;
                }
            }
        }
    }

    internal void SetSpan(Utils.SourceSpan span) => Span = span;
    internal void SetName(string name) => Name = name;
    internal void SetTypeAnnotation(TypeNode typeAnnotation) => TypeAnnotation = typeAnnotation;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.ProofParameter);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (TypeAnnotation != null)
        {
            var typeElement = doc.CreateElement(WellKnownStrings.XmlElements.Type);
            typeElement.AppendChild(TypeAnnotation.ToXmlElement(doc));
            element.AppendChild(typeElement);
        }

        return element;
    }
}

/// <summary>
/// Identifies the proof term form used by a proof declaration.
/// </summary>
public enum ProofTermKind
{
    /// <summary>
    /// Represents reflexivity, requiring both equality sides to be definitionally identical.
    /// </summary>
    Refl,

    /// <summary>
    /// Represents rewriting the current goal with a previously proved lemma.
    /// </summary>
    Rewrite,

    /// <summary>
    /// Represents source-level simplification that elaborates to explicit rewrite proof terms.
    /// </summary>
    Simp,

    /// <summary>
    /// Represents an explicit incomplete proof placeholder.
    /// </summary>
    Hole,

    /// <summary>
    /// Represents using a previously proved lemma as the current goal.
    /// </summary>
    Exact,

    /// <summary>
    /// Represents applying an implication lemma and proving its premise.
    /// </summary>
    Apply,

    /// <summary>
    /// Represents proving an equality by symmetry.
    /// </summary>
    Symm,

    /// <summary>
    /// Represents proving an equality by transitivity through a middle expression.
    /// </summary>
    Trans,

    /// <summary>
    /// Represents proving application equality by congruence over argument proofs.
    /// </summary>
    Congr,

    /// <summary>
    /// Represents proving function equality by introducing an arbitrary argument.
    /// </summary>
    Ext,

    /// <summary>
    /// Represents proving and binding an intermediate proposition before checking a body proof.
    /// </summary>
    Have,

    /// <summary>
    /// Represents binding a pure local expression before checking a body proof.
    /// </summary>
    Let,

    /// <summary>
    /// Represents a source-level equality chain that elaborates to transitivity proof terms.
    /// </summary>
    Calc,

    /// <summary>
    /// Represents proving the built-in true proposition.
    /// </summary>
    Trivial,

    /// <summary>
    /// Represents implication introduction by adding a local assumption.
    /// </summary>
    Intro,

    /// <summary>
    /// Represents introducing a proposition through its canonical constructor.
    /// </summary>
    Constructor,

    /// <summary>
    /// Represents eliminating a conjunction by selecting its first proposition.
    /// </summary>
    First,

    /// <summary>
    /// Represents eliminating a conjunction by selecting its second proposition.
    /// </summary>
    Second,

    /// <summary>
    /// Represents proving a goal from an explicit false assumption.
    /// </summary>
    Contradiction,

    /// <summary>
    /// Represents introducing an existential proposition with a witness.
    /// </summary>
    Exists,

    /// <summary>
    /// Represents eliminating an existential proposition by opening its witness and proof.
    /// </summary>
    ExistsLet,

    /// <summary>
    /// Represents introducing the left side of a disjunction.
    /// </summary>
    Left,

    /// <summary>
    /// Represents introducing the right side of a disjunction.
    /// </summary>
    Right,

    /// <summary>
    /// Represents eliminating a disjunction by proving the current goal in both branches.
    /// </summary>
    OrCases,

    /// <summary>
    /// Represents case splitting over a scrutinee, with each branch checked by reflexivity.
    /// </summary>
    Cases,

    /// <summary>
    /// Represents structural induction over a scrutinee, with each branch checked by reflexivity and induction hypotheses.
    /// </summary>
    Induction
}

/// <summary>
/// Identifies the source-level direction of an explicit rewrite proof clause.
/// </summary>
public enum ProofRewriteDirectionKind
{
    /// <summary>
    /// Rewrites from the lemma left-hand side to the right-hand side.
    /// </summary>
    Forward,

    /// <summary>
    /// Rewrites from the lemma right-hand side to the left-hand side.
    /// </summary>
    Reverse
}

/// <summary>
/// Identifies the source-level side targeted by an explicit rewrite proof clause.
/// </summary>
public enum ProofRewriteSideKind
{
    /// <summary>
    /// Tries the left side first and then the right side of the current equality goal.
    /// </summary>
    WholeGoal,

    /// <summary>
    /// Rewrites only the left side of the current equality goal.
    /// </summary>
    LeftSide,

    /// <summary>
    /// Rewrites only the right side of the current equality goal.
    /// </summary>
    RightSide
}
