using System.Xml;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;

namespace Eidosc.Ast.Declarations;

// Proof XML serialization and diagnostics
public partial record ProofDecl
{


    private static void CollectTerminalTokens(
        ConcreteSyntaxNode node,
        List<(string Text, Utils.SourceSpan Span)> tokens)
    {
        switch (node)
        {
            case TerminalCstNode terminal:
                tokens.Add((GetTokenText(terminal), terminal.Span));
                break;
            case NonTerminalCstNode nonTerminal:
                foreach (var child in nonTerminal.Children)
                {
                    CollectTerminalTokens(child, tokens);
                }
                break;
        }
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateDeclarationElement(doc, WellKnownStrings.XmlElements.ProofDecl);
        element.SetAttribute(WellKnownStrings.XmlAttributes.Name, Name);

        if (TypeParams.Count > 0)
        {
            var typeParamsElement = doc.CreateElement(WellKnownStrings.XmlElements.TypeParams);
            foreach (var param in TypeParams)
            {
                typeParamsElement.AppendChild(param.ToXmlElement(doc));
            }
            element.AppendChild(typeParamsElement);
        }

        if (Parameters.Count > 0)
        {
            var paramsElement = doc.CreateElement(WellKnownStrings.XmlElements.ProofParameters);
            foreach (var parameter in Parameters)
            {
                paramsElement.AppendChild(parameter.ToXmlElement(doc));
            }
            element.AppendChild(paramsElement);
        }

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

        if (HasBody)
        {
            var bodyElement = doc.CreateElement(WellKnownStrings.XmlElements.ProofTerm);
            bodyElement.SetAttribute(WellKnownStrings.XmlAttributes.Kind, BodyKind.ToString());
            if (BodyTerm != null)
            {
                bodyElement.AppendChild(BodyTerm.ToXmlElement(doc));
            }

            if (CaseExpression != null)
            {
                var caseExpressionElement = doc.CreateElement(WellKnownStrings.XmlElements.ProofCaseExpression);
                caseExpressionElement.AppendChild(CaseExpression.ToXmlElement(doc));
                bodyElement.AppendChild(caseExpressionElement);
            }

            if (Cases.Count > 0)
            {
                var casesElement = doc.CreateElement(WellKnownStrings.XmlElements.ProofCases);
                foreach (var proofCase in Cases)
                {
                    casesElement.AppendChild(proofCase.ToXmlElement(doc));
                }
                bodyElement.AppendChild(casesElement);
            }

            if (RewriteClause != null)
            {
                bodyElement.AppendChild(RewriteClause.ToXmlElement(doc));
            }

            element.AppendChild(bodyElement);
        }

        return element;
    }
}
