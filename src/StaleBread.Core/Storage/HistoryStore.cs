using System.Text;
using System.Text.Json;
using StaleBread.Core.Model;
namespace StaleBread.Core.Storage;

public enum HistoryKind { Finding, Action, Symptom, Error }
public sealed record HistoryEntry(int SchemaVersion, DateTimeOffset Timestamp, HistoryKind Kind, string Summary,
    Finding? Finding = null, ProcessIdentity? Identity = null);
public sealed class HistoryStore : IAsyncDisposable
{
    private sealed record Pending(HistoryEntry Entry, TaskCompletionSource<StorageResult> Completion);
    private readonly object gate = new();
    private readonly LinkedList<Pending> queue = [];
    private readonly SemaphoreSlim signal = new(0);
    private readonly SemaphoreSlim fileGate = new(1, 1);
    private readonly List<HistoryEntry> entries = [];
    private readonly string path;
    private readonly TimeProvider time;
    private readonly int maxBytes, capacity;
    private readonly Func<string, string, CancellationToken, Task> writer;
    private readonly Task worker;
    private bool disposed, loaded;
    private volatile bool healthy = true;
    private int dropped;
    public HistoryStore(string path, TimeProvider time, int maxBytes = 20 * 1024 * 1024, int capacity = 1024,
        Func<string, string, CancellationToken, Task>? writer = null)
    {
        if (maxBytes < 256 || capacity < 1) throw new ArgumentOutOfRangeException(nameof(maxBytes));
        this.path = path; this.time = time; this.maxBytes = maxBytes; this.capacity = capacity;
        this.writer = writer ?? AtomicFile.WriteAsync;
        worker = Task.Run(RunAsync);
    }
    public bool AuditHealthy => healthy;
    public int Dropped => Volatile.Read(ref dropped);
    public Task<StorageResult> AppendAsync(HistoryEntry entry, CancellationToken ct = default)
    {
        lock (gate)
        {
            if (disposed || ct.IsCancellationRequested) return Task.FromResult(new StorageResult(false, "History closed or cancelled"));
            if (entry.SchemaVersion != 1 || entry.Summary.Length > 4096) return Task.FromResult(new StorageResult(false, "History record invalid"));
            if (queue.Count >= capacity)
            {
                var candidate = queue.First;
                while (candidate is not null && candidate.Value.Entry.Kind == HistoryKind.Action) candidate = candidate.Next;
                if (candidate is null)
                {
                    dropped++;
                    if (entry.Kind == HistoryKind.Action) healthy = false;
                    return Task.FromResult(new StorageResult(false, "History queue full of audit records"));
                }
                candidate.Value.Completion.TrySetResult(new(false, "Observation dropped: queue overflow"));
                queue.Remove(candidate); dropped++;
            }
            var done = new TaskCompletionSource<StorageResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            queue.AddLast(new Pending(entry, done));
            signal.Release();
            return done.Task;
        }
    }
    private async Task LoadInsideLockAsync()
    {
        if (loaded) return;
        if (!File.Exists(path)) { loaded = true; return; }
        if (new FileInfo(path).Length > maxBytes) throw new IOException("History exceeds configured bound");
        try { entries.AddRange(JsonSerializer.Deserialize<HistoryEntry[]>(await File.ReadAllTextAsync(path).ConfigureAwait(false)) ?? []); }
        catch (JsonException) { File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N")); healthy = false; }
        loaded = true;
    }
    private async Task RunAsync()
    {
        while (true)
        {
            await signal.WaitAsync().ConfigureAwait(false);
            Pending? pending;
            lock (gate)
            {
                if (queue.First is { } first) { pending = first.Value; queue.RemoveFirst(); }
                else { if (disposed) return; continue; }
            }
            await fileGate.WaitAsync().ConfigureAwait(false);
            try
            {
                await LoadInsideLockAsync().ConfigureAwait(false);
                var next = entries.Where(e => e.Timestamp >= time.GetUtcNow().AddDays(-7)).ToList();
                if (pending.Entry.Timestamp >= time.GetUtcNow().AddDays(-7)) next.Add(pending.Entry);
                string json = JsonSerializer.Serialize(next);
                while (Encoding.UTF8.GetByteCount(json) > maxBytes && next.Count > 1)
                { next.RemoveAt(0); json = JsonSerializer.Serialize(next); }
                if (Encoding.UTF8.GetByteCount(json) > maxBytes) throw new IOException("Single history entry exceeds capacity");
                await writer(path, json, CancellationToken.None).ConfigureAwait(false);
                entries.Clear(); entries.AddRange(next);
                healthy = true; pending.Completion.TrySetResult(new(true));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            { healthy = false; pending.Completion.TrySetResult(new(false, "History write failed: " + e.GetType().Name)); }
            finally { fileGate.Release(); }
        }
    }
    public async Task<IReadOnlyList<HistoryEntry>> ReadAsync(CancellationToken ct = default)
    {
        await fileGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await LoadInsideLockAsync().ConfigureAwait(false);
            return entries.Where(e => e.Timestamp >= time.GetUtcNow().AddDays(-7)).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { healthy = false; return []; }
        finally { fileGate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { if (disposed) return; disposed = true; signal.Release(); }
        await worker.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        signal.Dispose(); fileGate.Dispose();
    }
}
