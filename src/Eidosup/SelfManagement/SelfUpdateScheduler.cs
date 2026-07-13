using System.Text.Json;
using Eidosup.Configuration;
using Eidosup.Installation;

namespace Eidosup.SelfManagement;

public sealed record SelfUpdateCheckState(int Schema, DateTimeOffset CheckedAt);

public sealed class SelfUpdateScheduler
{
    public const string StateFileName = "self-update-check.json";
    private readonly Func<ToolInstallLayout, bool, CancellationToken, Task<SelfUpdateResult>> _update;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _interval;

    public SelfUpdateScheduler(
        Func<ToolInstallLayout, bool, CancellationToken, Task<SelfUpdateResult>>? update = null,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? interval = null)
    {
        _update = update ?? ((layout, checkOnly, token) => new SelfLifecycleManager().UpdateAsync(layout, checkOnly, token));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _interval = interval ?? TimeSpan.FromHours(24);
    }

    public async Task<SelfUpdateResult?> RunIfDueAsync(
        ToolInstallLayout layout,
        CancellationToken cancellationToken)
    {
        var settingsPath = Path.Combine(layout.RootDirectory, EidosupSettingsStore.FileName);
        if (!File.Exists(settingsPath))
        {
            return null;
        }

        var settings = await new EidosupSettingsStore().ReadAsync(layout, cancellationToken);
        if (settings.AutoSelfUpdate == AutoSelfUpdateMode.Disable)
        {
            return null;
        }

        var statePath = Path.Combine(layout.StateDirectory, StateFileName);
        var now = _clock();
        if (await ReadCheckedAtAsync(statePath, cancellationToken) is { } checkedAt && now - checkedAt < _interval)
        {
            return null;
        }

        await using var operationLock = await InstallOperationLock.AcquireAsync(
            layout.LockDirectory,
            TimeSpan.FromSeconds(5),
            cancellationToken,
            operationName: "self-update-check");
        if (await ReadCheckedAtAsync(statePath, cancellationToken) is { } lockedCheckedAt && now - lockedCheckedAt < _interval)
        {
            return null;
        }

        try
        {
            return await _update(
                layout,
                settings.AutoSelfUpdate == AutoSelfUpdateMode.CheckOnly,
                cancellationToken);
        }
        finally
        {
            await WriteCheckedAtAsync(statePath, now, CancellationToken.None);
        }
    }

    private static async Task<DateTimeOffset?> ReadCheckedAtAsync(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var state = await JsonSerializer.DeserializeAsync<SelfUpdateCheckState>(stream, cancellationToken: cancellationToken);
            return state is { Schema: 1 } ? state.CheckedAt : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteCheckedAtAsync(string path, DateTimeOffset checkedAt, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(stream, new SelfUpdateCheckState(1, checkedAt), cancellationToken: cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
