namespace Eidosc.Tests.Fixtures;

public sealed class TestTempWorkspace : IDisposable
{
    private const int DeleteAttempts = 5;

    private bool _disposed;

    private TestTempWorkspace(string root)
    {
        Root = root;
    }

    public string Root { get; }

    public static TestTempWorkspace Create(string prefix = "eidosc_test")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        var safePrefix = string.Concat(prefix.Select(static ch =>
            char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_'));
        var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"{safePrefix}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return new TestTempWorkspace(root);
    }

    public string Path(params string[] segments)
    {
        if (segments.Length == 0)
        {
            return Root;
        }

        var parts = new string[segments.Length + 1];
        parts[0] = Root;
        Array.Copy(segments, 0, parts, 1, segments.Length);
        return System.IO.Path.Combine(parts);
    }

    public string CreateDirectory(params string[] segments)
    {
        var path = Path(segments);
        Directory.CreateDirectory(path);
        return path;
    }

    public string WriteText(string relativePath, string text)
    {
        var path = Path(relativePath.Split('/', '\\'));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        DeleteWithRetry(Root);
    }

    private static void DeleteWithRetry(string path)
    {
        for (var attempt = 1; attempt <= DeleteAttempts; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch when (attempt < DeleteAttempts)
            {
                Thread.Sleep(25 * attempt);
            }
            catch
            {
                return;
            }
        }
    }
}
