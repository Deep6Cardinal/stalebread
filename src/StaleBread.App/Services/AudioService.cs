using StaleBread.Windows.Audio;
namespace StaleBread.App.Services;
public interface IWaveOutput { Task PlayAsync(PcmWave wave, CancellationToken ct); }
public sealed class AudioService(IWaveOutput output, Func<Stream> asset) : IDisposable
{
    public string? Error { get; private set; }
    private readonly object gate = new();
    private CancellationTokenSource? playing;
    private bool disposed;
    public async Task PlayPopAsync(double volume, CancellationToken ct)
    {
        CancellationTokenSource current;
        lock (gate)
        {
            if (disposed || playing is not null || volume == 0 || ct.IsCancellationRequested) return;
            playing = current = CancellationTokenSource.CreateLinkedTokenSource(ct);
        }
        try
        {
            using var stream = asset(); var wave = PcmWave.Read(stream).Scale(volume);
            await output.PlayAsync(wave, current.Token).ConfigureAwait(false); Error = null;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { Error = "Audio unavailable: " + e.GetType().Name; }
        finally { lock (gate) { playing = null; current.Dispose(); } }
    }
    public void Stop() { lock (gate) playing?.Cancel(); }
    public void Dispose() { lock (gate) { disposed = true; playing?.Cancel(); } }
}
