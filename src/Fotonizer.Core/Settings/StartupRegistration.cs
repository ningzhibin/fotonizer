using System.IO;
using Fotonizer.Core.Logging;
using Microsoft.Win32;

namespace Fotonizer.Core.Settings;

/// <summary>Registers Fotonizer in HKCU Run so it starts with Windows (per-user, no admin needed).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Fotonizer";

    public static void Apply(bool enabled, string executablePath)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                            ?? Registry.CurrentUser.CreateSubKey(RunKey);
            if (key == null) return;

            if (enabled)
            {
                var command = $"\"{executablePath}\" --minimized";
                key.SetValue(ValueName, command, RegistryValueKind.String);
                AppLog.Shared.Info("已设置开机自启。");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLog.Shared.Info("已关闭开机自启。");
            }
        }
        catch (Exception ex)
        {
            AppLog.Shared.Warn($"更新开机自启设置失败：{ex.Message}");
        }
    }

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch
        {
            return false;
        }
    }
}
