using System.Drawing;
using System.Windows.Forms;

namespace Ultiviewer.App.Services;

/// <summary>The notification-area icon shown while Ultiviewer keeps running with its window closed.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Action open, Action quickNote, Action today, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Ultiviewer", null, (_, _) => open());
        menu.Items.Add("Quick note…", null, (_, _) => quickNote());
        menu.Items.Add("Today's note", null, (_, _) => today());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new NotifyIcon
        {
            Text = Core.AppInfo.Name,
            Icon = Environment.ProcessPath is { } exe ? Icon.ExtractAssociatedIcon(exe) : SystemIcons.Application,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                open();
            }
        };
    }

    public void ShowMessage(string text) => _icon.ShowBalloonTip(3000, Core.AppInfo.Name, text, ToolTipIcon.None);

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
