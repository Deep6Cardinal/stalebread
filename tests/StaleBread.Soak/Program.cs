using System.Diagnostics;
using System.Text.Json;
using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using StaleBread.Core.Trends;
using StaleBread.Core.Detection;
using StaleBread.App.Services;

if (args.Length == 2 && args[0] == "--icon")
{
    using var icon = ToastIcons.Create("watching"); using var file = File.Create(args[1]); icon.Save(file); return;
}
var clock = new ReplayTime();
var graph = new TemporalProcessGraph(clock, new(new(@"C:\Users\replay"), "replay", 1));
var counts = new CountHistory(clock); var scope = new CountScope(1, "1", new HashSet<ProcessIdentity>());
var rules = new RuleEngine(new("replay", 1), () => new());
var timings = new List<double>(); var process = Process.GetCurrentProcess();
long warmMemory = 0, finalMemory = 0; int maxNodes = 0, maxRecords = 0, maxMetrics = 0;
var timer = Stopwatch.StartNew(); CountTrendSnapshot? final = null;
for (int seconds = 0; seconds <= 8 * 3600; seconds += 5)
{
    clock.Seconds = seconds;
    var samples = Enumerable.Range(1, 500).Select(i => new ProcessSample(new(i, i == 50 ? 1 + seconds / 60 : 1), i,
        i is > 1 and <= 50 ? 1 : 0, i == 1 ? "claude.exe" : i <= 50 ? "node.exe" : "other.exe",
        i == 1 ? @"C:\Users\replay\.local\bin\claude.exe" : @"C:\test\helper.exe", "replay", 1,
        TimeSpan.FromSeconds(i <= 50 ? seconds * .0001 : 0), 0, 0, 0, 1024 * 1024, 2 * 1024 * 1024, 10, false, null, LaunchMode.NonInteractive)).ToArray();
    var batch = new ObservationBatch(clock.GetUtcNow(), clock.GetTimestamp(), SampleKind.Scheduled, samples, [],
        new(40, 16UL << 30, Environment.ProcessorCount), Coverage.SnapshotOnly, [], NativeCountCoverage.Usable, []);
    var scan = Stopwatch.StartNew(); counts.Append(batch, graph.Cadence, scope); var state = graph.Apply(batch);
    var findings = rules.Evaluate(state, new()); final = counts.Read(clock.GetTimestamp());
    timings.Add(scan.Elapsed.TotalMilliseconds);
    if (findings.Any(f => f.CleanupEligible)) throw new Exception("Live owner became eligible");
    if (counts.RecordCount > 800 || state.Current.Count + state.Retained.Count > 10000 || state.Current.Values.Any(n => n.Metrics.Count > 180))
        throw new Exception("Retention cap violated");
    if (seconds >= 3600 && final.Deltas.Any(d => d.Additions is null || d.Removals is null)) throw new Exception("Missing mature horizon");
    maxNodes = Math.Max(maxNodes, state.Current.Count + state.Retained.Count); maxRecords = Math.Max(maxRecords, counts.RecordCount);
    maxMetrics = Math.Max(maxMetrics, state.Current.Values.Max(n => n.Metrics.Count));
    if (seconds == 3600) { GC.Collect(); process.Refresh(); warmMemory = process.PrivateMemorySize64; }
}
GC.Collect(); process.Refresh(); finalMemory = process.PrivateMemorySize64;
Console.WriteLine(JsonSerializer.Serialize(new { Kind = "8-hour virtual-time replay, NOT an 8-hour hardware soak", Steps = timings.Count,
    Processes = 500, Candidates = 50, WallSeconds = timer.Elapsed.TotalSeconds, P95Milliseconds = timings.Order().ElementAt((int)(timings.Count * .95)),
    MaxNodes = maxNodes, MaxRecords = maxRecords, MaxMetrics = maxMetrics, WarmPrivateBytes = warmMemory, FinalPrivateBytes = finalMemory,
    Horizons = final!.Deltas.Select(d => new { Minutes = d.Horizon.TotalMinutes, d.Additions, d.Removals, d.Net }) }, new JsonSerializerOptions { WriteIndented = true }));
sealed class ReplayTime : TimeProvider
{
    public int Seconds; public override long TimestampFrequency => 1000;
    public override long GetTimestamp() => Seconds * 1000L;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddSeconds(Seconds);
}
