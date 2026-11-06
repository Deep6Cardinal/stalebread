using StaleBread.Core.Cleanup;
using StaleBread.Windows.Collection;
using StaleBread.Windows.Native;
using Xunit.Abstractions;

namespace StaleBread.Windows.Tests;

public class FixtureIntegrationTests(ITestOutputHelper output)
{
    [Fact] public async Task FixtureNaturalExitIsObservedThroughHeldHandle()
    {
        using var fixture = ContainedFixture.Start("idle", 1000);
        using var held = new ProcessHandleFactory().Open(fixture.Identity, ProcessAccess.Query);
        Assert.True(held.IsAlive);
        Assert.True(await held.WaitForExitAsync(TimeSpan.FromSeconds(5), default));
        Assert.False(held.IsAlive);
    }
    [Theory] [InlineData(100)] [InlineData(2000)] [InlineData(10000)]
    public async Task CompareKnownFixtureBirthWithTraceAndSnapshotLowerBound(int lifetime)
    {
        using var events = new ProcessEventSource(TimeProvider.System);
        events.Start();
        await Task.Delay(500);
        events.Drain();
        using var fixture = ContainedFixture.Start("idle", lifetime);
        var initial = new NativeProcessSnapshot().Read();
        Assert.Contains(initial.Processes, p => p.Identity == fixture.Identity);
        // Work duration starts after managed runtime startup. Wait on the exact handle,
        // not a sleep that incorrectly assumes creation == entry into Main.
        using var held = new ProcessHandleFactory().Open(fixture.Identity, ProcessAccess.Query);
        if (lifetime < 5000) Assert.True(await held.WaitForExitAsync(TimeSpan.FromSeconds(10), default));
        else await Task.Delay(5000);
        var later = new NativeProcessSnapshot().Read();
        var observed = events.Drain();
        int traceStarts = observed.Events.Count(e => e.IsStart && e.Pid == fixture.Identity.Pid);
        output.WriteLine($"Windows {Environment.OSVersion.Version}; fixture managed workload {lifetime} ms (runtime startup additional); known starts 1; trace available {events.Available}; trace status {events.Failure}; trace starts {traceStarts}; present at later snapshot {later.Processes.Any(p => p.Identity == fixture.Identity)}; loss {observed.Loss}");
        if (lifetime < 5000) Assert.DoesNotContain(later.Processes, p => p.Identity == fixture.Identity);
        // Delivery is optional, never asserted complete on a standard-user account.
    }
}
