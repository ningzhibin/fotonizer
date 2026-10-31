using System.Drawing;
using System.Windows;
using System.Windows.Forms;
using Fotonizer.Core;
using Fotonizer.Core.Models;
using Application = System.Windows.Application;

namespace Fotonizer.App;

/// <summary>System tray icon + context menu + balloon notifications.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly AppHost _host;
    private readonly MainWindow _window;
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _openSettingsItem;
    private readonly ToolStripMenuItem _importFolderItem;
    private readonly ToolStripMenuItem _pauseItem;
    private readonly ToolStripMenuItem _openLibraryItem;
    private readonly ToolStripMenuItem _openLogItem;
    private readonly ToolStripMenuItem _exitItem;
    private Icon? _icon;

    public TrayIcon(AppHost host, MainWindow window)
    {
        _host = host;
        _window = window;

        _menu = new ContextMenuStrip();
        _openSettingsItem = new ToolStripMenuItem("", null, (_, _) => ShowWindow());
        _importFolderItem = new ToolStripMenuItem("", null, (_, _) => ImportFolder());
        _pauseItem = new ToolStripMenuItem("", null, (_, _) => TogglePause());
        _openLibraryItem = new ToolStripMenuItem("", null, (_, _) => _window.ViewModel.OpenLibrary());
        _openLogItem = new ToolStripMenuItem("", null, (_, _) => _window.ViewModel.OpenLogFile());
        _exitItem = new ToolStripMenuItem("", null, (_, _) => Exit());

        _menu.Items.Add(_openSettingsItem);
        _menu.Items.Add(_importFolderItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_pauseItem);
        _menu.Items.Add(_openLibraryItem);
        _menu.Items.Add(_openLogItem);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_exitItem);

        _menu.Opening += (_, _) => UpdatePauseText();

        _icon = LoadIcon();
        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Visible = true,
            Text = "Fotonizer",
            ContextMenuStrip = _menu
        };
        _notifyIcon.DoubleClick += (_, _) => ShowWindow();

        Loc.LanguageChanged += OnLanguageChanged;
        ApplyLanguage();

        _host.Monitor.DeviceArrived += OnDeviceArrived;
        _host.Imports.ImportCompleted += OnImportCompleted;
    }

    private void OnLanguageChanged(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(ApplyLanguage));
    }

    private void ApplyLanguage()
    {
        _openSettingsItem.Text = Loc.T("打开设置", "Open settings");
        _importFolderItem.Text = Loc.T("立即导入文件夹…", "Import folder now…");
        _openLibraryItem.Text = Loc.T("打开照片库", "Open photo library");
        _openLogItem.Text = Loc.T("打开日志", "Open log");
        _exitItem.Text = Loc.T("退出", "Exit");
        UpdatePauseText();

        _notifyIcon.Text = Loc.T("Fotonizer · 自动照片整理", "Fotonizer · Automatic Photo Organizer");
    }

    private void UpdatePauseText()
    {
        _pauseItem.Text = _host.SettingsStore.Current.Paused
            ? Loc.T("恢复监控", "Resume monitoring")
            : Loc.T("暂停监控", "Pause monitoring");
    }

    public void ShowStartedBalloon()
    {
        if (!_host.SettingsStore.Current.ShowNotifications) return;
        ShowBalloon(Loc.T("Fotonizer 正在后台运行", "Fotonizer is running in the background"),
            Loc.T("插入 U 盘或连接相机后会自动整理照片。",
                "Photos will be organized automatically when you plug in a USB drive or connect a camera."),
            ToolTipIcon.Info);
    }

    public void ShowWindow()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _window.ShowFromTray();
        });
    }

    private void ImportFolder()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            _ = _window.ImportFolderDialogAsync();
        });
    }

    private void TogglePause()
    {
        Application.Current.Dispatcher.Invoke(() => _window.ViewModel.TogglePause());
    }

    private void Exit()
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            if (Application.Current is App app)
                _ = app.ExitApplicationAsync();
            else
                Application.Current.Shutdown();
        });
    }

    private void OnDeviceArrived(object? sender, Core.Devices.DeviceArrivedInfo info)
    {
        if (!_host.SettingsStore.Current.ShowNotifications) return;
        var dispatcher = Application.Current?.Dispatcher;
        dispatcher?.BeginInvoke(new Action(() =>
            ShowBalloon(Loc.T("检测到新设备", "New device detected"),
                string.Format(Loc.T("{0}，正在准备导入…", "{0} — preparing to import…"), info.Name),
                ToolTipIcon.Info)));
    }

    private void OnImportCompleted(object? sender, ImportSummary summary)
    {
        if (!_host.SettingsStore.Current.ShowNotifications) return;
        if (!summary.Success)
        {
            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
                ShowBalloon(Loc.T("导入失败", "Import failed"),
                    summary.Error ?? Loc.T("请查看日志。", "Please check the log."),
                    ToolTipIcon.Warning)));
            return;
        }

        if (summary.Imported == 0 && summary.Duplicates == 0) return;

        var message = summary.Imported > 0
            ? string.Format(Loc.T("新增 {0} 张照片", "Added {0} photos"), summary.Imported)
            : Loc.T("没有新照片（都已导入过）", "No new photos (already imported)");
        if (summary.BurstSkipped > 0)
            message += string.Format(Loc.T("，连拍优选跳过 {0} 张", " · burst selection skipped {0}"),
                summary.BurstSkipped);
        if (summary.Errors > 0)
            message += string.Format(Loc.T("，{0} 个失败", " · {0} failed"), summary.Errors);

        var icon = summary.Errors > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info;
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            ShowBalloon(Loc.T("照片导入完成", "Import complete"), message, icon)));
    }

    private void ShowBalloon(string title, string text, ToolTipIcon icon)
    {
        try
        {
            _notifyIcon.ShowBalloonTip(4500, title, text, icon);
        }
        catch
        {
        }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/fotonizer.ico"))?.Stream;
            if (stream != null) return new Icon(stream);
        }
        catch
        {
        }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        Loc.LanguageChanged -= OnLanguageChanged;
        _host.Monitor.DeviceArrived -= OnDeviceArrived;
        _host.Imports.ImportCompleted -= OnImportCompleted;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _menu.Dispose();
        _icon?.Dispose();
    }
}
