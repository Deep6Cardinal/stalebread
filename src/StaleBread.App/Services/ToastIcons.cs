using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
namespace StaleBread.App.Services;
public static class ToastIcons
{
    public static Icon Create(string state)
    {
        using var bitmap = new Bitmap(32, 32); using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias; g.Clear(Color.Transparent);
        using var shape = new GraphicsPath(); shape.AddBezier(6, 11, -2, 0, 34, 0, 26, 11);
        shape.AddLine(26, 11, 26, 27); shape.AddBezier(26, 27, 20, 30, 12, 30, 6, 27); shape.CloseFigure();
        using var crust = new SolidBrush(Color.FromArgb(214, 148, 68)); using var edge = new Pen(Color.FromArgb(92, 53, 26), 2);
        g.FillPath(crust, shape); g.DrawPath(edge, shape);
        using var crumb = new SolidBrush(Color.FromArgb(255, 240, 199)); g.FillRectangle(crumb, 8, 11, 16, 15);
        g.FillEllipse(Brushes.Black, 11, 15, 3, 4); g.FillEllipse(Brushes.Black, 19, 15, 3, 4);
        if (state != "watching")
        {
            g.FillEllipse(state == "found" ? Brushes.Firebrick : Brushes.DimGray, 21, 21, 11, 11);
            using var pen = new Pen(Color.White, 2);
            g.DrawLine(pen, 26, 23, 26, state == "paused" ? 29 : 27);
            if (state == "paused") g.DrawLine(pen, 29, 23, 29, 29); else g.FillEllipse(Brushes.White, 25, 29, 2, 2);
        }
        nint handle = bitmap.GetHicon();
        try { using var borrowed = Icon.FromHandle(handle); return (Icon)borrowed.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
