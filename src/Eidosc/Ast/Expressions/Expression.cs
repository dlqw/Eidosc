using System.Xml;

namespace Eidosc.Ast.Expressions;

/// <summary>
/// 表达式节点的抽象基类
/// </summary>
public abstract record Expression : EidosAstNode
{
    /// <summary>
    /// Exact semantic type denoted by this expression when it is used as a compile-time type value.
    /// </summary>
    public Eidosc.Types.Type? ReflectedType { get; internal set; }

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        return CreateElement(doc, GetType().Name);
    }
}
