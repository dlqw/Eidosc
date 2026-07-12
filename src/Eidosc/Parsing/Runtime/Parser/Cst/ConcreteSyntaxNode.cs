using Eidosc.Utils;

namespace Eidosc;

public abstract class ConcreteSyntaxNode
{
    public SourceSpan Span;

    public List<ConcreteSyntaxNode> Children { get; } = [];

    public IEnumerable<ConcreteSyntaxNode> Grandchild(int childIndex = 0)
    {
        return childIndex >= Children.Count ? [] : Children[childIndex].Children;
    }

    public abstract override string ToString();

    protected abstract bool ShouldBePruned();
    protected abstract bool ShouldBeUnpacked();
    
    public void AddChild(ConcreteSyntaxNode newNode)
    {
        if(newNode.ShouldBePruned()) return;
        if (newNode.ShouldBeUnpacked())
        {
            foreach (var child in newNode.Children)
            {
                AddChild(child);
            }
            return;
        }
        
        Children.Add(newNode);
    }
}