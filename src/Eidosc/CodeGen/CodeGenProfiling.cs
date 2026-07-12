using System.Diagnostics;

namespace Eidosc.CodeGen;

public sealed class CodeGenProfile
{
    private readonly object _gate = new();
    private readonly List<CodeGenProfileEvent> _events = [];

    public IReadOnlyList<CodeGenProfileEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return _events.ToArray();
            }
        }
    }

    public void Record(
        string category,
        string name,
        string? tool,
        TimeSpan elapsed,
        bool success,
        int? exitCode = null,
        bool cacheHit = false,
        IReadOnlyDictionary<string, string>? metadata = null)
    {
        var profileEvent = new CodeGenProfileEvent
        {
            Category = category,
            Name = name,
            Tool = tool,
            ElapsedMs = elapsed.TotalMilliseconds,
            Success = success,
            ExitCode = exitCode,
            CacheHit = cacheHit,
            Metadata = metadata == null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(metadata, StringComparer.Ordinal)
        };
        lock (_gate)
        {
            _events.Add(profileEvent);
        }
    }

    public IDisposable Measure(
        string category,
        string name,
        string? tool = null,
        Func<bool>? successProvider = null,
        Func<int?>? exitCodeProvider = null,
        bool cacheHit = false)
    {
        return new Scope(this, category, name, tool, successProvider, exitCodeProvider, cacheHit);
    }

    private sealed class Scope : IDisposable
    {
        private readonly CodeGenProfile _owner;
        private readonly string _category;
        private readonly string _name;
        private readonly string? _tool;
        private readonly Func<bool>? _successProvider;
        private readonly Func<int?>? _exitCodeProvider;
        private readonly bool _cacheHit;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private bool _disposed;

        public Scope(
            CodeGenProfile owner,
            string category,
            string name,
            string? tool,
            Func<bool>? successProvider,
            Func<int?>? exitCodeProvider,
            bool cacheHit)
        {
            _owner = owner;
            _category = category;
            _name = name;
            _tool = tool;
            _successProvider = successProvider;
            _exitCodeProvider = exitCodeProvider;
            _cacheHit = cacheHit;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stopwatch.Stop();
            _owner.Record(
                _category,
                _name,
                _tool,
                _stopwatch.Elapsed,
                _successProvider?.Invoke() ?? true,
                _exitCodeProvider?.Invoke(),
                _cacheHit);
        }
    }
}

public sealed class CodeGenProfileEvent
{
    public string Category { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Tool { get; init; }
    public double ElapsedMs { get; init; }
    public bool Success { get; init; }
    public int? ExitCode { get; init; }
    public bool CacheHit { get; init; }
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>(StringComparer.Ordinal);
}
