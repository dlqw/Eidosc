using System.Text.Json.Serialization;
using Eidosup.Installation;
using Eidosup.Toolchains;

namespace Eidosup.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true)]
[JsonSerializable(typeof(InstallManifest))]
[JsonSerializable(typeof(ToolchainState))]
internal sealed partial class EidosupJsonContext : JsonSerializerContext;
