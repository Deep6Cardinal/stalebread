using StaleBread.Core.Model;
using StaleBread.Core.Storage;
namespace StaleBread.Core.Tests;
public class SettingsControllerTests
{
    [Theory] [InlineData(true)] [InlineData(false)]
    public async Task StaleDraftCannotEraseNewIgnoreOrKeepProtection(bool ignore)
    {
        var controller = new SettingsController(new(), (_, _) => Task.FromResult(new StorageResult(true)));
        var draft = controller.Current;
        await controller.UpdateAsync(s => ignore ? s with { IgnoredProcesses = [new(12, 900)] } : s with { ExcludedPaths = [@"C:\keep.exe"] });
        var result = await controller.UpdateAsync(_ => draft with { Volume = .2 }, draft);
        Assert.False(result.Success); Assert.Equal(.4, controller.Current.Volume);
        if (ignore) Assert.Contains(new ProcessIdentity(12, 900), controller.Current.IgnoredProcesses);
        else Assert.Contains(@"C:\keep.exe", controller.Current.ExcludedPaths);
    }
    [Fact] public async Task ConcurrentEditsAreAppliedToLatestCommittedSettings()
    {
        var started = new TaskCompletionSource(); var release = new TaskCompletionSource(); int calls = 0;
        var controller = new SettingsController(new(), async (_, _) => { if (++calls == 1) { started.SetResult(); await release.Task; } return new(true); });
        var first = controller.UpdateAsync(s => s with { IgnoredProcesses = [new(12, 900)] });
        await started.Task;
        var second = controller.UpdateAsync(s => s with { ExcludedPaths = [@"C:\keep.exe"] });
        release.SetResult(); await Task.WhenAll(first, second);
        Assert.Contains(new ProcessIdentity(12, 900), controller.Current.IgnoredProcesses);
        Assert.Contains(@"C:\keep.exe", controller.Current.ExcludedPaths);
    }
    [Fact] public async Task FailedPersistenceDoesNotPublishEditedSettings()
    {
        var controller = new SettingsController(new(), (_, _) => Task.FromResult(new StorageResult(false, "disk full")));
        Assert.False((await controller.UpdateAsync(s => s with { Volume = .1 })).Success);
        Assert.Equal(.4, controller.Current.Volume);
    }
}
