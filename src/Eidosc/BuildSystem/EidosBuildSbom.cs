using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Eidosc.BuildSystem;

public sealed record EidosBuildSbomComponent(
    string Kind,
    string Name,
    string Sha256,
    string Role,
    long? Length = null);

public sealed record EidosBuildSbom(
    int SchemaVersion,
    string Format,
    string SpecVersion,
    string SerialNumber,
    string Name,
    string GraphSha256,
    IReadOnlyList<EidosBuildSbomComponent> Components,
    string CanonicalHash)
{
    public const int CurrentSchemaVersion = 1;
    public const string CycloneDxFormat = "CycloneDX";
    public const string CycloneDxSpecVersion = "1.6";

    public string ToCanonicalJson()
    {
        var payload = new
        {
            bomFormat = Format,
            specVersion = SpecVersion,
            serialNumber = SerialNumber,
            version = 1,
            metadata = new
            {
                component = new Dictionary<string, object>
                {
                    ["type"] = "application",
                    ["bom-ref"] = "eidos:build-graph",
                    ["name"] = Name,
                    ["hashes"] = new[] { new { alg = "SHA-256", content = GraphSha256 } }
                }
            },
            components = Components
                .OrderBy(static component => component.Role, StringComparer.Ordinal)
                .ThenBy(static component => component.Kind, StringComparer.Ordinal)
                .ThenBy(static component => component.Name, StringComparer.Ordinal)
                .Select(static component => new Dictionary<string, object>
                {
                    ["type"] = ToCycloneDxType(component.Kind),
                    ["bom-ref"] = $"eidos:{component.Role}:{component.Kind}:{component.Name}",
                    ["name"] = component.Name,
                    ["hashes"] = new[] { new { alg = "SHA-256", content = component.Sha256 } },
                    ["properties"] = new object?[]
                    {
                        new { name = "eidos:role", value = component.Role },
                        component.Length.HasValue
                            ? new { name = "eidos:length", value = component.Length.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) }
                            : null
                    }.Where(static property => property != null)
                })
        };
        return JsonSerializer.Serialize(
            payload,
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
            });
    }

    internal static EidosBuildSbom Create(
        string targetName,
        string programSha256,
        EidosBuildGraph graph,
        IReadOnlyList<EidosBuildDependency> dependencies,
        IReadOnlyList<EidosBuildOutput> outputs)
    {
        var components = dependencies
            .Select(static dependency => new EidosBuildSbomComponent(
                dependency.Kind,
                dependency.Name,
                dependency.Fingerprint,
                "material",
                dependency.Length))
            .Append(new EidosBuildSbomComponent(
                "program",
                "build.eidos",
                programSha256,
                "build-program"))
            .Concat(outputs.Select(static output => new EidosBuildSbomComponent(
                "output",
                output.Path,
                output.Sha256,
                "subject",
                output.Length)))
            .OrderBy(static component => component.Role, StringComparer.Ordinal)
            .ThenBy(static component => component.Kind, StringComparer.Ordinal)
            .ThenBy(static component => component.Name, StringComparer.Ordinal)
            .ToArray();
        var serialNumber = $"urn:uuid:{CreateDeterministicUuid(Hash(string.Join(
            "\0",
            programSha256,
            graph.CanonicalHash,
            targetName)))}";
        var sbom = new EidosBuildSbom(
            CurrentSchemaVersion,
            CycloneDxFormat,
            CycloneDxSpecVersion,
            serialNumber,
            $"eidos-build-{targetName}",
            graph.CanonicalHash,
            components,
            string.Empty);
        return sbom with { CanonicalHash = Hash(sbom.ToCanonicalJson()) };
    }

    private static string ToCycloneDxType(string kind) => kind switch
    {
        "tool" => "application",
        "file" or "program" or "output" => "file",
        _ => "data"
    };

    private static Guid CreateDeterministicUuid(string sha256)
    {
        var bytes = Convert.FromHexString(sha256[..32]);
        bytes[6] = (byte)((bytes[6] & 0x0f) | 0x50);
        bytes[8] = (byte)((bytes[8] & 0x3f) | 0x80);
        return new Guid(bytes, bigEndian: true);
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
