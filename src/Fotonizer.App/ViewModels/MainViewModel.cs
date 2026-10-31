using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Fotonizer.App.Converters;
using Fotonizer.Core;
using Fotonizer.Core.Import;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Models;
using Application = System.Windows.Application;

namespace Fotonizer.App.ViewModels;

public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly AppHost _host;
    private readonly DispatcherTimer _statsTimer;
    private int _statsTotal;
    private int _statsPending;
    private int _statsUploaded;
    private int _statsFailed;
    private string _statsSize = "0 MB";
    private string _lastImportText = "还没有导入记录";
    private string _progressText = "";
    private string _uploadText = "";
    private bool _isBusy;
    private bool _isPaused;
    private bool _uploadActive;

    public SettingsViewModel Settings { get; }
    public ObservableCollection<LogEntry> Activity { get; } = new();
    public ObservableCollection<string> ExtraFolders { get; } = new();

    public MainViewModel(AppHost host)
    {
        _host = host;
        Settings = new SettingsViewModel(host);
        Settings.SettingsSaved += (_, _) => RefreshGoogleStatus();

        foreach (var folder in host.SettingsStore.Current.ExtraFolders)
            ExtraFolders.Add(folder);

        _isPaused = host.SettingsStore.Current.Paused;
        RefreshStats();
        RefreshGoogleStatus();

        _statsTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(3)
        };
        _statsTimer.Tick += (_, _) => RefreshStats();
        _statsTimer.Start();

        AppLog.Shared.EntryAdded += OnLogEntry;
        host.Imports.ImportStarted += OnImportStarted;
        host.Imports.ProgressChanged += OnImportProgress;
        host.Imports.ImportCompleted += OnImportCompleted;
        host.Uploads.StatusChanged += OnUploadStatus;
        Loc.LanguageChanged += OnLanguageChanged;

        foreach (var entry in AppLog.Shared.Snapshot().TakeLast(80))
            Activity.Add(entry);
    }

    private void OnLanguageChanged(object? sender, EventArgs e) => Dispatch(() =>
    {
        RefreshStats();
        Settings.RefreshLocalization();
        Activity.Clear();
        foreach (var entry in AppLog.Shared.Snapshot().TakeLast(80))
            Activity.Add(entry);
        OnPropertyChanged(nameof(StatusHeadline));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(PauseButtonText));
        OnPropertyChanged(nameof(GoogleStatusText));
        OnPropertyChanged(nameof(GoogleStatusDetail));
        OnPropertyChanged(nameof(LastImportText));
    });

    // ---- status --------------------------------------------------------
    public string StatusHeadline => _isPaused
        ? Loc.T("监控已暂停", "Monitoring paused")
        : _isBusy
            ? Loc.T("正在导入照片…", "Importing photos…")
            : _uploadActive
                ? Loc.T("正在上传到 Google 照片…", "Uploading to Google Photos…")
                : Loc.T("正在后台监控", "Monitoring in the background");

    public string StatusDetail => _isPaused
        ? Loc.T("自动导入已暂停。点击“恢复监控”继续。",
            "Auto import is paused. Click “Resume monitoring” to continue.")
        : _isBusy
            ? _progressText
            : _uploadActive && !string.IsNullOrWhiteSpace(_uploadText)
                ? _uploadText
                : string.Format(
                    Loc.T("插入 U 盘、存储卡或连接相机后会自动导入到 {0}",
                        "Insert a USB drive, memory card, or camera to import automatically to {0}"),
                    ShortDestination);

    private string ShortDestination
    {
        get
        {
            var path = _host.SettingsStore.Current.DestinationRoot;
            return string.IsNullOrWhiteSpace(path) ? Loc.T("照片库", "photo library") : path;
        }
    }

    public string PauseButtonText => _isPaused
        ? Loc.T("恢复监控", "Resume monitoring")
        : Loc.T("暂停监控", "Pause monitoring");

    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (!SetProperty(ref _isPaused, value)) return;
            OnPropertyChanged(nameof(StatusHeadline));
            OnPropertyChanged(nameof(StatusDetail));
            OnPropertyChanged(nameof(PauseButtonText));
        }
    }

    public int StatsTotal
    {
        get => _statsTotal;
        private set => SetProperty(ref _statsTotal, value);
    }

    public int StatsPending
    {
        get => _statsPending;
        private set => SetProperty(ref _statsPending, value);
    }

    public int StatsUploaded
    {
        get => _statsUploaded;
        private set => SetProperty(ref _statsUploaded, value);
    }

    public int StatsFailed
    {
        get => _statsFailed;
        private set => SetProperty(ref _statsFailed, value);
    }

    public string StatsSize
    {
        get => _statsSize;
        private set => SetProperty(ref _statsSize, value);
    }

    public string LastImportText
    {
        get => _lastImportText;
        private set => SetProperty(ref _lastImportText, value);
    }

    public string GoogleStatusText => _host.Google.IsConfigured
        ? (_host.Google.NeedsReauthorization
            ? Loc.T("授权已过期，请重新连接", "Authorization expired. Please reconnect.")
            : Loc.T("已连接 Google 照片", "Connected to Google Photos"))
        : Loc.T("未连接", "Not connected");

    public string GoogleStatusDetail => _host.Google.IsConfigured
        ? Loc.T("新导入的照片会自动上传到你的 Google 照片时间线。",
            "New photos will be uploaded automatically to your Google Photos timeline.")
        : Loc.T("填写 OAuth 客户端信息并连接后即可自动上传。",
            "Enter your OAuth client details and connect to enable automatic upload.");

    public bool GoogleConnected => _host.Google.IsConfigured;

    public string DataFolder => _host.DataDirectory;

    public string VersionText => "Fotonizer 1.0.5";

    public string DestinationRoot => _host.SettingsStore.Current.DestinationRoot;

    // ---- actions -------------------------------------------------------
    public void TogglePause()
    {
        var model = _host.SettingsStore.Current;
        model.Paused = !model.Paused;
        _host.SettingsStore.Save();
        IsPaused = model.Paused;
        AppLog.Shared.Info(model.Paused ? "已暂停自动导入。" : "已恢复自动导入。");
        if (!model.Paused) _host.Uploads.Kick();
    }

    public async Task<ImportSummary> ImportFolderAsync(string path)
    {
        var request = new ImportRequest
        {
            Kind = ImportSourceKind.Folder,
            SourcePath = path,
            DisplayName = path,
            Automatic = false
        };

        _isBusy = true;
        _progressText = Loc.T("准备导入…", "Preparing to import…");
        OnPropertyChanged(nameof(StatusHeadline));
        OnPropertyChanged(nameof(StatusDetail));
        try
        {
            return await _host.Imports.RunNowAsync(request).ConfigureAwait(true);
        }
        finally
        {
            _isBusy = false;
            RefreshStats();
            OnPropertyChanged(nameof(StatusHeadline));
            OnPropertyChanged(nameof(StatusDetail));
        }
    }

    public async Task ConnectGoogleAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.GoogleClientId))
            throw new InvalidOperationException(
                Loc.T("请先填写 Google OAuth 客户端 ID。", "Please enter a Google OAuth client ID first."));

        await _host.Google.AuthorizeInteractiveAsync(Settings.GoogleClientId, Settings.GoogleClientSecret,
            CancellationToken.None).ConfigureAwait(true);
        Settings.SaveNow();
        RefreshGoogleStatus();
        AppLog.Shared.Info("Google 照片已连接。");
    }

    public void DisconnectGoogle()
    {
        _host.Google.SignOut();
        RefreshGoogleStatus();
        AppLog.Shared.Info("已断开 Google 照片连接。");
    }

    public int RetryFailedUploads()
    {
        var failed = _host.Database.GetStats().FailedUpload;
        if (failed == 0) return 0;

        _host.Database.ResetFailedUploads();
        _host.Uploads.Kick();
        RefreshStats();
        AppLog.Shared.Info($"已重新排队 {failed} 个失败的上传任务。");
        return failed;
    }

    public void OpenLibrary()
    {
        var path = _host.SettingsStore.Current.DestinationRoot;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Shared.Warn($"打开照片库失败：{ex.Message}");
        }
    }

    public void OpenDataFolder()
    {
        try
        {
            Directory.CreateDirectory(_host.DataDirectory);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{_host.DataDirectory}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            AppLog.Shared.Warn($"打开数据目录失败：{ex.Message}");
        }
    }

    public void OpenLogFile()
    {
        try
        {
            var logDir = Path.Combine(_host.DataDirectory, "logs");
            var latest = Directory.Exists(logDir)
                ? Directory.GetFiles(logDir, "fotonizer-*.log").OrderByDescending(File.GetLastWriteTime).FirstOrDefault()
                : null;
            if (latest != null)
                Process.Start(new ProcessStartInfo(latest) { UseShellExecute = true });
            else
                OpenDataFolder();
        }
        catch (Exception ex)
        {
            AppLog.Shared.Warn($"打开日志失败：{ex.Message}");
        }
    }

    public void AddExtraFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        var model = _host.SettingsStore.Current;
        if (model.ExtraFolders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase))) return;
        model.ExtraFolders.Add(folder);
        ExtraFolders.Add(folder);
        Settings.SaveNow();
    }

    public void RemoveExtraFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        var model = _host.SettingsStore.Current;
        model.ExtraFolders.RemoveAll(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase));
        ExtraFolders.Remove(folder);
        Settings.SaveNow();
    }

    public void ClearActivity()
    {
        Activity.Clear();
        AppLog.Shared.Clear();
    }

    public void RefreshStats()
    {
        try
        {
            var stats = _host.Database.GetStats();
            StatsTotal = stats.TotalFiles;
            StatsPending = stats.PendingUpload;
            StatsUploaded = stats.Uploaded;
            StatsFailed = stats.FailedUpload;
            StatsSize = FormatSize(stats.TotalBytes);
            LastImportText = stats.LastImportedAt is { } last
                ? Loc.T("最近导入：", "Last import: ") + last.ToString("yyyy-MM-dd HH:mm")
                : Loc.T("还没有导入记录", "No imports yet");
        }
        catch (Exception ex)
        {
            AppLog.Shared.Debug($"刷新统计失败：{ex.Message}");
        }
    }

    public void RefreshGoogleStatus()
    {
        OnPropertyChanged(nameof(GoogleStatusText));
        OnPropertyChanged(nameof(GoogleStatusDetail));
        OnPropertyChanged(nameof(GoogleConnected));
        OnPropertyChanged(nameof(DestinationRoot));
    }

    private void OnImportStarted(object? sender, ImportRequest request)
    {
        _isBusy = true;
        _progressText = string.Format(Loc.T("正在导入 {0}…", "Importing {0}…"), request);
        Dispatch(() =>
        {
            OnPropertyChanged(nameof(StatusHeadline));
            OnPropertyChanged(nameof(StatusDetail));
        });
    }

    private void OnImportProgress(object? sender, ImportProgress progress)
    {
        if (progress.Stage is ImportStage.Importing or ImportStage.Analyzing or ImportStage.Scanning)
        {
            var stage = progress.Stage switch
            {
                ImportStage.Scanning => Loc.T("扫描", "Scanning"),
                ImportStage.Analyzing => Loc.T("分析", "Analyzing"),
                ImportStage.Importing => Loc.T("导入", "Importing"),
                _ => progress.Stage.ToString()
            };
            var file = string.IsNullOrWhiteSpace(progress.CurrentFile) ? "" : $" · {progress.CurrentFile}";
            _progressText = $"{stage} {progress.Current}/{progress.Total}{file}";
        }
        else
        {
            _progressText = LogTranslator.Translate(progress.Message);
        }

        Dispatch(() => OnPropertyChanged(nameof(StatusDetail)));
    }

    private void OnImportCompleted(object? sender, ImportSummary summary)
    {
        _isBusy = false;
        Dispatch(() =>
        {
            RefreshStats();
            OnPropertyChanged(nameof(StatusHeadline));
            OnPropertyChanged(nameof(StatusDetail));
        });
    }

    private void OnUploadStatus(object? sender, Core.Upload.UploadStatus status)
    {
        Dispatch(() =>
        {
            RefreshStats();
            _uploadText = LocalizeUploadStatus(status.Message);
            _uploadActive = _uploadText.StartsWith("Uploading", StringComparison.OrdinalIgnoreCase) ||
                            _uploadText.StartsWith("正在上传", StringComparison.Ordinal);
            OnPropertyChanged(nameof(StatusHeadline));
            OnPropertyChanged(nameof(StatusDetail));
        });
    }

    private static string LocalizeUploadStatus(string message)
    {
        if (!Loc.IsEnglish) return message;

        if (message.StartsWith("文件缺失：", StringComparison.Ordinal))
            return "File missing: " + message["文件缺失：".Length..];
        if (message.StartsWith("正在上传 ", StringComparison.Ordinal))
            return "Uploading " +
                   message["正在上传 ".Length..].Replace("（", " (").Replace("）", ")");
        if (message.StartsWith("已上传到 Google 照片：", StringComparison.Ordinal))
            return "Uploaded to Google Photos: " + message["已上传到 Google 照片：".Length..];
        if (message.StartsWith("已上传 ", StringComparison.Ordinal))
            return "Uploaded " + message["已上传 ".Length..];
        if (message.StartsWith("上传超时：", StringComparison.Ordinal))
            return "Upload timed out: " + message["上传超时：".Length..];
        if (message.StartsWith("上传失败：", StringComparison.Ordinal))
            return "Upload failed: " + message["上传失败：".Length..];
        if (message == "Google 授权失效，请重新连接")
            return "Google authorization expired. Please reconnect.";
        if (message == "Google 限流，稍后自动重试")
            return "Google rate limit reached. Retrying automatically.";

        return message;
    }

    private void OnLogEntry(object? sender, LogEntry entry)
    {
        Dispatch(() =>
        {
            Activity.Add(entry);
            while (Activity.Count > 400) Activity.RemoveAt(0);
        });
    }

    private static void Dispatch(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.BeginInvoke(action);
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 MB";
        var mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024 ? $"{mb / 1024:F1} GB" : $"{mb:F0} MB";
    }

    public void Dispose()
    {
        _statsTimer.Stop();
        AppLog.Shared.EntryAdded -= OnLogEntry;
        _host.Imports.ImportStarted -= OnImportStarted;
        _host.Imports.ProgressChanged -= OnImportProgress;
        _host.Imports.ImportCompleted -= OnImportCompleted;
        _host.Uploads.StatusChanged -= OnUploadStatus;
        Settings.Flush();
    }
}
