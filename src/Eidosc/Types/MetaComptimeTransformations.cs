using Eidosc.Symbols;
using Eidosc.Syntax;

namespace Eidosc.Types;

internal static partial class MetaComptimeIntrinsics
{
    private static bool TryKeepTransformation(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 0)
        {
            return Fail("meta.keep expects no arguments", out value, out reason);
        }

        value = CreateTransformation([]);
        reason = string.Empty;
        return true;
    }

    private static bool TryTargetSyntaxEdit(
        string editKind,
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeMetaObjectValue { SchemaKind: "target" } target ||
            arguments[1] is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } syntax)
        {
            return Fail($"meta.{ToHelperName(editKind)} expects (meta.Target, List[meta.Syntax])", out value, out reason);
        }

        if (!TryReadTargetStage(target, out _))
        {
            return Fail($"meta.{ToHelperName(editKind)} target does not expose a valid stage", out value, out reason);
        }

        var requiredCategory = editKind == "add-members"
            ? SyntaxCategory.Member
            : SyntaxCategory.Item;
        for (var index = 0; index < syntax.Elements.Count; index++)
        {
            var element = syntax.Elements[index];
            if (IsSyntaxForCategory(element, requiredCategory))
            {
                continue;
            }

            return Fail(
                $"meta.{ToHelperName(editKind)} requires meta.Syntax[{requiredCategory}] elements; " +
                $"element {index} has category {DescribeSyntaxCategory(element)}",
                out value,
                out reason);
        }

        if (editKind == "add-members" &&
            (!TryReadTargetCategory(target, out var category) ||
             category is not ("item.type" or "item.trait" or "item.instance" or "item.module" or "member.case-type")))
        {
            return Fail("meta.add_members requires a type, case type, trait, instance, or module target", out value, out reason);
        }

        value = CreateTransformation([
            Obj(
                "transformation-edit",
                ("kind", new ComptimeStringValue(editKind)),
                ("target", target),
                ("syntax", syntax))
        ]);
        reason = string.Empty;
        return true;
    }

    private static bool TryReplaceTarget(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeMetaObjectValue { SchemaKind: "target" } target)
        {
            return Fail("meta.replace_target expects (meta.Target, meta.Syntax[meta.Item|meta.Member])", out value, out reason);
        }

        var replacement = arguments[1];
        var replacementSyntax = UnwrapSlottedOutput(replacement);
        if (replacementSyntax is not ComptimeSyntaxValue &&
            (replacementSyntax is not ComptimeMetaObjectValue structuredReplacement ||
             !IsDeclarationSyntax(structuredReplacement)))
        {
            return Fail(
                $"meta.replace_target requires declaration syntax; received {DescribeSyntaxCategory(replacement)}",
                out value,
                out reason);
        }

        if (!TryReadTargetStage(target, out var stage) || stage == "Layout")
        {
            return Fail("meta.replace_target is not permitted for a Layout target", out value, out reason);
        }

        if (!TryReadTargetCategory(target, out var targetCategory))
        {
            return Fail("meta.replace_target target does not expose a sealed category", out value, out reason);
        }

        var categoryMatches = replacementSyntax switch
        {
            ComptimeSyntaxValue syntax => TryGetTargetSyntaxCategory(targetCategory, out var expectedCategory) &&
                                           syntax.Category == expectedCategory,
            ComptimeMetaObjectValue structured => MatchesTargetCategory(targetCategory, structured.SchemaKind),
            _ => false
        };
        if (!categoryMatches)
        {
            return Fail(
                $"meta.replace_target source category {DescribeSyntaxCategory(replacement)} " +
                $"does not match target category {targetCategory}",
                out value,
                out reason);
        }

        value = CreateTransformation([
            Obj(
                "transformation-edit",
                ("kind", new ComptimeStringValue("replace-target")),
                ("target", target),
                ("syntax", replacement))
        ]);
        reason = string.Empty;
        return true;
    }

    private static bool TryRemoveTarget(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1 ||
            arguments[0] is not ComptimeMetaObjectValue { SchemaKind: "target" } target)
        {
            return Fail("meta.remove_target expects one meta.Target", out value, out reason);
        }

        if (!TryReadTargetStage(target, out var stage) || stage != "Syntax")
        {
            return Fail("meta.remove_target is only permitted for a Syntax target", out value, out reason);
        }

        value = CreateTransformation([
            Obj(
                "transformation-edit",
                ("kind", new ComptimeStringValue("remove-target")),
                ("target", target))
        ]);
        reason = string.Empty;
        return true;
    }

    private static bool TryReportTransformation(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1 ||
            arguments[0] is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } diagnostics ||
            diagnostics.Elements.Any(static element => element is not ComptimeMetaObjectValue { SchemaKind: "diagnostic" }))
        {
            return Fail("meta.report expects List[meta.Diagnostic]", out value, out reason);
        }

        value = CreateTransformation([
            Obj(
                "transformation-edit",
                ("kind", new ComptimeStringValue("report-diagnostic")),
                ("diagnostics", diagnostics))
        ]);
        reason = string.Empty;
        return true;
    }

    private static bool TryAddModuleTransformation(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 2 ||
            arguments[0] is not ComptimeMetaObjectValue { SchemaKind: "package-query" } query ||
            !query.TryGet("capabilities", out var capabilityValue) ||
            capabilityValue is not ComptimeSequenceValue capabilities ||
            !capabilities.Elements.OfType<ComptimeStringValue>()
                .Any(static capability => capability.Value == "emit-modules"))
        {
            return Fail(
                "meta.add_module requires a package query with emit-modules capability",
                out value,
                out reason);
        }

        if (arguments[1] is not ComptimeSyntaxValue { Category: SyntaxCategory.Item } syntax)
        {
            return Fail("meta.add_module expects typed meta.Syntax[meta.Item]", out value, out reason);
        }

        value = CreateTransformation([
            Obj(
                "transformation-edit",
                ("kind", new ComptimeStringValue("add-module")),
                ("query", query),
                ("syntax", syntax))]);
        reason = string.Empty;
        return true;
    }

    private static bool TryAddItemsTransformation(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 3 ||
            arguments[0] is not ComptimeMetaObjectValue { SchemaKind: "package-query" } query ||
            !HasPackageQueryCapability(query, "emit-items"))
        {
            return Fail(
                "meta.add_items requires a package query with emit-items capability",
                out value,
                out reason);
        }

        if (arguments[1] is not ComptimeMetaObjectValue { SchemaKind: "module-handle" } module ||
            !TryReadIdentity(module, "module-handle", out _) ||
            !query.TryGet("package", out var package) ||
            !TryReadIdentity(package, "package-handle", out var packageIdentity) ||
            !module.TryGet("packageIdentity", out var modulePackageValue) ||
            modulePackageValue is not ComptimeStringValue modulePackageIdentity ||
            !string.Equals(modulePackageIdentity.Value, packageIdentity, StringComparison.Ordinal))
        {
            return Fail(
                "meta.add_items requires a module in the package query's current package",
                out value,
                out reason);
        }

        if (arguments[2] is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } items)
        {
            return Fail(
                "meta.add_items expects List[meta.Syntax[meta.Item]]",
                out value,
                out reason);
        }

        for (var index = 0; index < items.Elements.Count; index++)
        {
            if (!IsSyntaxForCategory(items.Elements[index], SyntaxCategory.Item))
            {
                return Fail(
                    $"meta.add_items requires meta.Syntax[Item] elements; element {index} has category " +
                    DescribeSyntaxCategory(items.Elements[index]),
                    out value,
                    out reason);
            }
        }

        value = CreateTransformation([
            Obj(
                "transformation-edit",
                ("kind", new ComptimeStringValue("add-items")),
                ("query", query),
                ("module", module),
                ("syntax", items))]);
        reason = string.Empty;
        return true;
    }

    private static bool HasPackageQueryCapability(ComptimeMetaObjectValue query, string capability) =>
        query.TryGet("capabilities", out var capabilityValue) &&
        capabilityValue is ComptimeSequenceValue capabilities &&
        capabilities.Elements.OfType<ComptimeStringValue>()
            .Any(candidate => string.Equals(candidate.Value, capability, StringComparison.Ordinal));

    private static bool TryCombineTransformations(
        IReadOnlyList<ComptimeValue> arguments,
        out ComptimeValue value,
        out string reason)
    {
        if (arguments.Count != 1 ||
            arguments[0] is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } transformations)
        {
            return Fail("meta.combine expects List[meta.Transformation]", out value, out reason);
        }

        var edits = new List<ComptimeValue>();
        foreach (var transformation in transformations.Elements)
        {
            if (transformation is not ComptimeMetaObjectValue { SchemaKind: "transformation" } objectValue ||
                !objectValue.TryGet("edits", out var editValues) ||
                editValues is not ComptimeSequenceValue { Kind: ComptimeSequenceKind.List } editList)
            {
                return Fail("meta.combine received a value that is not meta.Transformation", out value, out reason);
            }

            edits.AddRange(editList.Elements);
        }

        value = CreateTransformation(edits);
        reason = string.Empty;
        return true;
    }

    private static ComptimeMetaObjectValue CreateTransformation(IEnumerable<ComptimeValue> edits) =>
        Obj("transformation", ("edits", List(edits))) with
        {
            StaticType = MetaSchemaRegistry.MetaType(
                WellKnownStrings.Meta.Types.Transformation,
                WellKnownTypeIds.MetaTransformationId)
        };

    private static bool TryReadTargetStage(ComptimeMetaObjectValue target, out string stage)
    {
        stage = string.Empty;
        if (!target.TryGet("stage", out var value) || value is not ComptimeAdtValue stageValue)
        {
            return false;
        }

        stage = stageValue.ConstructorName;
        return true;
    }

    private static bool TryReadTargetCategory(ComptimeMetaObjectValue target, out string category)
    {
        category = string.Empty;
        if (!target.TryGet("category", out var value) || value is not ComptimeStringValue categoryValue)
        {
            return false;
        }

        category = categoryValue.Value;
        return true;
    }

    private static bool IsDeclarationSyntax(ComptimeMetaObjectValue value) =>
        value.SchemaKind.StartsWith("declaration.", StringComparison.Ordinal);

    private static bool IsSyntaxForCategory(ComptimeValue value, SyntaxCategory category) => value switch
    {
        ComptimeSyntaxValue syntax => syntax.Category == category,
        ComptimeMetaObjectValue { SchemaKind: "slotted-output" } slotted
            when slotted.TryGet("output", out var output) => IsSyntaxForCategory(output, category),
        ComptimeMetaObjectValue structured => IsDeclarationSyntax(structured),
        _ => false
    };

    private static string DescribeSyntaxCategory(ComptimeValue value) => value switch
    {
        ComptimeSyntaxValue syntax => $"meta.Syntax[{syntax.Category}]",
        ComptimeMetaObjectValue structured when IsDeclarationSyntax(structured) => structured.SchemaKind,
        ComptimeMetaObjectValue { SchemaKind: "slotted-output" } slotted
            when slotted.TryGet("output", out var output) => DescribeSyntaxCategory(output),
        ComptimeMetaObjectValue structured => $"meta object '{structured.SchemaKind}'",
        _ => value.GetType().Name
    };

    private static ComptimeValue UnwrapSlottedOutput(ComptimeValue value) =>
        value is ComptimeMetaObjectValue { SchemaKind: "slotted-output" } slotted &&
        slotted.TryGet("output", out var output)
            ? output
            : value;

    private static bool TryGetTargetSyntaxCategory(string targetCategory, out SyntaxCategory category)
    {
        if (targetCategory.StartsWith("item.", StringComparison.Ordinal))
        {
            category = SyntaxCategory.Item;
            return true;
        }

        if (targetCategory.StartsWith("member.", StringComparison.Ordinal))
        {
            category = SyntaxCategory.Member;
            return true;
        }

        category = default;
        return false;
    }

    private static bool MatchesTargetCategory(string targetCategory, string syntaxKind) => targetCategory switch
    {
        "item.type" => syntaxKind is "declaration.type" or "declaration.type-alias",
        "member.case-type" => syntaxKind == "declaration.case-type",
        "item.function" => syntaxKind == "declaration.function",
        "item.trait" => syntaxKind == "declaration.trait",
        "item.instance" => syntaxKind == "declaration.implementation",
        "item.effect" => syntaxKind == "declaration.effect",
        "item.module" => syntaxKind == "declaration.module",
        "item.value" => syntaxKind == "declaration.comptime-value",
        "item.import" => syntaxKind == "declaration.import",
        _ => false
    };

    private static string ToHelperName(string editKind) => editKind.Replace('-', '_');
}
