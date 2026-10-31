using System.IO;
using System.Management;
using Fotonizer.Core.Import;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Models;
using Fotonizer.Core.Services;
using Fotonizer.Core.Settings;
using MediaDevices;

namespace Fotonizer.Core.Devices;

public sealed record DeviceArrivedInfo(string Name, string Detail, bool IsPortableDevice, string Key);

/// <summary>
/// Watches for USB drives, external disks and cameras/phones. Uses WMI volume-change events for
/// instant USB detection and a light polling loop as a reliable fallback (and for MTP devices).
/// </summary>
public sealed class DeviceMonitor : IDisposable
{
    private readonly SettingsStore _settings;
    private readonly PhotoImportService _imports;
    private readonly AppLog _log = AppLog.Shared;
    private readonly SemaphoreSlim _pollSignal = new(0, 1);
    private readonly HashSet<string> _knownDrives = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _knownDevices = new(StringComparer.OrdinalIgnoreCase);
    private ManagementEventWatcher? _watcher;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private DateTime _lastPortableScan = DateTime.MinValue;
    private DateTime _lastExtraScan = DateTime.MinValue;
    private bool _startupComplete;

    public event EventHandler<DeviceArrivedInfo>? DeviceArrived;

    public DeviceMonitor(SettingsStore settings, PhotoImportService imports)
    {
        _settings = settings;
        _imports = imports;
    }

    public void Start()
    {
        if (_loop != null) return;
        _cts = new CancellationTokenSource();
        TryStartWmiWatcher();
        _loop = Task.Run(() => PollLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try
        {
            TryStopWmiWatcher();
        }
        catch
        {
        }

        if (_loop != null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _loop = null;
        _cts.Dispose();
        _cts = null;
    }

    private void TryStartWmiWatcher()
    {
        try
        {
            var scope = new ManagementScope(@"\\.\root\CIMV2");
            var query = new WqlEventQuery("SELECT * FROM Win32_VolumeChangeEvent WHERE EventType = 2");
            _watcher = new ManagementEventWatcher(scope, query);
            _watcher.EventArrived += (_, _) =>
            {
                try
                {
                    if (_pollSignal.CurrentCount == 0) _pollSignal.Release();
                }
                catch (SemaphoreFullException)
                {
                }
            };
            _watcher.Start();
            _log.Debug("WMI 卷变化监听已启动");
        }
        catch (Exception ex)
        {
            _log.Warn($"WMI 设备事件监听不可用，将使用轮询：{ex.Message}");
            _watcher = null;
        }
    }

    private void TryStopWmiWatcher()
    {
        if (_watcher == null) return;
        try
        {
            _watcher.Stop();
            _watcher.Dispose();
        }
        catch
        {
        }
        finally
        {
            _watcher = null;
        }
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var settings = _settings.Current;
                try
                {
                    ScanDrives(settings, cancellationToken);
                    if (DateTime.Now - _lastPortableScan > TimeSpan.FromSeconds(8))
                    {
                        _lastPortableScan = DateTime.Now;
                        ScanPortableDevices(settings, cancellationToken);
                    }

                    ScanExtraFolders(settings, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log.Debug($"设备轮询异常：{ex.Message}");
                }

                _startupComplete = true;

                var delay = TimeSpan.FromSeconds(Math.Clamp(settings.PollSeconds, 2, 60));
                try
                {
                    await _pollSignal.WaitAsync(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ScanDrives(AppSettings settings, CancellationToken cancellationToken)
    {
        var current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (settings.WatchRemovableDrives || settings.WatchFixedDrives)
        {
            foreach (var drive in DriveInfo.GetDrives())
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (!drive.IsReady) continue;
                    var isRemovable = drive.DriveType == DriveType.Removable;
                    var isFixed = drive.DriveType == DriveType.Fixed;
                    if (!isRemovable && !(isFixed && settings.WatchFixedDrives)) continue;
                    if (isFixed && IsSystemDrive(drive.RootDirectory.FullName)) continue;

                    var root = drive.RootDirectory.FullName;
                    var label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                        ? (isRemovable ? "可移动磁盘" : "本地磁盘")
                        : drive.VolumeLabel;
                    current[root] = $"{label} ({drive.Name.TrimEnd('\\')})";
                }
                catch
                {
                    // drive disappeared mid-enumeration
                }
            }
        }

        foreach (var removed in _knownDrives.Where(k => !current.ContainsKey(k)).ToList())
        {
            _knownDrives.Remove(removed);
            _log.Info($"设备已移除：{removed}");
        }

        foreach (var (root, label) in current)
        {
            if (_knownDrives.Contains(root)) continue;
            _knownDrives.Add(root);

            if (!_startupComplete && !settings.ScanConnectedDevicesOnStartup)
            {
                _log.Debug($"启动扫描已关闭，忽略已连接的 {label}");
                continue;
            }

            RaiseArrived(new DeviceArrivedInfo(label, root, false, "drive:" + root));

            if (settings.AutoImport)
            {
                _imports.Enqueue(new ImportRequest
                {
                    Kind = ImportSourceKind.Drive,
                    SourcePath = root,
                    DisplayName = label,
                    Automatic = true
                });
            }
        }
    }

    private void ScanPortableDevices(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.WatchPortableDevices) return;

        List<MediaDevice> devices;
        try
        {
            devices = MediaDevice.GetDevices().ToList();
        }
        catch (Exception ex)
        {
            _log.Debug($"枚举相机/手机失败：{ex.Message}");
            return;
        }

        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var device in devices)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = string.IsNullOrWhiteSpace(device.DeviceId) ? device.FriendlyName : device.DeviceId;
                if (string.IsNullOrWhiteSpace(id)) continue;
                seen.Add(id);

                if (_knownDevices.ContainsKey(id)) continue;
                _knownDevices[id] = device.FriendlyName;

                if (!_startupComplete && !settings.ScanConnectedDevicesOnStartup)
                    continue;

                RaiseArrived(new DeviceArrivedInfo(device.FriendlyName,
                    string.IsNullOrWhiteSpace(device.Description) ? "相机/手机" : device.Description,
                    true, "mtp:" + id));

                if (settings.AutoImport)
                {
                    _imports.Enqueue(new ImportRequest
                    {
                        Kind = ImportSourceKind.PortableDevice,
                        SourcePath = device.FriendlyName,
                        DisplayName = device.FriendlyName,
                        DeviceId = id,
                        Automatic = true
                    });
                }
            }

            foreach (var gone in _knownDevices.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                _knownDevices.Remove(gone);
                _log.Info($"相机/手机已断开：{gone}");
            }
        }
        finally
        {
            foreach (var device in devices)
            {
                try
                {
                    device.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private void ScanExtraFolders(AppSettings settings, CancellationToken cancellationToken)
    {
        if (!settings.AutoImport || settings.ExtraFolders.Count == 0) return;

        var due = !_startupComplete ||
                  (settings.RescanExtraFoldersMinutes > 0 &&
                   DateTime.Now - _lastExtraScan > TimeSpan.FromMinutes(settings.RescanExtraFoldersMinutes));

        if (!due) return;
        _lastExtraScan = DateTime.Now;

        foreach (var folder in settings.ExtraFolders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Directory.Exists(folder))
            {
                _log.Debug($"附加目录不存在，跳过：{folder}");
                continue;
            }

            _imports.Enqueue(new ImportRequest
            {
                Kind = ImportSourceKind.Folder,
                SourcePath = folder,
                DisplayName = "文件夹 " + folder,
                Automatic = true
            });
        }
    }

    private static bool IsSystemDrive(string root)
    {
        var systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        return string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase);
    }

    private void RaiseArrived(DeviceArrivedInfo info)
    {
        _log.Info($"检测到新设备：{info.Name} ({info.Detail})");
        try
        {
            DeviceArrived?.Invoke(this, info);
        }
        catch (Exception ex)
        {
            _log.Debug($"DeviceArrived 处理器异常：{ex.Message}");
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _pollSignal.Dispose();
        TryStopWmiWatcher();
    }
}
