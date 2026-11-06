using StaleBread.Core.Trends;
namespace StaleBread.App.ViewModels;

public static class CountText
{
    public static string Compact(CountTrendSnapshot counts)
    {
        var pair = counts.Deltas.FirstOrDefault()?.Pair ?? "— / —";
        string status = counts.Partial ? " · limited count" : counts.Stale ? " · stale" : "";
        return $"{counts.CurrentCount?.ToString() ?? "—"} monitored · {pair} in 60s{status}";
    }
    public static string Horizon(TimeSpan span) => span.TotalSeconds == 60 ? "60s" : $"{span.TotalMinutes:g}m";
    public static string Details(CountDelta delta) => delta.ActualInterval is { } interval
        ? $"{delta.Spoken}. Actual interval {interval.TotalSeconds:F1} seconds; net {delta.Net:+0;-0;0}. Snapshot observations can miss short-lived processes."
        : delta.UnavailableReason ?? "Collecting history";
}
public sealed class WindowLifetime
{
    private bool explained = false;
    public bool ExitRequested { get; set; }
    public (bool HideToTray, bool Explain) Close()
    {
        if (ExitRequested) return (false, false);
        var result = (true, !explained);
        explained = true;
        return result;
    }
}
