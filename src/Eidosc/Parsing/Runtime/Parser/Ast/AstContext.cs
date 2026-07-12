using Eidosc.Utilities;
using Eidosc.Utils;

namespace Eidosc.Ast;

public class AstContext
{
    public List<StringId> FilePath = [];// todo 这里的Ast是每个编译单元一个
    public LogMessageList Messages = [];

    public void AddMessage(ErrorLevel level, SourceLocation location, string message, params object[] args)
    {
        if (args is { Length: > 0 })
            message = string.Format(message, args);
        Messages.Add(new LogMessage(level, location, message));
    }
}