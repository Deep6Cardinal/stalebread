using StaleBread.App.ViewModels;
using StaleBread.Core.Trends;
using StaleBread.Windows.Desktop;
namespace StaleBread.Windows.Tests;

public class DesktopTests
{
    [Fact] public void SingleInstanceScopeRejectsSecondOwner()
    {
        string scope = "test-" + Guid.NewGuid().ToString("N");
        using var first = new SingleInstance(scope); using var second = new SingleInstance(scope);
        Assert.True(first.IsFirst); Assert.False(second.IsFirst);
    }
    [Fact] public void StartupPathIsQuotedAndMovingExecutableIsVisible()
    {
        string? saved = null;
        var startup = new StartupRegistration(@"C:\A space\ごはん\StaleBread.exe", () => saved, value => saved = value);
        Assert.True(startup.SetEnabled(true).Success);
        Assert.Equal("\"C:\\A space\\ごはん\\StaleBread.exe\" --minimized", saved);
        Assert.True(new StartupRegistration(@"C:\Moved\StaleBread.exe", () => saved).IsStale);
        Assert.True(startup.SetEnabled(false).Success); Assert.Null(saved);
    }
    [Fact] public void CloseHidesOnceButExplicitExitCanClose()
    {
        var life = new WindowLifetime();
        Assert.Equal((true, true), life.Close()); Assert.Equal((true, false), life.Close());
        life.ExitRequested = true; Assert.Equal((false, false), life.Close());
    }
    [Theory] [InlineData(20, 20, "+20 / −20")] [InlineData(18, 15, "+18 / −15")] [InlineData(0, 0, "+0 / −0")]
    [InlineData(null, null, "— / —")]
    public void CompactDisplayNeverReplacesGrossChangesWithNet(int? added, int? removed, string expected)
    {
        var delta = new CountDelta(TimeSpan.FromSeconds(60), added - removed, added, removed, null, null);
        var counts = new CountTrendSnapshot(42, 0, false, false, [delta], null, 0);
        Assert.Contains(expected, CountText.Compact(counts));
        Assert.Contains("stale", CountText.Compact(counts with { Stale = true }));
        Assert.Contains("limited", CountText.Compact(counts with { Partial = true }));
    }
}
