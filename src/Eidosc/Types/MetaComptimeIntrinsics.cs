using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Eidosc.Syntax;
using Eidosc.Utils;

namespace Eidosc.Types;

internal static partial class MetaComptimeIntrinsics
{
    public static bool TryEvaluate(
        string name,
        CallExpr call,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        var traceKind = ClassifyTraceKind(name);
        context.Meta?.Trace?.Record(
            context.Meta.TracePhase,
            traceKind,
            $"meta.{name}",
            "begin",
            $"arguments={call.PositionalArgs.Count + call.NamedArgs.Count}",
            call.Span,
            context.CallDepth);
        var result = TryEvaluateCore(name, call, context, out value, out reason);
        context.Meta?.Trace?.Record(
            context.Meta.TracePhase,
            traceKind,
            $"meta.{name}",
            result ? "success" : "failure",
            result ? value.CanonicalText : reason,
            call.Span,
            context.CallDepth);
        return result;
    }

    private static bool TryEvaluateCore(
        string name,
        CallExpr call,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = string.Empty;
        if (context.Meta == null)
        {
            reason = $"meta.{name} requires a compiler meta evaluation context";
            return false;
        }

        if (name == "declaration_of")
        {
            return TryCreateDeclarationHandle(call, context, out value, out reason);
        }

        if (!TryEvaluateArguments(call, context, out var arguments, out reason))
        {
            return false;
        }

        if (IsCachedQuery(name))
        {
            return TryEvaluateExtendedQuery(name, arguments, context, call.Span, out value, out reason);
        }

        return name switch
        {
            "error" => TryReportDiagnostic(arguments, MetaDiagnosticLevel.Error, context.Meta, context.Resources, out value, out reason),
            "warning" => TryReportDiagnostic(arguments, MetaDiagnosticLevel.Warning, context.Meta, context.Resources, out value, out reason),
            "with_body" => TryReplaceFunctionBody(arguments, out value, out reason),
            "identifier" => TryCreateIdentifier(arguments, call.Span, context.Meta, out value, out reason),
            "site_of" => TryCreateSite(arguments, context.Meta, out value, out reason),
            "resolve_at" => TryResolveAt(arguments, context.Meta, out value, out reason),
            "origin_of" => TryCreateOrigin(arguments, context.Meta, out value, out reason),
            "parse_items" => TryParseTextSyntax(arguments, QuoteKind.Items, context, out value, out reason),
            "parse_expr" => TryParseTextSyntax(arguments, QuoteKind.Expression, context, out value, out reason),
            "function" => TryObject("declaration.function", arguments, ["name", "parameters", "result", "body"], out value, out reason),
            "implementation" => TryObject("declaration.implementation", arguments, ["trait", "target", "methods"], out value, out reason),
            "comptime_value" => TryObject("declaration.comptime-value", arguments, ["name", "type", "value"], out value, out reason),
            "test" => TryObject("declaration.test", arguments, ["name", "body"], out value, out reason),
            "module_member" => TryObject("declaration.module-member", arguments, ["declaration"], out value, out reason),
            "diagnostic" => TryObject("diagnostic", arguments, ["level", "span", "message"], out value, out reason),
            "fix" => TryObject("fix", arguments, ["span", "replacement"], out value, out reason),
            "diagnostic_with_fix" => TryObject("diagnostic", arguments, ["level", "span", "message", "fix"], out value, out reason),
            "parameter" => TryHandleObject("parameter", call.Span, context.Meta, arguments, ["name", "type"], out value, out reason),
            "binding" => TryHandleObject("binding", call.Span, context.Meta, arguments, ["name"], out value, out reason),
            "expr_param" => TryObject("expr.parameter", arguments, ["parameter"], out value, out reason),
            "expr_binding" => TryObject("expr.binding", arguments, ["binding"], out value, out reason),
            "expr_declaration" => TryObject("expr.decl", arguments, ["decl"], out value, out reason),
            "expr_int" => TryObject("expr.int", arguments, ["value"], out value, out reason),
            "expr_bool" => TryObject("expr.bool", arguments, ["value"], out value, out reason),
            "expr_string" => TryObject("expr.string", arguments, ["value"], out value, out reason),
            "expr_unit" => TryObject("expr.unit", arguments, [], out value, out reason),
            "expr_call" => TryObject("expr.call", arguments, ["callee", "arguments"], out value, out reason),
            "expr_constructor" => TryObject("expr.constructor", arguments, ["constructor", "arguments"], out value, out reason),
            "expr_constructor_fields" => TryObject("expr.record-constructor", arguments, ["constructor", "fields"], out value, out reason),
            "named_expr" => TryObject("named-expr", arguments, ["field", "expression"], out value, out reason),
            "expr_field" => TryObject("expr.field", arguments, ["subject", "field"], out value, out reason),
            "expr_binary" => TryObject("expr.binary", arguments, ["operator", "left", "right"], out value, out reason),
            "expr_tuple" => TryObject("expr.tuple", arguments, ["elements"], out value, out reason),
            "expr_list" => TryObject("expr.list", arguments, ["elements"], out value, out reason),
            "expr_match" => TryObject("expr.match", arguments, ["subject", "branches"], out value, out reason),
            "pattern_wildcard" => TryObject("pattern.wildcard", arguments, [], out value, out reason),
            "pattern_binding" => TryObject("pattern.binding", arguments, ["binding"], out value, out reason),
            "pattern_constructor" => TryObject("pattern.constructor", arguments, ["constructor", "patterns"], out value, out reason),
            "pattern_constructor_fields" => TryObject("pattern.record-constructor", arguments, ["constructor", "fields"], out value, out reason),
            "field_pattern" => TryObject("field-pattern", arguments, ["field", "pattern"], out value, out reason),
            "branch" => TryObject("branch", arguments, ["pattern", "expression"], out value, out reason),
            _ => Fail($"unknown Meta intrinsic '{name}'", out value, out reason)
        };
    }

    private static bool TryReplaceFunctionBody(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeMetaObjectValue { SchemaKind: "function-handle" } function ||
            arguments[1] is not ComptimeSyntaxValue { Category: SyntaxCategory.Expression } body)
        {
            return Fail(
                "meta.Function.with_body expects a function handle and meta.Syntax[meta.Expr]",
                out value,
                out reason);
        }

        value = new ComptimeMetaObjectValue(
            function.SchemaKind,
            [
                .. function.Properties.Where(static property =>
                    !string.Equals(property.Name, "replacementBody", StringComparison.Ordinal)),
                new ComptimeNamedValue("replacementBody", body)
            ])
        {
            StaticType = function.StaticType
        };
        reason = string.Empty;
        return true;
    }

    private static string ClassifyTraceKind(string name)
    {
        return name switch
        {
            "shape_of" or "name_of" or "has_field" or "find_field" or "kind_of" or
            "parameters_of" or "constructors_of" or "fields_of" or "declared_fields_of" or
            "type_of" or "result_type_of" or "effects_of" or "mutability_of" or
             "referent_of" or "items_of" or "constraints_of" or "clauses_of" or "span_of" or
             "clause_keyword_of" or "clause_kind_of" or
             "clause_arguments_of" or "clause_occurrence_of" or "clause_source_order_of" or
             "clause_argument_type_of" or "clause_argument_text_of" or
             "clause_argument_path_of" or "clause_argument_index_of" or
             "clause_argument_occurrence_of" or
            "layout_of" or "layout_size" or
            "layout_alignment" or "layout_field_offsets" or "cases_of" or "leaf_cases_of" or
            "parent_type_of" or "case_type_of" or "constructor_of" or "is_subtype" or
            "join_type_of" or "syntax_of" or "arguments_of" or "module_of" or "package_of" or
            "workspace_of" or "modules_of" or "imports_of" or "exports_of" or "body_of" or
            "nodes_of" or "value_of" or "references_to" or "calls_from" or "callers_of" or
            "implementations_of" or "declaration_of" => "query",
            "resource_path_of" or "resource_content_of" or "resource_exists" or "resource_hash_of" => "query",
            "error" or "warning" or "diagnostic" => "diagnostic",
            "identifier" or "site_of" or "resolve_at" or "origin_of" or "parse_items" or "parse_expr" => "syntax",
            _ => "builder"
        };
    }

    private static bool TryCreateDeclarationHandle(
        CallExpr call,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        var meta = context.Meta!;
        if (!TryGetSingleCallArgument(call, "Meta", out var argument, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        if (TryGetReferencedSymbol(argument, out var symbolId) &&
            meta.SymbolTable.GetSymbol(symbolId) is { } symbol)
        {
            return TryCompleteDeclarationQuery(
                CreateDeclValue(symbol, meta.SymbolTable),
                call.Span,
                context,
                out value,
                out reason);
        }

        if (!ComptimeEvaluator.TryEvaluateNode(argument, context, out var reflected, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        if (TryGetDeclarationFromReflectedValue(reflected, out var declaration))
        {
            return TryCompleteDeclarationQuery(
                declaration,
                call.Span,
                context,
                out value,
                out reason);
        }

        return Fail("meta.declaration_of expects one resolved declaration, type, trait, constructor, function, or body-node handle", out value, out reason);
    }

    private static bool TryCompleteDeclarationQuery(
        ComptimeDeclValue declaration,
        SourceSpan span,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        var meta = context.Meta!;
        var arguments = new ComptimeValue[] { declaration };
        var key = CreateQueryKey("declaration_of", arguments, meta);
        if (meta.Queries.TryGet(key, out value))
        {
            if (!context.Resources.TryConsumeQuery(value, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            meta.Queries.Record(key, value, cacheHit: true);
            RecordQueryCacheTrace("declaration_of", key, value, cacheHit: true, span, context);
            return true;
        }

        value = declaration;
        if (!context.Resources.TryConsumeQuery(value, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        meta.Queries.Store(key, value);
        meta.Queries.Record(key, value, cacheHit: false);
        RecordQueryCacheTrace("declaration_of", key, value, cacheHit: false, span, context);
        reason = string.Empty;
        return true;
    }

    private static bool TryGetReferencedSymbol(EidosAstNode node, out SymbolId symbolId)
    {
        symbolId = node switch
        {
            IdentifierExpr identifier => identifier.SymbolId,
            PathExpr path => path.SymbolId,
            MethodCallExpr { HasExplicitCallSyntax: false, ResolvedStaticExpression: { } resolved } =>
                resolved.SymbolId,
            MethodCallExpr { HasExplicitCallSyntax: false, ResolvedAsStaticPath: true } staticMember =>
                staticMember.SymbolId,
            _ => SymbolId.None
        };
        return symbolId.IsValid;
    }

    private static bool TryTypeInfo(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!TrySingle<ComptimeTypeValue>(arguments, out var typeValue, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = BuildTypeInfo(typeValue, meta);
        return true;
    }

    private static bool TryTypeName(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (!TrySingle<ComptimeTypeValue>(arguments, out var typeValue, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = new ComptimeStringValue(typeValue.TypeRef.Name);
        return true;
    }

    private static bool TryShapeOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1)
        {
            return Fail("meta.shape_of expects one reflected value", out value, out reason);
        }

        switch (arguments[0])
        {
            case ComptimeTypeValue typeValue:
                if (typeValue.TypeRef.SymbolId.IsValid &&
                    !CanAccessDeclaration(typeValue.TypeRef.SymbolId, requireBody: false, meta, out reason))
                {
                    value = ComptimeUnitValue.Instance;
                    return false;
                }
                value = BuildTypeInfo(typeValue, meta);
                reason = string.Empty;
                return true;
            case ComptimeDeclValue declaration:
                if (!CanAccessDeclaration(declaration.SymbolId, requireBody: false, meta, out reason))
                {
                    value = ComptimeUnitValue.Instance;
                    return false;
                }
                value = CreateDeclInfo(declaration, meta);
                reason = string.Empty;
                return true;
            default:
                return Fail("meta.shape_of expects Type or meta.Declaration", out value, out reason);
        }
    }

    private static bool TryNameOf(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1)
        {
            return Fail("meta.name_of expects one reflected value", out value, out reason);
        }

        var name = arguments[0] switch
        {
            ComptimeTypeValue typeValue => typeValue.TypeRef.Name,
            ComptimeDeclValue declaration => declaration.Name,
            ComptimeMetaObjectValue objectValue when objectValue.TryGet("name", out var property) &&
                                                         property is ComptimeStringValue stringValue => stringValue.Value,
            ComptimeAdtValue shape when TryGetShapePayload(shape, out var payload) &&
                                        payload.TryGet("name", out var property) &&
                                        property is ComptimeStringValue stringValue => stringValue.Value,
            _ => null
        };
        if (name == null)
        {
            return Fail("reflected value has no name", out value, out reason);
        }

        value = new ComptimeStringValue(name);
        reason = string.Empty;
        return true;
    }

    private static bool TryAnyObjectProperty(
        IReadOnlyList<ComptimeValue> arguments,
        string propertyName,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1)
        {
            return Fail($"meta query expects one argument exposing '{propertyName}'", out value, out reason);
        }

        if (arguments[0] is ComptimeDeclValue declarationHandle &&
            TryGetDeclarationSemanticProperty(declarationHandle, propertyName, meta, out value))
        {
            reason = string.Empty;
            return true;
        }

        var reflectedValue = arguments[0] switch
        {
            ComptimeMetaObjectValue reflected => (ComptimeValue)reflected,
            ComptimeAdtValue reflected => reflected,
            ComptimeTypeValue typeValue => BuildTypeInfo(typeValue, meta),
            ComptimeDeclValue declaration => CreateDeclInfo(declaration, meta),
            _ => null
        };
        if (reflectedValue == null || !TryGetReflectedProperty(reflectedValue, propertyName, out value))
        {
            return Fail($"reflected value does not expose '{propertyName}'", out value, out reason);
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryGetDeclarationSemanticProperty(
        ComptimeDeclValue declaration,
        string propertyName,
        MetaComptimeContext meta,
        out ComptimeValue value)
    {
        value = ComptimeUnitValue.Instance;
        if (!meta.DeclarationDefinitions.TryGetValue(declaration.SymbolId, out var definition) ||
            propertyName != "functionEffects")
        {
            return false;
        }

        IReadOnlyList<EffectRequirementNode> requirements = definition switch
        {
            FuncDef function => function.RequiredAbilities,
            FuncDecl function => function.RequiredAbilities,
            _ => []
        };
        if (definition is not (FuncDef or FuncDecl))
        {
            return false;
        }

        value = List(requirements.Select(requirement =>
        {
            var effect = requirement.SymbolId.IsValid
                ? meta.SymbolTable.GetSymbol<EffectSymbol>(requirement.SymbolId)
                : null;
            var display = requirement.Path.Count == 0
                ? effect?.Name ?? string.Empty
                : string.Join(WellKnownStrings.Separators.Path, requirement.Path);
            var identity = effect == null
                ? $"effect:{display}"
                : CreateStableIdentity(effect, meta.SymbolTable);
            return (ComptimeValue)new ComptimeTypeValue(new MetaTypeRef(
                MetaTypeKind.Effect,
                display,
                identity,
                effect?.Id ?? SymbolId.None,
                effect?.TypeId ?? TypeId.None,
                []));
        }));
        return true;
    }

    private static bool TryGetReflectedProperty(
        ComptimeValue reflected,
        string propertyName,
        out ComptimeValue value)
    {
        if (reflected is ComptimeMetaObjectValue objectValue)
        {
            return objectValue.TryGet(propertyName, out value);
        }

        if (reflected is ComptimeAdtValue shape && TryGetShapePayload(shape, out var payload))
        {
            return payload.TryGet(propertyName, out value);
        }

        value = ComptimeUnitValue.Instance;
        return false;
    }

    private static bool TryGetShapePayload(
        ComptimeAdtValue shape,
        out ComptimeMetaObjectValue payload)
    {
        if (shape.PositionalValues.Count == 1 &&
            shape.PositionalValues[0] is ComptimeMetaObjectValue objectValue)
        {
            payload = objectValue;
            return true;
        }

        payload = null!;
        return false;
    }

    private static bool TryFindFieldValue(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryTypeAndName(arguments, out var typeValue, out var fieldName, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        if (!TryFindField(typeValue, fieldName, meta, out var field, out var owner))
        {
            value = CreateOptionValue(meta.SymbolTable, isSome: false, ComptimeUnitValue.Instance);
            reason = string.Empty;
            return true;
        }

        value = CreateOptionValue(
            meta.SymbolTable,
            isSome: true,
            CreateFieldInfo(field.Name, field.Type, field.SymbolId, field.Span, owner, meta));
        reason = string.Empty;
        return true;
    }

    private static ComptimeAdtValue CreateOptionValue(
        SymbolTable symbolTable,
        bool isSome,
        ComptimeValue payload)
    {
        var constructorName = isSome ? "Some" : "None";
        var constructorId = symbolTable.Symbols.Values
            .OfType<AdtSymbol>()
            .Where(static symbol => !symbol.IsCaseType &&
                                    string.Equals(symbol.Name, WellKnownStrings.BuiltinTypes.Option, StringComparison.Ordinal))
            .SelectMany(symbol => symbol.DirectCases
                .Select(symbolTable.GetSymbol<AdtSymbol>)
                .Where(caseType => caseType != null && string.Equals(caseType.Name, constructorName, StringComparison.Ordinal)))
            .Select(static caseType => caseType!.CaseConstructor)
            .FirstOrDefault(SymbolId.None);
        return new ComptimeAdtValue(
            constructorId,
            constructorName,
            isSome ? [payload] : [],
            [])
        {
            ConstructorIdentity = constructorId.IsValid &&
                                  symbolTable.GetSymbol(constructorId) is { } constructor
                ? CreateStableIdentity(constructor, symbolTable)
                : $"builtin:Option.{constructorName}"
        };
    }

    private static bool TryFieldsOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        bool declaredOnly,
        out ComptimeValue value,
        out string reason)
    {
        if (!TrySingle<ComptimeTypeValue>(arguments, out var typeValue, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        if (!TryGetAdtAndCase(typeValue.TypeRef.SymbolId, meta, out var root, out var casePath))
        {
            return Fail($"type '{typeValue.TypeRef.Name}' has no field reflection data", out value, out reason);
        }

        var fields = new List<Field>();
        if (!declaredOnly || casePath.Count == 0)
        {
            fields.AddRange(root.Fields);
        }
        if (casePath.Count > 0)
        {
            var selected = declaredOnly ? casePath.TakeLast(1) : casePath;
            foreach (var caseType in selected)
            {
                fields.AddRange(caseType.Fields);
            }
        }

        value = List(fields.Select(field => (ComptimeValue)CreateFieldInfo(
            field.Name,
            field.Type,
            field.SymbolId,
            field.Span,
            root,
            meta)));
        reason = string.Empty;
        return true;
    }

    private static bool TryCasesOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        bool leavesOnly,
        out ComptimeValue value,
        out string reason)
    {
        if (!TrySingle<ComptimeTypeValue>(arguments, out var typeValue, out reason) ||
            !typeValue.TypeRef.SymbolId.IsValid ||
            meta.SymbolTable.GetSymbol<AdtSymbol>(typeValue.TypeRef.SymbolId) is not { } owner)
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        var cases = new List<ComptimeValue>();
        if (leavesOnly)
        {
            CollectLeaves(owner, typeValue);
        }
        else
        {
            foreach (var caseId in owner.DirectCases)
            {
                if (meta.SymbolTable.GetSymbol<AdtSymbol>(caseId) is { } caseType)
                {
                    cases.Add(CreateClosedCaseTypeValue(caseType, typeValue, meta));
                }
            }
        }

        value = List(cases);
        reason = string.Empty;
        return true;

        void CollectLeaves(AdtSymbol parent, ComptimeTypeValue reflectedParent)
        {
            foreach (var caseId in parent.DirectCases)
            {
                if (meta.SymbolTable.GetSymbol<AdtSymbol>(caseId) is not { } caseType)
                {
                    continue;
                }

                var reflectedCase = CreateClosedCaseTypeValue(caseType, reflectedParent, meta);
                if (caseType.DirectCases.Count == 0)
                {
                    cases.Add(reflectedCase);
                }
                else
                {
                    CollectLeaves(caseType, reflectedCase);
                }
            }
        }
    }

    private static ComptimeTypeValue CreateClosedCaseTypeValue(
        AdtSymbol caseType,
        ComptimeTypeValue parentType,
        MetaComptimeContext meta)
    {
        var parentArguments = CreateClosedCaseParentArguments(caseType, parentType, meta);
        var localArguments = caseType.TypeParams
            .Select(id => meta.SymbolTable.GetSymbol<TypeParamSymbol>(id))
            .Where(static parameter => parameter != null)
            .Select(parameter => CreateGenericParameterRef(parameter!, meta.SymbolTable));
        var genericArguments = parentArguments.Concat(localArguments).ToArray();
        var typeArguments = genericArguments
            .Where(static argument => argument.Domain == MetaGenericArgumentDomain.Type && argument.Type != null)
            .Select(static argument => argument.Type!)
            .ToArray();
        var baseType = CreateTypeValue(caseType, meta.SymbolTable).TypeRef;
        var stableIdentity = genericArguments.Length == 0
            ? baseType.StableIdentity
            : $"{baseType.StableIdentity}[{string.Join(";", genericArguments.Select(static argument => argument.CanonicalText))}]";
        var display = genericArguments.Length == 0
            ? baseType.Name
            : $"{baseType.Name}<{string.Join(", ", genericArguments.Select(static argument => argument.Display))}>";
        return new ComptimeTypeValue(baseType with
        {
            Name = display,
            StableIdentity = stableIdentity,
            Arguments = typeArguments,
            GenericArguments = genericArguments
        });
    }

    private static IReadOnlyList<MetaGenericArgumentRef> CreateClosedCaseParentArguments(
        AdtSymbol caseType,
        ComptimeTypeValue parentType,
        MetaComptimeContext meta)
    {
        var inheritedArguments = parentType.TypeRef.GenericArguments ?? [];
        if (!meta.DeclarationDefinitions.TryGetValue(caseType.Id, out var declaration) ||
            declaration is not CaseTypeDef { ParentSpecialization: TypePath specialization })
        {
            return inheritedArguments;
        }

        var explicitArguments = CreateGenericArgumentRefs(specialization, meta.SymbolTable);
        var effectiveParentParameters = meta.SymbolTable.GetClosedCaseEffectiveGenericParameterIds(caseType.ParentAdt);
        var parentBindings = effectiveParentParameters
            .Zip(inheritedArguments, static (parameter, argument) => (parameter, argument))
            .ToDictionary(static pair => pair.parameter, static pair => pair.argument);
        var substitutedArguments = explicitArguments
            .Select(argument => SubstituteMetaGenericArgument(argument, parentBindings, meta.SymbolTable))
            .ToArray();

        var directParentParameterCount = meta.SymbolTable.GetSymbol<AdtSymbol>(caseType.ParentAdt)?.TypeParams.Count ?? 0;
        var inheritedPrefixCount = effectiveParentParameters.Count - directParentParameterCount;
        if (inheritedPrefixCount > 0 && substitutedArguments.Length == directParentParameterCount)
        {
            return inheritedArguments.Take(inheritedPrefixCount).Concat(substitutedArguments).ToArray();
        }

        return substitutedArguments;
    }

    private static MetaGenericArgumentRef SubstituteMetaGenericArgument(
        MetaGenericArgumentRef argument,
        IReadOnlyDictionary<SymbolId, MetaGenericArgumentRef> bindings,
        SymbolTable symbolTable)
    {
        if (argument.SymbolId.IsValid &&
            bindings.TryGetValue(argument.SymbolId, out var bound) &&
            argument.Domain == bound.Domain)
        {
            return bound;
        }

        if (argument.Type == null)
        {
            return argument;
        }

        var substitutedType = SubstituteMetaTypeRef(argument.Type, bindings, symbolTable);
        return ReferenceEquals(substitutedType, argument.Type)
            ? argument
            : argument with
            {
                Display = substitutedType.Name,
                StableIdentity = substitutedType.StableIdentity,
                SymbolId = substitutedType.SymbolId,
                Type = substitutedType
            };
    }

    private static MetaTypeRef SubstituteMetaTypeRef(
        MetaTypeRef type,
        IReadOnlyDictionary<SymbolId, MetaGenericArgumentRef> bindings,
        SymbolTable symbolTable)
    {
        if (type.Kind == MetaTypeKind.TypeParameter &&
            type.SymbolId.IsValid &&
            bindings.TryGetValue(type.SymbolId, out var bound) &&
            bound.Type != null)
        {
            return bound.Type;
        }

        var arguments = type.Arguments
            .Select(argument => SubstituteMetaTypeRef(argument, bindings, symbolTable))
            .ToArray();
        var genericArguments = (type.GenericArguments ?? [])
            .Select(argument => SubstituteMetaGenericArgument(argument, bindings, symbolTable))
            .ToArray();
        var argumentsChanged = !arguments.SequenceEqual(type.Arguments);
        var genericArgumentsChanged = !genericArguments.SequenceEqual(type.GenericArguments ?? []);
        if (!argumentsChanged && !genericArgumentsChanged)
        {
            return type;
        }

        var symbol = type.SymbolId.IsValid ? symbolTable.GetSymbol(type.SymbolId) : null;
        var stableIdentity = type.Kind switch
        {
            MetaTypeKind.Tuple => $"tuple:{string.Join(",", arguments.Select(static argument => argument.StableIdentity))}",
            MetaTypeKind.Function when arguments.Length >= 2 =>
                $"function:{arguments[0].StableIdentity}->{arguments[^1].StableIdentity}",
            _ when genericArguments.Length > 0 =>
                $"{(symbol == null ? type.StableIdentity.Split('[', 2)[0] : CreateStableIdentity(symbol, symbolTable))}" +
                $"[{string.Join(";", genericArguments.Select(static argument => argument.CanonicalText))}]",
            _ => type.StableIdentity
        };
        var name = type.Kind switch
        {
            MetaTypeKind.Tuple => $"({string.Join(", ", arguments.Select(static argument => argument.Name))})",
            _ when genericArguments.Length > 0 =>
                $"{symbol?.Name ?? type.Name.Split(['[', '<'], 2)[0]}" +
                $"[{string.Join(", ", genericArguments.Select(static argument => argument.Display))}]",
            _ => type.Name
        };
        return type with
        {
            Name = name,
            StableIdentity = stableIdentity,
            Arguments = arguments,
            GenericArguments = genericArguments
        };
    }

    private static bool TryParentTypeOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!TrySingle<ComptimeTypeValue>(arguments, out var typeValue, out reason) ||
            meta.SymbolTable.GetSymbol<AdtSymbol>(typeValue.TypeRef.SymbolId) is not { ParentAdt.IsValid: true } caseType ||
            meta.SymbolTable.GetSymbol(caseType.ParentAdt) is not { } parent)
        {
            return Fail("meta.parent_type_of expects an exact case type", out value, out reason);
        }

        if (!TryProjectTypeValue(typeValue, parent, meta.SymbolTable, out var projected))
        {
            return Fail("exact case type does not carry a complete parent specialization", out value, out reason);
        }

        value = projected;
        reason = string.Empty;
        return true;
    }

    private static bool TryCaseTypeOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!TrySingle<ComptimeMetaObjectValue>(arguments, out var constructor, out reason) ||
            !constructor.TryGet("decl", out var declarationValue) ||
            declarationValue is not ComptimeDeclValue declaration ||
            meta.SymbolTable.GetSymbol<CtorSymbol>(declaration.SymbolId) is not { } ctor ||
            meta.SymbolTable.GetSymbol(ctor.OwnerAdt) is not { } caseType)
        {
            return Fail("meta.case_type_of expects a case constructor", out value, out reason);
        }

        value = CreateTypeValue(caseType, meta.SymbolTable);
        reason = string.Empty;
        return true;
    }

    private static bool TryConstructorOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!TrySingle<ComptimeTypeValue>(arguments, out var typeValue, out reason) ||
            meta.SymbolTable.GetSymbol<AdtSymbol>(typeValue.TypeRef.SymbolId) is not { CaseConstructor.IsValid: true } caseType ||
            meta.SymbolTable.GetSymbol<CtorSymbol>(caseType.CaseConstructor) is not { } constructor)
        {
            return Fail("meta.constructor_of expects a constructible case type", out value, out reason);
        }

        var root = meta.AdtDefinitions.Values.FirstOrDefault(adt =>
            adt.Constructors.Any(candidate => candidate.SymbolId == constructor.Id));
        var astConstructor = root?.Constructors.FirstOrDefault(candidate => candidate.SymbolId == constructor.Id);
        if (root == null || astConstructor == null)
        {
            return Fail("case constructor has no semantic reflection record", out value, out reason);
        }

        value = CreateConstructorInfo(astConstructor, root, meta);
        reason = string.Empty;
        return true;
    }

    private static bool TryIsSubtype(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryTwoTypes(arguments, out var left, out var right, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        var isNominalSubtype = meta.SymbolTable.IsClosedCaseSubtype(left.TypeRef.SymbolId, right.TypeRef.SymbolId);
        var specializationMatches = isNominalSubtype &&
                                    meta.SymbolTable.GetSymbol(right.TypeRef.SymbolId) is { } rightSymbol &&
                                    TryProjectTypeValue(left, rightSymbol, meta.SymbolTable, out var projectedLeft) &&
                                    HaveSameGenericSpecialization(projectedLeft.TypeRef, right.TypeRef);
        value = new ComptimeBoolValue(specializationMatches);
        reason = string.Empty;
        return true;
    }

    private static bool TryJoinTypeOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryTwoTypes(arguments, out var left, out var right, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        var ancestor = meta.SymbolTable.FindNearestClosedCommonAncestor(left.TypeRef.SymbolId, right.TypeRef.SymbolId);
        if (!ancestor.IsValid || meta.SymbolTable.GetSymbol(ancestor) is not { } symbol)
        {
            return Fail("types have no closed nominal common ancestor", out value, out reason);
        }

        if (!TryProjectTypeValue(left, symbol, meta.SymbolTable, out var projectedLeft) ||
            !TryProjectTypeValue(right, symbol, meta.SymbolTable, out var projectedRight) ||
            !HaveSameGenericSpecialization(projectedLeft.TypeRef, projectedRight.TypeRef))
        {
            return Fail("types have incompatible closed-case specializations", out value, out reason);
        }

        value = projectedLeft;
        reason = string.Empty;
        return true;
    }

    private static bool TryProjectTypeValue(
        ComptimeTypeValue source,
        Symbol target,
        SymbolTable symbolTable,
        out ComptimeTypeValue projected)
    {
        var parameterCount = target is AdtSymbol adt
            ? symbolTable.GetClosedCaseEffectiveGenericParameterIds(adt.Id).Count
            : 0;
        var sourceArguments = source.TypeRef.GenericArguments ?? [];
        if (sourceArguments.Count < parameterCount)
        {
            projected = null!;
            return false;
        }

        var genericArguments = sourceArguments.Take(parameterCount).ToArray();
        var typeArguments = genericArguments
            .Where(static argument => argument.Domain == MetaGenericArgumentDomain.Type && argument.Type != null)
            .Select(static argument => argument.Type!)
            .ToArray();
        var baseType = CreateTypeValue(target, symbolTable).TypeRef;
        var stableIdentity = genericArguments.Length == 0
            ? baseType.StableIdentity
            : $"{baseType.StableIdentity}[{string.Join(";", genericArguments.Select(static argument => argument.CanonicalText))}]";
        var display = genericArguments.Length == 0
            ? baseType.Name
            : $"{baseType.Name}<{string.Join(", ", genericArguments.Select(static argument => argument.Display))}>";
        projected = new ComptimeTypeValue(baseType with
        {
            Name = display,
            StableIdentity = stableIdentity,
            Arguments = typeArguments,
            GenericArguments = genericArguments
        });
        return true;
    }

    private static bool HaveSameGenericSpecialization(MetaTypeRef left, MetaTypeRef right)
    {
        var leftArguments = left.GenericArguments ?? [];
        var rightArguments = right.GenericArguments ?? [];
        return leftArguments.Count == rightArguments.Count &&
               leftArguments.Zip(rightArguments, static (leftArgument, rightArgument) =>
                       string.Equals(leftArgument.CanonicalText, rightArgument.CanonicalText, StringComparison.Ordinal))
                   .All(static equal => equal);
    }

    private static bool TryTwoTypes(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeTypeValue left,
        out ComptimeTypeValue right,
        out string reason)
    {
        left = null!;
        right = null!;
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeTypeValue leftType ||
            arguments[1] is not ComptimeTypeValue rightType)
        {
            reason = "expected (Type, Type)";
            return false;
        }

        left = leftType;
        right = rightType;
        reason = string.Empty;
        return true;
    }

    private static bool TryGetAdtAndCase(
        SymbolId symbolId,
        MetaComptimeContext meta,
        out AdtDef root,
        out List<CaseTypeDef> casePath)
    {
        root = null!;
        casePath = [];
        if (meta.AdtDefinitions.TryGetValue(symbolId, out root!))
        {
            return true;
        }

        foreach (var candidate in meta.AdtDefinitions.Values)
        {
            var path = new List<CaseTypeDef>();
            if (TryFindCase(candidate.Cases, symbolId, path))
            {
                root = candidate;
                casePath = path;
                return true;
            }
        }

        return false;
    }

    private static bool TryFindCase(
        IReadOnlyList<CaseTypeDef> cases,
        SymbolId symbolId,
        List<CaseTypeDef> path)
    {
        foreach (var caseType in cases)
        {
            path.Add(caseType);
            if (caseType.SymbolId == symbolId || TryFindCase(caseType.Cases, symbolId, path))
            {
                return true;
            }
            path.RemoveAt(path.Count - 1);
        }

        return false;
    }

    private static bool TryHasField(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryTypeAndName(arguments, out var typeValue, out var fieldName, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = new ComptimeBoolValue(TryFindField(typeValue, fieldName, meta, out _, out _));
        return true;
    }

    private static bool TryFieldType(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryTypeAndName(arguments, out var typeValue, out var fieldName, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        if (!TryFindField(typeValue, fieldName, meta, out var field, out var owner))
        {
            return Fail($"type '{typeValue.TypeRef.Name}' has no unique field named '{fieldName}'", out value, out reason);
        }

        value = new ComptimeTypeValue(field.Type == null
            ? new MetaTypeRef(MetaTypeKind.Unknown, "Unknown", "unknown", SymbolId.None, TypeId.None, [])
            : CreateTypeRef(field.Type, meta.SymbolTable, owner));
        return true;
    }

    private static bool TryDeclarationInfo(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!TrySingle<ComptimeDeclValue>(arguments, out var declaration, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = CreateDeclInfo(declaration, meta);
        return true;
    }

    private static bool TryObjectProperty(
        IReadOnlyList<ComptimeValue> arguments,
        string expectedKind,
        string propertyName,
        out ComptimeValue value,
        out string reason)
    {
        if (!TrySingle<ComptimeMetaObjectValue>(arguments, out var objectValue, out reason) ||
            !string.Equals(objectValue.SchemaKind, expectedKind, StringComparison.Ordinal))
        {
            return Fail($"Meta accessor expects {expectedKind}", out value, out reason);
        }

        if (!objectValue.TryGet(propertyName, out value))
        {
            return Fail($"{expectedKind} does not expose '{propertyName}' for this type category", out value, out reason);
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryReportDiagnostic(
        IReadOnlyList<ComptimeValue> arguments,
        MetaDiagnosticLevel level,
        MetaComptimeContext meta,
        ComptimeResourceBudget budget,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeMetaObjectValue { SchemaKind: "span" } spanValue ||
            arguments[1] is not ComptimeStringValue message ||
            !TryReadSpan(spanValue, out var span))
        {
            return Fail($"meta.{level.ToString().ToLowerInvariant()} expects (meta.Span, String)", out value, out reason);
        }

        if (!budget.TryConsumeDiagnostic(out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        meta.ReportDiagnostic?.Invoke(level, span, message.Value);
        value = ComptimeUnitValue.Instance;
        reason = string.Empty;
        return true;
    }

    private static bool TryObject(
        string kind,
        IReadOnlyList<ComptimeValue> arguments,
        IReadOnlyList<string> propertyNames,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != propertyNames.Count)
        {
            return Fail($"Meta builder '{kind}' expects {propertyNames.Count} arguments, got {arguments.Count}", out value, out reason);
        }

        value = new ComptimeMetaObjectValue(
            kind,
            arguments.Select((argument, index) => new ComptimeNamedValue(propertyNames[index], argument)).ToArray());
        reason = string.Empty;
        return true;
    }

    private static bool TryHandleObject(
        string kind,
        SourceSpan span,
        MetaComptimeContext meta,
        IReadOnlyList<ComptimeValue> arguments,
        IReadOnlyList<string> propertyNames,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryObject(kind, arguments, propertyNames, out value, out reason) ||
            value is not ComptimeMetaObjectValue objectValue)
        {
            return false;
        }

        var identity = Hash(
            $"{meta.ExpansionTrace}|{kind}|{CreatePublicSourceUri(span, meta.SymbolTable)}|{span.Position}|{span.Length}|" +
            string.Join("|", arguments.Select(static argument => argument.CanonicalText)));
        value = objectValue with
        {
            Properties = [new ComptimeNamedValue("identity", new ComptimeStringValue(identity)), .. objectValue.Properties]
        };
        return true;
    }

    private static ComptimeAdtValue BuildTypeInfo(ComptimeTypeValue typeValue, MetaComptimeContext meta) =>
        BuildTypeShape(typeValue, meta);

    private static ComptimeMetaObjectValue CreateConstructorInfo(
        Constructor constructor,
        AdtDef owner,
        MetaComptimeContext meta)
    {
        var fields = new List<ComptimeValue>();
        for (var i = 0; i < constructor.PositionalArgs.Count; i++)
        {
            fields.Add(CreateFieldInfo($"${i}", constructor.PositionalArgs[i], SymbolId.None, constructor.Span, owner, meta));
        }

        foreach (var field in constructor.NamedArgs)
        {
            fields.Add(CreateFieldInfo(field.Name, field.Type, field.SymbolId, field.Span, owner, meta));
        }

        var constructorSymbol = meta.SymbolTable.GetSymbol(constructor.SymbolId);
        var declaration = constructorSymbol == null
            ? new ComptimeDeclValue(SymbolId.None, $"constructor:{owner.Name}:{constructor.Name}", constructor.Name, "constructor", constructor.Span)
            : CreateDeclValue(constructorSymbol, meta.SymbolTable);
        return TypedObject(
            "constructor-info",
            WellKnownStrings.Meta.Types.Constructor,
            WellKnownTypeIds.MetaConstructorId,
            [
            ("name", new ComptimeStringValue(constructor.Name)),
            ("decl", declaration),
            ("fields", List(fields)),
            ("span", CreateSpan(constructor.Span, meta.SymbolTable))
            ]);
    }

    private static ComptimeMetaObjectValue CreateFieldInfo(
        string name,
        TypeNode? type,
        SymbolId symbolId,
        SourceSpan span,
        AdtDef owner,
        MetaComptimeContext meta,
        ComptimeTypeValue? receiverType = null)
    {
        var fieldType = type == null
            ? new ComptimeTypeValue(new MetaTypeRef(MetaTypeKind.Unknown, "Unknown", "unknown", SymbolId.None, TypeId.None, []))
            : new ComptimeTypeValue(CreateTypeRef(type, meta.SymbolTable, owner));
        var fieldSymbol = symbolId.IsValid ? meta.SymbolTable.GetSymbol(symbolId) : null;
        var declaration = fieldSymbol == null
            ? new ComptimeDeclValue(SymbolId.None, $"field:{owner.Name}:{name}", name, "field", span)
            : CreateDeclValue(fieldSymbol, meta.SymbolTable);
        var declaringType = fieldSymbol is FieldSymbol { OwnerType.IsValid: true } declaredField &&
                            meta.SymbolTable.GetSymbol(declaredField.OwnerType) is { } declaringSymbol
            ? CreateTypeValue(declaringSymbol, meta.SymbolTable)
            : owner.SymbolId.IsValid && meta.SymbolTable.GetSymbol(owner.SymbolId) is { } ownerSymbol
                ? CreateTypeValue(ownerSymbol, meta.SymbolTable)
                : null;
        var inherited = receiverType?.TypeRef.SymbolId.IsValid == true &&
                        declaringType?.TypeRef.SymbolId.IsValid == true &&
                        receiverType.TypeRef.SymbolId != declaringType.TypeRef.SymbolId;
        return TypedObject(
            "field-info",
            WellKnownStrings.Meta.Types.Field,
            WellKnownTypeIds.MetaFieldId,
            [
            ("name", new ComptimeStringValue(name)),
            ("type", fieldType),
            ("decl", declaration),
            ("declaringType", declaringType is null ? ComptimeUnitValue.Instance : declaringType),
            ("receiverType", receiverType is not null
                ? receiverType
                : declaringType is not null ? declaringType : ComptimeUnitValue.Instance),
            ("inherited", new ComptimeBoolValue(inherited)),
            ("span", CreateSpan(span, meta.SymbolTable))
            ]);
    }

    private static bool TryFindField(
        ComptimeTypeValue typeValue,
        string fieldName,
        MetaComptimeContext meta,
        out Field field,
        out AdtDef owner)
    {
        field = null!;
        owner = null!;
        if (!typeValue.TypeRef.SymbolId.IsValid ||
            !TryGetAdtAndCase(typeValue.TypeRef.SymbolId, meta, out var resolvedOwner, out var casePath))
        {
            return false;
        }

        owner = resolvedOwner;

        var effectiveFields = new List<Field>(owner.Fields);
        foreach (var caseType in casePath)
        {
            effectiveFields.AddRange(caseType.Fields);
        }
        if (casePath.Count == 0 && effectiveFields.Count == 0)
        {
            effectiveFields.AddRange(owner.Constructors.SelectMany(static constructor => constructor.NamedArgs));
        }

        var matches = effectiveFields
            .Where(candidate => string.Equals(candidate.Name, fieldName, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        field = matches[0];
        return true;
    }

    private static ComptimeSequenceValue CreateClauseList(
        IReadOnlyList<ClauseIR> clauses,
        MetaComptimeContext meta)
    {
        return List(clauses.Select(clause => (ComptimeValue)CreateClauseValue(clause, meta)));
    }

    private static ComptimeMetaObjectValue CreateClauseValue(ClauseIR clause, MetaComptimeContext meta)
    {
        var arguments = List(clause.Arguments.Select(argument =>
            (ComptimeValue)CreateClauseArgumentValue(clause.OccurrenceId, argument)));
        return Obj(
            "clause",
            ("schemaVersion", new ComptimeStringValue(clause.SchemaVersion)),
            ("occurrenceIdentity", new ComptimeStringValue(clause.OccurrenceId.ToString())),
            ("keyword", new ComptimeStringValue(clause.Keyword)),
            ("kind", new ComptimeStringValue(ToCanonicalEnumName(clause.Kind))),
            ("stage", CreateStageValue(clause.Stage, meta.SymbolTable)),
            ("sourceOrder", new ComptimeIntegerValue(clause.SourceOrder)),
            ("sourceOrderBehavior", new ComptimeStringValue(ToCanonicalEnumName(clause.SourceOrderBehavior))),
            ("arguments", arguments),
            ("span", CreateSpan(clause.Span, meta.SymbolTable)),
            ("hasCompilerOwnedSourceGrant", new ComptimeBoolValue(clause.HasCompilerOwnedSourceGrant))) with
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Clause,
                WellKnownTypeIds.MetaClauseId)
        };
    }

    private static ComptimeMetaObjectValue CreateClauseArgumentValue(
        ClauseOccurrenceId clauseOccurrence,
        ClauseArgumentIR argument)
    {
        var occurrence = clauseOccurrence with { ArgumentSubIndex = argument.Index };
        return Obj(
            "clause-argument",
            ("index", new ComptimeIntegerValue(argument.Index)),
            ("type", new ComptimeStringValue(ToCanonicalEnumName(argument.Type))),
            ("canonicalText", new ComptimeStringValue(argument.CanonicalText)),
            ("path", List(argument.Path.Select(static segment => (ComptimeValue)new ComptimeStringValue(segment)))),
            ("occurrenceIdentity", new ComptimeStringValue(occurrence.ToString()))) with
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.ClauseArgument,
                WellKnownTypeIds.MetaClauseArgumentId)
        };
    }

    private static ComptimeValue CreateStageValue(ClauseStage stage, SymbolTable symbolTable)
    {
        var stageName = stage.ToString();
        var stageSymbol = symbolTable.Symbols.Values
            .OfType<AdtSymbol>()
            .FirstOrDefault(symbol => symbol.TypeId.Value == WellKnownTypeIds.MetaStageId);
        var constructor = stageSymbol?.DirectCases
            .Select(symbolTable.GetSymbol<AdtSymbol>)
            .FirstOrDefault(symbol => symbol != null && string.Equals(symbol.Name, stageName, StringComparison.Ordinal));
        return new ComptimeAdtValue(
            constructor?.Id ?? SymbolId.None,
            stageName,
            [],
            [])
        {
            ConstructorIdentity = constructor == null
                ? $"meta:Stage.{stageName}"
                : CreateStableIdentity(constructor, symbolTable),
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Stage,
                WellKnownTypeIds.MetaStageId)
        };
    }

    private static string ToCanonicalEnumName<T>(T value)
        where T : struct, Enum
    {
        var name = value.ToString();
        var builder = new StringBuilder(name.Length + 4);
        for (var index = 0; index < name.Length; index++)
        {
            var current = name[index];
            if (index > 0 && char.IsUpper(current))
            {
                builder.Append('_');
            }
            builder.Append(char.ToLowerInvariant(current));
        }
        return builder.ToString();
    }

    private static bool TryTypeAndName(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeTypeValue typeValue,
        out string name,
        out string reason)
    {
        typeValue = null!;
        name = string.Empty;
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeTypeValue reflectedType ||
            arguments[1] is not ComptimeStringValue stringValue)
        {
            reason = "expected (Type, String)";
            return false;
        }

        typeValue = reflectedType;
        name = stringValue.Value;
        reason = string.Empty;
        return true;
    }

    internal static ComptimeTypeValue CreateTypeValue(Symbol symbol, SymbolTable symbolTable)
    {
        var kind = symbol switch
        {
            TypeParamSymbol => MetaTypeKind.TypeParameter,
            TraitSymbol => MetaTypeKind.Trait,
            AdtSymbol { Name: "Ref" } => MetaTypeKind.Reference,
            AdtSymbol { Name: "MRef" or "MutRef" } => MetaTypeKind.MutableReference,
            AdtSymbol { Name: "Shared" } => MetaTypeKind.SharedReference,
            AdtSymbol { Name: "RawPtr" or "Ptr" } => MetaTypeKind.RawPointer,
            AdtSymbol { Name: "Cfn" } => MetaTypeKind.ForeignFunction,
            AdtSymbol { IsTypeAlias: true } => MetaTypeKind.Alias,
            AdtSymbol when BaseTypes.IsBuiltIn(symbol.TypeId) => MetaTypeKind.Primitive,
            AdtSymbol { IsCaseType: true } => MetaTypeKind.Case,
            AdtSymbol { IsClosedSum: true } => MetaTypeKind.ClosedSum,
            AdtSymbol { IsCStruct: true } => MetaTypeKind.ForeignNominal,
            AdtSymbol => MetaTypeKind.Nominal,
            EffectSymbol => MetaTypeKind.Effect,
            _ => MetaTypeKind.Nominal
        };
        var parameters = symbol is AdtSymbol adt && adt.TypeParams.Count > 0
            ? adt.TypeParams
                .Select(id => symbolTable.GetSymbol<TypeParamSymbol>(id))
                .Where(static parameter => parameter != null)
                .Select(static parameter => parameter!)
                .ToArray()
            : [];
        var arguments = parameters
            .Where(static parameter => parameter.ParameterKind == GenericParameterKind.Type)
            .Select(parameter => CreateTypeParameterRef(parameter, symbolTable))
            .ToArray();
        var genericArguments = parameters
            .Select(parameter => CreateGenericParameterRef(parameter, symbolTable))
            .ToArray();
        return new ComptimeTypeValue(new MetaTypeRef(
            kind,
            symbol.Name,
            CreateStableIdentity(symbol, symbolTable),
            symbol.Id,
            symbol.TypeId,
            arguments,
            GenericArguments: genericArguments))
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Type,
                WellKnownTypeIds.MetaTypeId)
        };
    }

    internal static ComptimeTypeValue CreateTypeValue(TyCon type, SymbolTable symbolTable)
    {
        var symbol = type.Symbol.IsValid
            ? symbolTable.GetSymbol(type.Symbol)
            : type.Id.IsValid
                ? symbolTable.GetSymbolByTypeId(type.Id)
                : null;
        if (symbol == null)
        {
            var unresolvedTypeArguments = type.Args
                .Select(argument => CreateSemanticTypeRef(argument, symbolTable))
                .ToArray();
            var unresolvedGenericArguments = new List<MetaGenericArgumentRef>(
                type.Args.Count + type.ValueArgs.Count + type.EffectArgs.Count);
            var valueArguments = type.ValueArgs.ToDictionary(static argument => argument.ParameterIndex);
            var effectArguments = type.EffectArgs.ToDictionary(static argument => argument.ParameterIndex);
            var totalArguments = type.Args.Count + type.ValueArgs.Count + type.EffectArgs.Count;
            var unresolvedTypeArgumentIndex = 0;
            for (var parameterIndex = 0; parameterIndex < totalArguments; parameterIndex++)
            {
                if (valueArguments.TryGetValue(parameterIndex, out var valueArgument))
                {
                    unresolvedGenericArguments.Add(new MetaGenericArgumentRef(
                        MetaGenericArgumentDomain.Value,
                        valueArgument.DisplayText,
                        valueArgument.CanonicalHash,
                        SymbolId.None,
                        null));
                }
                else if (effectArguments.TryGetValue(parameterIndex, out var effectArgument))
                {
                    var effectRef = CreateSemanticTypeRef(effectArgument.Argument, symbolTable);
                    unresolvedGenericArguments.Add(new MetaGenericArgumentRef(
                        MetaGenericArgumentDomain.EffectRow,
                        effectRef.Name,
                        effectRef.StableIdentity,
                        SymbolId.None,
                        effectRef));
                }
                else if (unresolvedTypeArgumentIndex < unresolvedTypeArguments.Length)
                {
                    var argument = unresolvedTypeArguments[unresolvedTypeArgumentIndex++];
                    unresolvedGenericArguments.Add(new MetaGenericArgumentRef(
                        MetaGenericArgumentDomain.Type,
                        argument.Name,
                        argument.StableIdentity,
                        argument.SymbolId,
                        argument));
                }
            }

            var kind = type.ConstructorVarIndex.HasValue
                ? MetaTypeKind.HigherKinded
                : BaseTypes.IsBuiltIn(type.Id)
                    ? MetaTypeKind.Primitive
                    : type.Name switch
                    {
                        "Ref" => MetaTypeKind.Reference,
                        "MRef" or "MutRef" => MetaTypeKind.MutableReference,
                        "Shared" => MetaTypeKind.SharedReference,
                        "RawPtr" or "Ptr" => MetaTypeKind.RawPointer,
                        "Cfn" => MetaTypeKind.ForeignFunction,
                        _ => MetaTypeKind.Nominal
                    };
            var headIdentity = type.ConstructorVarIndex.HasValue
                ? $"higher-kinded-parameter:{type.ConstructorVarIndex.Value}"
                : $"{kind.ToToken()}:{type.Name}";
            var unresolvedStableIdentity = unresolvedGenericArguments.Count == 0
                ? headIdentity
                : $"{headIdentity}[{string.Join(";", unresolvedGenericArguments.Select(static argument => argument.CanonicalText))}]";
            return new ComptimeTypeValue(new MetaTypeRef(
                kind,
                type.ToString(),
                unresolvedStableIdentity,
                SymbolId.None,
                type.Id,
                unresolvedTypeArguments,
                GenericArguments: unresolvedGenericArguments));
        }

        var parameterIds = symbol is AdtSymbol adt
            ? adt.IsCaseType
                ? symbolTable.GetClosedCaseEffectiveGenericParameterIds(adt.Id)
                : adt.TypeParams
            : [];
        var typeArgumentIndex = 0;
        var genericArguments = new List<MetaGenericArgumentRef>(parameterIds.Count);
        foreach (var (parameterId, parameterIndex) in parameterIds.Select(static (id, index) => (id, index)))
        {
            if (symbolTable.GetSymbol<TypeParamSymbol>(parameterId) is not { } parameter)
            {
                continue;
            }

            switch (parameter.ParameterKind)
            {
                case GenericParameterKind.Type when typeArgumentIndex < type.Args.Count:
                {
                    var argument = CreateSemanticTypeRef(type.Args[typeArgumentIndex++], symbolTable);
                    genericArguments.Add(new MetaGenericArgumentRef(
                        MetaGenericArgumentDomain.Type,
                        argument.Name,
                        argument.StableIdentity,
                        argument.SymbolId,
                        argument));
                    break;
                }
                case GenericParameterKind.Value:
                {
                    var argument = type.ValueArgs.FirstOrDefault(candidate => candidate.ParameterIndex == parameterIndex);
                    if (argument != null)
                    {
                        genericArguments.Add(new MetaGenericArgumentRef(
                            MetaGenericArgumentDomain.Value,
                            argument.DisplayText,
                            argument.CanonicalHash,
                            SymbolId.None,
                            null));
                    }
                    break;
                }
                case GenericParameterKind.EffectRow:
                {
                    var argument = type.EffectArgs.FirstOrDefault(candidate => candidate.ParameterIndex == parameterIndex);
                    if (argument != null)
                    {
                        var effectType = CreateSemanticTypeRef(argument.Argument, symbolTable);
                        genericArguments.Add(new MetaGenericArgumentRef(
                            MetaGenericArgumentDomain.EffectRow,
                            effectType.Name,
                            effectType.StableIdentity,
                            effectType.SymbolId,
                            effectType));
                    }
                    break;
                }
            }
        }

        while (typeArgumentIndex < type.Args.Count)
        {
            var argument = CreateSemanticTypeRef(type.Args[typeArgumentIndex++], symbolTable);
            genericArguments.Add(new MetaGenericArgumentRef(
                MetaGenericArgumentDomain.Type,
                argument.Name,
                argument.StableIdentity,
                argument.SymbolId,
                argument));
        }

        var typeArguments = genericArguments
            .Where(static argument => argument.Domain == MetaGenericArgumentDomain.Type && argument.Type != null)
            .Select(static argument => argument.Type!)
            .ToArray();
        var baseIdentity = CreateStableIdentity(symbol, symbolTable);
        var stableIdentity = genericArguments.Count == 0
            ? baseIdentity
            : $"{baseIdentity}[{string.Join(";", genericArguments.Select(static argument => argument.CanonicalText))}]";
        return new ComptimeTypeValue(new MetaTypeRef(
            symbol switch
            {
                AdtSymbol { Name: "Ref" } => MetaTypeKind.Reference,
                AdtSymbol { Name: "MRef" or "MutRef" } => MetaTypeKind.MutableReference,
                AdtSymbol { Name: "Shared" } => MetaTypeKind.SharedReference,
                AdtSymbol { Name: "RawPtr" or "Ptr" } => MetaTypeKind.RawPointer,
                AdtSymbol { Name: "Cfn" } => MetaTypeKind.ForeignFunction,
                AdtSymbol { IsTypeAlias: true } => MetaTypeKind.Alias,
                AdtSymbol when BaseTypes.IsBuiltIn(symbol.TypeId) => MetaTypeKind.Primitive,
                AdtSymbol { IsCaseType: true } => MetaTypeKind.Case,
                AdtSymbol { IsClosedSum: true } => MetaTypeKind.ClosedSum,
                AdtSymbol { IsCStruct: true } => MetaTypeKind.ForeignNominal,
                AdtSymbol => MetaTypeKind.Nominal,
                TraitSymbol => MetaTypeKind.Trait,
                EffectSymbol => MetaTypeKind.Effect,
                TypeParamSymbol => MetaTypeKind.TypeParameter,
                _ => MetaTypeKind.Nominal
            },
            type.ToString(),
            stableIdentity,
            symbol.Id,
            symbol.TypeId,
            typeArguments,
            GenericArguments: genericArguments));
    }

    private static MetaTypeRef CreateSemanticTypeRef(Type type, SymbolTable symbolTable)
    {
        return type switch
        {
            TyCon constructor => CreateTypeValue(constructor, symbolTable).TypeRef,
            TyTuple tuple => new MetaTypeRef(
                MetaTypeKind.Tuple,
                tuple.ToString(),
                $"tuple:{string.Join(",", tuple.Elements.Select(element => CreateSemanticTypeRef(element, symbolTable).StableIdentity))}",
                SymbolId.None,
                tuple.Id,
                tuple.Elements.Select(element => CreateSemanticTypeRef(element, symbolTable)).ToArray()),
            TyFun function => CreateFunctionTypeRef(function, symbolTable),
            TyRef reference => CreateReferenceTypeRef(MetaTypeKind.Reference, "ref", reference.Inner, reference.Id, symbolTable),
            TyMutRef reference => CreateReferenceTypeRef(MetaTypeKind.MutableReference, "mref", reference.Inner, reference.Id, symbolTable),
            TyShared shared => CreateReferenceTypeRef(MetaTypeKind.SharedReference, "shared", shared.Inner, shared.Id, symbolTable),
            TyVar { Instance: { } instance } => CreateSemanticTypeRef(instance, symbolTable),
            TyVar variable => new MetaTypeRef(
                variable.IsErrorRecovery ? MetaTypeKind.Error : MetaTypeKind.TypeParameter,
                variable.IsErrorRecovery ? "<error>" : variable.ToString(),
                variable.IsErrorRecovery ? "error:recovery" : $"type-parameter:{variable.Index}",
                SymbolId.None,
                variable.Id,
                []),
            EffectTag effect => CreateEffectTagRef(effect, symbolTable),
            EffectRow effects => CreateEffectRowRef(effects, symbolTable),
            RequestType request => CreateRequestTypeRef(request, symbolTable),
            TyReflProof proof => CreateProofTypeRef(proof, symbolTable),
            _ => throw new InvalidOperationException($"Meta schema {WellKnownStrings.Meta.SchemaVersion} has no semantic type mapping for the supplied compiler type.")
        };

        static MetaTypeRef CreateReferenceTypeRef(
            MetaTypeKind kind,
            string spelling,
            Type inner,
            TypeId id,
            SymbolTable table)
        {
            var referent = CreateSemanticTypeRef(inner, table);
            return new MetaTypeRef(
                kind,
                $"{spelling}[{referent.Name}]",
                $"{spelling}:{referent.StableIdentity}",
                SymbolId.None,
                id,
                [referent]);
        }

        static MetaTypeRef CreateFunctionTypeRef(TyFun function, SymbolTable table)
        {
            var parameters = function.Params.Select(parameter => CreateSemanticTypeRef(parameter, table)).ToArray();
            var result = CreateSemanticTypeRef(function.Result, table);
            var effects = CreateEffectRowRef(function.Effects, table);
            return new MetaTypeRef(
                MetaTypeKind.Function,
                function.ToString(),
                $"function:{string.Join(";", parameters.Select(static parameter => parameter.CanonicalText))}" +
                $"->{result.CanonicalText}:{effects.CanonicalText}",
                SymbolId.None,
                function.Id,
                [.. parameters, result],
                GenericArguments:
                [
                    new MetaGenericArgumentRef(
                        MetaGenericArgumentDomain.EffectRow,
                        effects.Name,
                        effects.StableIdentity,
                        SymbolId.None,
                        effects)
                ]);
        }

        static MetaTypeRef CreateEffectTagRef(EffectTag effect, SymbolTable table)
        {
            var arguments = effect.TypeArgs.Select(argument => CreateSemanticTypeRef(argument, table)).ToArray();
            var symbol = effect.Symbol.IsValid ? table.GetSymbol(effect.Symbol) : null;
            var identity = symbol == null
                ? $"effect:{effect.Name}"
                : CreateStableIdentity(symbol, table);
            if (arguments.Length > 0)
            {
                identity += $"[{string.Join(";", arguments.Select(static argument => argument.CanonicalText))}]";
            }
            return new MetaTypeRef(MetaTypeKind.Effect, effect.ToString(), identity, effect.Symbol, effect.Id, arguments);
        }

        static MetaTypeRef CreateEffectRowRef(EffectRow effects, SymbolTable table)
        {
            var arguments = effects.Effects
                .Select(effect => CreateEffectTagRef(effect, table))
                .OrderBy(static effect => effect.StableIdentity, StringComparer.Ordinal)
                .ToArray();
            var variables = effects.Variables
                .Select(static variable => variable.Name)
                .Order(StringComparer.Ordinal)
                .ToArray();
            var identity = $"effect-row:effects[{string.Join(";", arguments.Select(static argument => argument.CanonicalText))}]" +
                           $":variables[{string.Join(";", variables.Select(ComptimeValue.EncodeText))}]";
            return new MetaTypeRef(MetaTypeKind.EffectRow, effects.ToString(), identity, SymbolId.None, effects.Id, arguments);
        }

        static MetaTypeRef CreateRequestTypeRef(RequestType request, SymbolTable table)
        {
            var arguments = new List<MetaTypeRef>
            {
                CreateSemanticTypeRef(request.Effect, table),
                CreateSemanticTypeRef(request.Result, table)
            };
            if (request.Payload != null)
            {
                arguments.Add(CreateSemanticTypeRef(request.Payload, table));
            }
            if (request.ResumeArg != null)
            {
                arguments.Add(CreateSemanticTypeRef(request.ResumeArg, table));
            }
            return new MetaTypeRef(
                MetaTypeKind.EffectRequest,
                request.ToString(),
                $"effect-request:[{string.Join(";", arguments.Select(static argument => argument.CanonicalText))}]",
                SymbolId.None,
                request.Id,
                arguments);
        }

        static MetaTypeRef CreateProofTypeRef(TyReflProof proof, SymbolTable table)
        {
            var witness = proof.WitnessType == null ? null : CreateSemanticTypeRef(proof.WitnessType, table);
            return new MetaTypeRef(
                MetaTypeKind.Proof,
                WellKnownStrings.Keywords.ReflConstructor,
                witness == null ? "proof:refl" : $"proof:refl:{witness.CanonicalText}",
                SymbolId.None,
                proof.Id,
                witness == null ? [] : [witness]);
        }
    }

    internal static ComptimeTypeValue CreateTypeTargetValue(
        Declaration target,
        SymbolTable symbolTable,
        IReadOnlyList<string>? targetPath = null)
    {
        var (name, typeParams, symbolId, span, kind, isTypeTarget) = target switch
        {
            AdtDef adt => (adt.Name, adt.TypeParams, adt.SymbolId, adt.Span, MetaTypeKind.TargetAdt, true),
            CaseTypeDef caseType => (caseType.Name, caseType.TypeParams, caseType.SymbolId, caseType.Span, MetaTypeKind.TargetCaseType, true),
            FuncDef function => (function.Name, function.TypeParams, function.SymbolId, function.Span, MetaTypeKind.TargetFunction, false),
            FuncDecl function => (function.Name, function.TypeParams, function.SymbolId, function.Span, MetaTypeKind.TargetFunction, false),
            TraitDef trait => (trait.Name, trait.TypeParams, trait.SymbolId, trait.Span, MetaTypeKind.TargetTrait, false),
            InstanceDecl instance => (instance.Name, instance.TypeParams, instance.SymbolId, instance.Span, MetaTypeKind.TargetInstance, false),
            EffectDef effect => (effect.Name, new List<TypeParam>(), effect.SymbolId, effect.Span, MetaTypeKind.TargetEffect, false),
            ModuleDecl module => (string.Join(WellKnownStrings.Separators.Path, module.Path), new List<TypeParam>(), module.SymbolId, module.Span, MetaTypeKind.TargetModule, false),
            LetDecl { Pattern: VarPattern variable } binding => (variable.Name, new List<TypeParam>(), binding.SymbolId, binding.Span, MetaTypeKind.TargetValue, false),
            ProofDecl proof => (proof.Name, proof.TypeParams, proof.SymbolId, proof.Span, MetaTypeKind.TargetProof, false),
            OperatorDecl op => (op.OperatorSymbol, new List<TypeParam>(), op.SymbolId, op.Span, MetaTypeKind.TargetOperator, false),
            ImportDecl import => (string.Join(WellKnownStrings.Separators.Path, import.ToQualifiedModulePath()), new List<TypeParam>(), import.SymbolId, import.Span, MetaTypeKind.TargetImport, false),
            LetQuestionDecl => ("let?", new List<TypeParam>(), target.SymbolId, target.Span, MetaTypeKind.TargetBinding, false),
            Assignment assignment => (assignment.Target, new List<TypeParam>(), assignment.SymbolId, assignment.Span, MetaTypeKind.TargetAssignment, false),
            _ => ("declaration", new List<TypeParam>(), target.SymbolId, target.Span, MetaTypeKind.TargetDeclaration, false)
        };
        var arguments = typeParams
            .Where(static parameter => parameter.ParameterKind == GenericParameterKind.Type)
            .Select(parameter => CreateTypeParameterRef(parameter, symbolTable))
            .ToArray();
        var genericArguments = typeParams
            .Select(parameter => CreateGenericParameterRef(parameter, symbolTable))
            .ToArray();
        TypePath? syntax = null;
        if (isTypeTarget)
        {
            IReadOnlyList<string> path = targetPath is { Count: > 0 } ? targetPath : [name];
            syntax = CreateTypePath(path[^1], symbolId, span);
            syntax.ModulePath = path.Take(path.Count - 1).ToList();
        }
        var syntaxArguments = new List<GenericArgumentNode>(typeParams.Count);
        foreach (var parameter in typeParams)
        {
            switch (parameter.ParameterKind)
            {
                case GenericParameterKind.Type:
                    syntaxArguments.Add(new TypeGenericArgumentNode
                    {
                        Type = CreateTypePath(parameter.Name, parameter.SymbolId, parameter.Span),
                        Span = parameter.Span
                    });
                    break;
                case GenericParameterKind.Value:
                    var valueReference = new IdentifierExpr();
                    valueReference.SetSpan(parameter.Span);
                    valueReference.SetName(parameter.Name);
                    valueReference.SymbolId = parameter.SymbolId;
                    syntaxArguments.Add(new ValueGenericArgumentNode
                    {
                        Expression = valueReference,
                        Span = parameter.Span
                    });
                    break;
                case GenericParameterKind.EffectRow:
                    syntaxArguments.Add(new EffectGenericArgumentNode
                    {
                        EffectRow = CreateTypePath(parameter.Name, parameter.SymbolId, parameter.Span),
                        Span = parameter.Span
                    });
                    break;
            }
        }
        syntax?.SetGenericArguments(syntaxArguments);

        var symbolValue = symbolTable.GetSymbol(symbolId);
        return new ComptimeTypeValue(new MetaTypeRef(
            kind,
            name,
            symbolValue == null ? $"{kind.ToToken()}:{name}" : CreateStableIdentity(symbolValue, symbolTable),
            symbolId,
            symbolValue?.TypeId ?? TypeId.None,
            arguments,
            syntax,
            genericArguments));
    }

    internal static ComptimeDeclValue CreateDeclValue(Symbol symbol, SymbolTable symbolTable) => new(
        symbol.Id,
        CreateStableIdentity(symbol, symbolTable),
        symbol.Name,
        symbol.Kind.ToString().ToLowerInvariant(),
        symbol.Span)
    {
        StaticType = GetDeclarationHandleType(symbol)
    };

    internal static ComptimeMetaObjectValue CreateFunctionHandle(
        FuncSymbol function,
        FuncDef? definition,
        SymbolTable symbolTable,
        MetaComptimeContext meta)
    {
        var identity = CreateStableIdentity(function, symbolTable);
        var signature = definition?.Signature.Count == 1
            ? CreateTypeRef(definition.Signature[0], symbolTable)
            : new MetaTypeRef(
                MetaTypeKind.Function,
                function.Name,
                $"function:{identity}",
                SymbolId.None,
                TypeId.None,
                []);
        var signatureParts = signature.Kind == MetaTypeKind.Function && signature.Arguments.Count > 0
            ? (signature.Arguments.Count == 1
                ? (Parameters: Array.Empty<MetaTypeRef>(), Result: signature.Arguments[0])
                : (Parameters: signature.Arguments.Take(signature.Arguments.Count - 1).ToArray(), Result: signature.Arguments[^1]))
            : (Parameters: Array.Empty<MetaTypeRef>(), Result: new MetaTypeRef(
                MetaTypeKind.Unknown,
                "Unknown",
                "unknown",
                SymbolId.None,
                TypeId.None,
                []));
        var parameters = signatureParts.Parameters
            .Select((type, index) => CreateFunctionParameterHandle(
                function,
                type,
                index,
                identity,
                symbolTable,
                meta))
            .ToArray();
        var ownership = signatureParts.Parameters
            .Select((type, index) => CreateOwnershipProjection(type, "parameter", index, meta))
            .Concat([CreateOwnershipProjection(signatureParts.Result, "result", -1, meta)])
            .ToArray();
        ComptimeValue body = definition is { Body.Count: > 0 } &&
                   meta.Access.AvailableStage >= ClauseStage.Body
            ? CreateBodyHandle(definition, CreateDeclValue(function, symbolTable), meta)
            : ComptimeUnitValue.Instance;
        var effects = CreateFunctionEffects(function, signature);
        return TypedObject(
            "function-handle",
            WellKnownStrings.Meta.Types.Function,
            WellKnownTypeIds.MetaFunctionId,
            [
                ("identity", new ComptimeStringValue(identity)),
                ("name", new ComptimeStringValue(function.Name)),
                ("declaration", CreateDeclValue(function, symbolTable)),
                ("span", CreateSpan(function.Span, symbolTable)),
                ("type", new ComptimeTypeValue(signature)),
                ("parameters", List(parameters)),
                ("result", new ComptimeTypeValue(signatureParts.Result)),
                ("effects", List(effects)),
                ("ownership", List(ownership)),
                ("body", body)
            ]);
    }

    private static ComptimeMetaObjectValue CreateFunctionParameterHandle(
        FuncSymbol function,
        MetaTypeRef type,
        int ordinal,
        string functionIdentity,
        SymbolTable symbolTable,
        MetaComptimeContext meta)
    {
        var parameterSymbol = ordinal < function.Parameters.Count
            ? symbolTable.GetSymbol(function.Parameters[ordinal])
            : null;
        var declaration = parameterSymbol == null
            ? new ComptimeDeclValue(
                SymbolId.None,
                Hash($"{functionIdentity}|parameter|{ordinal}"),
                $"arg{ordinal}",
                "parameter",
                function.Span)
            : CreateDeclValue(parameterSymbol, symbolTable);
        return TypedObject(
            "parameter",
            WellKnownStrings.Meta.Types.Parameter,
            WellKnownTypeIds.MetaParameterId,
            [
                ("identity", new ComptimeStringValue(Hash($"{functionIdentity}|parameter|{ordinal}|{type.CanonicalText}"))),
                ("name", new ComptimeStringValue(parameterSymbol?.Name ?? $"arg{ordinal}")),
                ("ordinal", new ComptimeIntegerValue(ordinal)),
                ("type", new ComptimeTypeValue(type)),
                ("declaration", declaration),
                ("ownership", CreateOwnershipProjection(type, "parameter", ordinal, meta)),
                ("span", CreateSpan(parameterSymbol?.Span ?? function.Span, symbolTable))
            ]);
    }

    private static IReadOnlyList<ComptimeValue> CreateFunctionEffects(
        FuncSymbol function,
        MetaTypeRef signature)
    {
        var effectNames = (signature.GenericArguments ?? [])
            .Where(static argument => argument.Domain == MetaGenericArgumentDomain.EffectRow)
            .Select(static argument => argument.Display)
            .Concat(function.EffectSummary?.InferredEffects.Effects.Select(static effect => effect.ToString()) ?? [])
            .Order(StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal)
            .Select(static effect => (ComptimeValue)new ComptimeStringValue(effect))
            .ToArray();
        return effectNames;
    }

    private static Type GetDeclarationHandleType(Symbol symbol) => symbol switch
    {
        AdtSymbol { IsCaseType: true } => MetaSchemaRegistry.MetaType(
            WellKnownStrings.Meta.Types.CaseType,
            WellKnownTypeIds.MetaCaseTypeId),
        FieldSymbol => MetaSchemaRegistry.MetaType(
            WellKnownStrings.Meta.Types.Field,
            WellKnownTypeIds.MetaFieldId),
        CtorSymbol => MetaSchemaRegistry.MetaType(
            WellKnownStrings.Meta.Types.Constructor,
            WellKnownTypeIds.MetaConstructorId),
        VarSymbol { IsParameter: true } => MetaSchemaRegistry.MetaType(
            WellKnownStrings.Meta.Types.Parameter,
            WellKnownTypeIds.MetaParameterId),
        ImplSymbol => MetaSchemaRegistry.MetaType(
            WellKnownStrings.Meta.Types.Implementation,
            WellKnownTypeIds.MetaImplementationId),
        ModuleSymbol => MetaSchemaRegistry.MetaType(
            WellKnownStrings.Meta.Types.Module,
            WellKnownTypeIds.MetaModuleId),
        _ => MetaSchemaRegistry.MetaType(
            WellKnownStrings.Meta.Types.Declaration,
            WellKnownTypeIds.MetaDeclarationId)
    };

    internal static ComptimeMetaObjectValue CreateTarget(
        Declaration target,
        Symbol generator,
        SourceSpan span,
        ClauseOccurrenceId occurrenceId,
        ClauseStage stage,
        SymbolTable symbolTable,
        IReadOnlyList<string>? targetPath = null)
    {
        var targetSymbol = symbolTable.GetSymbol(target.SymbolId)!;
        return Obj(
            "target",
            ("target", CreateTypeTargetValue(target, symbolTable, targetPath)),
            ("targetDecl", CreateDeclValue(targetSymbol, symbolTable)),
            ("generator", CreateDeclValue(generator, symbolTable)),
            ("stage", CreateStageValue(stage, symbolTable)),
            ("category", new ComptimeStringValue(GetTargetCategory(target))),
            ("span", CreateSpan(span, symbolTable)),
            ("occurrence", new ComptimeIntegerValue(occurrenceId.ClauseIndex)),
            ("occurrenceSubIndex", new ComptimeIntegerValue(occurrenceId.ArgumentSubIndex)),
            ("occurrenceIdentity", new ComptimeStringValue(occurrenceId.ToString())),
            ("schemaVersion", new ComptimeIntegerValue(WellKnownStrings.Meta.SchemaVersion))) with
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Target,
                WellKnownTypeIds.MetaTargetId)
        };
    }

    private static string GetTargetCategory(Declaration declaration) => declaration switch
    {
        AdtDef => "item.type",
        CaseTypeDef => "member.case-type",
        FuncDef or FuncDecl => "item.function",
        TraitDef => "item.trait",
        InstanceDecl => "item.instance",
        EffectDef => "item.effect",
        ModuleDecl => "item.module",
        LetDecl => "item.value",
        ImportDecl => "item.import",
        _ => "item.declaration"
    };

    internal static string CreateStableIdentity(Symbol symbol, SymbolTable symbolTable)
    {
        var owner = symbolTable.Modules.TryGetOwningModule(symbol.Id, out var module)
            ? module.Identity.ToIdentityKey()
            : "<builtin>";
        return $"{owner}::{symbol.Kind}:{symbol.Name}@{symbol.Span.Position}:{symbol.Span.Length}";
    }

    internal static MetaTypeRef CreateTypeRef(TypeNode type, SymbolTable symbolTable, AdtDef? owner = null)
    {
        return type switch
        {
            TypePath path => CreateTypePathRef(path, symbolTable),
            TupleType tuple => new MetaTypeRef(
                MetaTypeKind.Tuple,
                tuple.ToString(),
                $"tuple:{string.Join(",", tuple.Elements.Select(element => CreateTypeRef(element, symbolTable, owner).StableIdentity))}",
                SymbolId.None,
                TypeId.None,
                tuple.Elements.Select(element => CreateTypeRef(element, symbolTable, owner)).ToArray(),
                tuple),
            ArrowType arrow => new MetaTypeRef(
                MetaTypeKind.Function,
                arrow.ToString(),
                $"function:{CreateTypeRef(arrow.ParamType, symbolTable, owner).StableIdentity}->{CreateTypeRef(arrow.ReturnType, symbolTable, owner).StableIdentity}",
                SymbolId.None,
                TypeId.None,
                [CreateTypeRef(arrow.ParamType, symbolTable, owner), CreateTypeRef(arrow.ReturnType, symbolTable, owner)],
                arrow),
            EffectfulType effectful when effectful.OutputType != null => CreateEffectfulTypeRef(effectful, symbolTable, owner),
            AssociatedTypeProjection projection => CreateAssociatedProjectionRef(projection, symbolTable, owner),
            WildcardType wildcard => new MetaTypeRef(MetaTypeKind.Error, "_", "error:wildcard-type", SymbolId.None, TypeId.None, [], wildcard),
            _ => throw new InvalidOperationException($"Meta schema {WellKnownStrings.Meta.SchemaVersion} has no syntax type mapping for the supplied type node.")
        };

        static MetaTypeRef CreateEffectfulTypeRef(EffectfulType effectful, SymbolTable table, AdtDef? declarationOwner)
        {
            var input = CreateTypeRef(effectful.InputType, table, declarationOwner);
            var output = CreateTypeRef(effectful.OutputType!, table, declarationOwner);
            var effectNames = effectful.EnumerateEffectPaths()
                .Select(path => string.Join(WellKnownStrings.Separators.Path, path))
                .Order(StringComparer.Ordinal)
                .ToArray();
            var effectArguments = effectNames
                .Select(name => new MetaGenericArgumentRef(MetaGenericArgumentDomain.EffectRow, name, $"effect:{name}", SymbolId.None, null))
                .ToArray();
            return new MetaTypeRef(
                MetaTypeKind.Function,
                effectful.ToString(),
                $"function:{input.CanonicalText}->{output.CanonicalText}:effects[{string.Join(";", effectNames.Select(ComptimeValue.EncodeText))}]",
                SymbolId.None,
                TypeId.None,
                [input, output],
                effectful,
                effectArguments);
        }

        static MetaTypeRef CreateAssociatedProjectionRef(
            AssociatedTypeProjection projection,
            SymbolTable table,
            AdtDef? declarationOwner)
        {
            var target = projection.Target == null
                ? null
                : CreateTypeRef(projection.Target, table, declarationOwner);
            var arguments = projection.TypeArgs
                .Select(argument => CreateTypeRef(argument, table, declarationOwner))
                .ToArray();
            var identity = $"associated-projection:{target?.CanonicalText ?? "none"}:{ComptimeValue.EncodeText(projection.MemberName)}" +
                           $"[{string.Join(";", arguments.Select(static argument => argument.CanonicalText))}]";
            return new MetaTypeRef(
                MetaTypeKind.AssociatedProjection,
                projection.ToString(),
                identity,
                SymbolId.None,
                TypeId.None,
                target == null ? arguments : [target, .. arguments],
                projection);
        }
    }

    private static MetaTypeRef CreateTypePathRef(TypePath path, SymbolTable symbolTable)
    {
        var symbol = path.SymbolId.IsValid ? symbolTable.GetSymbol(path.SymbolId) : null;
        var qualifiedName = string.Join(WellKnownStrings.Separators.Path, path.ToQualifiedPathParts());
        var genericArguments = CreateGenericArgumentRefs(path, symbolTable);
        var name = genericArguments.Count == 0
            ? qualifiedName
            : $"{qualifiedName}[{string.Join(", ", genericArguments.Select(static argument => argument.Display))}]";
        var kind = path.TypeName switch
        {
            "Ref" => MetaTypeKind.Reference,
            "MRef" or "MutRef" => MetaTypeKind.MutableReference,
            "Shared" => MetaTypeKind.SharedReference,
            "RawPtr" or "Ptr" => MetaTypeKind.RawPointer,
            "Cfn" => MetaTypeKind.ForeignFunction,
            _ => symbol switch
            {
                TypeParamSymbol => MetaTypeKind.TypeParameter,
                TraitSymbol => MetaTypeKind.Trait,
                AdtSymbol { IsTypeAlias: true } => MetaTypeKind.Alias,
                AdtSymbol when BaseTypes.IsBuiltIn(symbol.TypeId) => MetaTypeKind.Primitive,
                AdtSymbol { IsCaseType: true } => MetaTypeKind.Case,
                AdtSymbol { IsClosedSum: true } => MetaTypeKind.ClosedSum,
                AdtSymbol { IsCStruct: true } => MetaTypeKind.ForeignNominal,
                AdtSymbol => MetaTypeKind.Nominal,
                EffectSymbol => MetaTypeKind.Effect,
                _ => MetaTypeKind.Nominal
            }
        };
        var baseIdentity = symbol == null ? $"{kind.ToToken()}:{name}" : CreateStableIdentity(symbol, symbolTable);
        var stableIdentity = genericArguments.Count == 0
            ? baseIdentity
            : $"{baseIdentity}[{string.Join(";", genericArguments.Select(static argument => argument.CanonicalText))}]";
        return new MetaTypeRef(
            kind,
            name,
            stableIdentity,
            symbol?.Id ?? path.SymbolId,
            symbol?.TypeId ?? TypeId.None,
            path.TypeArgs.Select(argument => CreateTypeRef(argument, symbolTable)).ToArray(),
            path,
            genericArguments);
    }

    private static MetaTypeRef CreateTypeParameterRef(TypeParamSymbol parameter, SymbolTable symbolTable) => new(
        MetaTypeKind.TypeParameter,
        parameter.Name,
        CreateStableIdentity(parameter, symbolTable),
        parameter.Id,
        parameter.TypeId,
        [],
        CreateTypePath(parameter.Name, parameter.Id, parameter.Span));

    private static MetaTypeRef CreateTypeParameterRef(TypeParam parameter, SymbolTable symbolTable)
    {
        var symbol = parameter.SymbolId.IsValid ? symbolTable.GetSymbol<TypeParamSymbol>(parameter.SymbolId) : null;
        return new MetaTypeRef(
            MetaTypeKind.TypeParameter,
            parameter.Name,
            symbol == null ? $"type-parameter:{parameter.Name}" : CreateStableIdentity(symbol, symbolTable),
            parameter.SymbolId,
            symbol?.TypeId ?? TypeId.None,
            [],
            CreateTypePath(parameter.Name, parameter.SymbolId, parameter.Span));
    }

    private static MetaGenericArgumentRef CreateGenericParameterRef(TypeParamSymbol parameter, SymbolTable symbolTable)
    {
        var domain = parameter.ParameterKind switch
        {
            GenericParameterKind.Type => MetaGenericArgumentDomain.Type,
            GenericParameterKind.Value => MetaGenericArgumentDomain.Value,
            GenericParameterKind.EffectRow => MetaGenericArgumentDomain.EffectRow,
            _ => throw new ArgumentOutOfRangeException(nameof(parameter.ParameterKind))
        };
        var type = parameter.ParameterKind == GenericParameterKind.Type
            ? CreateTypeParameterRef(parameter, symbolTable)
            : null;
        return new MetaGenericArgumentRef(
            domain,
            parameter.Name,
            CreateStableIdentity(parameter, symbolTable),
            parameter.Id,
            type);
    }

    private static MetaGenericArgumentRef CreateGenericParameterRef(TypeParam parameter, SymbolTable symbolTable)
    {
        var symbol = parameter.SymbolId.IsValid ? symbolTable.GetSymbol<TypeParamSymbol>(parameter.SymbolId) : null;
        var domain = parameter.ParameterKind switch
        {
            GenericParameterKind.Type => MetaGenericArgumentDomain.Type,
            GenericParameterKind.Value => MetaGenericArgumentDomain.Value,
            GenericParameterKind.EffectRow => MetaGenericArgumentDomain.EffectRow,
            _ => throw new ArgumentOutOfRangeException(nameof(parameter.ParameterKind))
        };
        var stableIdentity = symbol == null
            ? $"{domain.ToToken()}-parameter:{parameter.Name}"
            : CreateStableIdentity(symbol, symbolTable);
        return new MetaGenericArgumentRef(
            domain,
            parameter.Name,
            stableIdentity,
            parameter.SymbolId,
            parameter.ParameterKind == GenericParameterKind.Type
                ? CreateTypeParameterRef(parameter, symbolTable)
                : null);
    }

    private static IReadOnlyList<MetaGenericArgumentRef> CreateGenericArgumentRefs(
        TypePath path,
        SymbolTable symbolTable)
    {
        if (path.GenericArguments.Count == 0)
        {
            return path.TypeArgs
                .Select(argument => CreateTypeRef(argument, symbolTable))
                .Select(type => new MetaGenericArgumentRef(MetaGenericArgumentDomain.Type, type.Name, type.StableIdentity, type.SymbolId, type))
                .ToArray();
        }

        return path.GenericArguments.Select(argument => argument switch
        {
            TypeGenericArgumentNode typeArgument => CreateTypeGenericArgumentRef(typeArgument.Type, symbolTable),
            ValueGenericArgumentNode valueArgument => CreateValueGenericArgumentRef(valueArgument.Expression, symbolTable),
            EffectGenericArgumentNode effectArgument => CreateEffectGenericArgumentRef(effectArgument.EffectRow, symbolTable),
            UnresolvedGenericArgumentNode { TypeCandidate: { } type } => CreateTypeGenericArgumentRef(type, symbolTable),
            UnresolvedGenericArgumentNode { ValueCandidate: { } value } => CreateValueGenericArgumentRef(value, symbolTable),
            _ => throw new InvalidOperationException($"Meta schema {WellKnownStrings.Meta.SchemaVersion} has no generic argument mapping for the supplied syntax.")
        }).ToArray();
    }

    private static MetaGenericArgumentRef CreateTypeGenericArgumentRef(TypeNode type, SymbolTable symbolTable)
    {
        var typeRef = CreateTypeRef(type, symbolTable);
        return new MetaGenericArgumentRef(MetaGenericArgumentDomain.Type, typeRef.Name, typeRef.StableIdentity, typeRef.SymbolId, typeRef);
    }

    private static MetaGenericArgumentRef CreateEffectGenericArgumentRef(TypeNode type, SymbolTable symbolTable)
    {
        var typeRef = CreateTypeRef(type, symbolTable);
        return new MetaGenericArgumentRef(MetaGenericArgumentDomain.EffectRow, typeRef.Name, typeRef.StableIdentity, typeRef.SymbolId, typeRef);
    }

    private static MetaGenericArgumentRef CreateValueGenericArgumentRef(EidosAstNode expression, SymbolTable symbolTable)
    {
        var symbolId = expression switch
        {
            IdentifierExpr identifier => identifier.SymbolId,
            PathExpr path => path.SymbolId,
            _ => SymbolId.None
        };
        var display = expression switch
        {
            IdentifierExpr identifier => identifier.Name,
            PathExpr path => string.Join(WellKnownStrings.Separators.Path, path.Path),
            LiteralExpr literal => literal.RawText,
            _ => GetCanonicalNodeKind(expression)
        };
        var stableIdentity = symbolId.IsValid && symbolTable.GetSymbol(symbolId) is { } symbol
            ? CreateStableIdentity(symbol, symbolTable)
            : expression is LiteralExpr literalExpression && ComptimeValue.TryFromLiteral(literalExpression.Value, out var literalValue)
                ? literalValue.CanonicalText
                : $"value-expression:{GetCanonicalNodeKind(expression)}:{expression.Span.Position}:{expression.Span.Length}:{Hash(display)}";
        return new MetaGenericArgumentRef(MetaGenericArgumentDomain.Value, display, stableIdentity, symbolId, null);
    }

    private static TypePath CreateTypePath(string name, SymbolId symbolId, SourceSpan span)
    {
        var path = new TypePath { SymbolId = symbolId };
        path.SetTypeName(name);
        path.SetSpan(span);
        return path;
    }

    private static ComptimeAdtValue CreateDeclInfo(ComptimeDeclValue declaration, MetaComptimeContext meta) =>
        BuildDeclarationShape(declaration, meta);

    internal static ComptimeMetaObjectValue CreateSpan(SourceSpan span, SymbolTable? symbolTable = null) => TypedObject(
        "span",
        WellKnownStrings.Meta.Types.Span,
        WellKnownTypeIds.MetaSpanId,
        [
            ("file", new ComptimeStringValue(CreatePublicSourceUri(span, symbolTable))),
            ("position", new ComptimeIntegerValue(span.Position)),
            ("line", new ComptimeIntegerValue(span.Location.Line)),
            ("column", new ComptimeIntegerValue(span.Location.Column)),
            ("length", new ComptimeIntegerValue(span.Length))
        ]);

    internal static string CreatePublicSourceUri(SourceSpan span, SymbolTable? symbolTable = null)
    {
        var filePath = span.FilePath ?? string.Empty;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return string.Empty;
        }

        if (filePath.Contains("://", StringComparison.Ordinal))
        {
            return filePath;
        }

        var matchingModule = symbolTable?.Modules.GetModules()
            .Where(module => string.Equals(
                SourcePathNormalizer.NormalizeForCacheKey(module.Span.FilePath ?? string.Empty),
                SourcePathNormalizer.NormalizeForCacheKey(filePath),
                StringComparison.Ordinal))
            .OrderByDescending(static module => module.Path.Count)
            .FirstOrDefault();
        var package = EscapeSourceUriSegment(matchingModule?.PackageAlias ?? "current");
        var moduleSegments = matchingModule?.Path.Count > 0
            ? matchingModule.Path
            : [Path.GetFileNameWithoutExtension(filePath)];
        var relativePath = string.Join('/', moduleSegments.Select(EscapeSourceUriSegment));
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            relativePath = "module";
        }

        return $"eidos-source://{package}/{relativePath}.eidos";
    }

    private static string EscapeSourceUriSegment(string value) =>
        Uri.EscapeDataString(string.IsNullOrWhiteSpace(value) ? "current" : value);

    internal static bool TryReadSpan(ComptimeMetaObjectValue value, out SourceSpan span)
    {
        span = SourceSpan.Empty;
        if (!value.TryGet("file", out var file) || file is not ComptimeStringValue fileValue ||
            !value.TryGet("position", out var position) || position is not ComptimeIntegerValue positionValue ||
            !value.TryGet("line", out var line) || line is not ComptimeIntegerValue lineValue ||
            !value.TryGet("column", out var column) || column is not ComptimeIntegerValue columnValue ||
            !value.TryGet("length", out var length) || length is not ComptimeIntegerValue lengthValue)
        {
            return false;
        }

        span = new SourceSpan(
            new SourceLocation(
                checked((int)positionValue.Value),
                checked((int)lineValue.Value),
                checked((int)columnValue.Value),
                string.IsNullOrEmpty(fileValue.Value) ? null : fileValue.Value),
            checked((int)lengthValue.Value));
        return true;
    }

}
