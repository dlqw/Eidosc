using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Symbols;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private List<GenericArgumentNode> ResolveGenericArguments(
        SymbolId targetSymbolId,
        IReadOnlyList<GenericArgumentNode> arguments,
        SourceSpan applicationSpan)
    {
        if (arguments.Count == 0)
        {
            return [];
        }

        var resolved = new List<GenericArgumentNode>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            var parameterKind = TryGetGenericParameterKind(targetSymbolId, index, out var expectedKind)
                ? expectedKind
                : GenericParameterKind.Type;
            resolved.Add(ResolveGenericArgument(arguments[index], parameterKind, index, applicationSpan));
        }

        return resolved;
    }

    private GenericArgumentNode ResolveGenericArgument(
        GenericArgumentNode argument,
        GenericParameterKind parameterKind,
        int argumentIndex,
        SourceSpan applicationSpan)
    {
        if (argument.ResolvedKind is { } resolvedKind && resolvedKind != parameterKind)
        {
            AddError(
                argument.Span,
                $"Generic argument {argumentIndex + 1} has domain '{resolvedKind}' but the parameter expects '{parameterKind}'.");
            return argument;
        }

        return parameterKind switch
        {
            GenericParameterKind.Type => ResolveTypeGenericArgument(argument, argumentIndex, applicationSpan),
            GenericParameterKind.Value => ResolveValueGenericArgument(argument, argumentIndex, applicationSpan),
            GenericParameterKind.EffectRow => ResolveEffectGenericArgument(argument, argumentIndex, applicationSpan),
            _ => argument
        };
    }

    private GenericArgumentNode ResolveTypeGenericArgument(
        GenericArgumentNode argument,
        int argumentIndex,
        SourceSpan applicationSpan)
    {
        var type = argument switch
        {
            TypeGenericArgumentNode resolved => resolved.Type,
            UnresolvedGenericArgumentNode { TypeCandidate: { } candidate } => candidate,
            _ => null
        };
        if (type == null)
        {
            AddError(
                argument.Span,
                $"Generic argument {argumentIndex + 1} must be a type argument.");
            return argument;
        }

        ResolveTypeReferences(type);
        return new TypeGenericArgumentNode
        {
            Type = type,
            Span = HasSourceSpan(argument.Span) ? argument.Span : applicationSpan
        };
    }

    private GenericArgumentNode ResolveValueGenericArgument(
        GenericArgumentNode argument,
        int argumentIndex,
        SourceSpan applicationSpan)
    {
        var expression = argument switch
        {
            ValueGenericArgumentNode resolved => resolved.Expression,
            UnresolvedGenericArgumentNode { ValueCandidate: { } candidate } => candidate,
            UnresolvedGenericArgumentNode { TypeCandidate: TypePath candidate } => ConvertTypePathToValueExpression(candidate),
            _ => null
        };
        if (expression == null)
        {
            AddError(
                argument.Span,
                $"Generic argument {argumentIndex + 1} must be a compile-time value expression.");
            return argument;
        }

        ResolveExpressionReferences(expression);
        return new ValueGenericArgumentNode
        {
            Expression = expression,
            Span = HasSourceSpan(argument.Span) ? argument.Span : applicationSpan
        };
    }

    private GenericArgumentNode ResolveEffectGenericArgument(
        GenericArgumentNode argument,
        int argumentIndex,
        SourceSpan applicationSpan)
    {
        var effectRow = argument switch
        {
            EffectGenericArgumentNode resolved => resolved.EffectRow,
            UnresolvedGenericArgumentNode { TypeCandidate: { } candidate } => candidate,
            _ => null
        };
        if (effectRow == null)
        {
            AddError(
                argument.Span,
                $"Generic argument {argumentIndex + 1} must be an effect-row argument.");
            return argument;
        }

        ResolveTypeReferences(effectRow);
        return new EffectGenericArgumentNode
        {
            EffectRow = effectRow,
            Span = HasSourceSpan(argument.Span) ? argument.Span : applicationSpan
        };
    }

    private IReadOnlyList<SymbolId> GetGenericParameterIds(SymbolId targetSymbolId)
    {
        return _symbolTable.GetSymbol(targetSymbolId) switch
        {
            FuncSymbol function => function.TypeParams,
            AdtSymbol adt => GetClosedAdtGenericParameterIds(adt),
            TraitSymbol trait => trait.TypeParams,
            CtorSymbol constructor when constructor.TypeParams.Count > 0 => constructor.TypeParams,
            CtorSymbol constructor when _symbolTable.GetSymbol<AdtSymbol>(constructor.OwnerAdt) is { } owner =>
                GetClosedAdtGenericParameterIds(owner),
            _ => []
        };
    }

    private IReadOnlyList<SymbolId> GetClosedAdtGenericParameterIds(AdtSymbol adt)
    {
        var current = adt;
        while (current.TypeParams.Count == 0 &&
               current.ParentAdt.IsValid &&
               _symbolTable.GetSymbol<AdtSymbol>(current.ParentAdt) is { } parent)
        {
            current = parent;
        }

        return current.TypeParams;
    }

    private bool TryReinterpretSingleGenericApplication(IndexExpr index)
    {
        if (index.IsTypeApplication || index.Index == null)
        {
            return false;
        }

        var targetSymbolId = GetGenericApplicationTargetSymbol(index);
        if (!TryGetGenericParameterKind(targetSymbolId, 0, out var parameterKind))
        {
            return false;
        }

        GenericArgumentNode argument;
        switch (parameterKind)
        {
            case GenericParameterKind.Type when TryConvertExpressionToTypeCandidate(index.Index, out var type):
                argument = new TypeGenericArgumentNode
                {
                    Type = type,
                    Span = index.Index.Span
                };
                break;
            case GenericParameterKind.Value:
                argument = new ValueGenericArgumentNode
                {
                    Expression = index.Index,
                    Span = index.Index.Span
                };
                break;
            case GenericParameterKind.EffectRow when TryConvertExpressionToTypeCandidate(index.Index, out var effectRow):
                argument = new EffectGenericArgumentNode
                {
                    EffectRow = effectRow,
                    Span = index.Index.Span
                };
                break;
            default:
                return false;
        }

        index.ReinterpretAsGenericApplication([argument]);
        return true;
    }

    private void RegisterGenericParameterKinds(SymbolId symbolId, IReadOnlyList<TypeParam> parameters)
    {
        if (!symbolId.IsValid || parameters.Count == 0)
        {
            return;
        }

        _genericParameterKindsBySymbol[symbolId] = parameters
            .Select(static parameter => parameter.ParameterKind)
            .ToArray();
    }

    private bool TryGetGenericParameterKind(
        SymbolId targetSymbolId,
        int parameterIndex,
        out GenericParameterKind parameterKind)
    {
        if (_genericParameterKindsBySymbol.TryGetValue(targetSymbolId, out var declaredKinds) &&
            parameterIndex >= 0 &&
            parameterIndex < declaredKinds.Count)
        {
            parameterKind = declaredKinds[parameterIndex];
            return true;
        }

        var parameterIds = GetGenericParameterIds(targetSymbolId);
        if (parameterIndex >= 0 &&
            parameterIndex < parameterIds.Count &&
            _symbolTable.GetSymbol<TypeParamSymbol>(parameterIds[parameterIndex]) is { } parameter)
        {
            parameterKind = parameter.ParameterKind;
            return true;
        }

        parameterKind = default;
        return false;
    }

    internal static bool TryRehydrateGenericArguments(
        EidosAstNode application,
        IReadOnlyList<GenericParameterKind> argumentKinds)
    {
        var unresolved = application switch
        {
            IndexExpr { Index: not null } index =>
                new GenericArgumentNode[]
                {
                    new UnresolvedGenericArgumentNode
                    {
                        ValueCandidate = index.Index,
                        Span = index.Index.Span
                    }
                },
            TypePath path => path.GenericArguments.ToArray(),
            AssociatedTypeProjection projection => projection.GenericArguments.ToArray(),
            TraitRef trait => trait.GenericArguments.ToArray(),
            _ => []
        };
        if (unresolved.Length != argumentKinds.Count || unresolved.Length == 0)
        {
            return false;
        }

        var restored = new GenericArgumentNode[unresolved.Length];
        for (var index = 0; index < unresolved.Length; index++)
        {
            if (unresolved[index] is not UnresolvedGenericArgumentNode candidate ||
                !TryResolveRestoredGenericArgument(candidate, argumentKinds[index], out restored[index]))
            {
                return false;
            }
        }

        switch (application)
        {
            case IndexExpr index:
                index.ReinterpretAsGenericApplication(restored);
                return true;
            case TypePath path:
                path.SetGenericArguments(restored);
                return true;
            case AssociatedTypeProjection projection:
                projection.SetGenericArguments(restored);
                return true;
            case TraitRef trait:
                trait.SetGenericArguments(restored);
                return true;
            default:
                return false;
        }
    }

    private static bool TryResolveRestoredGenericArgument(
        UnresolvedGenericArgumentNode candidate,
        GenericParameterKind kind,
        out GenericArgumentNode argument)
    {
        switch (kind)
        {
            case GenericParameterKind.Type when candidate.TypeCandidate is { } type:
                argument = new TypeGenericArgumentNode { Type = type, Span = candidate.Span };
                return true;
            case GenericParameterKind.Type when candidate.ValueCandidate is { } value &&
                                                TryConvertExpressionToTypeCandidate(value, out var convertedType):
                argument = new TypeGenericArgumentNode { Type = convertedType, Span = candidate.Span };
                return true;
            case GenericParameterKind.Value when candidate.ValueCandidate is { } value:
                argument = new ValueGenericArgumentNode { Expression = value, Span = candidate.Span };
                return true;
            case GenericParameterKind.Value when candidate.TypeCandidate is TypePath type:
                argument = new ValueGenericArgumentNode
                {
                    Expression = ConvertTypePathToValueExpression(type),
                    Span = candidate.Span
                };
                return true;
            case GenericParameterKind.EffectRow when candidate.TypeCandidate is { } effectRow:
                argument = new EffectGenericArgumentNode { EffectRow = effectRow, Span = candidate.Span };
                return true;
            case GenericParameterKind.EffectRow when candidate.ValueCandidate is { } value &&
                                                     TryConvertExpressionToTypeCandidate(value, out var convertedEffect):
                argument = new EffectGenericArgumentNode { EffectRow = convertedEffect, Span = candidate.Span };
                return true;
            default:
                argument = null!;
                return false;
        }
    }

    internal static bool TryConvertExpressionToTypeCandidate(EidosAstNode expression, out TypeNode type)
    {
        switch (expression)
        {
            case IdentifierExpr identifier:
            {
                var path = new TypePath();
                path.SetSpan(identifier.Span);
                path.SetTypeName(identifier.Name);
                type = path;
                return true;
            }
            case PathExpr expressionPath:
            {
                var path = new TypePath();
                path.SetSpan(expressionPath.Span);
                path.SetPackageAlias(expressionPath.PackageAlias);
                path.ModulePath = [.. expressionPath.ModulePath];
                path.SetTypeName(expressionPath.Name);
                path.SetGenericArguments(expressionPath.GenericArguments);
                type = path;
                return true;
            }
            case MethodCallExpr
            {
                HasExplicitCallSyntax: false,
                Receiver: not null,
                PositionalArgs.Count: 0,
                NamedArgs.Count: 0
            } member when TryConvertExpressionToTypeCandidate(member.Receiver, out var target):
            {
                var projection = new AssociatedTypeProjection();
                projection.SetSpan(member.Span);
                projection.SetTarget(target);
                projection.SetMemberName(member.MethodName);
                type = projection;
                return true;
            }
            case IndexExpr { Object: not null } application
                when TryConvertExpressionToTypeCandidate(application.Object, out var target):
            {
                IReadOnlyList<GenericArgumentNode> arguments = application.GenericArguments;
                if (arguments.Count == 0 &&
                    application.Index != null &&
                    TryConvertExpressionToTypeCandidate(application.Index, out var argumentType))
                {
                    arguments =
                    [
                        new UnresolvedGenericArgumentNode
                        {
                            TypeCandidate = argumentType,
                            Span = application.Index.Span
                        }
                    ];
                }

                if (arguments.Count == 0)
                {
                    type = null!;
                    return false;
                }

                switch (target)
                {
                    case TypePath path:
                        path.SetGenericArguments(arguments);
                        type = path;
                        return true;
                    case AssociatedTypeProjection projection:
                        projection.SetGenericArguments(arguments);
                        type = projection;
                        return true;
                    default:
                        type = null!;
                        return false;
                }
            }
            case TupleExpr tuple:
            {
                var tupleType = new TupleType { Span = tuple.Span };
                foreach (var element in tuple.Elements)
                {
                    if (!TryConvertExpressionToTypeCandidate(element, out var elementType))
                    {
                        type = null!;
                        return false;
                    }

                    tupleType.Elements.Add(elementType);
                }

                type = tupleType;
                return true;
            }
            case LiteralExpr { RawText: "()" } unit:
                type = new TupleType { Span = unit.Span };
                return true;
            default:
                type = null!;
                return false;
        }
    }

    internal static EidosAstNode ConvertTypePathToValueExpression(TypePath typePath)
    {
        if (TryConvertTypePathToLiteralExpression(typePath, out var literal))
        {
            return literal;
        }

        var expression = new PathExpr();
        expression.SetSpan(typePath.Span);
        expression.SetPackageAlias(typePath.PackageAlias);
        expression.SetModulePath([.. typePath.ModulePath]);
        expression.SetName(typePath.TypeName);
        expression.SetIsTypePath(false);
        expression.SetGenericArguments(typePath.GenericArguments);
        return expression;
    }

    private static bool TryConvertTypePathToLiteralExpression(TypePath typePath, out LiteralExpr expression)
    {
        expression = null!;
        if (typePath.ModulePath.Count > 0 ||
            typePath.GenericArguments.Count > 0 ||
            typePath.TypeArgs.Count > 0 ||
            string.IsNullOrWhiteSpace(typePath.TypeName))
        {
            return false;
        }

        var text = typePath.TypeName;
        var isLiteral = text is "true" or "false" or "()" ||
                        text.StartsWith('"') ||
                        text.StartsWith('\'') ||
                        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ||
                        text.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ||
                        text.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ||
                        long.TryParse(
                            text,
                            System.Globalization.NumberStyles.Integer,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out _) ||
                        double.TryParse(
                            text,
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out _);
        if (!isLiteral)
        {
            return false;
        }

        expression = new LiteralExpr();
        expression.SetSpan(typePath.Span);
        expression.SetLiteral(text);
        return true;
    }

    private static SymbolId GetGenericApplicationTargetSymbol(IndexExpr index) => index.Object switch
    {
        IdentifierExpr identifier => identifier.SymbolId,
        PathExpr path => path.SymbolId,
        MethodCallExpr method => method.SymbolId,
        _ => SymbolId.None
    };
}
