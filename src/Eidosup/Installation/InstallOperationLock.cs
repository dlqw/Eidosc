using Eidosup.Diagnostics;

namespace Eidosup.Installation;

public sealed class InstallOperationLock : IAsyncDisposable
{
    private readonly FileStream _stream;

    private InstallOperationLock(FileStream stream)
    {
        _stream = stream;
    }

    public static async Task<InstallOperationLock> AcquireAsync(
        string lockDirectory,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        string operationName = "install")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Lock timeout must be positive.");
        }

        if (string.IsNullOrWhiteSpace(operationName) ||
            operationName.Any(static character => !char.IsAsciiLetterOrDigit(character) && character is not '-' and not '_'))
        {
            throw new ArgumentException("Operation name must contain only ASCII letters, digits, '-' or '_'.", nameof(operationName));
        }

        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, $"{operationName}.lock");
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileStream? stream = null;
            try
            {
                stream = new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous | FileOptions.WriteThrough);
                stream.SetLength(0);
                await using var writer = new StreamWriter(stream, leaveOpen: true);
                await writer.WriteAsync($"{Environment.ProcessId}\n{DateTimeOffset.UtcNow:O}");
                await writer.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
                stream.Position = 0;
                return new InstallOperationLock(stream);
            }
            catch (IOException) when (DateTimeOffset.UtcNow < deadline)
            {
                stream?.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
            }
            catch (IOException exception)
            {
                stream?.Dispose();
                throw new EidosupException(
                    EidosupErrorCode.LockTimeout,
                    EidosupExitCodes.LockTimeout,
                    $"Timed out waiting for {operationName} lock '{lockPath}'.",
                    "Wait for the other Eidosup process to finish, then retry.",
                    exception);
            }
            catch
            {
                stream?.Dispose();
                throw;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stream.DisposeAsync();
    }
}
