namespace Eidosc.CodeFormatting;

public sealed record EidosFormatterOptions
{
    public int IndentSize { get; init; } = 4;
    public int MaxLineLength { get; init; } = 100;
    public bool FinalNewline { get; init; } = true;
    public bool ValidateSyntax { get; init; } = true;
    public string LanguageVersion { get; init; } = Eidosc.ProjectSystem.EidosLanguageVersions.DefaultForExistingProjects;
    public string? NewLine { get; init; }
}
