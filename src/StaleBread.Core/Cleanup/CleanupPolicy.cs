using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using StaleBread.Core.Detection;
namespace StaleBread.Core.Cleanup;

public sealed record EligibilityDecision(bool Eligible, IReadOnlyList<string> ReasonCodes);
public sealed class CleanupPolicy(string currentSid, int sessionId)
{
    public static bool IsExcluded(ProcessIdentity id, GraphSnapshot graph, Settings settings)
    {
        if (settings.IgnoredProcesses.Contains(id)) return true;
        var seen = new HashSet<ProcessIdentity>();
        while (seen.Add(id) && graph.Find(id) is { } node)
        {
            if (node.Sample.CanonicalImagePath is { } path && settings.ExcludedPaths.Contains(path, StringComparer.OrdinalIgnoreCase)) return true;
            if (node.Parent is not { } parent) break;
            id = parent;
        }
        return false;
    }
    public EligibilityDecision Evaluate(ProcessIdentity id, GraphSnapshot graph, Settings settings)
    {
        var reasons = new List<string>();
        if (graph.Coverage == Coverage.Degraded) reasons.Add("IncompleteGraph");
        Check(id, graph, settings, reasons);
        // Every live descendant must be independently eligible. Unknown and protected children
        // block the parent; no arbitrary process tree is silently added to the selection.
        var descendants = Descendants(id, graph);
        foreach (var child in descendants)
        {
            var childReasons = new List<string>();
            Check(child, graph, settings, childReasons);
            if (childReasons.Count > 0) reasons.Add("IneligibleDescendant");
        }
        return new(reasons.Count == 0, reasons.Distinct().ToArray());
    }
    public static IReadOnlySet<ProcessIdentity> Descendants(ProcessIdentity id, GraphSnapshot graph)
    {
        var found = new HashSet<ProcessIdentity>();
        var visited = new HashSet<ProcessIdentity> { id };
        var queue = new Queue<ProcessIdentity>(); queue.Enqueue(id);
        while (queue.TryDequeue(out var parent))
            foreach (var (childId, child) in graph.Current.Concat(graph.Retained))
                if (childId != id && (child.Parent == parent || (!child.ParentKnown && child.Sample.ParentPid == parent.Pid)) &&
                    visited.Add(childId))
                { queue.Enqueue(childId); if (graph.Current.ContainsKey(childId)) found.Add(childId); }
        return found;
    }
    private void Check(ProcessIdentity id, GraphSnapshot graph, Settings settings, List<string> reasons)
    {
        if (!graph.Current.TryGetValue(id, out var node) || !graph.Associations.TryGetValue(id, out var association))
        { reasons.Add("IdentityNoLongerObserved"); return; }
        var p = node.Sample;
        if (p.Identity != id) reasons.Add("IdentityMismatch");
        if (association.Role != ProcessRole.Helper || association.IsProtected) reasons.Add("ProtectedOrUnclassified");
        if (association.OwnerKind != OwnerKind.Cli) reasons.Add("NotVerifiedCliOwnership");
        if (!association.LineageComplete || association.ReasonCodes.Contains("LiveAgentAncestor")) reasons.Add("UnknownOrLiveAncestry");
        if (p.OwnerSid != currentSid || p.SessionId != sessionId || p.SessionId == 0) reasons.Add("OwnershipUnverifiedOrOutOfScope");
        if (p.HasVisibleWindow != false) reasons.Add("VisibleOrUnknownWindow");
        if (ClassifierCatalog.IsInterpreter(p.Name) && p.LaunchMode != LaunchMode.NonInteractive) reasons.Add("InteractiveOrUnknownMode");
        if (IsExcluded(id, graph, settings))
            reasons.Add("UserExclusion");
        if (association.AgentRoot is not { } root || graph.Find(root)?.ExitedAt is not { } exited ||
            graph.Seconds(exited, graph.Timestamp) < 120) reasons.Add("OwnerExitNotConfirmedFor120Seconds");
        if (graph.Seconds(node.FirstSeen, graph.Timestamp) < 120) reasons.Add("StartupObservationGrace");
        if (!WindowTracker.Sustained(node, graph, TimeSpan.FromSeconds(120), i => i.Quiet))
            reasons.Add("NotContinuouslyQuietFor120Seconds");
    }
}
