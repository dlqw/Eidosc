using Eidosc.Symbols;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using EidosAttribute = Eidosc.Ast.Attribute;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    /// <summary>
    /// 真实语法约定：未显式 @impl 时，可通过“函数名匹配 trait 方法名 + 首参数是具体类型”注册实现。
    /// </summary>
    private void TryRegisterTraitImplByConvention(FuncDef func)
    {
        if (!TryGetImplTargetType(func, out var implementingTypePath, out var targetTypeId))
        {
            return;
        }

        var matchedTraits = new List<(SymbolId TraitId, SymbolId TraitMethodId)>();
        var genericTraitHints = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in _symbolTable.Symbols.Values)
        {
            if (symbol is not TraitSymbol trait)
            {
                continue;
            }

            if (!IsTraitDefinedInCurrentModule(trait.Id))
            {
                continue;
            }

            if (TryValidateTraitImplCompatibility(
                    trait.Id,
                    func,
                    implementingTypePath,
                    [],
                    out _,
                    out var matchedTraitMethodId))
            {
                matchedTraits.Add((trait.Id, matchedTraitMethodId));
                continue;
            }

            if (IsConventionCandidateForGenericTrait(trait.Id, func.Name))
            {
                genericTraitHints.Add(trait.Name);
            }
        }

        if (matchedTraits.Count == 1)
        {
            if (!TryBuildImplTypeRequirements(
                    func,
                    implementingTypePath,
                    out var implementingTypeRequirements,
                    out var requirementError))
            {
                AddError(func.Span, requirementError ?? DiagnosticMessages.UnsupportedConstrainedImplHead);
                return;
            }

            var implId = _symbolTable.DeclareImpl(
                matchedTraits[0].TraitId,
                targetTypeId,
                func.Span,
                [],
                NormalizeTypePath(implementingTypePath, selfType: null, traitTypeArgBindings: null),
                CanonicalizeTypePathForImplHead(implementingTypePath),
                [],
                [],
                [],
                implementingTypeRequirements,
                BuildImplHeadShape(matchedTraits[0].TraitId, [], implementingTypePath),
                BuildImplTypeRefKey(implementingTypePath));
            if (implId.IsValid && func.SymbolId.IsValid)
            {
                _symbolTable.AddMethodToImpl(implId, func.SymbolId, matchedTraits[0].TraitMethodId);
            }

            return;
        }

        if (matchedTraits.Count == 0 && genericTraitHints.Count > 0)
        {
            AddError(func.Span, BuildConventionGenericTraitHint(func.Name, genericTraitHints));
        }
    }

    private bool IsConventionCandidateForGenericTrait(SymbolId traitId, string functionName)
    {
        if (!_traitDefinitions.TryGetValue(traitId, out var traitDefinition) ||
            traitDefinition.TypeParams.Count == 0)
        {
            return false;
        }

        foreach (var method in traitDefinition.Methods)
        {
            if (string.Equals(method.Name, functionName, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildConventionGenericTraitHint(
        string functionName,
        IReadOnlyCollection<string> traitNames)
    {
        if (traitNames.Count == 1)
        {
            var traitName = traitNames.First();
            return DiagnosticMessages.ConventionGenericTraitHint(functionName, traitName);
        }

        var names = string.Join(", ", traitNames.OrderBy(static name => name, StringComparer.Ordinal));
        return DiagnosticMessages.ConventionGenericTraitsHint(functionName, names);
    }

    private bool TryResolveTraitFromImplAttribute(
        EidosAttribute attribute,
        out SymbolId traitId,
        out string traitName,
        out ImplTraitReference traitRef)
    {
        traitId = SymbolId.None;
        traitName = "";
        traitRef = new([], [], []);

        if (!TryExtractTraitReferenceFromAttribute(attribute, out traitRef))
        {
            return false;
        }

        var path = traitRef.Path;
        traitName = FormatTraitReferenceDisplay(traitRef);
        var result = ResolvePathWithImports(path);
        if (result.IsSuccess && _symbolTable.GetSymbol(result.SymbolId) is TraitSymbol)
        {
            traitId = result.SymbolId;
            return true;
        }

        if (path.Count == 1)
        {
            var fallback = _symbolTable.LookupTrait(path[0]);
            if (fallback is { } fallbackId && _symbolTable.GetSymbol(fallbackId) is TraitSymbol)
            {
                traitId = fallbackId;
                return true;
            }
        }

        return false;
    }

    private static bool TryExtractTraitReferenceFromAttribute(EidosAttribute attribute, out ImplTraitReference traitRef)
    {
        traitRef = new([], [], []);
        if (attribute.Arguments.Count > 0)
        {
            var firstArg = attribute.Arguments[0];
            if (firstArg is PathExpr pathExpr && pathExpr.Path.Count > 0)
            {
                var typeArgs = pathExpr.TypeArgs
                    .Select(RenderImplAttributeTypeArgText)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();
                traitRef = new(new List<string>(pathExpr.Path), typeArgs, pathExpr.TypeArgs.ToList());
                return true;
            }

            if (firstArg is IdentifierExpr identifier && !string.IsNullOrWhiteSpace(identifier.Name))
            {
                traitRef = new([identifier.Name], [], []);
                return true;
            }

            if (firstArg is TypePath typePath && !string.IsNullOrWhiteSpace(typePath.TypeName))
            {
                var path = new List<string>(typePath.ModulePath);
                path.Add(typePath.TypeName);
                var typeArgs = typePath.TypeArgs
                    .Select(RenderImplAttributeTypeArgText)
                    .Where(text => !string.IsNullOrWhiteSpace(text))
                    .ToList();
                traitRef = new(path, typeArgs, typePath.TypeArgs.ToList());
                return true;
            }

            if (TryExtractQualifiedPathFromExpression(firstArg, out var exprPath) &&
                exprPath.Count > 0)
            {
                traitRef = new(exprPath, [], []);
                return true;
            }
        }

        if (attribute.ArgumentTexts.Count > 0 && !string.IsNullOrWhiteSpace(attribute.ArgumentTexts[0]) &&
            TryParseTraitReferenceText(attribute.ArgumentTexts[0], out var parsedRef))
        {
            traitRef = parsedRef;
            return true;
        }

        return false;
    }

    private static bool TryExtractQualifiedPathFromExpression(EidosAstNode expr, out List<string> path)
    {
        path = [];

        switch (expr)
        {
            case PathExpr pathExpr when pathExpr.Path.Count > 0:
                path.AddRange(pathExpr.Path.Where(part => !string.IsNullOrWhiteSpace(part)));
                return path.Count > 0;

            case IdentifierExpr identifierExpr when !string.IsNullOrWhiteSpace(identifierExpr.Name):
                path.Add(identifierExpr.Name);
                return true;

            default:
                return false;
        }
    }

    private static bool TryParseTraitReferenceText(string traitRefText, out ImplTraitReference traitRef)
    {
        traitRef = new([], [], []);
        if (string.IsNullOrWhiteSpace(traitRefText))
        {
            return false;
        }

        var text = traitRefText.Trim();
        if (!TrySplitTraitReferenceText(text, out var pathText, out var typeArgText))
        {
            return false;
        }

        var path = ParsePathText(pathText);
        if (path.Count == 0)
        {
            return false;
        }

        var typeArgTexts = string.IsNullOrWhiteSpace(typeArgText)
            ? new List<string>()
            : SplitTopLevelCommaList(typeArgText);
        traitRef = new(path, typeArgTexts, []);
        return true;
    }

    private static bool TrySplitTraitReferenceText(
        string text,
        out string pathText,
        out string? typeArgText)
    {
        pathText = text;
        typeArgText = null;

        var firstBracket = text.IndexOf('[');
        if (firstBracket < 0)
        {
            return true;
        }

        var closingBracket = FindMatchingBracket(text, firstBracket);
        if (closingBracket < 0)
        {
            return false;
        }

        var trailing = text[(closingBracket + 1)..];
        if (!string.IsNullOrWhiteSpace(trailing))
        {
            return false;
        }

        pathText = text[..firstBracket].Trim();
        typeArgText = text.Substring(firstBracket + 1, closingBracket - firstBracket - 1);
        return !string.IsNullOrWhiteSpace(pathText);
    }

    private static int FindMatchingBracket(string text, int openingBracket)
    {
        if (openingBracket < 0 || openingBracket >= text.Length || text[openingBracket] != '[')
        {
            return -1;
        }

        var depth = 0;
        for (var i = openingBracket; i < text.Length; i++)
        {
            if (text[i] == '[')
            {
                depth++;
                continue;
            }

            if (text[i] != ']')
            {
                continue;
            }

            depth--;
            if (depth == 0)
            {
                return i;
            }
        }

        return -1;
    }
}
