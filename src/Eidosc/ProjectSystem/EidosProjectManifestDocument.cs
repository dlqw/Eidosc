using System.Text;
using System.Text.Json;
using Tomlyn;
using Tomlyn.Model;

namespace Eidosc.ProjectSystem;

public sealed class EidosProjectManifestDocument
{
    private static readonly JsonNamingPolicy TomlNamingPolicy = JsonNamingPolicy.CamelCase;

    public int? ManifestSchema { get; set; }
    public EidosProjectLanguageManifestDocument? Language { get; set; }
    public EidosProjectPackageManifestDocument? Package { get; set; }
    public string[]? SourceRoots { get; set; }
    public string[]? ImportRoots { get; set; }
    public string? DefaultTarget { get; set; }
    public string? NativeLinkMode { get; set; }
    public EidosProjectTargetManifestDocument[]? Targets { get; set; }
    public Dictionary<string, EidosProjectDependencyManifestDocument>? Dependencies { get; set; }
    public bool? NoImplicitStdlib { get; set; }
    public EidosProjectBuildManifestDocument? Build { get; set; }
    public EidosProjectFfiManifestDocument? Ffi { get; set; }
    public EidosProjectMetaManifestDocument? Meta { get; set; }

    public static EidosProjectManifestDocument Load(string path)
    {
        var text = ReadAllTextWithTransientRetry(path);
        return Parse(text, path);
    }

    public static EidosProjectManifestDocument Parse(string text, string? sourceName = null)
    {
        RejectRemovedVersionFields(text);
        ThrowIfDuplicateDependencyAliasAcrossForms(text);
        var dependencies = ParseDependencies(text, sourceName);
        var document = TomlSerializer.Deserialize<EidosProjectManifestDocument>(
            NormalizeDependencySyntaxForSerializer(text, sourceName, dependencies),
            CreateTomlOptions(sourceName)) ?? new EidosProjectManifestDocument();
        document.Dependencies = dependencies;
        return document;
    }

    private static void RejectRemovedVersionFields(string text)
    {
        string? section = null;
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line.Trim('[', ']').Trim();
                continue;
            }

            var equalsIndex = line.IndexOf('=');
            var key = equalsIndex > 0 ? line[..equalsIndex].Trim() : string.Empty;
            if ((section == null && string.Equals(key, "eidosVersion", StringComparison.Ordinal)) ||
                (string.Equals(section, "language", StringComparison.Ordinal) &&
                 string.Equals(key, "syntax", StringComparison.Ordinal)))
            {
                throw new TomlException(
                    "Removed manifest version field detected. Use 'manifestSchema = 3' and '[language].version'.");
            }
        }
    }

    private static void ThrowIfDuplicateDependencyAliasAcrossForms(string text)
    {
        var directAliases = new HashSet<string>(StringComparer.Ordinal);
        var tableAliases = new HashSet<string>(StringComparer.Ordinal);
        var insideDependenciesTable = false;

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Split('#', 2)[0].Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                insideDependenciesTable = string.Equals(line, "[dependencies]", StringComparison.Ordinal);
                const string prefix = "[dependencies.";
                if (line.StartsWith(prefix, StringComparison.Ordinal) && line.Length > prefix.Length + 1)
                {
                    tableAliases.Add(line[prefix.Length..^1].Trim().Trim('"'));
                }

                continue;
            }

            if (insideDependenciesTable)
            {
                var equalsIndex = line.IndexOf('=');
                if (equalsIndex > 0)
                {
                    directAliases.Add(line[..equalsIndex].Trim().Trim('"'));
                }
            }
        }

        foreach (var alias in directAliases)
        {
            if (tableAliases.Contains(alias))
            {
                throw new TomlException($"Duplicate dependency alias '{alias}'.");
            }
        }
    }

    public void Save(string path)
    {
        WriteAllTextWithTransientRetry(path, ToToml());
    }

    private static string ReadAllTextWithTransientRetry(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return File.ReadAllText(path);
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
        }
    }

    private static void WriteAllTextWithTransientRetry(string path, string contents)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.WriteAllText(path, contents);
                return;
            }
            catch (IOException) when (attempt < 5)
            {
                Thread.Sleep(20 * (attempt + 1));
            }
        }
    }

    public Dictionary<string, EidosProjectDependencyManifestDocument> GetOrCreateDependencies()
    {
        return Dependencies ??= new Dictionary<string, EidosProjectDependencyManifestDocument>(StringComparer.Ordinal);
    }

    public string ToToml()
    {
        return new Writer(this).Write();
    }

    private static Dictionary<string, EidosProjectDependencyManifestDocument>? ParseDependencies(
        string text,
        string? sourceName)
    {
        var model = TomlSerializer.Deserialize<TomlTable>(text, CreateTomlOptions(sourceName));
        if (model == null)
        {
            return null;
        }

        if (!model.TryGetValue("dependencies", out var dependenciesValue))
        {
            return null;
        }

        if (dependenciesValue is not TomlTable dependenciesTable || dependenciesTable.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, EidosProjectDependencyManifestDocument>(StringComparer.Ordinal);
        foreach (var (name, value) in dependenciesTable)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var dependency = value switch
            {
                string version => new EidosProjectDependencyManifestDocument { Version = version },
                TomlTable table => ParseDependencyTable(table),
                _ => throw new TomlException($"Dependency '{name}' must be a version string or dependency table.")
            };

            if (!result.TryAdd(name, dependency))
            {
                throw new TomlException($"Duplicate dependency alias '{name}'.");
            }
        }

        return result.Count == 0 ? null : result;
    }

    private static string NormalizeDependencySyntaxForSerializer(
        string text,
        string? sourceName,
        Dictionary<string, EidosProjectDependencyManifestDocument>? dependencies)
    {
        if (dependencies == null || dependencies.Count == 0)
        {
            return text;
        }

        var model = TomlSerializer.Deserialize<TomlTable>(text, CreateTomlOptions(sourceName));
        if (model == null)
        {
            return text;
        }

        var dependenciesTable = new TomlTable();
        foreach (var (name, spec) in dependencies)
        {
            var table = new TomlTable();
            Add("path", spec.Path);
            Add("git", spec.Git);
            Add("tag", spec.Tag);
            Add("branch", spec.Branch);
            Add("commit", spec.Commit);
            Add("version", spec.Version);
            Add("target", spec.Target);
            dependenciesTable[name] = table;

            void Add(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    table[key] = value;
                }
            }
        }

        model["dependencies"] = dependenciesTable;
        return TomlSerializer.Serialize(model, CreateTomlOptions(sourceName));
    }

    private static EidosProjectDependencyManifestDocument ParseDependencyTable(TomlTable table)
    {
        return new EidosProjectDependencyManifestDocument
        {
            Path = GetString(table, "path"),
            Git = GetString(table, "git"),
            Tag = GetString(table, "tag"),
            Branch = GetString(table, "branch"),
            Commit = GetString(table, "commit"),
            Version = GetString(table, "version"),
            Target = GetString(table, "target")
        };
    }

    private static string? GetString(TomlTable table, string key)
    {
        return table.TryGetValue(key, out var value) && value is string text
            ? text
            : null;
    }

    private static TomlSerializerOptions CreateTomlOptions(string? sourceName)
    {
        return new TomlSerializerOptions
        {
            PropertyNamingPolicy = TomlNamingPolicy,
            PropertyNameCaseInsensitive = true,
            SourceName = sourceName
        };
    }

    private sealed class Writer(EidosProjectManifestDocument manifest)
    {
        private readonly StringBuilder _builder = new();

        public string Write()
        {
            WriteTopLevel();
            WriteLanguage();
            WritePackage();
            WriteTargets();
            WriteDependencies();
            WriteBuild();
            WriteFfi();
            WriteMeta();

            var text = _builder.ToString().TrimEnd();
            return text.Length == 0 ? string.Empty : text + Environment.NewLine;
        }

        private void WriteTopLevel()
        {
            var wrote = false;
            AppendProperty("manifestSchema", manifest.ManifestSchema ?? 3);
            wrote = true;

            if (manifest.SourceRoots is { Length: > 0 } sourceRoots &&
                !IsDefaultSourceRoots(sourceRoots))
            {
                AppendProperty("sourceRoots", sourceRoots);
                wrote = true;
            }

            if (manifest.ImportRoots is { Length: > 0 } importRoots)
            {
                AppendProperty("importRoots", importRoots);
                wrote = true;
            }

            if (manifest.DefaultTarget != null && !IsDefaultInferredDefaultTarget(manifest))
            {
                AppendProperty("defaultTarget", manifest.DefaultTarget);
                wrote = true;
            }

            if (manifest.NativeLinkMode != null)
            {
                AppendProperty("nativeLinkMode", manifest.NativeLinkMode);
                wrote = true;
            }

            if (manifest.NoImplicitStdlib == true)
            {
                AppendProperty("noImplicitStdlib", true);
                wrote = true;
            }

            if (wrote)
            {
                AppendBlankLine();
            }
        }

        private void WritePackage()
        {
            var package = manifest.Package;
            if (package == null)
            {
                return;
            }

            AppendSection("package");
            AppendOptionalProperty("name", package.Name);
            AppendOptionalProperty("version", package.Version);
            AppendOptionalNonEmptyProperty("description", package.Description);
            AppendOptionalNonEmptyProperty("authors", package.Authors);
            AppendOptionalNonEmptyProperty("license", package.License);
            AppendOptionalNonEmptyProperty("keywords", package.Keywords);
            AppendBlankLine();
        }

        private void WriteLanguage()
        {
            var language = manifest.Language;
            if (language == null)
            {
                return;
            }

            AppendSection("language");
            AppendOptionalProperty("version", language.Version);
            AppendBlankLine();
        }

        private void WriteTargets()
        {
            if (manifest.Targets is not { Length: > 0 } targets)
            {
                return;
            }

            foreach (var target in targets)
            {
                if (IsDefaultInferredTarget(target))
                {
                    continue;
                }

                AppendSection("[targets]");
                AppendOptionalProperty("name", target.Name);
                AppendOptionalProperty("entry", target.Entry);
                if (!IsDefaultExecutableKind(target.Kind))
                {
                    AppendOptionalProperty("kind", target.Kind);
                }

                AppendOptionalNonEmptyProperty("dependencies", target.Dependencies);
                AppendOptionalNonEmptyProperty("projectDependencies", target.ProjectDependencies);
                AppendBlankLine();
            }
        }

        private void WriteDependencies()
        {
            if (manifest.Dependencies is not { Count: > 0 } dependencies)
            {
                return;
            }

            AppendSection("dependencies");
            foreach (var (name, spec) in dependencies.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
            {
                AppendLine($"{FormatKeySegment(name)} = {FormatDependency(spec)}");
            }

            AppendBlankLine();
        }

        private void WriteBuild()
        {
            var build = manifest.Build;
            if (build == null)
            {
                return;
            }

            AppendSection("build");
            AppendOptionalProperty("program", build.Program);
            AppendOptionalProperty("fileInputs", build.FileInputs);
            AppendOptionalProperty("environment", build.Environment);
            AppendOptionalProperty("networkInputs", build.NetworkInputs);
            AppendOptionalProperty("volatileCapabilities", build.VolatileCapabilities);
            AppendOptionalProperty("outputRoots", build.OutputRoots);
            AppendBlankLine();

            foreach (var tool in build.Tools ?? [])
            {
                AppendSection("[build.tools]");
                AppendOptionalProperty("name", tool.Name);
                AppendOptionalProperty("path", tool.Path);
                AppendOptionalProperty("execution", tool.Execution);
                AppendBlankLine();
            }
        }

        private void WriteFfi()
        {
            var ffi = manifest.Ffi;
            if (ffi == null)
            {
                return;
            }

            var wroteFfi = false;
            if (ffi.Libraries != null ||
                ffi.LibraryPaths != null ||
                ffi.IncludePaths != null ||
                ffi.NativeSources != null ||
                ffi.LinkerFlags != null)
            {
                AppendSection("ffi");
                AppendOptionalProperty("libraries", ffi.Libraries);
                AppendOptionalProperty("libraryPaths", ffi.LibraryPaths);
                AppendOptionalProperty("includePaths", ffi.IncludePaths);
                AppendOptionalProperty("nativeSources", ffi.NativeSources);
                AppendOptionalProperty("linkerFlags", ffi.LinkerFlags);
                wroteFfi = true;
            }

            if (ffi.Platform is { Count: > 0 } platform)
            {
                if (wroteFfi)
                {
                    AppendBlankLine();
                }

                AppendSection("ffi.platform");
                foreach (var (name, libraries) in platform.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    AppendOptionalProperty(FormatKeySegment(name), libraries);
                }

                wroteFfi = true;
            }

            if (wroteFfi)
            {
                AppendBlankLine();
            }
        }

        private void WriteMeta()
        {
            var meta = manifest.Meta;
            if (meta == null)
            {
                return;
            }

            if (meta.Checks != null)
            {
                AppendSection("meta");
                AppendOptionalProperty("checks", meta.Checks);
                AppendBlankLine();
            }

            foreach (var extension in meta.Extensions ?? [])
            {
                AppendSection("[meta.extensions]");
                AppendOptionalProperty("name", extension.Name);
                AppendOptionalProperty("entry", extension.Entry);
                AppendOptionalProperty("stage", extension.Stage);
                AppendOptionalProperty("scope", extension.Scope);
                AppendOptionalProperty("inputs", extension.Inputs);
                AppendOptionalProperty("capabilities", extension.Capabilities);
                AppendBlankLine();
            }
        }

        private void AppendOptionalProperty(string name, string? value)
        {
            if (value != null)
            {
                AppendProperty(name, value);
            }
        }

        private void AppendOptionalNonEmptyProperty(string name, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                AppendProperty(name, value);
            }
        }

        private void AppendOptionalNonEmptyProperty(string name, IReadOnlyList<string>? values)
        {
            if (values is { Count: > 0 })
            {
                AppendProperty(name, values);
            }
        }

        private void AppendOptionalProperty(string name, IReadOnlyList<string>? values)
        {
            if (values != null)
            {
                AppendProperty(name, values);
            }
        }

        private void AppendProperty(string name, int value)
        {
            AppendLine($"{name} = {value}");
        }

        private void AppendProperty(string name, bool value)
        {
            AppendLine($"{name} = {value.ToString().ToLowerInvariant()}");
        }

        private void AppendProperty(string name, string value)
        {
            AppendLine($"{name} = {FormatString(value)}");
        }

        private void AppendProperty(string name, IReadOnlyList<string> values)
        {
            AppendLine($"{name} = [{string.Join(", ", values.Select(FormatString))}]");
        }

        private void AppendSection(string name)
        {
            AppendLine($"[{name}]");
        }

        private void AppendBlankLine()
        {
            if (_builder.Length > 0)
            {
                _builder.AppendLine();
            }
        }

        private void AppendLine(string line)
        {
            _builder.Append(line).AppendLine();
        }

        private static string FormatString(string value)
        {
            return JsonSerializer.Serialize(value);
        }

        private static string FormatDependency(EidosProjectDependencyManifestDocument spec)
        {
            if (!string.IsNullOrWhiteSpace(spec.Version) &&
                string.IsNullOrWhiteSpace(spec.Path) &&
                string.IsNullOrWhiteSpace(spec.Git) &&
                string.IsNullOrWhiteSpace(spec.Tag) &&
                string.IsNullOrWhiteSpace(spec.Branch) &&
                string.IsNullOrWhiteSpace(spec.Commit) &&
                string.IsNullOrWhiteSpace(spec.Target))
            {
                return FormatString(spec.Version);
            }

            var parts = new List<string>();
            Append("path", spec.Path);
            Append("git", spec.Git);
            Append("tag", spec.Tag);
            Append("branch", spec.Branch);
            Append("commit", spec.Commit);
            Append("version", spec.Version);
            Append("target", spec.Target);
            return $"{{ {string.Join(", ", parts)} }}";

            void Append(string key, string? value)
            {
                if (!string.IsNullOrWhiteSpace(value))
                {
                    parts.Add($"{key} = {FormatString(value)}");
                }
            }
        }

        private static bool IsDefaultSourceRoots(IReadOnlyList<string> sourceRoots)
        {
            return sourceRoots.Count == 1 &&
                   string.Equals(sourceRoots[0], "src", StringComparison.Ordinal);
        }

        private static bool IsDefaultInferredTarget(EidosProjectTargetManifestDocument target)
        {
            return string.Equals(target.Name, "main", StringComparison.Ordinal) &&
                   string.Equals(target.Entry, "src/main.eidos", StringComparison.Ordinal) &&
                   IsDefaultExecutableKind(target.Kind) &&
                   (target.Dependencies is not { Length: > 0 }) &&
                   (target.ProjectDependencies is not { Length: > 0 });
        }

        private static bool IsDefaultInferredDefaultTarget(EidosProjectManifestDocument manifest)
        {
            return string.Equals(manifest.DefaultTarget, "main", StringComparison.Ordinal) &&
                   manifest.Targets is { Length: 1 } &&
                   IsDefaultInferredTarget(manifest.Targets[0]);
        }

        private static bool IsDefaultExecutableKind(string? kind)
        {
            return string.IsNullOrWhiteSpace(kind) ||
                   string.Equals(kind, "executable", StringComparison.OrdinalIgnoreCase);
        }

        private static string FormatKeySegment(string key)
        {
            if (key.Length > 0 && key.All(IsBareKeyCharacter))
            {
                return key;
            }

            return FormatString(key);
        }

        private static bool IsBareKeyCharacter(char value)
        {
            return value is >= 'A' and <= 'Z' ||
                   value is >= 'a' and <= 'z' ||
                   value is >= '0' and <= '9' ||
                   value is '-' or '_';
        }
    }
}

public sealed class EidosProjectPackageManifestDocument
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Description { get; set; }
    public string[]? Authors { get; set; }
    public string? License { get; set; }
    public string[]? Keywords { get; set; }
}

public sealed class EidosProjectLanguageManifestDocument
{
    public string? Version { get; set; }
}

public sealed class EidosProjectDependencyManifestDocument
{
    public string? Path { get; set; }
    public string? Git { get; set; }
    public string? Tag { get; set; }
    public string? Branch { get; set; }
    public string? Commit { get; set; }
    public string? Version { get; set; }
    public string? Target { get; set; }
}

public sealed class EidosProjectTargetManifestDocument
{
    public string? Name { get; set; }
    public string? Entry { get; set; }
    public string? Kind { get; set; }
    public string[]? Dependencies { get; set; }
    public string[]? ProjectDependencies { get; set; }
}

public sealed class EidosProjectFfiManifestDocument
{
    public string[]? Libraries { get; set; }
    public string[]? LibraryPaths { get; set; }
    public string[]? IncludePaths { get; set; }
    public string[]? NativeSources { get; set; }
    public string[]? LinkerFlags { get; set; }
    public Dictionary<string, string[]>? Platform { get; set; }
}

public sealed class EidosProjectBuildManifestDocument
{
    public string? Program { get; set; }
    public string[]? FileInputs { get; set; }
    public string[]? Environment { get; set; }
    public string[]? NetworkInputs { get; set; }
    public string[]? VolatileCapabilities { get; set; }
    public string[]? OutputRoots { get; set; }
    public EidosProjectBuildToolManifestDocument[]? Tools { get; set; }
}

public sealed class EidosProjectBuildToolManifestDocument
{
    public string? Name { get; set; }
    public string? Path { get; set; }
    public string? Execution { get; set; }
}

public sealed class EidosProjectMetaManifestDocument
{
    public string[]? Checks { get; set; }
    public EidosProjectMetaExtensionManifestDocument[]? Extensions { get; set; }
}

public sealed class EidosProjectMetaExtensionManifestDocument
{
    public string? Name { get; set; }
    public string? Entry { get; set; }
    public string? Stage { get; set; }
    public string? Scope { get; set; }
    public string[]? Inputs { get; set; }
    public string[]? Capabilities { get; set; }
}
