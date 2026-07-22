using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Eidosc.Ast.Declarations;
using Eidosc.Ast.Patterns;
using Eidosc.CodeGen;
using Eidosc.Diagnostic;
using Eidosc.Pipeline;
using Eidosc.ProjectSystem;
using Eidosc.Types;
using Eidosc.Utils;

namespace Eidosc.BuildSystem;

public sealed class EidosBuildHostOptions
{
    public required string ProjectDirectory { get; init; }
    public required EidosBuildConfiguration Configuration { get; init; }
    public required string LanguageVersion { get; init; }
    public required string TargetName { get; init; }
    public string TargetTriple { get; init; } = TargetInfo.Default.Triple;
    public IReadOnlyList<string> ImportSearchRoots { get; init; } = [];
    public IReadOnlyDictionary<string, string[]> PackageImportRoots { get; init; } =
        new Dictionary<string, string[]>(StringComparer.Ordinal);
    public bool NoImplicitPrelude { get; init; }
    public bool UseCache { get; init; } = true;
    public bool ReleaseProfile { get; init; }
    public bool TraceBuild { get; init; }
    public string? CacheRoot { get; init; }
    public TimeSpan ProcessTimeout { get; init; } = TimeSpan.FromMinutes(10);
    public long ComptimeFuelBudget { get; init; } = ComptimeResourceBudget.DefaultFuel;
    public long ComptimeAllocatedValueBytesBudget { get; init; } = ComptimeResourceBudget.DefaultAllocatedBytes;
    public int ComptimeDiagnosticBudget { get; init; } = ComptimeResourceBudget.DefaultDiagnosticCount;
}

public sealed record EidosBuildDependency(
    string Kind,
    string Name,
    string Fingerprint,
    long? Length = null,
    bool? IsPresent = null);

public sealed record EidosBuildCapabilityTrace(
    long Sequence,
    string Kind,
    string Name,
    string Fingerprint);

public sealed record EidosBuildHostResult(
    bool Success,
    string ProgramPath,
    string ProgramHash,
    string HostTriple,
    string TargetTriple,
    string CacheFingerprint,
    EidosBuildGraph? Graph,
    EidosBuildGraphExecutionResult? Execution,
    IReadOnlyList<EidosBuildDependency> Dependencies,
    EidosBuildProvenance? Provenance,
    EidosBuildSbom? Sbom,
    IReadOnlyList<EidosBuildCapabilityTrace> CapabilityTrace,
    IReadOnlyList<ComptimeTraceEntry> ComptimeTrace,
    IReadOnlyList<string> GeneratedSourceFiles,
    IReadOnlyList<string> GeneratedSourceRoots,
    IReadOnlyDictionary<string, string> GeneratedSourceUris,
    IReadOnlyList<Diagnostic.Diagnostic> Diagnostics);

public static class EidosBuildHost
{
    public static async Task<EidosBuildHostResult> RunAsync(
        EidosBuildHostOptions options,
        CancellationToken cancellationToken = default)
    {
        var projectDirectory = Path.GetFullPath(options.ProjectDirectory);
        var programPath = Path.GetFullPath(options.Configuration.Program);
        var hostTriple = TargetInfo.Default.Triple;
        var diagnostics = new List<Diagnostic.Diagnostic>();

        if (!File.Exists(programPath))
        {
            diagnostics.Add(Error($"Build program '{programPath}' does not exist.", "E5000"));
            return EmptyResult(options, programPath, hostTriple, diagnostics);
        }

        if (!IsPhysicallyContained(projectDirectory, programPath, out var programContainmentReason))
        {
            diagnostics.Add(Error(programContainmentReason, "E5000"));
            return EmptyResult(options, programPath, hostTriple, diagnostics);
        }

        foreach (var outputRoot in options.Configuration.OutputRoots)
        {
            if (!IsPhysicallyContained(projectDirectory, outputRoot, out var outputContainmentReason))
            {
                diagnostics.Add(Error(outputContainmentReason, "E5032"));
            }
        }

        if (diagnostics.Count == 0 &&
            TryResolvePhysicalPath(projectDirectory, programPath, out var physicalProgram, out _) &&
            options.Configuration.OutputRoots.Any(outputRoot =>
                TryResolvePhysicalPath(projectDirectory, outputRoot, out var physicalOutput, out _) &&
                PathsOverlap(physicalProgram, physicalOutput)))
        {
            diagnostics.Add(Error(
                "The build program and build output roots must be physically disjoint.",
                "E5031"));
        }

        if (diagnostics.Count > 0)
        {
            return EmptyResult(options, programPath, hostTriple, diagnostics);
        }

        if (options.ReleaseProfile && options.Configuration.VolatileCapabilities.Length > 0)
        {
            diagnostics.Add(Error(
                $"Release profile rejects volatile build capabilities: {string.Join(", ", options.Configuration.VolatileCapabilities)}.",
                "E5035"));
            return EmptyResult(options, programPath, hostTriple, diagnostics);
        }

        byte[] programBytes;
        try
        {
            programBytes = await File.ReadAllBytesAsync(programPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error($"Build program '{programPath}' could not be read: {ex.Message}", "E5000"));
            return EmptyResult(options, programPath, hostTriple, diagnostics);
        }

        var programHash = HashBytes(programBytes);
        if (!TrySnapshotCapabilities(
                projectDirectory,
                options.Configuration,
                out var files,
                out var environment,
                out var tools,
                out var network,
                out var snapshotDiagnostics))
        {
            diagnostics.AddRange(snapshotDiagnostics);
            return EmptyResult(options, programPath, hostTriple, diagnostics, programHash);
        }

        var capabilityIdentity = ComputeCapabilityIdentity(
            options,
            hostTriple,
            programHash,
            files,
            environment,
            tools,
            network);
        var traceCollector = new ComptimeTraceCollector(options.TraceBuild);
        var buildContext = new BuildComptimeContext(
            projectDirectory,
            hostTriple,
            options.TargetTriple,
            capabilityIdentity,
            files,
            environment,
            tools,
            network,
            options.Configuration.VolatileCapabilities,
            options.Configuration.OutputRoots,
            new ComptimeResourceBudget(
                options.ComptimeFuelBudget,
                options.ComptimeAllocatedValueBytesBudget,
                options.ComptimeDiagnosticBudget),
            traceCollector);

        string sourceText;
        try
        {
            sourceText = new UTF8Encoding(false, true).GetString(programBytes);
        }
        catch (DecoderFallbackException)
        {
            diagnostics.Add(Error($"Build program '{programPath}' is not valid UTF-8.", "E5000"));
            return CreateResult(
                options,
                programPath,
                programHash,
                hostTriple,
                string.Empty,
                null,
                null,
                files,
                environment,
                tools,
                buildContext,
                [],
                diagnostics);
        }

        var importRoots = options.ImportSearchRoots
            .Append(Path.GetDirectoryName(programPath) ?? projectDirectory)
            .Select(Path.GetFullPath)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .ToArray();
        var compilationOptions = new CompilationOptions
        {
            InputFile = programPath,
            LanguageVersion = options.LanguageVersion,
            Target = CompilationTarget.Typed,
            StopAtPhase = CompilationPhase.Types,
            ImportSearchRoots = importRoots,
            PackageImportRoots = options.PackageImportRoots.ToDictionary(
                static entry => entry.Key,
                static entry => entry.Value,
                StringComparer.Ordinal),
            NoImplicitPrelude = options.NoImplicitPrelude,
            TraceComptime = options.TraceBuild,
            ComptimeFuelBudget = options.ComptimeFuelBudget,
            ComptimeAllocatedValueBytesBudget = options.ComptimeAllocatedValueBytesBudget,
            ComptimeDiagnosticBudget = options.ComptimeDiagnosticBudget,
            BuildComptimeContext = buildContext,
            LlvmTargetTriple = options.TargetTriple
        };
        var compilation = new CompilationPipeline(sourceText, compilationOptions).Run();
        if (!compilation.Success)
        {
            diagnostics.AddRange(compilation.Diagnostics);
            return CreateResult(
                options,
                programPath,
                programHash,
                hostTriple,
                string.Empty,
                null,
                null,
                files,
                environment,
                tools,
                buildContext,
                compilation.ComptimeTrace,
                diagnostics);
        }

        if (!TryGetBuildGraphValue(compilation, out var graphValue, out var graphDiagnostic))
        {
            diagnostics.Add(graphDiagnostic);
            return CreateResult(
                options,
                programPath,
                programHash,
                hostTriple,
                string.Empty,
                null,
                null,
                files,
                environment,
                tools,
                buildContext,
                compilation.ComptimeTrace,
                diagnostics);
        }

        if (!EidosBuildGraphMaterializer.TryMaterialize(
                graphValue,
                buildContext,
                options.TargetName,
                out var graph,
                out var graphDiagnostics))
        {
            diagnostics.AddRange(graphDiagnostics);
            return CreateResult(
                options,
                programPath,
                programHash,
                hostTriple,
                string.Empty,
                null,
                null,
                files,
                environment,
                tools,
                buildContext,
                compilation.ComptimeTrace,
                diagnostics);
        }

        var cacheRoot = string.IsNullOrWhiteSpace(options.CacheRoot)
            ? Path.Combine(projectDirectory, "build", ".eidos-cache")
            : Path.GetFullPath(options.CacheRoot);
        var execution = await EidosBuildGraphExecutor.ExecuteAsync(
            graph,
            buildContext,
            cacheRoot,
            options.UseCache,
            options.ProcessTimeout,
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(execution.Diagnostics);
        var cacheFingerprint = execution.Success
            ? ComputeFinalFingerprint(capabilityIdentity, graph, execution.Outputs)
            : string.Empty;
        var generatedArtifacts = graph.Artifacts
            .Where(static artifact => artifact.Kind is "generated-source" or "generated-module")
            .ToArray();
        var generatedSourceFiles = generatedArtifacts
            .Select(artifact => Path.GetFullPath(artifact.Path, projectDirectory))
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        var generatedSourceRoots = generatedArtifacts
            .Select(artifact => string.IsNullOrEmpty(artifact.ImportRoot)
                ? Path.GetDirectoryName(Path.GetFullPath(artifact.Path, projectDirectory))!
                : Path.GetFullPath(artifact.ImportRoot, projectDirectory))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(static path => path, StringComparer.Ordinal)
            .ToArray();
        var generatedSourceUris = generatedArtifacts
            .Where(static artifact => !string.IsNullOrWhiteSpace(artifact.SourceUri))
            .ToDictionary(
                artifact => Path.GetFullPath(artifact.Path, projectDirectory),
                static artifact => artifact.SourceUri,
                OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return CreateResult(
            options,
            programPath,
            programHash,
            hostTriple,
            cacheFingerprint,
            graph,
            execution,
            files,
            environment,
            tools,
            buildContext,
            compilation.ComptimeTrace,
            diagnostics,
            generatedSourceFiles,
            generatedSourceRoots,
            generatedSourceUris);
    }

    private static bool TrySnapshotCapabilities(
        string projectDirectory,
        EidosBuildConfiguration configuration,
        out IReadOnlyList<BuildFileCapability> files,
        out IReadOnlyList<BuildEnvironmentCapability> environment,
        out IReadOnlyList<BuildToolCapability> tools,
        out IReadOnlyList<BuildNetworkCapability> network,
        out IReadOnlyList<Diagnostic.Diagnostic> diagnostics)
    {
        var errors = new List<Diagnostic.Diagnostic>();
        var fileResults = new Dictionary<string, BuildFileCapability>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var input in configuration.FileInputs)
        {
            if (!File.Exists(input) && !Directory.Exists(input))
            {
                errors.Add(Error($"Declared build file input '{input}' does not exist.", "E5030"));
                continue;
            }

            if (!TryResolvePhysicalPath(projectDirectory, input, out var physicalInput, out var inputContainmentReason))
            {
                errors.Add(Error(inputContainmentReason, "E5032"));
                continue;
            }

            var overlapsOutput = false;
            foreach (var outputRoot in configuration.OutputRoots)
            {
                if (!TryResolvePhysicalPath(projectDirectory, outputRoot, out var physicalOutput, out var outputReason))
                {
                    errors.Add(Error(outputReason, "E5032"));
                    overlapsOutput = true;
                    continue;
                }

                if (PathsOverlap(physicalInput, physicalOutput))
                {
                    overlapsOutput = true;
                }
            }

            if (overlapsOutput)
            {
                errors.Add(Error(
                    $"Declared build file input '{input}' overlaps output root; inputs and outputs must be disjoint.",
                    "E5031"));
                continue;
            }

            string[] candidates;
            try
            {
                candidates = File.Exists(input)
                    ? [Path.GetFullPath(input)]
                    : Directory.EnumerateFiles(input, "*", SearchOption.AllDirectories)
                        .Select(Path.GetFullPath)
                        .OrderBy(path => NormalizeRelativePath(projectDirectory, path), StringComparer.Ordinal)
                        .ToArray();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(Error(
                    $"Declared build input directory '{input}' could not be enumerated: {ex.Message}",
                    "E5033"));
                continue;
            }
            foreach (var candidate in candidates)
            {
                if (!IsPhysicallyContained(projectDirectory, candidate, out var containmentReason))
                {
                    errors.Add(Error(containmentReason, "E5032"));
                    continue;
                }

                try
                {
                    var info = new FileInfo(candidate);
                    fileResults[candidate] = new BuildFileCapability(
                        candidate,
                        NormalizeRelativePath(projectDirectory, candidate),
                        HashFile(candidate),
                        info.Length);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    errors.Add(Error($"Declared build input '{candidate}' could not be hashed: {ex.Message}", "E5033"));
                }
            }
        }

        var environmentResults = configuration.Environment
            .Select(name =>
            {
                var value = Environment.GetEnvironmentVariable(name);
                return new BuildEnvironmentCapability(name, value != null, value ?? string.Empty);
            })
            .OrderBy(static value => value.Name, StringComparer.Ordinal)
            .ToArray();
        var toolResults = new List<BuildToolCapability>(configuration.Tools.Length);
        foreach (var tool in configuration.Tools)
        {
            if (!File.Exists(tool.Path))
            {
                errors.Add(Error(
                    $"Registered build tool '{tool.Name}' does not exist at '{tool.Path}'.",
                    "E5034"));
                continue;
            }

            try
            {
                toolResults.Add(new BuildToolCapability(
                    tool.Name,
                    tool.Path,
                    HashFile(tool.Path),
                    tool.Execution));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                errors.Add(Error(
                    $"Registered build tool '{tool.Name}' could not be hashed: {ex.Message}",
                    "E5034"));
            }
        }

        files = fileResults.Values.OrderBy(static file => file.RelativePath, StringComparer.Ordinal).ToArray();
        environment = environmentResults;
        tools = toolResults.OrderBy(static tool => tool.Name, StringComparer.Ordinal).ToArray();
        network = configuration.NetworkInputs
            .Select(static url => new BuildNetworkCapability(url))
            .OrderBy(static capability => capability.Url, StringComparer.Ordinal)
            .ToArray();
        diagnostics = errors;
        return errors.Count == 0;
    }

    private static bool TryGetBuildGraphValue(
        CompilationResult compilation,
        out ComptimeValue value,
        out Diagnostic.Diagnostic diagnostic)
    {
        value = ComptimeUnitValue.Instance;
        var bindings = compilation.Ast?.Declarations
            .OfType<LetDecl>()
            .Where(static declaration =>
                declaration.Pattern is VarPattern { Name: "BuildGraph" })
            .ToArray() ?? [];
        if (bindings.Length != 1)
        {
            var message = bindings.Length == 0
                ? "Build program must declare exactly one top-level 'BuildGraph :: comptime Build.graph(...)' binding."
                : "Build program declares more than one top-level BuildGraph binding.";
            diagnostic = Error(message, "E5002");
            return false;
        }

        var binding = bindings[0];
        if (!binding.IsComptime ||
            !binding.SymbolId.IsValid ||
            compilation.TypeInferer == null ||
            !compilation.TypeInferer.ComptimeValues.TryGetValue(binding.SymbolId, out var resolvedValue))
        {
            diagnostic = Error(
                "BuildGraph must be a compile-time binding whose value is a Build.Graph.",
                "E5002");
            return false;
        }

        value = resolvedValue;
        diagnostic = Diagnostic.Diagnostic.Info(string.Empty);
        return true;
    }

    private static string ComputeCapabilityIdentity(
        EidosBuildHostOptions options,
        string hostTriple,
        string programHash,
        IReadOnlyList<BuildFileCapability> files,
        IReadOnlyList<BuildEnvironmentCapability> environment,
        IReadOnlyList<BuildToolCapability> tools,
        IReadOnlyList<BuildNetworkCapability> network)
    {
        var payload = new
        {
            schemaVersion = WellKnownStrings.Build.SchemaVersion,
            compiler = CompilerBuildIdentity.Current,
            language = options.LanguageVersion,
            program = programHash,
            host = hostTriple,
            target = options.TargetTriple,
            targetName = options.TargetName,
            files = files.Select(static file => new { file.RelativePath, file.Sha256, file.Length }),
            environment = environment.Select(static variable => new
            {
                variable.Name,
                variable.IsPresent,
                valueHash = HashText(variable.Value)
            }),
            tools = tools.Select(static tool => new { tool.Name, tool.FullPath, tool.Sha256, tool.ExecutionPlatform }),
            network = network.Select(static capability => capability.Url),
            volatileCapabilities = options.Configuration.VolatileCapabilities.Order(StringComparer.Ordinal)
        };
        return HashText(JsonSerializer.Serialize(payload));
    }

    private static string ComputeFinalFingerprint(
        string capabilityIdentity,
        EidosBuildGraph graph,
        IReadOnlyList<EidosBuildOutput> outputs)
    {
        var payload = new
        {
            capabilityIdentity,
            graph = graph.CanonicalHash,
            outputs = outputs.OrderBy(static output => output.Path, StringComparer.Ordinal)
        };
        return HashText(JsonSerializer.Serialize(payload));
    }

    private static EidosBuildHostResult CreateResult(
        EidosBuildHostOptions options,
        string programPath,
        string programHash,
        string hostTriple,
        string cacheFingerprint,
        EidosBuildGraph? graph,
        EidosBuildGraphExecutionResult? execution,
        IReadOnlyList<BuildFileCapability> files,
        IReadOnlyList<BuildEnvironmentCapability> environment,
        IReadOnlyList<BuildToolCapability> tools,
        BuildComptimeContext context,
        IReadOnlyList<ComptimeTraceEntry> comptimeTrace,
        IReadOnlyList<Diagnostic.Diagnostic> diagnostics,
        IReadOnlyList<string>? generatedSourceFiles = null,
        IReadOnlyList<string>? generatedSourceRoots = null,
        IReadOnlyDictionary<string, string>? generatedSourceUris = null)
    {
        var accessedNetworkFingerprints = context.Accesses
            .Where(static access => access.Kind == "network")
            .GroupBy(static access => access.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderByDescending(static access => access.Sequence).First().Fingerprint,
                StringComparer.Ordinal);
        var dependencies = files.Select(static file => new EidosBuildDependency(
                "file",
                file.RelativePath,
                file.Sha256,
                file.Length))
            .Concat(environment.Select(static variable => new EidosBuildDependency(
                "environment",
                variable.Name,
                HashText(variable.IsPresent ? $"present\0{variable.Value}" : "absent"),
                IsPresent: variable.IsPresent)))
            .Concat(tools.Select(static tool => new EidosBuildDependency(
                "tool",
                tool.Name,
                HashText($"{tool.ExecutionPlatform}\0{tool.Sha256}"))))
            .Concat(context.NetworkCapabilities.Select(capability => new EidosBuildDependency(
                "network",
                capability.Url,
                accessedNetworkFingerprints.TryGetValue(capability.Url, out var fingerprint)
                    ? fingerprint
                    : HashText(capability.Url))))
            .Concat(context.VolatileCapabilities.Select(static capability => new EidosBuildDependency(
                "volatile",
                capability,
                HashText(capability))))
            .ToArray();
        var provenance = execution?.Success == true && graph != null
            ? EidosBuildProvenance.Create(
                hostTriple,
                options.TargetTriple,
                options.TargetName,
                programHash,
                graph,
                cacheFingerprint,
                dependencies,
                execution.Outputs)
            : null;
        var sbom = execution?.Success == true && graph != null
            ? EidosBuildSbom.Create(
                options.TargetName,
                programHash,
                graph,
                dependencies,
                execution.Outputs)
            : null;
        return new EidosBuildHostResult(
            Success: execution?.Success == true && diagnostics.All(static diagnostic => diagnostic.Level != DiagnosticLevel.Error),
            ProgramPath: programPath,
            ProgramHash: programHash,
            HostTriple: hostTriple,
            TargetTriple: options.TargetTriple,
            CacheFingerprint: cacheFingerprint,
            Graph: graph,
            Execution: execution,
            Dependencies: dependencies,
            Provenance: provenance,
            Sbom: sbom,
            CapabilityTrace: context.Accesses.Select(static access => new EidosBuildCapabilityTrace(
                access.Sequence,
                access.Kind,
                access.Name,
                access.Fingerprint)).ToArray(),
            ComptimeTrace: comptimeTrace,
            GeneratedSourceFiles: generatedSourceFiles ?? [],
            GeneratedSourceRoots: generatedSourceRoots ?? [],
            GeneratedSourceUris: generatedSourceUris ?? new Dictionary<string, string>(),
            Diagnostics: diagnostics);
    }

    private static EidosBuildHostResult EmptyResult(
        EidosBuildHostOptions options,
        string programPath,
        string hostTriple,
        IReadOnlyList<Diagnostic.Diagnostic> diagnostics,
        string programHash = "") => new(
            Success: false,
            ProgramPath: programPath,
            ProgramHash: programHash,
            HostTriple: hostTriple,
            TargetTriple: options.TargetTriple,
            CacheFingerprint: string.Empty,
            Graph: null,
            Execution: null,
            Dependencies: [],
            Provenance: null,
            Sbom: null,
            CapabilityTrace: [],
            ComptimeTrace: [],
            GeneratedSourceFiles: [],
            GeneratedSourceRoots: [],
            GeneratedSourceUris: new Dictionary<string, string>(),
            Diagnostics: diagnostics);

    private static bool IsPhysicallyContained(string projectDirectory, string path, out string reason)
    {
        if (!TryResolvePhysicalPath(projectDirectory, path, out var physicalPath, out reason))
        {
            return false;
        }

        var projectRoot = ResolveExistingLink(new DirectoryInfo(projectDirectory));
        if (!IsWithin(projectRoot, physicalPath))
        {
            reason = $"Declared build path '{path}' resolves outside the project root.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryResolvePhysicalPath(
        string projectDirectory,
        string path,
        out string physicalPath,
        out string reason)
    {
        physicalPath = string.Empty;
        try
        {
            var projectRoot = ResolveExistingLink(new DirectoryInfo(projectDirectory));
            var relative = Path.GetRelativePath(projectDirectory, path);
            var lexicalCurrent = projectDirectory;
            var physicalCurrent = projectRoot;
            foreach (var segment in relative.Split(
                         [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                lexicalCurrent = Path.Combine(lexicalCurrent, segment);
                physicalCurrent = Path.Combine(physicalCurrent, segment);
                FileSystemInfo? info = Directory.Exists(lexicalCurrent)
                    ? new DirectoryInfo(lexicalCurrent)
                    : File.Exists(lexicalCurrent)
                        ? new FileInfo(lexicalCurrent)
                        : null;
                if (info?.LinkTarget != null)
                {
                    physicalCurrent = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                        ?? physicalCurrent;
                }

                if (!IsWithin(projectRoot, physicalCurrent))
                {
                    reason = $"Declared build path '{path}' resolves outside the project root.";
                    return false;
                }
            }

            physicalPath = Path.GetFullPath(physicalCurrent);
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = $"Declared build input '{path}' could not be resolved: {ex.Message}";
            return false;
        }
    }

    private static string ResolveExistingLink(FileSystemInfo info) =>
        info.LinkTarget == null
            ? info.FullName
            : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;

    private static bool PathsOverlap(string left, string right) =>
        IsWithin(left, right) || IsWithin(right, left);

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string NormalizeRelativePath(string projectDirectory, string path) =>
        Path.GetRelativePath(projectDirectory, path).Replace('\\', '/');

    private static string HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string HashBytes(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string HashText(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    private static Diagnostic.Diagnostic Error(string message, string code) =>
        Diagnostic.Diagnostic.Error(message, code).WithLabel(SourceSpan.Empty, message);
}
