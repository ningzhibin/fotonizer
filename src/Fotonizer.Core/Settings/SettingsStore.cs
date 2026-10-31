using System.Text.Json;
using System.Text.Json.Serialization;
using Fotonizer.Core.Logging;
using Fotonizer.Core.Models;

namespace Fotonizer.Core.Settings;

public sealed class SettingsStore
{
    private readonly object _gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FilePath { get; }
    public AppSettings Current { get; private set; }

    public event EventHandler? Saved;

    public SettingsStore(string? filePath = null)
    {
        FilePath = filePath ?? AppPaths.DefaultSettingsPath;
        Current = Load();
    }

    public AppSettings Snapshot() => Current.Clone();

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded != null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Shared.Warn($"读取设置失败，将使用默认设置：{ex.Message}");
            TryBackupCorruptFile();
        }

        var fresh = new AppSettings();
        fresh.Normalize();
        return fresh;
    }

    private void TryBackupCorruptFile()
    {
        try
        {
            if (File.Exists(FilePath))
                File.Copy(FilePath, FilePath + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true);
        }
        catch
        {
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Current.Normalize();
                var dir = Path.GetDirectoryName(FilePath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Current, JsonOptions));
                File.Move(tmp, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Shared.Error($"保存设置失败：{ex.Message}", ex);
                return;
            }
        }

        try
        {
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Shared.Debug($"Settings.Saved 订阅者异常：{ex.Message}");
        }
    }

    public void Reload()
    {
        Current = Load();
    }
}
