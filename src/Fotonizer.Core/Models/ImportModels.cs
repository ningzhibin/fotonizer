namespace Fotonizer.Core.Models;

public enum ImportSourceKind
{
    Folder = 0,
    Drive = 1,
    PortableDevice = 2
}

public enum ImportStage
{
    Preparing,
    Scanning,
    Analyzing,
    Importing,
    Completed,
    Failed
}

/// <summary>Describes one import job (a drive, a folder, or a camera/phone).</summary>
public sealed class ImportRequest
{
    public required ImportSourceKind Kind { get; init; }
    public required string SourcePath { get; init; }
    public string DisplayName { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public bool Automatic { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    public string Key => Kind == ImportSourceKind.PortableDevice
        ? $"mtp:{DeviceId}"
        : $"path:{Path.GetFullPath(SourcePath).TrimEnd('\\')}".ToLowerInvariant();

    public override string ToString() => string.IsNullOrWhiteSpace(DisplayName) ? SourcePath : DisplayName;
}

public sealed record ImportProgress(
    ImportStage Stage,
    int Current,
    int Total,
    string? CurrentFile = null,
    string Message = "");

public sealed record ImportSummary(
    bool Success,
    ImportRequest Request,
    int Found,
    int Imported,
    int Duplicates,
    int BurstGroups,
    int BurstSkipped,
    int Enhanced,
    int UploadQueued,
    int Errors,
    string? Error,
    TimeSpan Duration)
{
    public static ImportSummary Failed(ImportRequest request, string error, TimeSpan duration) =>
        new(false, request, 0, 0, 0, 0, 0, 0, 0, 0, error, duration);
}

/// <summary>A candidate photo/video discovered on a source device.</summary>
public sealed class SourceFile
{
    public required string Path { get; init; }
    public long Size { get; init; }
    public string SourceKey { get; init; } = "";
    public bool IsTemporary { get; init; }
}

public sealed class ImportedFileRecord
{
    public required string Sha256 { get; init; }
    public required string QuickHash { get; init; }
    public string? SourcePath { get; init; }
    public required string DestPath { get; init; }
    public required string FileName { get; init; }
    public long Size { get; init; }
    public DateTime CapturedAt { get; init; }
    public DateTime ImportedAt { get; init; }
    public UploadState UploadState { get; set; } = UploadState.Pending;
    public int UploadAttempts { get; set; }
    public string? UploadError { get; set; }
    public string? GoogleMediaItemId { get; set; }
    public DateTime? UploadedAt { get; set; }
    public string? BurstGroup { get; set; }
    public double? Score { get; set; }
    public bool IsBurstPick { get; set; }
}

public sealed record ImportStats(
    int TotalFiles,
    long TotalBytes,
    int Uploaded,
    int PendingUpload,
    int FailedUpload,
    DateTime? LastImportedAt);
