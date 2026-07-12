namespace Eidosc.Debug;

/// <summary>
/// Specifies the graph artifacts emitted next to textual debug output.
/// </summary>
public enum DebugGraphFormat
{
    /// <summary>No graph artifacts are emitted.</summary>
    None,

    /// <summary>Emits D2 source files.</summary>
    D2,

    /// <summary>Emits standalone SVG files.</summary>
    Svg,

    /// <summary>Emits both D2 source and standalone SVG files.</summary>
    Both
}
