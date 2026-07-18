using Eidosc.ProjectSystem;
using Tomlyn;

namespace Eidosc.Bindgen;

public sealed record BindingPackageGenerateOptions(
    string PackageDirectory,
    bool Check,
    bool NoShim);

public sealed record BindingPackageGenerateResult(
    bool Success,
    IReadOnlyList<string> WrittenFiles,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> Diagnostics);

public sealed class BindingPackageGenerator
{
    public const string SpecFileName = "bindgen.toml";

    public BindingPackageGenerateResult Generate(BindingPackageGenerateOptions options)
    {
        var packageDirectory = Path.GetFullPath(options.PackageDirectory);
        var specPath = Path.Combine(packageDirectory, SpecFileName);
        if (!File.Exists(specPath))
        {
            return new BindingPackageGenerateResult(
                false,
                [],
                [],
                [$"Binding spec not found: {specPath}"]);
        }

        try
        {
            var spec = BindingSpecDocument.Load(specPath);
            var generated = GenerateFiles(packageDirectory, spec, options.NoShim);
            var changed = generated
                .Where(file => !File.Exists(file.Path) || File.ReadAllText(file.Path) != file.Content)
                .Select(static file => file.Path)
                .ToArray();

            if (options.Check)
            {
                return new BindingPackageGenerateResult(
                    changed.Length == 0,
                    [],
                    changed,
                    changed.Length == 0 ? [] : ["Binding package generated files are out of date."]);
            }

            foreach (var file in generated)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file.Path) ?? packageDirectory);
                File.WriteAllText(file.Path, file.Content);
            }

            return new BindingPackageGenerateResult(true, generated.Select(static file => file.Path).ToArray(), changed, []);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or TomlException or FileNotFoundException)
        {
            return new BindingPackageGenerateResult(false, [], [], [ex.Message]);
        }
    }

    public void Initialize(
        string outputDirectory,
        string packageName,
        string library,
        IReadOnlyList<string> headers,
        IReadOnlyList<string> includePaths,
        IReadOnlyList<string> nativeSources,
        IReadOnlyList<string> linkerFlags)
    {
        if (headers.Count == 0)
            throw new InvalidOperationException("pkg bind init requires at least one --header.");

        var packageDirectory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(packageDirectory);
        Directory.CreateDirectory(Path.Combine(packageDirectory, "src"));
        Directory.CreateDirectory(Path.Combine(packageDirectory, "native"));

        var spec = new BindingSpecDocument
        {
            Package = packageName,
            Version = "0.1.0",
            Library = library,
            Headers = headers.Select(path => ToPackageRelativePath(packageDirectory, path)).ToArray(),
            IncludePaths = includePaths.Select(path => ToPackageRelativePath(packageDirectory, path)).ToArray(),
            NativeSources = nativeSources.Select(path => ToPackageRelativePath(packageDirectory, path)).ToArray(),
            LinkerFlags = linkerFlags.ToArray(),
            Modules =
            [
                new BindingModuleRule
                {
                    Name = "raw"
                }
            ]
        };

        var specPath = Path.Combine(packageDirectory, SpecFileName);
        if (!File.Exists(specPath))
            File.WriteAllText(specPath, spec.ToToml());
    }

    private IReadOnlyList<GeneratedFile> GenerateFiles(
        string packageDirectory,
        BindingSpecDocument spec,
        bool noShim)
    {
        var headerPath = ResolvePath(packageDirectory, spec.Headers![0]);
        var ir = new SimpleCHeaderParser().Parse(headerPath);
        var rawResult = new RawBindingGenerator(spec, ir).Generate();
        var wrappers = new WrapperBindingGenerator(spec, ir, rawResult.RawFunctionNames).Generate();
        var files = new List<GeneratedFile>
        {
            new(Path.Combine(packageDirectory, EidosProjectConfigurationLoader.DefaultFileName),
                GenerateManifest(spec, noShim ? null : CreateShimPathIfNeeded(ir))),
            new(Path.Combine(packageDirectory, "src", "raw.eidos"), rawResult.Source)
        };

        foreach (var wrapper in wrappers)
        {
            files.Add(new GeneratedFile(
                Path.Combine(
                    packageDirectory,
                    "src",
                    wrapper.ModulePath
                        .Replace('.', Path.DirectorySeparatorChar)
                        .Replace('/', Path.DirectorySeparatorChar) + ".eidos"),
                wrapper.Source));
        }

        if (!noShim)
        {
            var shimGenerator = new BindingCShimGenerator(ir);
            if (shimGenerator.HasShims)
            {
                files.Add(new GeneratedFile(
                    Path.Combine(packageDirectory, "native", $"{spec.Library}_shim.c"),
                    shimGenerator.Generate()));
            }
        }

        return files;

        string? CreateShimPathIfNeeded(CHeaderIr headerIr)
        {
            var shimGenerator = new BindingCShimGenerator(headerIr);
            return shimGenerator.HasShims
                ? $"native/{spec.Library}_shim.c"
                : null;
        }
    }

    private static string GenerateManifest(BindingSpecDocument spec, string? generatedShim)
    {
        var nativeSources = (spec.NativeSources ?? [])
            .Concat(generatedShim != null ? [generatedShim] : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        string[]? linkLibraries = nativeSources.Length == 0 && !string.IsNullOrWhiteSpace(spec.Library)
            ? [spec.Library!]
            : null;
        var manifest = new EidosProjectManifestDocument
        {
            Package = new EidosProjectPackageManifestDocument
            {
                Name = spec.Package,
                Version = string.IsNullOrWhiteSpace(spec.Version) ? "0.1.0" : spec.Version
            },
            Targets =
            [
                new EidosProjectTargetManifestDocument
                {
                    Name = "lib",
                    Entry = "src/raw.eidos",
                    Kind = "library"
                }
            ],
            Ffi = new EidosProjectFfiManifestDocument
            {
                Libraries = linkLibraries,
                IncludePaths = spec.IncludePaths,
                NativeSources = nativeSources.Length == 0 ? null : nativeSources,
                LinkerFlags = spec.LinkerFlags
            }
        };

        return manifest.ToToml();
    }

    private static string ResolvePath(string packageDirectory, string path) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(packageDirectory, path));

    private static string ToPackageRelativePath(string packageDirectory, string path)
    {
        var fullPath = Path.GetFullPath(path);
        return Path.GetRelativePath(packageDirectory, fullPath).Replace('\\', '/');
    }

    private sealed record GeneratedFile(string Path, string Content);
}
