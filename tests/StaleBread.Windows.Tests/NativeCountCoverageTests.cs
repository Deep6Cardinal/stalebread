using StaleBread.Core.Model;
using StaleBread.Windows.Collection;
using StaleBread.Windows.Native;

namespace StaleBread.Windows.Tests;

public class NativeCountCoverageTests
{
    [Theory] [InlineData("chrome.exe")] [InlineData("msedge.exe")] [InlineData("firefox.exe")]
    public async Task BrowserDiagnosticsReceiveVerifiedMetadataWithoutJoiningCounts(string name)
    {
        var raw = new[] { P(1), P(2) with { Name = name, ParentPid = 1 } };
        await using var collector = new WindowsProcessCollector(TimeProvider.System, 1, () => new(raw, true, null),
            (id, _) => Task.FromResult(new EnrichmentResult(id, id, @"C:\browser.exe", "user", 1, null, LaunchMode.Unknown, false, null)), false, () => new HashSet<int>());
        await collector.CollectAsync(default); var batch = await collector.CollectAsync(default);
        Assert.Equal("user", batch.Processes.Single(p => p.Pid == 2).OwnerSid);
        var counts = new StaleBread.Core.Trends.CountHistory(TimeProvider.System);
        counts.Append(batch, TimeSpan.FromSeconds(5), new(1, "1", new HashSet<ProcessIdentity>()));
        Assert.Equal(1, counts.Read(TimeProvider.System.GetTimestamp()).CurrentCount);
    }
    private sealed class Clock : TimeProvider { public int Seconds; public override long TimestampFrequency => 1; public override long GetTimestamp() => Seconds; }
    [Fact] public async Task DiscoveryDrainsBoundedBatchesInsteadOfOneCandidatePerSample()
    {
        int calls = 0;
        await using var collector = new WindowsProcessCollector(TimeProvider.System, 1, () => new(Enumerable.Range(1, 50).Select(P).ToArray(), true, null),
            (id, _) => { calls++; return Task.FromResult(new EnrichmentResult(id, id, @"C:\helper.exe", "user", 1, null, LaunchMode.NonInteractive, false, null)); }, false);
        var first = await collector.CollectAsync(default); Assert.Contains(first.Gaps, g => g.Contains("Metadata pending"));
        Assert.InRange(calls, 2, 8);
        ObservationBatch? batch = null; for (int i = 0; i < 8; i++) batch = await collector.CollectAsync(default);
        Assert.All(batch!.Processes, p => Assert.Equal("user", p.OwnerSid)); Assert.Equal(50, calls);
    }
    [Fact] public async Task RetryingCommandMetadataKeepsAlreadyVerifiedPathAndOwner()
    {
        var clock = new Clock(); int calls = 0; var pending = new TaskCompletionSource<EnrichmentResult>();
        await using var collector = new WindowsProcessCollector(clock, 1, () => new([P(1)], true, null),
            (id, _) => ++calls == 1 ? Task.FromResult(new EnrichmentResult(id, id, @"C:\keep.exe", "user", 1, null, LaunchMode.Unknown, false, "CommandMetadataUnavailable")) : pending.Task, false);
        await collector.CollectAsync(default); await collector.CollectAsync(default); clock.Seconds = 30;
        var batch = await collector.CollectAsync(default);
        Assert.Equal("user", batch.Processes[0].OwnerSid); Assert.Equal(@"C:\keep.exe", batch.Processes[0].CanonicalImagePath);
        Assert.Equal(2, calls);
    }
    [Fact] public async Task RepeatedDeniedOldCandidatesCannotStarveNewDiscoveries()
    {
        var clock = new Clock(); var attempted = new HashSet<int>();
        await using var collector = new WindowsProcessCollector(clock, 1, () => new(Enumerable.Range(1, 50).Select(P).ToArray(), true, null),
            (id, _) => { attempted.Add(id.Pid); return Task.FromResult(new EnrichmentResult(id, null, null, null, null, null, LaunchMode.Unknown, null, "Denied")); }, false);
        for (int i = 0; i < 8; i++) { clock.Seconds = i * 30; await collector.CollectAsync(default); }
        Assert.Equal(50, attempted.Count);
    }
    [Fact] public async Task FreshWindowVisibilityCannotRemainCachedFalse()
    {
        var visible = new HashSet<int>();
        await using var collector = new WindowsProcessCollector(TimeProvider.System, 1, () => new([P(1)], true, null),
            (id, _) => Task.FromResult(new EnrichmentResult(id, id, @"C:\helper.exe", "user", 1, null,
                LaunchMode.NonInteractive, false, null)), false, () => visible);
        await collector.CollectAsync(default);
        Assert.False((await collector.CollectAsync(default)).Processes[0].HasVisibleWindow);
        visible.Add(1);
        Assert.True((await collector.CollectAsync(default)).Processes[0].HasVisibleWindow);
    }
    private static ProcessSample P(int i) => new(new(i, 1), i, 0, "CLAUDE.EXE", SessionId: 1);
    [Theory] [InlineData("invalid")] [InlineData("creation")] [InlineData("session")] [InlineData("cap")]
    public async Task NativeFailuresArePartialRegardlessOfEnrichment(string failure)
    {
        var sample = P(1);
        var result = failure switch {
            "invalid" => new NativeSnapshotResult([], false, "Invalid"),
            "creation" => new([sample with { Identity = null }], true, null),
            "session" => new([sample with { SessionId = null }], true, null),
            _ => new(Enumerable.Range(1, 20001).Select(P).ToArray(), true, null)
        };
        await using var c = new WindowsProcessCollector(TimeProvider.System, 1, () => result, (_, _) => null, false);
        var b = await c.CollectAsync(default);
        Assert.Equal(NativeCountCoverage.Partial, b.NativeCountCoverage);
        Assert.NotEmpty(b.CountGaps);
    }
    [Theory] [InlineData(1)] [InlineData(2001)] [InlineData(10001)]
    public async Task MetadataDenialAndGraphCapsCannotPoisonCompleteNativeCounts(int count)
    {
        await using var c = new WindowsProcessCollector(TimeProvider.System, 1,
            () => new(Enumerable.Range(1, count).Select(P).ToArray(), true, null),
            (id, _) => Task.FromResult(new EnrichmentResult(id, null, null, null, null, null, LaunchMode.Unknown, null, "Denied")), false);
        var b = await c.CollectAsync(default);
        Assert.Equal(NativeCountCoverage.Usable, b.NativeCountCoverage);
        Assert.Equal(count, b.Processes.Count);
    }
    [Fact] public async Task StalledMetadataDoesNotBlockSubsequentSnapshotsOrLaunchReplacementCalls()
    {
        int calls = 0;
        var never = new TaskCompletionSource<EnrichmentResult>();
        await using var c = new WindowsProcessCollector(TimeProvider.System, 1, () => new([P(1), P(2)], true, null),
            (_, _) => { calls++; return never.Task; }, false);
        for (int i = 0; i < 10; i++) Assert.Equal(NativeCountCoverage.Usable, (await c.CollectAsync(default)).NativeCountCoverage);
        Assert.Equal(1, calls);
    }
    [Fact] public async Task WrongIdentityMetadataNeverAttachesToSnapshot()
    {
        await using var c = new WindowsProcessCollector(TimeProvider.System, 1, () => new([P(1)], true, null),
            (id, _) => Task.FromResult(new EnrichmentResult(id, new(1, 2), "wrong", "user", 1, null, LaunchMode.NonInteractive, false, null)), false);
        await c.CollectAsync(default);
        var b = await c.CollectAsync(default);
        Assert.Null(Assert.Single(b.Processes).CanonicalImagePath);
    }
}
