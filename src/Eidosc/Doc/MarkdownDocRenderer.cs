using System.Text;

namespace Eidosc.Doc;

/// <summary>
/// 将 DocModel 渲染为 Markdown 文档
/// </summary>
public static class MarkdownDocRenderer
{
    public static string Render(DocModule module)
    {
        var sb = new StringBuilder();

        sb.AppendLine(DocMessages.MarkdownModuleHeader(module.Name));
        sb.AppendLine();

        if (!string.IsNullOrEmpty(module.Summary))
        {
            sb.AppendLine(module.Summary);
            sb.AppendLine();
        }

        if (module.Types.Count > 0)
        {
            sb.AppendLine(DocMessages.MarkdownTypesHeader);
            sb.AppendLine();

            foreach (var type in module.Types)
            {
                RenderType(sb, type);
            }
        }

        if (module.Traits.Count > 0)
        {
            sb.AppendLine(DocMessages.MarkdownTraitsHeader);
            sb.AppendLine();

            foreach (var trait in module.Traits)
            {
                RenderTrait(sb, trait);
            }
        }

        if (module.Functions.Count > 0)
        {
            sb.AppendLine(DocMessages.MarkdownFunctionsHeader);
            sb.AppendLine();

            foreach (var func in module.Functions)
            {
                RenderFunction(sb, func);
            }
        }

        return sb.ToString();
    }

    private static void RenderType(StringBuilder sb, DocType type)
    {
        sb.AppendLine($"### `{type.Name}`");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(type.Kind))
        {
            sb.AppendLine($"*{type.Kind}*");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(type.Summary))
        {
            sb.AppendLine(type.Summary);
            sb.AppendLine();
        }

        if (type.TypeParams.Count > 0)
        {
            sb.AppendLine(DocMessages.MarkdownTypeParameters(string.Join(", ", type.TypeParams.Select(t => $"`{t}`"))));
            sb.AppendLine();
        }

        if (type.Fields.Count > 0)
        {
            sb.AppendLine(DocMessages.MarkdownFieldsHeader);
            sb.AppendLine();
            sb.AppendLine(DocMessages.MarkdownFieldsTableHeader);
            sb.AppendLine("|------|------|-------------|");
            foreach (var field in type.Fields)
            {
                var typeStr = field.TypeName != null ? $"`{field.TypeName}`" : "";
                var desc = field.Summary ?? "";
                sb.AppendLine($"| `{field.Name}` | {typeStr} | {desc} |");
            }
            sb.AppendLine();
        }

        if (type.Constructors.Count > 0)
        {
            sb.AppendLine(DocMessages.MarkdownConstructorsHeader);
            sb.AppendLine();
            foreach (var ctor in type.Constructors)
            {
                var paramStr = string.Join(", ", ctor.Parameters.Select(p =>
                    string.IsNullOrEmpty(p.TypeName) ? $"`{p.Name}`" : $"`{p.Name}`: `{p.TypeName}`"));
                sb.AppendLine($"- `{ctor.Name}({paramStr})`");
                if (!string.IsNullOrEmpty(ctor.Summary))
                    sb.AppendLine($"  {ctor.Summary}");
            }
            sb.AppendLine();
        }
    }

    private static void RenderTrait(StringBuilder sb, DocTrait trait)
    {
        sb.AppendLine(DocMessages.MarkdownTraitHeader(trait.Name));
        sb.AppendLine();

        if (!string.IsNullOrEmpty(trait.Summary))
        {
            sb.AppendLine(trait.Summary);
            sb.AppendLine();
        }

        if (trait.Methods.Count > 0)
        {
            sb.AppendLine(DocMessages.MarkdownMethodsHeader);
            sb.AppendLine();
            foreach (var method in trait.Methods)
            {
                sb.AppendLine($"- `{method.Name}`");
                if (!string.IsNullOrEmpty(method.Summary))
                    sb.AppendLine($"  {method.Summary}");
            }
            sb.AppendLine();
        }
    }

    private static void RenderFunction(StringBuilder sb, DocFunction func)
    {
        if (func.Deprecated != null)
            sb.AppendLine(DocMessages.MarkdownDeprecated(func.Deprecated));
        sb.AppendLine($"### `{func.Name}`");
        sb.AppendLine();

        if (!string.IsNullOrEmpty(func.Signature))
        {
            sb.AppendLine("```eidos");
            sb.AppendLine(func.Signature);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(func.Summary))
        {
            sb.AppendLine(func.Summary);
            sb.AppendLine();
        }

        if (func.Parameters.Count > 0)
        {
            sb.AppendLine(DocMessages.MarkdownParametersHeader);
            sb.AppendLine();
            foreach (var param in func.Parameters)
            {
                var typeStr = param.TypeName != null ? $" `{param.TypeName}`" : "";
                var descStr = !string.IsNullOrEmpty(param.Description) ? $" — {param.Description}" : "";
                sb.AppendLine($"- `{param.Name}`{typeStr}{descStr}");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrEmpty(func.ReturnType))
        {
            sb.AppendLine(DocMessages.MarkdownReturns(func.ReturnType));
            sb.AppendLine();
        }

        foreach (var example in func.Examples)
        {
            sb.AppendLine(DocMessages.MarkdownExampleHeader);
            sb.AppendLine("```eidos");
            sb.AppendLine(example);
            sb.AppendLine("```");
            sb.AppendLine();
        }
    }
}
