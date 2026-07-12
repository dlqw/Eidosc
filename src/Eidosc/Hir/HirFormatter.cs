using Eidosc.Symbols;
using System.Text;
using Eidosc.Pipeline;
using Eidosc.Semantic;
using Eidosc.Types;

namespace Eidosc.Hir;

/// <summary>
/// Formatting utilities extracted from PhaseOutput (A3).
/// </summary>
public static class HirFormatter
{
    public static string FormatHir(HirModule module)
    {
        var sb = new StringBuilder();
        sb.AppendLine(PipelineMessages.HirHeader);
        sb.AppendLine(PipelineMessages.ModuleLine(module.Name));
        sb.AppendLine(PipelineMessages.PathLine(string.Join(WellKnownStrings.Separators.Path, module.Path)));
        sb.AppendLine(PipelineMessages.DeclarationCount(module.Declarations.Count));
        sb.AppendLine();

        foreach (var decl in module.Declarations)
        {
            FormatHirDecl(decl, 0, sb);
        }

        return sb.ToString();
    }


    private static void FormatHirDecl(HirDecl decl, int indent, StringBuilder sb)
    {
        var prefix = new string(' ', indent * 2);

        switch (decl)
        {
            case HirFunc func:
                sb.AppendLine($"{prefix}Func {func.Name}{FormatHirTypeParams(func.TypeParams)}");
                if (func.Body != null)
                {
                    FormatHirNode(func.Body, indent + 1, sb);
                }
                break;

            case HirVal val:
                sb.AppendLine($"{prefix}{(val.IsComptime ? "ComptimeVal" : "Val")} {val.Name}");
                sb.AppendLine($"{prefix}  Pattern: {val.Pattern}");
                if (val.Initializer != null)
                {
                    FormatHirNode(val.Initializer, indent + 1, sb);
                }
                break;

            case HirVarDecl varDecl:
                sb.AppendLine($"{prefix}Var {varDecl.Name}");
                sb.AppendLine($"{prefix}  Pattern: {varDecl.Pattern}");
                if (varDecl.Initializer != null)
                {
                    FormatHirNode(varDecl.Initializer, indent + 1, sb);
                }
                break;

            case HirAdt adt:
                sb.AppendLine($"{prefix}Adt {adt.Name}{FormatHirTypeParams(adt.TypeParams)}");
                foreach (var ctor in adt.Constructors)
                {
                    sb.AppendLine($"{prefix}  Ctor {ctor.Name}");
                }
                break;

            case HirEffect effect:
                sb.AppendLine($"{prefix}Effect {effect.Name}");
                break;

            case HirTrait trait:
                sb.AppendLine($"{prefix}Trait {trait.Name}{FormatHirTypeParams(trait.TypeParams)}");
                break;

            default:
                sb.AppendLine($"{prefix}{decl.GetType().Name} {decl.Name}");
                break;
        }
    }


    private static string FormatHirTypeParams(IReadOnlyList<HirTypeParam> typeParams)
    {
        if (typeParams.Count == 0)
        {
            return "";
        }

        return $"[{string.Join(", ", typeParams.Select(FormatHirTypeParam))}]";
    }


    private static string FormatHirTypeParam(HirTypeParam typeParam)
    {
        var details = new List<string>();
        if (typeParam.IsComptime)
        {
            var annotation = string.IsNullOrWhiteSpace(typeParam.ComptimeTypeAnnotation)
                ? "Type"
                : typeParam.ComptimeTypeAnnotation;
            details.Add($"comptime={annotation}");
        }

        if (!string.IsNullOrWhiteSpace(typeParam.KindAnnotation) &&
            !string.Equals(typeParam.KindAnnotation, "kind1", StringComparison.Ordinal))
        {
            details.Add($"kind={typeParam.KindAnnotation}");
        }

        if (typeParam.Constraints.Count > 0)
        {
            details.Add($"constraints={string.Join(" + ", typeParam.Constraints.Select(FormatHirTraitConstraint))}");
        }

        if (details.Count == 0)
        {
            return typeParam.Name;
        }

        return $"{typeParam.Name}<{string.Join("; ", details)}>";
    }


    private static string FormatHirTraitConstraint(HirTraitConstraint constraint)
    {
        var prefix = constraint.ModulePath.Count == 0
            ? ""
            : string.Join(WellKnownStrings.Separators.Path, constraint.ModulePath) + WellKnownStrings.Separators.Path;
        if (constraint.TypeArgs.Count == 0)
        {
            return $"{prefix}{constraint.Name}";
        }

        var args = string.Join(", ", constraint.TypeArgs.Select(FormatHirTypeArg));
        return $"{prefix}{constraint.Name}[{args}]";
    }


    private static string FormatHirTypeArg(HirTypeArg typeArg)
    {
        if (!string.IsNullOrWhiteSpace(typeArg.DisplayText))
        {
            return typeArg.DisplayText;
        }

        return typeArg.TypeId.IsValid
            ? typeArg.TypeId.ToString()
            : "_";
    }


    private static void FormatHirNode(HirNode node, int indent, StringBuilder sb)
    {
        var prefix = new string(' ', indent * 2);

        switch (node)
        {
            case HirLiteral lit:
                var kindStr = lit.LiteralKind.ToString().ToLower();
                sb.AppendLine($"{prefix}Literal({kindStr}: {lit.Value ?? "null"})");
                break;

            case HirVar var:
                sb.AppendLine($"{prefix}Var({var.Name})");
                break;

            case HirBinOp binOp:
                sb.AppendLine($"{prefix}BinOp({binOp.Operator})");
                if (binOp.Left != null) FormatHirNode(binOp.Left, indent + 1, sb);
                if (binOp.Right != null) FormatHirNode(binOp.Right, indent + 1, sb);
                break;

            case HirUnaryOp unaryOp:
                sb.AppendLine($"{prefix}UnaryOp({unaryOp.Operator})");
                if (unaryOp.Operand != null) FormatHirNode(unaryOp.Operand, indent + 1, sb);
                break;

            case HirCall call:
                sb.AppendLine($"{prefix}Call({call.Convention})");
                if (call.Function != null) FormatHirNode(call.Function, indent + 1, sb);
                foreach (var arg in call.Arguments)
                {
                    FormatHirNode(arg, indent + 1, sb);
                }
                break;

            case HirIf ifExpr:
                sb.AppendLine($"{prefix}If");
                if (ifExpr.Condition != null) FormatHirNode(ifExpr.Condition, indent + 1, sb);
                sb.AppendLine($"{prefix}  Then:");
                if (ifExpr.ThenBranch != null) FormatHirNode(ifExpr.ThenBranch, indent + 2, sb);
                if (ifExpr.ElseBranch != null)
                {
                    sb.AppendLine($"{prefix}  Else:");
                    FormatHirNode(ifExpr.ElseBranch, indent + 2, sb);
                }
                break;

            case HirLoop loop:
                sb.AppendLine($"{prefix}Loop");
                FormatHirNode(loop.Body, indent + 1, sb);
                break;

            case HirBreak breakExpr:
                sb.AppendLine($"{prefix}Break");
                if (breakExpr.Value != null)
                {
                    FormatHirNode(breakExpr.Value, indent + 1, sb);
                }
                break;

            case HirContinue:
                sb.AppendLine($"{prefix}Continue");
                break;

            case HirMatch match:
                sb.AppendLine($"{prefix}Match");
                if (match.Scrutinee != null) FormatHirNode(match.Scrutinee, indent + 1, sb);
                foreach (var branch in match.Branches)
                {
                    sb.AppendLine($"{prefix}  Branch:");
                    sb.AppendLine($"{prefix}    Pattern: {branch.Pattern}");
                    if (branch.Guard != null)
                    {
                        sb.AppendLine($"{prefix}    Guard:");
                        FormatHirNode(branch.Guard, indent + 3, sb);
                    }

                    sb.AppendLine($"{prefix}    Body:");
                    if (branch.Body != null) FormatHirNode(branch.Body, indent + 3, sb);
                }
                break;

            case HirLambda lambda:
                sb.AppendLine($"{prefix}Lambda(params={lambda.Parameters.Count})");
                if (lambda.Body != null) FormatHirNode(lambda.Body, indent + 1, sb);
                break;

            case HirBlock block:
                sb.AppendLine($"{prefix}Block(stmts={block.Statements.Count})");
                foreach (var stmt in block.Statements)
                {
                    FormatHirStatement(stmt, indent + 1, sb);
                }
                if (block.Result != null)
                {
                    sb.AppendLine($"{prefix}  Result:");
                    FormatHirNode(block.Result, indent + 2, sb);
                }
                break;

            case HirTuple tuple:
                sb.AppendLine($"{prefix}Tuple(elems={tuple.Elements.Count})");
                foreach (var elem in tuple.Elements)
                {
                    FormatHirNode(elem, indent + 1, sb);
                }
                break;

            case HirList list:
                sb.AppendLine($"{prefix}List(elems={list.Elements.Count})");
                foreach (var elem in list.Elements)
                {
                    FormatHirNode(elem, indent + 1, sb);
                }
                break;

            case HirListComprehension comprehension:
                sb.AppendLine($"{prefix}ListComprehension(qualifiers={comprehension.Qualifiers.Count})");
                sb.AppendLine($"{prefix}  Output:");
                FormatHirNode(comprehension.Output, indent + 2, sb);

                for (var i = 0; i < comprehension.Qualifiers.Count; i++)
                {
                    var qualifier = comprehension.Qualifiers[i];
                    sb.AppendLine($"{prefix}  Qualifier[{i}]: {qualifier.Kind}");

                    if (qualifier.Kind == HirQualifierKind.Generator)
                    {
                        var patternText = qualifier.GeneratorPattern?.ToString() ?? "<missing-pattern>";
                        sb.AppendLine($"{prefix}    Pattern: {patternText}");
                        if (qualifier.GeneratorSource != null)
                        {
                            sb.AppendLine($"{prefix}    Source:");
                            FormatHirNode(qualifier.GeneratorSource, indent + 3, sb);
                        }
                    }
                    else if (qualifier.GuardExpression != null)
                    {
                        sb.AppendLine($"{prefix}    Guard:");
                        FormatHirNode(qualifier.GuardExpression, indent + 3, sb);
                    }
                }
                break;

            default:
                sb.AppendLine($"{prefix}{node.GetType().Name}");
                break;
        }
    }


    private static void FormatHirStatement(HirStatement stmt, int indent, StringBuilder sb)
    {
        var prefix = new string(' ', indent * 2);

        switch (stmt)
        {
            case HirDeclStatement declStmt:
                sb.AppendLine($"{prefix}DeclStatement");
                FormatHirDecl(declStmt.Declaration, indent + 1, sb);
                break;

            case HirExprStatement exprStmt:
                sb.AppendLine($"{prefix}ExprStatement");
                if (exprStmt.Expression != null) FormatHirNode(exprStmt.Expression, indent + 1, sb);
                break;

            case HirAssignStatement assign:
                var targetName = assign.Target is HirVar varNode ? varNode.Name : "?";
                sb.AppendLine($"{prefix}Assign({targetName})");
                if (assign.Value != null) FormatHirNode(assign.Value, indent + 1, sb);
                break;

            default:
                sb.AppendLine($"{prefix}{stmt.GetType().Name}");
                break;
        }
    }

    /// <summary>
    /// 格式化 MIR 模块
    /// </summary>

}
