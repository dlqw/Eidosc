using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Eidosc.Ast;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Expressions;
using Eidosc.Ast.Patterns;
using Eidosc.Ast.Types;
using Eidosc.Pipeline;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

internal static partial class MetaComptimeIntrinsics
{
    private static readonly HashSet<string> CachedQueries = new(StringComparer.Ordinal)
    {
        "shape_of", "name_of", "has_field", "find_field", "kind_of", "parameters_of", "constructors_of",
        "fields_of", "declared_fields_of", "type_of", "result_type_of", "effects_of", "mutability_of",
        "referent_of", "items_of", "constraints_of", "clauses_of", "clause_keyword_of", "clause_kind_of",
        "clause_stage_of", "clause_arguments_of", "clause_occurrence_of", "clause_source_order_of",
        "clause_argument_type_of", "clause_argument_text_of", "clause_argument_path_of",
        "clause_argument_index_of", "clause_argument_occurrence_of", "span_of", "target_type_of",
        "target_declaration_of", "cases_of", "leaf_cases_of", "parent_type_of", "case_type_of",
        "constructor_of", "is_subtype", "join_type_of", "layout_of", "layout_size", "layout_alignment",
        "layout_field_offsets", "syntax_of", "arguments_of", "module_of", "package_of", "workspace_of",
        "modules_of", "imports_of", "exports_of", "body_of", "nodes_of", "value_of", "references_to",
        "calls_from", "callers_of", "implementations_of", "target_scope", "module_scope", "package_scope",
        "dependencies_scope", "workspace_scope", "resources_of", "resource_path_of",
        "resource_content_of", "resource_exists", "resource_hash_of"
    };

    private static bool IsCachedQuery(string name) => CachedQueries.Contains(name);

    internal static bool TryEvaluateQuery(
        string name,
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!IsCachedQuery(name))
        {
            return Fail($"unknown Meta query '{name}'", out value, out reason);
        }

        var context = new ComptimeEvaluationContext(
            new Dictionary<SymbolId, ComptimeValue>(),
            new Dictionary<SymbolId, FuncDef>(),
            Meta: meta);
        return TryEvaluateExtendedQuery(name, arguments, context, SourceSpan.Empty, out value, out reason);
    }

    private static bool TryEvaluateExtendedQuery(
        string name,
        IReadOnlyList<ComptimeValue> arguments,
        ComptimeEvaluationContext context,
        SourceSpan span,
        out ComptimeValue value,
        out string reason)
    {
        var meta = context.Meta!;
        var key = CreateQueryKey(name, arguments, meta);
        if (meta.Queries.TryGet(key, out value))
        {
            if (!context.Resources.TryConsumeQuery(value, out reason))
            {
                value = ComptimeUnitValue.Instance;
                return false;
            }

            meta.Queries.Record(key, value, cacheHit: true);
            RecordQueryCacheTrace(name, key, value, cacheHit: true, span, context);
            return true;
        }

        var result = name switch
        {
            "shape_of" => TryShapeOf(arguments, meta, out value, out reason),
            "name_of" => TryNameOf(arguments, out value, out reason),
            "has_field" => TryHasField(arguments, meta, out value, out reason),
            "find_field" => TryFindFieldValue(arguments, meta, out value, out reason),
            "kind_of" => TryAnyObjectProperty(arguments, "kind", meta, out value, out reason),
            "parameters_of" => TryAnyObjectProperty(arguments, "parameters", meta, out value, out reason),
            "constructors_of" => TryAnyObjectProperty(arguments, "constructors", meta, out value, out reason),
            "fields_of" => TryFieldsOf(arguments, meta, declaredOnly: false, out value, out reason),
            "declared_fields_of" => TryFieldsOf(arguments, meta, declaredOnly: true, out value, out reason),
            "type_of" => TryAnyObjectProperty(arguments, "type", meta, out value, out reason),
            "result_type_of" => TryAnyObjectProperty(arguments, "functionResult", meta, out value, out reason),
            "effects_of" => TryAnyObjectProperty(arguments, "functionEffects", meta, out value, out reason),
            "mutability_of" => TryAnyObjectProperty(arguments, "referenceMutable", meta, out value, out reason),
            "referent_of" => TryAnyObjectProperty(arguments, "referenceReferent", meta, out value, out reason),
            "items_of" => TryAnyObjectProperty(arguments, "associatedItems", meta, out value, out reason),
            "constraints_of" => TryAnyObjectProperty(arguments, "constraints", meta, out value, out reason),
            "clauses_of" => TryAnyObjectProperty(arguments, "clauses", meta, out value, out reason),
            "clause_keyword_of" => TryObjectProperty(arguments, "clause", "keyword", out value, out reason),
            "clause_kind_of" => TryObjectProperty(arguments, "clause", "kind", out value, out reason),
            "clause_stage_of" => TryObjectProperty(arguments, "clause", "stage", out value, out reason),
            "clause_arguments_of" => TryObjectProperty(arguments, "clause", "arguments", out value, out reason),
            "clause_occurrence_of" => TryObjectProperty(arguments, "clause", "occurrenceIdentity", out value, out reason),
            "clause_source_order_of" => TryObjectProperty(arguments, "clause", "sourceOrder", out value, out reason),
            "clause_argument_type_of" => TryObjectProperty(arguments, "clause-argument", "type", out value, out reason),
            "clause_argument_text_of" => TryObjectProperty(arguments, "clause-argument", "canonicalText", out value, out reason),
            "clause_argument_path_of" => TryObjectProperty(arguments, "clause-argument", "path", out value, out reason),
            "clause_argument_index_of" => TryObjectProperty(arguments, "clause-argument", "index", out value, out reason),
            "clause_argument_occurrence_of" => TryObjectProperty(arguments, "clause-argument", "occurrenceIdentity", out value, out reason),
            "span_of" => TryAnyObjectProperty(arguments, "span", meta, out value, out reason),
            "target_type_of" => TryObjectProperty(arguments, "target", "target", out value, out reason),
            "target_declaration_of" => TryObjectProperty(arguments, "target", "targetDecl", out value, out reason),
            "cases_of" => TryCasesOf(arguments, meta, leavesOnly: false, out value, out reason),
            "leaf_cases_of" => TryCasesOf(arguments, meta, leavesOnly: true, out value, out reason),
            "parent_type_of" => TryParentTypeOf(arguments, meta, out value, out reason),
            "case_type_of" => TryCaseTypeOf(arguments, meta, out value, out reason),
            "constructor_of" => TryConstructorOf(arguments, meta, out value, out reason),
            "is_subtype" => TryIsSubtype(arguments, meta, out value, out reason),
            "join_type_of" => TryJoinTypeOf(arguments, meta, out value, out reason),
            "layout_of" => TryLayoutOf(arguments, meta, out value, out reason),
            "layout_size" => TryObjectProperty(arguments, "layout-info", "size", out value, out reason),
            "layout_alignment" => TryObjectProperty(arguments, "layout-info", "alignment", out value, out reason),
            "layout_field_offsets" => TryObjectProperty(arguments, "layout-info", "fieldOffsets", out value, out reason),
            "syntax_of" => TrySyntaxOf(arguments, meta, out value, out reason),
            "arguments_of" => TryArgumentsOf(arguments, meta, out value, out reason),
            "module_of" => TryModuleOf(arguments, meta, out value, out reason),
            "package_of" => TryPackageOf(arguments, meta, out value, out reason),
            "workspace_of" => TryWorkspaceOf(arguments, meta, out value, out reason),
            "modules_of" => TryModulesOf(arguments, meta, out value, out reason),
            "imports_of" => TryImportsOf(arguments, meta, out value, out reason),
            "exports_of" => TryExportsOf(arguments, meta, out value, out reason),
            "body_of" => TryBodyOf(arguments, meta, out value, out reason),
            "nodes_of" => TryNodesOf(arguments, meta, context.Resources, out value, out reason),
            "value_of" => TryValueOf(arguments, context, out value, out reason),
            "references_to" => TryReferencesTo(arguments, meta, context.Resources, out value, out reason),
            "calls_from" => TryCallsFrom(arguments, meta, context.Resources, out value, out reason),
            "callers_of" => TryCallersOf(arguments, meta, context.Resources, out value, out reason),
            "implementations_of" => TryImplementationsOf(arguments, meta, out value, out reason),
            "target_scope" => TryCreateScope("target", arguments, meta, out value, out reason),
            "module_scope" => TryCreateScope("module", arguments, meta, out value, out reason),
            "package_scope" => TryCreateScope("package", arguments, meta, out value, out reason),
            "dependencies_scope" => TryCreateScope("dependencies", arguments, meta, out value, out reason),
            "workspace_scope" => TryCreateScope("workspace", arguments, meta, out value, out reason),
            "resources_of" => TryResourcesOf(arguments, out value, out reason),
            "resource_path_of" => TryObjectProperty(arguments, "resource", "path", out value, out reason),
            "resource_content_of" => TryObjectProperty(arguments, "resource", "content", out value, out reason),
            "resource_exists" => TryObjectProperty(arguments, "resource", "exists", out value, out reason),
            "resource_hash_of" => TryObjectProperty(arguments, "resource", "contentHash", out value, out reason),
            _ => Fail($"unknown Meta query '{name}'", out value, out reason)
        };
        if (!result)
        {
            return false;
        }

        if (!context.Resources.TryConsumeQuery(value, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        meta.Queries.Store(key, value);
        meta.Queries.Record(key, value, cacheHit: false);
        RecordQueryCacheTrace(name, key, value, cacheHit: false, span, context);
        return true;
    }

    private static void RecordQueryCacheTrace(
        string name,
        string key,
        ComptimeValue value,
        bool cacheHit,
        SourceSpan span,
        ComptimeEvaluationContext context)
    {
        context.Meta?.Trace?.Record(
            context.Meta.TracePhase,
            "query-cache",
            $"meta.{name}",
            cacheHit ? "cache-hit" : "cache-miss",
            $"key={key};resultHash={value.CanonicalHash};resultBytes={Encoding.UTF8.GetByteCount(value.CanonicalText)}",
            span,
            context.CallDepth);
    }

    private static string CreateQueryKey(
        string name,
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta) => Hash(string.Join(
        "|",
        WellKnownStrings.Meta.SchemaVersion,
        name,
        meta.Access.Fingerprint,
        CreateQueryFactFingerprint(name, arguments, meta),
        string.Join("|", arguments.Select(static argument => argument.CanonicalText))));

    private static bool TrySyntaxOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1)
        {
            return Fail("meta.syntax_of expects one reflected entity", out value, out reason);
        }

        if (arguments[0] is ComptimeMetaObjectValue { SchemaKind: "syntax-handle" } syntax)
        {
            value = syntax;
            reason = string.Empty;
            return true;
        }

        if (!TryResolveSyntaxSubject(arguments[0], meta, out var declaration, out var node, out var identity, out var span))
        {
            return Fail("meta.syntax_of expects a declaration, type, body, or body-node handle", out value, out reason);
        }

        if (declaration != null && !CanAccessDeclaration(declaration.SymbolId, requireBody: false, meta, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        var properties = new List<(string Name, ComptimeValue Value)>
        {
            ("identity", new ComptimeStringValue(identity)),
            ("category", new ComptimeStringValue(GetSyntaxCategory(node ?? declaration))),
            ("span", CreateSpan(span, meta.SymbolTable))
        };
        if (declaration?.SymbolId.IsValid == true && meta.SymbolTable.GetSymbol(declaration.SymbolId) is { } symbol)
        {
            properties.Add(("declaration", CreateDeclValue(symbol, meta.SymbolTable)));
        }

        value = TypedObject("syntax-handle", WellKnownStrings.Meta.Types.Syntax, WellKnownTypeIds.MetaSyntaxId, properties);
        reason = string.Empty;
        return true;
    }

    private static bool TryArgumentsOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1)
        {
            return Fail("meta.arguments_of expects one reflected entity", out value, out reason);
        }

        if (arguments[0] is ComptimeTypeValue typeValue)
        {
            var genericArguments = typeValue.TypeRef.GenericArguments ??
                typeValue.TypeRef.Arguments.Select(static type => new MetaGenericArgumentRef(
                    MetaGenericArgumentDomain.Type, type.Name, type.StableIdentity, type.SymbolId, type)).ToArray();
            value = List(genericArguments.Select(argument => (ComptimeValue)TypedObject(
                "generic-argument",
                WellKnownStrings.Meta.Types.GenericArgument,
                WellKnownTypeIds.MetaGenericArgumentId,
                [
                    ("domain", new ComptimeStringValue(argument.Domain.ToToken())),
                    ("identity", new ComptimeStringValue(argument.StableIdentity)),
                    ("type", argument.Type == null ? ComptimeUnitValue.Instance : new ComptimeTypeValue(argument.Type))
                ])));
            reason = string.Empty;
            return true;
        }

        if (arguments[0] is ComptimeMetaObjectValue { SchemaKind: "call-handle" } call &&
            call.TryGet("arguments", out value))
        {
            reason = string.Empty;
            return true;
        }

        if (arguments[0] is ComptimeMetaObjectValue reflected && reflected.TryGet("arguments", out value))
        {
            reason = string.Empty;
            return true;
        }

        return Fail("reflected entity does not expose generic or call arguments", out value, out reason);
    }

    private static bool TryModuleOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1 || !TryResolveEntityModule(arguments[0], meta, out var module))
        {
            return Fail("meta.module_of expects an entity with an owning module", out value, out reason);
        }

        value = CreateModuleHandle(module);
        reason = string.Empty;
        return true;
    }

    private static bool TryPackageOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1)
        {
            return Fail("meta.package_of expects one module or declaration entity", out value, out reason);
        }

        if (arguments[0] is ComptimeMetaObjectValue { SchemaKind: "package-handle" } package)
        {
            value = package;
            reason = string.Empty;
            return true;
        }

        if (!TryResolveEntityModule(arguments[0], meta, out var module))
        {
            return Fail("meta.package_of expects an entity with an owning package", out value, out reason);
        }

        value = CreatePackageHandle(module);
        reason = string.Empty;
        return true;
    }

    private static bool TryResourcesOf(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1 ||
            arguments[0] is not ComptimeMetaObjectValue { SchemaKind: "package-handle" } package ||
            !package.TryGet("capabilities", out var capabilityValue) ||
            capabilityValue is not ComptimeSequenceValue capabilities ||
            !capabilities.Elements.OfType<ComptimeStringValue>()
                .Any(static capability => capability.Value == "read-declared-resources"))
        {
            return Fail(
                "meta.resources_of requires a package query with read-declared-resources capability",
                out value,
                out reason);
        }

        if (!package.TryGet("resources", out value))
        {
            return Fail("package query has no declared resource set", out value, out reason);
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryWorkspaceOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1)
        {
            return Fail("meta.workspace_of expects one reflected entity", out value, out reason);
        }

        if (!meta.Access.Capabilities.HasFlag(MetaQueryCapability.Workspace))
        {
            return Fail("workspace reflection requires an explicit workspace meta capability", out value, out reason);
        }

        value = CreateWorkspaceHandle(meta);
        reason = string.Empty;
        return true;
    }

    private static bool TryModulesOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1 || !TryReadIdentity(arguments[0], "package-handle", out var packageIdentity))
        {
            return Fail("meta.modules_of expects meta.Package", out value, out reason);
        }

        var modules = meta.SymbolTable.Modules.GetModules()
            .Where(module => string.Equals(GetPackageIdentity(module), packageIdentity, StringComparison.Ordinal))
            .Where(module => IsSamePackage(module, meta) || module.IsPublic)
            .Select(static module => (ComptimeValue)CreateModuleHandle(module));
        value = List(modules);
        reason = string.Empty;
        return true;
    }

    private static bool TryImportsOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1 || !TryResolveModuleHandle(arguments[0], meta, out var module))
        {
            return Fail("meta.imports_of expects meta.Module", out value, out reason);
        }

        if (!CanAccessModule(module, requireBody: false, meta, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = List(module.Imports
            .Select(meta.SymbolTable.Modules.GetModule)
            .Where(static imported => imported != null)
            .Select(static imported => (ComptimeValue)CreateModuleHandle(imported!)));
        reason = string.Empty;
        return true;
    }

    private static bool TryExportsOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1 || !TryResolveModuleHandle(arguments[0], meta, out var module))
        {
            return Fail("meta.exports_of expects meta.Module", out value, out reason);
        }

        if (!CanAccessModule(module, requireBody: false, meta, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        IEnumerable<SymbolId> exportedIds = module.UsesExplicitExports
            ? module.ExportedBindings.Select(static binding => binding.SymbolId)
            : module.Members.Where(id => meta.SymbolTable.GetSymbol(id)?.IsPublic == true);
        value = List(exportedIds
            .Distinct()
            .Select(meta.SymbolTable.GetSymbol)
            .Where(static symbol => symbol != null)
            .Select(symbol => (ComptimeValue)CreateDeclValue(symbol!, meta.SymbolTable)));
        reason = string.Empty;
        return true;
    }

    private static bool TryBodyOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (meta.Access.AvailableStage < ClauseStage.Body)
        {
            return Fail("meta.body_of requires Body-stage semantic facts", out value, out reason);
        }

        if (arguments.Count != 1 || !TryGetDeclarationFromReflectedValue(arguments[0], out var handle) ||
            !meta.DeclarationDefinitions.TryGetValue(handle.SymbolId, out var declaration) ||
            declaration is not FuncDef function)
        {
            return Fail("meta.body_of expects a function declaration with a body", out value, out reason);
        }

        if (!CanAccessDeclaration(handle.SymbolId, requireBody: true, meta, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = CreateBodyHandle(function, handle, meta);
        reason = string.Empty;
        return true;
    }

    private static bool TryNodesOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        ComptimeResourceBudget resources,
        out ComptimeValue value,
        out string reason)
    {
        if (meta.Access.AvailableStage < ClauseStage.Body)
        {
            return Fail("meta.nodes_of requires Body-stage semantic facts", out value, out reason);
        }

        if (arguments.Count != 1 || !TryResolveBody(arguments[0], meta, out var function, out var owner))
        {
            return Fail("meta.nodes_of expects meta.Body", out value, out reason);
        }

        var nodes = EnumerateBodyNodes(function).ToArray();
        if (!resources.TryConsumeSyntaxNodes(nodes.Length, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = List(nodes.Select(entry => (ComptimeValue)CreateBodyNodeHandle(owner, entry.Node, entry.Ordinal, meta)));
        reason = string.Empty;
        return true;
    }

    private static bool TryValueOf(
        IReadOnlyList<ComptimeValue> arguments,
        ComptimeEvaluationContext context,
        out ComptimeValue value,
        out string reason)
    {
        var meta = context.Meta!;
        if (arguments.Count != 1 || !TryGetDeclarationFromReflectedValue(arguments[0], out var declaration))
        {
            return Fail("meta.value_of expects a comptime constant declaration", out value, out reason);
        }

        if (!CanAccessDeclaration(declaration.SymbolId, requireBody: false, meta, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        if (!context.Values.TryGetValue(declaration.SymbolId, out value!))
        {
            return Fail($"comptime declaration '{declaration.Name}' has no serializable value in the current phase", out value, out reason);
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryReferencesTo(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        ComptimeResourceBudget resources,
        out ComptimeValue value,
        out string reason)
    {
        if (meta.Access.AvailableStage < ClauseStage.Semantic)
        {
            return Fail("meta.references_to requires Semantic-stage facts", out value, out reason);
        }

        if (!TryDeclarationAndScope(arguments, meta, out var target, out var scope, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        var references = new List<ComptimeValue>();
        foreach (var owner in EnumerateQueryableDeclarations(meta, scope, requireBody: true))
        {
            var ownerHandle = CreateDeclarationHandle(owner, meta);
            foreach (var entry in EnumerateDeclarationNodes(owner))
            {
                if (entry.Node.SymbolId != target.SymbolId)
                {
                    continue;
                }

                references.Add(TypedObject(
                    "reference-handle",
                    WellKnownStrings.Meta.Types.Reference,
                    WellKnownTypeIds.MetaReferenceId,
                    [
                        ("identity", new ComptimeStringValue(Hash($"reference|{ownerHandle.StableIdentity}|{entry.Ordinal}|{target.StableIdentity}"))),
                        ("declaration", target),
                        ("owner", ownerHandle),
                        ("node", CreateBodyNodeHandle(ownerHandle, entry.Node, entry.Ordinal, meta)),
                        ("span", CreateSpan(entry.Node.Span, meta.SymbolTable)),
                        ("kind", new ComptimeStringValue(GetCanonicalNodeKind(entry.Node)))
                    ]));
            }
        }

        if (!resources.TryConsumeSyntaxNodes(references.Count, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = List(references);
        reason = string.Empty;
        return true;
    }

    private static bool TryCallsFrom(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        ComptimeResourceBudget resources,
        out ComptimeValue value,
        out string reason)
    {
        if (meta.Access.AvailableStage < ClauseStage.Semantic)
        {
            return Fail("meta.calls_from requires Semantic-stage facts", out value, out reason);
        }

        if (arguments.Count != 1 || !TryGetDeclarationFromReflectedValue(arguments[0], out var caller) ||
            !meta.DeclarationDefinitions.TryGetValue(caller.SymbolId, out var declaration) ||
            declaration is not FuncDef function)
        {
            return Fail("meta.calls_from expects a function declaration", out value, out reason);
        }

        if (!CanAccessDeclaration(caller.SymbolId, requireBody: true, meta, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        var calls = EnumerateCallHandles(function, caller, meta).ToArray();
        if (!resources.TryConsumeSyntaxNodes(calls.Length, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = List(calls);
        reason = string.Empty;
        return true;
    }

    private static bool TryCallersOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        ComptimeResourceBudget resources,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryDeclarationAndScope(arguments, meta, out var callee, out var scope, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        var calls = EnumerateQueryableDeclarations(meta, scope, requireBody: true)
            .OfType<FuncDef>()
            .SelectMany(function => EnumerateCallHandles(function, CreateDeclarationHandle(function, meta), meta))
            .Where(call => call.TryGet("callee", out var target) &&
                           target is ComptimeDeclValue declaration &&
                           declaration.SymbolId == callee.SymbolId)
            .Cast<ComptimeValue>()
            .ToArray();
        if (!resources.TryConsumeSyntaxNodes(calls.Length, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        value = List(calls);
        reason = string.Empty;
        return true;
    }

    private static bool TryImplementationsOf(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (!TryDeclarationAndScope(arguments, meta, out var trait, out var scope, out reason))
        {
            value = ComptimeUnitValue.Instance;
            return false;
        }

        if (meta.SymbolTable.GetSymbol<TraitSymbol>(trait.SymbolId) == null)
        {
            return Fail("meta.implementations_of expects a trait declaration", out value, out reason);
        }

        var implementations = new List<ComptimeValue>();
        foreach (var implementation in meta.SymbolTable.GetImplsForTrait(trait.SymbolId)
                     .OrderBy(static item => item.CanonicalImplementingType, StringComparer.Ordinal)
                     .ThenBy(static item => item.Id.Value))
        {
            if (!IsSymbolWithinScope(implementation.Id, scope, meta) ||
                !CanAccessDeclaration(implementation.Id, requireBody: false, meta, out _))
            {
                continue;
            }

            var declaration = meta.DeclarationDefinitions.TryGetValue(implementation.Id, out var node)
                ? CreateDeclarationHandle(node, meta)
                : CreateDeclValue(implementation, meta.SymbolTable);
            var implementingSymbol = meta.SymbolTable.GetSymbolByTypeId(implementation.ImplementingType);
            implementations.Add(TypedObject(
                "implementation-handle",
                WellKnownStrings.Meta.Types.Implementation,
                WellKnownTypeIds.MetaImplementationId,
                [
                    ("identity", new ComptimeStringValue(CreateStableIdentity(implementation, meta.SymbolTable))),
                    ("declaration", declaration),
                    ("trait", trait),
                    ("implementingType", implementingSymbol == null
                        ? ComptimeUnitValue.Instance
                        : CreateTypeValue(implementingSymbol, meta.SymbolTable)),
                    ("methods", List(implementation.Methods
                        .Select(meta.SymbolTable.GetSymbol)
                        .Where(static symbol => symbol != null)
                        .Select(symbol => (ComptimeValue)CreateDeclValue(symbol!, meta.SymbolTable))))
                ]));
        }

        value = List(implementations);
        reason = string.Empty;
        return true;
    }

    private static bool TryCreateScope(
        string kind,
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1)
        {
            return Fail($"meta.{kind}_scope expects one boundary handle", out value, out reason);
        }

        var valid = kind switch
        {
            "target" => TryGetDeclarationFromReflectedValue(arguments[0], out _),
            "module" => TryReadIdentity(arguments[0], "module-handle", out _),
            "package" or "dependencies" => TryReadIdentity(arguments[0], "package-handle", out _),
            "workspace" => TryReadIdentity(arguments[0], "workspace-handle", out _) &&
                           meta.Access.Capabilities.HasFlag(MetaQueryCapability.Workspace),
            _ => false
        };
        if (!valid)
        {
            return Fail($"invalid or unauthorized boundary for {kind} reflection scope", out value, out reason);
        }

        value = TypedObject(
            "scope",
            WellKnownStrings.Meta.Types.Scope,
            WellKnownTypeIds.MetaScopeId,
            [
                ("kind", new ComptimeStringValue(kind)),
                ("boundary", arguments[0]),
                ("identity", new ComptimeStringValue(Hash($"scope|{kind}|{arguments[0].CanonicalText}")))
            ]);
        reason = string.Empty;
        return true;
    }

    private static IEnumerable<ComptimeMetaObjectValue> EnumerateCallHandles(
        FuncDef function,
        ComptimeDeclValue caller,
        MetaComptimeContext meta)
    {
        foreach (var entry in EnumerateBodyNodes(function))
        {
            if (!TryGetCall(entry.Node, out var callNode, out var arguments, out var calleeId) ||
                !calleeId.IsValid ||
                meta.SymbolTable.GetSymbol(calleeId) is not { } callee)
            {
                continue;
            }

            yield return TypedObject(
                "call-handle",
                WellKnownStrings.Meta.Types.Call,
                WellKnownTypeIds.MetaCallId,
                [
                    ("identity", new ComptimeStringValue(Hash($"call|{caller.StableIdentity}|{entry.Ordinal}|{CreateStableIdentity(callee, meta.SymbolTable)}"))),
                    ("caller", caller),
                    ("callee", CreateDeclValue(callee, meta.SymbolTable)),
                    ("node", CreateBodyNodeHandle(caller, callNode, entry.Ordinal, meta)),
                    ("arguments", List(arguments.Select((argument, index) =>
                        (ComptimeValue)CreateBodyNodeHandle(caller, argument, entry.Ordinal * 1024 + index + 1, meta)))),
                    ("span", CreateSpan(callNode.Span, meta.SymbolTable))
                ]);
        }
    }

    private static bool TryGetCall(
        EidosAstNode node,
        out EidosAstNode callNode,
        out IReadOnlyList<EidosAstNode> arguments,
        out SymbolId calleeId)
    {
        callNode = node;
        arguments = [];
        calleeId = SymbolId.None;
        switch (node)
        {
            case CallExpr call:
                arguments = [.. call.PositionalArgs, .. call.NamedArgs.Where(static arg => arg.Value != null).Select(static arg => arg.Value!)];
                calleeId = call.Function?.SymbolId ?? SymbolId.None;
                return true;
            case MethodCallExpr method when method.HasExplicitCallSyntax:
                arguments = [.. method.PositionalArgs, .. method.NamedArgs.Where(static arg => arg.Value != null).Select(static arg => arg.Value!)];
                calleeId = method.SymbolId.IsValid
                    ? method.SymbolId
                    : method.MethodCandidateSymbolIds.Count == 1 ? method.MethodCandidateSymbolIds[0] : SymbolId.None;
                return true;
            case InfixCallExpr infix:
                arguments = new EidosAstNode?[] { infix.Left, infix.Right }
                    .Where(static item => item != null)
                    .Cast<EidosAstNode>()
                    .ToArray();
                calleeId = infix.FunctionSymbolId;
                return true;
            default:
                return false;
        }
    }

    private static ComptimeMetaObjectValue CreateBodyHandle(
        FuncDef function,
        ComptimeDeclValue owner,
        MetaComptimeContext meta) => TypedObject(
        "body-handle",
        WellKnownStrings.Meta.Types.Body,
        WellKnownTypeIds.MetaBodyId,
        [
            ("identity", new ComptimeStringValue(Hash($"body|{owner.StableIdentity}"))),
            ("declaration", owner),
            ("span", CreateSpan(function.Span, meta.SymbolTable))
        ]);

    private static ComptimeMetaObjectValue CreateBodyNodeHandle(
        ComptimeDeclValue owner,
        EidosAstNode node,
        int ordinal,
        MetaComptimeContext meta)
    {
        var properties = new List<(string Name, ComptimeValue Value)>
        {
            ("identity", new ComptimeStringValue(Hash($"body-node|{owner.StableIdentity}|{ordinal}|{GetCanonicalNodeKind(node)}|{node.Span.Position}|{node.Span.Length}"))),
            ("owner", owner),
            ("ordinal", new ComptimeIntegerValue(ordinal)),
            ("kind", new ComptimeStringValue(GetCanonicalNodeKind(node))),
            ("span", CreateSpan(node.Span, meta.SymbolTable))
        };
        if (node.SymbolId.IsValid)
        {
            if (meta.SymbolTable.GetSymbol(node.SymbolId) is { } symbol)
            {
                properties.Add(("declaration", CreateDeclValue(symbol, meta.SymbolTable)));
            }
        }
        if (node.InferredType is Type inferredType)
        {
            properties.Add(("type", new ComptimeTypeValue(CreateSemanticTypeRef(inferredType, meta.SymbolTable))));
        }

        return TypedObject(
            "body-node-handle",
            WellKnownStrings.Meta.Types.BodyNode,
            WellKnownTypeIds.MetaBodyNodeId,
            properties);
    }

    private static IEnumerable<(EidosAstNode Node, int Ordinal)> EnumerateBodyNodes(FuncDef function)
    {
        var visited = new HashSet<EidosAstNode>(ReferenceEqualityComparer.Instance);
        var nodes = new List<EidosAstNode>();
        foreach (var branch in function.Body)
        {
            Visit(branch);
        }

        return nodes
            .Select(static (node, structuralOrdinal) => (Node: node, StructuralOrdinal: structuralOrdinal))
            .OrderBy(static entry => entry.Node.Span.FilePath, StringComparer.Ordinal)
            .ThenBy(static entry => entry.Node.Span.Position)
            .ThenBy(static entry => entry.StructuralOrdinal)
            .Select(static (entry, ordinal) => (entry.Node, ordinal));

        void Visit(EidosAstNode node)
        {
            if (!visited.Add(node))
            {
                return;
            }

            nodes.Add(node);
            foreach (var child in AstStableNodeTraversal.GetStructuralChildren(node))
            {
                if (child is Declaration)
                {
                    continue;
                }
                Visit(child);
            }
        }
    }

    private static IEnumerable<(EidosAstNode Node, int Ordinal)> EnumerateDeclarationNodes(Declaration declaration)
    {
        if (declaration is FuncDef function)
        {
            return EnumerateBodyNodes(function);
        }

        var children = AstStableNodeTraversal.GetStructuralChildren(declaration)
            .Where(static child => child is not Declaration)
            .SelectMany(EnumerateSubtree)
            .ToArray();
        return children.Select(static (node, ordinal) => (node, ordinal));

        static IEnumerable<EidosAstNode> EnumerateSubtree(EidosAstNode root)
        {
            yield return root;
            foreach (var child in AstStableNodeTraversal.GetStructuralChildren(root))
            {
                if (child is Declaration)
                {
                    continue;
                }
                foreach (var nested in EnumerateSubtree(child))
                {
                    yield return nested;
                }
            }
        }
    }

    private static bool TryResolveBody(
        ComptimeValue value,
        MetaComptimeContext meta,
        out FuncDef function,
        out ComptimeDeclValue owner)
    {
        function = null!;
        owner = null!;
        if (value is not ComptimeMetaObjectValue { SchemaKind: "body-handle" } body ||
            !body.TryGet("declaration", out var declaration) ||
            declaration is not ComptimeDeclValue handle ||
            !meta.DeclarationDefinitions.TryGetValue(handle.SymbolId, out var node) ||
            node is not FuncDef resolved ||
            !CanAccessDeclaration(handle.SymbolId, requireBody: true, meta, out _))
        {
            return false;
        }

        function = resolved;
        owner = handle;
        return true;
    }

    private static bool TryResolveSyntaxSubject(
        ComptimeValue value,
        MetaComptimeContext meta,
        out Declaration? declaration,
        out EidosAstNode? node,
        out string identity,
        out Eidosc.Utils.SourceSpan span)
    {
        declaration = null;
        node = null;
        identity = string.Empty;
        span = Eidosc.Utils.SourceSpan.Empty;
        if (TryGetDeclarationFromReflectedValue(value, out var handle) &&
            meta.DeclarationDefinitions.TryGetValue(handle.SymbolId, out declaration))
        {
            node = declaration;
            identity = $"syntax:{handle.StableIdentity}";
            span = declaration.Span;
            return true;
        }

        if (value is ComptimeTypeValue { TypeRef.SymbolId.IsValid: true } type &&
            meta.DeclarationDefinitions.TryGetValue(type.TypeRef.SymbolId, out declaration))
        {
            node = declaration;
            identity = $"syntax:{type.TypeRef.StableIdentity}";
            span = declaration.Span;
            return true;
        }

        if (value is ComptimeMetaObjectValue { SchemaKind: "body-handle" } body &&
            body.TryGet("declaration", out var bodyDeclaration) &&
            bodyDeclaration is ComptimeDeclValue bodyOwner &&
            meta.DeclarationDefinitions.TryGetValue(bodyOwner.SymbolId, out declaration))
        {
            node = declaration;
            identity = $"syntax:body:{bodyOwner.StableIdentity}";
            span = declaration.Span;
            return true;
        }

        if (value is ComptimeMetaObjectValue { SchemaKind: "body-node-handle" } bodyNode &&
            TryResolveBodyNode(bodyNode, meta, out node, out var owner))
        {
            declaration = meta.DeclarationDefinitions.GetValueOrDefault(owner.SymbolId);
            bodyNode.TryGet("identity", out var nodeIdentity);
            identity = nodeIdentity is ComptimeStringValue text ? $"syntax:{text.Value}" : $"syntax:{owner.StableIdentity}";
            span = node.Span;
            return true;
        }

        return false;
    }

    private static bool TryResolveBodyNode(
        ComptimeMetaObjectValue handle,
        MetaComptimeContext meta,
        out EidosAstNode node,
        out ComptimeDeclValue owner)
    {
        node = null!;
        owner = null!;
        if (!handle.TryGet("owner", out var ownerValue) || ownerValue is not ComptimeDeclValue declaration ||
            !handle.TryGet("ordinal", out var ordinalValue) || ordinalValue is not ComptimeIntegerValue ordinal ||
            !meta.DeclarationDefinitions.TryGetValue(declaration.SymbolId, out var ownerNode) ||
            ownerNode is not FuncDef function)
        {
            return false;
        }

        var entry = EnumerateBodyNodes(function).FirstOrDefault(candidate => candidate.Ordinal == ordinal.Value);
        if (entry.Node == null)
        {
            return false;
        }

        node = entry.Node;
        owner = declaration;
        return true;
    }

    private static bool TryGetDeclarationFromReflectedValue(ComptimeValue value, out ComptimeDeclValue declaration)
    {
        if (value is ComptimeDeclValue direct)
        {
            declaration = direct;
            return true;
        }

        if (value is ComptimeMetaObjectValue reflected)
        {
            foreach (var propertyName in new[] { "declaration", "decl", "targetDecl", "owner" })
            {
                if (reflected.TryGet(propertyName, out var property) && property is ComptimeDeclValue handle)
                {
                    declaration = handle;
                    return true;
                }
            }
        }

        declaration = null!;
        return false;
    }

    private static ComptimeDeclValue CreateDeclarationHandle(Declaration declaration, MetaComptimeContext meta)
    {
        if (declaration.SymbolId.IsValid && meta.SymbolTable.GetSymbol(declaration.SymbolId) is { } symbol)
        {
            return CreateDeclValue(symbol, meta.SymbolTable);
        }

        return new ComptimeDeclValue(
            declaration.SymbolId,
            Hash($"declaration|{GetSyntaxCategory(declaration)}|{CreatePublicSourceUri(declaration.Span, meta.SymbolTable)}|{declaration.Span.Position}|{declaration.Span.Length}"),
            GetDeclarationName(declaration),
            GetSyntaxCategory(declaration),
            declaration.Span);
    }

    private static bool TryResolveEntityModule(
        ComptimeValue value,
        MetaComptimeContext meta,
        out ModuleSymbol module)
    {
        if (TryResolveModuleHandle(value, meta, out module))
        {
            return true;
        }

        var symbolId = value switch
        {
            ComptimeTypeValue type => type.TypeRef.SymbolId,
            _ when TryGetDeclarationFromReflectedValue(value, out var declaration) => declaration.SymbolId,
            _ => SymbolId.None
        };
        if (!symbolId.IsValid)
        {
            module = null!;
            return false;
        }

        if (meta.SymbolTable.GetSymbol<ModuleSymbol>(symbolId) is { } directModule)
        {
            module = directModule;
            return true;
        }

        return meta.SymbolTable.Modules.TryGetOwningModule(symbolId, out module!);
    }

    private static bool TryResolveModuleHandle(
        ComptimeValue value,
        MetaComptimeContext meta,
        out ModuleSymbol module) =>
        TryResolveModuleHandle(value, meta.SymbolTable, out module);

    internal static bool TryResolveModuleHandle(
        ComptimeValue value,
        SymbolTable symbolTable,
        out ModuleSymbol module)
    {
        module = null!;
        if (!TryReadIdentity(value, "module-handle", out var identity))
        {
            return false;
        }

        module = symbolTable.Modules.GetModules()
            .FirstOrDefault(candidate => string.Equals(GetModuleIdentity(candidate), identity, StringComparison.Ordinal))!;
        return module != null;
    }

    private static ComptimeMetaObjectValue CreateModuleHandle(ModuleSymbol module) => TypedObject(
        "module-handle",
        WellKnownStrings.Meta.Types.Module,
        WellKnownTypeIds.MetaModuleId,
        [
            ("identity", new ComptimeStringValue(GetModuleIdentity(module))),
            ("name", new ComptimeStringValue(string.Join(WellKnownStrings.Separators.Path, module.Path))),
            ("path", List(module.Path.Select(static segment => (ComptimeValue)new ComptimeStringValue(segment)))),
            ("packageIdentity", new ComptimeStringValue(GetPackageIdentity(module))),
            ("public", new ComptimeBoolValue(module.IsPublic))
        ]);

    internal static ComptimeMetaObjectValue CreatePackageHandle(ModuleSymbol module) => TypedObject(
        "package-handle",
        WellKnownStrings.Meta.Types.Package,
        WellKnownTypeIds.MetaPackageId,
        [
            ("identity", new ComptimeStringValue(GetPackageIdentity(module))),
            ("name", new ComptimeStringValue(module.PackageAlias ?? "current"))
        ]);

    internal static bool TryCreateScopeForPackageProtocol(
        ComptimeMetaObjectValue package,
        MetaComptimeContext meta,
        out ComptimeMetaObjectValue scope,
        out string reason)
    {
        if (TryCreateScope("package", [package], meta, out var value, out reason) &&
            value is ComptimeMetaObjectValue reflectedScope)
        {
            scope = reflectedScope;
            return true;
        }

        scope = null!;
        return false;
    }

    private static ComptimeMetaObjectValue CreateWorkspaceHandle(MetaComptimeContext meta)
    {
        var identities = meta.SymbolTable.Modules.GetModules()
            .Select(GetPackageIdentity)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(static identity => identity, StringComparer.Ordinal);
        return TypedObject(
            "workspace-handle",
            WellKnownStrings.Meta.Types.Workspace,
            WellKnownTypeIds.MetaWorkspaceId,
            [("identity", new ComptimeStringValue(Hash($"workspace|{string.Join("|", identities)}")))]);
    }

    private static bool TryReadIdentity(ComptimeValue value, string schemaKind, out string identity)
    {
        identity = string.Empty;
        return value is ComptimeMetaObjectValue reflected &&
               string.Equals(reflected.SchemaKind, schemaKind, StringComparison.Ordinal) &&
               reflected.TryGet("identity", out var identityValue) &&
               identityValue is ComptimeStringValue text &&
               !string.IsNullOrWhiteSpace(identity = text.Value);
    }

    private static bool TryDeclarationAndScope(
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta,
        out ComptimeDeclValue declaration,
        out ComptimeMetaObjectValue scope,
        out string reason)
    {
        declaration = null!;
        if (arguments.Count != 2 ||
            !TryGetDeclarationFromReflectedValue(arguments[0], out declaration) ||
            !TryReadScope(arguments[1], out var reflectedScope))
        {
            scope = null!;
            reason = "meta scope query expects (meta.Declaration, meta.Scope)";
            return false;
        }

        if (!CanAccessDeclaration(declaration.SymbolId, requireBody: false, meta, out reason))
        {
            scope = null!;
            return false;
        }

        scope = reflectedScope;
        return true;
    }

    private static bool TryReadScope(ComptimeValue value, out ComptimeMetaObjectValue scope)
    {
        if (value is ComptimeMetaObjectValue { SchemaKind: "scope" } direct)
        {
            scope = direct;
            return true;
        }

        if (value is ComptimeMetaObjectValue { SchemaKind: "package-handle" } package &&
            package.TryGet("scope", out var scopeValue) &&
            scopeValue is ComptimeMetaObjectValue { SchemaKind: "scope" } queryScope)
        {
            scope = queryScope;
            return true;
        }

        scope = null!;
        return false;
    }

    private static IEnumerable<Declaration> EnumerateQueryableDeclarations(
        MetaComptimeContext meta,
        ComptimeMetaObjectValue scope,
        bool requireBody)
    {
        return meta.DeclarationDefinitions.Values
            .Where(declaration => declaration.SymbolId.IsValid)
            .DistinctBy(static declaration => declaration.SymbolId)
            .Where(declaration => IsSymbolWithinScope(declaration.SymbolId, scope, meta))
            .Where(declaration => CanAccessDeclaration(declaration.SymbolId, requireBody, meta, out _))
            .OrderBy(declaration => CreateDeclarationHandle(declaration, meta).StableIdentity, StringComparer.Ordinal)
            .ThenBy(static declaration => declaration.Span.Position);
    }

    private static bool IsSymbolWithinScope(
        SymbolId symbolId,
        ComptimeMetaObjectValue scope,
        MetaComptimeContext meta)
    {
        if (!scope.TryGet("kind", out var kindValue) || kindValue is not ComptimeStringValue kind ||
            !scope.TryGet("boundary", out var boundary))
        {
            return false;
        }

        return kind.Value switch
        {
            "target" => TryGetDeclarationFromReflectedValue(boundary, out var target) && target.SymbolId == symbolId,
            "module" => TryResolveModuleHandle(boundary, meta, out var module) &&
                        meta.SymbolTable.Modules.TryGetOwningModuleId(symbolId, out var owner) && owner == module.Id,
            "package" => TryReadIdentity(boundary, "package-handle", out var packageIdentity) &&
                         TryGetSymbolModule(symbolId, meta, out var packageModule) &&
                         string.Equals(GetPackageIdentity(packageModule), packageIdentity, StringComparison.Ordinal),
            "dependencies" => TryReadIdentity(boundary, "package-handle", out var sourcePackage) &&
                              TryGetSymbolModule(symbolId, meta, out var dependencyModule) &&
                              !string.Equals(GetPackageIdentity(dependencyModule), sourcePackage, StringComparison.Ordinal),
            "workspace" => meta.Access.Capabilities.HasFlag(MetaQueryCapability.Workspace),
            _ => false
        };
    }

    private static bool CanAccessDeclaration(
        SymbolId symbolId,
        bool requireBody,
        MetaComptimeContext meta,
        out string reason)
    {
        reason = string.Empty;
        if (!symbolId.IsValid || meta.SymbolTable.GetSymbol(symbolId) is not { } symbol)
        {
            reason = "reflection target has no stable declaration identity";
            return false;
        }

        if (meta.Access.TargetSymbolId is { } targetId && targetId == symbolId)
        {
            if (!requireBody || meta.Access.AvailableStage >= ClauseStage.Body)
            {
                return true;
            }
        }

        if (!TryGetSymbolModule(symbolId, meta, out var module))
        {
            return symbol.IsPublic && !requireBody;
        }

        var samePackage = IsSamePackage(module, meta);
        if (samePackage)
        {
            var required = requireBody
                ? MetaQueryCapability.CurrentPackageBodies
                : symbol.IsPublic ? MetaQueryCapability.None : MetaQueryCapability.CurrentPackagePrivateShapes;
            if (required == MetaQueryCapability.None || meta.Access.Capabilities.HasFlag(required))
            {
                return true;
            }
        }
        else if (requireBody
                     ? meta.Access.Capabilities.HasFlag(MetaQueryCapability.DependencyBodies)
                     : symbol.IsPublic || meta.Access.Capabilities.HasFlag(MetaQueryCapability.DependencyPrivateShapes))
        {
            return true;
        }

        reason = requireBody
            ? "body/reference reflection is outside the authorized package boundary"
            : "private declaration reflection is outside the authorized package boundary";
        return false;
    }

    private static bool CanAccessModule(
        ModuleSymbol module,
        bool requireBody,
        MetaComptimeContext meta,
        out string reason)
    {
        if (IsSamePackage(module, meta))
        {
            reason = string.Empty;
            return !requireBody || meta.Access.Capabilities.HasFlag(MetaQueryCapability.CurrentPackageBodies);
        }

        if (module.IsPublic && (!requireBody || meta.Access.Capabilities.HasFlag(MetaQueryCapability.DependencyBodies)))
        {
            reason = string.Empty;
            return true;
        }

        reason = "module reflection is outside the authorized package boundary";
        return false;
    }

    private static bool IsSamePackage(ModuleSymbol module, MetaComptimeContext meta)
    {
        if (meta.SymbolTable.Modules.GetModule(meta.Access.CurrentModuleId) is not { } requester)
        {
            return string.IsNullOrWhiteSpace(module.PackageAlias);
        }

        return string.Equals(GetPackageIdentity(module), GetPackageIdentity(requester), StringComparison.Ordinal);
    }

    private static bool TryGetSymbolModule(SymbolId symbolId, MetaComptimeContext meta, out ModuleSymbol module)
    {
        if (meta.SymbolTable.GetSymbol<ModuleSymbol>(symbolId) is { } direct)
        {
            module = direct;
            return true;
        }

        return meta.SymbolTable.Modules.TryGetOwningModule(symbolId, out module!);
    }

    private static string GetModuleIdentity(ModuleSymbol module) => Hash($"module|{module.Identity.ToIdentityKey()}");

    private static string GetPackageIdentity(ModuleSymbol module) => Hash($"package|{module.PackageInstanceKey ?? module.PackageAlias ?? "current"}");

    private static string GetSyntaxCategory(EidosAstNode? node) => node switch
    {
        ModuleDecl => "item.module",
        ImportDecl => "item.import",
        AdtDef => "item.type",
        CaseTypeDef => "member.case-type",
        TraitDef => "item.trait",
        InstanceDecl => "item.instance",
        EffectDef => "item.effect",
        FuncDef or FuncDecl => "item.function",
        LetDecl => "item.value",
        Declaration => "item.declaration",
        Pattern => "pattern",
        TypeNode => "type",
        Expression => "expr",
        _ => "body-node"
    };

    internal static string GetCanonicalNodeKind(EidosAstNode node) => node.GetType().Name switch
    {
        "PatternBranch" => "pattern-branch",
        "IdentifierExpr" => "identifier-expression",
        "PathExpr" => "path-expression",
        "LiteralExpr" => "literal-expression",
        "CallExpr" => "call-expression",
        "MethodCallExpr" => "method-call-expression",
        "InfixCallExpr" => "infix-call-expression",
        "UnaryExpr" => "unary-expression",
        "BinaryExpr" => "binary-expression",
        "BlockExpr" => "block-expression",
        "IfExpr" => "if-expression",
        "MatchExpr" => "match-expression",
        "TupleExpr" => "tuple-expression",
        "ListExpr" => "list-expression",
        "CtorExpr" => "constructor-expression",
        "FieldAccessExpr" => "field-access-expression",
        "IndexExpr" => "index-expression",
        "DoExpr" => "do-expression",
        "LambdaExpr" => "lambda-expression",
        "ReturnExpr" => "return-expression",
        "VarPattern" => "binding-pattern",
        "WildcardPattern" => "wildcard-pattern",
        "CtorPattern" => "constructor-pattern",
        "TuplePattern" => "tuple-pattern",
        "LiteralPattern" => "literal-pattern",
        "OrPattern" => "or-pattern",
        "AndPattern" => "and-pattern",
        "GuardPattern" => "guard-pattern",
        _ when node is Pattern => "pattern",
        _ when node is Expression => "expression",
        _ when node is TypeNode => "type-syntax",
        _ when node is Declaration => "declaration",
        _ => "body-node"
    };

    private static string GetDeclarationName(Declaration declaration) => declaration switch
    {
        ModuleDecl module => string.Join(WellKnownStrings.Separators.Path, module.Path),
        AdtDef adt => adt.Name,
        CaseTypeDef caseType => caseType.Name,
        TraitDef trait => trait.Name,
        InstanceDecl instance => instance.Name,
        EffectDef effect => effect.Name,
        FuncDef function => function.Name,
        FuncDecl function => function.Name,
        LetDecl { Pattern: VarPattern variable } => variable.Name,
        _ => "declaration"
    };

    private static ComptimeMetaObjectValue TypedObject(
        string schemaKind,
        string typeName,
        int typeId,
        IReadOnlyList<(string Name, ComptimeValue Value)> properties) =>
        Obj(schemaKind, properties.ToArray()) with { StaticType = MetaSchemaRegistry.MetaType(typeName, typeId) };

    private static string CreateQueryFactFingerprint(
        string name,
        IReadOnlyList<ComptimeValue> arguments,
        MetaComptimeContext meta)
    {
        var facts = new List<string> { name, meta.Access.AvailableStage.ToString() };
        foreach (var argument in arguments)
        {
            if (argument is ComptimeTypeValue { TypeRef.SymbolId.IsValid: true } typeValue &&
                meta.DeclarationDefinitions.TryGetValue(typeValue.TypeRef.SymbolId, out var typeNode))
            {
                facts.Add(CreateDeclarationFact(name, typeNode));
            }
            else if (TryGetDeclarationFromReflectedValue(argument, out var declaration) &&
                meta.DeclarationDefinitions.TryGetValue(declaration.SymbolId, out var node))
            {
                facts.Add(CreateDeclarationFact(name, node));
            }
            else if (TryResolveModuleHandle(argument, meta, out var module))
            {
                facts.Add(string.Join(
                    ",",
                    module.Identity.ToIdentityKey(),
                    string.Join(";", module.Members.Select(id => meta.SymbolTable.GetSymbol(id))
                        .Where(static symbol => symbol != null)
                        .Select(symbol => CreateStableIdentity(symbol!, meta.SymbolTable))
                        .OrderBy(static identity => identity, StringComparer.Ordinal)),
                    string.Join(";", module.Imports
                        .Select(id => meta.SymbolTable.Modules.GetModule(id)?.Identity.ToIdentityKey() ?? "missing")
                        .OrderBy(static identity => identity, StringComparer.Ordinal)),
                    string.Join(";", module.ExportedBindings.Select(static binding => binding.Name).OrderBy(static item => item, StringComparer.Ordinal))));
            }
            else
            {
                facts.Add(argument.CanonicalHash);
            }
        }

        if (name is "references_to" or "callers_of" or "implementations_of")
        {
            facts.AddRange(meta.DeclarationDefinitions.Values
                .Where(static declaration => declaration.SymbolId.IsValid)
                .DistinctBy(static declaration => declaration.SymbolId)
                .Select(declaration => (
                    OrderKey: CreateDeclarationFact("name_of", declaration),
                    Fact: CreateDeclarationFact(name, declaration)))
                .OrderBy(static entry => entry.OrderKey, StringComparer.Ordinal)
                .ThenBy(static entry => entry.Fact, StringComparer.Ordinal)
                .Select(static entry => entry.Fact));
        }

        return Hash(string.Join("|", facts));

        static string CreateDeclarationFact(string queryName, Declaration declaration)
        {
            var document = new XmlDocument();
            var element = declaration.ToXmlElement(document);
            if (!RequiresBodyFact(queryName))
            {
                RemoveDescendants(
                    element,
                    WellKnownStrings.XmlElements.Body,
                    WellKnownStrings.XmlElements.Value,
                    WellKnownStrings.XmlElements.ProofTerm,
                    WellKnownStrings.XmlElements.ProofCases);
            }

            return Hash(element.OuterXml);
        }

        static bool RequiresBodyFact(string queryName) => queryName is
            "body_of" or
            "nodes_of" or
            "value_of" or
            "syntax_of" or
            "calls_from" or
            "callers_of" or
            "references_to";

        static void RemoveDescendants(XmlElement root, params string[] names)
        {
            var removals = names
                .SelectMany(name => root.GetElementsByTagName(name).OfType<XmlNode>())
                .Distinct()
                .ToArray();
            foreach (var removal in removals)
            {
                removal.ParentNode?.RemoveChild(removal);
            }
        }
    }
}
