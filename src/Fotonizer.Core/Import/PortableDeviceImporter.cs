using System.IO;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Media;
using Fotonizer.Core.Models;
using MediaDevices;

namespace Fotonizer.Core.Import;

public sealed class PortableImportResult
{
    public List<SourceFile> Files { get; } = new();
    public string? TempDirectory { get; set; }
    public int SkippedKnown { get; set; }
}

/// <summary>
/// Downloads new photos from an MTP/PTP camera or phone using the Windows Portable Devices stack.
/// Downloaded files are placed in a temp folder and then flow through the normal import pipeline.
/// </summary>
public sealed class PortableDeviceImporter
{
    private static readonly string[] DefaultFolders = { "DCIM", "Pictures", "Images", "Camera" };

    public Task<PortableImportResult> DownloadAsync(ImportRequest request, AppSettings settings,
        ImportDatabase database, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        return Task.Run(() => DownloadCore(request, settings, database, progress, cancellationToken),
            cancellationToken);
    }

    private PortableImportResult DownloadCore(ImportRequest request, AppSettings settings,
        ImportDatabase database, IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new PortableImportResult();
        var tempDirectory = Path.Combine(Path.GetTempPath(), "Fotonizer", "mtp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDirectory);
        result.TempDirectory = tempDirectory;

        MediaDevice? device = null;
        var allDevices = new List<MediaDevice>();

        try
        {
            allDevices = MediaDevice.GetDevices().ToList();
            device = allDevices.FirstOrDefault(d =>
                          !string.IsNullOrEmpty(request.DeviceId) &&
                          string.Equals(d.DeviceId, request.DeviceId, StringComparison.OrdinalIgnoreCase))
                      ?? allDevices.FirstOrDefault(d =>
                          string.Equals(d.FriendlyName, request.DisplayName, StringComparison.OrdinalIgnoreCase));

            if (device == null)
                throw new InvalidOperationException($"未找到设备“{request.DisplayName}”，请确认相机/手机已解锁并连接。");

            progress?.Report(new ImportProgress(ImportStage.Preparing, 0, 0, null,
                $"正在连接 {device.FriendlyName}…"));

            device.Connect();
            AppLog.Shared.Info($"已连接便携设备：{device.FriendlyName} ({device.Description})");

            var roots = GetStorageRoots(device);
            var directories = new List<string>();
            foreach (var root in roots)
            {
                var found = FindPreferredFolders(device, root);
                if (found.Count > 0) directories.AddRange(found);
                else directories.Add(root);
            }

            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<(string Path, int Depth)>();
            foreach (var dir in directories)
                if (visited.Add(dir)) stack.Push((dir, 0));

            var checkedFiles = 0;
            var downloaded = 0;

            while (stack.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var (directory, depth) = stack.Pop();

                IEnumerable<string> filePaths;
                try
                {
                    filePaths = device.EnumerateFiles(directory).ToList();
                }
                catch (Exception ex)
                {
                    AppLog.Shared.Debug($"无法枚举设备目录 {directory}: {ex.Message}");
                    continue;
                }

                foreach (var filePath in filePaths)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    checkedFiles++;
                    if (!MediaTypes.IsSupported(filePath, settings.CopyVideos)) continue;
                    if (checkedFiles % 20 == 0)
                        progress?.Report(new ImportProgress(ImportStage.Scanning, 0, 0,
                            Path.GetFileName(filePath), $"已检查 {checkedFiles} 个文件…"));

                    MediaFileInfo info;
                    try
                    {
                        info = device.GetFileInfo(filePath);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Shared.Debug($"读取设备文件信息失败 {filePath}: {ex.Message}");
                        continue;
                    }

                    if (info.Length == 0) continue;

                    var key = BuildSourceKey(device.DeviceId, info, filePath);
                    if (database.IsSourceSeen(key))
                    {
                        result.SkippedKnown++;
                        continue;
                    }

                    var extension = Path.GetExtension(filePath);
                    if (string.IsNullOrWhiteSpace(extension)) extension = ".jpg";

                    // Keep the camera's original file name in its own temp folder so that
                    // the {original} naming token still does the right thing.
                    var itemDirectory = Path.Combine(tempDirectory, Guid.NewGuid().ToString("N"));
                    Directory.CreateDirectory(itemDirectory);
                    var originalName = NamingTemplate.SanitizeFileName(Path.GetFileName(filePath));
                    if (!Path.HasExtension(originalName)) originalName += extension;
                    var destination = Path.Combine(itemDirectory, originalName);

                    try
                    {
                        progress?.Report(new ImportProgress(ImportStage.Preparing, 0, 0,
                            Path.GetFileName(filePath), $"正在下载 {Path.GetFileName(filePath)}…"));

                        using (var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
                                   FileShare.None, 1024 * 1024))
                        {
                            device.DownloadFile(filePath, stream);
                        }

                        var size = new FileInfo(destination).Length;
                        if (size == 0)
                        {
                            FileFingerprint.TryDelete(destination);
                            continue;
                        }

                        result.Files.Add(new SourceFile
                        {
                            Path = destination,
                            Size = size,
                            SourceKey = key,
                            IsTemporary = true
                        });
                        downloaded++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        FileFingerprint.TryDelete(destination);
                        AppLog.Shared.Warn($"下载失败 {Path.GetFileName(filePath)}: {ex.Message}");
                    }
                }

                if (depth >= Math.Max(1, settings.MaxScanDepth)) continue;

                IEnumerable<string> children;
                try
                {
                    children = device.EnumerateDirectories(directory).ToList();
                }
                catch
                {
                    continue;
                }

                foreach (var child in children)
                {
                    var name = Path.GetFileName(child);
                    if (string.IsNullOrWhiteSpace(name) || name.StartsWith(".", StringComparison.Ordinal)) continue;
                    if (visited.Add(child)) stack.Push((child, depth + 1));
                }
            }

            AppLog.Shared.Info($"设备扫描完成：下载 {downloaded} 个新文件，跳过 {result.SkippedKnown} 个已导入文件");
            return result;
        }
        catch
        {
            try
            {
                if (Directory.Exists(tempDirectory)) Directory.Delete(tempDirectory, recursive: true);
            }
            catch
            {
            }

            throw;
        }
        finally
        {
            try
            {
                if (device is { IsConnected: true }) device.Disconnect();
            }
            catch
            {
            }

            foreach (var d in allDevices)
            {
                try
                {
                    d.Dispose();
                }
                catch
                {
                }
            }
        }
    }

    private static List<string> GetStorageRoots(MediaDevice device)
    {
        var roots = new List<string>();
        try
        {
            roots.AddRange(device.EnumerateDirectories("\\"));
        }
        catch (Exception ex)
        {
            AppLog.Shared.Debug($"枚举设备根目录失败：{ex.Message}");
        }

        if (roots.Count == 0)
        {
            try
            {
                roots.Add(device.GetRootDirectory().FullName);
            }
            catch (Exception ex)
            {
                AppLog.Shared.Debug($"获取设备根目录失败：{ex.Message}");
            }
        }

        return roots;
    }

    private static List<string> FindPreferredFolders(MediaDevice device, string storageRoot)
    {
        var result = new List<string>();
        IEnumerable<string> children;
        try
        {
            children = device.EnumerateDirectories(storageRoot).ToList();
        }
        catch
        {
            return result;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (DefaultFolders.Any(f => string.Equals(f, name, StringComparison.OrdinalIgnoreCase)))
                result.Add(child);
        }

        return result;
    }

    private static string BuildSourceKey(string deviceId, MediaFileInfo info, string path)
    {
        var identity = !string.IsNullOrWhiteSpace(info.PersistentUniqueId)
            ? info.PersistentUniqueId
            : $"{path}|{info.Length}|{info.DateAuthored:O}";
        return $"mtp:{deviceId}:{identity}";
    }
}
