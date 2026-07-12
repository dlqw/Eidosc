namespace Eidosc.CodeGen;

/// <summary>
/// Controls how native executables are linked when the backend invokes the system linker.
/// </summary>
public enum NativeLinkMode
{
    /// <summary>
    /// Let the target toolchain choose its default executable link mode.
    /// </summary>
    PlatformDefault,

    /// <summary>
    /// Link a non-PIE executable when the target platform supports an explicit flag.
    /// </summary>
    NonPieExecutable,

    /// <summary>
    /// Link a PIE executable when the target platform supports an explicit flag.
    /// </summary>
    PieExecutable
}
