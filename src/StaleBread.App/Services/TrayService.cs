using Forms = System.Windows.Forms;
using System.Drawing;
namespace StaleBread.App.Services;

public sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon icon;
    private readonly Forms.ToolStripMenuItem pause;
    public TrayService(Action findings, Action companion, Action settings, Action togglePause, Action quiet, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open findings", null, (_, _) => findings());
        menu.Items.Add("Show toast companion", null, (_, _) => companion());
        pause = new("Pause watching", null, (_, _) => togglePause()); menu.Items.Add(pause);
        menu.Items.Add("Quiet for one hour", null, (_, _) => quiet());
        menu.Items.Add("Settings", null, (_, _) => settings());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit StaleBread", null, (_, _) => exit());
        icon = new Forms.NotifyIcon { Icon = SystemIcons.Information, Text = "StaleBread — starting", Visible = true, ContextMenuStrip = menu };
        icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) findings(); };
        icon.DoubleClick += (_, _) => findings();
    }
    public void Update(string status, bool paused)
    { icon.Text = status.Length <= 63 ? status : status[..63]; pause.Text = paused ? "Resume watching" : "Pause watching"; }
    public void SetIcon(Icon value) => icon.Icon = value;
    public void ExplainClose() => icon.ShowBalloonTip(4000, "StaleBread is still watching", "Open the tray icon to review findings, or choose Exit to stop monitoring.", Forms.ToolTipIcon.Info);
    public void Dispose() { icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); }
}
