namespace StaleBread.Core.Model;

public readonly record struct ProcessIdentity(int Pid, long CreationFileTimeUtc);
public enum Coverage { Complete, SnapshotOnly, Degraded }
public enum EvidenceConfidence { Low, Medium, High }
public enum FindingKind { ObservedOrphan, PossibleOrphan, HeavyWorkload, Growth, Churn, Accumulation, ActiveLeftover }
public enum Impact { Low, Moderate, High }
public enum FindingState { Active, Resolved }
public enum SampleKind { Scheduled, OnDemand }
public enum NativeCountCoverage { Usable, Partial }
public enum ProcessRole { AgentRoot, Helper, InteractiveHost, ConsoleHost, Unknown }
public enum OwnerKind { Cli, Desktop, Ide, Unknown }
public enum LaunchMode { NonInteractive, Interactive, Unknown }
public enum TriggerKind { Cpu, MemoryPressure, MemoryGrowth, HandleGrowth, Churn, Accumulation }
public enum EvidenceScope { Process, HelperGroup, AssociatedFamily }

// Typed numeric evidence only. Each trigger retains its own scope; overlapping measurements are not additive.
public sealed record TriggerMeasurement(TriggerKind Trigger, EvidenceScope Scope, int MemberCount,
    ProcessIdentity? Subject = null, double? WindowSeconds = null, double? CpuMachinePercent = null,
    double? CpuCoreEquivalents = null, double? SystemMemoryLoadPercent = null,
    ulong? PrivateBytes = null, ulong? BaselinePrivateBytes = null, int? HandleCount = null,
    int? BaselineHandleCount = null, int? PreviousWindowStarts = null, int? RecentWindowStarts = null,
    int? ThresholdCount = null, int? BaselineCount = null, int? AddedCount = null, int? ThresholdAddedCount = null,
    double? PersistenceSeconds = null, double? ThresholdMachinePercent = null,
    double? ThresholdCores = null, ulong? ThresholdBytes = null, double? ThresholdFraction = null,
    int? ThresholdHandles = null, double? ThresholdSystemMemoryLoadPercent = null, double? RequiredWindowSeconds = null);

public sealed record ProcessSample(ProcessIdentity? Identity, int Pid, int ParentPid,
    string Name, string? CanonicalImagePath = null, string? OwnerSid = null, int? SessionId = null,
    TimeSpan? CpuTime = null, ulong? ReadBytes = null, ulong? WriteBytes = null, ulong? OtherBytes = null,
    ulong? PrivateBytes = null, ulong? WorkingSetBytes = null, int? HandleCount = null,
    bool? HasVisibleWindow = null, string? KnownAiMarker = null, LaunchMode LaunchMode = LaunchMode.Unknown);
public sealed record SystemSample(double? MemoryLoadPercent, ulong? AvailablePhysicalBytes, int LogicalProcessorCount);
public sealed record ProcessEvent(int Pid, int ParentPid, string Name, bool IsStart,
    long MonotonicTicks, long? EventFileTimeUtc, string? OwnerSid, int? SessionId,
    ProcessIdentity? ValidatedIdentity, ProcessIdentity? AttributedOwner);
public sealed record ObservationBatch(DateTimeOffset Utc, long MonotonicTicks, SampleKind SampleKind,
    IReadOnlyList<ProcessSample> Processes, IReadOnlyList<ProcessEvent> Events, SystemSample System,
    Coverage Coverage, IReadOnlyList<string> Gaps, NativeCountCoverage NativeCountCoverage,
    IReadOnlyList<string> CountGaps);
public sealed record Finding(string EpisodeKey, FindingKind Kind, EvidenceConfidence Confidence,
    IReadOnlyList<ProcessIdentity> Members, IReadOnlyList<string> ReasonCodes, bool CleanupEligible,
    DateTimeOffset FirstSeenUtc, DateTimeOffset LastSeenUtc, Impact Impact, string RuleVersion,
    ProcessIdentity? OwnerIdentity, IReadOnlyList<string> SatisfiedConditions, IReadOnlyList<string> MissingEvidence,
    ulong? AggregatePrivateBytes, Coverage Coverage, bool IsLowerBound, FindingState State)
{
    public DateTimeOffset? MeasurementsAtUtc { get; init; }
    public IReadOnlyList<TriggerMeasurement> Measurements { get; init; } = [];
    public bool IsClearing { get; init; }
    public double? ClearAfterSeconds { get; init; }
}
public interface IProcessCollector { ValueTask<ObservationBatch> CollectAsync(CancellationToken ct); }
