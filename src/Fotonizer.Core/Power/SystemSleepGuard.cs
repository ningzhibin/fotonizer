using System.ComponentModel;
using System.Runtime.InteropServices;
using Fotonizer.Core.Logging;

namespace Fotonizer.Core.Power;

/// <summary>
/// Keeps the system awake while Fotonizer is running, using a Windows power request
/// (PowerCreateRequest / PowerSetRequest). This prevents idle sleep but does not keep
/// the display on; the screen still follows the normal power plan.
/// </summary>
public sealed class SystemSleepGuard : IDisposable
{
    private const uint PowerRequestContextSimpleString = 0x1;

    private readonly object _gate = new();
    private IntPtr _request;
    private bool _enabled;
    private bool _disposed;

    public bool IsActive
    {
        get
        {
            lock (_gate) return _request != IntPtr.Zero;
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            if (_disposed || enabled == _enabled) return;

            if (enabled)
            {
                if (TryEnable())
                {
                    _enabled = true;
                }
                else
                {
                    _enabled = false;
                }
            }
            else
            {
                Disable();
                _enabled = false;
            }
        }
    }

    private bool TryEnable()
    {
        try
        {
            var context = new PowerRequestContext
            {
                Version = 0,
                Flags = PowerRequestContextSimpleString,
                SimpleReasonString = "Fotonizer 正在导入或上传照片"
            };

            var handle = PowerCreateRequest(ref context);
            if (handle == IntPtr.Zero)
            {
                LogFailure("PowerCreateRequest");
                return false;
            }

            if (!PowerSetRequest(handle, PowerRequestType.SystemRequired))
            {
                LogFailure("PowerSetRequest");
                CloseHandle(handle);
                return false;
            }

            _request = handle;
            AppLog.Shared.Info("已启用阻止系统休眠（屏幕仍可按电源计划关闭）。");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Shared.Warn($"启用阻止系统休眠失败：{ex.Message}");
            return false;
        }
    }

    private void Disable()
    {
        if (_request == IntPtr.Zero) return;

        try
        {
            PowerClearRequest(_request, PowerRequestType.SystemRequired);
        }
        catch
        {
        }

        try
        {
            CloseHandle(_request);
        }
        catch
        {
        }

        _request = IntPtr.Zero;
        AppLog.Shared.Info("已恢复系统正常休眠策略。");
    }

    private static void LogFailure(string api)
    {
        var error = Marshal.GetLastWin32Error();
        var message = error != 0 ? new Win32Exception(error).Message : "unknown error";
        AppLog.Shared.Warn($"无法阻止系统休眠（{api} 失败：{message}）。");
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            Disable();
            _enabled = false;
            _disposed = true;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PowerRequestContext
    {
        public uint Version;
        public uint Flags;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? SimpleReasonString;
    }

    private enum PowerRequestType
    {
        SystemRequired = 1,
        DisplayRequired = 2,
        UserPresent = 3,
        AwayMode = 4
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr PowerCreateRequest(ref PowerRequestContext context);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerSetRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PowerClearRequest(IntPtr powerRequest, PowerRequestType requestType);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
