namespace Fotonizer.Core.Media;

public static class MediaTypes
{
    public static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".png", ".heic", ".heif", ".webp", ".tif", ".tiff", ".bmp", ".gif",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".orf", ".rw2", ".pef", ".srw", ".mrw", ".3fr", ".fff"
    };

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mov", ".m4v", ".avi", ".mkv", ".3gp", ".wmv", ".mts", ".m2ts", ".mpg", ".mpeg"
    };

    /// <summary>Formats Fotonizer knows how to decode and re-encode to JPEG.</summary>
    public static readonly HashSet<string> EnhanceableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".heic", ".heif", ".tif", ".tiff"
    };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsVideo(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static bool IsSupported(string path, bool includeVideos) =>
        IsImage(path) || (includeVideos && IsVideo(path));

    public static bool IsEnhanceable(string path) => EnhanceableExtensions.Contains(Path.GetExtension(path));

    public static string[] AllImagePatterns => ImageExtensions.Select(e => "*" + e).ToArray();
}
