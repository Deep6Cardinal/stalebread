using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using StaleBread.Core.Cleanup;
namespace StaleBread.Core.Detection;

/// <summary>Single-writer deterministic rules. Findings never themselves execute actions.</summary>
public sealed class RuleEngine(CleanupPolicy policy, Func<Settings> settings)
{
    private Guid run;
    private long start;
    private long? lastApplied;
    private readonly Dictionary<string, (Finding Finding, int ClearCount, long? ClearSince)> episodes = [];
    private readonly Dictionary<ProcessIdentity, long> accumulationSince = [];
    private readonly HashSet<ProcessIdentity> seen = [];
    private readonly List<(long Time, ProcessIdentity Owner, bool Trace, string Key)> births = [];
    private readonly List<(long Time, double? Load)> loads = [];
    private readonly Dictionary<ProcessIdentity, List<(long Time, int Count)>> counts = [];
    private Coverage? priorCoverage;
    private long sourceSince;
    public IReadOnlyList<Finding> Evaluate(GraphSnapshot graph, RuleOptions options)
    {
        if (run != graph.RunId || graph.Gaps.Contains("SamplingCadenceChanged"))
        {
            run = graph.RunId; start = graph.Timestamp; lastApplied = null;
            episodes.Clear(); accumulationSince.Clear(); seen.Clear(); births.Clear(); counts.Clear(); loads.Clear(); priorCoverage = null;
        }
        if (lastApplied == graph.Timestamp) return episodes.Values.Select(e => e.Finding).ToArray();
        lastApplied = graph.Timestamp;
        if (graph.Coverage == Coverage.Degraded)
        {
            births.Clear(); counts.Clear(); loads.Clear(); accumulationSince.Clear(); sourceSince = graph.Timestamp;
            foreach (var key in episodes.Keys.ToArray()) episodes[key] = (episodes[key].Finding, 0, null);
            return episodes.Values.Select(e => e.Finding with { CleanupEligible = false, Coverage = Coverage.Degraded,
                MissingEvidence = ["Collector degraded; new decisions suspended"] }).ToArray();
        }
        if (priorCoverage != graph.Coverage) { sourceSince = graph.Timestamp; births.Clear(); }
        priorCoverage = graph.Coverage;
        loads.Add((graph.Timestamp, graph.System.MemoryLoadPercent));
        loads.RemoveAll(x => graph.Seconds(x.Time, graph.Timestamp) > 900);
        if (loads.Count > 180) loads.RemoveRange(0, loads.Count - 180);
        TrackBirths(graph);
        var candidates = new List<Finding>();
        var excluded = graph.Current.Keys.Concat(graph.Retained.Keys).Where(id => CleanupPolicy.IsExcluded(id, graph, settings())).ToHashSet();
        foreach (var key in episodes.Where(kv => kv.Value.Finding.Members.Any(excluded.Contains) ||
            kv.Value.Finding.OwnerIdentity is { } owner && excluded.Contains(owner)).Select(kv => kv.Key).ToArray()) episodes.Remove(key);
        var groups = graph.Current.Where(kv => graph.Associations[kv.Key].Role == ProcessRole.Helper && !excluded.Contains(kv.Key))
            .GroupBy(kv => graph.Associations[kv.Key].AgentRoot ?? kv.Key)
            .ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).ToArray());

        foreach (var (id, node) in graph.Current)
        {
            var a = graph.Associations[id];
            if (!ClassifierCatalog.WatchNames.Contains(node.Sample.Name) && a.Role != ProcessRole.Helper) continue;
            if (a.ReasonCodes.Contains("SelfOrExcluded") || a.ReasonCodes.Contains("SelfFamily")) continue;
            if (excluded.Contains(id)) continue;
            bool ownerExited = a.Role == ProcessRole.Helper && a.AgentRoot is { } owner &&
                graph.Find(owner)?.ExitedAt is { } exited && graph.Seconds(exited, graph.Timestamp) >= 120 &&
                !a.ReasonCodes.Contains("LiveAgentAncestor") && (a.LineageComplete || a.ReasonCodes.Contains("UnknownAncestryAboveAgentRoot")) &&
                graph.Seconds(node.FirstSeen, graph.Timestamp) >= 120;
            if (ownerExited)
            {
                if (WindowTracker.Sustained(node, graph, TimeSpan.FromSeconds(120), i => i.Quiet))
                    candidates.Add(Make(graph, options, FindingKind.ObservedOrphan, [id], a.AgentRoot, EvidenceConfidence.High,
                        policy.Evaluate(id, graph, settings()).Eligible, ["ObservedOwnerExit", "Quiet120Seconds"]));
                else if (WindowTracker.Sustained(node, graph, TimeSpan.FromSeconds(120), i => i.Valid) &&
                    WindowTracker.Intervals(node, graph.TimestampFrequency, true).TakeLast(3).ToArray() is { Length: 3 } recent &&
                    recent.All(i => i.Valid && !i.Quiet))
                    candidates.Add(Make(graph, options, FindingKind.ActiveLeftover, [id], a.AgentRoot, EvidenceConfidence.High,
                        false, ["ObservedOwnerExit", "ThreeActiveScheduledIntervals"]));
            }
            if (!node.ParentKnown && ClassifierCatalog.KnownMarker(node.Sample.KnownAiMarker) &&
                graph.Seconds(node.FirstSeen, graph.Timestamp) >= 600 &&
                graph.Utc.UtcDateTime.ToFileTimeUtc() - id.CreationFileTimeUtc >= TimeSpan.FromMinutes(15).Ticks &&
                WindowTracker.Sustained(node, graph, TimeSpan.FromMinutes(10), i => i.Quiet))
                candidates.Add(Make(graph, options, FindingKind.PossibleOrphan, [id], null, EvidenceConfidence.Low, false,
                    ["ParentAbsentAtFirstObservation", "Quiet10Minutes", "OwnerExitNotObserved"]));
            CheckResources(graph, options, node, [id], a.AgentRoot, candidates);
        }
        foreach (var (owner, members) in groups)
        {
            if (members.Length > 1 && Aggregate(graph, members) is { } aggregate)
                CheckResources(graph, options, aggregate, members, owner, candidates, growth: false);
            if (!counts.TryGetValue(owner, out var history)) counts[owner] = history = [];
            if (graph.SampleKind == SampleKind.Scheduled)
            {
                history.Add((graph.Timestamp, members.Length));
                history.RemoveAll(x => graph.Seconds(x.Time, graph.Timestamp) > 900);
                if (history.Count > 180) history.RemoveRange(0, history.Count - 180);
            }
            var baseline = history.LastOrDefault(x => graph.Seconds(x.Time, graph.Timestamp) >= 600);
            if (members.Length >= options.AccumulationCount &&
                history.Any(x => graph.Seconds(x.Time, graph.Timestamp) >= 600) &&
                members.Length - baseline.Count >= options.AccumulationAdded)
            {
                if (graph.SampleKind == SampleKind.Scheduled) accumulationSince.TryAdd(owner, graph.Timestamp);
                var candidate = Make(graph, options, FindingKind.Accumulation, members, owner, EvidenceConfidence.Medium,
                    false, ["HelperCountAboveThreshold", "AddedHelpersAboveBaseline", "SustainedAccumulation"]);
                bool alreadyActive = episodes.TryGetValue(candidate.EpisodeKey, out var previous) && previous.Finding.State == FindingState.Active;
                if (alreadyActive || graph.SampleKind == SampleKind.Scheduled && accumulationSince.TryGetValue(owner, out var since) &&
                    graph.Seconds(since, graph.Timestamp) >= RuleOptions.AccumulationPersistence.TotalSeconds)
                    candidates.Add(candidate with { Measurements = [new(TriggerKind.Accumulation, EvidenceScope.HelperGroup, members.Length,
                        WindowSeconds: graph.Seconds(baseline.Time, graph.Timestamp), PrivateBytes: candidate.AggregatePrivateBytes, ThresholdCount: options.AccumulationCount,
                        BaselineCount: baseline.Count, AddedCount: members.Length - baseline.Count, ThresholdAddedCount: options.AccumulationAdded,
                        PersistenceSeconds: RuleOptions.AccumulationPersistence.TotalSeconds, RequiredWindowSeconds: 600)] });
            }
            else accumulationSince.Remove(owner);
        }
        foreach (var owner in counts.Keys.Where(id => !groups.ContainsKey(id)).ToArray()) { counts.Remove(owner); accumulationSince.Remove(owner); }
        if (graph.Seconds(start, graph.Timestamp) >= 120 && graph.Seconds(sourceSince, graph.Timestamp) >= 120)
        {
            bool trace = graph.Coverage == Coverage.Complete;
            int threshold = trace ? options.EventStarts : options.SnapshotBirths;
            foreach (var group in births.Where(b => b.Trace == trace && !excluded.Contains(b.Owner)).GroupBy(b => b.Owner))
            {
                int first = group.Count(b => graph.Seconds(b.Time, graph.Timestamp) is > 60 and <= 120);
                int second = group.Count(b => graph.Seconds(b.Time, graph.Timestamp) is >= 0 and <= 60);
                if (first >= threshold && second >= threshold)
                {
                    var members = graph.Current.Keys.Where(id => graph.Associations[id].AgentRoot == group.Key && !excluded.Contains(id)).ToArray();
                    var churn = Make(graph, options, FindingKind.Churn, members, group.Key, EvidenceConfidence.Medium,
                        false, [trace ? "TwoWindowsEventStarts" : "TwoWindowsSnapshotAppearances"], lowerBound: !trace);
                    candidates.Add(churn with { Measurements = [new(TriggerKind.Churn, EvidenceScope.AssociatedFamily, members.Length,
                        WindowSeconds: 60, PrivateBytes: churn.AggregatePrivateBytes, PreviousWindowStarts: first,
                        RecentWindowStarts: second, ThresholdCount: threshold)] });
                }
            }
        }
        // Combine same family/owner once. Distinct members prevent double-counting group/single rules.
        var merged = candidates.GroupBy(f => f.EpisodeKey).Select(group =>
        {
            var f = group.OrderByDescending(f => f.Kind == FindingKind.ActiveLeftover).First();
            var members = group.SelectMany(f => f.Members).Distinct().ToArray();
            var total = Sum(members.Select(id => graph.Current[id].Sample.PrivateBytes));
            var impact = group.Max(x => x.Impact);
            if (impact == Impact.Low && (members.Length >= 3 || total >= 128UL * 1024 * 1024)) impact = Impact.Moderate;
            return f with { Members = members, AggregatePrivateBytes = total, Impact = impact,
                CleanupEligible = group.All(f => f.CleanupEligible), ReasonCodes = group.SelectMany(x => x.ReasonCodes).Distinct().ToArray(),
                Measurements = group.SelectMany(x => x.Measurements).Distinct().ToArray() };
        }).ToDictionary(f => f.EpisodeKey);
        foreach (var (key, f) in merged)
        {
            var first = episodes.TryGetValue(key, out var old) && old.Finding.State == FindingState.Active ? old.Finding.FirstSeenUtc : f.FirstSeenUtc;
            episodes[key] = (f with { FirstSeenUtc = first }, 0, null);
        }
        foreach (var key in episodes.Keys.Except(merged.Keys).ToArray())
        {
            var entry = episodes[key];
            if (entry.Finding.State == FindingState.Resolved) { episodes.Remove(key); continue; }
            int clear = entry.ClearCount + (graph.SampleKind == SampleKind.Scheduled ? 1 : 0);
            long? clearSince = entry.ClearSince ?? (graph.SampleKind == SampleKind.Scheduled ? graph.Timestamp : null);
            bool activity = entry.Finding.Kind is FindingKind.Churn or FindingKind.Accumulation;
            bool resolved = graph.SampleKind == SampleKind.Scheduled && (activity
                ? clearSince.HasValue && graph.Seconds(clearSince.Value, graph.Timestamp) >= RuleOptions.ActivityClearDuration.TotalSeconds
                : clear >= 3);
            episodes[key] = (entry.Finding with { CleanupEligible = false, LastSeenUtc = graph.Utc,
                State = resolved ? FindingState.Resolved : FindingState.Active, IsClearing = !resolved,
                ClearAfterSeconds = activity ? RuleOptions.ActivityClearDuration.TotalSeconds : null,
                MissingEvidence = [resolved ? "Trigger cleared" : "Trigger no longer satisfied; awaiting scheduled clear"] }, clear, clearSince);
        }
        if (episodes.Count > 2000)
            foreach (var key in episodes.OrderBy(kv => kv.Value.Finding.LastSeenUtc).Take(episodes.Count - 2000).Select(kv => kv.Key).ToArray())
                episodes.Remove(key);
        return episodes.Values.Select(e => e.Finding).ToArray();
    }
    private void CheckResources(GraphSnapshot g, RuleOptions o, ProcessNode node, ProcessIdentity[] members,
        ProcessIdentity? owner, List<Finding> found, bool growth = true)
    {
        bool cpu = WindowTracker.Sustained(node, g, o.BusyDuration, i =>
            i.CoreEquivalents >= o.BusyCores || i.CoreEquivalents / g.System.LogicalProcessorCount * 100 >= o.BusyMachinePercent);
        var memoryPoints = WindowTracker.Points(node, g, o.MemoryDuration);
        var loadBaseline = loads.LastOrDefault(x => g.Seconds(x.Time, g.Timestamp) >= o.MemoryDuration.TotalSeconds);
        bool memory = memoryPoints.Count >= 2 && memoryPoints.All(p => p.Sample.PrivateBytes >= o.PressurePrivateBytes) &&
            loads.Any(x => g.Seconds(x.Time, g.Timestamp) >= o.MemoryDuration.TotalSeconds) &&
            loads.Where(x => x.Time >= loadBaseline.Time).All(x => x.Load >= o.PressureLoadPercent);
        if (cpu || memory)
        {
            var measurements = new List<TriggerMeasurement>();
            var scope = members.Length > 1 ? EvidenceScope.HelperGroup : EvidenceScope.Process;
            ProcessIdentity? subject = members.Length == 1 ? members[0] : null;
            var cpuPoints = WindowTracker.Points(node, g, o.BusyDuration);
            double? cores = cpuPoints.Count > 1 ? (cpuPoints[^1].Sample.CpuTime - cpuPoints[0].Sample.CpuTime)?.TotalSeconds /
                g.Seconds(cpuPoints[0].Timestamp, cpuPoints[^1].Timestamp) : null;
            if (cpu) measurements.Add(new(TriggerKind.Cpu, scope, members.Length, subject, g.Seconds(cpuPoints[0].Timestamp, cpuPoints[^1].Timestamp),
                CpuMachinePercent: cores / g.System.LogicalProcessorCount * 100, CpuCoreEquivalents: cores,
                SystemMemoryLoadPercent: g.System.MemoryLoadPercent, PrivateBytes: node.Sample.PrivateBytes,
                ThresholdMachinePercent: o.BusyMachinePercent, ThresholdCores: o.BusyCores, RequiredWindowSeconds: o.BusyDuration.TotalSeconds));
            if (memory) measurements.Add(new(TriggerKind.MemoryPressure, scope, members.Length, subject, g.Seconds(memoryPoints[0].Timestamp, memoryPoints[^1].Timestamp),
                SystemMemoryLoadPercent: g.System.MemoryLoadPercent, PrivateBytes: node.Sample.PrivateBytes,
                ThresholdBytes: o.PressurePrivateBytes, ThresholdSystemMemoryLoadPercent: o.PressureLoadPercent, RequiredWindowSeconds: o.MemoryDuration.TotalSeconds));
            var f = Make(g, o, FindingKind.HeavyWorkload, members, owner, EvidenceConfidence.High, false,
                [.. (cpu ? new[] { "SustainedCpu" } : []), .. (memory ? new[] { "PrivateMemoryAndSystemPressure" } : []), "BusyNotStale"]);
            bool high = memory && loads.Where(x => x.Time >= loadBaseline.Time).All(x => x.Load >= 95) ||
                cpu && WindowTracker.Sustained(node, g, o.BusyDuration, i => i.CoreEquivalents / g.System.LogicalProcessorCount * 100 >= 80);
            found.Add(f with { Impact = high ? Impact.High : Impact.Moderate, Measurements = measurements.ToArray() });
        }
        if (!growth) return;
        var points = WindowTracker.Points(node, g, o.GrowthDuration);
        if (points.Count < 2) return;
        var a = points[0].Sample; var b = points[^1].Sample;
        bool memGrowth = points.All(p => p.Sample.PrivateBytes.HasValue) && b.PrivateBytes >= a.PrivateBytes &&
            b.PrivateBytes - a.PrivateBytes >= o.GrowthBytes && a.PrivateBytes > 0 &&
            (double)b.PrivateBytes.Value / a.PrivateBytes.Value - 1 >= o.GrowthFraction;
        bool handleGrowth = points.All(p => p.Sample.HandleCount.HasValue) && b.HandleCount - a.HandleCount >= o.GrowthHandles &&
            a.HandleCount > 0 && (double)b.HandleCount!.Value / a.HandleCount.Value - 1 >= o.HandleGrowthFraction;
        if (memGrowth || handleGrowth)
        {
            var evidence = new List<TriggerMeasurement>();
            if (memGrowth) evidence.Add(new(TriggerKind.MemoryGrowth, EvidenceScope.Process, 1, members[0], g.Seconds(points[0].Timestamp, points[^1].Timestamp),
                PrivateBytes: b.PrivateBytes, BaselinePrivateBytes: a.PrivateBytes, ThresholdBytes: o.GrowthBytes, ThresholdFraction: o.GrowthFraction, RequiredWindowSeconds: o.GrowthDuration.TotalSeconds));
            if (handleGrowth) evidence.Add(new(TriggerKind.HandleGrowth, EvidenceScope.Process, 1, members[0], g.Seconds(points[0].Timestamp, points[^1].Timestamp),
                HandleCount: b.HandleCount, BaselineHandleCount: a.HandleCount, PrivateBytes: b.PrivateBytes,
                ThresholdHandles: o.GrowthHandles, ThresholdFraction: o.HandleGrowthFraction, RequiredWindowSeconds: o.GrowthDuration.TotalSeconds));
            found.Add(Make(g, o, FindingKind.Growth, members, owner, EvidenceConfidence.High, false,
                [.. (memGrowth ? new[] { "MemoryGrowth10Minutes" } : []), .. (handleGrowth ? new[] { "HandleGrowth10Minutes" } : [])])
                with { Measurements = evidence.ToArray() });
        }
    }
    private void TrackBirths(GraphSnapshot g)
    {
        bool initial = seen.Count == 0;
        foreach (var (id, _) in g.Current)
            if (seen.Add(id) && !initial && g.Associations[id].AgentRoot is { } owner)
                births.Add((g.Timestamp, owner, false, $"s:{id.Pid}:{id.CreationFileTimeUtc}"));
        seen.IntersectWith(g.Current.Keys.Concat(g.Retained.Keys));
        foreach (var e in g.Events.Where(e => e.IsStart))
        {
            ProcessIdentity? owner = null;
            if (e.ValidatedIdentity is { } valid && g.Associations.TryGetValue(valid, out var a)) owner = a.AgentRoot;
            if (owner is null && e.OwnerSid is not null && e.SessionId.HasValue && e.EventFileTimeUtc.HasValue)
            {
                var parents = g.Current.Concat(g.Retained).Where(kv => kv.Key.Pid == e.ParentPid &&
                    kv.Key.CreationFileTimeUtc <= e.EventFileTimeUtc && kv.Value.Sample.OwnerSid == e.OwnerSid &&
                    kv.Value.Sample.SessionId == e.SessionId && e.MonotonicTicks >= kv.Value.FirstSeen &&
                    e.MonotonicTicks <= kv.Value.LastSeen).ToArray();
                if (parents.Length == 1)
                {
                    var parent = parents[0];
                    var evidence = g.Associations[parent.Key];
                    owner = evidence.Role == ProcessRole.AgentRoot ? parent.Key : evidence.AgentRoot;
                }
            }
            if (owner is null) continue;
            string key = e.ValidatedIdentity is { } identity ? $"e:{identity.Pid}:{identity.CreationFileTimeUtc}" : $"e:{e.Pid}:{e.EventFileTimeUtc}";
            if (!births.Any(b => b.Key == key)) births.Add((e.MonotonicTicks, owner.Value, true, key));
        }
        births.RemoveAll(b => g.Seconds(b.Time, g.Timestamp) > 120);
        if (births.Count > 4096) births.RemoveRange(0, births.Count - 4096);
    }
    private static ProcessNode? Aggregate(GraphSnapshot g, ProcessIdentity[] members)
    {
        var nodes = members.Select(id => g.Current[id]).ToArray();
        var common = nodes[0].Metrics.Select(p => p.Timestamp).ToHashSet();
        foreach (var n in nodes.Skip(1)) common.IntersectWith(n.Metrics.Select(p => p.Timestamp));
        var points = common.Order().Select(t =>
        {
            var p = nodes.Select(n => n.Metrics.Single(m => m.Timestamp == t)).ToArray();
            var first = p[0];
            return first with { Sample = first.Sample with {
                CpuTime = p.All(x => x.Sample.CpuTime.HasValue) ? TimeSpan.FromSeconds(p.Sum(x => x.Sample.CpuTime!.Value.TotalSeconds)) : null,
                PrivateBytes = Sum(p.Select(x => x.Sample.PrivateBytes)),
                ReadBytes = Sum(p.Select(x => x.Sample.ReadBytes)), WriteBytes = Sum(p.Select(x => x.Sample.WriteBytes)),
                OtherBytes = Sum(p.Select(x => x.Sample.OtherBytes))
            }};
        }).ToArray();
        return points.Length == 0 ? null : nodes[0] with { Metrics = points, Sample = points[^1].Sample };
    }
    private static ulong? Sum(IEnumerable<ulong?> values)
    {
        ulong total = 0;
        foreach (var value in values) { if (!value.HasValue || ulong.MaxValue - total < value) return null; total += value.Value; }
        return total;
    }
    private static Finding Make(GraphSnapshot g, RuleOptions o, FindingKind kind, ProcessIdentity[] members,
        ProcessIdentity? owner, EvidenceConfidence confidence, bool cleanup, string[] reasons, bool lowerBound = false)
    {
        string family = kind is FindingKind.ObservedOrphan or FindingKind.ActiveLeftover ? "Leftover" : kind.ToString();
        var keyId = owner ?? members.FirstOrDefault();
        ulong? bytes = Sum(members.Select(id => g.Current[id].Sample.PrivateBytes));
        bool leftover = kind is FindingKind.ObservedOrphan or FindingKind.ActiveLeftover or FindingKind.PossibleOrphan;
        // Notification identity survives monitoring resets; cleanup authorization still carries RunId independently.
        return new($"{family}/{keyId.Pid}:{keyId.CreationFileTimeUtc}", kind, confidence, members, reasons, cleanup,
            g.Utc, g.Utc, leftover && members.Length < 3 && !(bytes >= 128UL * 1024 * 1024) ? Impact.Low : Impact.Moderate,
            o.Version, owner, reasons, cleanup ? [] : ["Review only; no automatic action"], bytes, g.Coverage, lowerBound, FindingState.Active)
            { MeasurementsAtUtc = g.Utc };
    }
}
