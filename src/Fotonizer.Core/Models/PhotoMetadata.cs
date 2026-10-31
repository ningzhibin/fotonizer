namespace Fotonizer.Core.Models;

/// <summary>
/// Metadata needed for naming, grouping and upload decisions.
/// CapturedAt is local wall-clock time as recorded by the camera (EXIF DateTimeOriginal).
/// </summary>
public sealed class PhotoMetadata
{
    public required string SourcePath { get; init; }
    public long Size { get; init; }
    public DateTime CapturedAt { get; init; }
    public bool HasExifDate { get; init; }
    public string? Make { get; init; }
    public string? Model { get; init; }
    public int Orientation { get; init; } = 1;
    public bool IsVideo { get; init; }
    public string SourceKey { get; init; } = "";

    public string Extension => Path.GetExtension(SourcePath).ToLowerInvariant();
    public string Camera => !string.IsNullOrWhiteSpace(Model) ? Model!.Trim() : (Make ?? "").Trim();
    public string CaptureKey => $"{CapturedAt:yyyyMMddHHmmssfff}|{Camera}";
}
