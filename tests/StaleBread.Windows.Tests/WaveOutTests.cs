using StaleBread.App.Services;
using StaleBread.Windows.Audio;
namespace StaleBread.Windows.Tests;
public class WaveOutTests
{
    private sealed class Device : IWaveDevice
    {
        public readonly List<string> Calls = []; public uint WriteError; public bool Completed = true; public uint UnprepareError;
        public uint Open(PcmWave wave, out nint device) { Calls.Add("open"); device = 123; return 0; }
        public uint Prepare(nint device, nint header) { Calls.Add("prepare"); return 0; }
        public uint Write(nint device, nint header) { Calls.Add("write"); return WriteError; }
        public bool Done(nint header) => Completed;
        public uint Reset(nint device) { Calls.Add("reset"); return 0; }
        public uint Unprepare(nint device, nint header) { Calls.Add("unprepare"); return UnprepareError; }
        public uint Close(nint device) { Calls.Add("close"); return 0; }
    }
    [Fact] public async Task WriteFailureResetsBeforeReleasingDriverOwnedBuffers()
    {
        var device = new Device { WriteError = 6 }; var output = new WaveOutOutput(device);
        await Assert.ThrowsAsync<IOException>(() => output.PlayAsync(new(44100, 1, [0, 0]), default));
        Assert.Equal(new[] { "open", "prepare", "write", "reset", "unprepare", "close" }, device.Calls);
    }
    [Fact] public async Task DriverThatRetainsBufferDisablesFuturePlaybackInsteadOfGrowingAllocations()
    {
        var device = new Device { UnprepareError = 33 }; var output = new WaveOutOutput(device);
        await Assert.ThrowsAsync<IOException>(() => output.PlayAsync(new(44100, 1, [0, 0]), default));
        await Assert.ThrowsAsync<IOException>(() => output.PlayAsync(new(44100, 1, [0, 0]), default));
        Assert.Single(device.Calls, c => c == "open");
        Assert.DoesNotContain("close", device.Calls);
    }
}
