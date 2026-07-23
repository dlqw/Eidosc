using Eidosc.Symbols;
using Eidosc.Mir;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Semantic;

namespace Eidosc.Hir;

/// <summary>
/// Analyzes lambda expressions to determine which free variables need to be captured as closures.
/// Extracted from HirBuilder to separate capture analysis from HIR lowering.
/// </summary>
internal sealed class CaptureCollector
{
    private readonly SymbolTable _symbolTable;
    private readonly TypeIdRegistry _typeRegistry;

    public CaptureCollector(SymbolTable symbolTable, TypeIdRegistry typeRegistry)
    {
        _symbolTable = symbolTable;
        _typeRegistry = typeRegistry;
    }

    public List<HirCapture> Collect(LambdaExpr lambda, HashSet<string> freeVariables)
    {
        if (lambda.Body == null || freeVariables.Count == 0)
        {
            return [];
        }

        var boundSymbols = new HashSet<SymbolId>();
        foreach (var parameter in lambda.Parameters)
        {
            CollectPatternBindingSymbols(parameter, boundSymbols);
        }

        var captures = new List<HirCapture>();
        var capturedSymbols = new HashSet<SymbolId>();
        CollectNodeCaptures(lambda.Body, boundSymbols, freeVariables, captures, capturedSymbols);
        return captures;
    }

    private void CollectNodeCaptures(
        EidosAstNode? node,
        HashSet<SymbolId> boundSymbols,
        HashSet<string> freeVariables,
        List<HirCapture> captures,
        HashSet<SymbolId> capturedSymbols)
    {
        if (node == null)
        {
            return;
        }

        switch (node)
        {
            case IdentifierExpr identifier:
                TryAddCapture(identifier.Name, identifier.SymbolId, _typeRegistry.GetTypeId(identifier), freeVariables, boundSymbols, captures, capturedSymbols);
                return;

            case PathExpr path:
                TryAddCapture(path.Name, path.SymbolId, _typeRegistry.GetTypeId(path), freeVariables, boundSymbols, captures, capturedSymbols);
                return;

            case LambdaExpr nestedLambda:
                var nestedBoundSymbols = new HashSet<SymbolId>(boundSymbols);
                foreach (var parameter in nestedLambda.Parameters)
                {
                    CollectPatternBindingSymbols(parameter, nestedBoundSymbols);
                }

                CollectNodeCaptures(nestedLambda.Body, nestedBoundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case LetDecl letDecl:
                CollectNodeCaptures(letDecl.Value, boundSymbols, freeVariables, captures, capturedSymbols);
                if (letDecl.Pattern != null)
                {
                    AnalyzePatternCaptures(letDecl.Pattern, boundSymbols, freeVariables, captures, capturedSymbols);
                    CollectPatternBindingSymbols(letDecl.Pattern, boundSymbols);
                }
                return;

            case LetQuestionDecl letQuestionDecl:
                CollectNodeCaptures(letQuestionDecl.Value, boundSymbols, freeVariables, captures, capturedSymbols);
                if (letQuestionDecl.Pattern != null)
                {
                    AnalyzePatternCaptures(letQuestionDecl.Pattern, boundSymbols, freeVariables, captures, capturedSymbols);
                    CollectPatternBindingSymbols(letQuestionDecl.Pattern, boundSymbols);
                }
                return;

            case Assignment assignment:
                if (assignment.TargetSymbolId.IsValid &&
                    !boundSymbols.Contains(assignment.TargetSymbolId) &&
                    freeVariables.Contains(assignment.Target))
                {
                    TryAddCapture(assignment.Target, assignment.TargetSymbolId, TypeId.None, freeVariables, boundSymbols, captures, capturedSymbols);
                }

                CollectNodeCaptures(assignment.Value, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case BinaryExpr binary:
                CollectNodeCaptures(binary.Left, boundSymbols, freeVariables, captures, capturedSymbols);
                CollectNodeCaptures(binary.Right, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case UnaryExpr unary:
                CollectNodeCaptures(unary.Operand, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case CallExpr call:
                CollectNodeCaptures(call.Function, boundSymbols, freeVariables, captures, capturedSymbols);
                foreach (var argument in call.PositionalArgs)
                {
                    CollectNodeCaptures(argument, boundSymbols, freeVariables, captures, capturedSymbols);
                }

                foreach (var argument in call.NamedArgs)
                {
                    CollectNodeCaptures(argument.Value, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case MethodCallExpr methodCall:
                CollectNodeCaptures(methodCall.Receiver, boundSymbols, freeVariables, captures, capturedSymbols);
                foreach (var argument in methodCall.PositionalArgs)
                {
                    CollectNodeCaptures(argument, boundSymbols, freeVariables, captures, capturedSymbols);
                }

                foreach (var argument in methodCall.NamedArgs)
                {
                    CollectNodeCaptures(argument.Value, boundSymbols, freeVariables, captures, capturedSymbols);
                }

                return;

            case InfixCallExpr infixCall:
                CollectNodeCaptures(infixCall.Left, boundSymbols, freeVariables, captures, capturedSymbols);
                CollectNodeCaptures(infixCall.Right, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case IndexExpr index:
                CollectNodeCaptures(index.Object, boundSymbols, freeVariables, captures, capturedSymbols);
                CollectNodeCaptures(index.Index, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case IfExpr ifExpr:
                CollectNodeCaptures(ifExpr.Condition, boundSymbols, freeVariables, captures, capturedSymbols);
                CollectNodeCaptures(ifExpr.ThenBranch, boundSymbols, freeVariables, captures, capturedSymbols);
                CollectNodeCaptures(ifExpr.ElseBranch, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case IfLetExpr ifLetExpr:
                CollectNodeCaptures(ifLetExpr.MatchedExpression, boundSymbols, freeVariables, captures, capturedSymbols);
                var ifLetBoundSymbols = new HashSet<SymbolId>(boundSymbols);
                if (ifLetExpr.Pattern != null)
                {
                    AnalyzePatternCaptures(ifLetExpr.Pattern, boundSymbols, freeVariables, captures, capturedSymbols);
                    CollectPatternBindingSymbols(ifLetExpr.Pattern, ifLetBoundSymbols);
                }

                CollectNodeCaptures(ifLetExpr.ThenBranch, ifLetBoundSymbols, freeVariables, captures, capturedSymbols);
                CollectNodeCaptures(ifLetExpr.ElseBranch, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case WhileLetExpr whileLetExpr:
                CollectNodeCaptures(whileLetExpr.MatchedExpression, boundSymbols, freeVariables, captures, capturedSymbols);
                var whileLetBoundSymbols = new HashSet<SymbolId>(boundSymbols);
                if (whileLetExpr.Pattern != null)
                {
                    AnalyzePatternCaptures(whileLetExpr.Pattern, boundSymbols, freeVariables, captures, capturedSymbols);
                    CollectPatternBindingSymbols(whileLetExpr.Pattern, whileLetBoundSymbols);
                }

                CollectNodeCaptures(whileLetExpr.Body, whileLetBoundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case LoopExpr loop:
                CollectNodeCaptures(loop.Body, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case BreakExpr breakExpr:
                CollectNodeCaptures(breakExpr.Value, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case ReturnExpr returnExpr:
                CollectNodeCaptures(returnExpr.Value, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case BlockExpr block:
                var blockBoundSymbols = new HashSet<SymbolId>(boundSymbols);
                foreach (var statement in block.Statements)
                {
                    CollectNodeCaptures(statement, blockBoundSymbols, freeVariables, captures, capturedSymbols);
                }

                CollectNodeCaptures(block.ResultExpression, blockBoundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case TupleExpr tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectNodeCaptures(element, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case ListExpr list:
                foreach (var element in list.Elements)
                {
                    CollectNodeCaptures(element, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case ListComprehension comprehension:
                var comprehensionBoundSymbols = new HashSet<SymbolId>(boundSymbols);
                foreach (var qualifier in comprehension.Qualifiers)
                {
                    if (qualifier.GeneratorExpression != null)
                    {
                        CollectNodeCaptures(qualifier.GeneratorExpression, comprehensionBoundSymbols, freeVariables, captures, capturedSymbols);
                    }

                    if (qualifier.GuardExpression != null)
                    {
                        CollectNodeCaptures(qualifier.GuardExpression, comprehensionBoundSymbols, freeVariables, captures, capturedSymbols);
                    }

                    if (qualifier.GeneratorPattern != null)
                    {
                        AnalyzePatternCaptures(qualifier.GeneratorPattern, comprehensionBoundSymbols, freeVariables, captures, capturedSymbols);
                        CollectPatternBindingSymbols(qualifier.GeneratorPattern, comprehensionBoundSymbols);
                    }
                }

                CollectNodeCaptures(comprehension.Output, comprehensionBoundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case MatchExpr matchExpr:
                CollectNodeCaptures(matchExpr.MatchedExpression, boundSymbols, freeVariables, captures, capturedSymbols);
                foreach (var branch in matchExpr.Branches)
                {
                    var branchBoundSymbols = new HashSet<SymbolId>(boundSymbols);
                    if (branch.Pattern != null)
                    {
                        AnalyzePatternCaptures(branch.Pattern, boundSymbols, freeVariables, captures, capturedSymbols);
                        CollectPatternBindingSymbols(branch.Pattern, branchBoundSymbols);
                    }

                    CollectNodeCaptures(branch.Guard, branchBoundSymbols, freeVariables, captures, capturedSymbols);
                    CollectNodeCaptures(branch.Expression, branchBoundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case SelectionExpr selection:
                CollectNodeCaptures(selection.Subject, boundSymbols, freeVariables, captures, capturedSymbols);
                var thenBoundSymbols = new HashSet<SymbolId>(boundSymbols);
                foreach (var symbolId in selection.ThenPlaceholderSymbols.Values)
                {
                    thenBoundSymbols.Add(symbolId);
                }
                CollectNodeCaptures(selection.ThenArm, thenBoundSymbols, freeVariables, captures, capturedSymbols);

                var elseBoundSymbols = new HashSet<SymbolId>(boundSymbols);
                foreach (var symbolId in selection.ElsePlaceholderSymbols.Values)
                {
                    elseBoundSymbols.Add(symbolId);
                }
                CollectNodeCaptures(selection.ElseArm, elseBoundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case PatternGuardExpr patternGuardExpr:
                CollectNodeCaptures(patternGuardExpr.SourceExpression, boundSymbols, freeVariables, captures, capturedSymbols);
                if (patternGuardExpr.Pattern != null)
                {
                    AnalyzePatternCaptures(patternGuardExpr.Pattern, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case CtorExpr ctorExpr:
                foreach (var argument in ctorExpr.PositionalArgs)
                {
                    CollectNodeCaptures(argument, boundSymbols, freeVariables, captures, capturedSymbols);
                }

                if (ctorExpr.UpdateBase != null)
                {
                    CollectNodeCaptures(ctorExpr.UpdateBase, boundSymbols, freeVariables, captures, capturedSymbols);
                }

                foreach (var argument in ctorExpr.NamedArgs)
                {
                    CollectNodeCaptures(argument.Value, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case RecordUpdateExpr recordUpdate:
                if (recordUpdate.Base != null)
                {
                    CollectNodeCaptures(recordUpdate.Base, boundSymbols, freeVariables, captures, capturedSymbols);
                }

                foreach (var argument in recordUpdate.NamedArgs)
                {
                    CollectNodeCaptures(argument.Value, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case ContextualRecordLiteralExpr contextualRecord:
                foreach (var argument in contextualRecord.NamedArgs)
                {
                    CollectNodeCaptures(argument.Value, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case GivenExpr given:
                CollectNodeCaptures(given.Target, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case AssociatedConstExpr associatedConst:
                CollectNodeCaptures(associatedConst.ImplementationValue, boundSymbols, freeVariables, captures, capturedSymbols);
                return;
        }
    }

    private void AnalyzePatternCaptures(
        Pattern pattern,
        HashSet<SymbolId> boundSymbols,
        HashSet<string> freeVariables,
        List<HirCapture> captures,
        HashSet<SymbolId> capturedSymbols)
    {
        switch (pattern)
        {
            case ViewPattern viewPattern:
                CollectNodeCaptures(viewPattern.ViewExpression, boundSymbols, freeVariables, captures, capturedSymbols);
                if (viewPattern.InnerPattern != null)
                {
                    AnalyzePatternCaptures(viewPattern.InnerPattern, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case TuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    AnalyzePatternCaptures(element, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case ListPattern listPattern:
                foreach (var element in listPattern.Elements)
                {
                    AnalyzePatternCaptures(element, boundSymbols, freeVariables, captures, capturedSymbols);
                }

                if (listPattern.RestPattern != null)
                {
                    AnalyzePatternCaptures(listPattern.RestPattern, boundSymbols, freeVariables, captures, capturedSymbols);
                }

                foreach (var element in listPattern.SuffixElements)
                {
                    AnalyzePatternCaptures(element, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case CtorPattern ctorPattern:
                foreach (var positional in ctorPattern.PositionalPatterns)
                {
                    AnalyzePatternCaptures(positional, boundSymbols, freeVariables, captures, capturedSymbols);
                }

                foreach (var named in ctorPattern.NamedPatterns)
                {
                    if (named.Pattern != null)
                    {
                        AnalyzePatternCaptures(named.Pattern, boundSymbols, freeVariables, captures, capturedSymbols);
                    }
                }
                return;

            case OrPattern orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    AnalyzePatternCaptures(alternative, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case AndPattern andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    AnalyzePatternCaptures(conjunct, boundSymbols, freeVariables, captures, capturedSymbols);
                }
                return;

            case NotPattern notPattern when notPattern.InnerPattern != null:
                AnalyzePatternCaptures(notPattern.InnerPattern, boundSymbols, freeVariables, captures, capturedSymbols);
                return;

            case AsPattern asPattern when asPattern.InnerPattern != null:
                AnalyzePatternCaptures(asPattern.InnerPattern, boundSymbols, freeVariables, captures, capturedSymbols);
                return;
        }
    }

    private void TryAddCapture(
        string name,
        SymbolId symbolId,
        TypeId typeId,
        HashSet<string> freeVariables,
        HashSet<SymbolId> boundSymbols,
        List<HirCapture> captures,
        HashSet<SymbolId> capturedSymbols)
    {
        if (string.IsNullOrWhiteSpace(name) ||
            !symbolId.IsValid ||
            !freeVariables.Contains(name) ||
            boundSymbols.Contains(symbolId) ||
            capturedSymbols.Contains(symbolId))
        {
            return;
        }

        if (_symbolTable.GetSymbol(symbolId) is not VarSymbol variableSymbol ||
            variableSymbol.IsModuleLevel)
        {
            return;
        }

        captures.Add(new HirCapture
        {
            Name = variableSymbol.Name,
            SymbolId = variableSymbol.Id,
            TypeId = typeId.IsValid ? typeId : variableSymbol.Type,
            IsMutable = variableSymbol.IsMutable
        });
        capturedSymbols.Add(symbolId);
    }

    internal static void CollectPatternBindingSymbols(Pattern pattern, HashSet<SymbolId> boundSymbols)
    {
        switch (pattern)
        {
            case VarPattern varPattern when varPattern.SymbolId.IsValid:
                boundSymbols.Add(varPattern.SymbolId);
                return;

            case AsPattern asPattern:
                if (asPattern.SymbolId.IsValid)
                {
                    boundSymbols.Add(asPattern.SymbolId);
                }

                if (asPattern.InnerPattern != null)
                {
                    CollectPatternBindingSymbols(asPattern.InnerPattern, boundSymbols);
                }
                return;

            case TuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    CollectPatternBindingSymbols(element, boundSymbols);
                }
                return;

            case ListPattern listPattern:
                foreach (var element in listPattern.Elements)
                {
                    CollectPatternBindingSymbols(element, boundSymbols);
                }

                if (listPattern.RestPattern != null)
                {
                    CollectPatternBindingSymbols(listPattern.RestPattern, boundSymbols);
                }

                foreach (var element in listPattern.SuffixElements)
                {
                    CollectPatternBindingSymbols(element, boundSymbols);
                }
                return;

            case CtorPattern ctorPattern:
                foreach (var positional in ctorPattern.PositionalPatterns)
                {
                    CollectPatternBindingSymbols(positional, boundSymbols);
                }

                foreach (var named in ctorPattern.NamedPatterns)
                {
                    if (named.Pattern != null)
                    {
                        CollectPatternBindingSymbols(named.Pattern, boundSymbols);
                    }
                }
                return;

            case OrPattern orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    CollectPatternBindingSymbols(alternative, boundSymbols);
                }
                return;

            case AndPattern andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    CollectPatternBindingSymbols(conjunct, boundSymbols);
                }
                return;

            case NotPattern notPattern when notPattern.InnerPattern != null:
                CollectPatternBindingSymbols(notPattern.InnerPattern, boundSymbols);
                return;

            case ViewPattern viewPattern when viewPattern.InnerPattern != null:
                CollectPatternBindingSymbols(viewPattern.InnerPattern, boundSymbols);
                return;
        }
    }
}
