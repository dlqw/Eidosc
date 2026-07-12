namespace Eidosc;

[Flags]
public enum NumberOptions
{
    None = 0,
    Default = None,
    AllowStartEndDot = 0x01,
    IntOnly = 0x02,
    NoDotAfterInt = 0x04,
    AllowSign = 0x08,
    DisableQuickParse = 0x10,
    AllowLetterAfter = 0x20,
    AllowUnderscore = 0x40,
    Binary = 0x0100,
    Octal = 0x0200,
    Hex = 0x0400,
}