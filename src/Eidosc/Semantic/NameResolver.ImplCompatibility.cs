using Eidosc.Symbols;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Diagnostic;
using Eidosc.Types;

namespace Eidosc.Semantic;

public sealed partial class NameResolver
{
    private static List<string> SplitTopLevelCommaList(string text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var start = 0;
        var squareDepth = 0;
        var roundDepth = 0;
        var braceDepth = 0;

        for (var i = 0; i < text.Length; i++)
        {
            switch (text[i])
            {
                case '[':
                    squareDepth++;
                    break;
                case ']':
                    squareDepth = Math.Max(0, squareDepth - 1);
                    break;
                case '(':
                    roundDepth++;
                    break;
                case ')':
                    roundDepth = Math.Max(0, roundDepth - 1);
                    break;
                case '{':
                    braceDepth++;
                    break;
                case '}':
                    braceDepth = Math.Max(0, braceDepth - 1);
                    break;
                case ',' when squareDepth == 0 && roundDepth == 0 && braceDepth == 0:
                {
                    var piece = text.Substring(start, i - start).Trim();
                    if (!string.IsNullOrWhiteSpace(piece))
                    {
                        result.Add(piece);
                    }

                    start = i + 1;
                    break;
                }
            }
        }

        var tail = text[start..].Trim();
        if (!string.IsNullOrWhiteSpace(tail))
        {
            result.Add(tail);
        }

        return result;
    }

    private static List<string> ParsePathText(string pathText)
    {
        var separators = new[] { WellKnownStrings.Separators.Path, WellKnownStrings.Separators.ModulePath };
        var pieces = pathText.Split(separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new List<string>(pieces);
    }

    private static string RenderImplAttributeTypeArgText(TypeNode typeNode)
    {
        return NormalizeTypeNode(typeNode, selfType: null, traitTypeArgBindings: null);
    }

    private static string FormatTraitReferenceDisplay(ImplTraitReference traitRef)
    {
        var path = string.Join(WellKnownStrings.Separators.Path, traitRef.Path);
        if (traitRef.TypeArgTexts.Count == 0)
        {
            return path;
        }

        return $"{path}[{string.Join(", ", traitRef.TypeArgTexts)}]";
    }

    private bool TryBuildImplTypeRequirements(
        FuncDef func,
        TypePath implementingTypePath,
        out List<ImplTypeArgTraitRequirement> implementingTypeRequirements,
        out string? errorMessage)
    {
        implementingTypeRequirements = [];
        errorMessage = null;

        var constrainedTypeParams = func.TypeParams
            .Where(typeParam => typeParam.SymbolId.IsValid && typeParam.TraitConstraints.Count > 0)
            .ToDictionary(typeParam => typeParam.SymbolId, typeParam => typeParam);

        if (constrainedTypeParams.Count == 0)
        {
            return true;
        }

        var coveredTypeParams = new HashSet<SymbolId>();
        for (var i = 0; i < implementingTypePath.TypeArgs.Count; i++)
        {
            if (implementingTypePath.TypeArgs[i] is not TypePath
                {
                    ModulePath.Count: 0,
                    TypeArgs.Count: 0
                } typeArgPath)
            {
                continue;
            }

            TypeParam? typeParam = null;
            if (typeArgPath.SymbolId.IsValid &&
                constrainedTypeParams.TryGetValue(typeArgPath.SymbolId, out var symbolMatchedTypeParam))
            {
                typeParam = symbolMatchedTypeParam;
            }
            else
            {
                typeParam = constrainedTypeParams.Values.FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, typeArgPath.TypeName, StringComparison.Ordinal));
            }

            if (typeParam == null)
            {
                continue;
            }

            coveredTypeParams.Add(typeParam.SymbolId);
            foreach (var traitRef in typeParam.TraitConstraints)
            {
                implementingTypeRequirements.Add(new ImplTypeArgTraitRequirement
                {
                    TypeArgIndex = i,
                    Trait = ResolveImplRequirementTraitId(traitRef),
                    TraitName = string.IsNullOrWhiteSpace(traitRef.TraitName)
                        ? ComposeTraitRefDisplayName(traitRef)
                        : traitRef.TraitName,
                    TraitTypeArgs = traitRef.TypeArgs
                        .Select(RenderImplAttributeTypeArgText)
                        .ToList(),
                    TraitTypeArgKeys = traitRef.TypeArgs
                        .Select(BuildImplTypeRefKey)
                        .ToList()
                });
            }
        }

        return true;
    }

    private SymbolId ResolveImplRequirementTraitId(TraitRef traitRef)
    {
        if (traitRef.SymbolId.IsValid)
        {
            return traitRef.SymbolId;
        }

        var pathParts = new List<string>(traitRef.ModulePath);
        if (!string.IsNullOrWhiteSpace(traitRef.TraitName))
        {
            pathParts.Add(traitRef.TraitName);
        }

        if (pathParts.Count == 0)
        {
            return SymbolId.None;
        }

        var resolution = ResolvePathWithImports(pathParts);
        return resolution.IsSuccess ? resolution.SymbolId : SymbolId.None;
    }

    private bool TryGetImplTargetType(FuncDef func, out TypePath implementingTypePath, out TypeId typeId)
    {
        implementingTypePath = null!;
        typeId = TypeId.None;

        if (TryResolveImplTargetTypeNode(GetFirstParameterType(func), out implementingTypePath, out typeId)
            && typeId != BaseTypes.UnitId)
        {
            return true;
        }

        return TryResolveImplTargetTypeNode(GetReturnType(func), out implementingTypePath, out typeId);
    }

    private bool TryResolveImplTargetTypeNode(TypeNode? typeNode, out TypePath implementingTypePath, out TypeId typeId)
    {
        implementingTypePath = null!;
        typeId = TypeId.None;

        if (typeNode is not TypePath typePath)
        {
            return false;
        }

        var symbolId = typePath.SymbolId;
        if (!symbolId.IsValid && !string.IsNullOrWhiteSpace(typePath.TypeName))
        {
            symbolId = _symbolTable.LookupType(typePath.TypeName) ?? SymbolId.None;
        }

        if (!symbolId.IsValid)
        {
            return false;
        }

        var typeSymbol = _symbolTable.GetSymbol(symbolId);
        if (typeSymbol is TypeParamSymbol)
        {
            return false;
        }

        if (typeSymbol?.TypeId.IsValid != true)
        {
            return false;
        }

        implementingTypePath = typePath;
        typeId = typeSymbol.TypeId;
        return true;
    }

    private static TypeNode? GetFirstParameterType(FuncDef func)
    {
        if (func.Signature.Count == 0)
        {
            return null;
        }

        if (func.Signature.Count > 1)
        {
            return func.Signature[0];
        }

        return func.Signature[0] switch
        {
            ArrowType arrow => arrow.ParamType,
            EffectfulType effectful => effectful.InputType,
            _ => null
        };
    }

    private static TypeNode? GetReturnType(FuncDef func)
    {
        if (func.Signature.Count == 0)
        {
            return null;
        }

        if (func.Signature.Count > 1)
        {
            return func.Signature[^1];
        }

        return func.Signature[0] switch
        {
            ArrowType arrow => arrow.ReturnType,
            EffectfulType effectful => effectful.OutputType,
            _ => null
        };
    }

    private bool TryValidateTraitImplCompatibility(
        SymbolId traitId,
        FuncDef function,
        TypePath implementingTypePath,
        IReadOnlyList<string> traitTypeArgs,
        out string reason)
    {
        return TryValidateTraitImplCompatibility(
            traitId,
            function,
            implementingTypePath,
            traitTypeArgs,
            out reason,
            out _);
    }

    private bool TryValidateTraitImplCompatibility(
        SymbolId traitId,
        FuncDef function,
        TypePath implementingTypePath,
        IReadOnlyList<string> traitTypeArgs,
        out string reason,
        out SymbolId matchedTraitMethodId)
    {
        reason = string.Empty;
        matchedTraitMethodId = SymbolId.None;
        if (!_traitDefinitions.TryGetValue(traitId, out var traitDefinition))
        {
            var traitName = _symbolTable.GetSymbol(traitId)?.Name ?? "<unknown>";
            reason = DiagnosticMessages.TraitDefinitionUnavailableForImplSignature(traitName);
            return false;
        }

        var candidateMethods = traitDefinition.Methods
            .Where(method => string.Equals(method.Name, function.Name, StringComparison.Ordinal))
            .ToList();

        if (traitDefinition.Methods.Count == 0)
        {
            return true;
        }

        if (candidateMethods.Count == 0)
        {
            var expectedNames = traitDefinition.Methods.Select(m => m.Name).Distinct().ToList();
            var expectedDisplay = expectedNames.Count > 0
                ? string.Join(", ", expectedNames)
                : "<none>";
            var traitName = _symbolTable.GetSymbol(traitId)?.Name ?? traitDefinition.Name;
            reason = DiagnosticMessages.TraitFunctionDoesNotMatchMethods(function.Name, traitName, expectedDisplay);
            return false;
        }

        var traitTypeArgBindings = BuildTraitTypeArgBindings(traitDefinition, traitTypeArgs, out reason);
        if (traitTypeArgBindings == null)
        {
            return false;
        }

        foreach (var method in candidateMethods)
        {
            if (AreSignaturesEquivalent(
                    method,
                    function,
                    implementingTypePath,
                    traitTypeArgBindings))
            {
                matchedTraitMethodId = method.SymbolId;
                return true;
            }
        }

        var traitMethodSignature = NormalizeSignature(
            candidateMethods[0],
            implementingTypePath,
            traitTypeArgBindings);
        var functionSignature = NormalizeSignature(
            function,
            selfType: null,
            traitTypeArgBindings: null);
        reason = DiagnosticMessages.TraitMethodSignatureMismatch(traitMethodSignature, functionSignature);
        return false;
    }

}
