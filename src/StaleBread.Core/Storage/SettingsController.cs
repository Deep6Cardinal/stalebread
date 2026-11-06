using StaleBread.Core.Model;
namespace StaleBread.Core.Storage;
public sealed class SettingsController(Settings initial, Func<Settings, Settings, Task<StorageResult>> persist)
{
    private Settings current = initial;
    private readonly SemaphoreSlim gate = new(1, 1);
    public Settings Current => Volatile.Read(ref current);
    public async Task<StorageResult> UpdateAsync(Func<Settings, Settings> change, Settings? expected = null)
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (expected is not null && !ReferenceEquals(expected, Current))
                return new(false, "Settings changed elsewhere. Close and reopen Settings before saving; newer protections were retained.");
            var next = change(Current); var result = await persist(Current, next).ConfigureAwait(false);
            if (result.Success) Volatile.Write(ref current, next);
            return result;
        }
        finally { gate.Release(); }
    }
}
