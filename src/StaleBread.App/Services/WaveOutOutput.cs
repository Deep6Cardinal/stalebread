using StaleBread.Windows.Audio;
using System.Runtime.InteropServices;
using System.Diagnostics;
namespace StaleBread.App.Services;
public interface IWaveDevice
{
    uint Open(PcmWave wave, out nint device);
    uint Prepare(nint device, nint header);
    uint Write(nint device, nint header);
    bool Done(nint header);
    uint Reset(nint device);
    uint Unprepare(nint device, nint header);
    uint Close(nint device);
}
public sealed class WaveOutOutput(IWaveDevice native) : IWaveOutput
{
    private int busy;
    private volatile bool disabled;
    public Task PlayAsync(PcmWave wave, CancellationToken ct) => Task.Run(() =>
    {
        if (disabled) throw new IOException("Audio device retained resources; restart required");
        if (Interlocked.Exchange(ref busy, 1) != 0) return;
        nint device = 0, data = 0, header = 0; bool prepared = false, released = true;
        try
        {
            ct.ThrowIfCancellationRequested();
            Check(native.Open(wave, out device));
            data = Marshal.AllocHGlobal(wave.Samples.Length);
            Marshal.Copy(wave.Samples, 0, data, wave.Samples.Length);
            header = Marshal.AllocHGlobal(Marshal.SizeOf<WaveHeader>());
            Marshal.StructureToPtr(new WaveHeader { Data = data, BufferLength = (uint)wave.Samples.Length }, header, false);
            Check(native.Prepare(device, header)); prepared = true;
            Check(native.Write(device, header));
            var elapsed = Stopwatch.StartNew();
            while (!native.Done(header))
            {
                ct.ThrowIfCancellationRequested();
                if (elapsed.Elapsed > TimeSpan.FromSeconds(3)) throw new IOException("Audio completion timed out");
                ct.WaitHandle.WaitOne(20);
            }
        }
        finally
        {
            if (prepared)
            {
                native.Reset(device);
                released = native.Unprepare(device, header) == 0;
                // If the driver still owns these pointers, deliberately retain this ONE bounded
                // allocation until process exit. Never free it under the driver or retry playback.
                if (!released) disabled = true;
            }
            if (released)
            {
                if (header != 0) Marshal.FreeHGlobal(header);
                if (data != 0) Marshal.FreeHGlobal(data);
                if (device != 0 && native.Close(device) != 0) disabled = true;
            }
            Volatile.Write(ref busy, 0);
            if (!released) throw new IOException("Audio driver did not release buffer");
        }
    }, ct);
    private static void Check(uint result) { if (result != 0) throw new IOException("Audio device error " + result); }
}
[StructLayout(LayoutKind.Sequential)]
public struct WaveHeader
{ public nint Data; public uint BufferLength, BytesRecorded; public nuint User; public uint Flags, Loops; public nint Next; public nuint Reserved; }
public sealed class WinMmDevice : IWaveDevice
{
    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct Format { public ushort Tag, Channels; public uint Rate, ByteRate; public ushort Align, Bits, Extra; }
    public uint Open(PcmWave wave, out nint device)
    {
        var format = new Format { Tag = 1, Channels = (ushort)wave.Channels, Rate = (uint)wave.SampleRate,
            ByteRate = (uint)(wave.SampleRate * wave.Channels * 2), Align = (ushort)(wave.Channels * 2), Bits = 16 };
        return waveOutOpen(out device, uint.MaxValue, ref format, 0, 0, 0);
    }
    public uint Prepare(nint device, nint header) => waveOutPrepareHeader(device, header, (uint)Marshal.SizeOf<WaveHeader>());
    public uint Write(nint device, nint header) => waveOutWrite(device, header, (uint)Marshal.SizeOf<WaveHeader>());
    public bool Done(nint header) => (Marshal.PtrToStructure<WaveHeader>(header).Flags & 1) != 0;
    public uint Reset(nint device) => waveOutReset(device);
    public uint Unprepare(nint device, nint header) => waveOutUnprepareHeader(device, header, (uint)Marshal.SizeOf<WaveHeader>());
    public uint Close(nint device) => waveOutClose(device);
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out nint device, uint id, ref Format format, nint callback, nint instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(nint device);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(nint device, nint header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(nint device);
}
