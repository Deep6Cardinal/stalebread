using System.Globalization;
using System.Management;
using System.Runtime.InteropServices;
using StaleBread.Core.Cleanup;
using StaleBread.Core.Graph;
using StaleBread.Core.Model;
using StaleBread.Windows.Native;

namespace StaleBread.Windows.Collection;

public sealed record EnrichmentResult(ProcessIdentity RequestedIdentity, ProcessIdentity? VerifiedIdentity,
    string? ImagePath, string? OwnerSid, int? SessionId, string? Marker, LaunchMode LaunchMode,
    bool? HasVisibleWindow, string? FailureCode);
public sealed class MetadataEnricher : IDisposable
{
    private readonly BoundedWorker<EnrichmentResult> worker = new();
    public Task<EnrichmentResult>? TryEnrichAsync(ProcessIdentity id, CancellationToken ct)
        => ct.IsCancellationRequested ? null : worker.TryStart(() => Enrich(id));
    public static bool WmiDateMatches(string date, ProcessIdentity id)
    {
        if (date.Length != 25 || date[14] != '.' || (date[21] != '+' && date[21] != '-')) return false;
        if (!DateTime.TryParseExact(date[..21], "yyyyMMddHHmmss.ffffff", CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var local) || !int.TryParse(date.AsSpan(22), out int minutes)) return false;
        try
        {
            var utc = DateTime.SpecifyKind(local.AddMinutes(date[21] == '+' ? -minutes : minutes), DateTimeKind.Utc);
            return utc.ToFileTimeUtc() / 10 == id.CreationFileTimeUtc / 10;
        }
        catch (ArgumentOutOfRangeException) { return false; }
    }
    private static EnrichmentResult Enrich(ProcessIdentity id)
    {
        try
        {
            using var h = ProcessHandleFactory.OpenNative(id, ProcessAccess.Query);
            var path = h.ImagePath;
            if (!h.IsAlive) return Failure(id, "ExitedBeforeMetadata");
            string? marker = null;
            var mode = LaunchMode.Unknown;
            string? failure = "CommandMetadataUnavailable";
            try
            {
                using var search = new ManagementObjectSearcher(
                    $"SELECT ProcessId, CreationDate, CommandLine FROM Win32_Process WHERE ProcessId = {id.Pid}");
                search.Options.Timeout = TimeSpan.FromSeconds(2);
                using var results = search.Get();
                foreach (ManagementObject item in results)
                {
                    using (item)
                    {
                        if (Convert.ToInt32(item["ProcessId"]) != id.Pid ||
                            item["CreationDate"] is not string date || !WmiDateMatches(date, id) || !h.IsAlive)
                            continue;
                        // Never retain raw command text: emit only bounded semantic markers and mode.
                        if (item["CommandLine"] is string command && command.Length <= 32768)
                        {
                            failure = null;
                            var tokens = Tokenize(command);
                            mode = LaunchEvidence.Parse(Path.GetFileName(path) ?? "", tokens.Skip(1).ToArray());
                            foreach (var token in tokens.Take(2))
                            {
                                string normalized = token.Replace('\\', '/');
                                if (normalized.EndsWith("/node_modules/@anthropic-ai/claude-code/cli.js", StringComparison.OrdinalIgnoreCase))
                                    marker = "@anthropic-ai/claude-code";
                                if (normalized.EndsWith("/node_modules/@openai/codex/bin/codex.js", StringComparison.OrdinalIgnoreCase))
                                    marker = "@openai/codex";
                            }
                        }
                    }
                }
            }
            catch (Exception e) when (e is ManagementException or UnauthorizedAccessException or COMException)
            { failure = "CommandMetadataUnavailable"; }
            bool? visible = Visibility(id.Pid);
            if (!h.IsAlive) return Failure(id, "ExitedDuringMetadata");
            return new(id, id, path, h.OwnerSid, h.SessionId, marker, mode, visible, failure);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException)
        { return Failure(id, "IdentityOrAccessUnavailable"); }
    }
    private static EnrichmentResult Failure(ProcessIdentity id, string failure)
        => new(id, null, null, null, null, null, LaunchMode.Unknown, null, failure);
    private delegate bool EnumCallback(nint window, nint data);
    private static bool? Visibility(int pid)
    {
        bool found = false;
        bool success = EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out int owner);
            if (owner == pid && IsWindowVisible(window)) found = true;
            return true;
        }, 0);
        return success ? found : null;
    }
    private static string[] Tokenize(string command)
    {
        nint p = CommandLineToArgvW(command, out int count);
        if (p == 0) return [];
        try { return Enumerable.Range(0, Math.Min(count, 1024)).Select(i => Marshal.PtrToStringUni(Marshal.ReadIntPtr(p, i * IntPtr.Size)) ?? "").ToArray(); }
        finally { LocalFree(p); }
    }
    public void Dispose() => worker.Dispose();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern nint CommandLineToArgvW(string command, out int count);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint pointer);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumCallback callback, nint data);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
}
