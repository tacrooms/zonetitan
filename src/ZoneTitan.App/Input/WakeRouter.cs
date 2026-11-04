using System.Runtime.InteropServices;
using Microsoft.Win32;
using ZoneTitan.App.Interop;
using ZoneTitan.App.Services;
using ZoneTitan.Core.Diagnostics;

namespace ZoneTitan.App.Input;

/// <summary>
/// Activates a zone based on which input device you touch first after the machine resumes
/// (docs/addendum-input-device-routing.md).
/// <para>
/// This routes on first input rather than on the wake source, because Windows does not record
/// which device woke the machine on this hardware — eight consecutive wake events came back
/// with <c>WakeSourceType=0</c> and an empty device path. First-input is also the better
/// signal: it follows the device actually in your hand, and it covers resume-from-display-off
/// and unlock, which have no wake source at all.
/// </para>
/// </summary>
internal sealed class WakeRouter : IDisposable
{
    private readonly ZoneController _controller;
    private readonly MessageWindow _window;
    private DateTime _windowExpiresUtc = DateTime.MinValue;
    private bool _armed;
    private string? _lastUnmatchedDevice;
    private bool _disposed;

    /// <summary>Raised when routing fires, for the UI and the log.</summary>
    public event Action<string, string>? Routed;

    public WakeRouter(ZoneController controller, MessageWindow window)
    {
        _controller = controller;
        _window = window;

        _window.RawInputReceived += OnRawInput;
        RegisterForRawInput();

        // Message-only windows never receive WM_POWERBROADCAST — it is a broadcast message,
        // and broadcasts skip HWND_MESSAGE children. SystemEvents gets it via its own
        // top-level window.
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    private void RegisterForRawInput()
    {
        var devices = new[]
        {
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC,
                usUsage = NativeMethods.HID_USAGE_GENERIC_MOUSE,
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = _window.Handle,
            },
            new NativeMethods.RAWINPUTDEVICE
            {
                usUsagePage = NativeMethods.HID_USAGE_PAGE_GENERIC,
                usUsage = NativeMethods.HID_USAGE_GENERIC_KEYBOARD,
                dwFlags = NativeMethods.RIDEV_INPUTSINK,
                hwndTarget = _window.Handle,
            },
        };

        if (!NativeMethods.RegisterRawInputDevices(
                devices, (uint)devices.Length, (uint)Marshal.SizeOf<NativeMethods.RAWINPUTDEVICE>()))
        {
            Log.Warn($"Raw input registration failed (error {Marshal.GetLastWin32Error()}); " +
                     "wake routing will not work.");
        }
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            Arm("resume from sleep");
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason is SessionSwitchReason.SessionUnlock or SessionSwitchReason.SessionLogon)
            Arm("session unlock");
    }

    /// <summary>Opens the routing window. The first input inside it decides the zone.</summary>
    public void Arm(string reason)
    {
        var options = _controller.Config.InputRouting;
        if (options is null || !options.Enabled || options.Rules.Count == 0)
            return;

        var seconds = Math.Clamp(options.RoutingWindowSeconds, 2, 120);
        _windowExpiresUtc = DateTime.UtcNow.AddSeconds(seconds);
        _armed = true;

        Log.Info($"Wake routing armed for {seconds}s ({reason}).");
    }

    /// <summary>
    /// Captures the next device that sends input and hands back its interface name, without
    /// applying any zone.
    /// <para>
    /// This exists because inferring which physical peripheral a VID/PID belongs to from the
    /// vendor id is guesswork, and guessing wrong produces rules that fire for the wrong
    /// device. Pressing the key is the only reliable identification.
    /// </para>
    /// </summary>
    public void CaptureNextDevice(Action<string, string> onCaptured)
    {
        _captureCallback = onCaptured;
    }

    public void CancelCapture() => _captureCallback = null;

    /// <summary>Callback receives (device interface name, "Mouse" or "Keyboard").</summary>
    private Action<string, string>? _captureCallback;

    private void OnRawInput(nint hRawInput)
    {
        // Identification mode takes priority and ignores the routing window entirely.
        if (_captureCallback is { } capture)
        {
            var (capturedName, capturedType) = ResolveDevice(hRawInput);
            if (string.IsNullOrWhiteSpace(capturedName))
                return;

            _captureCallback = null;
            capture(capturedName, DescribeType(capturedType));
            return;
        }

        if (!_armed)
            return;

        if (DateTime.UtcNow > _windowExpiresUtc)
        {
            // Expired with nothing matched: the zone stands. Silence is the safe outcome.
            _armed = false;
            Log.Info("Wake routing window expired with no matching device.");
            return;
        }

        var deviceName = ResolveDeviceName(hRawInput);
        if (string.IsNullOrWhiteSpace(deviceName))
            return;

        var options = _controller.Config.InputRouting;
        if (options is null || !options.Enabled)
        {
            _armed = false;
            return;
        }

        var rule = options.Rules.FirstOrDefault(r => r.Matches(deviceName));
        if (rule is null)
        {
            // Not a device we route on — keep listening until the window closes, because the
            // user may still reach for a mouse that *is* mapped. Log it once, since an
            // unmatched device here is the single most useful clue when routing "does nothing".
            if (_lastUnmatchedDevice != deviceName)
            {
                _lastUnmatchedDevice = deviceName;
                Log.Info($"Wake routing: no rule for device {deviceName}");
            }

            return;
        }

        _armed = false;

        var label = rule.Label ?? rule.Device;
        Log.Info($"Wake routing: '{label}' matched → zone '{rule.Zone}'.");

        if (!string.Equals(_controller.ActiveZone, rule.Zone, StringComparison.OrdinalIgnoreCase))
            _controller.ApplyZone(rule.Zone);

        Routed?.Invoke(label, rule.Zone);
    }

    /// <summary>
    /// Resolves the raw-input handle to a device interface name such as
    /// <c>\\?\HID#VID_04D9&amp;PID_FC38&amp;MI_00#8&amp;...</c>.
    /// <para>
    /// Note this is the *interface* name, not the PnP instance id shown by Device Manager
    /// (<c>HID\VID_04D9&amp;PID_FC38&amp;MI_00\8&amp;...</c>). They differ in separators, which is
    /// exactly why rules default to VID/PID substring matching — that fragment appears
    /// identically in both forms.
    /// </para>
    /// </summary>
    private static string DescribeType(uint rawInputType) => rawInputType switch
    {
        NativeMethods.RIM_TYPEMOUSE => "Mouse",
        NativeMethods.RIM_TYPEKEYBOARD => "Keyboard",
        _ => "Device",
    };

    private static string? ResolveDeviceName(nint hRawInput) => ResolveDevice(hRawInput).Name;

    private static (string? Name, uint Type) ResolveDevice(nint hRawInput)
    {
        try
        {
            var headerSize = (uint)Marshal.SizeOf<NativeMethods.RAWINPUTHEADER>();
            var size = headerSize;

            var headerBuffer = Marshal.AllocHGlobal((int)headerSize);
            try
            {
                if (NativeMethods.GetRawInputData(
                        hRawInput, NativeMethods.RID_HEADER, headerBuffer, ref size, headerSize) == uint.MaxValue)
                {
                    return (null, 0);
                }

                var header = Marshal.PtrToStructure<NativeMethods.RAWINPUTHEADER>(headerBuffer);
                if (header.hDevice == nint.Zero)
                    return (null, header.dwType);

                uint charCount = 0;
                if (NativeMethods.GetRawInputDeviceInfo(
                        header.hDevice, NativeMethods.RIDI_DEVICENAME, nint.Zero, ref charCount) != 0 ||
                    charCount == 0)
                {
                    return (null, header.dwType);
                }

                var nameBuffer = Marshal.AllocHGlobal((int)((charCount + 1) * sizeof(char)));
                try
                {
                    if (NativeMethods.GetRawInputDeviceInfo(
                            header.hDevice, NativeMethods.RIDI_DEVICENAME, nameBuffer, ref charCount) == uint.MaxValue)
                    {
                        return (null, header.dwType);
                    }

                    return (Marshal.PtrToStringUni(nameBuffer), header.dwType);
                }
                finally
                {
                    Marshal.FreeHGlobal(nameBuffer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(headerBuffer);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not resolve raw input device: {ex.Message}");
            return (null, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _window.RawInputReceived -= OnRawInput;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.SessionSwitch -= OnSessionSwitch;
    }
}
