using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Threading;
using Fotonizer.Core;
using Fotonizer.Core.Import;
using Fotonizer.Core.Models;
using Fotonizer.Core.Settings;

namespace Fotonizer.App.ViewModels;

/// <summary>Two-way binding wrapper around <see cref="AppSettings"/> with debounced auto-save.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _saveTimer;

    public AppSettings Model { get; }

    public event EventHandler? SettingsSaved;

    public SettingsViewModel(AppHost host)
    {
        _host = host;
        Model = host.SettingsStore.Current;
        _saveTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(650)
        };
        _saveTimer.Tick += (_, _) => SaveNow();
        Loc.LanguageChanged += (_, _) => DispatchLocalization();
    }

    private void DispatchLocalization()
    {
        OnPropertyChanged(nameof(Language));
        RaisePreview();
    }

    public void RefreshLocalization() => DispatchLocalization();

    public void SaveNow()
    {
        _saveTimer.Stop();
        _host.SettingsStore.Save();
        ApplyStartupRegistration();
        SettingsSaved?.Invoke(this, EventArgs.Empty);
    }

    public void Flush() => SaveNow();

    private void ApplyStartupRegistration()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe)) return;
        if (exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase)) return;

        if (Model.StartWithWindows)
            StartupRegistration.Apply(true, exe);
        else if (StartupRegistration.IsEnabled())
            StartupRegistration.Apply(false, exe);
    }

    private void Set<T>(Func<T> getter, Action<T> setter, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(getter(), value)) return;
        setter(value);
        OnPropertyChanged(propertyName);
        if (propertyName is nameof(FileNameTemplate) or nameof(SubfolderTemplate) or nameof(DestinationRoot)
            or nameof(OrganizeByDevice))
            RaisePreview();
        ScheduleSave();
    }

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void RaisePreview()
    {
        OnPropertyChanged(nameof(FileNamePreview));
        OnPropertyChanged(nameof(FolderPreview));
        OnPropertyChanged(nameof(FolderExample));
        OnPropertyChanged(nameof(TemplateExample));
    }

    // ---- Sources -------------------------------------------------------
    public bool WatchRemovableDrives
    {
        get => Model.WatchRemovableDrives;
        set => Set(() => Model.WatchRemovableDrives, v => Model.WatchRemovableDrives = v, value);
    }

    public bool WatchFixedDrives
    {
        get => Model.WatchFixedDrives;
        set => Set(() => Model.WatchFixedDrives, v => Model.WatchFixedDrives = v, value);
    }

    public bool WatchPortableDevices
    {
        get => Model.WatchPortableDevices;
        set => Set(() => Model.WatchPortableDevices, v => Model.WatchPortableDevices = v, value);
    }

    public bool AutoImport
    {
        get => Model.AutoImport;
        set => Set(() => Model.AutoImport, v => Model.AutoImport = v, value);
    }

    public bool ScanConnectedDevicesOnStartup
    {
        get => Model.ScanConnectedDevicesOnStartup;
        set => Set(() => Model.ScanConnectedDevicesOnStartup, v => Model.ScanConnectedDevicesOnStartup = v, value);
    }

    public bool IncludeSubfolders
    {
        get => Model.IncludeSubfolders;
        set => Set(() => Model.IncludeSubfolders, v => Model.IncludeSubfolders = v, value);
    }

    public int MaxScanDepth
    {
        get => Model.MaxScanDepth;
        set => Set(() => Model.MaxScanDepth, v => Model.MaxScanDepth = Math.Clamp(v, 1, 32), value);
    }

    public bool CopyVideos
    {
        get => Model.CopyVideos;
        set => Set(() => Model.CopyVideos, v => Model.CopyVideos = v, value);
    }

    public int RescanExtraFoldersMinutes
    {
        get => Model.RescanExtraFoldersMinutes;
        set => Set(() => Model.RescanExtraFoldersMinutes, v => Model.RescanExtraFoldersMinutes = Math.Clamp(v, 0, 1440), value);
    }

    public int PollSeconds
    {
        get => Model.PollSeconds;
        set => Set(() => Model.PollSeconds, v => Model.PollSeconds = Math.Clamp(v, 2, 60), value);
    }

    // ---- Library -------------------------------------------------------
    public string DestinationRoot
    {
        get => Model.DestinationRoot;
        set => Set(() => Model.DestinationRoot, v => Model.DestinationRoot = v ?? "", value);
    }

    public string SubfolderTemplate
    {
        get => Model.SubfolderTemplate;
        set => Set(() => Model.SubfolderTemplate, v => Model.SubfolderTemplate = v ?? "", value);
    }

    public string FileNameTemplate
    {
        get => Model.FileNameTemplate;
        set => Set(() => Model.FileNameTemplate, v => Model.FileNameTemplate = v ?? "", value);
    }

    public bool OrganizeByDevice
    {
        get => Model.OrganizeByDevice;
        set => Set(() => Model.OrganizeByDevice, v => Model.OrganizeByDevice = v, value);
    }

    public bool DeleteSourceAfterCopy
    {
        get => Model.DeleteSourceAfterCopy;
        set => Set(() => Model.DeleteSourceAfterCopy, v => Model.DeleteSourceAfterCopy = v, value);
    }

    public string FolderPreview
    {
        get
        {
            var folder = NamingTemplate.RenderRelativeFolder(Model.SubfolderTemplate, PreviewMeta);
            return string.IsNullOrWhiteSpace(folder)
                ? Loc.T("（直接放在照片库根目录）", "(directly in the photo library root)")
                : folder;
        }
    }

    public string FolderExample => Loc.T("示例：", "Example: ") + FolderPreview;

    public string FileNamePreview => NamingTemplate.BuildFileName(Model.FileNameTemplate, PreviewMeta, 1, ".jpg");

    public string TemplateExample
    {
        get
        {
            var parts = new List<string>();
            if (Model.OrganizeByDevice)
                parts.Add(NamingTemplate.BuildDeviceFolder(PreviewMeta,
                    Loc.IsEnglish ? "Unknown camera" : "未识别相机"));

            var folder = NamingTemplate.RenderRelativeFolder(Model.SubfolderTemplate, PreviewMeta);
            if (!string.IsNullOrWhiteSpace(folder))
                parts.Add(folder);

            parts.Add(FileNamePreview);
            return Loc.T("示例：", "Example: ") + Path.Combine(parts.ToArray());
        }
    }

    private static PhotoMetadata PreviewMeta => new()
    {
        SourcePath = "IMG_1234.JPG",
        CapturedAt = DateTime.Now,
        Make = "Canon",
        Model = "EOS R6"
    };

    // ---- Enhancement ---------------------------------------------------
    public int EnhanceModeIndex
    {
        get => (int)Model.EnhanceMode;
        set => Set(() => (int)Model.EnhanceMode, v => Model.EnhanceMode = (EnhanceMode)Math.Clamp(v, 0, 2), value);
    }

    public int JpegQuality
    {
        get => Model.JpegQuality;
        set => Set(() => Model.JpegQuality, v => Model.JpegQuality = Math.Clamp(v, 60, 100), value);
    }

    // ---- Burst ---------------------------------------------------------
    public bool BurstSelection
    {
        get => Model.BurstSelection;
        set => Set(() => Model.BurstSelection, v => Model.BurstSelection = v, value);
    }

    public double BurstWindowSeconds
    {
        get => Model.BurstWindowSeconds;
        set => Set(() => Model.BurstWindowSeconds, v => Model.BurstWindowSeconds = Math.Clamp(v, 0.3, 15), value);
    }

    public int BurstMinCount
    {
        get => Model.BurstMinCount;
        set => Set(() => Model.BurstMinCount, v => Model.BurstMinCount = Math.Clamp(v, 2, 20), value);
    }

    public int BurstKeepIndex
    {
        get => (int)Model.BurstKeepMode;
        set => Set(() => (int)Model.BurstKeepMode, v => Model.BurstKeepMode = (BurstKeepMode)Math.Clamp(v, 0, 1), value);
    }

    // ---- Upload --------------------------------------------------------
    public bool UploadEnabled
    {
        get => Model.UploadEnabled;
        set => Set(() => Model.UploadEnabled, v => Model.UploadEnabled = v, value);
    }

    public string GoogleClientId
    {
        get => Model.GoogleClientId;
        set => Set(() => Model.GoogleClientId, v => Model.GoogleClientId = v ?? "", value);
    }

    public string GoogleClientSecret
    {
        get => _host.Secrets.Credentials.ClientSecret ?? "";
        set
        {
            if (value == (_host.Secrets.Credentials.ClientSecret ?? "")) return;
            _host.Secrets.SetClientSecret(value);
            OnPropertyChanged();
        }
    }

    public string AlbumTemplate
    {
        get => Model.AlbumTemplate;
        set => Set(() => Model.AlbumTemplate, v => Model.AlbumTemplate = v ?? "", value);
    }

    public bool UploadVideos
    {
        get => Model.UploadVideos;
        set => Set(() => Model.UploadVideos, v => Model.UploadVideos = v, value);
    }

    public int UploadRetryLimit
    {
        get => Model.UploadRetryLimit;
        set => Set(() => Model.UploadRetryLimit, v => Model.UploadRetryLimit = Math.Clamp(v, 1, 20), value);
    }

    // ---- General -------------------------------------------------------
    public string Language
    {
        get => Model.Language;
        set
        {
            var normalized = Loc.Normalize(value);
            if (string.Equals(Model.Language, normalized, StringComparison.Ordinal)) return;

            Model.Language = normalized;
            OnPropertyChanged();
            Loc.SetLanguage(normalized);
            SaveNow();
        }
    }

    public bool StartWithWindows
    {
        get => Model.StartWithWindows;
        set => Set(() => Model.StartWithWindows, v => Model.StartWithWindows = v, value);
    }

    public bool MinimizeToTray
    {
        get => Model.MinimizeToTray;
        set => Set(() => Model.MinimizeToTray, v => Model.MinimizeToTray = v, value);
    }

    public bool ShowNotifications
    {
        get => Model.ShowNotifications;
        set => Set(() => Model.ShowNotifications, v => Model.ShowNotifications = v, value);
    }

    public bool PreventSystemSleep
    {
        get => Model.PreventSystemSleep;
        set => Set(() => Model.PreventSystemSleep, v => Model.PreventSystemSleep = v, value);
    }

    public void ApplyFileNamePreset(string template)
    {
        FileNameTemplate = template;
        SaveNow();
    }

    /// <summary>Raises change notifications after code outside this view model modified the settings object.</summary>
    public void NotifyDestinationChanged()
    {
        OnPropertyChanged(nameof(DestinationRoot));
        OnPropertyChanged(nameof(FileNameTemplate));
        OnPropertyChanged(nameof(SubfolderTemplate));
        RaisePreview();
    }
}
