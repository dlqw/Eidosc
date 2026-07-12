namespace Eidosup.Proxies;

public enum ToolchainSelectionSource
{
    Explicit,
    Default
}

public sealed record ResolvedToolchain(
    string Selector,
    ToolchainSelectionSource SelectionSource,
    string ToolchainId,
    string ToolchainDirectory,
    string CommandPath,
    string RuntimePath,
    string RootDirectory);
