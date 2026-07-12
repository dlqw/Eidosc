using System.Xml;

namespace Eidosc.Ast.Declarations;

/// <summary>
/// 操作符声明：infixl 4 +++、infixr 5 ***、prefix 6 ~~~、postfix 7 !!!
/// </summary>
public record OperatorDecl : Declaration
{
    public enum OperatorFixity
    {
        InfixL,
        InfixR,
        Prefix,
        Postfix
    }

    public OperatorFixity Fixity { get; private set; }
    public int Precedence { get; private set; }
    public string OperatorSymbol { get; private set; } = "";

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        var children = GetChildren(node);

        // operatorDecl ::= ("infixl"|"infixr"|"prefix"|"postfix") intLiteral operatorSymbol
        var fixityText = ExtractFirstTokenText(children[0]) ?? "";
        Fixity = fixityText switch
        {
            "infixl" => OperatorFixity.InfixL,
            "infixr" => OperatorFixity.InfixR,
            "prefix" => OperatorFixity.Prefix,
            "postfix" => OperatorFixity.Postfix,
            _ => OperatorFixity.InfixL
        };

        var precedenceText = ExtractFirstTokenText(children[1]) ?? "4";
        Precedence = int.TryParse(precedenceText, out var p) ? p : 4;

        // 操作符符号可能是反引号包裹的标识符或裸符号序列
        OperatorSymbol = ExtractFirstTokenText(children[2]) ?? "";
        // 去掉反引号包裹
        if (OperatorSymbol.StartsWith('`') && OperatorSymbol.EndsWith('`') && OperatorSymbol.Length > 2)
            OperatorSymbol = OperatorSymbol[1..^1];
    }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "OperatorDecl");
        element.SetAttribute("fixity", Fixity.ToString());
        element.SetAttribute("precedence", Precedence.ToString());
        element.SetAttribute("symbol", OperatorSymbol);
        return element;
    }
}
