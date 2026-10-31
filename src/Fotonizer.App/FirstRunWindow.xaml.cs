using System.IO;
using System.Windows;
using Fotonizer.Core;
using Fotonizer.Core.Logging;
using Microsoft.Win32;
using MessageBox = System.Windows.MessageBox;

namespace Fotonizer.App;

public partial class FirstRunWindow : Window
{
    private readonly AppHost _host;

    public FirstRunWindow(AppHost host)
    {
        _host = host;
        InitializeComponent();
        Loc.Apply(this);
        DestinationBox.Text = host.SettingsStore.Current.DestinationRoot;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = Loc.T("选择照片库位置", "Choose a photo library location"),
            InitialDirectory = Directory.Exists(DestinationBox.Text) ? DestinationBox.Text : ""
        };

        if (dialog.ShowDialog(this) == true)
            DestinationBox.Text = dialog.FolderName;
    }

    private void Start_Click(object sender, RoutedEventArgs e)
    {
        var destination = DestinationBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(destination))
        {
            MessageBox.Show(this, Loc.T("请选择一个照片库目录。", "Please choose a photo library folder."),
                "Fotonizer", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            Directory.CreateDirectory(destination);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                (Loc.IsEnglish ? "Could not create the photo library folder: " : "无法创建照片库目录：") + ex.Message,
                "Fotonizer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var settings = _host.SettingsStore.Current;
        settings.DestinationRoot = destination;
        settings.ScanConnectedDevicesOnStartup = StartupScanCheck.IsChecked == true;
        settings.HasCompletedSetup = true;
        settings.Paused = false;
        _host.SettingsStore.Save();
        AppLog.Shared.Info($"首次设置完成，照片库：{destination}");

        DialogResult = true;
        Close();
    }
}
