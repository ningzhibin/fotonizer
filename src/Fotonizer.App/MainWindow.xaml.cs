using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Fotonizer.App.ViewModels;
using Fotonizer.Core;
using Fotonizer.Core.Logging;
using Microsoft.Win32;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace Fotonizer.App;

public partial class MainWindow : Window
{
    private readonly AppHost _host;
    private readonly MainViewModel _viewModel;
    private bool _allowClose;
    private bool _initializing = true;
    private bool _trayHintShown;

    public MainViewModel ViewModel => _viewModel;

    public MainWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Loc.Apply(this);

        _viewModel = new MainViewModel(host);
        DataContext = _viewModel;

        GoogleSecretBox.Password = _viewModel.Settings.GoogleClientSecret;
        _initializing = false;

        Loaded += (_, _) =>
        {
            NavList.SelectedIndex = 0;
            _viewModel.RefreshStats();
        };
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void PrepareForExit()
    {
        _allowClose = true;
    }

    public void RefreshAfterSetup()
    {
        _viewModel.Settings.NotifyDestinationChanged();
        _viewModel.RefreshStats();
        _viewModel.RefreshGoogleStatus();
    }

    public void FlushSettings()
    {
        _viewModel.Settings.Flush();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_allowClose)
        {
            e.Cancel = true;
            if (_host.SettingsStore.Current.MinimizeToTray)
            {
                Hide();
                if (!_trayHintShown && _host.SettingsStore.Current.ShowNotifications)
                {
                    _trayHintShown = true;
                    AppLog.Shared.Info("窗口已最小化到托盘，后台监控继续运行。");
                }
            }
            else
            {
                _ = ((App)Application.Current).ExitApplicationAsync();
            }

            return;
        }

        base.OnClosing(e);
    }

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HomePanel == null) return;

        var panels = new FrameworkElement[]
        {
            HomePanel, SourcesPanel, LibraryPanel, EnhancePanel, BurstPanel, UploadPanel, GeneralPanel,
            ActivityPanel
        };

        var index = Math.Clamp(NavList.SelectedIndex, 0, panels.Length - 1);
        for (var i = 0; i < panels.Length; i++)
            panels[i].Visibility = i == index ? Visibility.Visible : Visibility.Collapsed;

        ContentScroll.ScrollToTop();
    }

    private void Pause_Click(object sender, RoutedEventArgs e) => _viewModel.TogglePause();

    private async void ImportFolder_Click(object sender, RoutedEventArgs e) =>
        await ImportFolderDialogAsync();

    public async Task ImportFolderDialogAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("选择要导入的文件夹", "Choose a folder to import"),
            Multiselect = false
        };

        if (dialog.ShowDialog(this) == true)
            await RunImportAsync(dialog.FolderName);
    }

    private async Task RunImportAsync(string folder)
    {
        try
        {
            var summary = await _viewModel.ImportFolderAsync(folder);
            if (!summary.Success)
            {
                MessageBox.Show(this, $"{(Loc.IsEnglish ? "Import failed: " : "导入失败：")}{summary.Error}", "Fotonizer",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            else if (summary.Imported == 0 && summary.Duplicates > 0)
            {
                MessageBox.Show(this, Loc.T("这些照片之前都已经导入过了。", "These photos have already been imported."),
                    "Fotonizer", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            AppLog.Shared.Error("手动导入失败", ex);
            MessageBox.Show(this, $"{(Loc.IsEnglish ? "Import failed: " : "导入失败：")}{ex.Message}", "Fotonizer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void BrowseDestination_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("选择照片库目录", "Choose a photo library folder"),
            InitialDirectory = Directory.Exists(_viewModel.Settings.DestinationRoot)
                ? _viewModel.Settings.DestinationRoot
                : ""
        };

        if (dialog.ShowDialog(this) != true) return;
        _viewModel.Settings.DestinationRoot = dialog.FolderName;
        _viewModel.Settings.SaveNow();
        _viewModel.RefreshStats();
    }

    private void PresetDate_Click(object sender, RoutedEventArgs e) =>
        _viewModel.Settings.ApplyFileNamePreset("{yyyy}-{MM}-{dd}-{HH}{mm}{ss}");

    private void PresetDateCamera_Click(object sender, RoutedEventArgs e) =>
        _viewModel.Settings.ApplyFileNamePreset("{yyyy}-{MM}-{dd}-{HH}{mm}{ss}-{camera}");

    private void PresetOriginal_Click(object sender, RoutedEventArgs e) =>
        _viewModel.Settings.ApplyFileNamePreset("{original}");

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = Loc.T("选择要监视的文件夹", "Choose a folder to watch") };
        if (dialog.ShowDialog(this) == true)
            _viewModel.AddExtraFolder(dialog.FolderName);
    }

    private void RemoveFolder_Click(object sender, RoutedEventArgs e)
    {
        if (FolderList.SelectedItem is string folder)
            _viewModel.RemoveExtraFolder(folder);
    }

    private void OpenLibrary_Click(object sender, RoutedEventArgs e) => _viewModel.OpenLibrary();

    private void OpenData_Click(object sender, RoutedEventArgs e) => _viewModel.OpenDataFolder();

    private void OpenLogFile_Click(object sender, RoutedEventArgs e) => _viewModel.OpenLogFile();

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _viewModel.ClearActivity();

    private void GoLibrarySettings_Click(object sender, RoutedEventArgs e) => NavList.SelectedIndex = 2;

    private void GoUploadSettings_Click(object sender, RoutedEventArgs e) => NavList.SelectedIndex = 5;

    private void GoogleSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _viewModel.Settings.GoogleClientSecret = GoogleSecretBox.Password;
    }

    private async void ConnectGoogle_Click(object sender, RoutedEventArgs e)
    {
        _viewModel.Settings.GoogleClientSecret = GoogleSecretBox.Password;
        ConnectGoogleButton.IsEnabled = false;
        try
        {
            await _viewModel.ConnectGoogleAsync();
            MessageBox.Show(this,
                Loc.T("授权成功！之后导入的新照片会自动上传到 Google 照片。",
                    "Authorization successful. New photos will now be uploaded to Google Photos automatically."),
                "Fotonizer", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            AppLog.Shared.Warn("Google 授权失败：" + ex.Message);
            MessageBox.Show(this, $"{(Loc.IsEnglish ? "Connection failed: " : "连接失败：")}{ex.Message}", "Fotonizer",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ConnectGoogleButton.IsEnabled = true;
        }
    }

    private void DisconnectGoogle_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this,
                Loc.T("确定要断开 Google 照片连接吗？", "Disconnect from Google Photos?"), "Fotonizer",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _viewModel.DisconnectGoogle();
    }

    private void GoogleGuide_Click(object sender, RoutedEventArgs e)
    {
        var guide = new GoogleSetupWindow { Owner = this };
        guide.ShowDialog();
    }

    private void RetryUploads_Click(object sender, RoutedEventArgs e)
    {
        var count = _viewModel.RetryFailedUploads();
        MessageBox.Show(this,
            count > 0
                ? string.Format(Loc.T("已重新排队 {0} 个失败的上传任务。",
                    "Re-queued {0} failed uploads."), count)
                : Loc.T("没有失败的上传任务。", "There are no failed uploads."),
            "Fotonizer", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
