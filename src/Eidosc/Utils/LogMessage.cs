using Eidosc.Utils;

namespace Eidosc.Utilities
{
    public enum ErrorLevel
    {
        Info = 0,
        Warning = 1,
        Error = 2,
    }

    public class LogMessage(ErrorLevel level, SourceLocation location, string message)
    {
        public readonly ErrorLevel Level = level;
        public readonly SourceLocation Location = location;
        public readonly string Message = message;

        public override string ToString()
        {
            return $"[{Level}] {Location} : {Message}";
        }
    }

    public class LogMessageList : List<LogMessage>
    {
        public static int ByLocation(LogMessage x, LogMessage y)
        {
            return SourceLocation.Compare(x.Location, y.Location);
        }
    }
}