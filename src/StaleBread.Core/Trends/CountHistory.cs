using StaleBread.Core.Model;

namespace StaleBread.Core.Trends;

public sealed class CountHistory(TimeProvider time)
{
    private sealed record Record(long Timestamp, int Count, TimeSpan Cadence, int Additions, int Removals);
    private readonly List<Record> records = [];
    private HashSet<ProcessIdentity>? previous;
    private CountScope? priorScope;
    private int? lastCount;
    private long? lastSample;
    private bool partial;
    private bool reset = true;
    private long epoch;
    private string reason = "Collecting history";
    public int RecordCount => records.Count;
    public void Reset(string cause)
    {
        records.Clear(); previous = null; epoch++; reset = true; reason = cause;
    }
    public void Append(ObservationBatch batch, TimeSpan cadence, CountScope scope)
    {
        if (batch.SampleKind != SampleKind.Scheduled) return;
        if (cadence <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(cadence));
        if (priorScope is not null && (priorScope.SessionId != scope.SessionId ||
            priorScope.Version != scope.Version || !priorScope.SelfOwned.SetEquals(scope.SelfOwned)))
            Reset("Counting scope changed");
        // Copy caller-owned exclusions so later mutation cannot alter prior scope comparison.
        priorScope = scope with { SelfOwned = scope.SelfOwned.ToHashSet() };
        if (records.Count > 0 && (batch.MonotonicTicks <= records[^1].Timestamp ||
            Seconds(records[^1].Timestamp, batch.MonotonicTicks) > cadence.TotalSeconds * 3))
            Reset("Observation gap");
        var ids = new HashSet<ProcessIdentity>();
        bool incomplete = batch.NativeCountCoverage == NativeCountCoverage.Partial;
        foreach (var p in batch.Processes)
        {
            if (!CountScope.Names.Contains(p.Name)) continue;
            if (p.SessionId.HasValue && p.SessionId != scope.SessionId) continue;
            if (!p.SessionId.HasValue || p.Identity is not { } id || id.CreationFileTimeUtc <= 0 || id.Pid != p.Pid)
            { incomplete = true; continue; }
            if (scope.SelfOwned.Contains(id)) continue;
            if (ids.Count >= 20000 && !ids.Contains(id)) { incomplete = true; continue; }
            ids.Add(id);
        }
        lastCount = ids.Count; lastSample = batch.MonotonicTicks; partial = incomplete;
        if (incomplete)
        {
            Reset(batch.CountGaps.Count > 0 ? string.Join("; ", batch.CountGaps) : "Native counting incomplete");
            return;
        }
        int additions = previous is null ? 0 : ids.Count(id => !previous.Contains(id));
        int removals = previous is null ? 0 : previous.Count(id => !ids.Contains(id));
        records.Add(new(batch.MonotonicTicks, ids.Count, cadence, additions, removals));
        previous = ids; reset = false;
        records.RemoveAll(r => Seconds(r.Timestamp, batch.MonotonicTicks) > 3660);
        if (records.Count > 800) records.RemoveRange(0, records.Count - 800);
    }
    public CountTrendSnapshot Read(long nowTicks)
    {
        Record? latest = records.LastOrDefault();
        bool stale = reset || latest is null || nowTicks < latest.Timestamp ||
            Seconds(latest.Timestamp, nowTicks) > latest.Cadence.TotalSeconds * 3;
        var deltas = new List<CountDelta>();
        foreach (int seconds in new[] { 60, 300, 900, 1800, 3600 })
        {
            var horizon = TimeSpan.FromSeconds(seconds);
            Record? baseline = latest is null ? null : records.LastOrDefault(r =>
                Seconds(r.Timestamp, latest.Timestamp) >= seconds);
            if (stale || partial || baseline is null || latest is null ||
                Seconds(baseline.Timestamp, latest.Timestamp) - seconds > baseline.Cadence.TotalSeconds * 3)
            {
                deltas.Add(new(horizon, null, null, null, null,
                    partial ? "Limited native counting coverage" : stale ? "Count is stale" : reason));
                continue;
            }
            var intervals = records.Where(r => r.Timestamp > baseline.Timestamp);
            deltas.Add(new(horizon, latest.Count - baseline.Count, intervals.Sum(r => r.Additions),
                intervals.Sum(r => r.Removals), TimeSpan.FromSeconds(Seconds(baseline.Timestamp, latest.Timestamp)), null));
        }
        return new(lastCount, lastSample, stale, partial, deltas.ToArray(), reason, epoch);
    }
    private double Seconds(long a, long b) => (b - a) / (double)time.TimestampFrequency;
}
