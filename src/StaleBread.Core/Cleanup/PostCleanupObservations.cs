using StaleBread.Core.Model;
namespace StaleBread.Core.Cleanup;

public sealed record PostCleanupObservation(Guid ProposalId, int RequestedSeconds, SystemSample Before,
    SystemSample After, int SelectedStillObserved, string Explanation);
public sealed class PostCleanupObservations(IFreshSnapshotSource source, TimeProvider time,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public async Task RunAsync(CleanupOutcome outcome, Action<PostCleanupObservation> publish, CancellationToken ct)
    {
        long started = time.GetTimestamp();
        foreach (int seconds in new[] { 5, 30 })
        {
            try
            {
                TimeSpan remaining = TimeSpan.FromSeconds(seconds) - time.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                    await (delay is null ? Task.Delay(remaining, time, ct) : delay(remaining, ct)).ConfigureAwait(false);
                var graph = await source.GetAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), time, ct).ConfigureAwait(false);
                if (time.GetElapsedTime(graph.Timestamp) > TimeSpan.FromSeconds(5)) continue;
                publish(new(outcome.ProposalId, seconds, outcome.Before, graph.System,
                    outcome.Processes.Count(p => graph.Current.ContainsKey(p.Identity)),
                    "Observed global changes are not solely attributable to StaleBread; no typing-speed improvement is guaranteed."));
            }
            catch (OperationCanceledException) { return; }
            catch (Exception e) when (e is TimeoutException or InvalidOperationException or IOException) { /* Measurement unavailable, never invented. */ }
        }
    }
}
