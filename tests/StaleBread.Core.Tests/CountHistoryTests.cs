using StaleBread.Core.Model;
using StaleBread.Core.Trends;
using static StaleBread.Core.Tests.Fixtures;

namespace StaleBread.Core.Tests;

public class CountHistoryTests
{
    private static readonly CountScope Scope = new(1, "1", new HashSet<ProcessIdentity>());
    private static readonly TimeSpan Cadence = TimeSpan.FromSeconds(5);
    private static void Add(CountHistory h, double t, params ProcessSample[] p) => h.Append(B(t, p), Cadence, Scope);
    private static CountDelta Minute(CountHistory h, double t) => h.Read((long)(t * 1000)).Deltas[0];

    [Fact] public void SameTotalPreservesTwentyAppearedAndTwentyDisappeared()
    {
        var h = new CountHistory(new TestTime());
        for (int i = 0; i <= 12; i++)
            Add(h, i * 5, Enumerable.Range(i == 12 ? 21 : 1, 20).Select(n => P(n)).ToArray());
        var d = Minute(h, 60);
        Assert.Equal(20, d.Additions); Assert.Equal(20, d.Removals); Assert.Equal(0, d.Net);
        Assert.Equal("+20 / −20", d.Pair);
    }
    [Fact] public void ThirtyNineToFortyTwoCanContainEighteenAdditionsAndFifteenRemovals()
    {
        var h = new CountHistory(new TestTime());
        for (int i = 0; i <= 12; i++)
            Add(h, i * 5, Enumerable.Range(i == 12 ? 16 : 1, i == 12 ? 42 : 39).Select(n => P(n)).ToArray());
        var d = Minute(h, 60);
        Assert.Equal(18, d.Additions); Assert.Equal(15, d.Removals); Assert.Equal(3, d.Net);
    }
    [Theory] [InlineData(60)] [InlineData(300)] [InlineData(900)] [InlineData(1800)] [InlineData(3600)]
    public void EveryHorizonHasIndependentBaselineAndGrossTotals(int horizon)
    {
        var h = new CountHistory(new TestTime());
        for (int i = 0; i <= horizon; i += 5) Add(h, i, P(i / 5 + 1));
        var d = Assert.Single(h.Read(horizon * 1000).Deltas, x => x.Horizon.TotalSeconds == horizon);
        Assert.Equal(horizon / 5, d.Additions); Assert.Equal(horizon / 5, d.Removals);
        Assert.Equal(0, d.Net); Assert.Equal(TimeSpan.FromSeconds(horizon), d.ActualInterval);
    }
    [Fact] public void ScopeIsCaseInsensitiveDeduplicatedSessionOnlyAndIndependentOfMetadata()
    {
        var h = new CountHistory(new TestTime());
        var scope = Scope with { SelfOwned = new HashSet<ProcessIdentity> { new(1, 1) } };
        h.Append(B(0, P(1), P(2, name: "CLAUDE.EXE"), P(2, name: "CLAUDE.EXE"),
            P(3) with { OwnerSid = null }, P(4) with { SessionId = 2 }, P(5, name: "unrelated.exe")), Cadence, scope);
        Assert.Equal(2, h.Read(0).CurrentCount);
        Assert.False(h.Read(0).Partial);
    }
    [Theory] [InlineData("metadata")] [InlineData("ancestry")] [InlineData("graph-cap")]
    public void DiagnosticDegradationDoesNotBreakNativeCounts(string gap)
    {
        var h = new CountHistory(new TestTime());
        for (int i = 0; i <= 60; i += 5)
            h.Append(B(i, P(i + 1)) with { Coverage = Coverage.Degraded, Gaps = [gap] }, Cadence, Scope);
        Assert.Equal(12, Minute(h, 60).Additions);
        Assert.False(h.Read(60000).Partial);
    }
    [Theory] [InlineData("identity")] [InlineData("session")] [InlineData("enumeration")] [InlineData("cap")]
    public void NativeCountFailureClearsComparisonsAndPreviousSet(string failure)
    {
        var h = new CountHistory(new TestTime());
        Add(h, 0, P(1));
        var b = B(5, P(2));
        b = failure switch {
            "identity" => b with { Processes = [P(2) with { Identity = null }] },
            "session" => b with { Processes = [P(2) with { SessionId = null }] },
            "cap" => b with { Processes = Enumerable.Range(1, 20001).Select(n => P(n)).ToArray() },
            _ => b with { NativeCountCoverage = NativeCountCoverage.Partial }
        };
        h.Append(b, Cadence, Scope);
        Assert.True(h.Read(5000).Partial);
        Assert.All(h.Read(5000).Deltas, d => Assert.Null(d.Net));
        Add(h, 10, P(3));
        Assert.Equal(1, h.RecordCount);
        Assert.False(h.Read(10000).Partial);
    }
    [Fact] public void ReadTimeDoesNotMoveBaselineAndOldValuesBecomeStale()
    {
        var h = new CountHistory(new TestTime());
        for (int i = 0; i <= 60; i += 5) Add(h, i, P(i + 1));
        Assert.Equal(Minute(h, 60), Minute(h, 64));
        Assert.True(h.Read(76000).Stale);
        Assert.Null(Minute(h, 76).Additions);
    }
    [Fact] public void HealthyEconomyTransitionUsesLaterCadenceAndKeepsHistory()
    {
        var h = new CountHistory(new TestTime());
        for (int i = 0; i <= 60; i += 5) Add(h, i, P(1));
        h.Append(B(75.3, P(2)), TimeSpan.FromSeconds(15), Scope);
        var d = Minute(h, 75.3);
        Assert.Equal(1, d.Additions); Assert.Equal(1, d.Removals);
        Assert.Equal(TimeSpan.FromSeconds(60.3), d.ActualInterval);
        h.Append(B(120.31, P(3)), TimeSpan.FromSeconds(15), Scope);
        Assert.Null(Minute(h, 120.31).Additions);
        Assert.Equal(1, h.RecordCount);
    }
    [Theory] [InlineData("Paused")] [InlineData("Sleep")] [InlineData("Restart")]
    public void ResetExplainsMissingHistoryAndClearsPreviousIdentitySet(string reason)
    {
        var h = new CountHistory(new TestTime());
        for (int i = 0; i <= 60; i += 5) Add(h, i, P(1));
        h.Reset(reason);
        Assert.True(h.Read(60000).Stale);
        Add(h, 65, P(2));
        Assert.Equal(reason, h.Read(65000).HistoryReason);
        Assert.Null(Minute(h, 65).Additions);
        Assert.Equal(1, h.RecordCount);
        for (int i = 70; i <= 125; i += 5) Add(h, i, P(2));
        Assert.Equal(0, Minute(h, 125).Additions);
    }
    [Fact] public void ScopeChangesResetButEquivalentScopeObjectsDoNot()
    {
        var h = new CountHistory(new TestTime());
        for (int i = 0; i <= 60; i += 5) h.Append(B(i, P(1)), Cadence, new(1, "1", new HashSet<ProcessIdentity>()));
        Assert.Equal(0, Minute(h, 60).Net);
        h.Append(B(65, P(1)), Cadence, Scope with { Version = "2" });
        Assert.Equal(1, h.RecordCount);
        Assert.Null(Minute(h, 65).Net);
    }
    [Fact] public void OnDemandNeverChangesCountHistoryOrCurrentCount()
    {
        var h = new CountHistory(new TestTime());
        Add(h, 0, P(1));
        h.Append(B(1, P(2), P(3)) with { SampleKind = SampleKind.OnDemand }, Cadence, Scope);
        Assert.Equal(1, h.Read(1000).CurrentCount);
        Assert.Equal(1, h.RecordCount);
    }
    [Fact] public void BaselineOlderThanItsThreeCadenceToleranceIsUnavailable()
    {
        var h = new CountHistory(new TestTime());
        Add(h, 0, P(1));
        // Legal economy gaps can leave no sufficiently close normal-cadence baseline.
        h.Append(B(40, P(1)), TimeSpan.FromSeconds(15), Scope);
        h.Append(B(80, P(1)), TimeSpan.FromSeconds(15), Scope);
        Assert.Null(Minute(h, 80).Net);
    }
    [Fact] public void HistoryIsBoundedAndCannotInventPrunedBaselines()
    {
        var h = new CountHistory(new TestTime());
        for (int i = 0; i < 5000; i++) Add(h, i, P(1));
        Assert.True(h.RecordCount <= 800);
        Assert.Null(h.Read(4999000).Deltas[2].Net);
        var regular = new CountHistory(new TestTime());
        for (int i = 0; i < 7200; i += 5) Add(regular, i, P(1));
        Assert.InRange(regular.RecordCount, 720, 733);
        Assert.Equal(0, regular.Read(7195000).Deltas[4].Net);
    }
    [Fact] public void EmptyAndInsufficientHistoryAreNotFabricatedZeroActivity()
    {
        var h = new CountHistory(new TestTime());
        Assert.Null(h.Read(0).CurrentCount);
        Add(h, 0, P(1));
        Assert.Equal("— / —", Minute(h, 0).Pair);
        for (int i = 5; i <= 60; i += 5) Add(h, i, P(1));
        Assert.Equal("+0 / −0", Minute(h, 60).Pair);
    }
}
