using StaleBread.Core.Detection;
using StaleBread.Core.Model;
using static StaleBread.Core.Tests.Fixtures;

namespace StaleBread.Core.Tests;

public sealed class DiagnosticStabilityTests
{
    private static ProcessSample Root() => P(1, name: "claude.exe", path: @"C:\Users\test\.local\bin\claude.exe");
    private static ProcessSample[] Helpers(int count) => [Root(), .. Enumerable.Range(2, count).Select(i => P(i, 1))];

    [Fact]
    public void BriefAccumulationSpikeNeverBecomesAnAlert()
    {
        var graph = NewGraph(); var engine = new RuleEngine(new("user", 1), () => new());
        for (int t = 0; t <= 750; t += 5)
        {
            var found = engine.Evaluate(graph.Apply(B(t, Helpers(t == 600 ? 40 : 10))), new());
            Assert.DoesNotContain(found, f => f.Kind == FindingKind.Accumulation);
        }
    }

    [Theory] [InlineData(5)] [InlineData(15)]
    public void SustainedAccumulationWaitsTwoMinutesAndClearsAfterTwoQuietMinutes(int cadence)
    {
        var graph = NewGraph(); graph.Cadence = TimeSpan.FromSeconds(cadence);
        var engine = new RuleEngine(new("user", 1), () => new());
        DateTimeOffset? first = null;
        for (int t = 0; t <= 900; t += cadence)
        {
            var found = engine.Evaluate(graph.Apply(B(t, Helpers(t >= 600 && t <= 750 ? 30 : 10))), new());
            var accumulation = found.SingleOrDefault(f => f.Kind == FindingKind.Accumulation);
            if (t < 720) Assert.Null(accumulation);
            else if (t <= 870)
            {
                Assert.NotNull(accumulation); Assert.Equal(FindingState.Active, accumulation.State);
                first ??= accumulation.FirstSeenUtc; Assert.Equal(first.Value, accumulation.FirstSeenUtc);
                Assert.False(accumulation.CleanupEligible);
            }
            if (t == 870 + cadence) Assert.Equal(FindingState.Resolved, accumulation!.State);
        }
    }

    [Fact]
    public void ChurnShortDipAndReboundRemainOneEpisode()
    {
        var graph = NewGraph(); var engine = new RuleEngine(new("user", 1), () => new());
        DateTimeOffset? first = null;
        for (int t = 0; t <= 270; t += 5)
        {
            var ps = new List<ProcessSample> { Root() };
            if (t is 30 or 90 or 210 or 270) ps.AddRange(Enumerable.Range(100 + t * 10, 10).Select(i => P(i, 1, created: t)));
            var found = engine.Evaluate(graph.Apply(B(t, ps.ToArray())), new());
            if (t < 120) continue;
            var churn = Assert.Single(found, f => f.Kind == FindingKind.Churn);
            Assert.Equal(FindingState.Active, churn.State);
            first ??= churn.FirstSeenUtc; Assert.Equal(first.Value, churn.FirstSeenUtc);
        }
    }

    [Fact]
    public void OnDemandScanCannotCompleteAccumulationPersistence()
    {
        var graph = NewGraph(); var engine = new RuleEngine(new("user", 1), () => new());
        for (int t = 0; t <= 715; t += 5) engine.Evaluate(graph.Apply(B(t, Helpers(t >= 600 ? 30 : 10))), new());
        var demand = engine.Evaluate(graph.Apply(B(720, Helpers(30)) with { SampleKind = SampleKind.OnDemand }), new());
        Assert.DoesNotContain(demand, f => f.Kind == FindingKind.Accumulation);
        Assert.Contains(engine.Evaluate(graph.Apply(B(721, Helpers(30))), new()), f => f.Kind == FindingKind.Accumulation);
    }

    [Theory] [InlineData("degraded")] [InlineData("gap")] [InlineData("cadence")]
    public void BrokenObservationContinuityCannotCompleteOldPersistence(string interruption)
    {
        var graph = NewGraph(); var engine = new RuleEngine(new("user", 1), () => new());
        for (int t = 0; t <= 660; t += 5) engine.Evaluate(graph.Apply(B(t, Helpers(t >= 600 ? 30 : 10))), new());
        int begin = interruption == "gap" ? 700 : 665;
        if (interruption == "cadence") graph.Cadence = TimeSpan.FromSeconds(15);
        for (int t = begin; t <= 800; t += 5)
        {
            var batch = B(t, Helpers(30));
            if (interruption == "degraded" && t == begin) batch = batch with { Coverage = Coverage.Degraded };
            Assert.DoesNotContain(engine.Evaluate(graph.Apply(batch), new()), f => f.Kind == FindingKind.Accumulation);
        }
    }

    [Fact]
    public void SettlingAndResolvedKeepMeasurementTimestampAndNeverAuthorizeCleanup()
    {
        var graph = NewGraph(); var engine = new RuleEngine(new("user", 1), () => new());
        Finding? trigger = null;
        for (int t = 0; t <= 145; t += 5)
        {
            var ps = new List<ProcessSample> { Root() };
            if (t is 30 or 90) ps.AddRange(Enumerable.Range(t * 100, 10).Select(i => P(i, 1, created: t)));
            trigger = engine.Evaluate(graph.Apply(B(t, ps.ToArray())), new()).SingleOrDefault(f => f.Kind == FindingKind.Churn) ?? trigger;
        }
        Assert.NotNull(trigger); Assert.Empty(trigger.Members);
        Assert.Equal(10, Assert.Single(trigger.Measurements).PreviousWindowStarts);
        Assert.Equal(10, trigger.Measurements[0].RecentWindowStarts);
        for (int t = 150; t <= 275; t += 5)
        {
            var finding = Assert.Single(engine.Evaluate(graph.Apply(B(t, Root())), new()), f => f.Kind == FindingKind.Churn);
            if (t == 150) { trigger = finding; continue; }
            Assert.Equal(trigger.MeasurementsAtUtc, finding.MeasurementsAtUtc);
            Assert.False(finding.CleanupEligible);
            Assert.Equal(t < 275, finding.IsClearing);
            Assert.Equal(t < 275 ? FindingState.Active : FindingState.Resolved, finding.State);
        }
    }

    [Fact]
    public void OnDemandAndDegradedScansCannotAdvanceChurnClearTimer()
    {
        var graph = NewGraph(); var engine = new RuleEngine(new("user", 1), () => new());
        for (int t = 0; t <= 270; t += 5)
        {
            var ps = new List<ProcessSample> { Root() };
            if (t is 30 or 90) ps.AddRange(Enumerable.Range(t * 100, 10).Select(i => P(i, 1, created: t)));
            engine.Evaluate(graph.Apply(B(t, ps.ToArray())), new());
        }
        var demand = Assert.Single(engine.Evaluate(graph.Apply(B(275, Root()) with { SampleKind = SampleKind.OnDemand }), new()));
        Assert.Equal(FindingState.Active, demand.State);
        engine.Evaluate(graph.Apply(B(280, Root()) with { Coverage = Coverage.Degraded }), new());
        for (int t = 285; t <= 400; t += 5)
            Assert.Equal(FindingState.Active, Assert.Single(engine.Evaluate(graph.Apply(B(t, Root())), new())).State);
        Assert.Equal(FindingState.Resolved, Assert.Single(engine.Evaluate(graph.Apply(B(405, Root())), new())).State);
    }
}
