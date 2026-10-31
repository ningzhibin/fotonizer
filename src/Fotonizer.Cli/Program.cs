using System.IO;
using System.Text;
using Fotonizer.Core;
using Fotonizer.Core.Devices;
using Fotonizer.Core.Import;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Media;
using Fotonizer.Core.Models;

namespace Fotonizer.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintHelp();
            return 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "import" => await ImportAsync(args).ConfigureAwait(false),
                "devices" => Devices(),
                "scan" => Scan(args),
                "inspect" => Inspect(args),
                "version" => Version(),
                _ => Unknown(args[0])
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("已取消。");
            return 130;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("错误：" + ex.Message);
            return 1;
        }
    }

    private static async Task<int> ImportAsync(string[] args)
    {
        var source = Arg(args, "--source") ?? Arg(args, "-s");
        if (string.IsNullOrWhiteSpace(source))
        {
            Console.Error.WriteLine("缺少 --source <目录>");
            return 1;
        }

        source = Path.GetFullPath(Environment.ExpandEnvironmentVariables(source));
        if (!Directory.Exists(source))
        {
            Console.Error.WriteLine($"目录不存在：{source}");
            return 1;
        }

        var dataDirectory = Arg(args, "--data");
        var hostOptions = new AppHostOptions
        {
            DataDirectory = dataDirectory,
            SettingsPath = dataDirectory == null ? null : Path.Combine(dataDirectory, "settings.json"),
            DatabasePath = dataDirectory == null ? null : Path.Combine(dataDirectory, "library.db"),
            SecretsPath = dataDirectory == null ? null : Path.Combine(dataDirectory, "secrets.bin"),
            EnableFileLog = true
        };

        await using var host = new AppHost(hostOptions);
        var settings = host.SettingsStore.Current;

        var dest = Arg(args, "--dest");
        if (!string.IsNullOrWhiteSpace(dest))
            settings.DestinationRoot = Path.GetFullPath(Environment.ExpandEnvironmentVariables(dest));

        if (Arg(args, "--enhance") is { } enhanceArg)
        {
            settings.EnhanceMode = enhanceArg.ToLowerInvariant() switch
            {
                "off" or "none" or "0" => EnhanceMode.Off,
                "strong" or "2" => EnhanceMode.Strong,
                _ => EnhanceMode.Subtle
            };
        }

        if (HasFlag(args, "--no-burst")) settings.BurstSelection = false;
        if (HasFlag(args, "--keep-burst")) settings.BurstKeepMode = BurstKeepMode.KeepInSubfolder;
        if (HasFlag(args, "--no-videos")) settings.CopyVideos = false;
        if (HasFlag(args, "--delete-source")) settings.DeleteSourceAfterCopy = true;
        if (HasFlag(args, "--no-enhance")) settings.EnhanceMode = EnhanceMode.Off;
        settings.Paused = false;

        host.SettingsStore.Save();

        Console.WriteLine($"来源  : {source}");
        Console.WriteLine($"照片库: {settings.DestinationRoot}");
        Console.WriteLine($"增强  : {settings.EnhanceMode}，连拍优选: {(settings.BurstSelection ? "开" : "关")}");
        Console.WriteLine();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        var request = new ImportRequest
        {
            Kind = ImportSourceKind.Folder,
            SourcePath = source,
            DisplayName = source,
            Automatic = false
        };

        var lastStage = ImportStage.Preparing;
        var lastReport = DateTime.MinValue;
        var progress = new Progress<ImportProgress>(p =>
        {
            if (p.Stage != lastStage || DateTime.Now - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastStage = p.Stage;
                lastReport = DateTime.Now;
                var file = string.IsNullOrWhiteSpace(p.CurrentFile) ? "" : " " + p.CurrentFile;
                Console.WriteLine($"[{p.Stage}] {p.Current}/{p.Total}{file} {p.Message}");
            }
        });

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var summary = await host.Imports.RunNowAsync(request, cts.Token).ConfigureAwait(false);
        stopwatch.Stop();

        Console.WriteLine();
        Console.WriteLine("──────── 导入结果 ────────");
        Console.WriteLine($"成功    : {summary.Success}");
        Console.WriteLine($"发现    : {summary.Found}");
        Console.WriteLine($"导入    : {summary.Imported}");
        Console.WriteLine($"重复    : {summary.Duplicates}");
        Console.WriteLine($"连拍组  : {summary.BurstGroups}（跳过 {summary.BurstSkipped} 张）");
        Console.WriteLine($"增强    : {summary.Enhanced}");
        Console.WriteLine($"待上传  : {summary.UploadQueued}");
        Console.WriteLine($"错误    : {summary.Errors}");
        if (summary.Error != null) Console.WriteLine($"错误信息: {summary.Error}");
        Console.WriteLine($"耗时    : {summary.Duration.TotalSeconds:F1}s");

        return summary.Success ? 0 : 2;
    }

    private static int Devices()
    {
        Console.WriteLine("磁盘：");
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                Console.WriteLine($"  {drive.Name,-4} {drive.DriveType,-10} {drive.VolumeLabel}  " +
                                  $"{drive.AvailableFreeSpace / 1024d / 1024d / 1024d:F1} GB 可用");
            }
            catch
            {
            }
        }

        Console.WriteLine();
        Console.WriteLine("相机/手机（MTP）：");
        try
        {
            var devices = PortableDeviceCatalog.GetConnected();
            if (devices.Count == 0) Console.WriteLine("  （未检测到）");
            foreach (var device in devices)
                Console.WriteLine($"  {device.Name}  [{device.Description}]  {device.DeviceId}");
        }
        catch (Exception ex)
        {
            Console.WriteLine("  枚举失败：" + ex.Message);
        }

        return 0;
    }

    private static int Scan(string[] args)
    {
        var source = Arg(args, "--source") ?? ".";
        var settings = new AppSettings();
        settings.Normalize();
        var files = SourceScanner.Scan(Path.GetFullPath(source), settings, null, CancellationToken.None);
        foreach (var file in files.Take(50))
            Console.WriteLine($"{file.Size,12:N0}  {file.Path}");
        Console.WriteLine($"共 {files.Count} 个候选文件。");
        return 0;
    }

    private static int Inspect(string[] args)
    {
        var file = Arg(args, "--file") ?? args.Skip(1).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            Console.Error.WriteLine("用法：fotonizer-cli inspect --file <照片>");
            return 1;
        }

        var meta = MetadataReader.Read(file, MediaTypes.IsVideo(file));
        Console.WriteLine($"文件      : {file}");
        Console.WriteLine($"拍摄时间  : {meta.CapturedAt:yyyy-MM-dd HH:mm:ss.fff} (EXIF: {meta.HasExifDate})");
        Console.WriteLine($"相机      : {meta.Make} / {meta.Model}");
        Console.WriteLine($"方向      : {meta.Orientation}");
        Console.WriteLine($"大小      : {meta.Size:N0} 字节");
        Console.WriteLine();
        foreach (var line in MetadataReader.DumpExif(file))
            Console.WriteLine(line);
        return 0;
    }

    private static int Version()
    {
        Console.WriteLine("Fotonizer CLI 1.0.5");
        Console.WriteLine($"数据目录: {AppPaths.LocalDirectory}");
        return 0;
    }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"未知命令：{command}");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            Fotonizer 命令行工具

            用法:
              fotonizer-cli import --source <目录> [选项]
              fotonizer-cli devices
              fotonizer-cli scan --source <目录>

            import 选项:
              --dest <目录>       覆盖照片库目录
              --data <目录>       使用独立的数据目录（便于测试，不碰现有配置）
              --enhance <模式>    off | subtle | strong
              --no-enhance        关闭自动增强
              --no-burst          关闭连拍优选
              --keep-burst        保留连拍落选照片到 _Burst 子目录
              --no-videos         不导入视频
              --delete-source     导入成功后删除来源文件（危险）

            示例:
              fotonizer-cli import --source E:\DCIM --dest D:\Photos --enhance strong
            """);
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static bool HasFlag(string[] args, string name) =>
        args.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
}
