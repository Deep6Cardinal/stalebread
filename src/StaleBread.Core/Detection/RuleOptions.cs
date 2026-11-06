namespace StaleBread.Core.Detection;

public sealed record RuleOptions
{
    public string Version => "2";
    public static TimeSpan AccumulationPersistence => TimeSpan.FromMinutes(2);
    public static TimeSpan ActivityClearDuration => TimeSpan.FromMinutes(2);
    public double BusyCores { get; init; } = .8;
    public double BusyMachinePercent { get; init; } = 20;
    public TimeSpan BusyDuration { get; init; } = TimeSpan.FromSeconds(60);
    public ulong PressurePrivateBytes { get; init; } = 1UL << 30;
    public double PressureLoadPercent { get; init; } = 85;
    public TimeSpan MemoryDuration { get; init; } = TimeSpan.FromSeconds(120);
    public ulong GrowthBytes { get; init; } = 512UL << 20;
    public double GrowthFraction { get; init; } = .25;
    public int GrowthHandles { get; init; } = 2000;
    public double HandleGrowthFraction { get; init; } = .5;
    public TimeSpan GrowthDuration { get; init; } = TimeSpan.FromMinutes(10);
    public int EventStarts { get; init; } = 30;
    public int SnapshotBirths { get; init; } = 10;
    public int AccumulationCount { get; init; } = 20;
    public int AccumulationAdded { get; init; } = 10;
}
