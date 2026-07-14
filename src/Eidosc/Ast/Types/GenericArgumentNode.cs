using System.Xml;

namespace Eidosc.Ast.Types;

/// <summary>
/// Generic argument syntax before and after parameter-domain resolution.
/// </summary>
public abstract record GenericArgumentNode : EidosAstNode
{
    public abstract Eidosc.Types.GenericParameterKind? ResolvedKind { get; }

    public override void BuildFromCst(AstContext context, ConcreteSyntaxNode node)
    {
        Span = node.Span;
    }
}

public sealed record UnresolvedGenericArgumentNode : GenericArgumentNode
{
    public TypeNode? TypeCandidate { get; init; }

    public EidosAstNode? ValueCandidate { get; init; }

    public override Eidosc.Types.GenericParameterKind? ResolvedKind => null;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "UnresolvedGenericArgument");
        if (TypeCandidate != null)
        {
            element.AppendChild(TypeCandidate.ToXmlElement(doc));
        }
        else if (ValueCandidate != null)
        {
            element.AppendChild(ValueCandidate.ToXmlElement(doc));
        }

        return element;
    }
}

public sealed record TypeGenericArgumentNode : GenericArgumentNode
{
    public required TypeNode Type { get; init; }

    public override Eidosc.Types.GenericParameterKind? ResolvedKind => Eidosc.Types.GenericParameterKind.Type;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "TypeGenericArgument");
        element.AppendChild(Type.ToXmlElement(doc));
        return element;
    }
}

public sealed record ValueGenericArgumentNode : GenericArgumentNode
{
    public required EidosAstNode Expression { get; init; }

    public override Eidosc.Types.GenericParameterKind? ResolvedKind => Eidosc.Types.GenericParameterKind.Value;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "ValueGenericArgument");
        element.AppendChild(Expression.ToXmlElement(doc));
        return element;
    }
}

public sealed record EffectGenericArgumentNode : GenericArgumentNode
{
    public required TypeNode EffectRow { get; init; }

    public override Eidosc.Types.GenericParameterKind? ResolvedKind => Eidosc.Types.GenericParameterKind.EffectRow;

    public override XmlElement ToXmlElement(XmlDocument doc)
    {
        var element = CreateElement(doc, "EffectGenericArgument");
        element.AppendChild(EffectRow.ToXmlElement(doc));
        return element;
    }
}
