using StaleBread.App.Services;
using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using StaleBread.Core.Trends;
namespace StaleBread.Windows.Tests;

public class MonitorTests
{
 [Fact] public async Task SleepWakeDoesNotCancelManualPause()
 {
  var c = new Collector(); await using var monitor = Create(c); await monitor.StartAsync(); await monitor.GetAsync(default);
  monitor.SetPaused(true); monitor.SetSystemSuspended(true); monitor.SetSystemSuspended(false);
  Assert.True(monitor.State.Paused); await Assert.ThrowsAnyAsync<Exception>(() => monitor.GetAsync(default));
 }
 private sealed class Collector : IProcessCollector
 {
  public int Calls, Active, MaxConcurrent; public bool Fail;
  public async ValueTask<ObservationBatch> CollectAsync(CancellationToken ct)
  {
   int n = Interlocked.Increment(ref Active); MaxConcurrent = Math.Max(MaxConcurrent, n); Interlocked.Increment(ref Calls);
   try
   {
    await Task.Delay(30, ct);
    if (Fail) throw new IOException("test collector failure");
    return new(DateTimeOffset.UtcNow, TimeProvider.System.GetTimestamp(), SampleKind.Scheduled,
      [new(new(123, 1), 123, 0, "node.exe", SessionId: 1)], [], new(40, 1000, 12),
      Coverage.SnapshotOnly, [], NativeCountCoverage.Usable, []);
   }
   finally { Interlocked.Decrement(ref Active); }
  }
 }
 private static MonitorService Create(Collector c) => new(c, TimeProvider.System,
  new(new(@"C:\Users\test"), "user", 1), new CountScope(1, "1", new HashSet<ProcessIdentity>()), new("user", 1), () => new(), () => TimeSpan.Zero);
 [Fact] public async Task ConcurrentFreshRequestsShareCanonicalGraphAndNeverOverlapCollection()
 {
  var c = new Collector(); await using var monitor = Create(c); await monitor.StartAsync();
  var first = await monitor.GetAsync(default);
  var many = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => monitor.GetAsync(default)));
  Assert.All(many, graph => Assert.Equal(first.RunId, graph.RunId));
  Assert.Equal(1, c.MaxConcurrent); Assert.InRange(c.Calls, 2, 5);
  Assert.Equal(1, monitor.State.Counts.CurrentCount);
 }
 [Fact] public async Task PauseAndResumeNeverReturnOldCleanupAuthorization()
 {
  var c = new Collector(); await using var monitor = Create(c); await monitor.StartAsync();
  await monitor.GetAsync(default);
  monitor.SetPaused(true);
  await Assert.ThrowsAnyAsync<Exception>(() => monitor.GetAsync(default));
  Assert.True(monitor.State.Paused); Assert.True(monitor.State.Counts.Stale);
  monitor.SetPaused(false);
  await Assert.ThrowsAnyAsync<Exception>(() => monitor.GetAsync(default));
 }
 [Fact] public async Task CollectorFailureIsVisibleAndCannotReturnHealthyGraph()
 {
  var c = new Collector { Fail = true }; await using var monitor = Create(c); await monitor.StartAsync();
  await Assert.ThrowsAnyAsync<Exception>(() => monitor.GetAsync(default));
  Assert.Equal(Coverage.Degraded, monitor.State.Coverage);
  Assert.NotEmpty(monitor.State.Errors);
 }
 [Fact] public async Task CancellingOneWaiterDoesNotCancelOtherFreshRequests()
 {
  var c = new Collector(); await using var monitor = Create(c); await monitor.StartAsync();
  using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
  await Assert.ThrowsAnyAsync<OperationCanceledException>(() => monitor.GetAsync(cancelled.Token));
  Assert.NotNull(await monitor.GetAsync(default));
 }
 private sealed class Clock : TimeProvider
 {
  public double Seconds; public override long TimestampFrequency => 1000;
  public override long GetTimestamp() => (long)(Seconds * 1000);
 }
 [Fact] public void EconomyRequiresSustainedOverheadAndMinimumFiveMinuteDwell()
 {
  var clock = new Clock(); var controller = new EconomyController(clock);
  controller.Observe(TimeSpan.Zero, 1);
  for (int i = 5; i <= 65; i += 5) { clock.Seconds = i; controller.Observe(TimeSpan.FromSeconds(i * .03), 1); }
  Assert.True(controller.Economy);
  for (int i = 70; i <= 360; i += 5) { clock.Seconds = i; controller.Observe(TimeSpan.FromSeconds(1.95), 1); }
  Assert.True(controller.Economy);
  clock.Seconds = 365; controller.Observe(TimeSpan.FromSeconds(1.95), 1);
  Assert.False(controller.Economy);
 }
}
