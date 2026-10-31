using Fotonizer.Core.Logging;
using MediaDevices;

namespace Fotonizer.Core.Devices;

public sealed record PortableDeviceSummary(string DeviceId, string Name, string Description);

public static class PortableDeviceCatalog
{
    public static IReadOnlyList<PortableDeviceSummary> GetConnected()
    {
        var result = new List<PortableDeviceSummary>();
        List<MediaDevice> devices;
        try
        {
            devices = MediaDevice.GetDevices().ToList();
        }
        catch (Exception ex)
        {
            AppLog.Shared.Debug($"枚举便携设备失败：{ex.Message}");
            return result;
        }

        try
        {
            foreach (var device in devices)
            {
                var id = string.IsNullOrWhiteSpace(device.DeviceId) ? device.FriendlyName : device.DeviceId;
                if (string.IsNullOrWhiteSpace(id)) continue;
                result.Add(new PortableDeviceSummary(id, device.FriendlyName, device.Description));
            }
        }
        finally
        {
            foreach (var device in devices)
            {
                try
                {
                    device.Dispose();
                }
                catch
                {
                }
            }
        }

        return result;
    }
}
