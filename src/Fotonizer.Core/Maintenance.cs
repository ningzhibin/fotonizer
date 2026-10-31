using System.IO;
using Fotonizer.Core.Import;
using Fotonizer.Core.Logging;

namespace Fotonizer.Core;

/// <summary>Small background housekeeping tasks.</summary>
public static class Maintenance
{
    /// <summary>
    /// Removes temporary files left behind if the process was killed mid-import/enhance.
    /// Only files older than one day are touched, so an in-progress import is never disturbed.
    /// </summary>
    public static void CleanupStaleTempFiles(string? destinationRoot)
    {
        if (string.IsNullOrWhiteSpace(destinationRoot) || !Directory.Exists(destinationRoot)) return;

        var cutoff = DateTime.Now.AddDays(-1);
        try
        {
            foreach (var file in Directory.EnumerateFiles(destinationRoot, ".fotonizer-*.tmp*",
                         SearchOption.AllDirectories))
            {
                try
                {
                    if (File.GetLastWriteTime(file) < cutoff)
                    {
                        FileFingerprint.TryDelete(file);
                        AppLog.Shared.Debug($"已清理遗留临时文件：{file}");
                    }
                }
                catch
                {
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Shared.Debug($"清理临时文件时出错：{ex.Message}");
        }
    }
}
