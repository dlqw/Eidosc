using System.Text.Json;
using Tomlyn;

namespace Eidosc.Bindgen;

public sealed class BindingSpecDocument
{
    private static readonly JsonNamingPolicy TomlNamingPolicy = JsonNamingPolicy.CamelCase;

    public string? Package { get; set; }
    public string? Version { get; set; }
    public string? Library { get; set; }
    public string[]? Headers { get; set; }
    public string[]? IncludePaths { get; set; }
    public string[]? NativeSources { get; set; }
    public string[]? LinkerFlags { get; set; }
    public BindingModuleRule[]? Modules { get; set; }
    public BindingWrapperRule[]? Wrappers { get; set; }
    public BindingEffectRule[]? Effects { get; set; }
    public BindingOwnershipRule[]? Ownership { get; set; }

    public static BindingSpecDocument Load(string path)
    {
        var text = File.ReadAllText(path);
        var doc = TomlSerializer.Deserialize<BindingSpecDocument>(
            text,
            new TomlSerializerOptions
            {
                PropertyNamingPolicy = TomlNamingPolicy,
                PropertyNameCaseInsensitive = true,
                SourceName = path
            }) ?? new BindingSpecDocument();
        doc.Validate(path);
        return doc;
    }

    public string ToToml()
    {
        var lines = new List<string>
        {
            $"package = {FormatString(Package ?? "dev.eidos.binding")}",
            $"version = {FormatString(string.IsNullOrWhiteSpace(Version) ? "0.1.0" : Version!)}",
            $"library = {FormatString(Library ?? "native")}",
            $"headers = {FormatArray(Headers)}"
        };

        AppendArray("includePaths", IncludePaths);
        AppendArray("nativeSources", NativeSources);
        AppendArray("linkerFlags", LinkerFlags);

        foreach (var module in Modules ?? [])
        {
            lines.Add("");
            lines.Add("[[modules]]");
            lines.Add($"name = {FormatString(module.Name ?? "Raw")}");
            if (!string.IsNullOrWhiteSpace(module.Prefix))
                lines.Add($"prefix = {FormatString(module.Prefix!)}");
            if (module.Symbols is { Length: > 0 })
                lines.Add($"symbols = {FormatArray(module.Symbols)}");
        }

        foreach (var ability in Effects ?? [])
        {
            lines.Add("");
            lines.Add("[[abilities]]");
            lines.Add($"name = {FormatString(ability.Name ?? "")}");
            if (ability.Operations is { Length: > 0 })
                lines.Add($"operations = {FormatArray(ability.Operations)}");
        }

        foreach (var wrapper in Wrappers ?? [])
        {
            lines.Add("");
            lines.Add("[[wrappers]]");
            lines.Add($"module = {FormatString(wrapper.Module ?? "Api")}");
            lines.Add($"raw = {FormatString(wrapper.Raw ?? "")}");
            lines.Add($"name = {FormatString(wrapper.Name ?? "")}");
            if (!string.IsNullOrWhiteSpace(wrapper.Signature))
                lines.Add($"signature = {FormatString(wrapper.Signature!)}");
            if (wrapper.Effects is { Length: > 0 })
                lines.Add($"abilities = {FormatArray(wrapper.Effects)}");
        }

        foreach (var ownership in Ownership ?? [])
        {
            lines.Add("");
            lines.Add("[[ownership]]");
            lines.Add($"symbol = {FormatString(ownership.Symbol ?? "")}");
            if (!string.IsNullOrWhiteSpace(ownership.Result))
                lines.Add($"result = {FormatString(ownership.Result!)}");
            if (!string.IsNullOrWhiteSpace(ownership.MustFreeWith))
                lines.Add($"mustFreeWith = {FormatString(ownership.MustFreeWith!)}");
            if (ownership.Nullable.HasValue)
                lines.Add($"nullable = {ownership.Nullable.Value.ToString().ToLowerInvariant()}");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;

        void AppendArray(string name, string[]? values)
        {
            if (values is { Length: > 0 })
                lines.Add($"{name} = {FormatArray(values)}");
        }
    }

    public void Validate(string sourceName)
    {
        if (string.IsNullOrWhiteSpace(Package))
            throw new InvalidOperationException($"{sourceName}: bindgen package is required.");
        if (string.IsNullOrWhiteSpace(Library))
            throw new InvalidOperationException($"{sourceName}: bindgen library is required.");
        if (Headers is not { Length: > 0 })
            throw new InvalidOperationException($"{sourceName}: at least one bindgen header is required.");

        var moduleNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in Modules ?? [])
        {
            if (string.IsNullOrWhiteSpace(module.Name))
                throw new InvalidOperationException($"{sourceName}: module rule requires name.");
            if (!moduleNames.Add(module.Name))
                throw new InvalidOperationException($"{sourceName}: duplicate module rule '{module.Name}'.");
        }

        var abilityNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var ability in Effects ?? [])
        {
            if (string.IsNullOrWhiteSpace(ability.Name))
                throw new InvalidOperationException($"{sourceName}: ability rule requires name.");
            if (!abilityNames.Add(ability.Name))
                throw new InvalidOperationException($"{sourceName}: duplicate ability rule '{ability.Name}'.");
        }

        foreach (var wrapper in Wrappers ?? [])
        {
            if (string.IsNullOrWhiteSpace(wrapper.Module) ||
                string.IsNullOrWhiteSpace(wrapper.Raw) ||
                string.IsNullOrWhiteSpace(wrapper.Name))
            {
                throw new InvalidOperationException($"{sourceName}: wrapper rule requires module, raw, and name.");
            }

            foreach (var ability in wrapper.Effects ?? [])
            {
                if (!abilityNames.Contains(ability))
                    throw new InvalidOperationException($"{sourceName}: wrapper '{wrapper.Name}' references unknown ability '{ability}'.");
            }
        }
    }

    private static string FormatString(string value) =>
        System.Text.Json.JsonSerializer.Serialize(value);

    private static string FormatArray(IReadOnlyList<string>? values) =>
        values is { Count: > 0 }
            ? $"[{string.Join(", ", values.Select(FormatString))}]"
            : "[]";
}

public sealed class BindingModuleRule
{
    public string? Name { get; set; }
    public string? Prefix { get; set; }
    public string[]? Symbols { get; set; }
}

public sealed class BindingWrapperRule
{
    public string? Module { get; set; }
    public string? Raw { get; set; }
    public string? Name { get; set; }
    public string? Signature { get; set; }
    public string[]? Effects { get; set; }
}

public sealed class BindingEffectRule
{
    public string? Name { get; set; }
    public string[]? Operations { get; set; }
}

public sealed class BindingOwnershipRule
{
    public string? Symbol { get; set; }
    public string? Result { get; set; }
    public string? MustFreeWith { get; set; }
    public bool? Nullable { get; set; }
}
