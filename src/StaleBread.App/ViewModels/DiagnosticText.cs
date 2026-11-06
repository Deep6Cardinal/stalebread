using StaleBread.Core.Model;
using StaleBread.Core.Storage;

namespace StaleBread.App.ViewModels;

/// <summary>Presentation only: never changes detection, permissions or cleanup eligibility.</summary>
public static class DiagnosticText
{
    public static string MonitorStatus(Coverage coverage, bool paused) => paused ? "Paused" : coverage switch
    {
        Coverage.Complete => "Watching · snapshots and live process events",
        Coverage.SnapshotOnly => "Watching · snapshot monitoring (live process events unavailable)",
        _ => "Limited visibility — cleanup unavailable"
    };

    public static string CollectorMessage(string error)
    {
        if (!error.StartsWith("Process trace unavailable:", StringComparison.Ordinal) && error != "Process trace stopped") return error;
        string reason = error.EndsWith("AccessDenied", StringComparison.Ordinal) || error.EndsWith("UnauthorizedAccessException", StringComparison.Ordinal)
            ? "Windows denied access to live process events." : "Live process events unavailable.";
        return $"{reason} Snapshot scans can miss short-lived processes between scans. Normal monitoring does not require administrator mode. Diagnostic: {error}";
    }

    public static string Title(FindingKind kind) => kind switch
    {
        FindingKind.ObservedOrphan => "Likely leftover helpers",
        FindingKind.PossibleOrphan => "Possible leftover — review only",
        FindingKind.HeavyWorkload => "Heavy workload — busy, not stale",
        FindingKind.Growth => "Growing resource use",
        FindingKind.Churn => "Repeated subprocess activity",
        FindingKind.Accumulation => "Helper accumulation",
        _ => "Leftover still running — review only"
    };

    public static string Title(Finding finding) => (finding.IsClearing ? "Settling — " : "") +
        (finding.Kind == FindingKind.Churn && finding.Members.Count == 0
            ? "Recent subprocess activity — no associated processes at last trigger" : Title(finding.Kind));

    public static string MeasurementSummary(Finding finding)
    {
        string timing = finding.MeasurementsAtUtc is { } at ? $"last matching observation {at.LocalDateTime:G}" : "historical observation; measurement time unavailable";
        string impact = finding.Kind is FindingKind.Churn or FindingKind.Accumulation
            ? "activity advisory; performance impact not established" : $"rule impact {finding.Impact}; not proof of staleness";
        string memory = finding.AggregatePrivateBytes is { } bytes ? $"{bytes / 1048576d:F1} MiB private commit" : "private memory unavailable";
        return $"{finding.Members.Count} associated processes · {memory}\n{timing} · {impact} · evidence {finding.Confidence}";
    }

    public static string MeasurementDetails(Finding finding) => finding.Measurements.Count == 0
        ? "Numeric trigger evidence was not recorded for this finding."
        : string.Join("\n", finding.Measurements.Select(m => $"{m.Trigger} · {m.Scope} · {m.MemberCount} processes · window {m.WindowSeconds:g}s: " + (m.Trigger switch
        {
            TriggerKind.Cpu => $"average {m.CpuMachinePercent:F2}% machine CPU ({m.CpuCoreEquivalents:F2} cores); sustained interval thresholds {m.ThresholdMachinePercent:g}% OR {m.ThresholdCores:g} cores",
            TriggerKind.MemoryPressure => $"{m.PrivateBytes / 1048576d:F1} MiB private; system memory load {m.SystemMemoryLoadPercent:F1}%",
            TriggerKind.MemoryGrowth => $"private bytes {m.BaselinePrivateBytes} → {m.PrivateBytes}",
            TriggerKind.HandleGrowth => $"handles {m.BaselineHandleCount} → {m.HandleCount}",
            TriggerKind.Churn => $"previous/recent windows {m.PreviousWindowStarts} / {m.RecentWindowStarts} observed starts or appearances; threshold {m.ThresholdCount} per window",
            TriggerKind.Accumulation => $"baseline {m.BaselineCount} → {m.MemberCount} helpers (+{m.AddedCount}); qualifying increase persisted {m.PersistenceSeconds:g}s before first alert",
            _ => "Unavailable"
        }))) + "\nScopes may overlap: do not add their counts or memory totals.";

    public static string Cleanup(Finding finding)
    {
        if (finding.State == FindingState.Resolved) return "Resolved findings cannot authorize cleanup. Review current findings instead.";
        if (finding.IsClearing) return "Settling: the trigger is below threshold; waiting for the clear period. No cleanup is authorized by this finding. Measurements shown are from the last matching observation.";
        if (finding.Coverage == Coverage.Degraded) return "Cleanup unavailable: monitoring evidence is incomplete. Wait for healthy scans; missing evidence is not proof of staleness.";
        if (finding.CleanupEligible) return "Eligible for manual review, not proof of safety. Choose Review cleanup… to select exact processes and confirm. Force-stopping can lose work.";
        return finding.Kind switch
        {
            FindingKind.Churn => "Cleanup unavailable for churn: Repeated starts can be normal AI work, not proof of stale processes. Administrator mode does not enable cleanup for this alert.",
            FindingKind.HeavyWorkload => "Cleanup unavailable: this is busy work, not confirmed stale work. Review the originating app and close it normally if no longer needed.",
            FindingKind.Growth or FindingKind.Accumulation => "Cleanup unavailable: increasing resource use or helper counts alone do not establish that processes are safe to stop. Review the originating app.",
            FindingKind.PossibleOrphan => "Cleanup unavailable: the owner's exit was not observed. A missing parent and an idle process are not enough evidence to stop it.",
            FindingKind.ActiveLeftover => "Cleanup unavailable: the owner exited, but this helper still shows activity. Close its originating app normally when possible.",
            _ => "Cleanup unavailable: ownership, ancestry, inactivity or protection checks are not satisfied. Open Details for each process's current cleanup reasons. Administrator mode does not bypass these safeguards."
        };
    }

    public static string History(HistoryEntry entry)
    {
        string stamp = entry.Timestamp.LocalDateTime.ToString("g");
        if (entry.Kind != HistoryKind.Finding || entry.Finding is not { } finding)
            return $"{stamp} · {entry.Kind}\n{entry.Summary}";
        string state = finding.State == FindingState.Resolved ? "Resolved" : "Active when recorded";
        string owner = finding.OwnerIdentity is { } id ? $" · owner PID {id.Pid} (recorded identity {id.CreationFileTimeUtc})" : "";
        string explanation = finding.State == FindingState.Resolved
            ? (finding.ClearAfterSeconds is { } seconds ? $"Trigger below threshold for {seconds:g} seconds of scheduled observations. " : "Trigger no longer met for three scheduled scans. ") +
                "This is not a cleanup result or proof of process exit; see Action entries for any manual cleanup."
            : Cleanup(finding);
        return $"{stamp} · {state} · {Title(finding)}{owner}\n{MeasurementSummary(finding)}\n{explanation}\n{MeasurementDetails(finding)}";
    }
}
