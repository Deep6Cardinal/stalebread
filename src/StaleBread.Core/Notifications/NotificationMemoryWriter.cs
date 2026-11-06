namespace StaleBread.Core.Notifications;

public sealed class NotificationMemoryWriter(Func<NotificationMemory> snapshot,
    Func<NotificationMemory, CancellationToken, Task> persist)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task SaveAsync(CancellationToken ct)
    {
        // Capture the newest state only after earlier writes finish. Preserve the
        // caller's synchronization context while reading its UI-owned policy.
        await gate.WaitAsync(ct);
        try { await persist(snapshot(), ct); }
        finally { gate.Release(); }
    }
}
