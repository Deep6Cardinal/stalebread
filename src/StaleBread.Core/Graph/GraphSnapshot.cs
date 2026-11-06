using StaleBread.Core.Model;

namespace StaleBread.Core.Graph;

public sealed record MetricPoint(long Timestamp, SampleKind Kind, ProcessSample Sample);
public sealed record MetricInterval(long Start, long End, double? CoreEquivalents, double? IoBytesPerSecond)
{
    public bool Valid => CoreEquivalents.HasValue && IoBytesPerSecond.HasValue;
    public bool Quiet => Valid && CoreEquivalents < .02 && IoBytesPerSecond < 65536;
}
public sealed record ProcessNode(ProcessSample Sample, long FirstSeen, long LastSeen, long? ExitedAt,
    ProcessIdentity? Parent, bool ParentKnown, IReadOnlyList<MetricPoint> Metrics);
public sealed record AssociationEvidence(ProcessRole Role, bool IsProtected, OwnerKind OwnerKind,
    ProcessIdentity? AgentRoot, string? MarkerId, LaunchMode LaunchMode, bool LineageComplete,
    IReadOnlyList<string> ReasonCodes);
public sealed record GraphSnapshot(Guid RunId, DateTimeOffset Utc, long Timestamp, SampleKind SampleKind,
    IReadOnlyDictionary<ProcessIdentity, ProcessNode> Current,
    IReadOnlyDictionary<ProcessIdentity, ProcessNode> Retained,
    IReadOnlyDictionary<ProcessIdentity, AssociationEvidence> Associations,
    SystemSample System, Coverage Coverage, IReadOnlyList<string> Gaps, string CatalogVersion,
    long TimestampFrequency, IReadOnlyList<ProcessEvent> Events)
{
    public ProcessNode? Find(ProcessIdentity id) => Current.GetValueOrDefault(id) ?? Retained.GetValueOrDefault(id);
    public double Seconds(long start, long end) => (end - start) / (double)TimestampFrequency;
}
