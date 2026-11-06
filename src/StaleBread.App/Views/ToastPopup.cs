using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Threading;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using StaleBread.App.Services;
namespace StaleBread.App.Views;
public sealed class ToastPopup : Window
{
    private readonly ToastArt toast = new() { Height = 120 };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly PopupLifetime lifetime = new(TimeProvider.System);
    private readonly TextBlock message;
    public ToastPopup(string text, bool animate, Action review, Action snooze, Action dismiss)
    {
        Title = "StaleBread notification"; Width = 370; SizeToContent = SizeToContent.Height;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowActivated = false; ShowInTaskbar = false; Topmost = true;
        var panel = new StackPanel { Margin = new Thickness(18) }; Content = new Border { BorderBrush = SystemColors.WindowTextBrush, BorderThickness = new Thickness(1), Child = panel };
        panel.Children.Add(toast); message = Ui.Text(text, 17); panel.Children.Add(message);
        AutomationProperties.SetLiveSetting(message, AutomationLiveSetting.Polite);
        AutomationProperties.SetName(this, text);
        var actions = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) }; panel.Children.Add(actions);
        actions.Children.Add(Ui.Button("_Review", () => { Close(); review(); return Task.CompletedTask; }));
        actions.Children.Add(Ui.Button("_Snooze", () => { snooze(); Close(); return Task.CompletedTask; }));
        actions.Children.Add(Ui.Button("_Dismiss", () => { dismiss(); Close(); return Task.CompletedTask; }));
        SourceInitialized += (_, _) =>
        {
            nint hwnd = new WindowInteropHelper(this).Handle;
            SetWindowLongPtrW(hwnd, -20, GetWindowLongPtrW(hwnd, -20) | 0x08000000); // WS_EX_NOACTIVATE
            HwndSource.FromHwnd(hwnd)?.AddHook((nint h, int m, nint w, nint l, ref bool handled) =>
            { if (m == 0x21) { handled = true; return 3; } return 0; }); // MA_NOACTIVATE
        };
        Loaded += (_, _) =>
        {
            PlaceAtWorkAreaCorner(); toast.Pop(animate); timer.Start();
            var peer = UIElementAutomationPeer.CreatePeerForElement(message);
            peer?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        };
        timer.Tick += (_, _) => { if (lifetime.Expired(IsMouseOver)) Close(); };
        Closed += (_, _) => { timer.Stop(); toast.Stop(); };
    }
    private void PlaceAtWorkAreaCorner()
    {
        var area = System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).WorkingArea;
        nint hwnd = new WindowInteropHelper(this).Handle;
        // First move selects target monitor/DPI; then use the actual native window size.
        SetWindowPos(hwnd, 0, area.Left, area.Top, 0, 0, 0x15);
        GetWindowRect(hwnd, out var rect);
        SetWindowPos(hwnd, 0, Math.Max(area.Left, area.Right - (rect.Right - rect.Left) - 16),
            Math.Max(area.Top, area.Bottom - (rect.Bottom - rect.Top) - 16), 0, 0, 0x15);
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll")] private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
}
