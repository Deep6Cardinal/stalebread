using StaleBread.Core.Notifications;
namespace StaleBread.Core.Tests;

public class NotificationMemoryWriterTests
{
    private static NotificationMemory Memory(bool dismissed) => new(1, null,
        new Dictionary<string, EpisodeAcknowledgment> { ["episode"] = new(null, null, dismissed) });

    [Fact] public async Task SlowOldWriteCannotRestoreResolvedDismissal()
    {
        var firstStarted = new TaskCompletionSource(); var release = new TaskCompletionSource();
        var current = Memory(true); NotificationMemory? disk = null; int calls = 0;
        var writer = new NotificationMemoryWriter(() => current, async (value, _) =>
        {
            if (Interlocked.Increment(ref calls) == 1) { firstStarted.SetResult(); await release.Task; }
            disk = value;
        });
        var oldWrite = writer.SaveAsync(default); await firstStarted.Task;
        current = Memory(false); var resolvedWrite = writer.SaveAsync(default);
        release.SetResult(); await Task.WhenAll(oldWrite, resolvedWrite);
        Assert.False(disk!.Episodes["episode"].Dismissed);
    }
    [Fact] public async Task CancelledQueuedSaveCannotStartPersistence()
    {
        int calls = 0; var release = new TaskCompletionSource();
        var writer = new NotificationMemoryWriter(() => Memory(true), (_, _) =>
            Interlocked.Increment(ref calls) == 1 ? release.Task : Task.CompletedTask);
        var first = writer.SaveAsync(default);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var queued = writer.SaveAsync(cancelled.Token);
        release.SetResult(); await first;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
        Assert.Equal(1, calls);
    }
}
