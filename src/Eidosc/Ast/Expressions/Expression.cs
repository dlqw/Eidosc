using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 表达式节点的抽象基类
/// </summary>
public abstract record Expression : EidosAstNode
{
    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        return CreateElement(doc, GetType().Name);
    }
}
