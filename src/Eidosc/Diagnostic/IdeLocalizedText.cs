namespace Eidosc.Diagnostic;

/// <summary>
/// Exposes localized IDE text that is consumed outside the compiler core.
/// </summary>
public static class IdeLocalizedText
{
    /// <summary>
    /// Gets the localized detail text for synthetic module path symbols.
    /// </summary>
    public static string ModulePathDetail => DiagnosticMessages.IdeModulePathDetail;

    /// <summary>
    /// Gets the localized detail text for parameter symbols.
    /// </summary>
    public static string ParameterDetail => DiagnosticMessages.IdeSymbolDetailParameter;

    /// <summary>
    /// Gets the localized detail text for pattern binding symbols.
    /// </summary>
    public static string PatternBindingDetail => DiagnosticMessages.IdeSymbolDetailPatternBinding;

    /// <summary>
    /// Gets the localized detail text for mutable variable symbols.
    /// </summary>
    public static string MutableVariableDetail => DiagnosticMessages.IdeSymbolDetailMutableVariable;

    /// <summary>
    /// Formats the localized default documentation for a function symbol.
    /// </summary>
    /// <param name="name">The symbol name.</param>
    /// <returns>A localized documentation sentence.</returns>
    public static string FunctionDocumentation(string name) => DiagnosticMessages.IdeFunctionDocumentation(name);

    /// <summary>
    /// Formats the localized default documentation for a type symbol.
    /// </summary>
    /// <param name="name">The symbol name.</param>
    /// <returns>A localized documentation sentence.</returns>
    public static string TypeDocumentation(string name) => DiagnosticMessages.IdeTypeDocumentation(name);

    /// <summary>
    /// Formats the localized default documentation for a constructor symbol.
    /// </summary>
    /// <param name="name">The symbol name.</param>
    /// <returns>A localized documentation sentence.</returns>
    public static string ConstructorDocumentation(string name) => DiagnosticMessages.IdeConstructorDocumentation(name);

    /// <summary>
    /// Formats the localized default documentation for a trait symbol.
    /// </summary>
    /// <param name="name">The symbol name.</param>
    /// <returns>A localized documentation sentence.</returns>
    public static string TraitDocumentation(string name) => DiagnosticMessages.IdeTraitDocumentation(name);

    /// <summary>
    /// Formats the localized default documentation for an ability symbol.
    /// </summary>
    /// <param name="name">The symbol name.</param>
    /// <returns>A localized documentation sentence.</returns>
    public static string EffectDocumentation(string name) => DiagnosticMessages.IdeEffectDocumentation(name);

    /// <summary>
    /// Formats the localized default documentation for a proof symbol.
    /// </summary>
    /// <param name="name">The symbol name.</param>
    /// <returns>A localized documentation sentence.</returns>
    public static string ProofDocumentation(string name) => DiagnosticMessages.IdeProofDocumentation(name);

    /// <summary>
    /// Formats the localized default documentation for a type parameter symbol.
    /// </summary>
    /// <param name="name">The symbol name.</param>
    /// <returns>A localized documentation sentence.</returns>
    public static string TypeParameterDocumentation(string name) => DiagnosticMessages.IdeTypeParameterDocumentation(name);

    /// <summary>
    /// Formats the localized default documentation for a module symbol.
    /// </summary>
    /// <param name="name">The symbol name.</param>
    /// <returns>A localized documentation sentence.</returns>
    public static string ModuleDocumentation(string name) => DiagnosticMessages.IdeModuleDocumentation(name);

    /// <summary>
    /// Formats the localized default documentation for a field symbol.
    /// </summary>
    /// <param name="name">The symbol name.</param>
    /// <returns>A localized documentation sentence.</returns>
    public static string FieldDocumentation(string name) => DiagnosticMessages.IdeFieldDocumentation(name);

    /// <summary>
    /// Formats the localized default documentation for a trait implementation symbol.
    /// </summary>
    /// <param name="name">The symbol name.</param>
    /// <returns>A localized documentation sentence.</returns>
    public static string TraitImplementationDocumentation(string name) =>
        DiagnosticMessages.IdeTraitImplementationDocumentation(name);

    /// <summary>
    /// Formats the localized default documentation for a generic value symbol.
    /// </summary>
    /// <param name="name">The symbol name.</param>
    /// <returns>A localized documentation sentence.</returns>
    public static string ValueDocumentation(string name) => DiagnosticMessages.IdeValueDocumentation(name);
}
