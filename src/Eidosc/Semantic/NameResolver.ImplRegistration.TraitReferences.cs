using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private ImplTraitReference ResolveImplTraitReferenceGenericArguments(
        SymbolId traitId,
        ImplTraitReference traitRef,
        SourceSpan applicationSpan)
    {
        var genericArguments = traitRef.GenericArguments.Count > 0
            ? traitRef.GenericArguments
            : traitRef.TypeArgs
                .Select(static typeArgument => (GenericArgumentNode)new UnresolvedGenericArgumentNode
                {
                    TypeCandidate = typeArgument,
                    Span = typeArgument.Span
                })
                .ToList();
        if (genericArguments.Count == 0)
        {
            return traitRef;
        }

        var resolvedArguments = _traitDefinitions.TryGetValue(traitId, out var traitDefinition)
            ? genericArguments
                .Select((argument, index) => ResolveGenericArgument(
                    argument,
                    index < traitDefinition.TypeParams.Count
                        ? traitDefinition.TypeParams[index].ParameterKind
                        : GenericParameterKind.Type,
                    index,
                    applicationSpan))
                .ToList()
            : ResolveGenericArguments(traitId, genericArguments, applicationSpan);
        return traitRef with
        {
            GenericArguments = resolvedArguments,
            TypeArgs = resolvedArguments
                .Select(static argument => argument switch
                {
                    TypeGenericArgumentNode typeArgument => typeArgument.Type,
                    UnresolvedGenericArgumentNode { TypeCandidate: { } typeCandidate } => typeCandidate,
                    _ => null
                })
                .OfType<TypeNode>()
                .ToList(),
            TypeArgTexts = resolvedArguments
                .Select(RenderImplClauseGenericArgumentText)
                .Where(static text => !string.IsNullOrWhiteSpace(text))
                .ToList()
        };
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
