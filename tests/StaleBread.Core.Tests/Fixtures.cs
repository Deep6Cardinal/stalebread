using StaleBread.Core.Model;
using StaleBread.Core.Graph;

namespace StaleBread.Core.Tests;

public sealed class TestTime : TimeProvider
{
    public override long TimestampFrequency => 1000;
    public double Seconds { get; set; }
    public TimeSpan UtcOffset { get; set; }
    public override long GetTimestamp() => (long)(Seconds * TimestampFrequency);
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddSeconds(Seconds).Add(UtcOffset);
}
public static class Fixtures
{
    public static ProcessSample P(int pid, int parent = 0, string name = "node.exe", long created = 1,
        string? path = null, LaunchMode mode = LaunchMode.NonInteractive) =>
        new(new(pid, created), pid, parent, name, path, "user", 1, TimeSpan.Zero, 0, 0, 0,
            1024, 2048, 10, false, null, mode);
    public static ObservationBatch B(double seconds, params ProcessSample[] processes) =>
        new(DateTimeOffset.Parse("2026-01-01T00:00:00Z").AddSeconds(seconds), (long)(seconds * 1000),
            SampleKind.Scheduled, processes, [], new(40, 1000000000, 12), Coverage.SnapshotOnly, [],
            NativeCountCoverage.Usable, []);
    public static AssociationClassifier Classifier(IReadOnlySet<string>? exclusions = null) =>
        new(new(@"C:\Users\test"), "user", 1, exclusions: exclusions);
    public static TemporalProcessGraph NewGraph() => new(new TestTime(), Classifier());
}
