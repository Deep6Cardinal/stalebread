using System.Text.Json;
using StaleBread.Core.Model;
namespace StaleBread.Core.Storage;

public sealed record StorageResult(bool Success, string? Error = null);
public sealed record SettingsLoad(Settings Settings, string? Error, bool Recovered);
public sealed class SettingsStore(string path, Func<string, string, CancellationToken, Task>? writer = null)
{
    public async Task<SettingsLoad> LoadAsync(CancellationToken ct = default)
    {
        if (!File.Exists(path)) return new(new(), null, false);
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) throw new JsonException("Settings too large");
            var settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(path, ct).ConfigureAwait(false))
                ?? throw new JsonException("Empty settings");
            if (settings.SchemaVersion > 1) return new(new(), "Settings belong to a newer version; original retained", false);
            settings = settings with { SchemaVersion = 1 };
            if (Validate(settings, Environment.ProcessorCount).Count > 0) throw new JsonException("Invalid settings");
            return new(settings, null, false);
        }
        catch (JsonException)
        {
            try { File.Move(path, path + ".corrupt-" + Guid.NewGuid().ToString("N")); }
            catch (IOException) { return new(new(), "Corrupt settings could not be preserved; original retained", false); }
            return new(new(), "Corrupt settings preserved; safe defaults loaded", true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { return new(new(), "Settings unavailable: " + e.GetType().Name, false); }
    }
    public async Task<StorageResult> SaveAsync(Settings settings, CancellationToken ct = default)
    {
        var errors = Validate(settings, Environment.ProcessorCount);
        if (errors.Count > 0) return new(false, string.Join("; ", errors));
        try
        {
            await (writer ?? AtomicFile.WriteAsync)(path, JsonSerializer.Serialize(settings), ct).ConfigureAwait(false);
            return new(true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        { return new(false, "Settings not saved: " + e.GetType().Name); }
    }
    public static IReadOnlyList<string> Validate(Settings s, int logicalProcessors)
    {
        var errors = new List<string>();
        if (s.SchemaVersion != 1) errors.Add("Unsupported settings version");
        if (!double.IsFinite(s.Volume) || s.Volume is < 0 or > 1) errors.Add("Volume must be between 0 and 1");
        if (s.IgnoredProcesses is null || s.IgnoredProcesses.Count > 512 || s.IgnoredProcesses.Any(i => i.Pid <= 0 || i.CreationFileTimeUtc <= 0))
            errors.Add("Ignored process identities must be valid and bounded to 512");
        if (s.ExcludedPaths is null || s.ExcludedPaths.Count > 512 || s.ExcludedPaths.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > 32768 || !Path.IsPathFullyQualified(p)))
            errors.Add("Exclusions must be bounded absolute paths");
        var r = s.AdvisoryRules;
        if (r is null) { errors.Add("Missing advisory defaults"); return errors; }
        bool Range(double n, double min, double max) => double.IsFinite(n) && n >= min && n <= max;
        if (!Range(r.BusyCores, .01, Math.Max(1, logicalProcessors)) || !Range(r.BusyMachinePercent, .1, 100)) errors.Add("CPU threshold out of range");
        if (!Range(r.BusyDuration.TotalSeconds, 60, 600) || !Range(r.MemoryDuration.TotalSeconds, 60, 600) ||
            !Range(r.GrowthDuration.TotalSeconds, 60, 600)) errors.Add("Advisory duration must be 60–600 seconds (bounded metric history)");
        if (!Range(r.PressurePrivateBytes, 1048576, 68719476736) || !Range(r.GrowthBytes, 1048576, 68719476736)) errors.Add("Memory threshold out of range");
        if (!Range(r.PressureLoadPercent, .1, 100) || !Range(r.GrowthFraction, .001, 100) ||
            !Range(r.HandleGrowthFraction, .001, 100)) errors.Add("Growth/pressure fraction out of range");
        if (new[] { r.GrowthHandles, r.EventStarts, r.SnapshotBirths, r.AccumulationCount, r.AccumulationAdded }.Any(n => n is < 1 or > 10000))
            errors.Add("Count threshold out of range");
        return errors;
    }
}
