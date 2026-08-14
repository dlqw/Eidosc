using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Mir.Optimize;

public sealed partial class SequencePipelineFusionPass
{
    /// <summary>
    /// Shared cleanup route for fused non-Copy sequence locals.  A route is
    /// expressed as an ordered set of owned locals; duplicate roots are
    /// dropped once, which keeps short-circuit, reject, panic-recovery and
    /// normal loop-exit blocks on the same exactly-once path.
    /// </summary>
    private static void AppendOwnedCleanup(
        MirBasicBlock block,
        SourceSpan span,
        params MirPlace[] ownedLocals)
    {
        var seen = new HashSet<LocalId>();
        foreach (var local in ownedLocals)
        {
            if (local.Kind != PlaceKind.Local || !seen.Add(local.Local))
            {
                continue;
            }

            block.Instructions.Add(new MirDrop
            {
                Value = local,
                Span = span
            });
        }
    }

    private static void AppendOwnedCleanupAndGoto(
        MirBasicBlock block,
        BlockId target,
        SourceSpan span,
        params MirPlace[] ownedLocals)
    {
        AppendOwnedCleanup(block, span, ownedLocals);
        block.Terminator = new MirGoto { Target = target, Span = span };
    }
}
