using System.Buffers.Binary;
using System.Diagnostics;
using StaleBread.Core.Model;
using StaleBread.Windows.Native;
using StaleBread.Windows.Collection;

namespace StaleBread.Windows.Tests;

public class CollectorTests
{
    [Fact] public async Task NativeCollectionDoesNotLaunchPeriodicShellChildren()
    {
        using var self = Process.GetCurrentProcess();
        await using var collector = new WindowsProcessCollector(TimeProvider.System, self.SessionId, trace: false);
        for (int i = 0; i < 5; i++)
        {
            var b = await collector.CollectAsync(default);
            Assert.DoesNotContain(b.Processes, p => p.ParentPid == self.Id &&
                p.Name.ToLowerInvariant() is "cmd.exe" or "pwsh.exe" or "powershell.exe" or "bash.exe");
        }
    }
    public static byte[] Buffer()
    {
        var b = new byte[272];
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(32), 12345);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(80), 123);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(88), 100);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(100), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(58), 16);
        BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(64), 4096 + 256);
        System.Text.Encoding.Unicode.GetBytes("node.exe").CopyTo(b, 256);
        return b;
    }
    [Fact] public void ParentCreationAndPidComeFromSameEntry()
    {
        var result = NativeProcessSnapshot.Parse(Buffer(), 4096);
        Assert.True(result.Valid, result.Error);
        var p = Assert.Single(result.Processes);
        Assert.Equal(new ProcessIdentity(123, 12345), p.Identity);
        Assert.Equal(100, p.ParentPid);
        Assert.Equal("node.exe", p.Name);
        Assert.Equal(1, p.SessionId);
    }
    [Theory] [InlineData("short")] [InlineData("offset")] [InlineData("pointer")] [InlineData("odd")] [InlineData("pid")] [InlineData("negativeCpu")]
    public void InvalidNativeLayoutNeverEnablesLineage(string corruption)
    {
        var b = Buffer();
        if (corruption == "short") b = b[..100];
        if (corruption == "offset") BinaryPrimitives.WriteUInt32LittleEndian(b, 12);
        if (corruption == "pointer") BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(64), long.MaxValue);
        if (corruption == "odd") BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(56), 15);
        if (corruption == "pid") BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(80), long.MaxValue);
        if (corruption == "negativeCpu") BinaryPrimitives.WriteInt64LittleEndian(b.AsSpan(40), -1);
        var r = NativeProcessSnapshot.Parse(b, 4096);
        Assert.False(r.Valid);
        Assert.Empty(r.Processes);
    }
    [Fact] public void NativeSnapshotMatchesOwnExactCreationOnThisWindowsBuild()
    {
        using var self = Process.GetCurrentProcess();
        var result = new NativeProcessSnapshot().Read();
        Assert.True(result.Valid, result.Error);
        var observed = Assert.Single(result.Processes, p => p.Pid == self.Id);
        Assert.Equal(self.StartTime.ToUniversalTime().ToFileTimeUtc(), observed.Identity?.CreationFileTimeUtc);
        Assert.Equal(self.SessionId, observed.SessionId);
    }
    [Fact] public void OverflowHasAnExplicitGapAndBoundedEvents()
    {
        using var events = new ProcessEventSource(TimeProvider.System);
        for (int i = 0; i < 5000; i++) events.Enqueue(new(i, 1, "node.exe", true, i, null, null, 1, null, null));
        var batch = events.Drain();
        Assert.True(batch.Loss);
        Assert.Equal(4096, batch.Events.Count);
        Assert.Empty(events.Drain().Events);
    }
    [Fact] public async Task StalledEnrichmentNeverCreatesReplacementWorkers()
    {
        using var release = new ManualResetEventSlim();
        using var started = new ManualResetEventSlim();
        using var worker = new BoundedWorker<int>();
        var first = worker.TryStart(() => { started.Set(); release.Wait(); return 42; });
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(3)));
            Assert.NotNull(first);
            for (int i = 0; i < 100; i++) Assert.Null(worker.TryStart(() => 0));
        }
        finally { release.Set(); }
        Assert.Equal(42, await first!.WaitAsync(TimeSpan.FromSeconds(3)));
    }
    [Theory]
    [InlineData("20260101010000.123456+060", true)]
    [InlineData("20251231190000.123456-300", true)]
    [InlineData("20260101000000.123455+000", false)]
    [InlineData("20260101000000.******+000", false)]
    public void WmiMicrosecondsAndOffsetAreValidatedWithoutReplacingNativeIdentity(string date, bool matches)
    {
        var exact = DateTime.Parse("2026-01-01T00:00:00.1234567Z", null,
            System.Globalization.DateTimeStyles.AdjustToUniversal).ToFileTimeUtc();
        Assert.Equal(matches, MetadataEnricher.WmiDateMatches(date, new(1, exact)));
    }
}
