using Eidosc.Utils;

namespace Eidosc.Debug;

/// <summary>
/// 调试上下文 - 在编译阶段中传递
/// </summary>
public sealed class DebugContext
{
    private readonly IDebugEmitter? _emitter;
    private string _currentPhase;
    private readonly Stack<string> _phaseStack = new();

    /// <summary>
    /// 空的调试上下文（不输出任何内容）
    /// </summary>
    public static DebugContext None { get; } = new(null);

    /// <summary>
    /// 调试输出器
    /// </summary>
    public IDebugEmitter? Emitter => _emitter;

    /// <summary>
    /// 当前调试级别
    /// </summary>
    public DebugLevel Level => _emitter?.Level ?? DebugLevel.Minimal;

    /// <summary>
    /// 是否启用调试输出
    /// </summary>
    public bool IsEnabled => _emitter != null;

    /// <summary>
    /// 当前阶段名称
    /// </summary>
    public string CurrentPhase => _currentPhase;

    public DebugContext(IDebugEmitter? emitter, string phase = "")
    {
        _emitter = emitter;
        _currentPhase = phase;
    }

    /// <summary>
    /// 输出日志消息
    /// </summary>
    public void Log(string message)
    {
        if (_emitter == null || Level < DebugLevel.Normal) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _emitter.Emit(_currentPhase, "log", $"[{timestamp}] {message}");
    }

    /// <summary>
    /// 输出详细日志消息
    /// </summary>
    public void LogVerbose(string message)
    {
        if (_emitter == null || Level < DebugLevel.Verbose) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _emitter.Emit(_currentPhase, "verbose", $"[{timestamp}] [V] {message}");
    }

    /// <summary>
    /// 输出诊断信息
    /// </summary>
    public void LogDiagnostic(string message)
    {
        if (_emitter == null || Level < DebugLevel.Diagnostic) return;

        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        _emitter.Emit(_currentPhase, "diagnostic", $"[{timestamp}] [D] {message}");
    }

    /// <summary>
    /// 输出对象（序列化为 JSON）
    /// </summary>
    public void EmitObject(string fileName, object obj)
    {
        if (_emitter == null) return;
        _emitter.EmitObject(_currentPhase, fileName, obj);
    }

    /// <summary>
    /// 输出文本内容
    /// </summary>
    public void Emit(string fileName, string content)
    {
        if (_emitter == null) return;
        _emitter.Emit(_currentPhase, fileName, content);
    }

    /// <summary>
    /// 输出带源码位置的信息
    /// </summary>
    public void EmitWithSpan(string fileName, string message, SourceSpan span)
    {
        if (_emitter == null) return;
        _emitter.EmitWithSpan(_currentPhase, fileName, message, span);
    }

    /// <summary>
    /// 开始一个阶段作用域
    /// </summary>
    public IDisposable PhaseScope(string phaseName)
    {
        if (_emitter == null)
        {
            return new EmptyDisposable();
        }

        _emitter.BeginPhase(phaseName);
        _phaseStack.Push(_currentPhase);
        _currentPhase = phaseName;

        return new PhaseScopeDisposable(this, _phaseStack, _emitter);
    }

    /// <summary>
    /// 创建子阶段上下文
    /// </summary>
    public DebugContext CreatePhaseContext(string phaseName)
    {
        return new DebugContext(_emitter, phaseName);
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }

    private sealed class PhaseScopeDisposable : IDisposable
    {
        private readonly DebugContext _context;
        private readonly Stack<string> _stack;
        private readonly IDebugEmitter _emitter;
        private bool _disposed;

        public PhaseScopeDisposable(DebugContext context, Stack<string> stack, IDebugEmitter emitter)
        {
            _context = context;
            _stack = stack;
            _emitter = emitter;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _emitter.EndPhase(_context._currentPhase);
            if (_stack.Count > 0)
            {
                _context._currentPhase = _stack.Pop();
            }
        }
    }
}
