using System.Drawing;
using System.Drawing.Drawing2D;
using Forms = System.Windows.Forms;

namespace OpenPedalHub;

/// <summary>Notification-area icon, so the app can keep feeding the game with its window out of the way.</summary>
public sealed class TrayIcon : IDisposable
{
    readonly Forms.NotifyIcon _icon;
    bool _hinted;

    public TrayIcon(Action open, Action exit)
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open OpenPedalHub", null, (_, _) => open());
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new Forms.NotifyIcon { Icon = MakeIcon(), Text = "OpenPedalHub", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) open(); };
    }

    /// <summary>Hover text; Windows allows at most 63 characters.</summary>
    public string Tooltip { set => _icon.Text = value.Length > 63 ? value[..63] : value; }

    /// <summary>Shown the first time the window goes to the tray, so it is clear the app is still running.</summary>
    public void HintOnce()
    {
        if (_hinted) return;
        _hinted = true;
        _icon.ShowBalloonTip(4000, "OpenPedalHub is still running",
            "Your pedals keep working. Click the tray icon to open it again.", Forms.ToolTipIcon.None);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    // The tray shows the app's own icon (app.ico, built into the program), at the size Windows wants for the tray.
    static Icon MakeIcon()
    {
        try
        {
            var res = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/app.ico"));
            if (res != null)
                using (res.Stream)
                    return new Icon(res.Stream, Forms.SystemInformation.SmallIconSize);
        }
        catch { /* fall back to the drawn emblem below */ }
        return DrawEmblem();
    }

    // The same emblem, drawn: a silver orb with a green ring. Only used if app.ico cannot be read.
    static Icon DrawEmblem()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            var body = new Rectangle(1, 1, 30, 30);
            using (var fill = new LinearGradientBrush(body, Color.White, Color.FromArgb(0xB4, 0xBC, 0xC3), 90f))
                g.FillEllipse(fill, body);
            using (var edge = new Pen(Color.FromArgb(0x7C, 0x86, 0x8E), 1.2f))
                g.DrawEllipse(edge, body);
            using (var ring = new Pen(Color.FromArgb(0x5A, 0xB0, 0x18), 5f))
                g.DrawEllipse(ring, 8, 8, 16, 16);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
