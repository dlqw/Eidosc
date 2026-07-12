namespace Eidosup.Installation;

public sealed record EnvironmentPlan(
    string EidosHome,
    string? LlvmHome,
    IReadOnlyList<string> PathEntries);
