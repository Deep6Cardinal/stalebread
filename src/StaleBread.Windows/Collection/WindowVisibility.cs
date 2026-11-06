using System.Runtime.InteropServices;
namespace StaleBread.Windows.Collection;

public static class WindowVisibility
{
    private delegate bool Callback(nint window, nint value);
    public static IReadOnlySet<int>? Read()
    {
        var visible = new HashSet<int>();
        bool success = EnumWindows((window, _) => {
            if (IsWindowVisible(window)) { GetWindowThreadProcessId(window, out int pid); if (pid > 0) visible.Add(pid); }
            return true;
        }, 0);
        return success ? visible : null;
    }
    [DllImport("user32.dll")] private static extern bool EnumWindows(Callback callback, nint value);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out int pid);
}
