using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Data;
using Fotonizer.Core.Logging;

namespace Fotonizer.App.Converters;

public sealed class LogLevelTextConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is LogLevel level
            ? level switch
            {
                LogLevel.Debug => Loc.T("调试", "Debug"),
                LogLevel.Info => Loc.T("信息", "Info"),
                LogLevel.Warn => Loc.T("警告", "Warning"),
                LogLevel.Error => Loc.T("错误", "Error"),
                _ => level.ToString()
            }
            : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class LogMessageConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is string message ? LogTranslator.Translate(message) : value;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Best-effort English rendering of the technical log messages produced by Core.
/// Unknown messages fall back to the original Chinese text.
/// </summary>
public static class LogTranslator
{
    private static readonly (string Chinese, string English)[] Exact =
    {
        ("Fotonizer 正在停止…", "Fotonizer is stopping…"),
        ("WMI 卷变化监听已启动", "WMI volume-change watcher started."),
        ("已启用阻止系统休眠（屏幕仍可按电源计划关闭）。",
            "Sleep prevention enabled (the screen can still turn off according to your power plan)."),
        ("已恢复系统正常休眠策略。", "Normal sleep behavior restored."),
        ("窗口已最小化到托盘，后台监控继续运行。",
            "Window minimized to the tray; monitoring continues in the background."),
        ("已暂停自动导入。", "Automatic import paused."),
        ("已恢复自动导入。", "Automatic import resumed."),
        ("Google 照片已连接。", "Google Photos connected."),
        ("已断开 Google 照片连接。", "Disconnected from Google Photos."),
        ("Google 照片授权成功。", "Google Photos authorization successful."),
        ("正在打开浏览器进行 Google 照片授权…", "Opening the browser for Google Photos authorization…"),
        ("已设置开机自启。", "Start with Windows enabled."),
        ("已关闭开机自启。", "Start with Windows disabled."),
        ("正在读取照片信息…", "Reading photo information…"),
        ("正在导入…", "Importing…")
    };

    private static readonly (string Prefix, string English)[] Prefixes =
    {
        ("Fotonizer 启动，照片库：", "Fotonizer started. Library: "),
        ("检测到新设备：", "New device detected: "),
        ("设备已移除：", "Device removed: "),
        ("设备已连接：", "Device connected: "),
        ("已连接便携设备：", "Portable device connected: "),
        ("相机/手机已断开：", "Camera/phone disconnected: "),
        ("已排队导入：", "Import queued: "),
        ("准备导入 ", "Preparing to import "),
        ("正在连接 ", "Connecting to "),
        ("正在下载 ", "Downloading "),
        ("已上传到 Google 照片：", "Uploaded to Google Photos: "),
        ("开始上传 ", "Uploading "),
        ("文件缺失：", "File missing: "),
        ("读取 ", "Reading "),
        ("已清理遗留临时文件：", "Removed a leftover temporary file: "),
        ("清理临时文件时出错：", "Error while cleaning temporary files: "),
        ("枚举便携设备失败：", "Failed to enumerate portable devices: "),
        ("删除临时文件失败 ", "Failed to delete temporary file "),
        ("删除来源文件失败 ", "Failed to delete source file "),
        ("自动增强失败，保留原图 ", "Auto enhance failed; original kept "),
        ("导入已取消：", "Import cancelled: "),
        ("导入失败 ", "Import failed "),
        ("导入 ", "Import "),
        ("读取 EXIF 失败，使用文件时间 ", "Failed to read EXIF; using file time "),
        ("读取设置失败，将使用默认设置：", "Failed to read settings; using defaults: "),
        ("保存设置失败：", "Failed to save settings: "),
        ("读取凭据失败（需要重新授权 Google 照片）：",
            "Failed to read credentials (Google Photos reauthorization required): "),
        ("保存凭据失败：", "Failed to save credentials: "),
        ("启用阻止系统休眠失败：", "Failed to enable sleep prevention: "),
        ("无法阻止系统休眠（", "Could not prevent system sleep ("),
        ("扫描 ", "Scanning ")
    };

    public static string Translate(string message)
    {
        if (!Loc.IsEnglish || string.IsNullOrWhiteSpace(message)) return message;

        foreach (var (chinese, english) in Exact)
            if (string.Equals(message, chinese, StringComparison.Ordinal))
                return english;

        var match = Regex.Match(message,
            @"^设备扫描完成：下载 (\d+) 个新文件，跳过 (\d+) 个已导入文件$");
        if (match.Success)
            return $"Device scan complete: {match.Groups[1].Value} new files downloaded, " +
                   $"{match.Groups[2].Value} already imported";

        match = Regex.Match(message,
            @"^扫描完成：(.+) → (\d+) 个候选文件（共检查 (\d+) 个）$");
        if (match.Success)
            return $"Scan complete: {match.Groups[1].Value} → {match.Groups[2].Value} candidate files " +
                   $"({match.Groups[3].Value} checked)";

        match = Regex.Match(message,
            @"^导入完成 (.+)：发现 (\d+)，导入 (\d+)，重复 (\d+)，连拍跳过 (\d+)，错误 (\d+)$");
        if (match.Success)
            return $"Import complete {match.Groups[1].Value}: found {match.Groups[2].Value}, " +
                   $"imported {match.Groups[3].Value}, duplicates {match.Groups[4].Value}, " +
                   $"burst skipped {match.Groups[5].Value}, errors {match.Groups[6].Value}";

        match = Regex.Match(message, @"^已扫描 (\d+) 个文件…$");
        if (match.Success)
            return $"Scanned {match.Groups[1].Value} files…";

        match = Regex.Match(message, @"^已检查 (\d+) 个文件…$");
        if (match.Success)
            return $"Checked {match.Groups[1].Value} files…";

        foreach (var (prefix, english) in Prefixes)
        {
            if (!message.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var rest = message[prefix.Length..];
            return english + rest.Replace("（", " (").Replace("）", ")");
        }

        return message;
    }
}
