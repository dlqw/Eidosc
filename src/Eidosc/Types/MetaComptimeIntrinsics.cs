using System.Security.Cryptography;
using System.Text;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Types;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

internal static class MetaComptimeIntrinsics
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
            $"Meta::{name}",
            "begin",
            $"arguments={call.PositionalArgs.Count}",
            call.Span,
            context.CallDepth);
        var result = TryEvaluateCore(name, call, context, out value, out reason);
        context.Meta?.Trace?.Record(
            context.Meta.TracePhase,
            traceKind,
            $"Meta::{name}",
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
            reason = $"Meta::{name} requires a compiler meta evaluation context";
            return false;
        }

        if (name == "decl")
        {
            return TryCreateDeclarationHandle(call, context.Meta, out value, out reason);
        }

        if (!TryEvaluateArguments(call, context, out var arguments, out reason))
        {
            return false;
        }

        return name switch
        {
            "typeInfo" => TryTypeInfo(arguments, context.Meta, out value, out reason),
            "typeName" => TryTypeName(arguments, out value, out reason),
            "hasField" => TryHasField(arguments, context.Meta, out value, out reason),
            "fieldType" => TryFieldType(arguments, context.Meta, out value, out reason),
            "declarationInfo" => TryDeclarationInfo(arguments, context.Meta, out value, out reason),
            "typeKind" => TryObjectProperty(arguments, "type-info", "kind", out value, out reason),
            "typeParameters" => TryObjectProperty(arguments, "type-info", "parameters", out value, out reason),
            "constructors" => TryObjectProperty(arguments, "type-info", "constructors", out value, out reason),
            "constructorName" => TryObjectProperty(arguments, "constructor-info", "name", out value, out reason),
            "constructorDecl" => TryObjectProperty(arguments, "constructor-info", "decl", out value, out reason),
            "constructorFields" => TryObjectProperty(arguments, "constructor-info", "fields", out value, out reason),
            "fieldName" => TryObjectProperty(arguments, "field-info", "name", out value, out reason),
            "fieldTypeInfo" => TryObjectProperty(arguments, "field-info", "type", out value, out reason),
            "fieldDecl" => TryObjectProperty(arguments, "field-info", "decl", out value, out reason),
            "functionParameters" => TryObjectProperty(arguments, "type-info", "functionParameters", out value, out reason),
            "functionResult" => TryObjectProperty(arguments, "type-info", "functionResult", out value, out reason),
            "functionEffects" => TryObjectProperty(arguments, "type-info", "functionEffects", out value, out reason),
            "referenceMutable" => TryObjectProperty(arguments, "type-info", "referenceMutable", out value, out reason),
            "referenceReferent" => TryObjectProperty(arguments, "type-info", "referenceReferent", out value, out reason),
            "traitAssociatedItems" => TryObjectProperty(arguments, "type-info", "associatedItems", out value, out reason),
            "traitConstraints" => TryObjectProperty(arguments, "type-info", "constraints", out value, out reason),
            "attributes" => TryObjectProperty(arguments, "type-info", "attributes", out value, out reason),
            "declName" => TryObjectProperty(arguments, "decl-info", "name", out value, out reason),
            "declKind" => TryObjectProperty(arguments, "decl-info", "kind", out value, out reason),
            "declSpan" => TryObjectProperty(arguments, "decl-info", "span", out value, out reason),
            "target" => TryObjectProperty(arguments, "derive-input", "target", out value, out reason),
            "targetDecl" => TryObjectProperty(arguments, "derive-input", "targetDecl", out value, out reason),
            "deriveSpan" => TryObjectProperty(arguments, "derive-input", "span", out value, out reason),
            "layoutOf" => TryLayoutOf(arguments, context.Meta, out value, out reason),
            "layoutSize" => TryObjectProperty(arguments, "layout-info", "size", out value, out reason),
            "layoutAlignment" => TryObjectProperty(arguments, "layout-info", "alignment", out value, out reason),
            "layoutFieldOffsets" => TryObjectProperty(arguments, "layout-info", "fieldOffsets", out value, out reason),
            "error" => TryReportDiagnostic(arguments, MetaDiagnosticLevel.Error, context.Meta, context.Resources, out value, out reason),
            "warning" => TryReportDiagnostic(arguments, MetaDiagnosticLevel.Warning, context.Meta, context.Resources, out value, out reason),
            "expansion" => TryObject("expansion", arguments, ["declarations"], out value, out reason),
            "function" => TryObject("declaration.function", arguments, ["name", "parameters", "result", "body"], out value, out reason),
            "implementation" => TryObject("declaration.implementation", arguments, ["trait", "target", "methods"], out value, out reason),
            "comptimeValue" => TryObject("declaration.comptime-value", arguments, ["name", "type", "value"], out value, out reason),
            "attribute" => TryObject("declaration.attribute", arguments, ["target", "name", "arguments"], out value, out reason),
            "test" => TryObject("declaration.test", arguments, ["name", "body"], out value, out reason),
            "moduleMember" => TryObject("declaration.module-member", arguments, ["declaration"], out value, out reason),
            "diagnostic" => TryObject("declaration.diagnostic", arguments, ["level", "span", "message"], out value, out reason),
            "parameter" => TryHandleObject("parameter", call.Span, context.Meta, arguments, ["name", "type"], out value, out reason),
            "binding" => TryHandleObject("binding", call.Span, context.Meta, arguments, ["name"], out value, out reason),
            "exprParam" => TryObject("expr.parameter", arguments, ["parameter"], out value, out reason),
            "exprBinding" => TryObject("expr.binding", arguments, ["binding"], out value, out reason),
            "exprDecl" => TryObject("expr.decl", arguments, ["decl"], out value, out reason),
            "exprInt" => TryObject("expr.int", arguments, ["value"], out value, out reason),
            "exprBool" => TryObject("expr.bool", arguments, ["value"], out value, out reason),
            "exprString" => TryObject("expr.string", arguments, ["value"], out value, out reason),
            "exprUnit" => TryObject("expr.unit", arguments, [], out value, out reason),
            "exprCall" => TryObject("expr.call", arguments, ["callee", "arguments"], out value, out reason),
            "exprCtor" => TryObject("expr.constructor", arguments, ["constructor", "arguments"], out value, out reason),
            "exprCtorFields" => TryObject("expr.record-constructor", arguments, ["constructor", "fields"], out value, out reason),
            "namedExpr" => TryObject("named-expr", arguments, ["field", "expression"], out value, out reason),
            "exprField" => TryObject("expr.field", arguments, ["subject", "field"], out value, out reason),
            "exprBinary" => TryObject("expr.binary", arguments, ["operator", "left", "right"], out value, out reason),
            "exprTuple" => TryObject("expr.tuple", arguments, ["elements"], out value, out reason),
            "exprList" => TryObject("expr.list", arguments, ["elements"], out value, out reason),
            "exprMatch" => TryObject("expr.match", arguments, ["subject", "branches"], out value, out reason),
            "patternWildcard" => TryObject("pattern.wildcard", arguments, [], out value, out reason),
            "patternBinding" => TryObject("pattern.binding", arguments, ["binding"], out value, out reason),
            "patternCtor" => TryObject("pattern.constructor", arguments, ["constructor", "patterns"], out value, out reason),
            "patternCtorFields" => TryObject("pattern.record-constructor", arguments, ["constructor", "fields"], out value, out reason),
            "fieldPattern" => TryObject("field-pattern", arguments, ["field", "pattern"], out value, out reason),
            "branch" => TryObject("branch", arguments, ["pattern", "expression"], out value, out reason),
            _ => Fail($"unknown Meta intrinsic '{name}'", out value, out reason)
        };
    }

    private static string ClassifyTraceKind(string name)
    {
        return name switch
        {
            "typeInfo" or "typeName" or "hasField" or "fieldType" or "declarationInfo" or
            "typeKind" or "typeParameters" or "constructors" or "constructorName" or
            "constructorDecl" or "constructorFields" or "fieldName" or "fieldTypeInfo" or
            "fieldDecl" or "functionParameters" or "functionResult" or "functionEffects" or
            "referenceMutable" or "referenceReferent" or "traitAssociatedItems" or
            "traitConstraints" or "attributes" or "declName" or "declKind" or "declSpan" or
            "target" or "targetDecl" or "deriveSpan" or "layoutOf" or "layoutSize" or
            "layoutAlignment" or "layoutFieldOffsets" => "query",
            "error" or "warning" or "diagnostic" => "diagnostic",
            "decl" => "handle",
            _ => "builder"
        };
    }

    private static bool TryEvaluateArguments(
        CallExpr call,
        ComptimeEvaluationContext context,
        out IReadOnlyList<ComptimeValue> arguments,
        out string reason)
    {
        var values = new List<ComptimeValue>(call.PositionalArgs.Count);
        foreach (var argument in call.PositionalArgs)
        {
            if (!ComptimeEvaluator.TryEvaluateNode(argument, context, out var argumentValue, out reason))
            {
                arguments = [];
                return false;
            }

            values.Add(argumentValue);
        }

        arguments = values;
        reason = string.Empty;
        return true;
    }

    private static bool TryCreateDeclarationHandle(
        CallExpr call,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (call.PositionalArgs.Count != 1 ||
            !TryGetReferencedSymbol(call.PositionalArgs[0], out var symbolId) ||
            meta.SymbolTable.GetSymbol(symbolId) is not { } symbol)
        {
            return Fail("Meta::decl expects one resolved declaration, type, trait, constructor, or function reference", out value, out reason);
        }

        value = CreateDeclValue(symbol, meta.SymbolTable);
        reason = string.Empty;
        return true;
    }

    private static bool TryGetReferencedSymbol(EidosAstNode node, out SymbolId symbolId)
    {
        symbolId = node switch
        {
            IdentifierExpr identifier => identifier.SymbolId,
            PathExpr path => path.SymbolId,
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
            ? new MetaTypeRef("unknown", "Unknown", "unknown", SymbolId.None, TypeId.None, [])
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

        value = CreateDeclInfo(declaration, meta.SymbolTable);
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

    private static bool TryLayoutOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeTypeValue typeValue ||
            arguments[1] is not ComptimeStringValue target ||
            string.IsNullOrWhiteSpace(target.Value))
        {
            return Fail("Meta::layoutOf expects (Type, non-empty target triple)", out value, out reason);
        }

        var pointerSize = target.Value.Contains("64", StringComparison.Ordinal) ||
                          target.Value.Contains("aarch64", StringComparison.OrdinalIgnoreCase)
            ? 8
            : target.Value.Contains("32", StringComparison.Ordinal) ||
              target.Value.Contains("wasm32", StringComparison.OrdinalIgnoreCase)
                ? 4
                : 0;
        if (pointerSize == 0)
        {
            return Fail($"unsupported explicit layout target '{target.Value}'", out value, out reason);
        }

        var (size, alignment, offsets, complete) = GetLayout(typeValue.TypeRef, pointerSize, meta);
        if (!complete)
        {
            return Fail(
                $"layout fact for '{typeValue.TypeRef.Name}' is not complete in the reflection phase; query it after target layout or use a primitive/reference/@cstruct type",
                out value,
                out reason);
        }

        value = Obj(
            "layout-info",
            ("target", new ComptimeStringValue(target.Value)),
            ("size", new ComptimeIntegerValue(size)),
            ("alignment", new ComptimeIntegerValue(alignment)),
            ("fieldOffsets", List(offsets.Select(static offset => (ComptimeValue)new ComptimeIntegerValue(offset)))));
        reason = string.Empty;
        return true;
    }

    private static (long Size, long Alignment, IReadOnlyList<long> Offsets, bool Complete) GetLayout(
        MetaTypeRef type,
        int pointerSize,
        MetaComptimeContext meta)
    {
        if (type.Kind is "reference" or "mutable-reference" ||
            type.Name is "RawPtr" or "Ptr")
        {
            return (pointerSize, pointerSize, [], true);
        }

        var primitive = type.Name switch
        {
            "Int" or "Float" => (8L, 8L, true),
            "Bool" => (1L, 1L, true),
            "Char" => (4L, 4L, true),
            "Unit" or "()" => (0L, 1L, true),
            _ => (0L, 0L, false)
        };
        if (primitive.Item3)
        {
            return (primitive.Item1, primitive.Item2, [], true);
        }

        if (type.SymbolId.IsValid &&
            meta.SymbolTable.GetSymbol<AdtSymbol>(type.SymbolId) is { IsCStruct: true, CStructLayoutInfo: { } layout })
        {
            return (
                layout.TotalSize,
                layout.Alignment,
                layout.Fields.Select(static field => (long)field.Offset).ToArray(),
                true);
        }

        return (0, 0, [], false);
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
            return Fail($"Meta::{level.ToString().ToLowerInvariant()} expects (Meta::Span, String)", out value, out reason);
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
            $"{meta.ExpansionTrace}|{kind}|{span.FilePath}|{span.Position}|{span.Length}|" +
            string.Join("|", arguments.Select(static argument => argument.CanonicalText)));
        value = objectValue with
        {
            Properties = [new ComptimeNamedValue("identity", new ComptimeStringValue(identity)), .. objectValue.Properties]
        };
        return true;
    }

    private static ComptimeMetaObjectValue BuildTypeInfo(ComptimeTypeValue typeValue, MetaComptimeContext meta)
    {
        var type = typeValue.TypeRef;
        var parameters = List(type.Arguments.Select(static argument => (ComptimeValue)new ComptimeTypeValue(argument)));
        var properties = new List<(string Name, ComptimeValue Value)>
        {
            ("kind", new ComptimeStringValue(ClassifyType(type, meta))),
            ("type", typeValue),
            ("name", new ComptimeStringValue(type.Name)),
            ("parameters", parameters),
            ("constructors", List([])),
            ("functionParameters", List([])),
            ("functionResult", ComptimeUnitValue.Instance),
            ("functionEffects", List([])),
            ("referenceMutable", new ComptimeBoolValue(type.Kind == "mutable-reference")),
            ("referenceReferent", type.Arguments.Count > 0 ? new ComptimeTypeValue(type.Arguments[0]) : ComptimeUnitValue.Instance),
            ("associatedItems", List([])),
            ("constraints", List([])),
            ("attributes", List([])),
            ("declaration", type.SymbolId.IsValid && meta.SymbolTable.GetSymbol(type.SymbolId) is { } declaration
                ? CreateDeclInfo(CreateDeclValue(declaration, meta.SymbolTable), meta.SymbolTable)
                : Obj("decl-info", ("name", new ComptimeStringValue(type.Name)), ("kind", new ComptimeStringValue(type.Kind)), ("span", CreateSpan(SourceSpan.Empty))))
        };

        if (type.Kind == "function" && type.Arguments.Count > 0)
        {
            properties.Replace("functionParameters", List(type.Arguments.Take(type.Arguments.Count - 1).Select(static argument => (ComptimeValue)new ComptimeTypeValue(argument))));
            properties.Replace("functionResult", new ComptimeTypeValue(type.Arguments[^1]));
        }

        if (type.SymbolId.IsValid && meta.AdtDefinitions.TryGetValue(type.SymbolId, out var adt))
        {
            properties.Replace("attributes", List(adt.Attributes.Select(static attribute => (ComptimeValue)new ComptimeStringValue(FormatAttribute(attribute)))));
            properties.Replace("constructors", List(adt.Constructors.Select(constructor => (ComptimeValue)CreateConstructorInfo(constructor, adt, meta))));
        }
        else if (type.SymbolId.IsValid && meta.TraitDefinitions.TryGetValue(type.SymbolId, out var trait))
        {
            var items = trait.Methods
                .Select(method => meta.SymbolTable.GetSymbol(method.SymbolId))
                .Where(static symbol => symbol != null)
                .Select(symbol => (ComptimeValue)CreateDeclInfo(CreateDeclValue(symbol!, meta.SymbolTable), meta.SymbolTable))
                .Concat(trait.AssociatedTypes.Select(associated => (ComptimeValue)Obj(
                    "decl-info",
                    ("name", new ComptimeStringValue(associated.Name)),
                    ("kind", new ComptimeStringValue("associated-type")),
                    ("span", CreateSpan(associated.Span)))))
                .Concat(trait.AssociatedConsts.Select(associated => (ComptimeValue)Obj(
                    "decl-info",
                    ("name", new ComptimeStringValue(associated.Name)),
                    ("kind", new ComptimeStringValue("associated-const")),
                    ("span", CreateSpan(associated.Span)))))
                .ToArray();
            properties.Replace("associatedItems", List(items));
            properties.Replace("constraints", List(trait.SuperTraits.Select(static constraint => (ComptimeValue)new ComptimeStringValue(constraint.ToString()))));
            properties.Replace("attributes", List(trait.Attributes.Select(static attribute => (ComptimeValue)new ComptimeStringValue(FormatAttribute(attribute)))));
        }

        return Obj("type-info", properties.ToArray());
    }

    private static string ClassifyType(MetaTypeRef type, MetaComptimeContext meta)
    {
        if (type.Kind is "tuple" or "function" or "reference" or "mutable-reference" or "type-parameter")
        {
            return type.Kind;
        }

        if (type.SymbolId.IsValid && meta.SymbolTable.GetSymbol(type.SymbolId) is TraitSymbol)
        {
            return "trait";
        }

        if (type.SymbolId.IsValid && meta.SymbolTable.GetSymbol(type.SymbolId) is AdtSymbol symbol)
        {
            return BaseTypes.IsBuiltIn(symbol.TypeId) ? "primitive" : "adt";
        }

        return type.Kind;
    }

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
        return Obj(
            "constructor-info",
            ("name", new ComptimeStringValue(constructor.Name)),
            ("decl", declaration),
            ("fields", List(fields)),
            ("span", CreateSpan(constructor.Span)));
    }

    private static ComptimeMetaObjectValue CreateFieldInfo(
        string name,
        TypeNode? type,
        SymbolId symbolId,
        SourceSpan span,
        AdtDef owner,
        MetaComptimeContext meta)
    {
        var fieldType = type == null
            ? new ComptimeTypeValue(new MetaTypeRef("unknown", "Unknown", "unknown", SymbolId.None, TypeId.None, []))
            : new ComptimeTypeValue(CreateTypeRef(type, meta.SymbolTable, owner));
        var fieldSymbol = symbolId.IsValid ? meta.SymbolTable.GetSymbol(symbolId) : null;
        var declaration = fieldSymbol == null
            ? new ComptimeDeclValue(SymbolId.None, $"field:{owner.Name}:{name}", name, "field", span)
            : CreateDeclValue(fieldSymbol, meta.SymbolTable);
        return Obj(
            "field-info",
            ("name", new ComptimeStringValue(name)),
            ("type", fieldType),
            ("decl", declaration),
            ("span", CreateSpan(span)));
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
            !meta.AdtDefinitions.TryGetValue(typeValue.TypeRef.SymbolId, out var resolvedOwner))
        {
            return false;
        }

        owner = resolvedOwner;

        var matches = owner.Constructors
            .SelectMany(static constructor => constructor.NamedArgs)
            .Where(candidate => string.Equals(candidate.Name, fieldName, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            return false;
        }

        field = matches[0];
        return true;
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
            TypeParamSymbol => "type-parameter",
            TraitSymbol => "trait",
            AdtSymbol when BaseTypes.IsBuiltIn(symbol.TypeId) => "primitive",
            AdtSymbol => "adt",
            _ => "nominal"
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
            GenericArguments: genericArguments));
    }

    internal static ComptimeTypeValue CreateAdtTargetTypeValue(AdtDef adt, SymbolTable symbolTable)
    {
        var arguments = adt.TypeParams
            .Where(static parameter => parameter.ParameterKind == GenericParameterKind.Type)
            .Select(parameter => CreateTypeParameterRef(parameter, symbolTable))
            .ToArray();
        var genericArguments = adt.TypeParams
            .Select(parameter => CreateGenericParameterRef(parameter, symbolTable))
            .ToArray();
        var syntax = CreateTypePath(adt.Name, adt.SymbolId, adt.Span);
        var syntaxArguments = new List<GenericArgumentNode>(adt.TypeParams.Count);
        foreach (var parameter in adt.TypeParams)
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
        syntax.SetGenericArguments(syntaxArguments);

        var symbolValue = symbolTable.GetSymbol(adt.SymbolId);
        return new ComptimeTypeValue(new MetaTypeRef(
            "adt",
            adt.Name,
            symbolValue == null ? $"adt:{adt.Name}" : CreateStableIdentity(symbolValue, symbolTable),
            adt.SymbolId,
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
        symbol.Span);

    internal static ComptimeMetaObjectValue CreateDeriveInput(
        AdtDef target,
        Symbol generator,
        SourceSpan span,
        int occurrenceIndex,
        SymbolTable symbolTable)
    {
        var targetSymbol = symbolTable.GetSymbol(target.SymbolId)!;
        return Obj(
            "derive-input",
            ("target", CreateAdtTargetTypeValue(target, symbolTable)),
            ("targetDecl", CreateDeclValue(targetSymbol, symbolTable)),
            ("generator", CreateDeclValue(generator, symbolTable)),
            ("span", CreateSpan(span)),
            ("occurrence", new ComptimeIntegerValue(occurrenceIndex)),
            ("schemaVersion", new ComptimeIntegerValue(WellKnownStrings.Meta.SchemaVersion)));
    }

    internal static string CreateStableIdentity(Symbol symbol, SymbolTable symbolTable)
    {
        var owner = symbolTable.Modules.TryGetOwningModule(symbol.Id, out var module)
            ? ModuleRegistry.FormatModuleFullName(module)
            : "<builtin>";
        var path = string.IsNullOrWhiteSpace(symbol.Span.FilePath)
            ? string.Empty
            : Path.GetFullPath(symbol.Span.FilePath).Replace('\\', '/');
        return $"{owner}::{symbol.Kind}:{symbol.Name}@{path}:{symbol.Span.Position}:{symbol.Span.Length}";
    }

    internal static MetaTypeRef CreateTypeRef(TypeNode type, SymbolTable symbolTable, AdtDef? owner = null)
    {
        return type switch
        {
            TypePath path => CreateTypePathRef(path, symbolTable),
            TupleType tuple => new MetaTypeRef(
                "tuple",
                tuple.ToString(),
                $"tuple:{string.Join(",", tuple.Elements.Select(element => CreateTypeRef(element, symbolTable, owner).StableIdentity))}",
                SymbolId.None,
                TypeId.None,
                tuple.Elements.Select(element => CreateTypeRef(element, symbolTable, owner)).ToArray(),
                tuple),
            ArrowType arrow => new MetaTypeRef(
                "function",
                arrow.ToString(),
                $"function:{CreateTypeRef(arrow.ParamType, symbolTable, owner).StableIdentity}->{CreateTypeRef(arrow.ReturnType, symbolTable, owner).StableIdentity}",
                SymbolId.None,
                TypeId.None,
                [CreateTypeRef(arrow.ParamType, symbolTable, owner), CreateTypeRef(arrow.ReturnType, symbolTable, owner)],
                arrow),
            EffectfulType effectful when effectful.OutputType != null => new MetaTypeRef(
                "function",
                effectful.ToString(),
                $"effectful:{effectful}",
                SymbolId.None,
                TypeId.None,
                [CreateTypeRef(effectful.InputType, symbolTable, owner), CreateTypeRef(effectful.OutputType, symbolTable, owner)],
                effectful),
            _ => new MetaTypeRef("unknown", type.ToString(), $"unknown:{type}", SymbolId.None, TypeId.None, [], type)
        };
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
            "Ref" => "reference",
            "MRef" or "MutRef" => "mutable-reference",
            _ => symbol switch
            {
                TypeParamSymbol => "type-parameter",
                TraitSymbol => "trait",
                AdtSymbol when BaseTypes.IsBuiltIn(symbol.TypeId) => "primitive",
                AdtSymbol => "adt",
                _ => "nominal"
            }
        };
        return new MetaTypeRef(
            kind,
            name,
            symbol == null ? $"{kind}:{name}" : CreateStableIdentity(symbol, symbolTable),
            symbol?.Id ?? path.SymbolId,
            symbol?.TypeId ?? TypeId.None,
            path.TypeArgs.Select(argument => CreateTypeRef(argument, symbolTable)).ToArray(),
            path,
            genericArguments);
    }

    private static MetaTypeRef CreateTypeParameterRef(TypeParamSymbol parameter, SymbolTable symbolTable) => new(
        "type-parameter",
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
            "type-parameter",
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
            GenericParameterKind.Type => "type",
            GenericParameterKind.Value => "value",
            GenericParameterKind.EffectRow => "effect-row",
            _ => "unknown"
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
            GenericParameterKind.Type => "type",
            GenericParameterKind.Value => "value",
            GenericParameterKind.EffectRow => "effect-row",
            _ => "unknown"
        };
        var stableIdentity = symbol == null
            ? $"{domain}-parameter:{parameter.Name}"
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
                .Select(type => new MetaGenericArgumentRef("type", type.Name, type.StableIdentity, type.SymbolId, type))
                .ToArray();
        }

        return path.GenericArguments.Select(argument => argument switch
        {
            TypeGenericArgumentNode typeArgument => CreateTypeGenericArgumentRef(typeArgument.Type, symbolTable),
            ValueGenericArgumentNode valueArgument => CreateValueGenericArgumentRef(valueArgument.Expression, symbolTable),
            EffectGenericArgumentNode effectArgument => CreateEffectGenericArgumentRef(effectArgument.EffectRow, symbolTable),
            UnresolvedGenericArgumentNode { TypeCandidate: { } type } => CreateTypeGenericArgumentRef(type, symbolTable),
            UnresolvedGenericArgumentNode { ValueCandidate: { } value } => CreateValueGenericArgumentRef(value, symbolTable),
            _ => new MetaGenericArgumentRef("unknown", "_", "unknown", SymbolId.None, null)
        }).ToArray();
    }

    private static MetaGenericArgumentRef CreateTypeGenericArgumentRef(TypeNode type, SymbolTable symbolTable)
    {
        var typeRef = CreateTypeRef(type, symbolTable);
        return new MetaGenericArgumentRef("type", typeRef.Name, typeRef.StableIdentity, typeRef.SymbolId, typeRef);
    }

    private static MetaGenericArgumentRef CreateEffectGenericArgumentRef(TypeNode type, SymbolTable symbolTable)
    {
        var typeRef = CreateTypeRef(type, symbolTable);
        return new MetaGenericArgumentRef("effect-row", typeRef.Name, typeRef.StableIdentity, typeRef.SymbolId, typeRef);
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
            _ => expression.GetType().Name
        };
        var stableIdentity = symbolId.IsValid && symbolTable.GetSymbol(symbolId) is { } symbol
            ? CreateStableIdentity(symbol, symbolTable)
            : expression is LiteralExpr literalExpression && ComptimeValue.TryFromLiteral(literalExpression.Value, out var literalValue)
                ? literalValue.CanonicalText
                : $"value-expression:{expression.Span.FilePath}:{expression.Span.Position}:{expression.Span.Length}:{expression.GetType().Name}";
        return new MetaGenericArgumentRef("value", display, stableIdentity, symbolId, null);
    }

    private static TypePath CreateTypePath(string name, SymbolId symbolId, SourceSpan span)
    {
        var path = new TypePath { SymbolId = symbolId };
        path.SetTypeName(name);
        path.SetSpan(span);
        return path;
    }

    private static ComptimeMetaObjectValue CreateDeclInfo(ComptimeDeclValue declaration, SymbolTable symbolTable)
    {
        var isPublic = declaration.SymbolId.IsValid && symbolTable.GetSymbol(declaration.SymbolId)?.IsPublic == true;
        return Obj(
            "decl-info",
            ("decl", declaration),
            ("name", new ComptimeStringValue(declaration.Name)),
            ("kind", new ComptimeStringValue(declaration.DeclarationKind)),
            ("public", new ComptimeBoolValue(isPublic)),
            ("span", CreateSpan(declaration.Span)));
    }

    internal static ComptimeMetaObjectValue CreateSpan(SourceSpan span) => Obj(
        "span",
        ("file", new ComptimeStringValue(span.FilePath ?? string.Empty)),
        ("position", new ComptimeIntegerValue(span.Position)),
        ("line", new ComptimeIntegerValue(span.Location.Line)),
        ("column", new ComptimeIntegerValue(span.Location.Column)),
        ("length", new ComptimeIntegerValue(span.Length)));

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

    private static ComptimeMetaObjectValue Obj(string kind, params (string Name, ComptimeValue Value)[] properties) =>
        new(kind, properties.Select(static property => new ComptimeNamedValue(property.Name, property.Value)).ToArray());

    private static ComptimeSequenceValue List(IEnumerable<ComptimeValue> values) =>
        new(ComptimeSequenceKind.List, values.ToArray());

    private static string FormatAttribute(Eidosc.Ast.Attribute attribute) => attribute.ArgumentTexts.Count == 0
        ? $"@{attribute.Name}"
        : $"@{attribute.Name}({string.Join(", ", attribute.ArgumentTexts)})";

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static bool TrySingle<T>(
        IReadOnlyList<ComptimeValue> arguments,
        out T value,
        out string reason)
        where T : ComptimeValue
    {
        if (arguments.Count == 1 && arguments[0] is T typed)
        {
            value = typed;
            reason = string.Empty;
            return true;
        }

        value = null!;
        reason = $"expected one {typeof(T).Name} argument";
        return false;
    }

    private static bool Fail(string message, out ComptimeValue value, out string reason)
    {
        value = ComptimeUnitValue.Instance;
        reason = message;
        return false;
    }

    private static void Replace(
        this List<(string Name, ComptimeValue Value)> properties,
        string name,
        ComptimeValue value)
    {
        var index = properties.FindIndex(property => string.Equals(property.Name, name, StringComparison.Ordinal));
        if (index >= 0)
        {
            properties[index] = (name, value);
        }
        else
        {
            properties.Add((name, value));
        }
    }
}
