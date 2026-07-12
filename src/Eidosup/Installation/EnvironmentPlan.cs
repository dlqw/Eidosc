namespace Eidosup.Installation;

public sealed record EnvironmentPlan(
    string EidosHome,
    string EidoscHome,
    string RuntimePath,
    string? LlvmHome,
    IReadOnlyList<string> PathEntries);
