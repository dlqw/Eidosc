namespace Eidosup.Proxies;

using Eidosup.Configuration;

public enum ToolchainSelectionSource
{
    Explicit,
    Environment,
    ProjectFile,
    DirectoryOverride,
    Default
}

public sealed record ResolvedToolchain(
    string Selector,
    ToolchainSelectionSource SelectionSource,
    string ToolchainId,
    string ToolchainDirectory,
    string CommandPath,
    string RuntimePath,
    string StdlibPath,
    string RootDirectory,
    string? SelectionSourcePath = null,
    bool IsCustom = false,
    ToolchainCompatibilityResult? Compatibility = null);
