using Eidosc.Utils;

namespace Eidosc.Debug;

/// <summary>
/// 组合调试输出器 - 同时输出到多个目标
/// </summary>
public sealed class CompositeEmitter : IDebugEmitter, IDisposable
{
    private readonly List<IDebugEmitter> _emitters = new();
    private bool _disposed;

    /// <summary>
    /// 调试输出级别（同步到所有子输出器）
    /// </summary>
    public DebugLevel Level
    {
        get => _emitters.Count > 0 ? _emitters[0].Level : DebugLevel.Normal;
        set
        {
            foreach (var emitter in _emitters)
            {
                emitter.Level = value;
            }
        }
    }

    public CompositeEmitter(params IDebugEmitter[] emitters)
    {
        _emitters.AddRange(emitters);
    }

    public CompositeEmitter(IEnumerable<IDebugEmitter> emitters)
    {
        _emitters.AddRange(emitters);
    }

    /// <summary>
    /// 添加输出器
    /// </summary>
    public void AddEmitter(IDebugEmitter emitter)
    {
        _emitters.Add(emitter);
    }

    /// <summary>
    /// 移除输出器
    /// </summary>
    public void RemoveEmitter(IDebugEmitter emitter)
    {
        _emitters.Remove(emitter);
    }

    public void Emit(string phase, string fileName, string content)
    {
        ThrowIfDisposed();
        foreach (var emitter in _emitters)
        {
            emitter.Emit(phase, fileName, content);
        }
    }

    public void EmitObject(string phase, string fileName, object obj)
    {
        ThrowIfDisposed();
        foreach (var emitter in _emitters)
        {
            emitter.EmitObject(phase, fileName, obj);
        }
    }

    public void EmitWithSpan(string phase, string fileName, string message, SourceSpan span)
    {
        ThrowIfDisposed();
        foreach (var emitter in _emitters)
        {
            emitter.EmitWithSpan(phase, fileName, message, span);
        }
    }

    public void BeginPhase(string phase)
    {
        ThrowIfDisposed();
        foreach (var emitter in _emitters)
        {
            emitter.BeginPhase(phase);
        }
    }

    public void EndPhase(string phase)
    {
        ThrowIfDisposed();
        foreach (var emitter in _emitters)
        {
            emitter.EndPhase(phase);
        }
    }

    public void Flush()
    {
        foreach (var emitter in _emitters)
        {
            emitter.Flush();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(CompositeEmitter));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;

        foreach (var emitter in _emitters)
        {
            if (emitter is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
        _emitters.Clear();
        _disposed = true;
    }
}
