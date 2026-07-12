using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using System.Text;
using Eidosc.Mir;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Borrow;

/// <summary>
/// Formatting utilities extracted from PhaseOutput (A3).
/// </summary>
public static class BorrowFormatter
{
    public static string FormatLiveness(LivenessAnalyzer analyzer)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.LivenessHeader);
        sb.AppendLine();

        // LiveIn/LiveOut
        sb.AppendLine(PipelineMessages.BasicBlockLiveVariablesHeader);
        sb.AppendLine(Separator('-'));
        sb.AppendLine($"{PipelineMessages.BlockIdColumn,-10} | {"LiveIn",-30} | {"LiveOut",-30}");
        sb.AppendLine(Separator('-'));

        foreach (var (blockId, liveIn) in analyzer.LiveIn)
        {
            var liveOut = analyzer.LiveOut.TryGetValue(blockId, out var lo) ? lo : new HashSet<LocalId>();
            var liveInStr = string.Join(", ", liveIn.Select(l => $"%{l.Value}"));
            var liveOutStr = string.Join(", ", liveOut.Select(l => $"%{l.Value}"));
            sb.AppendLine($"{blockId,-10} | {liveInStr,-30} | {liveOutStr,-30}");
        }

        sb.AppendLine();

        // 活跃范围
        sb.AppendLine(PipelineMessages.VariableLiveRangesHeader);
        sb.AppendLine(Separator('-'));
        sb.AppendLine($"{PipelineMessages.VariableColumn,-10} | {PipelineMessages.DefinitionPointColumn,-15} | {PipelineMessages.LiveBlockCountColumn,-10}");
        sb.AppendLine(Separator('-'));

        foreach (var (localId, range) in analyzer.LiveRanges)
        {
            var defStr = $"bb{range.Definition.Block.Value}:{range.Definition.Index}";
            sb.AppendLine($"%{localId.Value,-9} | {defStr,-15} | {range.LiveBlocks.Count}");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化变量状态
    /// </summary>


    public static string FormatVariableStates(AffineTypeChecker checker)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.VariableStateTrackingHeader);
        sb.AppendLine();

        // 输出每个程序点的状态
        sb.AppendLine(PipelineMessages.VariableStateSimplifiedNote);
        sb.AppendLine();

        if (checker.Diagnostics.Count == 0)
        {
            sb.AppendLine(PipelineMessages.NoAffineTypeErrors);
        }
        else
        {
            sb.AppendLine(PipelineMessages.AffineIssueCount(checker.Diagnostics.Count));
            foreach (var diag in checker.Diagnostics)
            {
                var location = ResolveAffineLocation(diag);
                sb.AppendLine($"//   %{diag.Variable.Value}: {diag.Kind} at bb{location.Block.Value}:{location.Index}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化仿射类型错误
    /// </summary>


    public static string FormatAffineErrors(AffineTypeChecker checker)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.AffineTypeErrorsHeader);
        sb.AppendLine();

        foreach (var diag in checker.Diagnostics)
        {
            sb.AppendLine(PipelineMessages.ErrorLine(diag.Message));
            sb.AppendLine(PipelineMessages.IndentedVariableLine(diag.Variable.Value));
            sb.AppendLine(PipelineMessages.IndentedTypeLine(diag.Kind));
            var location = ResolveAffineLocation(diag);
            sb.AppendLine(PipelineMessages.IndentedLocationLine(location.Block.Value, location.Index));

            if (diag.FirstLocation.Block.IsValid)
            {
                sb.AppendLine(PipelineMessages.IndentedRelatedLocationLine(
                    diag.FirstLocation.Block.Value,
                    diag.FirstLocation.Index));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化活跃借用
    /// </summary>


    public static string FormatActiveBorrows(BorrowChecker checker)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.ActiveBorrowsHeader);
        sb.AppendLine();

        if (checker.ActiveBorrows.Count == 0)
        {
            sb.AppendLine(PipelineMessages.NoActiveBorrows);
            return sb.ToString();
        }

        sb.AppendLine($"{PipelineMessages.BorrowerColumn,-10} | {PipelineMessages.BorroweeColumn,-10} | {PipelineMessages.MutableColumn,-6} | {PipelineMessages.CreatedColumn,-10} | {PipelineMessages.EndedColumn,-10} | {PipelineMessages.SourceColumn,-22}");
        sb.AppendLine(Separator('-'));

        foreach (var borrow in checker.ActiveBorrows)
        {
            var mutStr = borrow.IsMutable ? PipelineMessages.Yes : PipelineMessages.No;
            var locStr = $"bb{borrow.Location.Block.Value}:{borrow.Location.Index}";
            var endStr = borrow.EndLocation is { } endLocation
                ? $"bb{endLocation.Block.Value}:{endLocation.Index}"
                : WellKnownStrings.Operators.Subtract;
            var originStr = string.IsNullOrEmpty(borrow.OriginSummary)
                ? $"bb{borrow.OriginLocation.Block.Value}:{borrow.OriginLocation.Index}"
                : borrow.OriginSummary;

            sb.AppendLine($"%{borrow.Borrower.Value,-9} | %{borrow.Borrowee.Value,-9} | {mutStr,-6} | {locStr,-10} | {endStr,-10} | {TrimForColumn(originStr, 22),-22}");

            if (borrow.AliasTrace.Count > 0)
            {
                var traceIdSuffix = string.IsNullOrEmpty(borrow.TraceId)
                    ? ""
                    : $" (id={borrow.TraceId})";
                sb.AppendLine($"//   trace: {string.Join(" => ", borrow.AliasTrace)}{traceIdSuffix}");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化每个程序点的 alias 状态
    /// </summary>


    public static string FormatBorrowAliasStates(BorrowChecker checker)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.BorrowAliasStatesHeader);
        sb.AppendLine();
        AppendCapabilitySummary(sb, checker.CapabilitySnapshot);

        var anyState = false;
        foreach (var (point, borrows) in checker.EnumerateBorrowStates())
        {
            anyState = true;
            sb.AppendLine($"bb{point.Block.Value}:{point.Index}");

            if (borrows.Count == 0)
            {
                sb.AppendLine(PipelineMessages.NoActiveAlias);
                sb.AppendLine();
                continue;
            }

            foreach (var borrow in borrows.OrderBy(b => b.Borrower.Value).ThenBy(b => b.Borrowee.Value))
            {
                var origin = string.IsNullOrEmpty(borrow.OriginSummary)
                    ? $"bb{borrow.OriginLocation.Block.Value}:{borrow.OriginLocation.Index}"
                    : borrow.OriginSummary;
                sb.AppendLine($"  %{borrow.Borrower.Value} -> %{borrow.Borrowee.Value} {(borrow.IsMutable ? "&mut" : WellKnownStrings.Operators.AddressOf)} origin={origin}");

                if (borrow.AliasTrace.Count > 0)
                {
                    var traceIdSuffix = string.IsNullOrEmpty(borrow.TraceId)
                        ? ""
                        : $" (id={borrow.TraceId})";
                    sb.AppendLine($"    trace: {string.Join(" => ", borrow.AliasTrace)}{traceIdSuffix}");
                }
            }

            sb.AppendLine();
        }

        if (!anyState)
        {
            sb.AppendLine(PipelineMessages.NoAliasStates);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化借用错误
    /// </summary>


    public static string FormatBorrowErrors(BorrowChecker checker)
    {
        return FormatBorrowDiagnostics(checker.Diagnostics, PipelineMessages.BorrowCheckErrorsTitle);
    }


    public static string FormatBorrowDiagnostics(
        IReadOnlyList<BorrowDiagnostic> diagnostics,
        string title)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"// {title}");
        sb.AppendLine();

        if (diagnostics.Count == 0)
        {
            sb.AppendLine(PipelineMessages.NoBorrowErrors);
            return sb.ToString();
        }

        foreach (var diag in diagnostics)
        {
            sb.AppendLine(PipelineMessages.ErrorLine(diag.Message));
            sb.AppendLine(PipelineMessages.IndentedTypeLine(diag.Kind));
            sb.AppendLine(PipelineMessages.IndentedLocationLine(diag.Location.Block.Value, diag.Location.Index));

            if (diag.RelatedLocation.Block.IsValid)
            {
                sb.AppendLine(PipelineMessages.IndentedRelatedLocationLine(
                    diag.RelatedLocation.Block.Value,
                    diag.RelatedLocation.Index));
            }

            if (diag.RelatedAliasTrace.Count > 0)
            {
                if (!string.IsNullOrEmpty(diag.RelatedAliasTraceId))
                {
                    sb.AppendLine($"//   alias trace id: {diag.RelatedAliasTraceId}");
                    sb.AppendLine($"//   lookup: search \"id={diag.RelatedAliasTraceId}\" in borrow_aliases/loan_constraint_states debug output");
                }
                sb.AppendLine($"//   alias trace: {string.Join(" => ", diag.RelatedAliasTrace)}");
            }

            if (!string.IsNullOrEmpty(diag.Hint))
            {
                sb.AppendLine(PipelineMessages.IndentedHintLine(diag.Hint));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化借用签名
    /// </summary>


    public static string FormatLoanSignature(LoanSignature signature)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.LoanSignatureHeader);
        sb.AppendLine($"// {signature}");
        sb.AppendLine();

        if (signature.ParamRequirements.Count > 0)
        {
            sb.AppendLine(PipelineMessages.ParameterRequirementsHeader);
            foreach (var param in signature.ParamRequirements)
            {
                sb.AppendLine($"//   [{param.ParamIndex}] {param}");
            }
            sb.AppendLine();
        }

        if (signature.ReturnsBorrow())
        {
            sb.AppendLine(PipelineMessages.ReturnConstraint(signature.ReturnConstraint));
        }
        else
        {
            sb.AppendLine(PipelineMessages.ReturnConstraintOwn);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化调用借用约束状态
    /// </summary>


    public static string FormatLoanConstraintStates(LoanConstraintVerifier verifier)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.LoanConstraintStatesHeader);
        sb.AppendLine();
        AppendCapabilitySummary(sb, verifier.CapabilitySnapshot);

        var anyState = false;
        foreach (var (point, borrows, movedVars) in verifier.EnumerateStates())
        {
            anyState = true;
            sb.AppendLine($"bb{point.Block.Value}:{point.Index}");

            if (borrows.Count == 0)
            {
                sb.AppendLine("  borrows: (none)");
            }
            else
            {
                foreach (var borrow in borrows.OrderBy(b => b.Borrower.Value).ThenBy(b => b.Borrowee.Value))
                {
                    var origin = string.IsNullOrEmpty(borrow.OriginSummary)
                        ? $"bb{borrow.OriginLocation.Block.Value}:{borrow.OriginLocation.Index}"
                        : borrow.OriginSummary;
                    sb.AppendLine($"  %{borrow.Borrower.Value} -> %{borrow.Borrowee.Value} {(borrow.IsMutable ? "&mut" : WellKnownStrings.Operators.AddressOf)} origin={origin}");

                    if (borrow.AliasTrace.Count > 0)
                    {
                        var traceIdSuffix = string.IsNullOrEmpty(borrow.TraceId)
                            ? ""
                            : $" (id={borrow.TraceId})";
                        sb.AppendLine($"    trace: {string.Join(" => ", borrow.AliasTrace)}{traceIdSuffix}");
                    }
                }
            }

            if (movedVars.Count == 0)
            {
                sb.AppendLine("  moved: (none)");
            }
            else
            {
                sb.AppendLine($"  moved: {string.Join(", ", movedVars.OrderBy(local => local.Value).Select(local => $"%{local.Value}"))}");
            }

            sb.AppendLine();
        }

        if (!anyState)
        {
            sb.AppendLine(PipelineMessages.NoLoanConstraintStates);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 格式化调用借用约束错误
    /// </summary>


    public static string FormatLoanConstraintErrors(LoanConstraintVerifier verifier)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.LoanConstraintErrorsHeader);
        sb.AppendLine();

        if (verifier.Diagnostics.Count == 0)
        {
            sb.AppendLine(PipelineMessages.NoLoanConstraintErrors);
            return sb.ToString();
        }

        foreach (var diag in verifier.Diagnostics)
        {
            sb.AppendLine(PipelineMessages.ErrorLine(diag.Message));
            sb.AppendLine(PipelineMessages.IndentedTypeLine(diag.Kind));
            sb.AppendLine(PipelineMessages.IndentedLocationLine(diag.Location.Block.Value, diag.Location.Index));

            if (diag.RelatedLocation.Block.IsValid)
            {
                sb.AppendLine(PipelineMessages.IndentedRelatedLocationLine(
                    diag.RelatedLocation.Block.Value,
                    diag.RelatedLocation.Index));
            }

             if (diag.RelatedAliasTrace.Count > 0)
            {
                if (!string.IsNullOrEmpty(diag.RelatedAliasTraceId))
                {
                    sb.AppendLine($"//   alias trace id: {diag.RelatedAliasTraceId}");
                    sb.AppendLine($"//   lookup: search \"id={diag.RelatedAliasTraceId}\" in borrow_aliases/loan_constraint_states debug output");
                }
                sb.AppendLine($"//   alias trace: {string.Join(" => ", diag.RelatedAliasTrace)}");
            }

            if (!string.IsNullOrEmpty(diag.Hint))
            {
                sb.AppendLine(PipelineMessages.IndentedHintLine(diag.Hint));
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }


    private static void AppendCapabilitySummary(StringBuilder sb, BorrowCapabilitySnapshot? snapshot)
    {
        if (snapshot == null)
        {
            sb.AppendLine("// capability gate: disabled (no snapshot)");
            sb.AppendLine("// capability note: no resolved @borrow(read/write/move) tags found in current ability set");
            sb.AppendLine();
            return;
        }

        var global = snapshot.GlobalCapabilities
            .Select(cap => cap.ToString().ToLowerInvariant())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        var globalText = global.Count == 0 ? "(none)" : string.Join(", ", global);
        var localGrants = snapshot.EnumerateLocalCapabilityGrants().ToList();
        var targetGrants = snapshot.EnumerateTargetCapabilityGrants().ToList();
        var providers = snapshot.EnumerateCapabilityProviders().ToList();

        sb.AppendLine($"// capability gate: {(snapshot.IsEnforced ? "enabled" : "disabled (compat mode)")}");
        sb.AppendLine($"// capability globals: {globalText}");
        sb.AppendLine("// capability resolution order: target -> local -> global (explicit target/local grants block fallback)");

        if (localGrants.Count == 0)
        {
            sb.AppendLine("// capability locals: (none)");
        }
        else
        {
            sb.AppendLine("// capability locals:");
            foreach (var (local, capabilities) in localGrants)
            {
                sb.AppendLine($"//   %{local.Value}: {FormatCapabilityKinds(capabilities)} [source=local]");
            }
        }

        if (targetGrants.Count == 0)
        {
            sb.AppendLine("// capability targets: (none)");
        }
        else
        {
            sb.AppendLine("// capability targets:");
            foreach (var (targetKey, capabilities) in targetGrants)
            {
                sb.AppendLine($"//   {targetKey}: {FormatCapabilityKinds(capabilities)} [source=target]");
            }
        }

        if (providers.Count == 0)
        {
            sb.AppendLine("// capability providers: (none)");
        }
        else
        {
            sb.AppendLine("// capability providers (@borrow tags):");
            foreach (var (provider, capabilities) in providers)
            {
                sb.AppendLine($"//   {provider}: {FormatCapabilityKinds(capabilities)}");
            }
        }

        sb.AppendLine();
    }


    private static string FormatCapabilityKinds(IEnumerable<BorrowCapabilityKind> capabilities)
    {
        var parts = capabilities
            .Select(capability => capability.ToString().ToLowerInvariant())
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();
        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }

    /// <summary>
    /// 格式化 Perceus 优化提示
    /// </summary>


    public static string FormatPerceusHints(PerceusAnalyzer analyzer)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.PerceusHintsHeader);
        sb.AppendLine();

        // 可省略的 dup
        sb.AppendLine(PipelineMessages.OmittableDupHeader);
        if (analyzer.Hints.OmitDup.Count == 0)
        {
            sb.AppendLine(PipelineMessages.NoneItem);
        }
        else
        {
            foreach (var (block, index) in analyzer.Hints.OmitDup)
            {
                sb.AppendLine($"//   bb{block.Value}:{index}");
            }
        }

        sb.AppendLine();

        // 可省略的 drop
        sb.AppendLine(PipelineMessages.OmittableDropHeader);
        if (analyzer.Hints.OmitDrop.Count == 0)
        {
            sb.AppendLine(PipelineMessages.NoneItem);
        }
        else
        {
            foreach (var (block, index) in analyzer.Hints.OmitDrop)
            {
                sb.AppendLine($"//   bb{block.Value}:{index}");
            }
        }

        sb.AppendLine();

        return sb.ToString();
    }


    private static string Separator(char c) => new string(c, 80);

    private static (BlockId Block, int Index) ResolveAffineLocation(AffineDiagnostic diagnostic)
    {
        if (diagnostic.SecondLocation.Block.IsValid)
        {
            return diagnostic.SecondLocation;
        }

        if (diagnostic.FirstLocation.Block.IsValid)
        {
            return diagnostic.FirstLocation;
        }

        return (BlockId.None, 0);
    }


    private static string TrimForColumn(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..(maxLength - 2)] + WellKnownStrings.Punctuation.DotDot;
    }

}
