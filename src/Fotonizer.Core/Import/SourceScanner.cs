using System.IO;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Media;
using Fotonizer.Core.Models;

namespace Fotonizer.Core.Import;

/// <summary>Enumerates photo/video candidates on a mounted drive or folder.</summary>
public static class SourceScanner
{
    private static readonly string[] IgnoredDirectoryNames =
    {
        "System Volume Information", "$RECYCLE.BIN", "found.000", ".thumbnails", "#recycle",
        "node_modules", ".git", "Windows", "Program Files", "Program Files (x86)", "ProgramData"
    };

    public static List<SourceFile> Scan(string root, AppSettings settings,
        IProgress<ImportProgress>? progress, CancellationToken cancellationToken)
    {
        var result = new List<SourceFile>();
        if (!Directory.Exists(root)) return result;

        var options = new EnumerationOptions
        {
            RecurseSubdirectories = settings.IncludeSubfolders,
            MaxRecursionDepth = Math.Max(1, settings.MaxScanDepth),
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint,
            MatchCasing = MatchCasing.CaseInsensitive,
            ReturnSpecialDirectories = false
        };

        var destination = AppPaths.Expand(settings.DestinationRoot);
        var scanned = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(root, "*", options))
            {
                cancellationToken.ThrowIfCancellationRequested();
                scanned++;

                if (scanned % 50 == 0)
                    progress?.Report(new ImportProgress(ImportStage.Scanning, 0, 0, path, $"已扫描 {scanned} 个文件…"));

                if (!MediaTypes.IsSupported(path, settings.CopyVideos)) continue;
                if (AppPaths.IsUnder(path, destination)) continue;

                var name = Path.GetFileName(path);
                if (name.StartsWith("._", StringComparison.Ordinal) ||
                    name.StartsWith("Thumbs.db", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (IsInIgnoredDirectory(path, root)) continue;

                long size;
                try
                {
                    size = new FileInfo(path).Length;
                }
                catch
                {
                    continue;
                }

                if (size <= 0) continue;
                result.Add(new SourceFile { Path = path, Size = size });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            AppLog.Shared.Warn($"扫描 {root} 时出错：{ex.Message}");
        }

        AppLog.Shared.Info($"扫描完成：{root} → {result.Count} 个候选文件（共检查 {scanned} 个）");
        return result;
    }

    private static bool IsInIgnoredDirectory(string path, string root)
    {
        var relative = Path.GetRelativePath(root, path);
        if (relative.StartsWith("..", StringComparison.Ordinal)) return false;

        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        for (var i = 0; i < segments.Length - 1; i++)
        {
            if (IgnoredDirectoryNames.Any(d => string.Equals(d, segments[i], StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }
}
