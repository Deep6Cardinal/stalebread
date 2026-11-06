using StaleBread.Core.Graph;
using StaleBread.Core.Model;
namespace StaleBread.Core.Detection;

public static class WindowTracker
{
    public static IReadOnlyList<MetricInterval> Intervals(ProcessNode node, long frequency, bool scheduledOnly = false)
    {
        var points = node.Metrics.Where(p => !scheduledOnly || p.Kind == SampleKind.Scheduled).ToArray();
        var intervals = new List<MetricInterval>();
        for (int i = 1; i < points.Length; i++)
        {
            var a = points[i - 1]; var b = points[i];
            double seconds = (b.Timestamp - a.Timestamp) / (double)frequency;
            double? cpu = seconds > 0 && a.Sample.CpuTime.HasValue && b.Sample.CpuTime >= a.Sample.CpuTime
                ? (b.Sample.CpuTime.Value - a.Sample.CpuTime.Value).TotalSeconds / seconds : null;
            double? io = seconds > 0 ? Delta(a.Sample.ReadBytes, b.Sample.ReadBytes) +
                Delta(a.Sample.WriteBytes, b.Sample.WriteBytes) + Delta(a.Sample.OtherBytes, b.Sample.OtherBytes) : null;
            intervals.Add(new(a.Timestamp, b.Timestamp, cpu, io / seconds));
        }
        return intervals;
    }
    private static double? Delta(ulong? a, ulong? b) => a.HasValue && b >= a ? (double)(b.Value - a.Value) : null;
    public static IReadOnlyList<MetricPoint> Points(ProcessNode node, GraphSnapshot graph, TimeSpan duration)
    {
        var baseline = node.Metrics.LastOrDefault(p => graph.Seconds(p.Timestamp, graph.Timestamp) >= duration.TotalSeconds);
        return baseline is null || node.Metrics.Count == 0 || node.Metrics[^1].Timestamp != graph.Timestamp
            ? [] : node.Metrics.Where(p => p.Timestamp >= baseline.Timestamp).ToArray();
    }
    public static bool Sustained(ProcessNode node, GraphSnapshot graph, TimeSpan duration, Func<MetricInterval, bool> predicate)
    {
        var points = Points(node, graph, duration);
        if (points.Count < 2) return false;
        var intervals = Intervals(node with { Metrics = points }, graph.TimestampFrequency);
        return intervals.Count > 0 && intervals.All(i => i.End > i.Start && predicate(i));
    }
}
