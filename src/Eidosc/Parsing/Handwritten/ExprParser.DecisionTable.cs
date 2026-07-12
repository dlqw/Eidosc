using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Utils;

namespace Eidosc.Parsing.Handwritten;

public sealed partial class ExprParser
{
    private EidosAstNode ParseDecideExpr()
    {
        var startToken = ctx.Current;
        ctx.Expect(WellKnownStrings.Keywords.Decide);

        var fallback = ParseExpr();
        ctx.Expect("{");

        var rows = new List<DecisionRow>();
        while (!ctx.Check("}") && !ctx.IsEof)
        {
            var pos = ctx.Position;
            var template = ParseExprNoLambda();
            ctx.Expect(":");
            ParseDecisionRows(template, rows);
            ctx.Match(",");

            if (ctx.Position == pos)
            {
                ctx.Advance();
            }
        }

        ctx.Expect("}");
        if (rows.Count == 0)
        {
            ctx.Error("decision table requires at least one row", startToken.Location);
            return fallback;
        }

        var result = fallback;
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            var row = rows[i];
            var ifExpr = new IfExpr();
            ifExpr.SetSpan(ctx.SpanFrom(startToken));
            ifExpr.SetCondition(row.Condition);
            ifExpr.SetThenBranch(row.Result);
            ifExpr.SetElseBranch(result);
            result = ifExpr;
        }

        return result;
    }

    private void ParseDecisionRows(EidosAstNode template, List<DecisionRow> rows)
    {
        while (!ctx.Check("}") && !ctx.IsEof)
        {
            if (IsNextDecisionGroupStart())
            {
                return;
            }

            var keys = ParseDecisionKeyList();
            EidosAstNode? guard = null;
            if (ctx.Match("when"))
            {
                guard = ParseExprNoLambda();
            }

            ctx.Expect("=>");
            var result = ParseExpr();

            if (keys.Count == 0)
            {
                ctx.Error("decision table row requires at least one key expression");
            }
            else
            {
                var condition = BuildDecisionCondition(template, keys);
                if (guard != null)
                {
                    condition = CreateBinary(condition, BinaryOp.And, guard, condition.Span);
                }

                rows.Add(new DecisionRow(condition, result));
            }

            if (!ctx.Match(","))
            {
                return;
            }

            if (ctx.Check("}"))
            {
                return;
            }
        }
    }

    private List<EidosAstNode> ParseDecisionKeyList()
    {
        var keys = new List<EidosAstNode> { ParseExprNoLambda() };
        while (ctx.Match("|"))
        {
            keys.Add(ParseExprNoLambda());
        }

        return keys;
    }

    private EidosAstNode BuildDecisionCondition(EidosAstNode template, IReadOnlyList<EidosAstNode> keys)
    {
        EidosAstNode? condition = null;
        var holeCount = CountDecisionHoles(template);
        if (holeCount == 0)
        {
            ctx.Error("decision table predicate template must contain '_' hole");
        }

        foreach (var key in keys)
        {
            var replacements = GetDecisionKeyReplacements(key, holeCount);
            var expanded = SubstituteDecisionHoles(template, replacements);

            condition = condition == null
                ? expanded
                : CreateBinary(condition, BinaryOp.Or, expanded, condition.Span);
        }

        return condition ?? CreateRecoveredLiteral(ctx.Current);
    }

    private IReadOnlyList<EidosAstNode> GetDecisionKeyReplacements(EidosAstNode key, int holeCount)
    {
        if (holeCount <= 1)
        {
            return [key];
        }

        if (key is TupleExpr tuple)
        {
            if (tuple.Elements.Count != holeCount)
            {
                ctx.Error($"decision row key arity mismatch: template has {holeCount} holes but tuple key has {tuple.Elements.Count} values");
            }

            return tuple.Elements;
        }

        ctx.Error($"decision row key arity mismatch: template has {holeCount} holes but row key has 1 value");
        return [key];
    }

    private static EidosAstNode SubstituteDecisionHoles(EidosAstNode template, IReadOnlyList<EidosAstNode> replacements)
    {
        var replacementIndex = 0;
        var result = CloneExpression(template, identifier =>
        {
            if (identifier.Name != "_")
            {
                return null;
            }

            var replacement = replacementIndex < replacements.Count
                ? replacements[replacementIndex]
                : replacements.Count > 0
                    ? replacements[^1]
                    : identifier;
            replacementIndex++;
            return CloneExpression(replacement);
        });
        return result;
    }

    private static int CountDecisionHoles(EidosAstNode expr)
    {
        return expr switch
        {
            IdentifierExpr { Name: "_" } => 1,
            CtorExpr ctor => ctor.PositionalArgs.Sum(CountDecisionHoles)
                + ctor.NamedArgs.Sum(CountDecisionHoles)
                + (ctor.UpdateBase == null ? 0 : CountDecisionHoles(ctor.UpdateBase)),
            CallExpr call => (call.Function == null ? 0 : CountDecisionHoles(call.Function))
                + call.PositionalArgs.Sum(CountDecisionHoles)
                + call.NamedArgs.Sum(CountDecisionHoles),
            MethodCallExpr methodCall => (methodCall.Receiver == null ? 0 : CountDecisionHoles(methodCall.Receiver))
                + methodCall.PositionalArgs.Sum(CountDecisionHoles)
                + methodCall.NamedArgs.Sum(CountDecisionHoles),
            TupleExpr tuple => tuple.Elements.Sum(CountDecisionHoles),
            ListExpr list => list.Elements.Sum(CountDecisionHoles),
            UnaryExpr unary => unary.Operand == null ? 0 : CountDecisionHoles(unary.Operand),
            BinaryExpr binary => (binary.Left == null ? 0 : CountDecisionHoles(binary.Left))
                + (binary.Right == null ? 0 : CountDecisionHoles(binary.Right)),
            IndexExpr index => (index.Object == null ? 0 : CountDecisionHoles(index.Object))
                + (index.Index == null ? 0 : CountDecisionHoles(index.Index)),
            RecordUpdateExpr update => (update.Base == null ? 0 : CountDecisionHoles(update.Base))
                + update.NamedArgs.Sum(CountDecisionHoles),
            BlockExpr block => block.Statements.Sum(CountDecisionHoles),
            _ => 0
        };
    }

    private static int CountDecisionHoles(FieldInit field)
    {
        return field.Value == null ? 0 : CountDecisionHoles(field.Value);
    }

    private static int CountDecisionHoles(NamedArg arg)
    {
        return arg.Value == null ? 0 : CountDecisionHoles(arg.Value);
    }

    private bool IsNextDecisionGroupStart()
    {
        var depth = 0;
        for (var offset = 0; offset < 128; offset++)
        {
            var token = ctx.Peek(offset);
            if (token is EofToken)
            {
                return false;
            }

            var text = ctx.GetText(token);
            if (depth == 0)
            {
                if (text == ":")
                {
                    return true;
                }

                if (text is "=>" or "," or "}" or ";")
                {
                    return false;
                }
            }

            depth += text switch
            {
                "(" or "[" or "{" => 1,
                ")" or "]" or "}" => -1,
                _ => 0
            };
            depth = Math.Max(0, depth);
        }

        return false;
    }

    private static EidosAstNode CloneExpression(
        EidosAstNode expr,
        Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier = null)
    {
        return expr switch
        {
            LiteralExpr literal => CloneLiteral(literal),
            IdentifierExpr identifier => replaceIdentifier?.Invoke(identifier) ?? CloneIdentifier(identifier),
            PathExpr path => ClonePathExpr(path),
            CtorExpr ctor => CloneCtorExpr(ctor, replaceIdentifier),
            CallExpr call => CloneCallExpr(call, replaceIdentifier),
            MethodCallExpr methodCall => CloneMethodCallExpr(methodCall, replaceIdentifier),
            TupleExpr tuple => CloneTupleExpr(tuple, replaceIdentifier),
            ListExpr list => CloneListExpr(list, replaceIdentifier),
            UnaryExpr unary => CloneUnaryExpr(unary, replaceIdentifier),
            BinaryExpr binary => CloneBinaryExpr(binary, replaceIdentifier),
            IndexExpr index => CloneIndexExpr(index, replaceIdentifier),
            RecordUpdateExpr update => CloneRecordUpdateExpr(update, replaceIdentifier),
            BlockExpr block => CloneBlockExpr(block, replaceIdentifier),
            _ => expr
        };
    }

    private static LiteralExpr CloneLiteral(LiteralExpr literal)
    {
        var clone = new LiteralExpr();
        clone.SetSpan(literal.Span);
        clone.SetLiteral(literal.RawText);
        return clone;
    }

    private static IdentifierExpr CloneIdentifier(IdentifierExpr identifier)
    {
        var clone = new IdentifierExpr();
        clone.SetSpan(identifier.Span);
        clone.SetName(identifier.Name);
        return clone;
    }

    private static PathExpr ClonePathExpr(PathExpr path)
    {
        var clone = new PathExpr();
        clone.SetSpan(path.Span);
        clone.SetPackageAlias(path.PackageAlias);
        clone.SetModulePath([.. path.ModulePath]);
        clone.SetName(path.Name);
        clone.SetIsTypePath(path.IsTypePath);
        clone.SetTypeArgs([.. path.TypeArgs]);
        return clone;
    }

    private static CtorExpr CloneCtorExpr(CtorExpr ctor, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        var clone = new CtorExpr();
        clone.SetSpan(ctor.Span);
        if (ctor.ConstructorPath != null)
        {
            clone.SetConstructorPath(CloneTypePathShallow(ctor.ConstructorPath));
        }
        else if (!string.IsNullOrWhiteSpace(ctor.ConstructorName))
        {
            clone.SetConstructorName(ctor.ConstructorName);
        }

        foreach (var arg in ctor.PositionalArgs)
        {
            clone.AddPositionalArg(CloneExpression(arg, replaceIdentifier));
        }

        foreach (var field in ctor.NamedArgs)
        {
            clone.AddNamedArg(CloneFieldInit(field, replaceIdentifier));
        }

        if (ctor.UpdateBase != null)
        {
            clone.SetUpdateBase(CloneExpression(ctor.UpdateBase, replaceIdentifier));
        }

        return clone;
    }

    private static CallExpr CloneCallExpr(CallExpr call, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        var clone = new CallExpr();
        clone.SetSpan(call.Span);
        if (call.Function != null)
        {
            clone.SetFunction(CloneExpression(call.Function, replaceIdentifier));
        }

        foreach (var arg in call.PositionalArgs)
        {
            clone.AddPositionalArg(CloneExpression(arg, replaceIdentifier));
        }

        foreach (var arg in call.NamedArgs)
        {
            clone.AddNamedArg(CloneNamedArg(arg, replaceIdentifier));
        }

        return clone;
    }

    private static MethodCallExpr CloneMethodCallExpr(MethodCallExpr methodCall, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        var clone = new MethodCallExpr();
        clone.SetSpan(methodCall.Span);
        clone.SetMethodName(methodCall.MethodName);
        if (methodCall.Receiver != null)
        {
            clone.SetReceiver(CloneExpression(methodCall.Receiver, replaceIdentifier));
        }

        if (methodCall.HasExplicitCallSyntax)
        {
            clone.MarkExplicitCallSyntax();
        }

        foreach (var arg in methodCall.PositionalArgs)
        {
            clone.AddPositionalArg(CloneExpression(arg, replaceIdentifier));
        }

        foreach (var arg in methodCall.NamedArgs)
        {
            clone.AddNamedArg(CloneNamedArg(arg, replaceIdentifier));
        }

        return clone;
    }

    private static TupleExpr CloneTupleExpr(TupleExpr tuple, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        return new TupleExpr
        {
            Span = tuple.Span,
            Elements = tuple.Elements.Select(element => CloneExpression(element, replaceIdentifier)).ToList()
        };
    }

    private static ListExpr CloneListExpr(ListExpr list, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        var clone = new ListExpr();
        clone.SetSpan(list.Span);
        foreach (var element in list.Elements)
        {
            clone.AddElement(CloneExpression(element, replaceIdentifier));
        }

        clone.SetHasRest(list.HasRest);
        return clone;
    }

    private static UnaryExpr CloneUnaryExpr(UnaryExpr unary, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        var clone = new UnaryExpr();
        clone.SetSpan(unary.Span);
        clone.SetOperator(unary.Operator);
        if (unary.Operand != null)
        {
            clone.SetOperand(CloneExpression(unary.Operand, replaceIdentifier));
        }

        return clone;
    }

    private static BinaryExpr CloneBinaryExpr(BinaryExpr binary, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        var clone = new BinaryExpr();
        clone.SetSpan(binary.Span);
        clone.SetOperator(binary.Operator);
        if (binary.Left != null)
        {
            clone.SetLeft(CloneExpression(binary.Left, replaceIdentifier));
        }

        if (binary.Right != null)
        {
            clone.SetRight(CloneExpression(binary.Right, replaceIdentifier));
        }

        return clone;
    }

    private static IndexExpr CloneIndexExpr(IndexExpr index, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        var clone = new IndexExpr();
        clone.SetSpan(index.Span);
        clone.SetTypeArgs([.. index.TypeArgs]);
        if (index.Object != null)
        {
            clone.SetObject(CloneExpression(index.Object, replaceIdentifier));
        }

        if (index.Index != null)
        {
            clone.SetIndex(CloneExpression(index.Index, replaceIdentifier));
        }

        return clone;
    }

    private static RecordUpdateExpr CloneRecordUpdateExpr(RecordUpdateExpr update, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        var clone = new RecordUpdateExpr();
        clone.SetSpan(update.Span);
        if (update.Base != null)
        {
            clone.SetBase(CloneExpression(update.Base, replaceIdentifier));
        }

        foreach (var field in update.NamedArgs)
        {
            clone.AddNamedArg(CloneFieldInit(field, replaceIdentifier));
        }

        return clone;
    }

    private static BlockExpr CloneBlockExpr(BlockExpr block, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        var clone = new BlockExpr();
        clone.SetSpan(block.Span);
        foreach (var stmt in block.Statements)
        {
            var cloned = CloneExpression(stmt, replaceIdentifier);
            clone.AddStatement(cloned);
            if (ReferenceEquals(stmt, block.ResultExpression))
            {
                clone.SetResultExpression(cloned);
            }
        }

        return clone;
    }

    private static FieldInit CloneFieldInit(FieldInit field, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        var clone = new FieldInit();
        clone.SetSpan(field.Span);
        clone.SetFieldName(field.FieldName);
        if (field.Value != null)
        {
            clone.SetValue(CloneExpression(field.Value, replaceIdentifier));
        }

        return clone;
    }

    private static NamedArg CloneNamedArg(NamedArg arg, Func<IdentifierExpr, EidosAstNode?>? replaceIdentifier)
    {
        return new NamedArg
        {
            Span = arg.Span,
            Name = arg.Name,
            Value = arg.Value == null ? null : CloneExpression(arg.Value, replaceIdentifier)
        };
    }

    private static TypePath CloneTypePathShallow(TypePath path)
    {
        var clone = new TypePath();
        clone.SetSpan(path.Span);
        clone.SetPackageAlias(path.PackageAlias);
        clone.ModulePath = [.. path.ModulePath];
        clone.SetTypeName(path.TypeName);
        clone.TypeArgs = [.. path.TypeArgs];
        return clone;
    }

    private static BinaryExpr CreateBinary(EidosAstNode left, BinaryOp op, EidosAstNode right, SourceSpan span)
    {
        var binary = new BinaryExpr();
        binary.SetSpan(new SourceSpan(span.Location, right.Span.EndPosition - span.Position));
        binary.SetLeft(left);
        binary.SetOperator(op);
        binary.SetRight(right);
        return binary;
    }

    private readonly record struct DecisionRow(EidosAstNode Condition, EidosAstNode Result);
}
