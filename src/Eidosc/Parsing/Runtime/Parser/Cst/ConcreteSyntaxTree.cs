using Eidosc.Utilities;

namespace Eidosc;

public class ConcreteSyntaxTree(ConcreteSyntaxNode root)
{
    public readonly ConcreteSyntaxNode Root = root;
    public readonly LogMessageList ParserMessages = [];
}