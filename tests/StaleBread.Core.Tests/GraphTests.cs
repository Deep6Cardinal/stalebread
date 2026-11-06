using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using static StaleBread.Core.Tests.Fixtures;

namespace StaleBread.Core.Tests;

public class GraphTests
{
    [Fact] public void CounterResetBreaksTheMetricWindow()
    {
        var g = NewGraph();
        g.Apply(B(0, P(1) with { CpuTime = TimeSpan.FromSeconds(5), ReadBytes = 100 }));
        var s = g.Apply(B(5, P(1)));
        Assert.Single(s.Current[new(1, 1)].Metrics);
    }
    [Fact] public void ReusedParentPidCannotInventAncestry()
    {
        var g = NewGraph();
        var s = g.Apply(B(0, P(1, created: 20), P(2, 1, created: 10)));
        Assert.False(s.Current[new(2, 10)].ParentKnown);
    }
    [Fact] public void ReusedPidIsSeparateIdentityAndNeedsTwoMissingSnapshots()
    {
        var g = NewGraph();
        g.Apply(B(0, P(1)));
        var first = g.Apply(B(5, P(1, created: 2)));
        Assert.Null(first.Retained[new(1, 1)].ExitedAt);
        var second = g.Apply(B(10, P(1, created: 2)));
        Assert.Equal(10000, second.Retained[new(1, 1)].ExitedAt);
        Assert.Single(second.Current);
    }
    [Fact] public void DegradedSnapshotBreaksConsecutiveAbsenceEvidence()
    {
        var g = NewGraph();
        g.Apply(B(0, P(1)));
        g.Apply(B(5));
        g.Apply(B(10) with { Coverage = Coverage.Degraded });
        Assert.Null(g.Apply(B(15)).Retained.GetValueOrDefault(new(1, 1))?.ExitedAt);
    }
    [Fact] public void UnknownStartupParentDoesNotBecomeAnObservedExit()
    {
        var g = NewGraph();
        for (int i = 0; i < 10; i++) g.Apply(B(i * 5, P(2, 999)));
        var s = g.Apply(B(50, P(2, 999)));
        Assert.False(s.Current[new(2, 1)].ParentKnown);
        Assert.Empty(s.Retained);
    }
    [Fact] public void CyclicAncestryIsIncompleteAndTraversalTerminates()
    {
        var s = NewGraph().Apply(B(0, P(1, 2), P(2, 1)));
        Assert.All(s.Associations.Values, a => Assert.False(a.LineageComplete));
    }
    [Fact] public void ExitedShimRetainsLivingRootForItsHelper()
    {
        var g = NewGraph();
        var root = P(1, name: "claude.exe", path: @"C:\Users\test\.local\bin\claude.exe");
        var shim = P(2, 1, "bash.exe");
        var helper = P(3, 2);
        g.Apply(B(0, root, shim, helper));
        g.Apply(B(5, root, helper));
        var s = g.Apply(B(10, root, helper));
        Assert.Equal(new ProcessIdentity(1, 1), s.Associations[new(3, 1)].AgentRoot);
        Assert.Equal(OwnerKind.Cli, s.Associations[new(3, 1)].OwnerKind);
        Assert.Equal(ProcessRole.Helper, s.Associations[new(3, 1)].Role);
        Assert.Null(s.Current[new(1, 1)].ExitedAt);
    }
    [Fact] public void ExitedRootAndIntermediateStayPinnedBeyondRetention()
    {
        var g = NewGraph();
        var root = P(1, name: "codex.exe", path: @"C:\Users\test\.codex\bin\codex.exe");
        var child = P(2, 1);
        g.Apply(B(0, root, child));
        for (int i = 1; i <= 200; i++) g.Apply(B(i * 5, child));
        var s = g.Apply(B(1005, child));
        Assert.NotNull(s.Retained[new(1, 1)].ExitedAt);
        Assert.Equal(new ProcessIdentity(1, 1), s.Associations[new(2, 1)].AgentRoot);
    }
    [Theory]
    [InlineData("claude.exe", null, OwnerKind.Unknown)]
    [InlineData("CLAUDE.EXE", @"C:\Users\test\.local\bin\claude.exe", OwnerKind.Cli)]
    [InlineData("claude.exe", @"C:\Users\test\AppData\Local\AnthropicClaude\Claude.exe", OwnerKind.Desktop)]
    [InlineData("Code.exe", null, OwnerKind.Ide)]
    public void AllAgentImagesProtectedRegardlessOfMode(string name, string? path, OwnerKind owner)
    {
        var s = NewGraph().Apply(B(0, P(1, name: name, path: path)));
        var a = s.Associations[new(1, 1)];
        Assert.True(a.IsProtected);
        Assert.Equal(ProcessRole.AgentRoot, a.Role);
        Assert.Equal(owner, a.OwnerKind);
    }
    [Theory]
    [InlineData(LaunchMode.Interactive)]
    [InlineData(LaunchMode.Unknown)]
    public void TerminalShellWithoutNonInteractiveEvidenceIsProtected(LaunchMode mode)
    {
        var s = NewGraph().Apply(B(0, P(1, name: "pwsh.exe", mode: mode)));
        Assert.True(s.Associations[new(1, 1)].IsProtected);
    }
    [Fact] public void ExclusionsAndOtherUserAreExplicitProtection()
    {
        var g = new TemporalProcessGraph(new TestTime(), Classifier(new HashSet<string> { @"C:\tools\helper.exe" }));
        var s = g.Apply(B(0, P(1, path: @"C:\tools\helper.exe"), P(2) with { OwnerSid = "other" }));
        Assert.All(s.Associations.Values, a => Assert.True(a.IsProtected));
    }
    [Theory] [InlineData(2001)] [InlineData(10001)]
    public void CapacityOverflowDegradesInsteadOfHidingRisk(int count)
    {
        var s = NewGraph().Apply(B(0, Enumerable.Range(1, count).Select(i => P(i)).ToArray()));
        Assert.Equal(Coverage.Degraded, s.Coverage);
        Assert.True(s.Current.Count + s.Retained.Count <= 10000);
        Assert.True(s.Current.Values.Count(n => n.Metrics.Count > 0) <= 2000);
    }
    [Fact] public void BackwardUtcDoesNotChangeMonotonicMetricHistory()
    {
        var g = NewGraph();
        g.Apply(B(0, P(1)));
        var s = g.Apply(B(5, P(1)) with { Utc = DateTimeOffset.MinValue });
        Assert.Equal(2, s.Current[new(1, 1)].Metrics.Count);
        Assert.Equal(1000, s.TimestampFrequency);
    }
    [Fact] public void GapOrExplicitResumeDiscardsOldAuthorization()
    {
        var g = NewGraph();
        var before = g.Apply(B(0, P(1), P(2, 1)));
        var after = g.Apply(B(16, P(2, 1)));
        Assert.NotEqual(before.RunId, after.RunId);
        Assert.False(after.Current[new(2, 1)].ParentKnown);
        g.Reset("Sleep");
        Assert.NotEqual(after.RunId, g.Apply(B(21, P(2, 1))).RunId);
    }
    [Fact] public void NewRunDoesNotInheritExitOrMetricHistory()
    {
        var a = NewGraph().Apply(B(0, P(1)));
        var b = NewGraph().Apply(B(0, P(1)));
        Assert.NotEqual(a.RunId, b.RunId);
        Assert.Single(b.Current[new(1, 1)].Metrics);
    }
}
