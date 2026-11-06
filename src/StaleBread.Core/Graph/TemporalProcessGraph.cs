using System.Collections.Frozen;
using StaleBread.Core.Model;

namespace StaleBread.Core.Graph;

/// <summary>Single-writer, immutable published snapshots. Missing is distinct from confirmed exited.</summary>
public sealed class TemporalProcessGraph(TimeProvider time, AssociationClassifier classifier,
    int detailedLimit = 2000, int aggregateLimit = 10000)
{
    private readonly Dictionary<ProcessIdentity, ProcessNode> nodes = [];
    private readonly Dictionary<ProcessIdentity, int> misses = [];
    private Guid runId = Guid.NewGuid();
    private long? lastTimestamp;
    private TimeSpan previousCadence = TimeSpan.FromSeconds(5);
    private string? resetReason;
    public TimeSpan Cadence { get; set; } = TimeSpan.FromSeconds(5);
    public void Reset(string reason)
    {
        nodes.Clear(); misses.Clear(); lastTimestamp = null;
        runId = Guid.NewGuid(); resetReason = reason;
    }
    public GraphSnapshot Apply(ObservationBatch batch)
    {
        if (lastTimestamp is { } last &&
            (batch.MonotonicTicks <= last || (batch.MonotonicTicks - last) / (double)time.TimestampFrequency > Cadence.TotalSeconds * 3))
            Reset("ObservationContinuityLost");
        var gaps = batch.Gaps.ToList();
        if (resetReason is not null) { gaps.Add(resetReason); resetReason = null; }
        if (previousCadence != Cadence)
        {
            gaps.Add("SamplingCadenceChanged");
            foreach (var id in nodes.Keys.ToArray()) nodes[id] = nodes[id] with { Metrics = [] };
        }
        previousCadence = Cadence;
        lastTimestamp = batch.MonotonicTicks;
        var incoming = batch.Processes.Where(p => p.Identity.HasValue).GroupBy(p => p.Identity!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        if (incoming.Count != batch.Processes.Count(p => p.Identity.HasValue)) gaps.Add("DuplicateIdentity");
        var present = incoming.Keys.ToHashSet();
        // Degraded batches cannot advance exit evidence or preserve quiet authorization.
        if (batch.Coverage == Coverage.Degraded)
        {
            misses.Clear();
            foreach (var id in nodes.Keys.ToArray()) nodes[id] = nodes[id] with { Metrics = [] };
        }
        else
        {
            foreach (var id in nodes.Keys.ToArray())
            {
                if (present.Contains(id)) { misses.Remove(id); continue; }
                var node = nodes[id];
                if (node.ExitedAt.HasValue) continue;
                int count = misses.GetValueOrDefault(id) + 1;
                misses[id] = count;
                if (count >= 2) nodes[id] = node with { ExitedAt = batch.MonotonicTicks };
            }
        }
        var byPid = incoming.Values.GroupBy(p => p.Pid).ToDictionary(g => g.Key, g => g.ToArray());
        int detailed = 0;
        foreach (var (id, p) in incoming)
        {
            nodes.TryGetValue(id, out var old);
            if (old is not null && (p.CpuTime < old.Sample.CpuTime || p.ReadBytes < old.Sample.ReadBytes ||
                p.WriteBytes < old.Sample.WriteBytes || p.OtherBytes < old.Sample.OtherBytes))
                old = old with { Metrics = [] };
            ProcessIdentity? parent = old?.Parent;
            bool parentKnown = old?.ParentKnown ?? p.ParentPid == 0;
            // A known edge never rebinds to a replacement PID. Unknown edges may resolve only
            // to a currently observed parent that predates this child.
            if (parent is null && p.ParentPid != 0 && byPid.TryGetValue(p.ParentPid, out var possible) &&
                possible.Length == 1 && possible[0].Identity is { } candidate && candidate != id &&
                candidate.CreationFileTimeUtc <= id.CreationFileTimeUtc)
            { parent = candidate; parentKnown = true; }
            bool track = ClassifierCatalog.NeedsDiagnostics(p);
            IReadOnlyList<MetricPoint> metrics = [];
            if (track && ++detailed <= detailedLimit && batch.Coverage != Coverage.Degraded)
            {
                metrics = (old?.Metrics ?? []).Where(m => (batch.MonotonicTicks - m.Timestamp) /
                    (double)time.TimestampFrequency <= 900)
                    .Append(new MetricPoint(batch.MonotonicTicks, batch.SampleKind, p)).TakeLast(180).ToArray();
            }
            nodes[id] = new(p, old?.FirstSeen ?? batch.MonotonicTicks, batch.MonotonicTicks, null,
                parent, parentKnown, metrics);
        }
        if (detailed > detailedLimit) gaps.Add("DetailedCapacityExceeded");
        // Pin every required intermediate as well as the owner. Pins remain subject to the hard cap.
        var pinned = new HashSet<ProcessIdentity>();
        foreach (var id in present)
        {
            var cursor = id;
            while (pinned.Add(cursor) && nodes.TryGetValue(cursor, out var node) && node.Parent is { } parent)
                cursor = parent;
        }
        foreach (var (id, node) in nodes.ToArray())
            if (!present.Contains(id) && !pinned.Contains(id) && node.ExitedAt is { } exited &&
                (batch.MonotonicTicks - exited) / (double)time.TimestampFrequency > 900)
            { nodes.Remove(id); misses.Remove(id); }
        if (nodes.Count > aggregateLimit)
        {
            gaps.Add("AggregateCapacityExceeded");
            foreach (var id in nodes.OrderBy(kv => present.Contains(kv.Key))
                .ThenBy(kv => pinned.Contains(kv.Key)).ThenBy(kv => kv.Value.LastSeen)
                .Take(nodes.Count - aggregateLimit).Select(kv => kv.Key).ToArray())
            { nodes.Remove(id); misses.Remove(id); }
        }
        foreach (var (id, node) in nodes.ToArray())
            if (node.Parent is { } parent && !nodes.ContainsKey(parent))
                nodes[id] = node with { Parent = null, ParentKnown = false };
        var coverage = gaps.Any(g => g.EndsWith("Exceeded", StringComparison.Ordinal) || g == "DuplicateIdentity")
            ? Coverage.Degraded : batch.Coverage;
        var current = nodes.Where(kv => present.Contains(kv.Key)).ToFrozenDictionary();
        var retained = nodes.Where(kv => !present.Contains(kv.Key)).ToFrozenDictionary();
        var graph = new GraphSnapshot(runId, batch.Utc, batch.MonotonicTicks, batch.SampleKind, current, retained,
            new Dictionary<ProcessIdentity, AssociationEvidence>().ToFrozenDictionary(), batch.System, coverage,
            gaps.ToArray(), classifier.CatalogVersion, time.TimestampFrequency, batch.Events.ToArray());
        return graph with { Associations = nodes.ToFrozenDictionary(kv => kv.Key, kv => classifier.Classify(kv.Value.Sample, graph)) };
    }
}
