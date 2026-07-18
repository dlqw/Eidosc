using Eidosc.Types;
using Eidosc.Utils;
using EidosType = Eidosc.Types.Type;

namespace Eidosc.Symbols;

internal static class MetaSchemaRegistry
{
    public const string IntrinsicPrefix = "meta.";

    private sealed record MetaTypeSpec(string Name, int TypeId, int Arity = 0);

    private sealed record MetaFunctionSpec(string Name, int Arity);

    private static readonly MetaTypeSpec[] s_types =
    [
        new(WellKnownStrings.Meta.Types.TypeShape, WellKnownTypeIds.MetaTypeShapeId),
        new(WellKnownStrings.Meta.Types.Declaration, WellKnownTypeIds.MetaDeclarationId),
        new(WellKnownStrings.Meta.Types.DeclarationShape, WellKnownTypeIds.MetaDeclarationShapeId),
        new(WellKnownStrings.Meta.Types.Span, WellKnownTypeIds.MetaSpanId),
        new(WellKnownStrings.Meta.Types.Stage, WellKnownTypeIds.MetaStageId),
        new(WellKnownStrings.Meta.Types.Target, WellKnownTypeIds.MetaTargetId, Arity: 1),
        new(WellKnownStrings.Meta.Types.Site, WellKnownTypeIds.MetaSiteId, Arity: 1),
        new(WellKnownStrings.Meta.Types.Transformation, WellKnownTypeIds.MetaTransformationId),
        new(WellKnownStrings.Meta.Types.Syntax, WellKnownTypeIds.MetaSyntaxId, Arity: 1),
        new(WellKnownStrings.Meta.Types.Item, WellKnownTypeIds.MetaItemId),
        new(WellKnownStrings.Meta.Types.Member, WellKnownTypeIds.MetaMemberId),
        new(WellKnownStrings.Meta.Types.Expr, WellKnownTypeIds.MetaExprId),
        new(WellKnownStrings.Meta.Types.Pattern, WellKnownTypeIds.MetaPatternId),
        new(WellKnownStrings.Meta.Types.Stmt, WellKnownTypeIds.MetaStmtId),
        new(WellKnownStrings.Meta.Types.TypeSyntax, WellKnownTypeIds.MetaTypeSyntaxId),
        new(WellKnownStrings.Meta.Types.Branch, WellKnownTypeIds.MetaBranchId),
        new(WellKnownStrings.Meta.Types.Parameter, WellKnownTypeIds.MetaParameterId),
        new(WellKnownStrings.Meta.Types.Binding, WellKnownTypeIds.MetaBindingId),
        new(WellKnownStrings.Meta.Types.ExprSyntax, WellKnownTypeIds.MetaExprSyntaxId),
        new(WellKnownStrings.Meta.Types.PatternSyntax, WellKnownTypeIds.MetaPatternSyntaxId),
        new(WellKnownStrings.Meta.Types.BranchSyntax, WellKnownTypeIds.MetaBranchSyntaxId),
        new(WellKnownStrings.Meta.Types.Field, WellKnownTypeIds.MetaFieldId),
        new(WellKnownStrings.Meta.Types.Constructor, WellKnownTypeIds.MetaConstructorId),
        new(WellKnownStrings.Meta.Types.NamedExpr, WellKnownTypeIds.MetaNamedExprId),
        new(WellKnownStrings.Meta.Types.FieldPattern, WellKnownTypeIds.MetaFieldPatternId),
        new(WellKnownStrings.Meta.Types.Layout, WellKnownTypeIds.MetaLayoutId),
        new(WellKnownStrings.Meta.Types.Clause, WellKnownTypeIds.MetaClauseId),
        new(WellKnownStrings.Meta.Types.ClauseArgument, WellKnownTypeIds.MetaClauseArgumentId),
        new(WellKnownStrings.Meta.Types.Diagnostic, WellKnownTypeIds.MetaDiagnosticId),
        new(WellKnownStrings.Meta.Types.Workspace, WellKnownTypeIds.MetaWorkspaceId),
        new(WellKnownStrings.Meta.Types.Package, WellKnownTypeIds.MetaPackageId),
        new(WellKnownStrings.Meta.Types.Module, WellKnownTypeIds.MetaModuleId),
        new(WellKnownStrings.Meta.Types.Dependency, WellKnownTypeIds.MetaDependencyId),
        new(WellKnownStrings.Meta.Types.Import, WellKnownTypeIds.MetaImportId),
        new(WellKnownStrings.Meta.Types.Export, WellKnownTypeIds.MetaExportId),
        new(WellKnownStrings.Meta.Types.CaseType, WellKnownTypeIds.MetaCaseTypeId),
        new(WellKnownStrings.Meta.Types.Implementation, WellKnownTypeIds.MetaImplementationId),
        new(WellKnownStrings.Meta.Types.Reference, WellKnownTypeIds.MetaReferenceId),
        new(WellKnownStrings.Meta.Types.Call, WellKnownTypeIds.MetaCallId),
        new(WellKnownStrings.Meta.Types.Body, WellKnownTypeIds.MetaBodyId),
        new(WellKnownStrings.Meta.Types.BodyNode, WellKnownTypeIds.MetaBodyNodeId),
        new(WellKnownStrings.Meta.Types.Tokens, WellKnownTypeIds.MetaTokensId),
        new(WellKnownStrings.Meta.Types.Identifier, WellKnownTypeIds.MetaIdentifierId),
        new(WellKnownStrings.Meta.Types.IdentifierCategory, WellKnownTypeIds.MetaIdentifierCategoryId),
        new(WellKnownStrings.Meta.Types.ParseFailure, WellKnownTypeIds.MetaParseFailureId),
        new(WellKnownStrings.Meta.Types.ResolveFailure, WellKnownTypeIds.MetaResolveFailureId),
        new(WellKnownStrings.Meta.Types.GeneratedModule, WellKnownTypeIds.MetaGeneratedModuleId),
        new(WellKnownStrings.Meta.Types.GenerationSlot, WellKnownTypeIds.MetaGenerationSlotId),
        new(WellKnownStrings.Meta.Types.Resource, WellKnownTypeIds.MetaResourceId),
        new(WellKnownStrings.Meta.Types.Fix, WellKnownTypeIds.MetaFixId),
        new(WellKnownStrings.Meta.Types.Origin, WellKnownTypeIds.MetaOriginId),
        new(WellKnownStrings.Meta.Types.Scope, WellKnownTypeIds.MetaScopeId),
        new(WellKnownStrings.Meta.Types.ScopeKind, WellKnownTypeIds.MetaScopeKindId),
        new(WellKnownStrings.Meta.Types.Query, WellKnownTypeIds.MetaQueryId, Arity: 1),
        new(WellKnownStrings.Meta.Types.FunctionShape, WellKnownTypeIds.MetaFunctionShapeId),
        new(WellKnownStrings.Meta.Types.NominalShape, WellKnownTypeIds.MetaNominalShapeId),
        new(WellKnownStrings.Meta.Types.ReferenceShape, WellKnownTypeIds.MetaReferenceShapeId),
        new(WellKnownStrings.Meta.Types.TupleShape, WellKnownTypeIds.MetaTupleShapeId),
        new(WellKnownStrings.Meta.Types.ClosedSumShape, WellKnownTypeIds.MetaClosedSumShapeId),
        new(WellKnownStrings.Meta.Types.CaseShape, WellKnownTypeIds.MetaCaseShapeId),
        new(WellKnownStrings.Meta.Types.GenericArgument, WellKnownTypeIds.MetaGenericArgumentId)
    ];

    private static readonly MetaFunctionSpec[] s_functions =
    [
        new("shape_of", 1),
        new("name_of", 1),
        new("declaration_of", 1),
        new("kind_of", 1),
        new("parameters_of", 1),
        new("constructors_of", 1),
        new("fields_of", 1),
        new("declared_fields_of", 1),
        new("find_field", 2),
        new("has_field", 2),
        new("type_of", 1),
        new("result_type_of", 1),
        new("effects_of", 1),
        new("mutability_of", 1),
        new("referent_of", 1),
        new("items_of", 1),
        new("constraints_of", 1),
        new("clauses_of", 1),
        new("clause_keyword_of", 1),
        new("clause_kind_of", 1),
        new("clause_stage_of", 1),
        new("clause_arguments_of", 1),
        new("clause_occurrence_of", 1),
        new("clause_source_order_of", 1),
        new("clause_argument_type_of", 1),
        new("clause_argument_text_of", 1),
        new("clause_argument_path_of", 1),
        new("clause_argument_index_of", 1),
        new("clause_argument_occurrence_of", 1),
        new("span_of", 1),
        new("target_type_of", 1),
        new("target_declaration_of", 1),
        new("layout_of", 2),
        new("layout_size", 1),
        new("layout_alignment", 1),
        new("layout_field_offsets", 1),
        new("cases_of", 1),
        new("leaf_cases_of", 1),
        new("parent_type_of", 1),
        new("case_type_of", 1),
        new("constructor_of", 1),
        new("is_subtype", 2),
        new("join_type_of", 2),
        new("syntax_of", 1),
        new("arguments_of", 1),
        new("module_of", 1),
        new("package_of", 1),
        new("workspace_of", 1),
        new("modules_of", 1),
        new("imports_of", 1),
        new("exports_of", 1),
        new("body_of", 1),
        new("nodes_of", 1),
        new("value_of", 1),
        new("references_to", 2),
        new("calls_from", 1),
        new("callers_of", 2),
        new("implementations_of", 2),
        new("target_scope", 1),
        new("module_scope", 1),
        new("package_scope", 1),
        new("dependencies_scope", 1),
        new("workspace_scope", 1),
        new("resources_of", 1),
        new("resource_path_of", 1),
        new("resource_content_of", 1),
        new("resource_exists", 1),
        new("resource_hash_of", 1),
        new("error", 2),
        new("warning", 2),
        new("slot_from", 1),
        new("with_slot", 2),
        new("keep", 0),
        new("add_before", 2),
        new("add_after", 2),
        new("add_members", 2),
        new("replace_target", 2),
        new("remove_target", 1),
        new("report", 1),
        new("add_items", 3),
        new("add_module", 2),
        new("combine", 1),
        new("function", 4),
        new("implementation", 3),
        new("comptime_value", 3),
        new("test", 2),
        new("module_member", 1),
        new("diagnostic", 3),
        new("fix", 2),
        new("diagnostic_with_fix", 4),
        new("parameter", 2),
        new("binding", 1),
        new("expr_param", 1),
        new("expr_binding", 1),
        new("expr_declaration", 1),
        new("expr_int", 1),
        new("expr_bool", 1),
        new("expr_string", 1),
        new("expr_unit", 0),
        new("expr_call", 2),
        new("expr_constructor", 2),
        new("expr_constructor_fields", 2),
        new("named_expr", 2),
        new("expr_field", 2),
        new("expr_binary", 3),
        new("expr_tuple", 1),
        new("expr_list", 1),
        new("expr_match", 2),
        new("pattern_wildcard", 0),
        new("pattern_binding", 1),
        new("pattern_constructor", 2),
        new("pattern_constructor_fields", 2),
        new("field_pattern", 2),
        new("branch", 2),
        new("identifier", 2),
        new("site_of", 1),
        new("resolve_at", 2),
        new("origin_of", 1),
        new("parse_items", 2),
        new("parse_expr", 2)
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

        var stageId = SymbolId.None;
        var scopeKindId = SymbolId.None;
        var typeShapeId = SymbolId.None;
        var declarationShapeId = SymbolId.None;
        var identifierCategoryId = SymbolId.None;
        var parseFailureId = SymbolId.None;
        var resolveFailureId = SymbolId.None;
        foreach (var typeSpec in s_types)
        {
            var typeId = symbolTable.RegisterSymbol(new AdtSymbol
            {
                Name = typeSpec.Name,
                Span = SourceSpan.Empty,
                IsModuleLevel = true,
                IsPublic = true,
                TypeId = new TypeId(typeSpec.TypeId),
                TypeParams = Enumerable.Repeat(SymbolId.None, typeSpec.Arity).ToList()
            });
            symbolTable.AddMemberToModule(moduleId, typeId);
            if (string.Equals(typeSpec.Name, WellKnownStrings.Meta.Types.Stage, StringComparison.Ordinal))
            {
                stageId = typeId;
            }
            else if (string.Equals(typeSpec.Name, WellKnownStrings.Meta.Types.ScopeKind, StringComparison.Ordinal))
            {
                scopeKindId = typeId;
            }
            else if (string.Equals(typeSpec.Name, WellKnownStrings.Meta.Types.TypeShape, StringComparison.Ordinal))
            {
                typeShapeId = typeId;
            }
            else if (string.Equals(typeSpec.Name, WellKnownStrings.Meta.Types.DeclarationShape, StringComparison.Ordinal))
            {
                declarationShapeId = typeId;
            }
            else if (string.Equals(typeSpec.Name, WellKnownStrings.Meta.Types.IdentifierCategory, StringComparison.Ordinal))
            {
                identifierCategoryId = typeId;
            }
            else if (string.Equals(typeSpec.Name, WellKnownStrings.Meta.Types.ParseFailure, StringComparison.Ordinal))
            {
                parseFailureId = typeId;
            }
            else if (string.Equals(typeSpec.Name, WellKnownStrings.Meta.Types.ResolveFailure, StringComparison.Ordinal))
            {
                resolveFailureId = typeId;
            }
        }

        if (stageId.IsValid)
        {
            foreach (var stageName in new[] { "Syntax", "Semantic", "Body", "Layout" })
            {
                RegisterMetaCase(symbolTable, stageId, stageName);
            }
        }

        if (scopeKindId.IsValid)
        {
            foreach (var scopeKindName in new[] { "Target", "Module", "Package", "Dependencies", "Workspace" })
            {
                RegisterMetaCase(symbolTable, scopeKindId, scopeKindName);
            }
        }

        if (typeShapeId.IsValid)
        {
            foreach (var (shapeName, payloadTypeId) in new (string Name, int PayloadTypeId)[]
                     {
                         ("Primitive", WellKnownTypeIds.MetaNominalShapeId),
                         ("Nominal", WellKnownTypeIds.MetaNominalShapeId),
                         ("ClosedSum", WellKnownTypeIds.MetaClosedSumShapeId),
                         ("Case", WellKnownTypeIds.MetaCaseShapeId),
                         ("Tuple", WellKnownTypeIds.MetaTupleShapeId),
                         ("Function", WellKnownTypeIds.MetaFunctionShapeId),
                         ("ForeignFunction", WellKnownTypeIds.MetaFunctionShapeId),
                         ("Reference", WellKnownTypeIds.MetaReferenceShapeId),
                         ("RawPointer", WellKnownTypeIds.MetaReferenceShapeId),
                         ("Alias", WellKnownTypeIds.MetaNominalShapeId),
                         ("Trait", WellKnownTypeIds.MetaNominalShapeId),
                         ("Effect", WellKnownTypeIds.MetaNominalShapeId),
                         ("EffectRow", WellKnownTypeIds.MetaNominalShapeId),
                         ("EffectRequest", WellKnownTypeIds.MetaNominalShapeId),
                         ("TypeParameter", WellKnownTypeIds.MetaNominalShapeId),
                         ("HigherKinded", WellKnownTypeIds.MetaNominalShapeId),
                         ("AssociatedProjection", WellKnownTypeIds.MetaNominalShapeId),
                         ("Opaque", WellKnownTypeIds.MetaNominalShapeId),
                         ("Foreign", WellKnownTypeIds.MetaNominalShapeId),
                         ("Error", WellKnownTypeIds.MetaNominalShapeId)
                     })
            {
                RegisterMetaCase(symbolTable, typeShapeId, shapeName, payloadTypeId);
            }
        }

        if (declarationShapeId.IsValid)
        {
            foreach (var shapeName in new[]
                     {
                         "Module", "Type", "CaseType", "Trait", "Effect", "Constructor", "Field", "Function",
                         "Operator", "Parameter", "Variable", "GenericParameter", "AssociatedType",
                         "AssociatedConst", "Instance", "Proof", "Generated"
                     })
            {
                RegisterMetaCase(
                    symbolTable,
                    declarationShapeId,
                    shapeName,
                    WellKnownTypeIds.MetaDeclarationShapeId);
            }
        }

        if (identifierCategoryId.IsValid)
        {
            foreach (var categoryName in new[]
                     {
                         "Item", "Member", "Value", "Type", "Function", "Field", "Constructor",
                         "Parameter", "Local", "Module", "AssociatedType", "AssociatedConst"
                     })
            {
                RegisterMetaCase(symbolTable, identifierCategoryId, categoryName);
            }
        }

        if (parseFailureId.IsValid)
        {
            RegisterMetaCase(symbolTable, parseFailureId, "ParseError", WellKnownTypeIds.StringId);
        }

        if (resolveFailureId.IsValid)
        {
            RegisterMetaCase(symbolTable, resolveFailureId, "NotFound", WellKnownTypeIds.StringId);
            RegisterMetaCase(symbolTable, resolveFailureId, "Ambiguous", WellKnownTypeIds.StringId);
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

    public static bool PrefersCompileTimeNamespace(string name, int argumentIndex) => name switch
    {
        "shape_of" or "name_of" or "declaration_of" or "kind_of" or "parameters_of" or
            "constructors_of" or "fields_of" or "declared_fields_of" or "type_of" or
            "result_type_of" or "effects_of" or "mutability_of" or "referent_of" or
            "items_of" or "constraints_of" or "clauses_of" or "span_of" or "cases_of" or
            "leaf_cases_of" or "parent_type_of" or "case_type_of" or "constructor_of"
            or "syntax_of" or "arguments_of" or "module_of" or "package_of" or "workspace_of"
            or "modules_of" or "imports_of" or "exports_of" or "body_of" or "nodes_of"
            or "value_of" or "calls_from" or "target_scope" or "module_scope" or "package_scope"
            or "dependencies_scope" or "workspace_scope"
            or "resources_of"
            or "resource_path_of" or "resource_content_of" or "resource_exists" or "resource_hash_of"
            or "site_of" or "origin_of"
            => argumentIndex == 0,
        "slot_from" => argumentIndex == 0,
        "clause_keyword_of" or "clause_kind_of" or "clause_stage_of" or
            "clause_arguments_of" or "clause_occurrence_of" or "clause_source_order_of" or
            "clause_argument_type_of" or "clause_argument_text_of" or
            "clause_argument_path_of" or "clause_argument_index_of" or
            "clause_argument_occurrence_of" => false,
        "has_field" or "find_field" or "layout_of" => argumentIndex == 0,
        "is_subtype" or "join_type_of" => argumentIndex is 0 or 1,
        "references_to" or "callers_of" or "implementations_of" => argumentIndex == 0,
        "function" => argumentIndex == 2,
        "implementation" or "comptime_value" or "parameter" => argumentIndex == 1,
        _ => false
    };

    public static EidosType CreateFunctionType(FuncSymbol symbol, Substitution substitution, SymbolTable symbolTable)
    {
        if (!IsMetaIntrinsic(symbol, out var name))
        {
            throw new ArgumentException("Function is not a Meta intrinsic.", nameof(symbol));
        }

        var typeValue = BaseTypes.TypeValue;
        var decl = MetaType(WellKnownStrings.Meta.Types.Declaration, WellKnownTypeIds.MetaDeclarationId);
        var declInfo = MetaType(WellKnownStrings.Meta.Types.DeclarationShape, WellKnownTypeIds.MetaDeclarationShapeId);
        var span = MetaType(WellKnownStrings.Meta.Types.Span, WellKnownTypeIds.MetaSpanId);
        var deriveInput = MetaType(WellKnownStrings.Meta.Types.Target, WellKnownTypeIds.MetaTargetId) with
        {
            Args = [substitution.FreshTypeVariable()]
        };
        var expansion = MetaType(WellKnownStrings.Meta.Types.Transformation, WellKnownTypeIds.MetaTransformationId);
        var declaration = MetaType(WellKnownStrings.Meta.Types.Syntax, WellKnownTypeIds.MetaSyntaxId) with
        {
            Args = [MetaType(WellKnownStrings.Meta.Types.Item, WellKnownTypeIds.MetaItemId)]
        };
        var memberSyntax = MetaType(WellKnownStrings.Meta.Types.Syntax, WellKnownTypeIds.MetaSyntaxId) with
        {
            Args = [MetaType(WellKnownStrings.Meta.Types.Member, WellKnownTypeIds.MetaMemberId)]
        };
        var parameter = MetaType(WellKnownStrings.Meta.Types.Parameter, WellKnownTypeIds.MetaParameterId);
        var binding = MetaType(WellKnownStrings.Meta.Types.Binding, WellKnownTypeIds.MetaBindingId);
        var expr = MetaType(WellKnownStrings.Meta.Types.ExprSyntax, WellKnownTypeIds.MetaExprSyntaxId);
        var pattern = MetaType(WellKnownStrings.Meta.Types.PatternSyntax, WellKnownTypeIds.MetaPatternSyntaxId);
        var branch = MetaType(WellKnownStrings.Meta.Types.BranchSyntax, WellKnownTypeIds.MetaBranchSyntaxId);
        var fieldInfo = MetaType(WellKnownStrings.Meta.Types.Field, WellKnownTypeIds.MetaFieldId);
        var constructorInfo = MetaType(WellKnownStrings.Meta.Types.Constructor, WellKnownTypeIds.MetaConstructorId);
        var namedExpr = MetaType(WellKnownStrings.Meta.Types.NamedExpr, WellKnownTypeIds.MetaNamedExprId);
        var fieldPattern = MetaType(WellKnownStrings.Meta.Types.FieldPattern, WellKnownTypeIds.MetaFieldPatternId);
        var layout = MetaType(WellKnownStrings.Meta.Types.Layout, WellKnownTypeIds.MetaLayoutId);
        var clause = MetaType(WellKnownStrings.Meta.Types.Clause, WellKnownTypeIds.MetaClauseId);
        var clauseArgument = MetaType(WellKnownStrings.Meta.Types.ClauseArgument, WellKnownTypeIds.MetaClauseArgumentId);
        var diagnostic = MetaType(WellKnownStrings.Meta.Types.Diagnostic, WellKnownTypeIds.MetaDiagnosticId);
        var fix = MetaType(WellKnownStrings.Meta.Types.Fix, WellKnownTypeIds.MetaFixId);
        var resource = MetaType(WellKnownStrings.Meta.Types.Resource, WellKnownTypeIds.MetaResourceId);
        var workspace = MetaType(WellKnownStrings.Meta.Types.Workspace, WellKnownTypeIds.MetaWorkspaceId);
        var package = MetaType(WellKnownStrings.Meta.Types.Package, WellKnownTypeIds.MetaPackageId);
        var module = MetaType(WellKnownStrings.Meta.Types.Module, WellKnownTypeIds.MetaModuleId);
        var implementationInfo = MetaType(WellKnownStrings.Meta.Types.Implementation, WellKnownTypeIds.MetaImplementationId);
        var referenceInfo = MetaType(WellKnownStrings.Meta.Types.Reference, WellKnownTypeIds.MetaReferenceId);
        var callInfo = MetaType(WellKnownStrings.Meta.Types.Call, WellKnownTypeIds.MetaCallId);
        var body = MetaType(WellKnownStrings.Meta.Types.Body, WellKnownTypeIds.MetaBodyId);
        var bodyNode = MetaType(WellKnownStrings.Meta.Types.BodyNode, WellKnownTypeIds.MetaBodyNodeId);
        var scope = MetaType(WellKnownStrings.Meta.Types.Scope, WellKnownTypeIds.MetaScopeId);
        var genericArgument = MetaType(WellKnownStrings.Meta.Types.GenericArgument, WellKnownTypeIds.MetaGenericArgumentId);
        var syntax = MetaType(WellKnownStrings.Meta.Types.Syntax, WellKnownTypeIds.MetaSyntaxId) with
        {
            Args = [substitution.FreshTypeVariable()]
        };
        var expressionSyntax = MetaType(WellKnownStrings.Meta.Types.Syntax, WellKnownTypeIds.MetaSyntaxId) with
        {
            Args = [MetaType(WellKnownStrings.Meta.Types.Expr, WellKnownTypeIds.MetaExprId)]
        };
        var identifier = MetaType(WellKnownStrings.Meta.Types.Identifier, WellKnownTypeIds.MetaIdentifierId);
        var origin = MetaType(WellKnownStrings.Meta.Types.Origin, WellKnownTypeIds.MetaOriginId);
        var generationSlot = MetaType(
            WellKnownStrings.Meta.Types.GenerationSlot,
            WellKnownTypeIds.MetaGenerationSlotId);
        var parseFailure = MetaType(WellKnownStrings.Meta.Types.ParseFailure, WellKnownTypeIds.MetaParseFailureId);
        var resolveFailure = MetaType(WellKnownStrings.Meta.Types.ResolveFailure, WellKnownTypeIds.MetaResolveFailureId);
        var site = MetaType(WellKnownStrings.Meta.Types.Site, WellKnownTypeIds.MetaSiteId) with
        {
            Args = [substitution.FreshTypeVariable()]
        };
        var query = MetaType(WellKnownStrings.Meta.Types.Query, WellKnownTypeIds.MetaQueryId) with
        {
            Args = [substitution.FreshTypeVariable()]
        };

        var any = substitution.FreshTypeVariable();
        var parameters = name switch
        {
            "shape_of" or "name_of" or "declaration_of" or "kind_of" or "parameters_of" or
                "constructors_of" or "fields_of" or "declared_fields_of" or "type_of" or
                "result_type_of" or "effects_of" or "mutability_of" or "referent_of" or
                "items_of" or "constraints_of" or "clauses_of" or "span_of" or
                "cases_of" or "leaf_cases_of" or "parent_type_of" or "case_type_of" or
                "constructor_of" => [any],
            "syntax_of" or "arguments_of" or "module_of" or "package_of" or "workspace_of" or
                "modules_of" or "imports_of" or "exports_of" or "body_of" or "nodes_of" or
                "value_of" or "calls_from" or "target_scope" or "module_scope" or "package_scope" or
                "dependencies_scope" or "workspace_scope" => [any],
            "resources_of" => [query],
            "resource_path_of" or "resource_content_of" or "resource_exists" or "resource_hash_of" => [resource],
            "references_to" or "callers_of" or "implementations_of" =>
                [substitution.FreshTypeVariable(), substitution.FreshTypeVariable()],
            "clause_keyword_of" or "clause_kind_of" or "clause_stage_of" or
                "clause_arguments_of" or "clause_occurrence_of" or "clause_source_order_of" => [clause],
            "clause_argument_type_of" or "clause_argument_text_of" or
                "clause_argument_path_of" or "clause_argument_index_of" or
                "clause_argument_occurrence_of" => [clauseArgument],
            "has_field" or "find_field" => [typeValue, BaseTypes.String],
            "is_subtype" or "join_type_of" => [typeValue, typeValue],
            "target_type_of" or "target_declaration_of" => [deriveInput],
            "layout_of" => [typeValue, BaseTypes.String],
            "layout_size" or "layout_alignment" or "layout_field_offsets" => [layout],
            "error" or "warning" => [span, BaseTypes.String],
            "slot_from" => [any],
            "with_slot" => [any, generationSlot],
            "identifier" => [BaseTypes.String, typeValue],
            "site_of" or "origin_of" => [any],
            "resolve_at" => [site, BaseTypes.String],
            "parse_items" or "parse_expr" => [BaseTypes.String, origin],
            "keep" => [],
            "add_before" or "add_after" => [deriveInput, ListOf(symbolTable, declaration)],
            "add_members" => [deriveInput, ListOf(symbolTable, memberSyntax)],
            "replace_target" => [deriveInput, syntax],
            "remove_target" => [deriveInput],
            "report" => [ListOf(symbolTable, diagnostic)],
            "add_items" => [query, module, ListOf(symbolTable, declaration)],
            "add_module" => [query, declaration],
            "combine" => [ListOf(symbolTable, expansion)],
            "function" => [BaseTypes.String, ListOf(symbolTable, parameter), typeValue, expr],
            "implementation" => [decl, typeValue, ListOf(symbolTable, declaration)],
            "comptime_value" => [BaseTypes.String, typeValue, expr],
            "test" => [BaseTypes.String, expr],
            "module_member" => [declaration],
            "diagnostic" => [BaseTypes.String, span, BaseTypes.String],
            "fix" => [span, BaseTypes.String],
            "diagnostic_with_fix" => [BaseTypes.String, span, BaseTypes.String, fix],
            "parameter" => [BaseTypes.String, typeValue],
            "binding" => [BaseTypes.String],
            "expr_param" => [parameter],
            "expr_binding" => [binding],
            "expr_declaration" => [decl],
            "expr_int" => [BaseTypes.Int],
            "expr_bool" => [BaseTypes.Bool],
            "expr_string" => [BaseTypes.String],
            "expr_unit" or "pattern_wildcard" => [],
            "expr_call" => [expr, ListOf(symbolTable, expr)],
            "expr_constructor" => [decl, ListOf(symbolTable, expr)],
            "expr_constructor_fields" => [decl, ListOf(symbolTable, namedExpr)],
            "named_expr" => [fieldInfo, expr],
            "expr_field" => [expr, fieldInfo],
            "expr_binary" => [BaseTypes.String, expr, expr],
            "expr_tuple" or "expr_list" => [ListOf(symbolTable, expr)],
            "expr_match" => [expr, ListOf(symbolTable, branch)],
            "pattern_binding" => [binding],
            "pattern_constructor" => [decl, ListOf(symbolTable, pattern)],
            "pattern_constructor_fields" => [decl, ListOf(symbolTable, fieldPattern)],
            "field_pattern" => [fieldInfo, pattern],
            "branch" => [pattern, expr],
            _ => Enumerable.Repeat<EidosType>(substitution.FreshTypeVariable(), symbol.Parameters.Count).ToList()
        };

        EidosType result = name switch
        {
            "shape_of" => substitution.FreshTypeVariable(),
            "name_of" or "kind_of" or "clause_keyword_of" or "clause_kind_of" or
                "clause_occurrence_of" or "clause_argument_type_of" or
                "clause_argument_text_of" or "clause_argument_occurrence_of" => BaseTypes.String,
            "has_field" or "is_subtype" or "mutability_of" => BaseTypes.Bool,
            "type_of" or "result_type_of" or "referent_of" or
                "target_type_of" or "parent_type_of" or "case_type_of" or "join_type_of" => typeValue,
            "find_field" => OptionOf(symbolTable, fieldInfo),
            "declaration_of" or "target_declaration_of" => decl,
            "parameters_of" or "cases_of" or "leaf_cases_of" => ListOf(symbolTable, typeValue),
            "constructors_of" => ListOf(symbolTable, constructorInfo),
            "fields_of" or "declared_fields_of" => ListOf(symbolTable, fieldInfo),
            "constructor_of" => constructorInfo,
            "syntax_of" => syntax,
            "arguments_of" => ListOf(symbolTable, genericArgument),
            "module_of" => module,
            "package_of" => package,
            "workspace_of" => workspace,
            "modules_of" => ListOf(symbolTable, module),
            "imports_of" => ListOf(symbolTable, module),
            "exports_of" => ListOf(symbolTable, decl),
            "body_of" => body,
            "nodes_of" => ListOf(symbolTable, bodyNode),
            "value_of" => substitution.FreshTypeVariable(),
            "references_to" => ListOf(symbolTable, referenceInfo),
            "calls_from" or "callers_of" => ListOf(symbolTable, callInfo),
            "implementations_of" => ListOf(symbolTable, implementationInfo),
            "target_scope" or "module_scope" or "package_scope" or "dependencies_scope" or
                "workspace_scope" => scope,
            "resources_of" => ListOf(symbolTable, resource),
            "resource_path_of" or "resource_content_of" or "resource_hash_of" => BaseTypes.String,
            "resource_exists" => BaseTypes.Bool,
            "effects_of" or "constraints_of" => ListOf(symbolTable, BaseTypes.String),
            "clauses_of" => ListOf(symbolTable, clause),
            "clause_stage_of" => MetaType(WellKnownStrings.Meta.Types.Stage, WellKnownTypeIds.MetaStageId),
            "clause_arguments_of" => ListOf(symbolTable, clauseArgument),
            "clause_argument_path_of" => ListOf(symbolTable, BaseTypes.String),
            "clause_source_order_of" or "clause_argument_index_of" => BaseTypes.Int,
            "items_of" => ListOf(symbolTable, declInfo),
            "span_of" => span,
            "layout_of" => layout,
            "layout_size" or "layout_alignment" => BaseTypes.Int,
            "layout_field_offsets" => ListOf(symbolTable, BaseTypes.Int),
            "error" or "warning" => BaseTypes.Unit,
            "slot_from" => generationSlot,
            "with_slot" => any,
            "identifier" => identifier,
            "site_of" => site,
            "resolve_at" => ResultOf(symbolTable, decl, resolveFailure),
            "origin_of" => origin,
            "parse_items" => ResultOf(symbolTable, ListOf(symbolTable, declaration), parseFailure),
            "parse_expr" => ResultOf(symbolTable, expressionSyntax, parseFailure),
            "keep" or "add_before" or "add_after" or "add_members" or
                "replace_target" or "remove_target" or "report" or "add_items" or "add_module" or "combine" => expansion,
            "function" or "implementation" or "comptime_value" or "test" or
                "module_member" => declaration,
            "diagnostic" => diagnostic,
            "fix" => fix,
            "diagnostic_with_fix" => diagnostic,
            "parameter" => parameter,
            "binding" => binding,
            "expr_param" or "expr_binding" or "expr_declaration" or "expr_int" or "expr_bool" or "expr_string" or
                "expr_unit" or "expr_call" or "expr_constructor" or "expr_constructor_fields" or "expr_field" or
                "expr_binary" or "expr_tuple" or "expr_list" or "expr_match" => expr,
            "named_expr" => namedExpr,
            "pattern_wildcard" or "pattern_binding" or "pattern_constructor" or "pattern_constructor_fields" => pattern,
            "field_pattern" => fieldPattern,
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

    private static void RegisterMetaCase(
        SymbolTable symbolTable,
        SymbolId owner,
        string name,
        int? payloadTypeId = null)
    {
        var caseTypeId = symbolTable.DeclareCaseType(name, SourceSpan.Empty, owner, isPublic: true);
        var constructorId = symbolTable.RegisterSymbol(new CtorSymbol
        {
            Name = name,
            Span = SourceSpan.Empty,
            OwnerAdt = caseTypeId,
            IsPublic = true
        });
        if (symbolTable.GetSymbol<CtorSymbol>(constructorId) is { } constructor)
        {
            constructor.PositionalArgs = payloadTypeId.HasValue
                ? [new TypeId(payloadTypeId.Value)]
                : [];
            symbolTable.UpdateSymbol(constructor);
        }
        if (symbolTable.GetSymbol<AdtSymbol>(caseTypeId) is { } caseType)
        {
            symbolTable.UpdateSymbol(caseType with
            {
                CaseConstructor = constructorId,
                Constructors = [constructorId]
            });
        }
    }

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

    private static TyCon OptionOf(SymbolTable symbolTable, EidosType elementType)
    {
        var symbol = symbolTable.LookupType(WellKnownStrings.BuiltinTypes.Option) ?? SymbolId.None;
        return new TyCon
        {
            Name = WellKnownStrings.BuiltinTypes.Option,
            Symbol = symbol,
            Id = symbolTable.GetSymbol(symbol)?.TypeId ?? TypeId.None,
            Args = [elementType]
        };
    }

    private static TyCon ResultOf(SymbolTable symbolTable, EidosType successType, EidosType failureType)
    {
        var symbol = symbolTable.LookupType(WellKnownStrings.BuiltinTypes.Result) ?? SymbolId.None;
        return new TyCon
        {
            Name = WellKnownStrings.BuiltinTypes.Result,
            Symbol = symbol,
            Id = symbolTable.GetSymbol(symbol)?.TypeId ?? TypeId.None,
            Args = [successType, failureType]
        };
    }
}
