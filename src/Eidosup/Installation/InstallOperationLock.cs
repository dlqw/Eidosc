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
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockDirectory);
        if (timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(timeout), "Lock timeout must be positive.");
        }

        Directory.CreateDirectory(lockDirectory);
        var lockPath = Path.Combine(lockDirectory, "install.lock");
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
                    $"Timed out waiting for install lock '{lockPath}'.",
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
