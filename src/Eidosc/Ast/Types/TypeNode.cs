using System.Xml;

namespace Eidosc.Ast.Types;

/// <summary>
/// 类型节点的抽象基类
/// </summary>
public abstract record TypeNode : EidosAstNode
{
    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        return CreateElement(doc, GetType().Name);
    }
}
