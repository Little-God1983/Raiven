using Raiven.Core.Config;
using Raiven.Core.Logging;
using Raiven.Core.Summaries;

namespace Raiven.App;

public sealed class TrayContext : ApplicationContext
{
    private const string RepositoryUrl = "https://github.com/Little-God1983/Raiven";

    private readonly NotifyIcon _icon;

    public TrayContext(
        AppState state,
        RaivenConfig config,
        Action saveConfig,
        SummaryHistory history,
        Action<SummaryHistoryEntry> replaySummary,
        Action testToast,
        Action testVoice,
        Action<bool>? onPauseChanged = null,
        Action<bool>? onKeepAudioAliveChanged = null)
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add(CreateHeaderItem(menu));
        menu.Items.Add(new ToolStripSeparator());

        var pauseItem = new ToolStripMenuItem("Pause notifications") { CheckOnClick = true };
        pauseItem.CheckedChanged += (_, _) =>
        {
            state.Paused = pauseItem.Checked;
            onPauseChanged?.Invoke(pauseItem.Checked);
        };

        var startupItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = StartupRegistration.IsEnabled(),
        };
        startupItem.CheckedChanged += (_, _) => StartupRegistration.SetEnabled(startupItem.Checked);

        var recentItem = new ToolStripMenuItem("Recent summaries");
        menu.Opening += (_, _) => RebuildRecentSummaries(recentItem, history, replaySummary);
        RebuildRecentSummaries(recentItem, history, replaySummary);

        var settingsItem = BuildSettingsMenu(config, saveConfig, onKeepAudioAliveChanged);

        menu.Items.Add(pauseItem);
        menu.Items.Add(recentItem);
        menu.Items.Add(settingsItem);
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

    private static void RebuildRecentSummaries(
        ToolStripMenuItem parent, SummaryHistory history, Action<SummaryHistoryEntry> replaySummary)
    {
        parent.DropDownItems.Clear();
        var entries = history.Entries;
        if (entries.Count == 0)
        {
            parent.DropDownItems.Add(new ToolStripMenuItem("(none yet)") { Enabled = false });
            return;
        }

        foreach (var entry in entries)
        {
            var captured = entry;
            // Escape ampersands so headlines with '&' don't render as menu mnemonics.
            var label = captured.MenuLabel.Replace("&", "&&");
            parent.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => replaySummary(captured)));
        }
    }

    private static ToolStripMenuItem BuildSettingsMenu(
        RaivenConfig config, Action saveConfig, Action<bool>? onKeepAudioAliveChanged)
    {
        var settings = new ToolStripMenuItem("Settings");

        var notifyTurns = new ToolStripMenuItem("Notify on finished turns")
        {
            CheckOnClick = true,
            Checked = config.NotifyOnFinishedTurn,
        };
        notifyTurns.CheckedChanged += (_, _) => { config.NotifyOnFinishedTurn = notifyTurns.Checked; saveConfig(); };

        var notifyQuestions = new ToolStripMenuItem("Notify on questions")
        {
            CheckOnClick = true,
            Checked = config.NotifyOnQuestion,
        };
        notifyQuestions.CheckedChanged += (_, _) => { config.NotifyOnQuestion = notifyQuestions.Checked; saveConfig(); };

        var turnVoice = new ToolStripMenuItem("Finished turn voice");
        AddRadioGroup(turnVoice,
            [("Haiku summary", "summary"), ("Read last message", "message")],
            config.FinishedTurnVoice,
            value => { config.FinishedTurnVoice = value; saveConfig(); });

        var questionVoice = new ToolStripMenuItem("Question voice");
        AddRadioGroup(questionVoice,
            [("Announce only", "announce"), ("Read message", "message"), ("Haiku summary", "summary")],
            config.QuestionVoice,
            value => { config.QuestionVoice = value; saveConfig(); });

        settings.DropDownItems.Add(notifyTurns);
        settings.DropDownItems.Add(notifyQuestions);
        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(turnVoice);
        settings.DropDownItems.Add(questionVoice);

        var keepAlive = new ToolStripMenuItem("Keep audio device awake")
        {
            CheckOnClick = true,
            Checked = config.KeepAudioAlive,
        };
        keepAlive.CheckedChanged += (_, _) =>
        {
            config.KeepAudioAlive = keepAlive.Checked;
            saveConfig();
            onKeepAudioAliveChanged?.Invoke(keepAlive.Checked);
        };

        var showStatus = new ToolStripMenuItem("Show playback status")
        {
            CheckOnClick = true,
            Checked = config.ShowPlaybackStatus,
        };
        showStatus.CheckedChanged += (_, _) => { config.ShowPlaybackStatus = showStatus.Checked; saveConfig(); };

        settings.DropDownItems.Add(new ToolStripSeparator());
        settings.DropDownItems.Add(keepAlive);
        settings.DropDownItems.Add(showStatus);
        return settings;
    }

    private static void AddRadioGroup(
        ToolStripMenuItem parent, (string Label, string Value)[] options, string current, Action<string> apply)
    {
        foreach (var (label, value) in options)
        {
            var item = new ToolStripMenuItem(label)
            {
                Checked = string.Equals(current, value, StringComparison.OrdinalIgnoreCase),
            };
            item.Click += (_, _) =>
            {
                foreach (var sibling in parent.DropDownItems.OfType<ToolStripMenuItem>())
                    sibling.Checked = false;
                item.Checked = true;
                apply(value);
            };
            parent.DropDownItems.Add(item);
        }

        // Unknown config value: show the default (first) option as selected.
        if (!parent.DropDownItems.OfType<ToolStripMenuItem>().Any(i => i.Checked))
            ((ToolStripMenuItem)parent.DropDownItems[0]).Checked = true;
    }

    private static ToolStripControlHost CreateHeaderItem(ContextMenuStrip owner)
    {
        const int logoSize = 48;
        const int panelWidth = 200;
        const int panelHeight = 78;

        var panel = new Panel
        {
            Width = panelWidth,
            Height = panelHeight,
            BackColor = Color.Black,
            Cursor = Cursors.Hand,
        };

        var picture = new PictureBox
        {
            Image = LoadHeaderBitmap(logoSize),
            SizeMode = PictureBoxSizeMode.Zoom,
            Size = new Size(logoSize, logoSize),
            Location = new Point((panelWidth - logoSize) / 2, 6),
            Cursor = Cursors.Hand,
            BackColor = Color.Transparent,
        };

        var label = new Label
        {
            Text = "RAIVEN",
            Font = new Font(SystemFonts.MenuFont ?? SystemFonts.DefaultFont, FontStyle.Bold),
            ForeColor = Color.FromArgb(196, 152, 255),
            BackColor = Color.Transparent,
            TextAlign = ContentAlignment.MiddleCenter,
            Dock = DockStyle.Bottom,
            Height = 20,
            Cursor = Cursors.Hand,
        };

        panel.Controls.Add(picture);
        panel.Controls.Add(label);

        void OpenRepository(object? sender, EventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo(RepositoryUrl) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                FileLog.Error("Failed to open RAIVEN repository link", ex);
            }
            owner.Close();
        }

        panel.Click += OpenRepository;
        picture.Click += OpenRepository;
        label.Click += OpenRepository;

        return new ToolStripControlHost(panel)
        {
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            AutoSize = false,
            Size = new Size(panelWidth, panelHeight),
        };
    }

    private static Bitmap? LoadHeaderBitmap(int size)
    {
        var iconPath = GetIconPath();
        if (!File.Exists(iconPath))
            return null;

        try
        {
            using var icon = new Icon(iconPath, new Size(size, size));
            return icon.ToBitmap();
        }
        catch (Exception ex)
        {
            FileLog.Error("Failed to load RAIVEN header image", ex);
            return null;
        }
    }

    private static Icon LoadTrayIcon()
    {
        var iconPath = GetIconPath();
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

    private static string GetIconPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "raiven.ico");
}
