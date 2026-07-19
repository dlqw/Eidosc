using Eidosc.Ast.Declarations;
using Eidosc.Ast.Types;
using Eidosc.Symbols;

namespace Eidosc.Semantic;

internal enum CompilerMetaProtocolKind
{
    PureComptime,
    SyntaxExpansion,
    Derive,
    BodyTransform,
    Analyzer,
    ExtensionItems,
    ExtensionModules,
    BuildHost,
    LegacyTransformation
}

internal sealed record CompilerMetaProtocolMatch(
    CompilerMetaProtocolKind Kind,
    ClauseStage EarliestStage);

internal static class CompilerMetaProtocolRegistry
{
    public static bool TryClassify(
        FuncDef function,
        int explicitArgumentCount,
        SymbolTable symbolTable,
        out CompilerMetaProtocolMatch match,
        out string reason)
    {
        match = null!;
        reason = string.Empty;
        if (!function.IsComptime || function.Signature.Count != 1)
        {
            reason = "compiler protocols require one comptime-only function signature";
            return false;
        }

        var parameters = new List<TypeNode>();
        TypeNode result = function.Signature[0];
        while (result is ArrowType arrow)
        {
            parameters.Add(arrow.ParamType);
            result = arrow.ReturnType;
        }

        if (parameters.Count != explicitArgumentCount + 1)
        {
            reason = $"protocol expects {explicitArgumentCount} explicit argument(s) followed by one compiler-managed input";
            return false;
        }

        var input = parameters[^1];
        var matches = new List<CompilerMetaProtocolMatch>();
        AddMatch(IsSameSyntaxCategory(input, result, symbolTable), CompilerMetaProtocolKind.SyntaxExpansion, ClauseStage.Syntax);
        AddMatch(IsType(input, WellKnownTypeIds.MetaTypeId, symbolTable) && IsType(result, WellKnownTypeIds.MetaItemsId, symbolTable), CompilerMetaProtocolKind.Derive, ClauseStage.Semantic);
        AddMatch(IsType(input, WellKnownTypeIds.MetaFunctionId, symbolTable) && IsType(result, WellKnownTypeIds.MetaFunctionId, symbolTable), CompilerMetaProtocolKind.BodyTransform, ClauseStage.Body);
        AddMatch(IsType(input, WellKnownTypeIds.MetaPackageId, symbolTable) && IsDiagnosticSequence(result, symbolTable), CompilerMetaProtocolKind.Analyzer, ClauseStage.Body);
        AddMatch(IsType(input, WellKnownTypeIds.MetaPackageId, symbolTable) && IsType(result, WellKnownTypeIds.MetaItemsId, symbolTable), CompilerMetaProtocolKind.ExtensionItems, ClauseStage.Syntax);
        AddMatch(IsType(input, WellKnownTypeIds.MetaPackageId, symbolTable) && IsType(result, WellKnownTypeIds.MetaModulesId, symbolTable), CompilerMetaProtocolKind.ExtensionModules, ClauseStage.Syntax);
        AddMatch(IsType(input, WellKnownTypeIds.BuildInputsId, symbolTable) && IsType(result, WellKnownTypeIds.BuildGraphId, symbolTable), CompilerMetaProtocolKind.BuildHost, ClauseStage.Syntax);

        if (matches.Count == 0)
        {
            match = new CompilerMetaProtocolMatch(CompilerMetaProtocolKind.PureComptime, ClauseStage.Semantic);
            return true;
        }

        if (matches.Count > 1)
        {
            reason = "function signature matches more than one compiler protocol";
            return false;
        }

        match = matches[0];
        return true;

        void AddMatch(bool condition, CompilerMetaProtocolKind kind, ClauseStage stage)
        {
            if (condition)
            {
                matches.Add(new CompilerMetaProtocolMatch(kind, stage));
            }
        }
    }

    private static bool IsSameSyntaxCategory(TypeNode input, TypeNode result, SymbolTable symbolTable) =>
        input is TypePath { TypeArgs.Count: 1 } inputSyntax &&
        result is TypePath { TypeArgs.Count: 1 } resultSyntax &&
        IsType(inputSyntax, WellKnownTypeIds.MetaSyntaxId, symbolTable) &&
        IsType(resultSyntax, WellKnownTypeIds.MetaSyntaxId, symbolTable) &&
        HasSameResolvedType(inputSyntax.TypeArgs[0], resultSyntax.TypeArgs[0]);

    private static bool IsDiagnosticSequence(TypeNode result, SymbolTable symbolTable) =>
        result is TypePath { TypeArgs.Count: 1 } sequence &&
        sequence.SymbolId == symbolTable.LookupType(WellKnownStrings.BuiltinTypes.Seq) &&
        IsType(sequence.TypeArgs[0], WellKnownTypeIds.MetaDiagnosticId, symbolTable);

    private static bool HasSameResolvedType(TypeNode left, TypeNode right) =>
        left is TypePath leftPath &&
        right is TypePath rightPath &&
        ((leftPath.SymbolId.IsValid && leftPath.SymbolId == rightPath.SymbolId) ||
         (!leftPath.SymbolId.IsValid && !rightPath.SymbolId.IsValid &&
          string.Equals(leftPath.TypeName, rightPath.TypeName, StringComparison.Ordinal))) &&
        leftPath.TypeArgs.Count == rightPath.TypeArgs.Count &&
        leftPath.TypeArgs.Zip(rightPath.TypeArgs).All(static pair => HasSameResolvedType(pair.First, pair.Second));

    private static bool IsType(TypeNode node, int typeId, SymbolTable symbolTable) =>
        node is TypePath { SymbolId.IsValid: true } path &&
        (symbolTable.GetSymbol(path.SymbolId)?.TypeId == new TypeId(typeId) ||
         typeId == WellKnownTypeIds.MetaItemsId &&
         string.Equals(symbolTable.GetSymbol(path.SymbolId)?.Name, WellKnownStrings.Meta.Types.Items, StringComparison.Ordinal));
}
