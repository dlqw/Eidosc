using System.Xml;

namespace Eidosc.Ast;

/// <summary>
/// XML 序列化接口
/// </summary>
public interface IXmlSerializable
{
    XmlElement ToXmlElement(XmlDocument doc);
}
