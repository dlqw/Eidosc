using System.Xml;
using Eidosc.Utilities;

namespace Eidosc.Ast.Expressions;

public record InfixCallExpr : Expression
{
    public EidosAstNode? Left { get; private set; }
    public string FunctionName { get; private set; } = "";
    public SymbolId FunctionSymbolId { get; set; } = SymbolId.None;
    public List<SymbolId> FunctionCandidateSymbolIds { get; private set; } = [];
    public EidosAstNode? Right { get; private set; }

    public void SetSpan(Utils.SourceSpan span) => Span = span;
    public void SetLeft(EidosAstNode left) => Left = left;
    public void SetRight(EidosAstNode right) => Right = right;
    public void SetFunctionName(string name) => FunctionName = name;
    public void ClearFunctionCandidates() => FunctionCandidateSymbolIds.Clear();

    public void AddFunctionCandidate(SymbolId symbolId)
    {
        if (symbolId.IsValid && !FunctionCandidateSymbolIds.Contains(symbolId))
        {
            FunctionCandidateSymbolIds.Add(symbolId);
        }
    }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;

        if (node is not NonTerminalCstNode ntNode) return;

        var operands = new List<EidosAstNode>();
        ExtractParts(ntNode, operands);

        Left = operands.Count > 0 ? operands[0] : null;
        Right = operands.Count > 1 ? operands[1] : null;
    }

    private void ExtractParts(NonTerminalCstNode node, List<EidosAstNode> operands)
    {
        foreach (var child in node.Children)
        {
            if (child is NonTerminalCstNode childNt)
            {
                if (childNt.AstNode is EidosAstNode expr)
                {
                    operands.Add(expr);
                    continue;
                }

                ExtractParts(childNt, operands);
                continue;
            }

            if (child is TerminalCstNode term)
            {
                var text = GetTokenText(term);
                if (term.Terminal?.ToString() is WellKnownStrings.Terminals.Identifier or WellKnownStrings.Terminals.OperatorIdentifier &&
                    !string.IsNullOrEmpty(text))
                {
                    SetFunctionName(text);
                }
            }
        }
    }

    private new static string GetTokenText(TerminalCstNode term)
    {
        return term.Token switch
        {
            ContentToken contentToken => contentToken.TextId.Resolve(),
            EofToken => "<eof>",
            ErrorToken errorToken => errorToken.Message,
            _ => term.Token?.ToString() ?? string.Empty
        };
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, WellKnownStrings.XmlElements.InfixCallExpr);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Function, FunctionName);

        if (Left != null)
        {
            var leftElement = doc.CreateElement(WellKnownStrings.XmlElements.Left);
            leftElement.AppendChild(Left.ToXmlElement(doc));
            element.AppendChild(leftElement);
        }

        if (Right != null)
        {
            var rightElement = doc.CreateElement(WellKnownStrings.XmlElements.Right);
            rightElement.AppendChild(Right.ToXmlElement(doc));
            element.AppendChild(rightElement);
        }

        return element;
    }
}
