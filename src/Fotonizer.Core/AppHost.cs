using System.IO;
using Fotonizer.Core.Devices;
using Fotonizer.Core.Import;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Models;
using Fotonizer.Core.Power;
using Fotonizer.Core.Services;
using Fotonizer.Core.Settings;
using Fotonizer.Core.Upload;

namespace Fotonizer.Core;

public sealed class AppHostOptions
{
    public string? SettingsPath { get; init; }
    public string? DatabasePath { get; init; }
    public string? SecretsPath { get; init; }
    public string? DataDirectory { get; init; }
    public bool EnableFileLog { get; init; } = true;
}

/// <summary>
/// Owns the long-lived services. The WPF app and the CLI both use this so behaviour stays identical.
/// </summary>
public sealed class AppHost : IAsyncDisposable
{
    private bool _started;

    public SettingsStore SettingsStore { get; }
    public SecretStore Secrets { get; }
    public ImportDatabase Database { get; }
    public GooglePhotosClient Google { get; }
    public UploadService Uploads { get; }
    public PhotoImportService Imports { get; }
    public DeviceMonitor Monitor { get; }
    public SystemSleepGuard SleepGuard { get; }

    public string DataDirectory { get; }

    public AppHost(AppHostOptions? options = null)
    {
        options ??= new AppHostOptions();
        DataDirectory = options.DataDirectory ?? AppPaths.LocalDirectory;
        Directory.CreateDirectory(DataDirectory);

        if (options.EnableFileLog)
        {
            var logPath = Path.Combine(DataDirectory, "logs", $"fotonizer-{DateTime.Now:yyyyMMdd}.log");
            AppLog.Shared.ConfigureFile(logPath);
            AppLog.PruneOldLogs(Path.GetDirectoryName(logPath)!);
        }

        SettingsStore = new SettingsStore(options.SettingsPath);
        Secrets = new SecretStore(options.SecretsPath);
        Database = new ImportDatabase(options.DatabasePath ??
                                      Path.Combine(DataDirectory, "library.db"));
        Google = new GooglePhotosClient(SettingsStore, Secrets);
        Uploads = new UploadService(SettingsStore, Database, Google);
        Imports = new PhotoImportService(SettingsStore, Database);
        Monitor = new DeviceMonitor(SettingsStore, Imports);
        SleepGuard = new SystemSleepGuard();

        SettingsStore.Saved += (_, _) =>
            SleepGuard.SetEnabled(SettingsStore.Current.PreventSystemSleep);

        Imports.ImportCompleted += (_, summary) =>
        {
            if (summary.Success && summary.UploadQueued > 0 && SettingsStore.Current.UploadEnabled)
                Uploads.Kick();
        };
    }

    public Task StartAsync()
    {
        if (_started) return Task.CompletedTask;
        _started = true;

        AppLog.Shared.Info($"Fotonizer 启动，照片库：{SettingsStore.Current.DestinationRoot}");
        Imports.Start();
        Uploads.Start();

        // Do not touch devices until the user has completed the first-run setup.
        if (SettingsStore.Current.HasCompletedSetup)
            Monitor.Start();

        if (SettingsStore.Current.UploadEnabled)
            Uploads.Kick();

        SleepGuard.SetEnabled(SettingsStore.Current.PreventSystemSleep);

        _ = Task.Run(() => Maintenance.CleanupStaleTempFiles(SettingsStore.Current.DestinationRoot));
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_started) return;
        _started = false;

        AppLog.Shared.Info("Fotonizer 正在停止…");
        await Monitor.StopAsync().ConfigureAwait(false);
        await Imports.StopAsync().ConfigureAwait(false);
        await Uploads.StopAsync().ConfigureAwait(false);
        SleepGuard.SetEnabled(false);
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        Monitor.Dispose();
        Imports.Dispose();
        Uploads.Dispose();
        Google.Dispose();
        SleepGuard.Dispose();
    }
}
