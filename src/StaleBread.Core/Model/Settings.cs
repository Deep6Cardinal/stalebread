namespace StaleBread.Core.Model;

public sealed record Settings
{
    public int SchemaVersion { get; init; } = 1;
    public bool SoundEnabled { get; init; } = true;
    public double Volume { get; init; } = .4;
    public bool PopupEnabled { get; init; } = true;
    public bool ReducedMotion { get; init; }
    public bool StartAtSignIn { get; init; }
    public bool ShowCompanion { get; init; } = true;
    public bool QuietHoursEnabled { get; init; }
    public TimeOnly QuietStart { get; init; } = new(22, 0);
    public TimeOnly QuietEnd { get; init; } = new(8, 0);
    public IReadOnlyList<string> ExcludedPaths { get; init; } = [];
    public IReadOnlyList<ProcessIdentity> IgnoredProcesses { get; init; } = [];
    public StaleBread.Core.Detection.RuleOptions AdvisoryRules { get; init; } = new();
}
