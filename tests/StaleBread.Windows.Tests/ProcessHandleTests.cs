using System.Diagnostics;
using StaleBread.Core.Cleanup;
using StaleBread.Core.Model;
using StaleBread.Windows.Native;

namespace StaleBread.Windows.Tests;

public class ProcessHandleTests
{
    [Fact] public async Task StopsOnlySelectedDisposableFixtureAndUntargetedSiblingSurvives()
    {
        using var target = ContainedFixture.Start("idle", 10000);
        using var sibling = ContainedFixture.Start("idle", 10000);
        var factory = new ProcessHandleFactory();
        using var selected = factory.Open(target.Identity, ProcessAccess.Query | ProcessAccess.Terminate | ProcessAccess.Synchronize);
        using var preserved = factory.Open(sibling.Identity, ProcessAccess.Query);
        selected.Terminate();
        Assert.True(await selected.WaitForExitAsync(TimeSpan.FromSeconds(5), default));
        Assert.True(preserved.IsAlive);
    }
    [Fact] public void QueryOnlyHandleCannotTerminateEvenAnOwnedFixture()
    {
        using var fixture = ContainedFixture.Start("idle", 10000);
        using var handle = new ProcessHandleFactory().Open(fixture.Identity, ProcessAccess.Query);
        Assert.Throws<UnauthorizedAccessException>(handle.Terminate);
        Assert.True(handle.IsAlive);
    }
    [Fact] public void ReplacementPidCannotBeOpenedAsHistoricIdentity()
    {
        using var self = Process.GetCurrentProcess();
        var id = new ProcessIdentity(self.Id, self.StartTime.ToUniversalTime().ToFileTimeUtc() - 1);
        Assert.Throws<InvalidOperationException>(() => new ProcessHandleFactory().Open(id, ProcessAccess.Query));
    }
    [Fact] public void DisposedHandleCannotBeReused()
    {
        using var self = Process.GetCurrentProcess();
        var id = new ProcessIdentity(self.Id, self.StartTime.ToUniversalTime().ToFileTimeUtc());
        var handle = new ProcessHandleFactory().Open(id, ProcessAccess.Query | ProcessAccess.Synchronize);
        Assert.Equal(id, handle.Identity);
        Assert.True(handle.IsAlive);
        Assert.Equal(self.SessionId, handle.SessionId);
        Assert.NotNull(handle.OwnerSid);
        handle.Dispose();
        Assert.False(handle.IsAlive);
    }
    [Fact] public void InvalidPidFailsClosed()
        => Assert.ThrowsAny<Exception>(() => new ProcessHandleFactory().Open(new(-1, 1), ProcessAccess.Query));
}
