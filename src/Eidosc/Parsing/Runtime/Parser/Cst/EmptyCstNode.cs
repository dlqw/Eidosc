namespace Eidosc;

public class EmptyCstNode : ConcreteSyntaxNode
{
    public override string ToString()
    {
        return "Start";
    }

    protected override bool ShouldBePruned()
    {
        return false;
    }

    protected override bool ShouldBeUnpacked()
    {
        return true;
    }
}