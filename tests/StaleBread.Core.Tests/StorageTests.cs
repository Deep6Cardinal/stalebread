using System.Text.Json;
using StaleBread.Core.Model;
using StaleBread.Core.Storage;

namespace StaleBread.Core.Tests;

public class StorageTests : IDisposable
{
    [Fact] public void AdvisoryDurationsCannotExceedTheAvailableMetricHistory()
    {
        var settings = new Settings { AdvisoryRules = new() { BusyDuration = TimeSpan.FromHours(1) } };
        Assert.NotEmpty(SettingsStore.Validate(settings, 12));
    }
    [Fact] public async Task OversizedHistoryIsNotSilentlyOverwrittenOnNextAttempt()
    {
        string path = Path.Combine(root, "oversized.json");
        string original = new('x', 3000);
        await File.WriteAllTextAsync(path, original);
        await using var store = new HistoryStore(path, new TestTime(), maxBytes: 2048);
        var first = await store.AppendAsync(new(1, DateTimeOffset.UtcNow, HistoryKind.Action, "first"));
        var next = await store.AppendAsync(new(1, DateTimeOffset.UtcNow, HistoryKind.Action, "next"));
        Assert.False(first.Success); Assert.False(next.Success);
        Assert.Equal(original, await File.ReadAllTextAsync(path));
    }
    private readonly string root = Path.Combine(Path.GetTempPath(), "StaleBread-tests-" + Guid.NewGuid().ToString("N"));
    public StorageTests() => Directory.CreateDirectory(root);
    public void Dispose() => Directory.Delete(root, true); // Unique directory owned by this fixture only.
    [Fact] public async Task SettingsRoundTripAndCorruptOriginalIsPreserved()
    {
        var path = Path.Combine(root, "settings.json"); var store = new SettingsStore(path);
        Assert.True((await store.SaveAsync(new Settings { Volume = .2 })).Success);
        Assert.Equal(.2, (await store.LoadAsync()).Settings.Volume);
        await File.WriteAllTextAsync(path, "{broken");
        var loaded = await store.LoadAsync();
        Assert.True(loaded.Recovered); Assert.NotNull(loaded.Error);
        Assert.Contains(Directory.GetFiles(root), f => f.Contains(".corrupt-", StringComparison.Ordinal));
    }
    [Fact] public async Task FailedAtomicWriteDoesNotClaimSuccessOrReplaceOldSettings()
    {
        var path = Path.Combine(root, "settings.json");
        await new SettingsStore(path).SaveAsync(new Settings { Volume = .2 });
        var store = new SettingsStore(path, (_, _, _) => throw new IOException("disk full"));
        Assert.False((await store.SaveAsync(new Settings { Volume = .9 })).Success);
        Assert.Equal(.2, (await new SettingsStore(path).LoadAsync()).Settings.Volume);
    }
    [Theory] [InlineData(-.1)] [InlineData(1.1)] [InlineData(double.NaN)]
    public void InvalidVolumeIsRejected(double volume)
        => Assert.NotEmpty(SettingsStore.Validate(new Settings { Volume = volume }, 12));
    [Fact] public async Task HistoryExpiresAfterSevenDaysAndAllKindsShareDiskCap()
    {
        var t = new TestTime();
        var path = Path.Combine(root, "history.json");
        await using var store = new HistoryStore(path, t, maxBytes: 2048);
        await store.AppendAsync(new(1, t.GetUtcNow().AddDays(-8), HistoryKind.Action, "expired"));
        for (int i = 0; i < 30; i++) await store.AppendAsync(new(1, t.GetUtcNow(),
            i % 2 == 0 ? HistoryKind.Action : HistoryKind.Finding, new string('x', 80)));
        var entries = await store.ReadAsync();
        Assert.DoesNotContain(entries, e => e.Summary == "expired");
        Assert.InRange(new FileInfo(path).Length, 1, 2048);
        Assert.Contains(entries, e => e.Kind == HistoryKind.Action);
        Assert.Contains(entries, e => e.Kind == HistoryKind.Finding);
    }
    [Fact] public async Task DiskFailureMakesAuditUnavailableWithoutCrashing()
    {
        await using var store = new HistoryStore(Path.Combine(root, "history.json"), new TestTime(),
            writer: (_, _, _) => throw new IOException("disk full"));
        Assert.False((await store.AppendAsync(new(1, DateTimeOffset.UtcNow, HistoryKind.Action, "attempt"))).Success);
        Assert.False(store.AuditHealthy);
    }
    [Fact] public async Task QueueOverflowDropsObservationsBeforeAudits()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var store = new HistoryStore(Path.Combine(root, "history.json"), new TestTime(), capacity: 2,
            writer: async (_, _, _) => { entered.TrySetResult(); await release.Task; });
        var first = store.AppendAsync(new(1, DateTimeOffset.UtcNow, HistoryKind.Finding, "first"));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var second = store.AppendAsync(new(1, DateTimeOffset.UtcNow, HistoryKind.Finding, "second"));
        var third = store.AppendAsync(new(1, DateTimeOffset.UtcNow, HistoryKind.Action, "audit"));
        var fourth = store.AppendAsync(new(1, DateTimeOffset.UtcNow, HistoryKind.Finding, "fourth"));
        release.SetResult();
        await Task.WhenAll(first, second, third, fourth);
        Assert.True((await third).Success);
        Assert.True(store.Dropped >= 1);
    }
    [Fact] public void ExportOmitsPrivateTextAndAnonymizesIdentities()
    {
        var entry = new HistoryEntry(1, DateTimeOffset.UtcNow, HistoryKind.Error,
            @"ExampleUser C:\Users\ExampleUser\secret-chat-title --api-key=secret", Identity: new(123, 999));
        var result = DiagnosticExporter.Export(new(new Settings { ExcludedPaths = [@"C:\Users\ExampleUser\helper.exe"] },
            [entry], Coverage.SnapshotOnly, @"C:\Users\ExampleUser", "ExampleUser"));
        foreach (string content in new[] { result.Json, result.Csv })
        {
            Assert.DoesNotContain("ExampleUser", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret", content, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("999", content);
        }
        using var json = JsonDocument.Parse(result.Json);
        Assert.NotEmpty(result.RedactionSummary);
    }
}
