using Eidosc.Utils;

namespace Eidosc.Debug;

/// <summary>
/// 控制台调试输出器 - 将调试信息输出到控制台
/// </summary>
public sealed class ConsoleDebugEmitter : IDebugEmitter
{
    private readonly object _lock = new();

    /// <summary>
    /// 调试输出级别
    /// </summary>
    public DebugLevel Level { get; set; } = DebugLevel.Normal;

    /// <summary>
    /// 是否使用彩色输出
    /// </summary>
    public bool UseColors { get; set; } = true;

    /// <summary>
    /// 是否显示时间戳
    /// </summary>
    public bool ShowTimestamp { get; set; } = true;

    public void Emit(string phase, string fileName, string content)
    {
        lock (_lock)
        {
            WriteHeader(phase, fileName);
            WriteLine(content, ConsoleColor.White);
        }
    }

    public void EmitObject(string phase, string fileName, object obj)
    {
        lock (_lock)
        {
            WriteHeader(phase, fileName);
            var json = System.Text.Json.JsonSerializer.Serialize(obj, new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true
            });
            WriteLine(json, ConsoleColor.Cyan);
        }
    }

    public void EmitWithSpan(string phase, string fileName, string message, SourceSpan span)
    {
        lock (_lock)
        {
            WriteHeader(phase, fileName);
            Write(DebugMessages.SourceSpanPrefix(span.Location.Line + 1, span.Location.Column + 1), ConsoleColor.Yellow);
            WriteLine(message, ConsoleColor.White);
        }
    }

    public void BeginPhase(string phase)
    {
        lock (_lock)
        {
            var timestamp = ShowTimestamp ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
            Write($"{timestamp}", ConsoleColor.DarkGray);
            Write("▶ ", ConsoleColor.Green);
            WriteLine(DebugMessages.StartingPhase(phase), ConsoleColor.Yellow);
        }
    }

    public void EndPhase(string phase)
    {
        lock (_lock)
        {
            var timestamp = ShowTimestamp ? $"[{DateTime.Now:HH:mm:ss.fff}] " : "";
            Write($"{timestamp}", ConsoleColor.DarkGray);
            Write("◀ ", ConsoleColor.Green);
            WriteLine(DebugMessages.FinishedPhase(phase), ConsoleColor.Yellow);
        }
    }

    public void Flush()
    {
        // 控制台输出不需要刷新
    }

    private void WriteHeader(string phase, string fileName)
    {
        if (ShowTimestamp)
        {
            Write($"[{DateTime.Now:HH:mm:ss.fff}] ", ConsoleColor.DarkGray);
        }
        Write($"[{phase}] ", ConsoleColor.Blue);
        Write($"{fileName}: ", ConsoleColor.Magenta);
    }

    private void Write(string text, ConsoleColor color)
    {
        if (UseColors)
        {
            var originalColor = System.Console.ForegroundColor;
            System.Console.ForegroundColor = color;
            System.Console.Write(text);
            System.Console.ForegroundColor = originalColor;
        }
        else
        {
            System.Console.Write(text);
        }
    }

    private void WriteLine(string text, ConsoleColor color)
    {
        if (UseColors)
        {
            var originalColor = System.Console.ForegroundColor;
            System.Console.ForegroundColor = color;
            System.Console.WriteLine(text);
            System.Console.ForegroundColor = originalColor;
        }
        else
        {
            System.Console.WriteLine(text);
        }
    }
}
