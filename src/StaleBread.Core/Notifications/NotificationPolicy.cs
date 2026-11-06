using StaleBread.Core.Model;
using StaleBread.Core.Graph;
namespace StaleBread.Core.Notifications;

public enum NotificationState { Accepts, Suppressed, Unknown }
public sealed record DesktopState(bool Locked = false, bool Disconnected = false,
    NotificationState Notifications = NotificationState.Accepts, bool ReducedMotion = false);
public sealed record NotificationDecision(IReadOnlyList<string> EpisodeIds, bool ShowPopup, bool PlaySound,
    bool ShowBadge, bool ShowSilentDigest, IReadOnlyList<string> ReasonCodes);
public sealed record EpisodeAcknowledgment(DateTimeOffset? LastNotificationUtc, DateTimeOffset? SnoozeUntilUtc, bool Dismissed);
public sealed record NotificationMemory(int SchemaVersion, DateTimeOffset? LastGlobalUtc,
    IReadOnlyDictionary<string, EpisodeAcknowledgment> Episodes);
public sealed record NotificationContext(TimeProvider Time, TimeZoneInfo TimeZone, DesktopState Desktop, Settings Settings,
    GraphSnapshot? Observation = null, TimeSpan? Cadence = null);
public sealed class NotificationPolicy
{
    private sealed class Episode
    {
        public long First;
        public long? Notified, SnoozeUntil, MissingSince;
        public DateTimeOffset? NotifiedUtc, SnoozeUntilUtc;
        public bool Dismissed, Active;
    }
    private readonly Dictionary<string, Episode> episodes = [];
    private long? global;
    private DateTimeOffset? globalUtc;
    private bool pendingDigest;
    public long Revision { get; private set; }
    private Guid observationRun;
    private long? lastObservation;
    private TimeSpan observationCadence;
    public NotificationDecision Evaluate(IReadOnlyList<Finding> findings, NotificationContext context)
    {
        var time = context.Time; long now = time.GetTimestamp();
        var active = findings.Where(f => f.State == FindingState.Active && !f.IsClearing &&
            !(f.Kind == FindingKind.Churn && f.Members.Count == 0) && f.Coverage != Coverage.Degraded && Qualifies(f)).ToArray();
        foreach (var f in findings.Where(f => f.State == FindingState.Resolved))
            if (episodes.TryGetValue(f.EpisodeKey, out var resolved)) Resolve(resolved);
        ReconcileMissing(findings, context);
        foreach (var f in active)
        {
            if (!episodes.TryGetValue(f.EpisodeKey, out var state))
                episodes[f.EpisodeKey] = state = new() { First = now, Active = true };
            if (!state.Active) { state.Active = true; state.First = now; }
        }
        if (episodes.Count > 2000)
            foreach (string key in episodes.OrderBy(kv => kv.Value.Active).ThenBy(kv => kv.Value.First)
                .Take(episodes.Count - 2000).Select(kv => kv.Key).ToArray()) { episodes.Remove(key); Revision++; }
        var ready = active.Where(f => episodes.TryGetValue(f.EpisodeKey, out var state) && !state.Dismissed &&
            (!state.SnoozeUntil.HasValue || now >= state.SnoozeUntil) &&
            time.GetElapsedTime(state.First, now) >= TimeSpan.FromSeconds(30) &&
            (!state.Notified.HasValue || time.GetElapsedTime(state.Notified.Value, now) >= TimeSpan.FromMinutes(30)))
            .Select(f => f.EpisodeKey).Distinct().ToArray();
        bool badge = active.Any(f => episodes.TryGetValue(f.EpisodeKey, out var e) && !e.Dismissed);
        var local = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(time.GetUtcNow(), context.TimeZone).DateTime);
        var s = context.Settings;
        bool quiet = s.QuietHoursEnabled && (s.QuietStart == s.QuietEnd || (s.QuietStart < s.QuietEnd
            ? local >= s.QuietStart && local < s.QuietEnd : local >= s.QuietStart || local < s.QuietEnd));
        bool suppressed = context.Desktop.Locked || context.Desktop.Disconnected ||
            context.Desktop.Notifications != NotificationState.Accepts || quiet || !s.PopupEnabled;
        if (suppressed)
        {
            pendingDigest |= ready.Length > 0;
            return new([], false, false, badge, false,
                [context.Desktop.Notifications == NotificationState.Unknown ? "NotificationStateUnknown" : "QuietOrBusy"]);
        }
        if (ready.Length == 0) return new([], false, false, badge, false, []);
        if (global.HasValue && time.GetElapsedTime(global.Value, now) < TimeSpan.FromMinutes(5))
            return new([], false, false, badge, false, ["GlobalCooldown"]);
        bool digest = pendingDigest; pendingDigest = false;
        foreach (string key in ready)
        {
            episodes[key].Notified = now; episodes[key].NotifiedUtc = time.GetUtcNow();
        }
        global = now; globalUtc = time.GetUtcNow();
        Revision++;
        return new(ready, true, !digest && s.SoundEnabled && s.Volume > 0, badge, digest, digest ? ["SilentReturnDigest"] : []);
    }
    private void Resolve(Episode episode)
    {
        episode.Active = false; episode.MissingSince = null;
        if (episode.Dismissed) { episode.Dismissed = false; Revision++; }
    }
    private void ReconcileMissing(IReadOnlyList<Finding> findings, NotificationContext context)
    {
        var graph = context.Observation;
        if (graph is null || graph.Coverage == Coverage.Degraded)
        {
            lastObservation = null;
            foreach (var e in episodes.Values) e.MissingSince = null;
            return;
        }
        if (graph.SampleKind != SampleKind.Scheduled) return;
        var cadence = context.Cadence ?? TimeSpan.FromSeconds(5);
        bool newEpoch = observationRun != graph.RunId || observationCadence != cadence ||
            lastObservation is { } previous && (graph.Timestamp < previous || graph.Seconds(previous, graph.Timestamp) > cadence.TotalSeconds * 3);
        if (newEpoch)
        {
            foreach (var e in episodes.Values) e.MissingSince = null;
            lastObservation = null;
        }
        if (lastObservation == graph.Timestamp) return; // UI refreshes are not new observations.
        observationRun = graph.RunId; observationCadence = cadence; lastObservation = graph.Timestamp;
        var present = findings.Where(f => f.State == FindingState.Active).Select(f => f.EpisodeKey).ToHashSet();
        var rules = context.Settings.AdvisoryRules;
        // A reset loses detector windows. Wait at least the longest supported window
        // before interpreting healthy absence as resolution, not merely warm-up.
        double required = new[] { 600d, rules.BusyDuration.TotalSeconds, rules.MemoryDuration.TotalSeconds,
            rules.GrowthDuration.TotalSeconds }.Max();
        foreach (var (key, episode) in episodes)
        {
            if (!episode.Active || present.Contains(key)) { episode.MissingSince = null; continue; }
            episode.MissingSince ??= graph.Timestamp;
            if (graph.Seconds(episode.MissingSince.Value, graph.Timestamp) >= required) Resolve(episode);
        }
    }
    private static bool Qualifies(Finding f) => f.Kind is not (FindingKind.ObservedOrphan or FindingKind.PossibleOrphan or FindingKind.ActiveLeftover)
        || f.Members.Count >= 3 || f.AggregatePrivateBytes >= 128UL * 1024 * 1024;
    public void Snooze(string episode, TimeProvider time)
    {
        if (!episodes.TryGetValue(episode, out var e)) episodes[episode] = e = new() { First = time.GetTimestamp(), Active = true };
        e.SnoozeUntil = time.GetTimestamp() + checked(1800 * time.TimestampFrequency);
        e.SnoozeUntilUtc = time.GetUtcNow().AddMinutes(30);
        Revision++;
    }
    public void Dismiss(string episode)
    { if (episodes.TryGetValue(episode, out var e) && !e.Dismissed) { e.Dismissed = true; Revision++; } }
    public NotificationMemory Export() => new(1, globalUtc, episodes.ToDictionary(kv => kv.Key,
        kv => new EpisodeAcknowledgment(kv.Value.NotifiedUtc, kv.Value.SnoozeUntilUtc, kv.Value.Dismissed)));
    public void Restore(NotificationMemory memory, TimeProvider time)
    {
        if (memory.SchemaVersion != 1) return;
        episodes.Clear(); globalUtc = memory.LastGlobalUtc; lastObservation = null; observationRun = Guid.Empty;
        long now = time.GetTimestamp();
        long Past(DateTimeOffset utc, double cap) => now - (long)(Math.Clamp((time.GetUtcNow() - utc).TotalSeconds, 0, cap) * time.TimestampFrequency);
        global = globalUtc.HasValue ? Past(globalUtc.Value, 300) : null;
        foreach (var (key, value) in memory.Episodes.Take(2000))
            episodes[key] = new() { First = now, Active = true, Dismissed = value.Dismissed,
                NotifiedUtc = value.LastNotificationUtc, SnoozeUntilUtc = value.SnoozeUntilUtc,
                Notified = value.LastNotificationUtc.HasValue ? Past(value.LastNotificationUtc.Value, 1800) : null,
                SnoozeUntil = value.SnoozeUntilUtc.HasValue ? now +
                    (long)(Math.Clamp((value.SnoozeUntilUtc.Value - time.GetUtcNow()).TotalSeconds, 0, 1800) * time.TimestampFrequency) : null };
    }
}
