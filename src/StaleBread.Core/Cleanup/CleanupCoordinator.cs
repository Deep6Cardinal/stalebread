using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace StaleBread.Core.Cleanup;

public sealed class CleanupCoordinator(IProcessHandleFactory handles, IFreshSnapshotSource snapshots,
    CleanupPolicy policy, Func<Settings> settings, TimeProvider time, ICleanupAudit audit)
{
    private readonly Guid coordinator = Guid.NewGuid();
    private readonly Dictionary<Guid, CleanupProposal> issued = [];
    public CleanupProposal Propose(IReadOnlyList<ProcessIdentity> identities, GraphSnapshot graph)
    {
        var selected = identities.Distinct().ToImmutableArray();
        if (selected.Length == 0 || selected.Length > 2000) throw new InvalidOperationException("Select explicit eligible identities");
        var set = selected.ToHashSet();
        foreach (var id in selected)
        {
            if (!policy.Evaluate(id, graph, settings()).Eligible) throw new InvalidOperationException("Selection is not eligible");
            if (!CleanupPolicy.Descendants(id, graph).All(set.Contains))
                throw new InvalidOperationException("Every affected descendant must be explicitly selected");
        }
        long now = time.GetTimestamp();
        var proposal = new CleanupProposal(Guid.NewGuid(), selected,
            selected.ToImmutableDictionary(id => id, id => graph.Current[id].Parent), graph.RunId,
            graph.CatalogVersion + "/cleanup-1", Fingerprint(), now, now + checked(30 * time.TimestampFrequency),
            ["Force termination can lose work, output and locks.", "This cannot be undone.", "Close the originating app normally when possible."]);
        foreach (var key in issued.Where(kv => kv.Value.Expires < now).Select(kv => kv.Key).ToArray()) issued.Remove(key);
        if (issued.Count >= 16) throw new InvalidOperationException("Too many outstanding reviews");
        issued[proposal.Id] = proposal;
        return proposal;
    }
    public async Task<PreparedCleanup> PrepareAsync(CleanupProposal proposal, UserConfirmation confirmation, CancellationToken ct = default)
    {
        if (!issued.TryGetValue(proposal.Id, out var original) || !ReferenceEquals(original, proposal) ||
            confirmation.ProposalId != proposal.Id || confirmation.Timestamp < proposal.Created ||
            confirmation.Timestamp > time.GetTimestamp())
            throw new InvalidOperationException("Confirmation does not match the issued proposal");
        issued.Remove(proposal.Id);
        var opened = new Dictionary<ProcessIdentity, IValidatedProcessHandle>();
        try
        {
            ct.ThrowIfCancellationRequested(); CheckExpiry(proposal);
            if (!audit.Available || !await audit.RecordAsync(proposal.Id, null, "PrepareRequested", ct).ConfigureAwait(false))
                throw new InvalidOperationException("Audit unavailable; no action permitted");
            foreach (var id in proposal.Identities)
            {
                ct.ThrowIfCancellationRequested();
                opened[id] = handles.Open(id, ProcessAccess.Query | ProcessAccess.Terminate | ProcessAccess.Synchronize);
            }
            var graph = await Fresh(ct).ConfigureAwait(false);
            Validate(proposal, graph, opened, ImmutableHashSet<ProcessIdentity>.Empty);
            return new(proposal, opened.ToImmutableDictionary(), graph, coordinator);
        }
        catch { foreach (var h in opened.Values) h.Dispose(); throw; }
    }
    public async Task<CleanupOutcome> ExecuteAsync(PreparedCleanup prepared, CancellationToken ct = default)
    {
        if (prepared.Coordinator != coordinator || !prepared.Take()) throw new InvalidOperationException("Prepared batch already used or foreign");
        var proposal = prepared.Proposal;
        var results = proposal.Identities.ToDictionary(id => id, id => new ProcessStopOutcome(id, StopResult.Skipped, "Not attempted"));
        var completed = new HashSet<ProcessIdentity>();
        bool recorded = true;
        int attempted = 0;
        long start = time.GetTimestamp();
        try
        {
            ct.ThrowIfCancellationRequested();
            Validate(proposal, await Fresh(ct).ConfigureAwait(false), prepared.Handles, completed);
            if (!audit.Available || !await audit.RecordAsync(proposal.Id, null, "ExecuteConfirmed", ct).ConfigureAwait(false))
            { recorded = false; throw new InvalidOperationException("Audit unavailable"); }
            int Depth(ProcessIdentity id)
            {
                int depth = 0; var seen = new HashSet<ProcessIdentity>();
                while (seen.Add(id) && proposal.Topology.TryGetValue(id, out var parent) && parent.HasValue)
                { id = parent.Value; depth++; }
                return depth;
            }
            foreach (var id in proposal.Identities.OrderByDescending(Depth))
            {
                ct.ThrowIfCancellationRequested();
                if (time.GetElapsedTime(start) >= TimeSpan.FromSeconds(15)) break;
                if (!audit.Available || !await audit.RecordAsync(proposal.Id, id,
                    "ForceStopIntent; R1 v1; observed CLI owner exit and quiet >=120s; exact identity and ownership required", ct).ConfigureAwait(false))
                { recorded = false; break; }
                Validate(proposal, await Fresh(ct).ConfigureAwait(false), prepared.Handles, completed);
                if (time.GetElapsedTime(start) >= TimeSpan.FromSeconds(15)) break;
                var h = prepared.Handles[id];
                // Final check on the held object, never a new lookup by PID/name.
                if (!h.IsAlive || h.Identity != id) break;
                attempted++;
                try
                {
                    h.Terminate();
                    var remaining = TimeSpan.FromSeconds(15) - time.GetElapsedTime(start);
                    var wait = remaining < TimeSpan.FromSeconds(5) ? remaining : TimeSpan.FromSeconds(5);
                    bool exited = wait > TimeSpan.Zero && await h.WaitForExitAsync(wait, ct).WaitAsync(wait, time, ct).ConfigureAwait(false);
                    results[id] = new(id, exited ? StopResult.Exited : StopResult.Pending, exited ? "Selected process exited" : "Exit not confirmed");
                    if (!await audit.RecordAsync(proposal.Id, id, results[id].Result.ToString(), CancellationToken.None).ConfigureAwait(false))
                    { recorded = false; break; }
                    if (!exited) break;
                    completed.Add(id);
                }
                catch (Exception e) when (e is UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    results[id] = new(id, StopResult.Denied, e.GetType().Name);
                    recorded &= await audit.RecordAsync(proposal.Id, id, "Denied", CancellationToken.None).ConfigureAwait(false);
                    break;
                }
                catch (Exception e) when (e is TimeoutException or OperationCanceledException)
                {
                    results[id] = new(id, StopResult.Pending, "Cancellation or timeout after request; exit unconfirmed");
                    recorded &= await audit.RecordAsync(proposal.Id, id, "Pending", CancellationToken.None).ConfigureAwait(false);
                    break;
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or OperationCanceledException or TimeoutException or IOException or UnauthorizedAccessException)
        {
            foreach (var id in results.Keys.ToArray())
                if (results[id].Result == StopResult.Skipped) results[id] = new(id, StopResult.Skipped, "Batch stopped: " + e.GetType().Name);
        }
        finally { prepared.Dispose(); }
        return new(proposal.Id, results.Values.ToArray(), recorded, attempted > 0 && completed.Count != proposal.Identities.Length, prepared.Before.System);
    }
    private async Task<GraphSnapshot> Fresh(CancellationToken ct) =>
        await snapshots.GetAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), time, ct).ConfigureAwait(false);
    private string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(settings()))));
    private void CheckExpiry(CleanupProposal proposal)
    {
        long now = time.GetTimestamp();
        if (now < proposal.Created || now > proposal.Expires) throw new InvalidOperationException("Review expired; refresh the explicit selection");
        if (Fingerprint() != proposal.SettingsFingerprint) throw new InvalidOperationException("Settings changed since review");
    }
    private void Validate(CleanupProposal proposal, GraphSnapshot graph,
        IReadOnlyDictionary<ProcessIdentity, IValidatedProcessHandle> opened, IReadOnlySet<ProcessIdentity> completed)
    {
        CheckExpiry(proposal);
        if (graph.RunId != proposal.RunId || graph.Coverage == Coverage.Degraded ||
            graph.CatalogVersion + "/cleanup-1" != proposal.PolicyVersion ||
            time.GetTimestamp() < graph.Timestamp || time.GetElapsedTime(graph.Timestamp) > TimeSpan.FromSeconds(5))
            throw new InvalidOperationException("Fresh complete observation required");
        foreach (var id in proposal.Identities.Where(id => !completed.Contains(id)))
        {
            if (!policy.Evaluate(id, graph, settings()).Eligible || !graph.Current.TryGetValue(id, out var node) ||
                node.Parent != proposal.Topology[id]) throw new InvalidOperationException("Policy or topology changed");
            var expected = proposal.Identities.Where(x => !completed.Contains(x)).ToHashSet();
            if (!CleanupPolicy.Descendants(id, graph).Where(x => !completed.Contains(x)).All(expected.Contains))
                throw new InvalidOperationException("Unapproved descendant appeared");
            if (!opened.TryGetValue(id, out var h) || h.Identity != id || !h.IsAlive ||
                h.OwnerSid is null || h.OwnerSid != node.Sample.OwnerSid || !h.SessionId.HasValue || h.SessionId != node.Sample.SessionId)
                throw new InvalidOperationException("Held identity, ownership or liveness changed");
        }
    }
}
