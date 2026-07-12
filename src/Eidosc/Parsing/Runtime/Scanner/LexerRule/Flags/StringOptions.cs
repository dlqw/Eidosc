namespace Eidosc;

[Flags]
public enum StringOptions : short
{
    None = 0,
    IsChar = 0x01,
    AllowsDoubledQuote = 0x02,
    AllowsLineBreak = 0x04,
    NoEscapes = 0x10,
    AllowsUEscapes = 0x20,
    AllowsXEscapes = 0x40,
    AllowsOctalEscapes = 0x80,
    AllowsAllEscapes = AllowsUEscapes | AllowsXEscapes | AllowsOctalEscapes,
}