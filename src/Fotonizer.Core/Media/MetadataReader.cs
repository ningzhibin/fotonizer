using System.IO;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Models;
using MetadataExtractor;
using MetadataExtractor.Formats.Exif;
using Directory = MetadataExtractor.Directory;

namespace Fotonizer.Core.Media;

public static class MetadataReader
{
    public static PhotoMetadata Read(string path, bool isVideo, string sourceKey = "")
    {
        var info = new FileInfo(path);
        var captured = FallbackTimestamp(info);
        var hasExif = false;
        string? make = null, model = null;
        var orientation = 1;

        if (!isVideo)
        {
            try
            {
                IReadOnlyList<Directory> directories = ImageMetadataReader.ReadMetadata(path);
                var subIfd = directories.OfType<ExifSubIfdDirectory>().FirstOrDefault();
                var ifd0 = directories.OfType<ExifIfd0Directory>().FirstOrDefault();

                // Cameras normally store DateTimeOriginal in the Exif SubIFD, but some tools
                // (and our sample generator) write it into IFD0 — support both.
                var dateDirectories = new ExifDirectoryBase?[] { subIfd, ifd0 };
                DateTime? exifDate = null;
                foreach (var dir in dateDirectories)
                {
                    if (dir == null) continue;
                    if (dir.TryGetDateTime(ExifDirectoryBase.TagDateTimeOriginal, out var dt))
                    {
                        exifDate = dt;
                        break;
                    }
                }

                if (exifDate == null)
                {
                    foreach (var dir in dateDirectories)
                    {
                        if (dir == null) continue;
                        if (dir.TryGetDateTime(ExifDirectoryBase.TagDateTimeDigitized, out var digitized))
                        {
                            exifDate = digitized;
                            break;
                        }
                    }
                }

                if (exifDate != null)
                {
                    captured = exifDate.Value;
                    hasExif = true;

                    var subSecText = dateDirectories
                        .Where(d => d != null)
                        .Select(d => d!.GetString(ExifDirectoryBase.TagSubsecondTimeOriginal))
                        .FirstOrDefault(text => !string.IsNullOrWhiteSpace(text));
                    if (!string.IsNullOrWhiteSpace(subSecText))
                    {
                        var digits = new string(subSecText.Where(char.IsDigit).ToArray());
                        if (digits.Length > 0)
                        {
                            var ms = int.Parse(digits.Length >= 3 ? digits[..3] : digits.PadRight(3, '0'));
                            captured = captured.AddMilliseconds(ms);
                        }
                    }
                }

                if (ifd0 != null)
                {
                    if (ifd0.GetString(ExifDirectoryBase.TagMake) is { } makeText) make = Clean(makeText);
                    if (ifd0.GetString(ExifDirectoryBase.TagModel) is { } modelText) model = Clean(modelText);

                    if (ifd0.TryGetInt32(ExifDirectoryBase.TagOrientation, out var orientationValue) &&
                        orientationValue is >= 1 and <= 8)
                        orientation = orientationValue;
                }
            }
            catch (Exception ex) when (ex is ImageProcessingException or IOException or NotSupportedException
                                           or UnauthorizedAccessException or ArgumentException)
            {
                AppLog.Shared.Debug($"读取 EXIF 失败，使用文件时间 {Path.GetFileName(path)}: {ex.Message}");
            }
        }

        return new PhotoMetadata
        {
            SourcePath = path,
            Size = info.Length,
            CapturedAt = captured,
            HasExifDate = hasExif,
            Make = make,
            Model = model,
            Orientation = orientation,
            IsVideo = isVideo,
            SourceKey = sourceKey
        };
    }

    private static string? Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var cleaned = text.Trim().Trim('\0');
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>Debug helper used by the CLI to inspect every metadata tag.</summary>
    public static IReadOnlyList<string> DumpExif(string path)
    {
        var lines = new List<string>();
        foreach (var directory in ImageMetadataReader.ReadMetadata(path))
        foreach (var tag in directory.Tags)
            lines.Add($"{directory.Name} | {tag.Name} [{tag.Type}] = {tag.Description}");
        return lines;
    }

    private static DateTime FallbackTimestamp(FileInfo info)
    {
        var last = info.LastWriteTime;
        var created = info.CreationTime;

        if (last.Year is < 1980 or > 2200) last = created;
        if (last.Year is < 1980 or > 2200) last = DateTime.Now;
        return DateTime.SpecifyKind(last, DateTimeKind.Unspecified);
    }
}