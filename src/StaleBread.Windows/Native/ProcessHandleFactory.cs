using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using StaleBread.Core.Cleanup;
using StaleBread.Core.Model;

namespace StaleBread.Windows.Native;

public sealed class ProcessHandleFactory : IProcessHandleFactory
{
    public IValidatedProcessHandle Open(ProcessIdentity identity, ProcessAccess access) => OpenNative(identity, access);
    public static NativeProcessHandle OpenNative(ProcessIdentity identity, ProcessAccess access)
    {
        if (identity.Pid <= 0 || identity.CreationFileTimeUtc <= 0) throw new ArgumentException("Invalid exact identity");
        uint rights = 0x1000; // Query limited information; never request all-access.
        if (access.HasFlag(ProcessAccess.Terminate)) rights |= 1;
        rights |= 0x100000; // Liveness checked on the held process object.
        var handle = NativeProcessHandle.OpenProcess(rights, false, identity.Pid);
        if (handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        try
        {
            if (!NativeProcessHandle.GetProcessTimes(handle, out long created, out _, out _, out _) ||
                created != identity.CreationFileTimeUtc)
                throw new InvalidOperationException("Process identity changed or cannot be verified");
            return new(handle, identity, access);
        }
        catch { handle.Dispose(); throw; }
    }
}
public sealed class NativeProcessHandle : IValidatedProcessHandle
{
    private readonly SafeProcessHandle handle;
    private readonly ProcessAccess access;
    public ProcessIdentity Identity { get; }
    public string? OwnerSid { get; }
    public int? SessionId { get; }
    internal NativeProcessHandle(SafeProcessHandle handle, ProcessIdentity id, ProcessAccess access)
    {
        this.handle = handle; Identity = id; this.access = access;
        (OwnerSid, SessionId) = ReadToken(handle);
    }
    public bool IsAlive => !handle.IsClosed && !handle.IsInvalid && WaitForSingleObject(handle, 0) == 258 &&
        GetProcessTimes(handle, out long created, out _, out _, out _) && created == Identity.CreationFileTimeUtc;
    public string? ImagePath
    {
        get
        {
            if (!IsAlive) return null;
            var b = new StringBuilder(32768);
            int size = b.Capacity;
            return QueryFullProcessImageName(handle, 0, b, ref size) && IsAlive ? b.ToString() : null;
        }
    }
    public void Terminate()
    {
        if (!access.HasFlag(ProcessAccess.Terminate)) throw new UnauthorizedAccessException("Handle not opened for termination");
        if (!IsAlive) throw new InvalidOperationException("Exact process is no longer alive");
        if (!TerminateProcess(handle, 1)) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    public async Task<bool> WaitForExitAsync(TimeSpan timeout, CancellationToken ct)
    {
        var start = TimeProvider.System.GetTimestamp();
        do
        {
            ObjectDisposedException.ThrowIf(handle.IsClosed, this);
            uint state = WaitForSingleObject(handle, 0);
            if (state == 0) return true;
            if (state != 258) throw new Win32Exception(Marshal.GetLastWin32Error());
            await Task.Delay(25, ct).ConfigureAwait(false);
        } while (TimeProvider.System.GetElapsedTime(start) < timeout);
        return WaitForSingleObject(handle, 0) == 0;
    }
    public void Dispose() => handle.Dispose();
    private static (string?, int?) ReadToken(SafeProcessHandle process)
    {
        if (!OpenProcessToken(process, 8, out var token)) return (null, null);
        using (token)
        {
            GetTokenInformation(token, 1, 0, 0, out int required);
            if (required <= 0 || required > 65536) return (null, null);
            nint data = Marshal.AllocHGlobal(Math.Max(required, 4));
            try
            {
                string? sid = GetTokenInformation(token, 1, data, required, out _)
                    ? new SecurityIdentifier(Marshal.ReadIntPtr(data)).Value : null;
                int? session = GetTokenInformation(token, 12, data, required, out _) ? Marshal.ReadInt32(data) : null;
                return (sid, session);
            }
            finally { Marshal.FreeHGlobal(data); }
        }
    }
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetProcessTimes(SafeProcessHandle h, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(SafeProcessHandle h, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool TerminateProcess(SafeProcessHandle h, uint exitCode);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(SafeProcessHandle h, uint flags, StringBuilder path, ref int size);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(SafeProcessHandle h, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint data, int size, out int required);
}
