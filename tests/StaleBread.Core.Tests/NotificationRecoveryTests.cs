using StaleBread.Core.Detection;
using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using StaleBread.Core.Notifications;
using static StaleBread.Core.Tests.Fixtures;
namespace StaleBread.Core.Tests;

public class NotificationRecoveryTests
{
    [Fact] public void ResolutionSignalsThatClearedDismissalMustBePersisted()
    {
        var time = new TestTime(); var policy = new NotificationPolicy();
        policy.Snooze("test", time); policy.Dismiss("test"); long savedRevision = policy.Revision;
        var resolved = new Finding("test", FindingKind.Churn, EvidenceConfidence.Medium, [], [], false,
            time.GetUtcNow(), time.GetUtcNow(), Impact.Moderate, "1", null, [], [], null, Coverage.SnapshotOnly, true, FindingState.Resolved);
        policy.Evaluate([resolved], new(time, TimeZoneInfo.Utc, new(), new()));
        Assert.True(policy.Revision > savedRevision);
        var restored = new NotificationPolicy(); restored.Restore(policy.Export(), time);
        Assert.False(restored.Export().Episodes["test"].Dismissed);
    }
    [Theory] [InlineData(true, false)] [InlineData(false, false)] [InlineData(true, true)]
    public void HealthyAbsenceAfterResetAllowsNewEpisodeWithFreshCoalescing(bool dismiss, bool restart)
    {
        var time = new TestTime(); var graph = NewGraph(); var settings = new Settings();
        var engine = new RuleEngine(new("user", 1), () => settings); var policy = new NotificationPolicy();
        NotificationDecision Step(int seconds, double cpu)
        {
            time.Seconds = seconds;
            var snapshot = graph.Apply(B(seconds, P(7) with { CpuTime = TimeSpan.FromSeconds(cpu) }));
            return policy.Evaluate(engine.Evaluate(snapshot, settings.AdvisoryRules),
                new(time, TimeZoneInfo.Utc, new(), settings, snapshot, TimeSpan.FromSeconds(5)));
        }
        NotificationDecision? decision = null;
        for (int t = 0; t <= 90; t += 5) decision = Step(t, t);
        string key = Assert.Single(decision!.EpisodeIds); Assert.True(decision.ShowPopup);
        if (dismiss) policy.Dismiss(key);
        if (restart) { var memory = policy.Export(); policy = new(); policy.Restore(memory, time); }
        long savedRevision = policy.Revision;
        graph.Reset("History restarted after resume");
        for (int t = 95; t <= 1900; t += 5) Assert.False(Step(t, 90).ShowPopup);
        if (dismiss) { Assert.False(policy.Export().Episodes[key].Dismissed); Assert.True(policy.Revision > savedRevision); }
        // A genuinely new CPU episode starts at 1960, then must coalesce for 30s.
        for (int t = 1905; t < 1990; t += 5) Assert.False(Step(t, 90 + t - 1900).ShowPopup);
        Assert.True(Step(1990, 180).ShowPopup);
    }

    [Fact] public void ContinuousLongWindowEpisodeKeepsDismissalDuringRewarm()
    {
        var time = new TestTime(); var graph = NewGraph();
        var settings = new Settings { AdvisoryRules = new() { BusyDuration = TimeSpan.FromSeconds(600) } };
        var engine = new RuleEngine(new("user", 1), () => settings); var policy = new NotificationPolicy();
        NotificationDecision Step(int seconds)
        {
            time.Seconds = seconds;
            var snapshot = graph.Apply(B(seconds, P(7) with { CpuTime = TimeSpan.FromSeconds(seconds) }));
            return policy.Evaluate(engine.Evaluate(snapshot, settings.AdvisoryRules), new(time, TimeZoneInfo.Utc, new(), settings, snapshot));
        }
        NotificationDecision? decision = null;
        for (int t = 0; t <= 630; t += 5) decision = Step(t);
        policy.Dismiss(Assert.Single(decision!.EpisodeIds)); graph.Reset("Resume");
        for (int t = 635; t <= 3000; t += 5) Assert.False(Step(t).ShowPopup);
    }

    [Theory] [InlineData("duplicate")] [InlineData("gap")] [InlineData("degraded")] [InlineData("on-demand")]
    public void UnhealthyOrRepeatedObservationsCannotResolveAcknowledgment(string interruption)
    {
        var time = new TestTime(); var graph = NewGraph(); var policy = new NotificationPolicy(); var settings = new Settings();
        policy.Snooze("test", time); policy.Dismiss("test");
        GraphSnapshot? snapshot = null;
        for (int t = 0; t <= 610; t += 5)
        {
            time.Seconds = t;
            if (interruption == "gap" && t is > 295 and < 400) continue;
            if (snapshot is null || interruption != "duplicate") snapshot = graph.Apply(B(t));
            if (interruption == "degraded" && t == 300) snapshot = snapshot with { Coverage = Coverage.Degraded };
            if (interruption == "on-demand") snapshot = snapshot with { SampleKind = SampleKind.OnDemand };
            policy.Evaluate([], new(time, TimeZoneInfo.Utc, new(), settings, snapshot));
        }
        Assert.True(policy.Export().Episodes["test"].Dismissed);
    }
}
