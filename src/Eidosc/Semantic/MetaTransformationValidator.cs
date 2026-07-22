using Eidosc.Ast.Declarations;

namespace Eidosc.Semantic;

internal static class MetaTransformationValidator
{
    public static bool TryValidate(
        ClauseStage stage,
        Declaration target,
        MetaExpansionMaterializationResult materialization,
        out string reason)
    {
        if (materialization.RemovesTarget && stage != ClauseStage.Syntax)
        {
            reason = $"stage '{stage}' cannot remove a declaration; target removal is restricted to Syntax";
            return false;
        }

        foreach (var materialized in materialization.Nodes)
        {
            if (!TryValidateNode(stage, target, materialized, out reason))
            {
                return false;
            }
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryValidateNode(
        ClauseStage stage,
        Declaration target,
        MaterializedMetaNode materialized,
        out string reason)
    {
        if (materialized.Placement == MetaDeclarationPlacement.ReplaceTarget)
        {
            if (stage is not (ClauseStage.Syntax or ClauseStage.Body))
            {
                reason = $"stage '{stage}' cannot replace a declaration; use Syntax for declaration shape edits or Body for a contract-preserving function body rewrite";
                return false;
            }

            if (stage == ClauseStage.Body &&
                (target is not FuncDef || materialized.Node is not FuncDef))
            {
                reason = "Body replacement requires a function target and function syntax";
                return false;
            }

            if (materialized.Node is not Declaration replacement ||
                !HasSameTargetCategory(target, replacement))
            {
                reason = $"target replacement category '{GetTargetCategory(materialized.Node as Declaration)}' " +
                         $"does not match authorized category '{GetTargetCategory(target)}'";
                return false;
            }

            reason = string.Empty;
            return true;
        }

        if (materialized.Placement == MetaDeclarationPlacement.Member)
        {
            if (stage == ClauseStage.Syntax)
            {
                reason = string.Empty;
                return true;
            }

            if (stage == ClauseStage.Semantic && materialized.Node is not (Field or CaseTypeDef))
            {
                reason = string.Empty;
                return true;
            }

            reason = materialized.Node switch
            {
                Field => $"stage '{stage}' cannot add a field; field and public shape edits are restricted to Syntax",
                CaseTypeDef => $"stage '{stage}' cannot add a closed case; the sealed case and exhaustiveness graph is frozen after Syntax",
                _ => $"stage '{stage}' cannot add {materialized.Node.GetType().Name} as a member"
            };
            return false;
        }

        switch (stage)
        {
            case ClauseStage.Syntax:
            case ClauseStage.Semantic:
                reason = string.Empty;
                return true;

            case ClauseStage.Body:
                if (materialized.Node is FuncDef { IsExported: false })
                {
                    reason = string.Empty;
                    return true;
                }

                reason = "Body may only insert a non-exported private helper function, preserve a function contract through replace_target, or report diagnostics";
                return false;

            case ClauseStage.Layout:
                if (materialized.Node is LetDecl { IsComptime: true } ||
                    materialized.Node is FuncDef { IsExported: false } test &&
                    test.Name.StartsWith("test_", StringComparison.Ordinal))
                {
                    reason = string.Empty;
                    return true;
                }

                reason = "Layout may only add layout comptime constants, static checks/tests, artifact metadata, or diagnostics; it cannot affect existing name, coherence, or layout decisions";
                return false;

            default:
                reason = $"unsupported transformation stage '{stage}'";
                return false;
        }
    }

    internal static bool HasSameTargetCategory(Declaration target, Declaration replacement) =>
        string.Equals(GetTargetCategory(target), GetTargetCategory(replacement), StringComparison.Ordinal);

    internal static string GetTargetCategory(Declaration? declaration) => declaration switch
    {
        AdtDef => "item.type",
        CaseTypeDef => "member.case-type",
        FuncDef or FuncDecl => "item.function",
        TraitDef => "item.trait",
        InstanceDecl => "item.instance",
        EffectDef => "item.effect",
        ModuleDecl => "item.module",
        LetDecl => "item.value",
        ImportDecl => "item.import",
        null => "non-declaration",
        _ => "item.declaration"
    };
}
