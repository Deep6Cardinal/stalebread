using System.Runtime.InteropServices;
using StaleBread.Core.Notifications;
namespace StaleBread.Windows.Desktop;

public sealed class DesktopStateProvider(int sessionId)
{
    public DesktopState Read()
    {
        bool disconnected = true;
        if (WTSQuerySessionInformationW(0, sessionId, 8, out var info, out int bytes))
        { try { disconnected = bytes < 4 || Marshal.ReadInt32(info) != 0; } finally { WTSFreeMemory(info); } }
        nint desktop = OpenInputDesktop(0, false, 1);
        bool locked = desktop == 0;
        if (desktop != 0) CloseDesktop(desktop);
        var notification = SHQueryUserNotificationState(out int state) == 0
            ? state == 5 ? NotificationState.Accepts : NotificationState.Suppressed : NotificationState.Unknown;
        bool animation = false;
        bool motionKnown = SystemParametersInfoW(0x1042, 0, ref animation, 0);
        return new(locked, disconnected, notification, !motionKnown || !animation);
    }
    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode)] private static extern bool WTSQuerySessionInformationW(nint server, int session, int kind, out nint data, out int bytes);
    [DllImport("wtsapi32.dll")] private static extern void WTSFreeMemory(nint data);
    [DllImport("user32.dll")] private static extern nint OpenInputDesktop(uint flags, bool inherit, uint rights);
    [DllImport("user32.dll")] private static extern bool CloseDesktop(nint desktop);
    [DllImport("shell32.dll")] private static extern int SHQueryUserNotificationState(out int state);
    [DllImport("user32.dll")] private static extern bool SystemParametersInfoW(uint action, uint parameter, ref bool value, uint flags);
}
