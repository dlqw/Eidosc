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

        var parameterIds = GetGenericParameterIds(targetSymbolId);
        var resolved = new List<GenericArgumentNode>(arguments.Count);
        for (var index = 0; index < arguments.Count; index++)
        {
            var parameterKind = index < parameterIds.Count &&
                                _symbolTable.GetSymbol<TypeParamSymbol>(parameterIds[index]) is { } parameter
                ? parameter.ParameterKind
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
            AdtSymbol adt => adt.TypeParams,
            TraitSymbol trait => trait.TypeParams,
            CtorSymbol constructor when constructor.TypeParams.Count > 0 => constructor.TypeParams,
            CtorSymbol constructor => _symbolTable.GetSymbol<AdtSymbol>(constructor.OwnerAdt)?.TypeParams ?? [],
            _ => []
        };
    }

    private bool TryReinterpretSingleValueGenericApplication(IndexExpr index)
    {
        if (index.IsTypeApplication || index.Index == null)
        {
            return false;
        }

        var parameterIds = GetGenericParameterIds(GetGenericApplicationTargetSymbol(index));
        if (parameterIds.Count != 1 ||
            _symbolTable.GetSymbol<TypeParamSymbol>(parameterIds[0]) is not
                { ParameterKind: GenericParameterKind.Value })
        {
            return false;
        }

        index.ReinterpretAsGenericApplication(
        [
            new ValueGenericArgumentNode
            {
                Expression = index.Index,
                Span = index.Index.Span
            }
        ]);
        return true;
    }

    private static EidosAstNode ConvertTypePathToValueExpression(TypePath typePath)
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
        _ => SymbolId.None
    };
}
