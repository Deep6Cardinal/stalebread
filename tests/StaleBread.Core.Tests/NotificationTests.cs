using StaleBread.Core.Model;
using StaleBread.Core.Notifications;

namespace StaleBread.Core.Tests;

public class NotificationTests
{
    [Fact]
    public void SettlingAlertDoesNotNotifyOrClearDismissal()
    {
        var p = new NotificationPolicy(); var t = new TestTime();
        p.Evaluate([F()], C(t)); p.Dismiss("one");
        t.Seconds = 60;
        var settling = p.Evaluate([F() with { IsClearing = true }], C(t));
        Assert.False(settling.ShowPopup); Assert.False(settling.PlaySound);
        Assert.True(p.Export().Episodes["one"].Dismissed);
        t.Seconds = 90; Assert.False(p.Evaluate([F()], C(t)).ShowPopup);
    }

    [Fact]
    public void UndismissedSettlingAndZeroMemberChurnStaySilent()
    {
        foreach (var finding in new[] { F() with { IsClearing = true }, F(count: 0) })
        {
            var p = new NotificationPolicy(); var t = new TestTime();
            p.Evaluate([finding], C(t)); t.Seconds = 60;
            var result = p.Evaluate([finding], C(t));
            Assert.False(result.ShowPopup); Assert.False(result.PlaySound); Assert.False(result.ShowBadge);
        }
    }

    [Theory] [InlineData("snooze")] [InlineData("dismiss")] [InlineData("cooldown")]
    public void RealRuleEpisodeAcknowledgmentSurvivesGraphRecreation(string action)
    {
        Finding Busy()
        {
            var g = Fixtures.NewGraph(); var engine = new StaleBread.Core.Detection.RuleEngine(new("user", 1), () => new());
            IReadOnlyList<Finding> found = [];
            for (int i = 0; i <= 60; i += 5)
                found = engine.Evaluate(g.Apply(Fixtures.B(i, Fixtures.P(7) with { CpuTime = TimeSpan.FromSeconds(i) })), new());
            return Assert.Single(found);
        }
        var first = Busy(); var policy = new NotificationPolicy(); var t = new TestTime();
        policy.Evaluate([first], C(t)); t.Seconds = 30; Assert.True(policy.Evaluate([first], C(t)).ShowPopup);
        if (action == "snooze") policy.Snooze(first.EpisodeKey, t);
        if (action == "dismiss") policy.Dismiss(first.EpisodeKey);
        var restored = new NotificationPolicy(); restored.Restore(policy.Export(), t);
        var next = Busy(); restored.Evaluate([next], C(t)); t.Seconds = 330;
        Assert.False(restored.Evaluate([next], C(t)).ShowPopup);
    }
    [Fact] public void BackwardUtcCannotBypassInRunMonotonicCooldown()
    {
        var p = new NotificationPolicy(); var t = new TestTime();
        p.Evaluate([F()], C(t)); t.Seconds = 30; p.Evaluate([F()], C(t));
        t.UtcOffset = TimeSpan.FromDays(-2); t.Seconds = 100;
        Assert.False(p.Evaluate([F()], C(t)).ShowPopup);
    }
    [Theory] [InlineData("2026-11-01T05:30:00Z")] [InlineData("2026-11-01T06:30:00Z")]
    public void BothRepeatedDstHoursHonorLocalQuietWindow(string utc)
    {
        var p = new NotificationPolicy(); var t = new TestTime {
            Seconds = (DateTimeOffset.Parse(utc) - DateTimeOffset.Parse("2026-01-01T00:00:00Z")).TotalSeconds };
        var c = C(t, settings: new Settings { QuietHoursEnabled = true, QuietStart = new(1, 0), QuietEnd = new(2, 0) })
            with { TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time") };
        p.Evaluate([F()], c); t.Seconds += 30;
        Assert.False(p.Evaluate([F()], c).ShowPopup);
    }
    private static Finding F(string key = "one", FindingKind kind = FindingKind.Churn, int count = 3, ulong bytes = 0) =>
        new(key, kind, EvidenceConfidence.Medium, Enumerable.Range(1, count).Select(i => new ProcessIdentity(i, 1)).ToArray(),
            [], false, DateTimeOffset.MinValue, DateTimeOffset.MinValue, Impact.Moderate, "1", null, [], [], bytes,
            Coverage.SnapshotOnly, true, FindingState.Active);
    private static NotificationContext C(TestTime time, DesktopState? desktop = null, Settings? settings = null) =>
        new(time, TimeZoneInfo.Utc, desktop ?? new(), settings ?? new());
    [Fact] public void CoalescesThirtySecondsAndPlaysOnce()
    {
        var p = new NotificationPolicy(); var t = new TestTime();
        Assert.False(p.Evaluate([F()], C(t)).ShowPopup);
        t.Seconds = 29; Assert.False(p.Evaluate([F()], C(t)).PlaySound);
        t.Seconds = 30; var d = p.Evaluate([F()], C(t));
        Assert.True(d.ShowPopup); Assert.True(d.PlaySound);
        Assert.False(p.Evaluate([F()], C(t)).PlaySound);
    }
    [Fact] public void GlobalAndEpisodeCooldownsAreIndependent()
    {
        var p = new NotificationPolicy(); var t = new TestTime();
        p.Evaluate([F()], C(t)); t.Seconds = 30; p.Evaluate([F()], C(t));
        t.Seconds = 31; p.Evaluate([F(), F("two")], C(t));
        t.Seconds = 329; Assert.False(p.Evaluate([F(), F("two")], C(t)).ShowPopup);
        t.Seconds = 330; Assert.Equal(["two"], p.Evaluate([F(), F("two")], C(t)).EpisodeIds);
        t.Seconds = 1829; Assert.False(p.Evaluate([F()], C(t)).ShowPopup);
        t.Seconds = 1830; Assert.True(p.Evaluate([F()], C(t)).ShowPopup);
    }
    [Fact] public void SnoozeDismissAndLeftoverKindSwitchDoNotSpam()
    {
        var p = new NotificationPolicy(); var t = new TestTime();
        p.Evaluate([F(kind: FindingKind.ObservedOrphan)], C(t));
        p.Snooze("one", t); t.Seconds = 1799;
        Assert.False(p.Evaluate([F(kind: FindingKind.ActiveLeftover)], C(t)).ShowPopup);
        t.Seconds = 1800; Assert.True(p.Evaluate([F(kind: FindingKind.ActiveLeftover)], C(t)).ShowPopup);
        p.Dismiss("one"); t.Seconds = 4000;
        Assert.False(p.Evaluate([F(kind: FindingKind.ObservedOrphan)], C(t)).ShowPopup);
        p.Evaluate([F() with { State = FindingState.Resolved }], C(t));
        t.Seconds = 4001; p.Evaluate([F()], C(t));
        t.Seconds = 4031; Assert.True(p.Evaluate([F()], C(t)).ShowPopup);
    }
    [Theory] [InlineData(FindingKind.ObservedOrphan)] [InlineData(FindingKind.PossibleOrphan)] [InlineData(FindingKind.ActiveLeftover)]
    public void SmallLeftoversStaySilentUntilCountOrMemoryThreshold(FindingKind kind)
    {
        var p = new NotificationPolicy(); var t = new TestTime();
        p.Evaluate([F(kind: kind, count: 2, bytes: 134217727)], C(t));
        t.Seconds = 60; Assert.False(p.Evaluate([F(kind: kind, count: 2, bytes: 134217727)], C(t)).ShowPopup);
        p.Evaluate([F(kind: kind, count: 2, bytes: 134217728)], C(t));
        t.Seconds = 90; Assert.True(p.Evaluate([F(kind: kind, count: 2, bytes: 134217728)], C(t)).ShowPopup);
    }
    [Theory] [InlineData("locked")] [InlineData("disconnected")] [InlineData("fullscreen")] [InlineData("unknown")]
    public void SuppressedNotificationsReturnAsOneSilentDigest(string reason)
    {
        var p = new NotificationPolicy(); var t = new TestTime();
        var state = reason switch { "locked" => new DesktopState(Locked: true), "disconnected" => new(Disconnected: true),
            "fullscreen" => new(Notifications: NotificationState.Suppressed), _ => new(Notifications: NotificationState.Unknown) };
        p.Evaluate([F()], C(t, state)); t.Seconds = 60;
        var suppressed = p.Evaluate([F()], C(t, state));
        Assert.False(suppressed.ShowPopup); Assert.False(suppressed.PlaySound); Assert.True(suppressed.ShowBadge);
        var digest = p.Evaluate([F()], C(t));
        Assert.True(digest.ShowSilentDigest); Assert.False(digest.PlaySound);
        Assert.False(p.Evaluate([F()], C(t)).ShowSilentDigest);
    }
    [Theory] [InlineData(23, true)] [InlineData(7, true)] [InlineData(12, false)]
    public void QuietHoursCrossMidnight(int hour, bool quiet)
    {
        var p = new NotificationPolicy(); var t = new TestTime { Seconds = hour * 3600 };
        var settings = new Settings { QuietHoursEnabled = true };
        p.Evaluate([F()], C(t, settings: settings)); t.Seconds += 30;
        Assert.Equal(!quiet, p.Evaluate([F()], C(t, settings: settings)).ShowPopup);
    }
    [Fact] public void MuteAndPopupSettingsPreventIntrusiveDelivery()
    {
        var p = new NotificationPolicy(); var t = new TestTime();
        var settings = new Settings { SoundEnabled = false };
        p.Evaluate([F()], C(t, settings: settings)); t.Seconds = 30;
        Assert.False(p.Evaluate([F()], C(t, settings: settings)).PlaySound);
        t.Seconds = 3000;
        Assert.False(p.Evaluate([F()], C(t, settings: settings with { PopupEnabled = false })).ShowPopup);
    }
    [Fact] public void RestoredCooldownCannotBeBypassedByBackwardUtc()
    {
        var p = new NotificationPolicy(); var t = new TestTime { Seconds = 1000 };
        p.Evaluate([F()], C(t)); t.Seconds = 1030; p.Evaluate([F()], C(t));
        var persisted = p.Export();
        var q = new NotificationPolicy(); var clock = new TestTime { Seconds = 0 };
        q.Restore(persisted, clock);
        q.Evaluate([F()], C(clock)); clock.Seconds = 30;
        Assert.False(q.Evaluate([F()], C(clock)).ShowPopup);
    }
}
