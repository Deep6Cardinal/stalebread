using StaleBread.Core.Cleanup;
using StaleBread.Core.Model;
namespace StaleBread.Core.Storage;

public sealed class CleanupAuditWriter(HistoryStore history, TimeProvider time) : ICleanupAudit
{
    public bool Available => history.AuditHealthy;
    public async Task<bool> RecordAsync(Guid proposal, ProcessIdentity? identity, string result, CancellationToken ct)
        => (await history.AppendAsync(new(1, time.GetUtcNow(), HistoryKind.Action,
            $"Proposal {proposal:N}: {result}", Identity: identity), ct).ConfigureAwait(false)).Success;
}
