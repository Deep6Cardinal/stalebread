using StaleBread.Core.Model;
using StaleBread.Core.Graph;
using StaleBread.Windows.Native;

namespace StaleBread.Windows.Collection;

public sealed class WindowsProcessCollector : IProcessCollector, IAsyncDisposable
{
    private readonly TimeProvider time;
    private readonly int sessionId;
    private readonly Func<NativeSnapshotResult> read;
    private readonly Func<ProcessIdentity, CancellationToken, Task<EnrichmentResult>?> enrich;
    private readonly MetadataEnricher? ownedEnricher;
    private readonly ProcessEventSource events;
    private readonly Func<IReadOnlySet<int>?> visibility;
    private readonly Dictionary<ProcessIdentity, (EnrichmentResult? Result, long Attempt)> cache = [];
    private Task<IReadOnlyList<EnrichmentResult>>? outstanding;
    private long started;
    private bool disposed;
    private int collecting;
    public WindowsProcessCollector(TimeProvider time, int sessionId, Func<NativeSnapshotResult>? read = null,
        Func<ProcessIdentity, CancellationToken, Task<EnrichmentResult>?>? enrich = null, bool trace = true,
        Func<IReadOnlySet<int>?>? windowVisibility = null)
    {
        this.time = time; this.sessionId = sessionId;
        visibility = windowVisibility ?? WindowVisibility.Read;
        this.read = read ?? new NativeProcessSnapshot().Read;
        if (enrich is null) { ownedEnricher = new(); this.enrich = ownedEnricher.TryEnrichAsync; }
        else this.enrich = enrich;
        events = new(time);
        if (trace) events.Start();
    }
    public ValueTask<ObservationBatch> CollectAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ct.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref collecting, 1) != 0) throw new InvalidOperationException("Overlapping collector call");
        try
        {
            var result = read();
            long now = time.GetTimestamp();
            var gaps = new List<string>();
            var countGaps = new List<string>();
            var raw = result.Valid ? result.Processes : DisplayOnlyFallback.Read();
            if (!result.Valid) { gaps.Add(result.Error ?? "Native snapshot unavailable"); countGaps.Add("Display-only fallback"); }
            var identities = raw.Where(p => p.Identity.HasValue).Select(p => p.Identity!.Value).ToHashSet();
            foreach (var id in cache.Keys.Where(id => !identities.Contains(id)).ToArray()) cache.Remove(id);
            if (outstanding?.IsCompleted == true)
            {
                if (outstanding.IsCompletedSuccessfully)
                {
                    foreach (var metadata in outstanding.Result)
                        if (metadata.VerifiedIdentity == metadata.RequestedIdentity && identities.Contains(metadata.RequestedIdentity) &&
                            cache.TryGetValue(metadata.RequestedIdentity, out var previous))
                            cache[metadata.RequestedIdentity] = (metadata, previous.Attempt);
                }
                else { _ = outstanding.Exception; gaps.Add("Metadata worker failed"); }
                outstanding = null;
            }
            if (outstanding is not null && time.GetElapsedTime(started, now) > TimeSpan.FromSeconds(2))
                gaps.Add("Metadata enrichment delayed; no replacement worker");
            var counted = new HashSet<ProcessIdentity>();
            foreach (var p in raw.Where(p => ClassifierCatalog.WatchNames.Contains(p.Name)))
            {
                if (p.SessionId.HasValue && p.SessionId != sessionId) continue;
                if (p.Identity is not { } id || id.CreationFileTimeUtc <= 0 || id.Pid != p.Pid || !p.SessionId.HasValue)
                { if (!countGaps.Contains("Native count identity/session unavailable")) countGaps.Add("Native count identity/session unavailable"); continue; }
                counted.Add(id);
                if (counted.Count > 20000) { countGaps.Add("Native count capacity exceeded"); break; }
            }
            if (outstanding is null && result.Valid)
            {
                var candidates = raw.Where(p => p.Identity is { } id && p.SessionId == sessionId &&
                    ClassifierCatalog.NeedsDiagnostics(p) &&
                    (!cache.TryGetValue(id, out var state) || (state.Result?.FailureCode is not null || state.Result is null) &&
                        time.GetElapsedTime(state.Attempt, now) >= TimeSpan.FromSeconds(30)))
                    .OrderBy(p => cache.ContainsKey(p.Identity!.Value) ? 1 : 0)
                    .ThenBy(p => cache.TryGetValue(p.Identity!.Value, out var state) ? state.Attempt : long.MinValue)
                    .Take(8).Select(p => p.Identity!.Value).ToArray();
                var selected = new List<ProcessIdentity>();
                foreach (var id in candidates)
                {
                    if (!cache.ContainsKey(id) && cache.Count >= 10000) { gaps.Add("Metadata cache capacity reached"); break; }
                    cache.TryGetValue(id, out var previous);
                    cache[id] = (previous.Result, now); // Keep verified path/SID while command metadata retries.
                    selected.Add(id);
                }
                if (selected.Count > 0) { started = now; outstanding = EnrichBatchAsync(selected, ct); }
            }
            int pendingMetadata = raw.Count(p => p.Identity is { } id && p.SessionId == sessionId &&
                ClassifierCatalog.NeedsDiagnostics(p) &&
                (!cache.TryGetValue(id, out var state) || state.Result is null || state.Result.FailureCode is not null));
            if (pendingMetadata > 0) gaps.Add($"Metadata pending or unavailable for {pendingMetadata} processes; findings may be incomplete");
            var visible = visibility();
            var processes = raw.Select(p => p.Identity is { } id && cache.TryGetValue(id, out var state) &&
                state.Result is { } m && m.VerifiedIdentity == id
                ? p with { CanonicalImagePath = m.ImagePath, OwnerSid = m.OwnerSid,
                    // Never overwrite native session with asynchronous metadata.
                    KnownAiMarker = m.Marker, LaunchMode = m.LaunchMode, HasVisibleWindow = visible?.Contains(p.Pid) }
                : p).ToArray();
            var eventBatch = events.Drain();
            gaps.AddRange(eventBatch.Gaps);
            if (!events.Available) gaps.Add(events.Failure ?? "Short-lived process activity may be missed");
            var validated = eventBatch.Events.Select(e =>
            {
                var p = processes.FirstOrDefault(p => p.Pid == e.Pid && p.Identity is { } id &&
                    e.IsStart && e.EventFileTimeUtc >= id.CreationFileTimeUtc &&
                    e.EventFileTimeUtc - id.CreationFileTimeUtc < TimeSpan.FromSeconds(2).Ticks);
                return p?.Identity is { } id ? e with { ValidatedIdentity = id } : e;
            }).ToArray();
            return ValueTask.FromResult(new ObservationBatch(time.GetUtcNow(), now, SampleKind.Scheduled,
                processes, validated, SystemMetrics.Read(),
                !result.Valid ? Coverage.Degraded : events.Available && !eventBatch.Loss ? Coverage.Complete : Coverage.SnapshotOnly,
                gaps.ToArray(), countGaps.Count == 0 ? NativeCountCoverage.Usable : NativeCountCoverage.Partial,
                countGaps.ToArray()));
        }
        finally { Volatile.Write(ref collecting, 0); }
    }
    private async Task<IReadOnlyList<EnrichmentResult>> EnrichBatchAsync(IReadOnlyList<ProcessIdentity> identities, CancellationToken ct)
    {
        var results = new List<EnrichmentResult>();
        // At most eight sequential requests per batch, still on the ONE bounded native worker.
        // A hung request holds this batch; it never causes replacement WMI workers.
        foreach (var id in identities)
        {
            if (ct.IsCancellationRequested || disposed) break;
            var work = enrich(id, ct);
            if (work is null) break;
            try { results.Add(await work.ConfigureAwait(false)); }
            catch (Exception e) when (e is not OperationCanceledException)
            { results.Add(new(id, null, null, null, null, null, LaunchMode.Unknown, null, "Metadata worker failed")); }
        }
        return results;
    }
    public ValueTask DisposeAsync()
    {
        disposed = true; events.Dispose(); ownedEnricher?.Dispose(); cache.Clear();
        return ValueTask.CompletedTask;
    }
}
