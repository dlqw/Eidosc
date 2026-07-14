using Eidosc.Types;
using Eidosc.Utils;
using EidosType = Eidosc.Types.Type;

namespace Eidosc.Symbols;

internal static class MetaSchemaRegistry
{
    public const string IntrinsicPrefix = "meta.";

    private sealed record MetaTypeSpec(string Name, int TypeId);

    private sealed record MetaFunctionSpec(string Name, int Arity);

    private static readonly MetaTypeSpec[] s_types =
    [
        new(WellKnownStrings.Meta.Types.TypeInfo, WellKnownTypeIds.MetaTypeInfoId),
        new(WellKnownStrings.Meta.Types.Decl, WellKnownTypeIds.MetaDeclId),
        new(WellKnownStrings.Meta.Types.DeclInfo, WellKnownTypeIds.MetaDeclInfoId),
        new(WellKnownStrings.Meta.Types.Span, WellKnownTypeIds.MetaSpanId),
        new(WellKnownStrings.Meta.Types.DeriveInput, WellKnownTypeIds.MetaDeriveInputId),
        new(WellKnownStrings.Meta.Types.Expansion, WellKnownTypeIds.MetaExpansionId),
        new(WellKnownStrings.Meta.Types.Declaration, WellKnownTypeIds.MetaDeclarationId),
        new(WellKnownStrings.Meta.Types.Parameter, WellKnownTypeIds.MetaParameterId),
        new(WellKnownStrings.Meta.Types.Binding, WellKnownTypeIds.MetaBindingId),
        new(WellKnownStrings.Meta.Types.Expr, WellKnownTypeIds.MetaExprId),
        new(WellKnownStrings.Meta.Types.Pattern, WellKnownTypeIds.MetaPatternId),
        new(WellKnownStrings.Meta.Types.Branch, WellKnownTypeIds.MetaBranchId),
        new(WellKnownStrings.Meta.Types.FieldInfo, WellKnownTypeIds.MetaFieldInfoId),
        new(WellKnownStrings.Meta.Types.ConstructorInfo, WellKnownTypeIds.MetaConstructorInfoId),
        new(WellKnownStrings.Meta.Types.NamedExpr, WellKnownTypeIds.MetaNamedExprId),
        new(WellKnownStrings.Meta.Types.FieldPattern, WellKnownTypeIds.MetaFieldPatternId),
        new(WellKnownStrings.Meta.Types.LayoutInfo, WellKnownTypeIds.MetaLayoutInfoId)
    ];

    private static readonly MetaFunctionSpec[] s_functions =
    [
        new("typeInfo", 1),
        new("typeName", 1),
        new("hasField", 2),
        new("fieldType", 2),
        new("declarationInfo", 1),
        new("typeKind", 1),
        new("typeParameters", 1),
        new("constructors", 1),
        new("constructorName", 1),
        new("constructorDecl", 1),
        new("constructorFields", 1),
        new("fieldName", 1),
        new("fieldTypeInfo", 1),
        new("fieldDecl", 1),
        new("functionParameters", 1),
        new("functionResult", 1),
        new("functionEffects", 1),
        new("referenceMutable", 1),
        new("referenceReferent", 1),
        new("traitAssociatedItems", 1),
        new("traitConstraints", 1),
        new("attributes", 1),
        new("decl", 1),
        new("declName", 1),
        new("declKind", 1),
        new("declSpan", 1),
        new("target", 1),
        new("targetDecl", 1),
        new("deriveSpan", 1),
        new("layoutOf", 2),
        new("layoutSize", 1),
        new("layoutAlignment", 1),
        new("layoutFieldOffsets", 1),
        new("error", 2),
        new("warning", 2),
        new("expansion", 1),
        new("function", 4),
        new("implementation", 3),
        new("comptimeValue", 3),
        new("attribute", 3),
        new("test", 2),
        new("moduleMember", 1),
        new("diagnostic", 3),
        new("parameter", 2),
        new("binding", 1),
        new("exprParam", 1),
        new("exprBinding", 1),
        new("exprDecl", 1),
        new("exprInt", 1),
        new("exprBool", 1),
        new("exprString", 1),
        new("exprUnit", 0),
        new("exprCall", 2),
        new("exprCtor", 2),
        new("exprCtorFields", 2),
        new("namedExpr", 2),
        new("exprField", 2),
        new("exprBinary", 3),
        new("exprTuple", 1),
        new("exprList", 1),
        new("exprMatch", 2),
        new("patternWildcard", 0),
        new("patternBinding", 1),
        new("patternCtor", 2),
        new("patternCtorFields", 2),
        new("fieldPattern", 2),
        new("branch", 2)
    ];

    public static void Register(SymbolTable symbolTable)
    {
        if (symbolTable.Modules.LookupRootModule(WellKnownStrings.Meta.Module) is { IsValid: true })
        {
            return;
        }

        var moduleId = symbolTable.DeclareModule(
            WellKnownStrings.Meta.Module,
            [WellKnownStrings.Meta.Module],
            SourceSpan.Empty,
            isPublic: true);

        foreach (var typeSpec in s_types)
        {
            var typeId = symbolTable.RegisterSymbol(new AdtSymbol
            {
                Name = typeSpec.Name,
                Span = SourceSpan.Empty,
                IsModuleLevel = true,
                IsPublic = true,
                TypeId = new TypeId(typeSpec.TypeId)
            });
            symbolTable.AddMemberToModule(moduleId, typeId);
        }

        foreach (var functionSpec in s_functions)
        {
            var functionId = symbolTable.RegisterSymbol(new FuncSymbol
            {
                Name = functionSpec.Name,
                Span = SourceSpan.Empty,
                IsModuleLevel = true,
                IsPublic = true,
                IsComptime = true,
                HasBody = false,
                Parameters = Enumerable.Repeat(SymbolId.None, functionSpec.Arity).ToList(),
                IntrinsicName = IntrinsicPrefix + functionSpec.Name
            });
            symbolTable.AddMemberToModule(moduleId, functionId);
        }
    }

    public static bool IsMetaIntrinsic(FuncSymbol symbol, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(symbol.IntrinsicName) ||
            !symbol.IntrinsicName.StartsWith(IntrinsicPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        name = symbol.IntrinsicName[IntrinsicPrefix.Length..];
        return true;
    }

    public static EidosType CreateFunctionType(FuncSymbol symbol, Substitution substitution, SymbolTable symbolTable)
    {
        if (!IsMetaIntrinsic(symbol, out var name))
        {
            throw new ArgumentException("Function is not a Meta intrinsic.", nameof(symbol));
        }

        var typeValue = BaseTypes.TypeValue;
        var typeInfo = MetaType(WellKnownStrings.Meta.Types.TypeInfo, WellKnownTypeIds.MetaTypeInfoId);
        var decl = MetaType(WellKnownStrings.Meta.Types.Decl, WellKnownTypeIds.MetaDeclId);
        var declInfo = MetaType(WellKnownStrings.Meta.Types.DeclInfo, WellKnownTypeIds.MetaDeclInfoId);
        var span = MetaType(WellKnownStrings.Meta.Types.Span, WellKnownTypeIds.MetaSpanId);
        var deriveInput = MetaType(WellKnownStrings.Meta.Types.DeriveInput, WellKnownTypeIds.MetaDeriveInputId);
        var expansion = MetaType(WellKnownStrings.Meta.Types.Expansion, WellKnownTypeIds.MetaExpansionId);
        var declaration = MetaType(WellKnownStrings.Meta.Types.Declaration, WellKnownTypeIds.MetaDeclarationId);
        var parameter = MetaType(WellKnownStrings.Meta.Types.Parameter, WellKnownTypeIds.MetaParameterId);
        var binding = MetaType(WellKnownStrings.Meta.Types.Binding, WellKnownTypeIds.MetaBindingId);
        var expr = MetaType(WellKnownStrings.Meta.Types.Expr, WellKnownTypeIds.MetaExprId);
        var pattern = MetaType(WellKnownStrings.Meta.Types.Pattern, WellKnownTypeIds.MetaPatternId);
        var branch = MetaType(WellKnownStrings.Meta.Types.Branch, WellKnownTypeIds.MetaBranchId);
        var fieldInfo = MetaType(WellKnownStrings.Meta.Types.FieldInfo, WellKnownTypeIds.MetaFieldInfoId);
        var constructorInfo = MetaType(WellKnownStrings.Meta.Types.ConstructorInfo, WellKnownTypeIds.MetaConstructorInfoId);
        var namedExpr = MetaType(WellKnownStrings.Meta.Types.NamedExpr, WellKnownTypeIds.MetaNamedExprId);
        var fieldPattern = MetaType(WellKnownStrings.Meta.Types.FieldPattern, WellKnownTypeIds.MetaFieldPatternId);
        var layout = MetaType(WellKnownStrings.Meta.Types.LayoutInfo, WellKnownTypeIds.MetaLayoutInfoId);

        var any = substitution.FreshTypeVariable();
        var parameters = name switch
        {
            "typeInfo" or "typeName" => [typeValue],
            "hasField" or "fieldType" => [typeValue, BaseTypes.String],
            "declarationInfo" => [decl],
            "typeKind" or "typeParameters" or "constructors" or "functionParameters" or
                "functionResult" or "functionEffects" or "referenceMutable" or "referenceReferent" or
                "traitAssociatedItems" or "traitConstraints" or "attributes" => [typeInfo],
            "constructorName" or "constructorDecl" or "constructorFields" => [constructorInfo],
            "fieldName" or "fieldTypeInfo" or "fieldDecl" => [fieldInfo],
            "decl" => [any],
            "declName" or "declKind" or "declSpan" => [declInfo],
            "target" or "targetDecl" or "deriveSpan" => [deriveInput],
            "layoutOf" => [typeValue, BaseTypes.String],
            "layoutSize" or "layoutAlignment" or "layoutFieldOffsets" => [layout],
            "error" or "warning" => [span, BaseTypes.String],
            "expansion" => [ListOf(symbolTable, declaration)],
            "function" => [BaseTypes.String, ListOf(symbolTable, parameter), typeValue, expr],
            "implementation" => [decl, typeValue, ListOf(symbolTable, declaration)],
            "comptimeValue" => [BaseTypes.String, typeValue, expr],
            "attribute" => [decl, BaseTypes.String, ListOf(symbolTable, BaseTypes.String)],
            "test" => [BaseTypes.String, expr],
            "moduleMember" => [declaration],
            "diagnostic" => [BaseTypes.String, span, BaseTypes.String],
            "parameter" => [BaseTypes.String, typeValue],
            "binding" => [BaseTypes.String],
            "exprParam" => [parameter],
            "exprBinding" => [binding],
            "exprDecl" => [decl],
            "exprInt" => [BaseTypes.Int],
            "exprBool" => [BaseTypes.Bool],
            "exprString" => [BaseTypes.String],
            "exprUnit" or "patternWildcard" => [],
            "exprCall" => [expr, ListOf(symbolTable, expr)],
            "exprCtor" => [decl, ListOf(symbolTable, expr)],
            "exprCtorFields" => [decl, ListOf(symbolTable, namedExpr)],
            "namedExpr" => [fieldInfo, expr],
            "exprField" => [expr, fieldInfo],
            "exprBinary" => [BaseTypes.String, expr, expr],
            "exprTuple" or "exprList" => [ListOf(symbolTable, expr)],
            "exprMatch" => [expr, ListOf(symbolTable, branch)],
            "patternBinding" => [binding],
            "patternCtor" => [decl, ListOf(symbolTable, pattern)],
            "patternCtorFields" => [decl, ListOf(symbolTable, fieldPattern)],
            "fieldPattern" => [fieldInfo, pattern],
            "branch" => [pattern, expr],
            _ => Enumerable.Repeat<EidosType>(substitution.FreshTypeVariable(), symbol.Parameters.Count).ToList()
        };

        EidosType result = name switch
        {
            "typeInfo" => typeInfo,
            "typeName" or "typeKind" or "constructorName" or "fieldName" or "declName" or
                "declKind" => BaseTypes.String,
            "hasField" or "referenceMutable" => BaseTypes.Bool,
            "fieldType" or "fieldTypeInfo" or "functionResult" or "referenceReferent" or "target" => typeValue,
            "declarationInfo" => declInfo,
            "typeParameters" or "functionParameters" => ListOf(symbolTable, typeValue),
            "constructors" => ListOf(symbolTable, constructorInfo),
            "constructorDecl" or "fieldDecl" or "targetDecl" or "decl" => decl,
            "constructorFields" => ListOf(symbolTable, fieldInfo),
            "functionEffects" or "traitConstraints" or "attributes" => ListOf(symbolTable, BaseTypes.String),
            "traitAssociatedItems" => ListOf(symbolTable, declInfo),
            "declSpan" or "deriveSpan" => span,
            "layoutOf" => layout,
            "layoutSize" or "layoutAlignment" => BaseTypes.Int,
            "layoutFieldOffsets" => ListOf(symbolTable, BaseTypes.Int),
            "error" or "warning" => BaseTypes.Unit,
            "expansion" => expansion,
            "function" or "implementation" or "comptimeValue" or "attribute" or "test" or
                "moduleMember" or "diagnostic" => declaration,
            "parameter" => parameter,
            "binding" => binding,
            "exprParam" or "exprBinding" or "exprDecl" or "exprInt" or "exprBool" or "exprString" or
                "exprUnit" or "exprCall" or "exprCtor" or "exprCtorFields" or "exprField" or
                "exprBinary" or "exprTuple" or "exprList" or "exprMatch" => expr,
            "namedExpr" => namedExpr,
            "patternWildcard" or "patternBinding" or "patternCtor" or "patternCtorFields" => pattern,
            "fieldPattern" => fieldPattern,
            "branch" => branch,
            _ => substitution.FreshTypeVariable()
        };

        return new TyFun { Params = parameters, Result = result };
    }

    public static TyCon MetaType(string name, int typeId) => new()
    {
        Name = name,
        Id = new TypeId(typeId)
    };

    private static TyCon ListOf(SymbolTable symbolTable, EidosType elementType)
    {
        var symbol = symbolTable.LookupType(WellKnownStrings.BuiltinTypes.Seq) ?? SymbolId.None;
        return new TyCon
        {
            Name = WellKnownStrings.BuiltinTypes.Seq,
            Symbol = symbol,
            Args = [elementType]
        };
    }
}
