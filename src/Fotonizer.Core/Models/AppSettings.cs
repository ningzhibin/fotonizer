using System.Text.Json.Serialization;

namespace Fotonizer.Core.Models;

public enum EnhanceMode
{
    Off = 0,
    Subtle = 1,
    Strong = 2
}

public enum BurstKeepMode
{
    Skip = 0,
    KeepInSubfolder = 1
}

public enum UploadState
{
    Pending = 0,
    Uploaded = 1,
    Failed = 2,
    Skipped = 3
}

/// <summary>
/// All user-configurable options. Persisted as JSON in %APPDATA%\Fotonizer\settings.json.
/// Keep this class free of UI dependencies so it can be used from the CLI and tests.
/// </summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;

    // ---- Sources -------------------------------------------------------
    public bool WatchRemovableDrives { get; set; } = true;
    public bool WatchFixedDrives { get; set; } = false;
    public bool WatchPortableDevices { get; set; } = true;
    public bool AutoImport { get; set; } = true;
    public bool ScanConnectedDevicesOnStartup { get; set; } = true;
    public List<string> ExtraFolders { get; set; } = new();
    public int RescanExtraFoldersMinutes { get; set; } = 0;
    public bool IncludeSubfolders { get; set; } = true;
    public int MaxScanDepth { get; set; } = 8;
    public bool CopyVideos { get; set; } = true;
    public int PollSeconds { get; set; } = 5;

    // ---- Library / naming ---------------------------------------------
    public string DestinationRoot { get; set; } = AppPaths.DefaultDestination;
    public string SubfolderTemplate { get; set; } = "{yyyy}\\{MM}";
    public string FileNameTemplate { get; set; } = "{yyyy}-{MM}-{dd}-{HH}{mm}{ss}";
    public bool OrganizeByDevice { get; set; } = false;
    public bool DeleteSourceAfterCopy { get; set; } = false;

    // ---- Enhancement ---------------------------------------------------
    public EnhanceMode EnhanceMode { get; set; } = EnhanceMode.Subtle;
    public int JpegQuality { get; set; } = 92;

    // ---- Burst selection ----------------------------------------------
    public bool BurstSelection { get; set; } = true;
    public double BurstWindowSeconds { get; set; } = 1.5;
    public int BurstMinCount { get; set; } = 3;
    public BurstKeepMode BurstKeepMode { get; set; } = BurstKeepMode.Skip;
    public string BurstFolderName { get; set; } = "_Burst";

    // ---- Google Photos -------------------------------------------------
    public bool UploadEnabled { get; set; } = false;
    public string GoogleClientId { get; set; } = "";
    public string AlbumTemplate { get; set; } = "{yyyy}-{MM}";
    public bool UploadVideos { get; set; } = true;
    public int UploadRetryLimit { get; set; } = 5;

    // ---- Application ---------------------------------------------------
    public bool HasCompletedSetup { get; set; } = false;
    public bool StartWithWindows { get; set; } = false;
    public bool MinimizeToTray { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool PreventSystemSleep { get; set; } = false;
    public string Language { get; set; } = "zh-CN";
    public bool Paused { get; set; } = false;

    [JsonIgnore]
    public string GoogleClientSecret { get; set; } = "";

    public AppSettings Clone()
    {
        var clone = (AppSettings)MemberwiseClone();
        clone.ExtraFolders = new List<string>(ExtraFolders);
        return clone;
    }

    /// <summary>Clamps/repairs values loaded from disk so the rest of the app can trust them.</summary>
    public void Normalize()
    {
        ExtraFolders ??= new List<string>();
        ExtraFolders = ExtraFolders
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Environment.ExpandEnvironmentVariables(p.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (string.IsNullOrWhiteSpace(DestinationRoot))
            DestinationRoot = AppPaths.DefaultDestination;
        DestinationRoot = Environment.ExpandEnvironmentVariables(DestinationRoot.Trim());

        if (string.IsNullOrWhiteSpace(FileNameTemplate))
            FileNameTemplate = "{yyyy}-{MM}-{dd}-{HH}{mm}{ss}";
        if (SubfolderTemplate is null)
            SubfolderTemplate = "";
        if (string.IsNullOrWhiteSpace(BurstFolderName))
            BurstFolderName = "_Burst";

        JpegQuality = Math.Clamp(JpegQuality, 60, 100);
        BurstWindowSeconds = Math.Clamp(BurstWindowSeconds, 0.3, 15);
        BurstMinCount = Math.Clamp(BurstMinCount, 2, 20);
        UploadRetryLimit = Math.Clamp(UploadRetryLimit, 1, 20);
        PollSeconds = Math.Clamp(PollSeconds, 2, 60);
        MaxScanDepth = Math.Clamp(MaxScanDepth, 1, 32);
        RescanExtraFoldersMinutes = Math.Clamp(RescanExtraFoldersMinutes, 0, 24 * 60);
        AlbumTemplate ??= "";
        GoogleClientId = (GoogleClientId ?? "").Trim();
        Language = string.IsNullOrWhiteSpace(Language) ||
                   !Language.StartsWith("en", StringComparison.OrdinalIgnoreCase)
            ? "zh-CN"
            : "en-US";
    }
}
