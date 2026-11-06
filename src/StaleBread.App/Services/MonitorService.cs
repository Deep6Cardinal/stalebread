using System.Diagnostics;
using StaleBread.Core.Cleanup;
using StaleBread.Core.Detection;
using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using StaleBread.Core.Trends;
namespace StaleBread.App.Services;

public sealed record MonitorState(DateTimeOffset? LastSuccessfulUtc, GraphSnapshot? Graph,
 IReadOnlyList<Finding> Findings, CountTrendSnapshot Counts, bool Paused, bool EconomyMode, Coverage Coverage,
 IReadOnlyList<string> Errors, TimeSpan Cadence, double ScanMilliseconds);
public sealed class MonitorService : IFreshSnapshotSource, IAsyncDisposable
{
    private readonly object gate = new();
    private readonly IProcessCollector collector;
    private readonly TimeProvider time;
    private readonly TemporalProcessGraph graph;
    private readonly RuleEngine rules;
    private readonly CountHistory counts;
    private readonly CountScope scope;
    private readonly Func<Settings> settings;
    private readonly Func<TimeSpan> selfCpu;
    private readonly EconomyController economy;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private Task? loop;
    private TaskCompletionSource<GraphSnapshot>? pending;
    private MonitorState latest;
    private bool paused;
    private bool userPaused, systemSuspended;
    private string? resetReason;
    private readonly RecoveryGate recovery;
    public MonitorService(IProcessCollector collector, TimeProvider time, AssociationClassifier classifier,
        CountScope scope, CleanupPolicy policy, Func<Settings> settings, Func<TimeSpan>? selfCpu = null)
    {
        this.collector = collector; this.time = time; this.scope = scope; this.settings = settings;
        recovery = new(time);
        this.selfCpu = selfCpu ?? (() => { using var p = Process.GetCurrentProcess(); return p.TotalProcessorTime; });
        graph = new(time, classifier); rules = new(policy, settings); counts = new(time); economy = new(time);
        latest = new(null, null, [], counts.Read(time.GetTimestamp()), false, false, Coverage.Degraded,
            ["Starting collector"], TimeSpan.FromSeconds(5), 0);
    }
    public MonitorState State
    {
        get
        {
            lock (gate)
            {
                var state = latest; long now = time.GetTimestamp();
                bool stale = paused || resetReason is not null || state.Graph is null ||
                    time.GetElapsedTime(state.Graph.Timestamp, now) > state.Cadence * 3;
                if (!stale) return state;
                return state with { Paused = paused, Coverage = Coverage.Degraded,
                    Findings = state.Findings.Select(f => f with { CleanupEligible = false }).ToArray(),
                    Counts = state.Counts with { Stale = true, Deltas = state.Counts.Deltas.Select(d =>
                        d with { Additions = null, Removals = null, Net = null, UnavailableReason = paused ? "Paused" : "Observation stale" }).ToArray() },
                    Errors = paused ? ["Paused"] : state.Errors.Append("Waiting for healthy observations").Distinct().ToArray() };
            }
        }
    }
    public Task StartAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (gate) { loop ??= Task.Run(RunAsync); }
        return Task.CompletedTask;
    }
    private void Signal() { if (wake.CurrentCount == 0) { try { wake.Release(); } catch (SemaphoreFullException) { } } }
    public void SetPaused(bool value, string reason = "Paused")
    { lock (gate) { userPaused = value; SetEffectivePause(userPaused || systemSuspended, reason); } }
    private void SetEffectivePause(bool value, string reason)
    {
        lock (gate)
        {
            if (paused == value) return;
            paused = value; resetReason = value ? reason : "History restarted after resume";
            recovery.Reset();
            pending?.TrySetException(new InvalidOperationException("Pause/resume invalidated pending cleanup")); pending = null;
            latest = latest with { Paused = value };
        }
        Signal();
    }
    public void SetSystemSuspended(bool value)
    { lock (gate) { systemSuspended = value; SetEffectivePause(userPaused || systemSuspended, value ? "History restarted after sleep" : "History restarted after resume"); } }
    public void ResetAfterResume()
    {
        lock (gate) { resetReason = "History restarted after sleep"; recovery.Reset(); }
        Signal();
    }
    public async Task<GraphSnapshot> GetAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Task<GraphSnapshot> requested;
        lock (gate)
        {
            if (loop is null || stop.IsCancellationRequested || paused || recovery.Recovering)
                throw new InvalidOperationException("Monitoring paused, stopped or recovering");
            pending ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            requested = pending.Task;
        }
        Signal();
        var result = await requested.WaitAsync(TimeSpan.FromSeconds(5), time, ct).ConfigureAwait(false);
        if (time.GetElapsedTime(result.Timestamp) > TimeSpan.FromSeconds(5)) throw new InvalidOperationException("Fresh scan expired");
        return result;
    }
    private async Task RunAsync()
    {
        long next = time.GetTimestamp();
        try
        {
            while (!stop.IsCancellationRequested)
            {
                TaskCompletionSource<GraphSnapshot>? request;
                bool scheduled;
                lock (gate)
                {
                    if (resetReason is { } reason)
                    { graph.Reset(reason); counts.Reset(reason); resetReason = null; next = time.GetTimestamp(); }
                    scheduled = !paused && time.GetTimestamp() >= next;
                    request = !paused ? pending : null;
                    if (scheduled || request is not null) pending = null;
                    if (paused) latest = latest with { Paused = true, Counts = counts.Read(time.GetTimestamp()) };
                }
                if (!scheduled && request is null)
                {
                    double remaining = Math.Max(.01, (next - time.GetTimestamp()) / (double)time.TimestampFrequency);
                    await wake.WaitAsync(TimeSpan.FromSeconds(paused ? 1 : Math.Min(remaining, 15)), stop.Token).ConfigureAwait(false);
                    continue;
                }
                var watch = Stopwatch.StartNew();
                try
                {
                    var batch = await collector.CollectAsync(stop.Token).ConfigureAwait(false);
                    lock (gate)
                    {
                        if (paused || resetReason is not null)
                        { request?.TrySetException(new InvalidOperationException("Observation invalidated")); continue; }
                        batch = batch with { SampleKind = scheduled ? SampleKind.Scheduled : SampleKind.OnDemand };
                        counts.Append(batch, graph.Cadence, scope);
                        var snapshot = graph.Apply(batch);
                        var findings = rules.Evaluate(snapshot, settings().AdvisoryRules);
                        recovery.Observe(snapshot.Coverage != Coverage.Degraded);
                        bool recovering = recovery.Recovering;
                        if (recovering) findings = findings.Select(f => f with { CleanupEligible = false }).ToArray();
                        if (scheduled && economy.Observe(selfCpu(), batch.System.LogicalProcessorCount))
                            graph.Cadence = TimeSpan.FromSeconds(economy.Economy ? 15 : 5);
                        if (scheduled) next = time.GetTimestamp() + (long)(graph.Cadence.TotalSeconds * time.TimestampFrequency);
                        latest = new(batch.Coverage != Coverage.Degraded ? batch.Utc : latest.LastSuccessfulUtc, snapshot,
                            findings, counts.Read(time.GetTimestamp()), false, economy.Economy,
                            recovering ? Coverage.Degraded : snapshot.Coverage,
                            recovering ? snapshot.Gaps.Append("Recovering: 60 healthy seconds required").ToArray() : snapshot.Gaps,
                            graph.Cadence, watch.Elapsed.TotalMilliseconds);
                        if (snapshot.Coverage == Coverage.Degraded || recovering)
                            request?.TrySetException(new InvalidOperationException("Collector coverage insufficient for cleanup"));
                        else request?.TrySetResult(snapshot);
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested)
                { request?.TrySetCanceled(stop.Token); break; }
                catch (Exception e)
                {
                    lock (gate)
                    {
                        graph.Reset("Collector failed"); counts.Reset("Collector failed");
                        recovery.Reset();
                        latest = latest with { Coverage = Coverage.Degraded, Errors = ["Collector failed: " + e.GetType().Name], Counts = counts.Read(time.GetTimestamp()) };
                        next = time.GetTimestamp() + 5 * time.TimestampFrequency;
                        request?.TrySetException(new InvalidOperationException("Collector failed", e));
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        finally
        {
            lock (gate) { pending?.TrySetException(new InvalidOperationException("Monitoring stopped")); pending = null; }
            if (collector is IAsyncDisposable disposable) await disposable.DisposeAsync().ConfigureAwait(false);
        }
    }
    public async Task StopAsync(CancellationToken ct = default)
    {
        stop.Cancel(); Signal();
        Task? running; lock (gate) running = loop;
        if (running is not null) await running.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
    }
    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false); stop.Dispose(); wake.Dispose();
    }
}
