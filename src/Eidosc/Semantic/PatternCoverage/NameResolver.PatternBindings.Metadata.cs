using Eidosc.Semantic.PatternCoverage;
using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private void AssignPatternBindingSymbols(
        Pattern pattern,
        IReadOnlyDictionary<string, SymbolId> bindingSymbols)
    {
        switch (pattern)
        {
            case VarPattern varPattern when
                !string.IsNullOrWhiteSpace(varPattern.Name) &&
                bindingSymbols.TryGetValue(varPattern.Name, out var varSymbolId):
                varPattern.SymbolId = varSymbolId;
                RegisterSyntaxIdentitySymbol(varPattern, varSymbolId);
                return;

            case CtorPattern ctorPattern:
                foreach (var positional in ctorPattern.PositionalPatterns)
                {
                    AssignPatternBindingSymbols(positional, bindingSymbols);
                }

                foreach (var named in ctorPattern.NamedPatterns)
                {
                    if (named.Pattern != null)
                    {
                        AssignPatternBindingSymbols(named.Pattern, bindingSymbols);
                        continue;
                    }

                    if (named.IsShorthand &&
                        !string.IsNullOrWhiteSpace(named.FieldName) &&
                        bindingSymbols.TryGetValue(named.FieldName, out var namedSymbolId))
                    {
                        named.SymbolId = namedSymbolId;
                        RegisterSyntaxIdentitySymbol(named, namedSymbolId);
                    }
                }

                return;

            case TuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    AssignPatternBindingSymbols(element, bindingSymbols);
                }

                return;

            case ListPattern listPattern:
                foreach (var element in listPattern.Elements)
                {
                    AssignPatternBindingSymbols(element, bindingSymbols);
                }

                if (listPattern.RestPattern != null)
                {
                    AssignPatternBindingSymbols(listPattern.RestPattern, bindingSymbols);
                }

                foreach (var element in listPattern.SuffixElements)
                {
                    AssignPatternBindingSymbols(element, bindingSymbols);
                }

                return;

            case OrPattern orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    AssignPatternBindingSymbols(alternative, bindingSymbols);
                }

                return;

            case AndPattern andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    AssignPatternBindingSymbols(conjunct, bindingSymbols);
                }

                return;

            case NotPattern:
                return;

            case RangePattern rangePattern:
                if (rangePattern.Start != null)
                {
                    AssignPatternBindingSymbols(rangePattern.Start, bindingSymbols);
                }

                if (rangePattern.End != null)
                {
                    AssignPatternBindingSymbols(rangePattern.End, bindingSymbols);
                }

                return;

            case ViewPattern viewPattern:
                if (viewPattern.InnerPattern != null)
                {
                    AssignPatternBindingSymbols(viewPattern.InnerPattern, bindingSymbols);
                }

                return;

            case AsPattern asPattern:
                if (!string.IsNullOrWhiteSpace(asPattern.BindingName) &&
                    bindingSymbols.TryGetValue(asPattern.BindingName, out var asSymbolId))
                {
                    asPattern.SymbolId = asSymbolId;
                    RegisterSyntaxIdentitySymbol(asPattern, asSymbolId);
                }

                if (asPattern.InnerPattern != null)
                {
                    AssignPatternBindingSymbols(asPattern.InnerPattern, bindingSymbols);
                }

                return;

            default:
                return;
        }
    }

    private static void CollectPatternBindingModes(
        Pattern pattern,
        IDictionary<string, PatternBindingMode> bindingModes)
    {
        switch (pattern)
        {
            case VarPattern varPattern when !string.IsNullOrWhiteSpace(varPattern.Name):
                bindingModes.TryAdd(varPattern.Name, varPattern.BindingMode);
                return;

            case CtorPattern ctorPattern:
                foreach (var positional in ctorPattern.PositionalPatterns)
                {
                    CollectPatternBindingModes(positional, bindingModes);
                }

                foreach (var named in ctorPattern.NamedPatterns)
                {
                    if (named.Pattern != null)
                    {
                        CollectPatternBindingModes(named.Pattern, bindingModes);
                        continue;
                    }

                    if (named.IsShorthand && !string.IsNullOrWhiteSpace(named.FieldName))
                    {
                        bindingModes.TryAdd(named.FieldName, PatternBindingMode.ByValue);
                    }
                }

                return;

            case TuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    CollectPatternBindingModes(element, bindingModes);
                }

                return;

            case ListPattern listPattern:
                foreach (var element in listPattern.Elements)
                {
                    CollectPatternBindingModes(element, bindingModes);
                }

                if (listPattern.RestPattern != null)
                {
                    CollectPatternBindingModes(listPattern.RestPattern, bindingModes);
                }

                foreach (var element in listPattern.SuffixElements)
                {
                    CollectPatternBindingModes(element, bindingModes);
                }

                return;

            case OrPattern orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    CollectPatternBindingModes(alternative, bindingModes);
                }

                return;

            case AndPattern andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    CollectPatternBindingModes(conjunct, bindingModes);
                }

                return;

            case RangePattern rangePattern:
                if (rangePattern.Start != null)
                {
                    CollectPatternBindingModes(rangePattern.Start, bindingModes);
                }

                if (rangePattern.End != null)
                {
                    CollectPatternBindingModes(rangePattern.End, bindingModes);
                }

                return;

            case ViewPattern viewPattern when viewPattern.InnerPattern != null:
                CollectPatternBindingModes(viewPattern.InnerPattern, bindingModes);
                return;

            case AsPattern asPattern:
                if (!string.IsNullOrWhiteSpace(asPattern.BindingName))
                {
                    bindingModes.TryAdd(asPattern.BindingName, asPattern.BindingMode);
                }

                if (asPattern.InnerPattern != null)
                {
                    CollectPatternBindingModes(asPattern.InnerPattern, bindingModes);
                }

                return;

            default:
                return;
        }
    }

    private bool IsTransparentIdentityViewExpression(EidosAstNode? viewExpression)
    {
        if (viewExpression is LambdaExpr lambdaExpr &&
            lambdaExpr.Parameters.Count == 1 &&
            lambdaExpr.Parameters[0] is VarPattern { Name.Length: > 0 } parameter &&
            lambdaExpr.Body is IdentifierExpr identifierExpr)
        {
            return string.Equals(parameter.Name, identifierExpr.Name, StringComparison.Ordinal);
        }

        return false;
    }

    private static void CollectPatternBindingNames(Pattern pattern, HashSet<string> names)
    {
        switch (pattern)
        {
            case VarPattern varPattern when !string.IsNullOrWhiteSpace(varPattern.Name):
                names.Add(varPattern.Name);
                return;

            case CtorPattern ctorPattern:
                foreach (var positional in ctorPattern.PositionalPatterns)
                {
                    CollectPatternBindingNames(positional, names);
                }

                foreach (var named in ctorPattern.NamedPatterns)
                {
                    if (named.Pattern != null)
                    {
                        CollectPatternBindingNames(named.Pattern, names);
                    }
                    else if (named.IsShorthand && !string.IsNullOrWhiteSpace(named.FieldName))
                    {
                        names.Add(named.FieldName);
                    }
                }

                return;

            case TuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    CollectPatternBindingNames(element, names);
                }

                return;

            case ListPattern listPattern:
                foreach (var element in listPattern.Elements)
                {
                    CollectPatternBindingNames(element, names);
                }

                if (listPattern.RestPattern != null)
                {
                    CollectPatternBindingNames(listPattern.RestPattern, names);
                }

                foreach (var element in listPattern.SuffixElements)
                {
                    CollectPatternBindingNames(element, names);
                }

                return;

            case OrPattern orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    CollectPatternBindingNames(alternative, names);
                }

                return;

            case AndPattern andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    CollectPatternBindingNames(conjunct, names);
                }

                return;

            case NotPattern:
                return;

            case RangePattern rangePattern:
                if (rangePattern.Start != null)
                {
                    CollectPatternBindingNames(rangePattern.Start, names);
                }

                if (rangePattern.End != null)
                {
                    CollectPatternBindingNames(rangePattern.End, names);
                }

                return;

            case ViewPattern viewPattern:
                if (viewPattern.InnerPattern != null)
                {
                    CollectPatternBindingNames(viewPattern.InnerPattern, names);
                }

                return;

            case AsPattern asPattern:
                if (!string.IsNullOrWhiteSpace(asPattern.BindingName))
                {
                    names.Add(asPattern.BindingName);
                }

                if (asPattern.InnerPattern != null)
                {
                    CollectPatternBindingNames(asPattern.InnerPattern, names);
                }

                return;

            default:
                return;
        }
    }

    private static void CollectPatternPotentialBindingNames(Pattern pattern, HashSet<string> names)
    {
        switch (pattern)
        {
            case VarPattern varPattern when !string.IsNullOrWhiteSpace(varPattern.Name):
                names.Add(varPattern.Name);
                return;

            case CtorPattern ctorPattern:
                foreach (var positional in ctorPattern.PositionalPatterns)
                {
                    CollectPatternPotentialBindingNames(positional, names);
                }

                foreach (var named in ctorPattern.NamedPatterns)
                {
                    if (named.Pattern != null)
                    {
                        CollectPatternPotentialBindingNames(named.Pattern, names);
                    }
                    else if (named.IsShorthand && !string.IsNullOrWhiteSpace(named.FieldName))
                    {
                        names.Add(named.FieldName);
                    }
                }

                return;

            case TuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    CollectPatternPotentialBindingNames(element, names);
                }

                return;

            case ListPattern listPattern:
                foreach (var element in listPattern.Elements)
                {
                    CollectPatternPotentialBindingNames(element, names);
                }

                if (listPattern.RestPattern != null)
                {
                    CollectPatternPotentialBindingNames(listPattern.RestPattern, names);
                }

                foreach (var element in listPattern.SuffixElements)
                {
                    CollectPatternPotentialBindingNames(element, names);
                }

                return;

            case OrPattern orPattern:
                foreach (var alternative in orPattern.Alternatives)
                {
                    CollectPatternPotentialBindingNames(alternative, names);
                }

                return;

            case AndPattern andPattern:
                foreach (var conjunct in andPattern.Conjuncts)
                {
                    CollectPatternPotentialBindingNames(conjunct, names);
                }

                return;

            case RangePattern rangePattern:
                if (rangePattern.Start != null)
                {
                    CollectPatternPotentialBindingNames(rangePattern.Start, names);
                }

                if (rangePattern.End != null)
                {
                    CollectPatternPotentialBindingNames(rangePattern.End, names);
                }

                return;

            case ViewPattern viewPattern:
                if (viewPattern.InnerPattern != null)
                {
                    CollectPatternPotentialBindingNames(viewPattern.InnerPattern, names);
                }

                return;

            case AsPattern asPattern:
                if (!string.IsNullOrWhiteSpace(asPattern.BindingName))
                {
                    names.Add(asPattern.BindingName);
                }

                if (asPattern.InnerPattern != null)
                {
                    CollectPatternPotentialBindingNames(asPattern.InnerPattern, names);
                }

                return;

            case NotPattern notPattern:
                if (notPattern.InnerPattern != null)
                {
                    CollectPatternPotentialBindingNames(notPattern.InnerPattern, names);
                }

                return;

            default:
                return;
        }
    }
}
