using Eidosc.Ast;
using Eidosc.Utils;

namespace Eidosc;

public class NonTerminalCstNode : ConcreteSyntaxNode
{
    public readonly NonTerminal NonTerminal;
    public EidosAstNode? AstNode;

    public NonTerminalCstNode(NonTerminal nonTerminal, SourceSpan span)
    {
        NonTerminal = nonTerminal;
        Span = span;
    }

    public override string ToString()
    {
        return NonTerminal.ToString();
    }

    protected override bool ShouldBePruned()
    {
        return NonTerminal.HasFlag(NonTerminalFlag.Pruning) && Children.Count == 0;
    }

    protected override bool ShouldBeUnpacked()
    {
        if (NonTerminal.HasFlag(NonTerminalFlag.Unpacking)) return true;
        if (NonTerminal.HasFlag(NonTerminalFlag.Squeezing) && Children.Count == 1) return true;

        return false;
    }
}