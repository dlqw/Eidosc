using System.Security.Cryptography;
using System.Text;
using Eidosc.Symbols;
using Eidosc.Utils;

namespace Eidosc.Types;

internal sealed record BuildFileCapability(
    string FullPath,
    string RelativePath,
    string Sha256,
    long Length);

internal sealed record BuildEnvironmentCapability(
    string Name,
    bool IsPresent,
    string Value);

internal sealed record BuildToolCapability(
    string Name,
    string FullPath,
    string Sha256,
    string ExecutionPlatform);

internal sealed record BuildNetworkCapability(string Url);

internal sealed record BuildCapabilityAccess(
    long Sequence,
    string Kind,
    string Name,
    string Fingerprint);

internal sealed class BuildComptimeContext
{
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Dictionary<string, BuildFileCapability> _files;
    private readonly Dictionary<string, BuildEnvironmentCapability> _environment;
    private readonly Dictionary<string, BuildToolCapability> _tools;
    private readonly Dictionary<string, BuildNetworkCapability> _network;
    private readonly List<BuildCapabilityAccess> _accesses = [];
    private readonly object _accessGate = new();
    private long _nextAccessSequence;

    public BuildComptimeContext(
        string projectDirectory,
        string hostTriple,
        string targetTriple,
        string capabilityIdentity,
        IReadOnlyList<BuildFileCapability> files,
        IReadOnlyList<BuildEnvironmentCapability> environment,
        IReadOnlyList<BuildToolCapability> tools,
        IReadOnlyList<BuildNetworkCapability> network,
        IReadOnlyList<string> volatileCapabilities,
        IReadOnlyList<string> outputRoots,
        ComptimeResourceBudget resourceBudget,
        ComptimeTraceCollector? trace = null,
        string tracePhase = "build.comptime")
    {
        ProjectDirectory = Path.GetFullPath(projectDirectory);
        HostTriple = hostTriple;
        TargetTriple = targetTriple;
        CapabilityIdentity = capabilityIdentity;
        OutputRoots = outputRoots.Select(Path.GetFullPath).ToArray();
        Resources = resourceBudget;
        Trace = trace;
        TracePhase = tracePhase;

        var pathComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        PathComparer = pathComparer;
        var environmentComparer = OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
        _files = files.ToDictionary(static file => file.FullPath, pathComparer);
        _environment = environment.ToDictionary(static variable => variable.Name, environmentComparer);
        _tools = tools.ToDictionary(static tool => tool.Name, StringComparer.Ordinal);
        _network = network.ToDictionary(static capability => capability.Url, StringComparer.Ordinal);
        VolatileCapabilities = volatileCapabilities.Order(StringComparer.Ordinal).ToArray();
    }

    public string ProjectDirectory { get; }
    public string HostTriple { get; }
    public string TargetTriple { get; }
    public string CapabilityIdentity { get; }
    public StringComparer PathComparer { get; }
    public IReadOnlyList<string> OutputRoots { get; }
    public IReadOnlyList<string> VolatileCapabilities { get; }
    public bool IsReproducible => VolatileCapabilities.Count == 0;
    public IReadOnlyList<BuildFileCapability> DeclaredFiles => _files.Values
        .OrderBy(static file => file.RelativePath, StringComparer.Ordinal)
        .ToArray();
    public IReadOnlyList<BuildEnvironmentCapability> EnvironmentCapabilities => _environment.Values
        .OrderBy(static variable => variable.Name, StringComparer.Ordinal)
        .ToArray();
    public IReadOnlyList<BuildToolCapability> ToolCapabilities => _tools.Values
        .OrderBy(static tool => tool.Name, StringComparer.Ordinal)
        .ToArray();
    public IReadOnlyList<BuildNetworkCapability> NetworkCapabilities => _network.Values
        .OrderBy(static capability => capability.Url, StringComparer.Ordinal)
        .ToArray();
    public ComptimeResourceBudget Resources { get; }
    public ComptimeTraceCollector? Trace { get; }
    public string TracePhase { get; }
    public SymbolTable? SymbolTable { get; private set; }

    public void AttachSymbolTable(SymbolTable symbolTable)
    {
        SymbolTable = symbolTable;
    }

    public IReadOnlyList<BuildCapabilityAccess> Accesses
    {
        get
        {
            lock (_accessGate)
            {
                return _accesses.ToArray();
            }
        }
    }

    public bool TryReadText(string path, out string text, out string reason)
    {
        text = string.Empty;
        if (!TryResolveProjectPath(path, out var fullPath, out reason))
        {
            return false;
        }

        if (!_files.TryGetValue(fullPath, out var capability))
        {
            reason = $"BuildFs denied undeclared file input '{NormalizeDisplayPath(path)}'";
            return false;
        }

        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(capability.FullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"BuildFs could not read declared input '{capability.RelativePath}': {ex.Message}";
            return false;
        }

        var currentHash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(currentHash, capability.Sha256, StringComparison.Ordinal))
        {
            reason = $"BuildFs input '{capability.RelativePath}' changed while the build program was executing";
            return false;
        }

        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            reason = $"BuildFs input '{capability.RelativePath}' is not valid UTF-8 text";
            return false;
        }

        RecordAccess("file", capability.RelativePath, capability.Sha256);
        reason = string.Empty;
        return true;
    }

    public bool TryReadEnvironment(string name, out string value, out string reason)
    {
        value = string.Empty;
        if (!_environment.TryGetValue(name, out var capability))
        {
            reason = $"BuildEnv denied undeclared environment variable '{name}'";
            return false;
        }

        var fingerprint = capability.IsPresent
            ? HashText($"present\0{capability.Value}")
            : HashText("absent");
        RecordAccess("environment", capability.Name, fingerprint);
        if (!capability.IsPresent)
        {
            reason = $"Declared build environment variable '{capability.Name}' is not set";
            return false;
        }

        value = capability.Value;
        reason = string.Empty;
        return true;
    }

    public bool TryGetTool(string name, out BuildToolCapability tool, out string reason)
    {
        if (!_tools.TryGetValue(name, out tool!))
        {
            reason = $"BuildProcess denied unregistered tool '{name}'";
            return false;
        }

        RecordAccess("tool", tool.Name, tool.Sha256);
        reason = string.Empty;
        return true;
    }

    public bool TryGetHostTool(string name, out BuildToolCapability tool, out string reason)
    {
        if (!TryGetTool(name, out tool, out reason))
        {
            return false;
        }

        if (!string.Equals(tool.ExecutionPlatform, "host", StringComparison.Ordinal))
        {
            reason = $"BuildProcess cannot execute target tool '{name}' on host '{HostTriple}'";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public bool TryAuthorizeNetworkFetch(string url, string sha256, out string reason)
    {
        if (!_network.ContainsKey(url))
        {
            reason = $"BuildNetwork denied undeclared URL '{url}'";
            return false;
        }

        if (!IsSha256(sha256))
        {
            reason = "BuildNetwork fetch requires a lowercase 64-hex SHA-256 digest";
            return false;
        }

        RecordAccess("network", url, HashText($"{url}\0{sha256}"));
        reason = string.Empty;
        return true;
    }

    public bool TryResolveProjectPath(string path, out string fullPath, out string reason)
    {
        fullPath = string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            reason = "Build path cannot be empty";
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path, ProjectDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            reason = $"Invalid Build path '{path}': {ex.Message}";
            return false;
        }

        var relative = Path.GetRelativePath(ProjectDirectory, fullPath);
        if (Path.IsPathRooted(relative) ||
            relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
            relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
        {
            reason = $"Build path '{NormalizeDisplayPath(path)}' escapes the project root";
            fullPath = string.Empty;
            return false;
        }


        if (!TryValidatePhysicalContainment(fullPath, out reason))
        {
            fullPath = string.Empty;
            return false;
        }

        reason = string.Empty;
        return true;
    }

    public string ToRelativePath(string fullPath) =>
        Path.GetRelativePath(ProjectDirectory, Path.GetFullPath(fullPath)).Replace('\\', '/');

    private void RecordAccess(string kind, string name, string fingerprint)
    {
        lock (_accessGate)
        {
            _accesses.Add(new BuildCapabilityAccess(
                ++_nextAccessSequence,
                kind,
                name,
                fingerprint));
        }
    }

    private static string NormalizeDisplayPath(string path) => path.Replace('\\', '/');

    private static bool IsSha256(string value) =>
        value.Length == 64 && value.All(static character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private bool TryValidatePhysicalContainment(string fullPath, out string reason)
    {
        try
        {
            var projectRoot = ResolveExistingLink(new DirectoryInfo(ProjectDirectory));
            var physicalCurrent = projectRoot;
            var lexicalCurrent = ProjectDirectory;
            var relative = Path.GetRelativePath(ProjectDirectory, fullPath);
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
                    reason = $"Build path '{ToRelativePath(fullPath)}' resolves outside the project root";
                    return false;
                }
            }

            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            reason = $"Build path '{NormalizeDisplayPath(fullPath)}' could not be physically resolved: {ex.Message}";
            return false;
        }
    }

    private static string ResolveExistingLink(FileSystemInfo info) =>
        info.LinkTarget == null
            ? info.FullName
            : info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName;

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) &&
               !relative.Equals("..", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) &&
               !relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal);
    }

    private static string HashText(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
