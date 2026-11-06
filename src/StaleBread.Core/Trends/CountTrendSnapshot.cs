using System.Collections.Frozen;
using StaleBread.Core.Graph;
using StaleBread.Core.Model;

namespace StaleBread.Core.Trends;

public sealed record CountScope(int SessionId, string Version, IReadOnlySet<ProcessIdentity> SelfOwned)
{
    public static FrozenSet<string> Names => ClassifierCatalog.WatchNames;
}
public sealed record CountDelta(TimeSpan Horizon, int? Net, int? Additions, int? Removals,
    TimeSpan? ActualInterval, string? UnavailableReason)
{
    public string Pair => Additions.HasValue && Removals.HasValue ? $"+{Additions} / −{Removals}" : "— / —";
    public string Spoken => Additions.HasValue && Removals.HasValue
        ? $"{Additions} appeared, {Removals} disappeared in {Horizon.TotalMinutes:g} minutes"
        : UnavailableReason ?? "Collecting history";
}
public sealed record CountTrendSnapshot(int? CurrentCount, long? SampleTimestamp, bool Stale, bool Partial,
    IReadOnlyList<CountDelta> Deltas, string? HistoryReason, long Epoch);
