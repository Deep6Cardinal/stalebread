using StaleBread.Core.Cleanup;
using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using static StaleBread.Core.Tests.Fixtures;
namespace StaleBread.Core.Tests;

public class PostCleanupTests
{
    private sealed class Source(TestTime time) : IFreshSnapshotSource
    {
        public Task<GraphSnapshot> GetAsync(CancellationToken ct) => Task.FromResult(NewGraph().Apply(B(time.Seconds)));
    }
    [Fact] public async Task ObservesFiveAndThirtySecondsWithoutAttributingGlobalDeltaToCleanup()
    {
        var time = new TestTime();
        var waits = new List<double>(); var values = new List<PostCleanupObservation>();
        var observer = new PostCleanupObservations(new Source(time), time, (span, _) =>
        { waits.Add(span.TotalSeconds); time.Seconds += span.TotalSeconds; return Task.CompletedTask; });
        await observer.RunAsync(new(Guid.NewGuid(), [new(new(1, 1), StopResult.Exited, "Exited")], true, false, new(40, 1000, 12)),
            values.Add, default);
        Assert.Equal([5d, 25d], waits);
        Assert.Equal([5, 30], values.Select(v => v.RequestedSeconds));
        Assert.All(values, v => Assert.Contains("not solely attributable", v.Explanation));
    }
}
