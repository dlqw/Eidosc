using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Eidosc.BuildSystem;

public sealed record EidosBuildProvenanceMaterial(
    string Uri,
    string Sha256,
    long? Length = null);

public sealed record EidosBuildProvenanceSubject(
    string Name,
    string Sha256,
    long Length);

public sealed record EidosBuildProvenance(
    int SchemaVersion,
    string StatementType,
    string PredicateType,
    string BuildType,
    string BuilderId,
    string InvocationId,
    string HostTriple,
    string TargetTriple,
    string TargetName,
    string ProgramSha256,
    string GraphSha256,
    bool Reproducible,
    IReadOnlyList<string> VolatileCapabilities,
    IReadOnlyList<EidosBuildProvenanceMaterial> Materials,
    IReadOnlyList<EidosBuildProvenanceSubject> Subjects,
    string CanonicalHash)
{
    public const int CurrentSchemaVersion = 1;
    public const string InTotoStatementType = "https://in-toto.io/Statement/v1";
    public const string SlsaPredicateType = "https://slsa.dev/provenance/v1";
    public const string EidosBuildType = "https://eidos.dev/build-host/v1";
    public const string EidosBuilderId = "https://eidos.dev/eidosc/build-host";

    public string ToCanonicalJson()
    {
        var payload = new
        {
            _type = StatementType,
            predicateType = PredicateType,
            subject = Subjects
                .OrderBy(static subject => subject.Name, StringComparer.Ordinal)
                .Select(static subject => new
                {
                    name = subject.Name,
                    digest = new { sha256 = subject.Sha256 },
                    length = subject.Length
                }),
            predicate = new
            {
                buildDefinition = new
                {
                    buildType = BuildType,
                    externalParameters = new
                    {
                        host = HostTriple,
                        target = TargetTriple,
                        targetName = TargetName
                    },
                    internalParameters = new
                    {
                        programSha256 = ProgramSha256,
                        graphSha256 = GraphSha256,
                        reproducible = Reproducible,
                        volatileCapabilities = VolatileCapabilities.Order(StringComparer.Ordinal)
                    },
                    resolvedDependencies = Materials
                        .OrderBy(static material => material.Uri, StringComparer.Ordinal)
                        .Select(static material => new
                        {
                            uri = material.Uri,
                            digest = new { sha256 = material.Sha256 },
                            length = material.Length
                        })
                },
                runDetails = new
                {
                    builder = new { id = BuilderId },
                    metadata = new { invocationId = InvocationId }
                }
            }
        };
        return JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true });
    }

    internal static EidosBuildProvenance Create(
        string hostTriple,
        string targetTriple,
        string targetName,
        string programSha256,
        EidosBuildGraph graph,
        string cacheFingerprint,
        IReadOnlyList<EidosBuildDependency> dependencies,
        IReadOnlyList<EidosBuildOutput> outputs)
    {
        var materials = dependencies
            .Select(static dependency => new EidosBuildProvenanceMaterial(
                $"eidos:{dependency.Kind}:{dependency.Name}",
                dependency.Fingerprint,
                dependency.Length))
            .Append(new EidosBuildProvenanceMaterial(
                "eidos:build-program",
                programSha256))
            .OrderBy(static material => material.Uri, StringComparer.Ordinal)
            .ToArray();
        var subjects = outputs
            .Select(static output => new EidosBuildProvenanceSubject(
                output.Path,
                output.Sha256,
                output.Length))
            .OrderBy(static subject => subject.Name, StringComparer.Ordinal)
            .ToArray();
        var invocationId = Hash(string.Join(
            "\0",
            programSha256,
            graph.CanonicalHash,
            cacheFingerprint,
            hostTriple,
            targetTriple,
            targetName));
        var provenance = new EidosBuildProvenance(
            CurrentSchemaVersion,
            InTotoStatementType,
            SlsaPredicateType,
            EidosBuildType,
            EidosBuilderId,
            invocationId,
            hostTriple,
            targetTriple,
            targetName,
            programSha256,
            graph.CanonicalHash,
            graph.IsReproducible,
            graph.VolatileCapabilities,
            materials,
            subjects,
            string.Empty);
        return provenance with { CanonicalHash = Hash(provenance.ToCanonicalJson()) };
    }

    private static string Hash(string value) => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
