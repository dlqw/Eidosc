using System.Text;
using System.Text.Json;
using Eidosc.Utils;

namespace Eidosc.Debug;

/// <summary>
/// 文件调试输出器 - 将调试信息输出到文件系统
/// </summary>
public sealed class FileDebugEmitter : IDebugEmitter, IDisposable
{
    private readonly string _outputDirectory;
    private readonly DebugGraphFormat _graphFormat;
    private readonly Dictionary<string, StringBuilder> _buffers = new();
    private readonly HashSet<string> _createdPhases = new();
    private readonly object _lock = new();
    private bool _disposed;

    /// <summary>
    /// 调试输出级别
    /// </summary>
    public DebugLevel Level { get; set; } = DebugLevel.Normal;

    /// <summary>
    /// 输出目录
    /// </summary>
    public string OutputDirectory => _outputDirectory;

    /// <summary>
    /// JSON 序列化选项
    /// </summary>
    public JsonSerializerOptions JsonOptions { get; } = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="FileDebugEmitter" /> class.
    /// </summary>
    /// <param name="outputDirectory">The output directory for debug artifacts.</param>
    /// <param name="level">One of the enumeration values that specifies the debug verbosity.</param>
    /// <param name="graphFormat">One of the enumeration values that specifies graph artifact generation.</param>
    /// <param name="cleanOutputDirectory"><see langword="true" /> to recreate the output directory before writing; otherwise, <see langword="false" />.</param>
    public FileDebugEmitter(
        string outputDirectory,
        DebugLevel level = DebugLevel.Normal,
        DebugGraphFormat graphFormat = DebugGraphFormat.None,
        bool cleanOutputDirectory = false)
    {
        _outputDirectory = Path.GetFullPath(outputDirectory);
        _graphFormat = graphFormat;
        Level = level;

        if (cleanOutputDirectory && Directory.Exists(_outputDirectory))
        {
            Directory.Delete(_outputDirectory, recursive: true);
        }

        Directory.CreateDirectory(_outputDirectory);
    }

    /// <summary>
    /// 输出文本内容到文件
    /// </summary>
    public void Emit(string phase, string fileName, string content)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            EnsurePhaseDirectory(phase);

            var filePath = GetFilePath(phase, fileName, "txt");
            File.WriteAllText(filePath, content);
            TryWriteGraphArtifacts(filePath, phase, fileName, content);
        }
    }

    /// <summary>
    /// 输出对象到 JSON 文件
    /// </summary>
    public void EmitObject(string phase, string fileName, object obj)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            EnsurePhaseDirectory(phase);

            var json = JsonSerializer.Serialize(obj, JsonOptions);
            var filePath = GetFilePath(phase, fileName, "json");
            File.WriteAllText(filePath, json);
        }
    }

    /// <summary>
    /// 输出带源码位置的信息
    /// </summary>
    public void EmitWithSpan(string phase, string fileName, string message, SourceSpan span)
    {
        var content = DebugMessages.SourceSpanMessage(span.Location.Line + 1, span.Location.Column + 1, message);
        Emit(phase, fileName, content);
    }

    /// <summary>
    /// 开始阶段
    /// </summary>
    public void BeginPhase(string phase)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            EnsurePhaseDirectory(phase);

            var logFile = GetFilePath(phase, "phase_log", "txt");
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            File.AppendAllText(logFile, DebugMessages.PhaseStartedLogLine(timestamp, phase));
        }
    }

    /// <summary>
    /// 结束阶段
    /// </summary>
    public void EndPhase(string phase)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            var logFile = GetFilePath(phase, "phase_log", "txt");
            var timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff");
            File.AppendAllText(logFile, DebugMessages.PhaseEndedLogLine(timestamp, phase));
        }
    }

    /// <summary>
    /// 追加内容到缓冲区（用于大量输出的情况）
    /// </summary>
    public void AppendToBuffer(string phase, string fileName, string content)
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            var key = $"{phase}/{fileName}";
            if (!_buffers.TryGetValue(key, out var buffer))
            {
                buffer = new StringBuilder();
                _buffers[key] = buffer;
            }
            buffer.AppendLine(content);
        }
    }

    /// <summary>
    /// 刷新所有缓冲区到文件
    /// </summary>
    public void Flush()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            foreach (var (key, buffer) in _buffers)
            {
                var parts = key.Split('/');
                if (parts.Length == 2)
                {
                    var phase = parts[0];
                    var fileName = parts[1];

                    EnsurePhaseDirectory(phase);

                    var filePath = GetFilePath(phase, fileName, "txt");
                    File.WriteAllText(filePath, buffer.ToString());
                    TryWriteGraphArtifacts(filePath, phase, fileName, buffer.ToString());
                }
            }
        }
    }

    /// <summary>
    /// 清空指定缓冲区
    /// </summary>
    public void ClearBuffer(string phase, string fileName)
    {
        lock (_lock)
        {
            var key = $"{phase}/{fileName}";
            if (_buffers.TryGetValue(key, out var buffer))
            {
                buffer.Clear();
            }
        }
    }

    /// <summary>
    /// 获取所有已创建的阶段目录
    /// </summary>
    public IReadOnlySet<string> GetCreatedPhases()
    {
        lock (_lock)
        {
            return new HashSet<string>(_createdPhases);
        }
    }

    private void EnsurePhaseDirectory(string phase)
    {
        if (!_createdPhases.Contains(phase))
        {
            var phaseDir = GetPhaseDirectory(phase);
            if (!Directory.Exists(phaseDir))
            {
                Directory.CreateDirectory(phaseDir);
            }
            _createdPhases.Add(phase);
        }
    }

    private string GetPhaseDirectory(string phase)
    {
        // 格式化阶段名称：01_lexer, 02_parser, 等
        var formattedPhase = FormatPhaseName(phase);
        return Path.Combine(_outputDirectory, formattedPhase);
    }

    private string GetFilePath(string phase, string fileName, string extension)
    {
        var phaseDir = GetPhaseDirectory(phase);
        return Path.Combine(phaseDir, $"{fileName}.{extension}");
    }

    private static string FormatPhaseName(string phase)
    {
        // 尝试从阶段名称中提取编号
        // 例如 "01_lexer" -> "01_lexer", "lexer" -> "lexer"
        return phase;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FileDebugEmitter));
        }
    }

    private void TryWriteGraphArtifacts(
        string filePath,
        string phase,
        string fileName,
        string content)
    {
        try
        {
            DebugGraphArtifactWriter.WriteArtifacts(filePath, phase, fileName, content, _graphFormat);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            var errorPath = Path.ChangeExtension(filePath, ".graph_error.txt");
            File.WriteAllText(errorPath, ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        Flush();
        _buffers.Clear();
        _disposed = true;
    }
}
