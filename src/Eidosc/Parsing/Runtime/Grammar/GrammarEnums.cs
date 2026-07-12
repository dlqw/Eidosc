namespace Eidosc
{
    [Flags]
    public enum TermListOptions
    {
        None = 0,
        AllowEmpty = 0x01,
        AllowTrailingDelimiter = 0x02,

        AddPreferShiftHint = 0x04,
        PlusList = AddPreferShiftHint,
        StarList = AllowEmpty | AddPreferShiftHint,
    }
}