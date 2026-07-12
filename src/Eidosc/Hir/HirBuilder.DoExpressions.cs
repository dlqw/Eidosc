using Eidosc.Symbols;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Semantic;
using Eidosc.Utils;

namespace Eidosc.Hir;

public sealed partial class HirBuilder
{
    private HirNode ConvertDoExpr(DoExpr doExpr)
    {
        if (doExpr.Bindings.Count == 0)
        {
            return ReportUnsupportedExpr(doExpr);
        }

        return DesugarDoBindings(doExpr.Bindings, 0, doExpr.Span);
    }

    private HirNode DesugarDoBindings(List<DoBinding> bindings, int index, SourceSpan span)
    {
        if (index >= bindings.Count)
        {
            return new HirLiteral { LiteralKind = LiteralKind.Unit, Value = "()", Span = span };
        }

        var current = bindings[index];
        var isLast = index == bindings.Count - 1;

        if (isLast)
        {
            return current.Value != null
                ? ConvertExprOrFallback(current.Value, "do expression", current.Span)
                : new HirLiteral { LiteralKind = LiteralKind.Unit, Value = "()", Span = span };
        }

        switch (current.Kind)
        {
            case DoBindingKind.Bind:
            {
                var rest = DesugarDoBindings(bindings, index + 1, span);
                return BuildDoMonadBind(current, rest);
            }
            case DoBindingKind.Let:
            {
                var rest = DesugarDoBindings(bindings, index + 1, span);
                return BuildDoLetBind(current, rest);
            }
            default:
            {
                var rest = DesugarDoBindings(bindings, index + 1, span);
                return BuildDoExprBind(current, rest);
            }
        }
    }

    private HirNode BuildDoMonadBind(DoBinding binding, HirNode rest)
    {
        var value = binding.Value != null
            ? ConvertExprOrFallback(binding.Value, "do bind value", binding.Span)
            : new HirLiteral { LiteralKind = LiteralKind.Unit, Value = "()", Span = binding.Span };

        // For simple variable patterns, use directly as lambda param
        // For complex patterns, generate a match in the lambda body
        var paramName = GetDoBindParamName(binding);
        var paramSymbolId = binding.Pattern is Ast.Patterns.VarPattern varPattern
            ? varPattern.SymbolId
            : SymbolId.None;
        var paramTypeId = binding.Pattern != null
            ? ResolveDoPatternTypeId(binding.Pattern)
            : TypeId.None;
        var lambdaParams = new List<HirParam>
        {
            new() { Name = paramName, SymbolId = paramSymbolId, TypeId = paramTypeId }
        };

        HirNode lambdaBody;
        if (binding.Pattern is Ast.Patterns.VarPattern varPat && !string.IsNullOrEmpty(varPat.Name))
        {
            lambdaBody = rest;
        }
        else if (binding.Pattern != null)
        {
            lambdaBody = new HirMatch
            {
                Scrutinee = new HirVar { Name = paramName, Span = binding.Span, TypeId = paramTypeId },
                Branches = [new HirMatchBranch
                {
                    Pattern = ConvertPattern(binding.Pattern),
                    Body = rest
                }],
                Span = binding.Span,
                TypeId = rest.TypeId
            };
        }
        else
        {
            lambdaBody = rest;
        }

        var lambda = BuildSyntheticDoLambda(lambdaParams, lambdaBody, binding.Span);

        return new HirCall
        {
            Function = BuildStdlibFunctionVar("Std::Monad::Monad", "bind", binding.Span),
            Arguments = [value, lambda],
            Span = binding.Span,
            TypeId = rest.TypeId
        };
    }

    private HirNode BuildDoLetBind(DoBinding binding, HirNode rest)
    {
        var value = binding.Value != null
            ? ConvertExprOrFallback(binding.Value, "do let value", binding.Span)
            : new HirLiteral { LiteralKind = LiteralKind.Unit, Value = "()", Span = binding.Span };

        var varName = string.IsNullOrWhiteSpace(binding.VarName)
            ? "$do_let"
            : binding.VarName;

        return new HirBlock
        {
            Span = binding.Span,
            Result = rest,
            TypeId = rest.TypeId,
            Statements =
            [
                new HirDeclStatement
                {
                    Span = binding.Span,
                    Declaration = new HirVal
                    {
                        Name = varName,
                        Initializer = value,
                        Span = binding.Span,
                        SymbolId = binding.SymbolId,
                        IsModuleLevel = false,
                        Pattern = new HirVarPattern
                        {
                            Name = varName,
                            SymbolId = binding.SymbolId,
                            Span = binding.Span,
                            TypeId = GetTypeId(binding)
                        },
                        TypeId = GetTypeId(binding)
                    }
                }
            ]
        };
    }

    private HirNode BuildDoExprBind(DoBinding binding, HirNode rest)
    {
        var value = binding.Value != null
            ? ConvertExprOrFallback(binding.Value, "do expr value", binding.Span)
            : new HirLiteral { LiteralKind = LiteralKind.Unit, Value = "()", Span = binding.Span };

        var lambda = BuildSyntheticDoLambda([new HirParam { Name = "_" }], rest, binding.Span);

        return new HirCall
        {
            Function = BuildStdlibFunctionVar("Std::Monad::Monad", "bind", binding.Span),
            Arguments = [value, lambda],
            Span = binding.Span,
            TypeId = rest.TypeId
        };
    }

    private HirLambda BuildSyntheticDoLambda(List<HirParam> parameters, HirNode body, SourceSpan span)
    {
        var returnType = body.TypeId;
        var lambdaType = CreateSyntheticDoLambdaTypeId(parameters, returnType);
        var lambda = new HirLambda
        {
            Parameters = parameters,
            ReturnType = returnType,
            Body = body,
            Span = span,
            TypeId = lambdaType
        };

        lambda.Captures.AddRange(CollectSyntheticLambdaCaptures(lambda));
        return lambda;
    }

    private TypeId CreateSyntheticDoLambdaTypeId(IReadOnlyList<HirParam> parameters, TypeId returnType)
    {
        if (!returnType.IsValid || parameters.Count == 0)
        {
            return TypeId.None;
        }

        var parameterTypes = new TypeId[parameters.Count];
        for (var i = 0; i < parameters.Count; i++)
        {
            var parameterType = parameters[i].TypeId;
            if (!parameterType.IsValid)
            {
                return TypeId.None;
            }

            parameterTypes[i] = parameterType;
        }

        return _typeRegistry.GetFunctionTypeId(parameterTypes, returnType);
    }

    private TypeId ResolveDoPatternTypeId(Pattern pattern)
    {
        var patternType = GetTypeId(pattern);
        if (patternType.IsValid)
        {
            return patternType;
        }

        return pattern switch
        {
            Ast.Patterns.VarPattern { SymbolId.IsValid: true } varPattern
                when _symbolTable.GetSymbol(varPattern.SymbolId) is VarSymbol variableSymbol =>
                variableSymbol.Type,
            _ => TypeId.None
        };
    }

    private List<HirCapture> CollectSyntheticLambdaCaptures(HirLambda lambda)
    {
        if (lambda.Body == null)
        {
            return [];
        }

        var boundSymbols = new HashSet<SymbolId>();
        foreach (var parameter in lambda.Parameters)
        {
            if (parameter.SymbolId.IsValid)
            {
                boundSymbols.Add(parameter.SymbolId);
            }
        }

        var captures = new List<HirCapture>();
        var capturedSymbols = new HashSet<SymbolId>();
        CollectSyntheticNodeCaptures(lambda.Body, boundSymbols, captures, capturedSymbols);
        return captures;
    }

    private void CollectSyntheticNodeCaptures(
        HirNode? node,
        HashSet<SymbolId> boundSymbols,
        List<HirCapture> captures,
        HashSet<SymbolId> capturedSymbols)
    {
        if (node == null)
        {
            return;
        }

        switch (node)
        {
            case HirVar variable:
                TryAddSyntheticCapture(variable, boundSymbols, captures, capturedSymbols);
                return;

            case HirLambda nestedLambda:
                foreach (var capture in nestedLambda.Captures)
                {
                    TryAddSyntheticCapture(capture, boundSymbols, captures, capturedSymbols);
                }
                return;

            case HirBinOp binary:
                CollectSyntheticNodeCaptures(binary.Left, boundSymbols, captures, capturedSymbols);
                CollectSyntheticNodeCaptures(binary.Right, boundSymbols, captures, capturedSymbols);
                return;

            case HirUnaryOp unary:
                CollectSyntheticNodeCaptures(unary.Operand, boundSymbols, captures, capturedSymbols);
                return;

            case HirCall call:
                CollectSyntheticNodeCaptures(call.Function, boundSymbols, captures, capturedSymbols);
                foreach (var argument in call.Arguments)
                {
                    CollectSyntheticNodeCaptures(argument, boundSymbols, captures, capturedSymbols);
                }

                return;

            case HirIf ifExpr:
                CollectSyntheticNodeCaptures(ifExpr.Condition, boundSymbols, captures, capturedSymbols);
                CollectSyntheticNodeCaptures(ifExpr.ThenBranch, boundSymbols, captures, capturedSymbols);
                CollectSyntheticNodeCaptures(ifExpr.ElseBranch, boundSymbols, captures, capturedSymbols);
                return;

            case HirLoop loop:
                CollectSyntheticNodeCaptures(loop.Body, boundSymbols, captures, capturedSymbols);
                return;

            case HirBreak breakExpr:
                CollectSyntheticNodeCaptures(breakExpr.Value, boundSymbols, captures, capturedSymbols);
                return;

            case HirReturn returnExpr:
                CollectSyntheticNodeCaptures(returnExpr.Value, boundSymbols, captures, capturedSymbols);
                return;

            case HirPatternGuard patternGuard:
                CollectSyntheticNodeCaptures(patternGuard.SourceExpression, boundSymbols, captures, capturedSymbols);
                CollectSyntheticPatternCaptures(patternGuard.Pattern, boundSymbols, captures, capturedSymbols);
                return;

            case HirSequentialGuard sequentialGuard:
                foreach (var guard in sequentialGuard.Guards)
                {
                    CollectSyntheticNodeCaptures(guard, boundSymbols, captures, capturedSymbols);
                }
                return;

            case HirMatch match:
                CollectSyntheticNodeCaptures(match.Scrutinee, boundSymbols, captures, capturedSymbols);
                foreach (var branch in match.Branches)
                {
                    var branchBoundSymbols = new HashSet<SymbolId>(boundSymbols);
                    CollectSyntheticPatternCaptures(branch.Pattern, boundSymbols, captures, capturedSymbols);
                    CollectHirPatternBindingSymbols(branch.Pattern, branchBoundSymbols);
                    CollectSyntheticNodeCaptures(branch.Guard, branchBoundSymbols, captures, capturedSymbols);
                    CollectSyntheticNodeCaptures(branch.Body, branchBoundSymbols, captures, capturedSymbols);
                }
                return;

            case HirBlock block:
                var blockBoundSymbols = new HashSet<SymbolId>(boundSymbols);
                foreach (var statement in block.Statements)
                {
                    CollectSyntheticStatementCaptures(statement, blockBoundSymbols, captures, capturedSymbols);
                }

                CollectSyntheticNodeCaptures(block.Result, blockBoundSymbols, captures, capturedSymbols);
                return;

            case HirTuple tuple:
                foreach (var element in tuple.Elements)
                {
                    CollectSyntheticNodeCaptures(element, boundSymbols, captures, capturedSymbols);
                }
                return;

            case HirList list:
                foreach (var element in list.Elements)
                {
                    CollectSyntheticNodeCaptures(element, boundSymbols, captures, capturedSymbols);
                }
                return;

            case HirListComprehension comprehension:
                var comprehensionBoundSymbols = new HashSet<SymbolId>(boundSymbols);
                foreach (var qualifier in comprehension.Qualifiers)
                {
                    CollectSyntheticNodeCaptures(qualifier.GeneratorSource, comprehensionBoundSymbols, captures, capturedSymbols);
                    CollectSyntheticNodeCaptures(qualifier.GuardExpression, comprehensionBoundSymbols, captures, capturedSymbols);

                    if (qualifier.GeneratorPattern != null)
                    {
                        CollectSyntheticPatternCaptures(qualifier.GeneratorPattern, comprehensionBoundSymbols, captures, capturedSymbols);
                        CollectHirPatternBindingSymbols(qualifier.GeneratorPattern, comprehensionBoundSymbols);
                    }
                }

                CollectSyntheticNodeCaptures(comprehension.Output, comprehensionBoundSymbols, captures, capturedSymbols);
                return;

            case HirFieldAccess fieldAccess:
                CollectSyntheticNodeCaptures(fieldAccess.Target, boundSymbols, captures, capturedSymbols);
                return;

            case HirIndexAccess indexAccess:
                CollectSyntheticNodeCaptures(indexAccess.Target, boundSymbols, captures, capturedSymbols);
                CollectSyntheticNodeCaptures(indexAccess.Index, boundSymbols, captures, capturedSymbols);
                return;

        }
    }

    private void CollectSyntheticStatementCaptures(
        HirStatement statement,
        HashSet<SymbolId> boundSymbols,
        List<HirCapture> captures,
        HashSet<SymbolId> capturedSymbols)
    {
        switch (statement)
        {
            case HirDeclStatement { Declaration: HirVal value }:
                CollectSyntheticNodeCaptures(value.Initializer, boundSymbols, captures, capturedSymbols);
                CollectHirPatternBindingSymbols(value.Pattern, boundSymbols);
                if (value.SymbolId.IsValid)
                {
                    boundSymbols.Add(value.SymbolId);
                }
                return;

            case HirDeclStatement { Declaration: HirVarDecl variable }:
                CollectSyntheticNodeCaptures(variable.Initializer, boundSymbols, captures, capturedSymbols);
                CollectHirPatternBindingSymbols(variable.Pattern, boundSymbols);
                if (variable.SymbolId.IsValid)
                {
                    boundSymbols.Add(variable.SymbolId);
                }
                return;

            case HirExprStatement exprStatement:
                CollectSyntheticNodeCaptures(exprStatement.Expression, boundSymbols, captures, capturedSymbols);
                return;

            case HirAssignStatement assignStatement:
                CollectSyntheticNodeCaptures(assignStatement.Target, boundSymbols, captures, capturedSymbols);
                CollectSyntheticNodeCaptures(assignStatement.Value, boundSymbols, captures, capturedSymbols);
                return;
        }
    }

    private void CollectSyntheticPatternCaptures(
        HirPattern? pattern,
        HashSet<SymbolId> boundSymbols,
        List<HirCapture> captures,
        HashSet<SymbolId> capturedSymbols)
    {
        switch (pattern)
        {
            case HirViewPattern viewPattern:
                CollectSyntheticNodeCaptures(viewPattern.View, boundSymbols, captures, capturedSymbols);
                CollectSyntheticPatternCaptures(viewPattern.InnerPattern, boundSymbols, captures, capturedSymbols);
                return;

            case HirCtorPattern ctorPattern:
                foreach (var field in ctorPattern.Fields)
                {
                    CollectSyntheticPatternCaptures(field.Pattern, boundSymbols, captures, capturedSymbols);
                }
                return;

            case HirTuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    CollectSyntheticPatternCaptures(element, boundSymbols, captures, capturedSymbols);
                }
                return;

            case HirListPattern listPattern:
                foreach (var element in listPattern.Elements)
                {
                    CollectSyntheticPatternCaptures(element, boundSymbols, captures, capturedSymbols);
                }

                CollectSyntheticPatternCaptures(listPattern.RestPattern, boundSymbols, captures, capturedSymbols);

                foreach (var element in listPattern.SuffixElements)
                {
                    CollectSyntheticPatternCaptures(element, boundSymbols, captures, capturedSymbols);
                }
                return;

            case HirOrPattern orPattern:
                CollectSyntheticPatternCaptures(orPattern.Left, boundSymbols, captures, capturedSymbols);
                CollectSyntheticPatternCaptures(orPattern.Right, boundSymbols, captures, capturedSymbols);
                return;

            case HirAndPattern andPattern:
                CollectSyntheticPatternCaptures(andPattern.Left, boundSymbols, captures, capturedSymbols);
                CollectSyntheticPatternCaptures(andPattern.Right, boundSymbols, captures, capturedSymbols);
                return;

            case HirNotPattern notPattern:
                CollectSyntheticPatternCaptures(notPattern.InnerPattern, boundSymbols, captures, capturedSymbols);
                return;

            case HirAsPattern asPattern:
                CollectSyntheticPatternCaptures(asPattern.InnerPattern, boundSymbols, captures, capturedSymbols);
                return;
        }
    }

    private static void CollectHirPatternBindingSymbols(HirPattern? pattern, HashSet<SymbolId> boundSymbols)
    {
        switch (pattern)
        {
            case HirVarPattern { SymbolId.IsValid: true } varPattern:
                boundSymbols.Add(varPattern.SymbolId);
                return;

            case HirAsPattern asPattern:
                if (asPattern.SymbolId.IsValid)
                {
                    boundSymbols.Add(asPattern.SymbolId);
                }

                CollectHirPatternBindingSymbols(asPattern.InnerPattern, boundSymbols);
                return;

            case HirCtorPattern ctorPattern:
                foreach (var field in ctorPattern.Fields)
                {
                    CollectHirPatternBindingSymbols(field.Pattern, boundSymbols);
                }
                return;

            case HirTuplePattern tuplePattern:
                foreach (var element in tuplePattern.Elements)
                {
                    CollectHirPatternBindingSymbols(element, boundSymbols);
                }
                return;

            case HirListPattern listPattern:
                foreach (var element in listPattern.Elements)
                {
                    CollectHirPatternBindingSymbols(element, boundSymbols);
                }

                CollectHirPatternBindingSymbols(listPattern.RestPattern, boundSymbols);

                foreach (var element in listPattern.SuffixElements)
                {
                    CollectHirPatternBindingSymbols(element, boundSymbols);
                }
                return;

            case HirOrPattern orPattern:
                CollectHirPatternBindingSymbols(orPattern.Left, boundSymbols);
                CollectHirPatternBindingSymbols(orPattern.Right, boundSymbols);
                return;

            case HirAndPattern andPattern:
                CollectHirPatternBindingSymbols(andPattern.Left, boundSymbols);
                CollectHirPatternBindingSymbols(andPattern.Right, boundSymbols);
                return;

            case HirNotPattern notPattern:
                CollectHirPatternBindingSymbols(notPattern.InnerPattern, boundSymbols);
                return;

            case HirViewPattern viewPattern:
                CollectHirPatternBindingSymbols(viewPattern.InnerPattern, boundSymbols);
                return;
        }
    }

    private void TryAddSyntheticCapture(
        HirVar variable,
        HashSet<SymbolId> boundSymbols,
        List<HirCapture> captures,
        HashSet<SymbolId> capturedSymbols)
    {
        if (!variable.SymbolId.IsValid ||
            boundSymbols.Contains(variable.SymbolId) ||
            capturedSymbols.Contains(variable.SymbolId) ||
            _symbolTable.GetSymbol(variable.SymbolId) is not VarSymbol variableSymbol ||
            variableSymbol.IsModuleLevel)
        {
            return;
        }

        captures.Add(new HirCapture
        {
            Name = variableSymbol.Name,
            SymbolId = variableSymbol.Id,
            TypeId = variable.TypeId.IsValid ? variable.TypeId : variableSymbol.Type,
            IsMutable = variableSymbol.IsMutable
        });
        capturedSymbols.Add(variable.SymbolId);
    }

    private void TryAddSyntheticCapture(
        HirCapture capture,
        HashSet<SymbolId> boundSymbols,
        List<HirCapture> captures,
        HashSet<SymbolId> capturedSymbols)
    {
        if (!capture.SymbolId.IsValid ||
            boundSymbols.Contains(capture.SymbolId) ||
            capturedSymbols.Contains(capture.SymbolId) ||
            _symbolTable.GetSymbol(capture.SymbolId) is not VarSymbol variableSymbol ||
            variableSymbol.IsModuleLevel)
        {
            return;
        }

        captures.Add(capture);
        capturedSymbols.Add(capture.SymbolId);
    }

    private static string GetDoBindParamName(DoBinding binding)
    {
        if (binding.Pattern is Ast.Patterns.VarPattern varPat && !string.IsNullOrEmpty(varPat.Name))
            return varPat.Name;
        return "__do_bind";
    }
}
