using StaleBread.Core.Cleanup;
using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using static StaleBread.Core.Tests.Fixtures;

namespace StaleBread.Core.Tests;

public class CleanupTests
{
    private static GraphSnapshot WithHiddenGrandchild(GraphSnapshot s)
    {
        var current = s.Current.ToDictionary(); var retained = s.Retained.ToDictionary();
        retained[new(9, 1)] = new(P(9, 2), 0, 5000, 10000, new(2, 1), true, []);
        current[new(4, 1)] = new(P(4, 9, "unknown.exe"), 130000, 130000, null, new(9, 1), true, []);
        return s with { Current = current, Retained = retained };
    }
    [Fact] public async Task PreparationRejectsGrandchildBehindRetainedExitedShim()
    {
        var h = new Harness(); var p = h.Proposal(); h.Source.Graph = WithHiddenGrandchild(h.Source.Graph);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Prepare(p)); Assert.Empty(h.Factory.Order);
    }
    [Fact] public async Task ExecutionRejectsNewGrandchildBehindRetainedExitedShim()
    {
        var h = new Harness(); using var prepared = await h.Prepare(h.Proposal()); h.Source.Graph = WithHiddenGrandchild(h.Source.Graph);
        var result = await h.Coordinator.ExecuteAsync(prepared);
        Assert.Empty(h.Factory.Order); Assert.All(result.Processes, p => Assert.Equal(StopResult.Skipped, p.Result));
    }
    private static GraphSnapshot Eligible()
    {
        var g = NewGraph();
        g.Apply(B(0, P(1, name: "claude.exe", path: @"C:\Users\test\.local\bin\claude.exe"),
            P(2, 1, path: @"C:\helper.exe"), P(3, 2, path: @"C:\helper.exe")));
        GraphSnapshot? s = null;
        for (int t = 5; t <= 130; t += 5) s = g.Apply(B(t, P(2, 1, path: @"C:\helper.exe"), P(3, 2, path: @"C:\helper.exe")));
        return s!;
    }
    private sealed class Source(GraphSnapshot initial) : IFreshSnapshotSource
    {
        public GraphSnapshot Graph = initial;
        public Task<GraphSnapshot> GetAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Graph); }
    }
    private sealed class Handle(ProcessIdentity identity, List<int> order) : IValidatedProcessHandle
    {
        public ProcessIdentity Identity { get; set; } = identity;
        public string? OwnerSid { get; set; } = "user";
        public int? SessionId { get; set; } = 1;
        public bool Alive = true, Disposed, Fail, Pending;
        public Action? OnTerminate;
        public bool IsAlive => Alive && !Disposed;
        public int Calls;
        public void Terminate() { Calls++; if (Fail) throw new UnauthorizedAccessException(); order.Add(Identity.Pid); Alive = Pending; OnTerminate?.Invoke(); }
        public Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(!Alive);
        public void Dispose() => Disposed = true;
    }
    private sealed class Factory : IProcessHandleFactory
    {
        public readonly List<int> Order = [];
        public readonly Dictionary<ProcessIdentity, Handle> Opened = [];
        public Action<Handle>? Configure;
        public int? DenyPid;
        public IValidatedProcessHandle Open(ProcessIdentity id, ProcessAccess access)
        {
            Assert.True(access.HasFlag(ProcessAccess.Terminate));
            if (id.Pid == DenyPid) throw new UnauthorizedAccessException();
            var h = new Handle(id, Order); Configure?.Invoke(h); Opened.Add(id, h); return h;
        }
    }
    private sealed class Audit : ICleanupAudit
    {
        public bool Available { get; set; } = true;
        public bool Accept = true;
        public readonly List<(ProcessIdentity? Identity, string Result)> Records = [];
        public Task<bool> RecordAsync(Guid proposal, ProcessIdentity? identity, string result, CancellationToken ct)
        { Records.Add((identity, result)); return Task.FromResult(Accept); }
    }
    [Fact] public async Task ExactIntentAndRuleEvidenceAreAuditedBeforeIrreversibleCall()
    {
        var h = new Harness();
        h.Factory.Configure = handle => handle.OnTerminate = () =>
            Assert.Contains(h.Audit.Records, r => r.Identity == handle.Identity &&
                r.Result.Contains("ForceStopIntent", StringComparison.Ordinal) && r.Result.Contains("R1", StringComparison.Ordinal));
        using var prepared = await h.Prepare(h.Proposal());
        var result = await h.Coordinator.ExecuteAsync(prepared);
        Assert.All(result.Processes, p => Assert.Equal(StopResult.Exited, p.Result));
    }
    private sealed class Harness
    {
        public readonly TestTime Time = new() { Seconds = 130 };
        public readonly Source Source = new(Eligible());
        public readonly Factory Factory = new();
        public readonly Audit Audit = new();
        public Settings Settings = new();
        public CleanupCoordinator Coordinator { get; }
        public Harness() => Coordinator = new(Factory, Source, new("user", 1), () => Settings, Time, Audit);
        public CleanupProposal Proposal() => Coordinator.Propose([new(2, 1), new(3, 1)], Source.Graph);
        public Task<PreparedCleanup> Prepare(CleanupProposal p, CancellationToken ct = default) => Coordinator.PrepareAsync(p, new(p.Id, Time.GetTimestamp()), ct);
    }
    [Fact] public async Task ExactConfirmedSetStopsLeafFirstAndCanExecuteOnlyOnce()
    {
        var h = new Harness(); using var prepared = await h.Prepare(h.Proposal());
        var outcome = await h.Coordinator.ExecuteAsync(prepared);
        Assert.Equal([3, 2], h.Factory.Order);
        Assert.All(outcome.Processes, p => Assert.Equal(StopResult.Exited, p.Result));
        Assert.True(outcome.AuditRecorded);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.ExecuteAsync(prepared));
        Assert.All(h.Factory.Opened.Values, handle => Assert.True(handle.Disposed));
    }
    [Theory] [InlineData("pid")] [InlineData("sid")] [InlineData("session")] [InlineData("dead")] [InlineData("denied")]
    public async Task HeldHandleMismatchAbortsAllTargetsBeforeAnyStop(string defect)
    {
        var h = new Harness(); var p = h.Proposal();
        h.Factory.Configure = handle => {
            if (handle.Identity.Pid != 3) return;
            if (defect == "pid") handle.Identity = new(3, 2);
            if (defect == "sid") handle.OwnerSid = null;
            if (defect == "session") handle.SessionId = 2;
            if (defect == "dead") handle.Alive = false;
        };
        if (defect == "denied") h.Factory.DenyPid = 3;
        await Assert.ThrowsAnyAsync<Exception>(() => h.Prepare(p));
        Assert.Empty(h.Factory.Order);
        Assert.All(h.Factory.Opened.Values, handle => Assert.True(handle.Disposed));
    }
    [Theory] [InlineData("expired")] [InlineData("child")] [InlineData("excluded")]
    [InlineData("version")] [InlineData("stale")] [InlineData("degraded")] [InlineData("audit")]
    public async Task ChangedConditionsAbortTheEntirePreflight(string change)
    {
        var h = new Harness(); var p = h.Proposal();
        switch (change)
        {
            case "expired": h.Time.Seconds += 31; break;
            case "stale": h.Time.Seconds += 6; break;
            case "excluded": h.Settings = new() { ExcludedPaths = [@"C:\helper.exe"] }; break;
            case "version": h.Source.Graph = h.Source.Graph with { CatalogVersion = "2" }; break;
            case "degraded": h.Source.Graph = h.Source.Graph with { Coverage = Coverage.Degraded }; break;
            case "audit": h.Audit.Accept = false; break;
            case "child":
                var map = h.Source.Graph.Current.ToDictionary();
                map[new(4, 1)] = new(P(4, 2, "unknown.exe"), 130000, 130000, null, new(2, 1), true, []);
                h.Source.Graph = h.Source.Graph with { Current = map };
                break;
        }
        await Assert.ThrowsAnyAsync<Exception>(() => h.Prepare(p));
        Assert.Empty(h.Factory.Order);
    }
    [Fact] public async Task WrongConfirmationCannotAuthorizeAProposal()
    {
        var h = new Harness(); var p = h.Proposal();
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Coordinator.PrepareAsync(p, new(Guid.NewGuid(), h.Time.GetTimestamp())));
        Assert.Empty(h.Factory.Opened);
    }
    [Fact] public async Task CancellationBeforeFirstActionStopsNothing()
    {
        var h = new Harness(); using var prepared = await h.Prepare(h.Proposal());
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        var result = await h.Coordinator.ExecuteAsync(prepared, cancel.Token);
        Assert.Empty(h.Factory.Order);
        Assert.All(result.Processes, p => Assert.Equal(StopResult.Skipped, p.Result));
    }
    [Fact] public async Task NewChildAfterPreparationIsCaughtBeforeFirstStop()
    {
        var h = new Harness(); using var prepared = await h.Prepare(h.Proposal());
        var map = h.Source.Graph.Current.ToDictionary();
        map[new(4, 1)] = new(P(4, 2, "unknown.exe"), 130000, 130000, null, new(2, 1), true, []);
        h.Source.Graph = h.Source.Graph with { Current = map };
        var result = await h.Coordinator.ExecuteAsync(prepared);
        Assert.Empty(h.Factory.Order);
        Assert.All(result.Processes, p => Assert.Equal(StopResult.Skipped, p.Result));
    }
    [Theory] [InlineData("failure")] [InlineData("deadline")] [InlineData("pending")]
    public async Task PartialOutcomesNeverClaimAllProcessesStopped(string failure)
    {
        var h = new Harness();
        h.Factory.Configure = handle => {
            if (handle.Identity.Pid == 2 && failure == "failure") handle.Fail = true;
            if (handle.Identity.Pid == 3 && failure == "deadline") handle.OnTerminate = () => h.Time.Seconds += 16;
            if (handle.Identity.Pid == 3 && failure == "pending") handle.Pending = true;
        };
        using var prepared = await h.Prepare(h.Proposal());
        var outcome = await h.Coordinator.ExecuteAsync(prepared);
        Assert.True(outcome.Partial);
        Assert.Contains(outcome.Processes, p => p.Result != StopResult.Exited);
        if (failure != "failure") Assert.Equal(0, h.Factory.Opened[new(2, 1)].Calls);
    }
    [Fact] public async Task InvalidatedHandleIsNeverReopenedByPid()
    {
        var h = new Harness(); using var prepared = await h.Prepare(h.Proposal());
        h.Factory.Opened[new(3, 1)].Alive = false;
        await h.Coordinator.ExecuteAsync(prepared);
        Assert.Equal(2, h.Factory.Opened.Count);
        Assert.Empty(h.Factory.Order);
    }
    [Fact] public void UnselectedDescendantIsNeverSilentlyAdded()
    {
        var h = new Harness();
        Assert.Throws<InvalidOperationException>(() => h.Coordinator.Propose([new(2, 1)], h.Source.Graph));
        Assert.Empty(h.Factory.Opened);
    }
}
