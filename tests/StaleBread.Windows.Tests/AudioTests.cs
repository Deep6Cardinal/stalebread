using StaleBread.App.Services;
using StaleBread.Windows.Audio;
namespace StaleBread.Windows.Tests;
public class AudioTests
{
    [Fact] public void BundledRecordedAssetIsOneSecondPcmWithHeadroom()
    {
        using var stream = typeof(AudioService).Assembly.GetManifestResourceStream("StaleBread.Assets.toaster-pop.wav");
        Assert.NotNull(stream); var wave = PcmWave.Read(stream);
        Assert.Equal(44100, wave.SampleRate); Assert.Equal(88200, wave.Samples.Length);
        int peak = Enumerable.Range(0, wave.Samples.Length / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(wave.Samples, i * 2)));
        Assert.InRange(peak, 20000, 23500);
    }
    private static Stream Wave(short format = 1)
    {
        var stream = new MemoryStream(); var w = new BinaryWriter(stream);
        w.Write("RIFF"u8); w.Write(42); w.Write("WAVEfmt "u8); w.Write(16); w.Write(format); w.Write((short)1);
        w.Write(44100); w.Write(88200); w.Write((short)2); w.Write((short)16); w.Write("data"u8); w.Write(6);
        w.Write(short.MinValue); w.Write((short)10000); w.Write(short.MaxValue); stream.Position = 0; return stream;
    }
    [Fact] public void PcmScalingHonorsVolumeWithoutMutatingAssetOrClipping()
    {
        var wave = PcmWave.Read(Wave()); var scaled = wave.Scale(.4);
        Assert.Equal(-13107, BitConverter.ToInt16(scaled.Samples, 0));
        Assert.Equal(4000, BitConverter.ToInt16(scaled.Samples, 2));
        Assert.Equal(short.MinValue, BitConverter.ToInt16(wave.Samples, 0));
        Assert.All(wave.Scale(0).Samples, b => Assert.Equal(0, b));
        Assert.Throws<ArgumentOutOfRangeException>(() => wave.Scale(double.NaN));
    }
    [Fact] public void RejectsCompressedAndTruncatedAssets()
    {
        Assert.Throws<InvalidDataException>(() => PcmWave.Read(Wave(3)));
        Assert.Throws<InvalidDataException>(() => PcmWave.Read(new MemoryStream([1, 2, 3])));
    }
    private sealed class Output : IWaveOutput
    { public int Calls; public bool Fail; public async Task PlayAsync(PcmWave wave, CancellationToken ct) { Calls++; if (Fail) throw new IOException("device removed"); await Task.Delay(10000, ct); } }
    [Fact] public async Task MuteDoesNotOpenDeviceAndDeviceLossDoesNotEscapeOrRetry()
    {
        var output = new Output { Fail = true }; using var audio = new AudioService(output, () => Wave());
        await audio.PlayPopAsync(0, default); Assert.Equal(0, output.Calls);
        await audio.PlayPopAsync(.4, default); Assert.Equal(1, output.Calls); Assert.NotNull(audio.Error);
    }
    [Fact] public async Task PlaybackCoalescesAndStopCancelsOwnedPlayback()
    {
        var output = new Output(); using var audio = new AudioService(output, () => Wave());
        var playing = audio.PlayPopAsync(.4, default);
        await audio.PlayPopAsync(.4, default); audio.Stop(); await playing.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, output.Calls);
    }
}
