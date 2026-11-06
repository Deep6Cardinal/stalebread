using System.Text;
using System.Text.Json;
using StaleBread.Core.Model;
namespace StaleBread.Core.Storage;

public sealed record DiagnosticSnapshot(Settings Settings, IReadOnlyList<HistoryEntry> History, Coverage Coverage, string UserProfile, string AccountName);
public sealed record SanitizedExport(string Json, string Csv, string RedactionSummary, IReadOnlyList<string> Errors);
public static class DiagnosticExporter
{
    private static readonly HashSet<string> SafeReasons = new(StringComparer.Ordinal)
    {
        "ObservedOwnerExit", "Quiet120Seconds", "ThreeActiveScheduledIntervals", "ParentAbsentAtFirstObservation",
        "Quiet10Minutes", "OwnerExitNotObserved", "SustainedCpu", "PrivateMemoryAndSystemPressure", "BusyNotStale",
        "MemoryGrowth10Minutes", "HandleGrowth10Minutes", "TwoWindows30Starts", "TwoWindows10NewIdentities",
        "TwentyHelpers", "TenAddedSurvivingHelpers", "TwoWindowsEventStarts", "TwoWindowsSnapshotAppearances",
        "HelperCountAboveThreshold", "AddedHelpersAboveBaseline", "SustainedAccumulation"
    };
    public static SanitizedExport Export(DiagnosticSnapshot snapshot)
    {
        // Allow-list export shape. Never serialize local error summaries, paths, arguments,
        // chat titles, raw episode keys or exact identities, even if a caller supplied them.
        var aliases = new Dictionary<ProcessIdentity, string>();
        string Alias(ProcessIdentity id)
        {
            if (!aliases.TryGetValue(id, out var alias)) aliases[id] = alias = "process-" + (aliases.Count + 1);
            return alias;
        }
        var episodeAliases = new Dictionary<(string, DateTimeOffset), string>();
        string EpisodeAlias(Finding finding)
        {
            var key = (finding.EpisodeKey, finding.FirstSeenUtc);
            if (!episodeAliases.TryGetValue(key, out var alias)) episodeAliases[key] = alias = "episode-" + (episodeAliases.Count + 1);
            return alias;
        }
        var rows = snapshot.History.Select(e => new {
            timestamp = e.Timestamp, kind = e.Kind.ToString(), finding = e.Finding?.Kind.ToString(),
            confidence = e.Finding?.Confidence.ToString(), impact = e.Finding?.Impact.ToString(),
            state = e.Finding?.State.ToString(), memberCount = e.Finding?.Members.Count,
            members = e.Finding?.Members.Select(Alias).ToArray(),
            episode = e.Finding is { } f ? EpisodeAlias(f) : null,
            owner = e.Finding?.OwnerIdentity is { } owner ? Alias(owner) : null,
            identity = e.Identity.HasValue ? Alias(e.Identity.Value) : null,
            privateBytes = e.Finding?.AggregatePrivateBytes, coverage = e.Finding?.Coverage.ToString(),
            measurementsAt = e.Finding?.MeasurementsAtUtc,
            measurementBasis = e.Finding is null ? null : e.Finding.MeasurementsAtUtc is null ? "LegacyTimestampUnavailable" : "LastMatchingObservation",
            clearing = e.Finding?.IsClearing, clearAfterSeconds = e.Finding?.ClearAfterSeconds,
            observedLowerBound = e.Finding?.IsLowerBound,
            impactBasis = e.Finding is null ? null : e.Finding.Kind is FindingKind.Churn or FindingKind.Accumulation
                ? "ActivityAdvisoryNotMeasuredSlowdown" : "RuleClassificationNotProofOfStaleness",
            reasons = e.Finding?.ReasonCodes.Where(SafeReasons.Contains).Distinct().ToArray(),
            measurements = e.Finding?.Measurements.Where(m => Enum.IsDefined(m.Trigger) && Enum.IsDefined(m.Scope)).Select(m => new {
                trigger = m.Trigger.ToString(), scope = m.Scope.ToString(), memberCount = m.MemberCount,
                subject = m.Subject is { } subject ? Alias(subject) : null,
                windowSeconds = m.WindowSeconds, requiredWindowSeconds = m.RequiredWindowSeconds,
                cpuMachinePercent = m.CpuMachinePercent, cpuCoreEquivalents = m.CpuCoreEquivalents,
                systemMemoryLoadPercent = m.SystemMemoryLoadPercent, privateBytes = m.PrivateBytes, baselinePrivateBytes = m.BaselinePrivateBytes,
                handleCount = m.HandleCount, baselineHandleCount = m.BaselineHandleCount,
                previousWindowStarts = m.PreviousWindowStarts, recentWindowStarts = m.RecentWindowStarts,
                thresholdCount = m.ThresholdCount, baselineCount = m.BaselineCount, addedCount = m.AddedCount,
                thresholdAddedCount = m.ThresholdAddedCount, persistenceSeconds = m.PersistenceSeconds,
                thresholdMachinePercent = m.ThresholdMachinePercent, thresholdCores = m.ThresholdCores,
                thresholdBytes = m.ThresholdBytes, thresholdFraction = m.ThresholdFraction, thresholdHandles = m.ThresholdHandles,
                thresholdSystemMemoryLoadPercent = m.ThresholdSystemMemoryLoadPercent
            }).ToArray()
        }).ToArray();
        var json = JsonSerializer.Serialize(new {
            schemaVersion = 2, coverage = snapshot.Coverage.ToString(),
            units = new { cpu = "total-machine percent; CPU evidence is window-average, thresholds apply to each interval",
                memory = "private bytes (commit, not guaranteed free physical RAM)", counts = "observed lower bounds",
                measurements = "last matching observation, not current or reclaimed resources; overlapping scopes are not additive" },
            settings = new { snapshot.Settings.SoundEnabled, snapshot.Settings.Volume, snapshot.Settings.ReducedMotion,
                snapshot.Settings.QuietHoursEnabled, exclusionCount = snapshot.Settings.ExcludedPaths.Count }, entries = rows
        }, new JsonSerializerOptions { WriteIndented = true });
        var csv = new StringBuilder("timestamp,kind,finding,state,episode,owner,memberCount,privateBytes,coverage,measurementsAt,measurementBasis,clearing,impactBasis\r\n");
        foreach (var row in rows) csv.AppendLine(FormattableString.Invariant($"{row.timestamp:O},{row.kind},{row.finding},{row.state},{row.episode},{row.owner},{row.memberCount},{row.privateBytes},{row.coverage},{row.measurementsAt:O},{row.measurementBasis},{row.clearing},{row.impactBasis}"));
        return new(json, csv.ToString(), "Omitted free text, paths, account names, command lines and chat/session titles; process/owner identities and episodes replaced with export-local aliases. JSON includes allow-listed trigger codes and numeric evidence; legacy evidence may be unavailable.", []);
    }
}
