using System.Globalization;
using System.Reflection;
using System.Resources;

namespace Eidosc.Doc;

internal static class DocMessages
{
    private static readonly ResourceManager Resources = new(
        "Eidosc.Doc.DocResources",
        Assembly.GetExecutingAssembly());

    public static string MarkdownModuleHeader(string moduleName) =>
        Format(nameof(MarkdownModuleHeader), moduleName);

    public static string MarkdownTypesHeader => Get(nameof(MarkdownTypesHeader));

    public static string MarkdownTraitsHeader => Get(nameof(MarkdownTraitsHeader));

    public static string MarkdownFunctionsHeader => Get(nameof(MarkdownFunctionsHeader));

    public static string MarkdownTypeParameters(string typeParameters) =>
        Format(nameof(MarkdownTypeParameters), typeParameters);

    public static string MarkdownFieldsHeader => Get(nameof(MarkdownFieldsHeader));

    public static string MarkdownFieldsTableHeader => Get(nameof(MarkdownFieldsTableHeader));

    public static string MarkdownConstructorsHeader => Get(nameof(MarkdownConstructorsHeader));

    public static string MarkdownTraitHeader(string traitName) =>
        Format(nameof(MarkdownTraitHeader), traitName);

    public static string MarkdownMethodsHeader => Get(nameof(MarkdownMethodsHeader));

    public static string MarkdownDeprecated(string message) =>
        Format(nameof(MarkdownDeprecated), message);

    public static string MarkdownParametersHeader => Get(nameof(MarkdownParametersHeader));

    public static string MarkdownReturns(string returnType) =>
        Format(nameof(MarkdownReturns), returnType);

    public static string MarkdownExampleHeader => Get(nameof(MarkdownExampleHeader));

    public static string HtmlLanguage => Get(nameof(HtmlLanguage));

    public static string HtmlTitle(string moduleName) =>
        Format(nameof(HtmlTitle), moduleName);

    public static string HtmlModuleHeader(string moduleName) =>
        Format(nameof(HtmlModuleHeader), moduleName);

    public static string HtmlTypesHeader => Get(nameof(HtmlTypesHeader));

    public static string HtmlTraitsHeader => Get(nameof(HtmlTraitsHeader));

    public static string HtmlFunctionsHeader => Get(nameof(HtmlFunctionsHeader));

    public static string HtmlFieldsHeader => Get(nameof(HtmlFieldsHeader));

    public static string HtmlFieldsTableHeader => Get(nameof(HtmlFieldsTableHeader));

    public static string HtmlConstructorsHeader => Get(nameof(HtmlConstructorsHeader));

    public static string HtmlTraitHeader(string traitName) =>
        Format(nameof(HtmlTraitHeader), traitName);

    public static string HtmlMethodsHeader => Get(nameof(HtmlMethodsHeader));

    public static string HtmlDeprecated(string message) =>
        Format(nameof(HtmlDeprecated), message);

    public static string HtmlParametersHeader => Get(nameof(HtmlParametersHeader));

    public static string HtmlReturns(string returnType) =>
        Format(nameof(HtmlReturns), returnType);

    public static string HtmlExampleHeader => Get(nameof(HtmlExampleHeader));

    private static string Get(string name) =>
        Resources.GetString(name, CultureInfo.CurrentUICulture) ?? name;

    private static string Format(string name, params object[] args) =>
        string.Format(CultureInfo.CurrentUICulture, Get(name), args);
}
