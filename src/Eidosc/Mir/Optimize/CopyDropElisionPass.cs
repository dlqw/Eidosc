namespace Eidosc.Mir.Optimize;

/// <summary>
/// Turns an immediately consumed copy into an ownership transfer.  Drop
/// insertion intentionally makes the end of the source lifetime explicit;
/// when that drop directly follows the copy, retaining and then releasing the
/// same value is unnecessary for both pointer and inline aggregate values.
/// </summary>
public sealed class CopyDropElisionPass : IMirOptimizationPass
{
    public string Name => "CopyDropElision";

    public MirModule Run(MirModule module)
    {
        foreach (var function in module.Functions)
        {
            foreach (var block in function.BasicBlocks)
            {
                ElideCopyDropPairs(block);
            }
        }

        return module;
    }

    private static void ElideCopyDropPairs(MirBasicBlock block)
    {
        for (var index = 0; index + 1 < block.Instructions.Count; index++)
        {
            if (block.Instructions[index] is not MirCopy
                {
                    Target: { Kind: PlaceKind.Local } target,
                    Source: { Kind: PlaceKind.Local } source
                } copy ||
                block.Instructions[index + 1] is not MirDrop
                {
                    Value: MirPlace { Kind: PlaceKind.Local, Local: var droppedLocal }
                } ||
                source.Local != droppedLocal ||
                target.Local == source.Local)
            {
                continue;
            }

            block.Instructions[index] = new MirMove
            {
                Target = target,
                Source = source,
                Span = copy.Span
            };
            block.Instructions.RemoveAt(index + 1);
        }
    }
}
