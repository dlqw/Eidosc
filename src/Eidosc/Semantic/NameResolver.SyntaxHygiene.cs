using System.Security.Cryptography;
using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Symbols;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static string GetSyntaxBindingName(EidosAstNode node, string sourceName)
    {
        if (node.AttachedSyntaxIdentity is not { Kind: SyntaxIdentityKind.Hygiene } identity)
        {
            return sourceName;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity.StableIdentity)))
            .ToLowerInvariant()[..16];
        var suffix = new string(sourceName
            .Where(static character => char.IsLetterOrDigit(character) || character == '_')
            .ToArray());
        return string.IsNullOrWhiteSpace(suffix)
            ? $"meta_hygiene_{hash}"
            : $"meta_hygiene_{hash}_{suffix}";
    }

    private void RegisterSyntaxIdentitySymbol(EidosAstNode node, SymbolId symbolId)
    {
        if (!symbolId.IsValid ||
            node.AttachedSyntaxIdentity is not
            {
                Kind: SyntaxIdentityKind.Hygiene or SyntaxIdentityKind.Identifier,
                StableIdentity.Length: > 0
            } identity)
        {
            return;
        }

        if (!_syntaxIdentitySymbols.TryGetValue(identity.StableIdentity, out var symbols))
        {
            symbols = [];
            _syntaxIdentitySymbols[identity.StableIdentity] = symbols;
        }
        if (!symbols.Contains(symbolId))
        {
            symbols.Add(symbolId);
        }
    }

    private bool TryUseMappedSyntaxIdentity(EidosAstNode node, out Symbol symbol)
    {
        symbol = null!;
        if (node.AttachedSyntaxIdentity is not
            {
                Kind: SyntaxIdentityKind.Hygiene or SyntaxIdentityKind.Identifier,
                StableIdentity.Length: > 0
            } identity ||
            !_syntaxIdentitySymbols.TryGetValue(identity.StableIdentity, out var candidates))
        {
            return false;
        }

        var selected = candidates
            .Select(_symbolTable.GetSymbol)
            .Where(static candidate => candidate != null)
            .Where(candidate => IsSyntaxSymbolCompatible(node, candidate!))
            .DistinctBy(static candidate => candidate!.Id)
            .ToArray();
        if (selected.Length != 1)
        {
            return false;
        }

        symbol = selected[0]!;
        node.SymbolId = symbol.Id;
        return true;
    }

    private static bool IsSyntaxSymbolCompatible(EidosAstNode node, Symbol symbol) => node switch
    {
        TypePath => symbol is AdtSymbol or TraitSymbol or EffectSymbol or TypeParamSymbol or AssociatedTypeSymbol,
        CtorExpr or CtorPattern => symbol is CtorSymbol,
        MethodCallExpr { HasExplicitCallSyntax: false } => symbol is FieldSymbol or VarSymbol or FuncSymbol,
        MethodCallExpr => symbol is FuncSymbol or CtorSymbol,
        IdentifierExpr => symbol is VarSymbol or FuncSymbol or CtorSymbol or ImplSymbol or AssociatedConstSymbol,
        PathExpr => true,
        _ => true
    };

    private static bool HasUnresolvedHygienicSyntaxIdentity(EidosAstNode node) =>
        node.AttachedSyntaxIdentity is { Kind: SyntaxIdentityKind.Hygiene };

    private void AddUnresolvedHygienicIdentifierError(EidosAstNode node, string name)
    {
        AddError(
            node.Span,
            $"hygienic identifier '{name}' has no quote-local or definition-site binding; " +
            "use meta.resolve_at for explicit call-site capture");
    }
}
