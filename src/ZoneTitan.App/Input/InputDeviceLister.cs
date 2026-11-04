using System.Runtime.InteropServices;
using ZoneTitan.App.Interop;
using ZoneTitan.Core.Diagnostics;

namespace ZoneTitan.App.Input;

/// <summary>A mouse or keyboard currently attached, as raw input sees it.</summary>
internal sealed record InputDevice(string InterfaceName, string Kind)
{
    /// <summary>
    /// The <c>VID_xxxx&amp;PID_xxxx&amp;MI_xx</c> fragment, which is what rules match on. It appears
    /// identically in the raw-input interface name and the Device Manager instance id, so a
    /// rule built from one still matches the other.
    /// </summary>
    public string? VidPidFragment
    {
        get
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                InterfaceName, @"VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}(&MI_[0-9A-Fa-f]{2})?");

            return match.Success ? match.Value : null;
        }
    }

    public string Display => VidPidFragment is { } fragment ? $"{Kind}  ·  {fragment}" : $"{Kind}  ·  {InterfaceName}";
}

/// <summary>
/// Lists attached mice and keyboards so wake-routing rules can be built from a live picker
/// rather than typed from memory — the discipline the stale-device incident argued for.
/// </summary>
internal static class InputDeviceLister
{
    public static IReadOnlyList<InputDevice> List()
    {
        var devices = new List<InputDevice>();

        try
        {
            uint count = 0;
            var structSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICELIST>();

            if (NativeMethods.GetRawInputDeviceList(null, ref count, structSize) == uint.MaxValue || count == 0)
                return devices;

            var list = new NativeMethods.RAWINPUTDEVICELIST[count];
            if (NativeMethods.GetRawInputDeviceList(list, ref count, structSize) == uint.MaxValue)
                return devices;

            foreach (var entry in list.Take((int)count))
            {
                if (entry.dwType is not (NativeMethods.RIM_TYPEMOUSE or NativeMethods.RIM_TYPEKEYBOARD))
                    continue;

                var name = ResolveName(entry.hDevice);
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                // RDP and other virtual devices are noise in a picker about physical hardware.
                if (name.Contains("RDP_MOU", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("RDP_KBD", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                devices.Add(new InputDevice(
                    name,
                    entry.dwType == NativeMethods.RIM_TYPEMOUSE ? "Mouse" : "Keyboard"));
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not list input devices: {ex.Message}");
        }

        return devices
            .GroupBy(d => d.VidPidFragment ?? d.InterfaceName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(d => d.Kind)
            .ThenBy(d => d.Display)
            .ToList();
    }

    private static string? ResolveName(nint hDevice)
    {
        uint charCount = 0;
        if (NativeMethods.GetRawInputDeviceInfo(hDevice, NativeMethods.RIDI_DEVICENAME, nint.Zero, ref charCount) != 0
            || charCount == 0)
        {
            return null;
        }

        var buffer = Marshal.AllocHGlobal((int)((charCount + 1) * sizeof(char)));
        try
        {
            return NativeMethods.GetRawInputDeviceInfo(
                hDevice, NativeMethods.RIDI_DEVICENAME, buffer, ref charCount) == uint.MaxValue
                ? null
                : Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
