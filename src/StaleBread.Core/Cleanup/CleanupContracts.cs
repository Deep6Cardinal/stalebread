using System.Collections.Immutable;
using StaleBread.Core.Graph;
using StaleBread.Core.Model;
namespace StaleBread.Core.Cleanup;

public sealed record CleanupProposal(Guid Id, ImmutableArray<ProcessIdentity> Identities,
    ImmutableDictionary<ProcessIdentity, ProcessIdentity?> Topology, Guid RunId, string PolicyVersion,
    string SettingsFingerprint, long Created, long Expires, ImmutableArray<string> Risks);
public sealed record UserConfirmation(Guid ProposalId, long Timestamp);
public enum StopResult { Exited, Skipped, Denied, Pending }
public sealed record ProcessStopOutcome(ProcessIdentity Identity, StopResult Result, string Reason);
public sealed record CleanupOutcome(Guid ProposalId, IReadOnlyList<ProcessStopOutcome> Processes,
    bool AuditRecorded, bool Partial, SystemSample Before);
public interface ICleanupAudit
{
    bool Available { get; }
    Task<bool> RecordAsync(Guid proposal, ProcessIdentity? identity, string result, CancellationToken ct);
}
public sealed class PreparedCleanup : IDisposable
{
    internal PreparedCleanup(CleanupProposal proposal, IReadOnlyDictionary<ProcessIdentity, IValidatedProcessHandle> handles,
        GraphSnapshot graph, Guid coordinator) { Proposal = proposal; Handles = handles; Before = graph; Coordinator = coordinator; }
    public CleanupProposal Proposal { get; }
    internal IReadOnlyDictionary<ProcessIdentity, IValidatedProcessHandle> Handles { get; }
    internal GraphSnapshot Before { get; }
    internal Guid Coordinator { get; }
    private int used;
    internal bool Take() => Interlocked.Exchange(ref used, 1) == 0;
    public void Dispose() { Interlocked.Exchange(ref used, 1); foreach (var h in Handles.Values) h.Dispose(); }
}
