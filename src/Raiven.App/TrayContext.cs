using Raiven.Core.Logging;

namespace Raiven.App;

public sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;

    public TrayContext(AppState state, Action testToast, Action testVoice)
    {
        var menu = new ContextMenuStrip();

        var pauseItem = new ToolStripMenuItem("Pause notifications") { CheckOnClick = true };
        pauseItem.CheckedChanged += (_, _) => state.Paused = pauseItem.Checked;

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = StartupRegistration.IsEnabled(),
        };
        startupItem.CheckedChanged += (_, _) => StartupRegistration.SetEnabled(startupItem.Checked);

        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Test notification", null, (_, _) => testToast());
        menu.Items.Add("Test voice", null, (_, _) => testVoice());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(startupItem);
        menu.Items.Add("Open data folder", null, (_, _) =>
            System.Diagnostics.Process.Start("explorer.exe", AppPaths.DataDir));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Text = "RAIVEN",
            Visible = true,
            ContextMenuStrip = menu,
        };
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }

    private static Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "raiven.ico");
        if (File.Exists(iconPath))
        {
            try
            {
                return new Icon(iconPath, SystemInformation.SmallIconSize);
            }
            catch (Exception ex)
            {
                FileLog.Error("Failed to load RAIVEN tray icon; falling back to default", ex);
            }
        }
        return SystemIcons.Application;
    }
}
