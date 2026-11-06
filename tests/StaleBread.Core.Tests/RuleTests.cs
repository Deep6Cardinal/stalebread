using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using StaleBread.Core.Detection;
using StaleBread.Core.Cleanup;
using static StaleBread.Core.Tests.Fixtures;

namespace StaleBread.Core.Tests;

public class RuleTests
{
    [Theory] [InlineData("chrome.exe")] [InlineData("msedge.exe")] [InlineData("firefox.exe")]
    public void AssociatedBrowserReceivesResourceHistory(string browser)
    {
        var g = NewGraph(); var engine = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 120; t += 5)
            found = engine.Evaluate(g.Apply(B(t, Root(), P(2, 1, browser) with { CpuTime = TimeSpan.FromSeconds(t * 2) })), new());
        Assert.Contains(found, f => f.Kind == FindingKind.HeavyWorkload && f.Members.Contains(new(2, 1)));
    }
    [Fact] public void IdleBrowserCannotEraseCombinedHelperWorkload()
    {
        var g = NewGraph(); var engine = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 65; t += 5)
            found = engine.Evaluate(g.Apply(B(t, Root(),
                P(2, 1) with { CpuTime = TimeSpan.FromSeconds(t * .4) },
                P(3, 1) with { CpuTime = TimeSpan.FromSeconds(t * .4) }, P(4, 2, "chrome.exe"))), new());
        Assert.Equal(3, Assert.Single(found, f => f.Kind == FindingKind.HeavyWorkload).Members.Count);
    }
    [Fact] public void BrowserLeftoverAndGrowthRulesHaveMetricEvidence()
    {
        var g = NewGraph(); var engine = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 600; t += 5)
        {
            var browser = P(2, 1, "chrome.exe") with { PrivateBytes = (2UL << 30) + (ulong)((512UL << 20) * (t / 600.0)) };
            found = engine.Evaluate(g.Apply(t == 0 ? B(t, Root(), browser) : B(t, browser)), new());
        }
        Assert.Contains(found, f => f.Kind == FindingKind.ObservedOrphan);
        Assert.Contains(found, f => f.Kind == FindingKind.Growth);
    }
    [Fact] public void UnassociatedBrowserRemainsOutsideFindingsAndFixedCountCatalog()
    {
        var g = NewGraph(); var engine = Engine(); var counts = new StaleBread.Core.Trends.CountHistory(new TestTime());
        var scope = new StaleBread.Core.Trends.CountScope(1, "1", new HashSet<ProcessIdentity>());
        IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 65; t += 5)
        {
            var batch = B(t, P(2, name: "chrome.exe") with { CpuTime = TimeSpan.FromSeconds(t * 2) }, P(3));
            counts.Append(batch, TimeSpan.FromSeconds(5), scope); found = engine.Evaluate(g.Apply(batch), new());
        }
        Assert.Empty(found); Assert.Equal(1, counts.Read(65000).CurrentCount);
    }

    [Theory] [InlineData("-noe")] [InlineData("-Interactive")]
    public void PowerShellStayOpenLaunchCannotReachCleanup(string flag)
    {
        var mode = LaunchEvidence.Parse("pwsh.exe", [flag, "-Command", "Get-Date"]);
        var s = Orphan(child: _ => P(2, 1, "pwsh.exe", mode: mode));
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, new()).Eligible);
    }

    [Fact] public void MissingAncestorsAboveObservedOwnerStillReportButCannotAuthorizeCleanup()
    {
        var g = NewGraph(); var explorer = P(10, 9, "explorer.exe"); var shell = P(11, 10, "pwsh.exe", mode: LaunchMode.Interactive);
        var root = Root() with { ParentPid = 11 }; var helper = P(2, 1);
        g.Apply(B(0, explorer, shell, root, helper)); GraphSnapshot? s = null;
        for (int t = 5; t <= 130; t += 5) s = g.Apply(B(t, explorer, shell, helper));
        var finding = Assert.Single(Engine().Evaluate(s!, new()), f => f.Kind == FindingKind.ObservedOrphan);
        Assert.False(finding.CleanupEligible);
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s!, new()).Eligible);
    }
    [Fact] public void UnknownInterpreterModeRetainsReviewOnlyLeftoverFinding()
    {
        var s = Orphan(child: _ => P(2, 1, mode: LaunchMode.Unknown));
        var finding = Assert.Single(Engine().Evaluate(s, new()), f => f.Kind == FindingKind.ObservedOrphan);
        Assert.False(finding.CleanupEligible); Assert.True(s.Associations[new(2, 1)].IsProtected);
    }
    [Theory] [InlineData("unknown.exe")] [InlineData("pwsh.exe")] [InlineData("claude.exe")]
    public void RetainedExitedIntermediateCannotHideLiveProtectedGrandchild(string name)
    {
        var g = NewGraph(); var target = P(2, 1); var bridge = P(3, 2);
        var child = P(4, 3, name, mode: LaunchMode.Interactive);
        g.Apply(B(0, Root(), target, bridge, child)); GraphSnapshot? s = null;
        for (int t = 5; t <= 130; t += 5) s = g.Apply(B(t, target, child));
        Assert.Contains(new ProcessIdentity(4, 1), CleanupPolicy.Descendants(new(2, 1), s!));
        Assert.DoesNotContain(new ProcessIdentity(3, 1), CleanupPolicy.Descendants(new(2, 1), s!));
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s!, new()).Eligible);
    }
    [Fact] public void IgnoreMatchesExactLifetimeAndBlocksCleanupButNotReusedPid()
    {
        var s = Orphan();
        var settings = new Settings { IgnoredProcesses = [new(2, 1)] };
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, settings).Eligible);
        Assert.Empty(new RuleEngine(new("user", 1), () => settings).Evaluate(s, new()));
        var reused = Orphan(child: _ => P(2, 1, created: 2));
        Assert.True(new CleanupPolicy("user", 1).Evaluate(new(2, 2), reused, settings).Eligible);
    }
    [Fact] public void KeptOwnerProtectsRetainedDescendantsAndSuppressesFindings()
    {
        var s = Orphan(extra: P(3, 2));
        var settings = new Settings { ExcludedPaths = [@"C:\Users\test\.local\bin\CLAUDE.exe"] };
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, settings).Eligible);
        Assert.Empty(new RuleEngine(new("user", 1), () => settings).Evaluate(s, new()));
    }
    [Fact] public void KeptHelperCannotReappearThroughGroupResourceRules()
    {
        var g = NewGraph(); var settings = new Settings { ExcludedPaths = [@"C:\helper.exe"] };
        var e = new RuleEngine(new("user", 1), () => settings); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 60; t += 5)
            found = e.Evaluate(g.Apply(B(t, Root(),
                P(2, 1, path: @"C:\helper.exe") with { CpuTime = TimeSpan.FromSeconds(t) },
                P(3, 1, path: @"C:\helper.exe") with { CpuTime = TimeSpan.FromSeconds(t) })), new());
        Assert.Empty(found);
    }
    [Fact] public void GroupMetricsSumEachHelperOnceAndRaiseGroupImpact()
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 60; t += 5)
            found = e.Evaluate(g.Apply(B(t, Root(),
                P(2, 1) with { CpuTime = TimeSpan.FromSeconds(t * .4) },
                P(3, 2) with { CpuTime = TimeSpan.FromSeconds(t * .4) })), new());
        var busy = Assert.Single(found, f => f.Kind == FindingKind.HeavyWorkload);
        Assert.Equal(2, busy.Members.Count);
        Assert.False(busy.CleanupEligible);
    }
    [Fact] public void ThreeQuietLeftoversHaveModerateImpactEvenWhenEachIsSmall()
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 130; t += 5)
        {
            var ps = new List<ProcessSample> { P(2, 1), P(3, 1), P(4, 1) };
            if (t == 0) ps.Add(Root());
            found = e.Evaluate(g.Apply(B(t, ps.ToArray())), new());
        }
        Assert.Equal(Impact.Moderate, Assert.Single(found, f => f.Kind == FindingKind.ObservedOrphan).Impact);
    }
    [Fact] public void EconomyCadenceResetsChurnContinuityWithoutLosingRetainedOwnership()
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 120; t += 5)
        {
            if (t == 60) g.Cadence = TimeSpan.FromSeconds(15);
            var ps = new List<ProcessSample> { Root() };
            if (t == 30 || t == 90) ps.AddRange(Enumerable.Range(t * 10, 10).Select(i => P(i, 1, created: t)));
            found = e.Evaluate(g.Apply(B(t, ps.ToArray())), new());
        }
        Assert.DoesNotContain(found, f => f.Kind == FindingKind.Churn);
    }
    [Fact] public void HealthyEconomyJitterKeepsGraphButFortyFiveSecondGapDiscardsIt()
    {
        var g = NewGraph(); g.Cadence = TimeSpan.FromSeconds(15);
        var a = g.Apply(B(0, Root(), P(2, 1)));
        var b = g.Apply(B(15.3, P(2, 1)));
        Assert.Equal(a.RunId, b.RunId);
        Assert.NotEqual(b.RunId, g.Apply(B(60.31, P(2, 1))).RunId);
    }
    [Fact] public void RepeatedDuplicateEventsAndWrongOwnerCannotManufactureChurn()
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 120; t += 5)
        {
            var one = new ProcessEvent(99, 1, "cmd.exe", true, t * 1000,
                B(t).Utc.UtcDateTime.ToFileTimeUtc(), "other-user", 1, null, null);
            found = e.Evaluate(g.Apply(B(t, Root()) with { Coverage = Coverage.Complete,
                Events = Enumerable.Repeat(one, 100).ToArray() }), new());
        }
        Assert.DoesNotContain(found, f => f.Kind == FindingKind.Churn);
    }
    private static ProcessSample Root(string? path = @"C:\Users\test\.local\bin\claude.exe") => P(1, name: "claude.exe", path: path);
    private static RuleEngine Engine() => new(new("user", 1), () => new());
    private static GraphSnapshot Orphan(double end = 130, Func<double, ProcessSample>? child = null,
        string? rootPath = @"C:\Users\test\.local\bin\claude.exe", ProcessSample? extra = null)
    {
        var g = NewGraph();
        GraphSnapshot? s = null;
        for (double t = 0; t <= end; t += 5)
        {
            var list = new List<ProcessSample> { child?.Invoke(t) ?? P(2, 1) };
            if (t == 0) list.Add(Root(rootPath));
            if (extra is not null) list.Add(extra);
            s = g.Apply(B(t, list.ToArray()));
        }
        return s!;
    }
    [Theory] [InlineData(125, false)] [InlineData(130, true)] [InlineData(135, true)]
    public void R1WaitsForFullQuietAndConfirmedOwnerExitAge(int seconds, bool expected)
    {
        var s = Orphan(seconds);
        var findings = Engine().Evaluate(s, new());
        Assert.Equal(expected, findings.Any(f => f.Kind == FindingKind.ObservedOrphan));
        Assert.Equal(expected, new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, new()).Eligible);
    }
    [Theory] [InlineData(.019, true)] [InlineData(.02, false)] [InlineData(.021, false)]
    public void QuietCoreThresholdIsStrict(double cores, bool eligible)
    {
        var s = Orphan(child: t => P(2, 1) with { CpuTime = TimeSpan.FromSeconds(t * cores) });
        Assert.Equal(eligible, new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, new()).Eligible);
    }
    [Theory] [InlineData(65535, true)] [InlineData(65536, false)] [InlineData(65537, false)]
    public void QuietIoIncludesOtherBytesAndIsStrict(int bytesPerSecond, bool eligible)
    {
        var s = Orphan(child: t => P(2, 1) with { OtherBytes = (ulong)(t * bytesPerSecond) });
        Assert.Equal(eligible, new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, new()).Eligible);
    }
    [Fact] public void CoreAndMachineCpuUseElapsedTimeAndLogicalProcessorCount()
    {
        var g = NewGraph(); g.Apply(B(0, P(2)));
        var s = g.Apply(B(5, P(2) with { CpuTime = TimeSpan.FromSeconds(6) }));
        var interval = Assert.Single(WindowTracker.Intervals(s.Current[new(2, 1)], 1000));
        Assert.Equal(1.2, interval.CoreEquivalents);
        Assert.Equal(10, interval.CoreEquivalents / s.System.LogicalProcessorCount * 100);
    }
    [Theory] [InlineData(.799, false)] [InlineData(.8, true)] [InlineData(.801, true)] [InlineData(1, true)]
    public void R3SeesOneCoreSpinnerOnThirtyTwoCoreMachine(double cores, bool expected)
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 60; t += 5)
            found = e.Evaluate(g.Apply(B(t, P(2) with { CpuTime = TimeSpan.FromSeconds(t * cores) })
                with { System = new(40, 1UL << 32, 32) }), new());
        Assert.Equal(expected, found.Any(f => f.Kind == FindingKind.HeavyWorkload));
        Assert.All(found, f => Assert.False(f.CleanupEligible));
    }
    [Theory] [InlineData(55, false)] [InlineData(60, true)] [InlineData(65, true)]
    public void R3RequiresSustainedCpuDuration(int end, bool expected)
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= end; t += 5)
            found = e.Evaluate(g.Apply(B(t, P(2) with { CpuTime = TimeSpan.FromSeconds(t) })), new());
        Assert.Equal(expected, found.Any(f => f.Kind == FindingKind.HeavyWorkload));
    }
    [Theory] [InlineData(1073741823UL, 85, false)] [InlineData(1073741824UL, 84.9, false)]
    [InlineData(1073741824UL, 85, true)] [InlineData(1073741825UL, 95, true)]
    public void R3MemoryRequiresBothSizeAndPressure(ulong memory, double load, bool expected)
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 120; t += 5)
            found = e.Evaluate(g.Apply(B(t, P(2) with { PrivateBytes = memory }) with { System = new(load, 1, 12) }), new());
        Assert.Equal(expected, found.Any(f => f.Kind == FindingKind.HeavyWorkload));
    }
    [Theory] [InlineData(536870911UL, false)] [InlineData(536870912UL, true)] [InlineData(536870913UL, true)]
    public void R4RequiresAbsoluteAndRelativeMemoryGrowth(ulong growth, bool expected)
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 600; t += 5)
            found = e.Evaluate(g.Apply(B(t, P(2) with { PrivateBytes = (2UL << 30) + (ulong)(growth * (t / 600.0)) })), new());
        Assert.Equal(expected, found.Any(f => f.Kind == FindingKind.Growth));
    }
    [Theory] [InlineData(1999, false)] [InlineData(2000, true)] [InlineData(2001, true)]
    public void R4HandleGrowthRequiresTwoThousandAndFiftyPercent(int growth, bool expected)
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 600; t += 5)
            found = e.Evaluate(g.Apply(B(t, P(2) with { HandleCount = 4000 + (int)(growth * (t / 600.0)) })), new());
        Assert.Equal(expected, found.Any(f => f.Kind == FindingKind.Growth));
    }
    [Theory] [InlineData(595, false)] [InlineData(600, true)] [InlineData(605, true)]
    public void R2MissingParentIsReviewOnlyAfterTenMinutesAndFifteenMinuteAge(int end, bool expected)
    {
        var birth = B(0).Utc.AddMinutes(-5).UtcDateTime.ToFileTimeUtc();
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= end; t += 5)
            found = e.Evaluate(g.Apply(B(t, P(2, 999, created: birth) with { KnownAiMarker = "@openai/codex" })), new());
        Assert.Equal(expected, found.Any(f => f.Kind == FindingKind.PossibleOrphan));
        Assert.All(found, f => Assert.False(f.CleanupEligible));
    }
    [Theory] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, true)] [InlineData(4, true)]
    public void R7RequiresThreeNonquietScheduledIntervalsNotOneBlip(int intervals, bool expected)
    {
        double from = 130 - intervals * 5;
        var s = Orphan(child: t => P(2, 1) with { CpuTime = TimeSpan.FromSeconds(Math.Max(0, t - from) * .1) });
        var found = Engine().Evaluate(s, new());
        Assert.Equal(expected, found.Any(f => f.Kind == FindingKind.ActiveLeftover));
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, new()).Eligible);
    }
    [Theory] [InlineData(null)] [InlineData(@"C:\Users\test\AppData\Local\AnthropicClaude\Claude.exe")]
    public void UnknownAndDesktopOwnersNeverGainCleanup(string? path)
    {
        var s = Orphan(rootPath: path);
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, new()).Eligible);
    }
    [Theory] [InlineData("visible")] [InlineData("visibilityUnknown")] [InlineData("ownershipUnknown")]
    [InlineData("interactive")] [InlineData("cpuUnknown")] [InlineData("ioUnknown")] [InlineData("excluded")]
    public void MissingOrProtectedEvidenceNeverAuthorizesCleanup(string reason)
    {
        ProcessSample Make(double _) => reason switch {
            "visible" => P(2, 1) with { HasVisibleWindow = true },
            "visibilityUnknown" => P(2, 1) with { HasVisibleWindow = null },
            "ownershipUnknown" => P(2, 1) with { OwnerSid = null },
            "interactive" => P(2, 1) with { LaunchMode = LaunchMode.Interactive },
            "cpuUnknown" => P(2, 1) with { CpuTime = null },
            "ioUnknown" => P(2, 1) with { OtherBytes = null },
            _ => P(2, 1, path: @"C:\helper.exe")
        };
        var s = Orphan(child: Make);
        var settings = new Settings { ExcludedPaths = reason == "excluded" ? [@"C:\helper.exe"] : [] };
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, settings).Eligible);
    }
    [Theory] [InlineData("conhost.exe")] [InlineData("unknown.exe")] [InlineData("claude.exe")] [InlineData("pwsh.exe")]
    public void ProtectedOrUnknownDescendantsBlockRootAction(string name)
    {
        var s = Orphan(extra: P(3, 2, name, mode: LaunchMode.Unknown));
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, new()).Eligible);
    }
    [Fact] public void IdleLiveOwnerAndSharedHelpersNeverBecomeEligible()
    {
        var g = NewGraph(); GraphSnapshot s = g.Apply(B(0, Root(), P(2, 1), P(3, 1)));
        for (int t = 5; t <= 600; t += 5) s = g.Apply(B(t, Root(), P(2, 1), P(3, 1)));
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, new()).Eligible);
        Assert.DoesNotContain(Engine().Evaluate(s, new()), f => f.Kind == FindingKind.ObservedOrphan);
    }
    [Fact] public void NestedExitedCliStillHasLiveProtectedAncestorVeto()
    {
        var g = NewGraph(); var outer = P(9, name: "Code.exe");
        g.Apply(B(0, outer, Root() with { ParentPid = 9 }, P(2, 1)));
        GraphSnapshot? s = null;
        for (int t = 5; t <= 130; t += 5) s = g.Apply(B(t, outer, P(2, 1)));
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s!, new()).Eligible);
    }
    [Theory] [InlineData(9, false)] [InlineData(10, true)] [InlineData(11, true)]
    public void R5SnapshotProxyNeedsThresholdInEachNonoverlappingWindow(int births, bool expected)
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 120; t += 5)
        {
            var list = new List<ProcessSample> { Root() };
            if (t == 30 || t == 90) list.AddRange(Enumerable.Range(t * 10, births).Select(i => P(i, 1, "conhost.exe", t)));
            found = e.Evaluate(g.Apply(B(t, list.ToArray())), new());
        }
        Assert.Equal(expected, found.Any(f => f.Kind == FindingKind.Churn && f.IsLowerBound));
        Assert.All(found, f => Assert.False(f.CleanupEligible));
    }
    [Theory] [InlineData(29, false)] [InlineData(30, true)] [InlineData(31, true)]
    public void R5EventRateRemainsSeparateFromSnapshotProxy(int births, bool expected)
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 120; t += 5)
        {
            var events = t is 30 or 90 ? Enumerable.Range(t * 100, births).Select(i =>
                new ProcessEvent(i, 1, "cmd.exe", true, t * 1000, B(t).Utc.UtcDateTime.ToFileTimeUtc(),
                    "user", 1, null, null)).ToArray() : [];
            found = e.Evaluate(g.Apply(B(t, Root()) with { Events = events, Coverage = Coverage.Complete }), new());
        }
        Assert.Equal(expected, found.Any(f => f.Kind == FindingKind.Churn && !f.IsLowerBound));
    }
    [Theory] [InlineData(9, false)] [InlineData(10, true)] [InlineData(11, true)]
    public void R6NeedsTwentyHelpersAndTenAddedSurvivors(int added, bool expected)
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 720; t += 5)
        {
            var list = new List<ProcessSample> { Root() };
            list.AddRange(Enumerable.Range(2, t >= 600 ? 10 + added : 10).Select(i => P(i, 1)));
            found = e.Evaluate(g.Apply(B(t, list.ToArray())), new());
        }
        Assert.Equal(expected, found.Any(f => f.Kind == FindingKind.Accumulation));
    }
    [Fact] public void OnDemandActivityVetoesQuietButDoesNotMakeThreeR7Intervals()
    {
        var g = NewGraph(); GraphSnapshot s = g.Apply(B(0, Root(), P(2, 1)));
        for (int t = 5; t <= 130; t += 5) s = g.Apply(B(t, P(2, 1)));
        for (int i = 1; i <= 3; i++)
            s = g.Apply(B(130 + i * .1, P(2, 1) with { CpuTime = TimeSpan.FromSeconds(i * .1) })
                with { SampleKind = SampleKind.OnDemand });
        var found = Engine().Evaluate(s, new());
        Assert.DoesNotContain(found, f => f.Kind == FindingKind.ActiveLeftover);
        Assert.False(new CleanupPolicy("user", 1).Evaluate(new(2, 1), s, new()).Eligible);
    }
    [Fact] public void ThreeSampleClearingCannotBeAcceleratedByOnDemandScans()
    {
        var g = NewGraph(); var e = Engine(); IReadOnlyList<Finding> found = [];
        for (int t = 0; t <= 60; t += 5)
            found = e.Evaluate(g.Apply(B(t, P(2) with { CpuTime = TimeSpan.FromSeconds(t) })), new());
        Assert.Contains(found, f => f.State == FindingState.Active);
        for (int i = 1; i <= 3; i++)
            found = e.Evaluate(g.Apply(B(60 + i * .1, P(2) with { CpuTime = TimeSpan.FromSeconds(60) })
                with { SampleKind = SampleKind.OnDemand }), new());
        Assert.Contains(found, f => f.State == FindingState.Active);
        for (int t = 65; t <= 75; t += 5)
            found = e.Evaluate(g.Apply(B(t, P(2) with { CpuTime = TimeSpan.FromSeconds(60) })), new());
        Assert.DoesNotContain(found, f => f.State == FindingState.Active);
    }
}
