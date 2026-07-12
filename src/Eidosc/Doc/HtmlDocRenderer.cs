using System.Text;
using System.Text.Encodings.Web;

namespace Eidosc.Doc;

/// <summary>
/// 将 DocModel 渲染为 HTML 文档
/// </summary>
public static class HtmlDocRenderer
{
    public static string Render(DocModule module)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine($"<html lang=\"{HtmlEncode(DocMessages.HtmlLanguage)}\">");
        sb.AppendLine("<head>");
        sb.AppendLine("<meta charset=\"utf-8\">");
        sb.AppendLine($"<title>{DocMessages.HtmlTitle(HtmlEncode(module.Name))}</title>");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; max-width: 960px; margin: 0 auto; padding: 2rem; color: #333; }");
        sb.AppendLine("h1 { border-bottom: 2px solid #e0e0e0; padding-bottom: 0.5rem; }");
        sb.AppendLine("h2 { margin-top: 2rem; color: #2a6496; }");
        sb.AppendLine("h3 { margin-top: 1.5rem; }");
        sb.AppendLine("h4 { margin-top: 1rem; color: #555; }");
        sb.AppendLine("code { background: #f5f5f5; padding: 0.15em 0.4em; border-radius: 3px; font-size: 0.9em; }");
        sb.AppendLine("pre { background: #f5f5f5; padding: 1rem; border-radius: 5px; overflow-x: auto; }");
        sb.AppendLine("pre code { background: none; padding: 0; }");
        sb.AppendLine("table { border-collapse: collapse; width: 100%; margin: 1rem 0; }");
        sb.AppendLine("th, td { border: 1px solid #ddd; padding: 0.5rem 0.75rem; text-align: left; }");
        sb.AppendLine("th { background: #f5f5f5; }");
        sb.AppendLine(".deprecated { background: #fff3cd; padding: 0.5rem 1rem; border-radius: 3px; border-left: 3px solid #ffc107; }");
        sb.AppendLine(".signature { font-family: 'SFMono-Regular', Consolas, monospace; }");
        sb.AppendLine("</style>");
        sb.AppendLine("</head>");
        sb.AppendLine("<body>");

        sb.AppendLine($"<h1>{DocMessages.HtmlModuleHeader(HtmlEncode(module.Name))}</h1>");

        if (!string.IsNullOrEmpty(module.Summary))
        {
            sb.AppendLine($"<p>{HtmlEncode(module.Summary)}</p>");
        }

        if (module.Types.Count > 0)
        {
            sb.AppendLine($"<h2>{DocMessages.HtmlTypesHeader}</h2>");
            foreach (var type in module.Types)
                RenderType(sb, type);
        }

        if (module.Traits.Count > 0)
        {
            sb.AppendLine($"<h2>{DocMessages.HtmlTraitsHeader}</h2>");
            foreach (var trait in module.Traits)
                RenderTrait(sb, trait);
        }

        if (module.Functions.Count > 0)
        {
            sb.AppendLine($"<h2>{DocMessages.HtmlFunctionsHeader}</h2>");
            foreach (var func in module.Functions)
                RenderFunction(sb, func);
        }

        sb.AppendLine("</body>");
        sb.AppendLine("</html>");

        return sb.ToString();
    }

    private static void RenderType(StringBuilder sb, DocType type)
    {
        sb.AppendLine($"<h3 id=\"type-{HtmlId(type.Name)}\"><code>{HtmlEncode(type.Name)}</code></h3>");

        if (!string.IsNullOrEmpty(type.Kind))
            sb.AppendLine($"<p><em>{HtmlEncode(type.Kind)}</em></p>");

        if (!string.IsNullOrEmpty(type.Summary))
            sb.AppendLine($"<p>{HtmlEncode(type.Summary)}</p>");

        if (type.Fields.Count > 0)
        {
            sb.AppendLine($"<h4>{DocMessages.HtmlFieldsHeader}</h4>");
            sb.AppendLine(DocMessages.HtmlFieldsTableHeader);
            foreach (var field in type.Fields)
            {
                var typeStr = field.TypeName != null ? $"<code>{HtmlEncode(field.TypeName)}</code>" : "";
                sb.AppendLine($"<tr><td><code>{HtmlEncode(field.Name)}</code></td><td>{typeStr}</td><td>{HtmlEncode(field.Summary ?? "")}</td></tr>");
            }
            sb.AppendLine("</table>");
        }

        if (type.Constructors.Count > 0)
        {
            sb.AppendLine($"<h4>{DocMessages.HtmlConstructorsHeader}</h4>");
            sb.AppendLine("<ul>");
            foreach (var ctor in type.Constructors)
            {
                var paramStr = string.Join(", ", ctor.Parameters.Select(p =>
                {
                    var t = p.TypeName != null ? $": <code>{HtmlEncode(p.TypeName)}</code>" : "";
                    return $"<code>{HtmlEncode(p.Name)}</code>{t}";
                }));
                sb.AppendLine($"<li><code>{HtmlEncode(ctor.Name)}({paramStr})</code></li>");
            }
            sb.AppendLine("</ul>");
        }
    }

    private static void RenderTrait(StringBuilder sb, DocTrait trait)
    {
        sb.AppendLine($"<h3 id=\"trait-{HtmlId(trait.Name)}\">{DocMessages.HtmlTraitHeader(HtmlEncode(trait.Name))}</h3>");

        if (!string.IsNullOrEmpty(trait.Summary))
            sb.AppendLine($"<p>{HtmlEncode(trait.Summary)}</p>");

        if (trait.Methods.Count > 0)
        {
            sb.AppendLine($"<h4>{DocMessages.HtmlMethodsHeader}</h4><ul>");
            foreach (var method in trait.Methods)
                sb.AppendLine($"<li><code>{HtmlEncode(method.Name)}</code></li>");
            sb.AppendLine("</ul>");
        }
    }

    private static void RenderFunction(StringBuilder sb, DocFunction func)
    {
        if (func.Deprecated != null)
            sb.AppendLine(DocMessages.HtmlDeprecated(HtmlEncode(func.Deprecated)));

        sb.AppendLine($"<h3 id=\"fn-{HtmlId(func.Name)}\"><code>{HtmlEncode(func.Name)}</code></h3>");

        if (!string.IsNullOrEmpty(func.Signature))
        {
            sb.AppendLine("<pre class=\"signature\"><code>");
            sb.AppendLine(HtmlEncode(func.Signature));
            sb.AppendLine("</code></pre>");
        }

        if (!string.IsNullOrEmpty(func.Summary))
            sb.AppendLine($"<p>{HtmlEncode(func.Summary)}</p>");

        if (func.Parameters.Count > 0)
        {
            sb.AppendLine($"<h4>{DocMessages.HtmlParametersHeader}</h4><ul>");
            foreach (var param in func.Parameters)
            {
                var t = param.TypeName != null ? $": <code>{HtmlEncode(param.TypeName)}</code>" : "";
                var d = param.Description != null ? $" — {HtmlEncode(param.Description)}" : "";
                sb.AppendLine($"<li><code>{HtmlEncode(param.Name)}</code>{t}{d}</li>");
            }
            sb.AppendLine("</ul>");
        }

        if (!string.IsNullOrEmpty(func.ReturnType))
            sb.AppendLine(DocMessages.HtmlReturns(HtmlEncode(func.ReturnType)));

        foreach (var example in func.Examples)
        {
            sb.AppendLine($"<h4>{DocMessages.HtmlExampleHeader}</h4><pre><code>");
            sb.AppendLine(HtmlEncode(example));
            sb.AppendLine("</code></pre>");
        }
    }

    private static string HtmlEncode(string text) => HtmlEncoder.Default.Encode(text);

    private static string HtmlId(string name) => name.Replace(' ', '-').ToLowerInvariant();
}
