// -----------------------------------------------------------------------------
// The CCD query/marshal sequence used here follows the approach in
// DisplayProfileManager (https://github.com/zac15987/DisplayProfileManager),
// src/Helpers/DisplayConfigHelper.cs.
//
//     MIT License
//     Copyright (c) 2025 zac15987
//
// Full license text: THIRD-PARTY-LICENSES.md at the repository root.
//
// Modified for ZoneTitan:
//   - Enumerates QDC_ALL_PATHS so detached-but-attached monitors are visible
//     (upstream enumerates active paths only).
//   - Keys monitors on monitorDevicePath rather than friendly name (spec §4).
//   - Deduplicates the source x target path explosion, preferring active paths.
//   - Adds preferred-mode and 24H2 advanced-colour queries with legacy fallback.
// -----------------------------------------------------------------------------

using ZoneTitan.Core.Native;
using static ZoneTitan.Core.Native.CcdNative;

namespace ZoneTitan.Core.Display;

/// <summary>Raised when the CCD API refuses a query.</summary>
public sealed class DisplayQueryException(string message) : Exception(message);

/// <summary>
/// Reads the current display topology and returns every physically attached monitor,
/// active or detached, identified by device path.
/// </summary>
public static class MonitorEnumerator
{
    /// <summary>
    /// Enumerates all attached monitors. Detached monitors are included with
    /// <see cref="MonitorInfo.IsActive"/> false — ZoneTitan needs to see a monitor it
    /// is about to re-attach.
    /// </summary>
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var topology = DisplayTopology.Query(QueryDisplayConfigFlags.QDC_ALL_PATHS);
        var (paths, modes) = (topology.Paths, topology.Modes);

        // QDC_ALL_PATHS returns the cross product of sources and targets, so one
        // physical monitor appears many times. Collapse on device path, preferring
        // the active path — that is the one carrying real mode information.
        var byDevicePath = new Dictionary<string, MonitorInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            if (!path.targetInfo.IsTargetAvailable)
                continue;

            if (!TryGetTargetName(path, out var targetName))
                continue;

            var devicePath = targetName.monitorDevicePath;

            // A target with no device path is a GPU output with nothing plugged into it.
            if (string.IsNullOrWhiteSpace(devicePath))
                continue;

            // First writer wins unless a later path is active and the stored one is not.
            if (byDevicePath.TryGetValue(devicePath, out var existing) &&
                (existing.IsActive || !path.IsActive))
            {
                continue;
            }

            byDevicePath[devicePath] = BuildMonitorInfo(path, targetName, modes);
        }

        // Active monitors first, then left-to-right by desktop position; detached
        // monitors trail in a stable alphabetical order.
        return byDevicePath.Values
            .OrderByDescending(m => m.IsActive)
            .ThenBy(m => m.Position?.X ?? int.MaxValue)
            .ThenBy(m => m.Position?.Y ?? int.MaxValue)
            .ThenBy(m => m.FriendlyName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool TryGetTargetName(
        DISPLAYCONFIG_PATH_INFO path,
        out DISPLAYCONFIG_TARGET_DEVICE_NAME targetName)
        => DisplayTopology.TryGetTargetName(in path, out targetName);

    private static MonitorInfo BuildMonitorInfo(
        DISPLAYCONFIG_PATH_INFO path,
        DISPLAYCONFIG_TARGET_DEVICE_NAME targetName,
        DISPLAYCONFIG_MODE_INFO[] modes)
    {
        DisplayMode? currentMode = null;
        DisplayPosition? position = null;

        if (path.IsActive)
        {
            var sourceIdx = path.sourceInfo.modeInfoIdx;
            if (sourceIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && sourceIdx < modes.Length &&
                modes[sourceIdx].infoType == DisplayConfigModeInfoType.SOURCE)
            {
                var source = modes[sourceIdx].modeInfo.sourceMode;
                position = new DisplayPosition(source.position.x, source.position.y);
                currentMode = new DisplayMode(source.width, source.height, ResolveRefreshHz(path, modes));
            }
        }

        var friendly = string.IsNullOrWhiteSpace(targetName.monitorFriendlyDeviceName)
            ? "(unnamed display)"
            : targetName.monitorFriendlyDeviceName.Trim();

        return new MonitorInfo
        {
            DevicePath = targetName.monitorDevicePath,
            FriendlyName = friendly,
            VendorCode = DecodeVendorCode(targetName.edidManufactureId),
            ProductCode = targetName.edidProductCodeId,
            IsActive = path.IsActive,
            ConnectorType = DescribeConnector(targetName.outputTechnology),
            CurrentMode = currentMode,
            PreferredMode = TryGetPreferredMode(path),
            Position = position,
            Rotation = path.IsActive ? DescribeRotation(path.targetInfo.rotation) : null,
            Hdr = QueryHdrState(path),
            GdiName = TryGetGdiName(path),
            AdapterLuid = path.targetInfo.adapterId.ToString(),
            AdapterDevicePath = TryGetAdapterPath(path),
            SourceId = path.sourceInfo.id,
            TargetId = path.targetInfo.id,
        };
    }

    /// <summary>
    /// Refresh rate from the target mode's vertical sync frequency, which is the value
    /// the display is actually running. Falls back to the rate advertised on the path.
    /// </summary>
    private static double ResolveRefreshHz(DISPLAYCONFIG_PATH_INFO path, DISPLAYCONFIG_MODE_INFO[] modes)
    {
        var targetIdx = path.targetInfo.modeInfoIdx;
        if (targetIdx != DISPLAYCONFIG_PATH_MODE_IDX_INVALID && targetIdx < modes.Length &&
            modes[targetIdx].infoType == DisplayConfigModeInfoType.TARGET)
        {
            var hz = modes[targetIdx].modeInfo.targetMode.targetVideoSignalInfo.vSyncFreq.ToHz();
            if (hz > 0)
                return hz;
        }

        return path.targetInfo.refreshRate.ToHz();
    }

    private static DisplayMode? TryGetPreferredMode(DISPLAYCONFIG_PATH_INFO path)
    {
        var request = new DISPLAYCONFIG_TARGET_PREFERRED_MODE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.GET_TARGET_PREFERRED_MODE,
                size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_TARGET_PREFERRED_MODE>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref request) != ERROR_SUCCESS)
            return null;

        var hz = request.targetMode.targetVideoSignalInfo.vSyncFreq.ToHz();
        return new DisplayMode(request.width, request.height, hz);
    }

    /// <summary>
    /// Reads HDR state, preferring the Windows 11 24H2 structure and falling back to the
    /// legacy advanced-colour query on older builds (spec §5).
    /// </summary>
    private static HdrState QueryHdrState(DISPLAYCONFIG_PATH_INFO path)
    {
        var modern = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.GET_ADVANCED_COLOR_INFO_2,
                size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref modern) == ERROR_SUCCESS)
        {
            // INFO_2 separates HDR from wide-colour gamut, so report HDR specifically.
            // The legacy struct's AdvancedColorSupported bit conflates the two and reports
            // "supported" for WCG-only panels that cannot actually do HDR.
            return new HdrState(
                Supported: modern.HdrSupported,
                Enabled: modern.activeColorMode == DisplayConfigAdvancedColorMode.HDR,
                LimitedByPolicy: modern.LimitedByPolicy,
                ColorEncoding: modern.colorEncoding.ToString(),
                BitsPerColorChannel: modern.bitsPerColorChannel,
                Source: $"GET_ADVANCED_COLOR_INFO_2 (mode: {modern.activeColorMode}, flags: 0x{modern.value:X2})");
        }

        var legacy = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.GET_ADVANCED_COLOR_INFO,
                size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };

        if (DisplayConfigGetDeviceInfo(ref legacy) != ERROR_SUCCESS)
            return HdrState.Unknown;

        var forceDisabled = legacy.values.HasFlag(AdvancedColorInfoFlags.AdvancedColorForceDisabled);

        return new HdrState(
            Supported: legacy.values.HasFlag(AdvancedColorInfoFlags.AdvancedColorSupported) && !forceDisabled,
            Enabled: legacy.values.HasFlag(AdvancedColorInfoFlags.AdvancedColorEnabled),
            LimitedByPolicy: forceDisabled,
            ColorEncoding: legacy.colorEncoding.ToString(),
            BitsPerColorChannel: (uint)legacy.bitsPerColorChannel,
            Source: "GET_ADVANCED_COLOR_INFO (legacy)");
    }

    private static string? TryGetGdiName(DISPLAYCONFIG_PATH_INFO path)
    {
        var request = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.GET_SOURCE_NAME,
                size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                adapterId = path.sourceInfo.adapterId,
                id = path.sourceInfo.id,
            },
        };

        return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS
            ? request.viewGdiDeviceName
            : null;
    }

    private static string? TryGetAdapterPath(DISPLAYCONFIG_PATH_INFO path)
    {
        var request = new DISPLAYCONFIG_ADAPTER_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.GET_ADAPTER_NAME,
                size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<DISPLAYCONFIG_ADAPTER_NAME>(),
                adapterId = path.targetInfo.adapterId,
                id = path.targetInfo.id,
            },
        };

        return DisplayConfigGetDeviceInfo(ref request) == ERROR_SUCCESS
            ? request.adapterDevicePath
            : null;
    }

    /// <summary>
    /// Decodes the EDID manufacturer id into its three-letter PNP code (e.g. <c>DEL</c>).
    /// The id is stored big-endian with five bits per letter, 1 = 'A'.
    /// </summary>
    private static string? DecodeVendorCode(ushort edidManufactureId)
    {
        if (edidManufactureId == 0)
            return null;

        var swapped = (ushort)((edidManufactureId >> 8) | (edidManufactureId << 8));

        var letters = new char[3];
        for (var i = 0; i < 3; i++)
        {
            var value = (swapped >> (10 - i * 5)) & 0x1F;
            if (value is < 1 or > 26)
                return null;

            letters[i] = (char)('A' + value - 1);
        }

        return new string(letters);
    }

    private static string DescribeConnector(DisplayConfigVideoOutputTechnology technology) => technology switch
    {
        DisplayConfigVideoOutputTechnology.HD15 => "VGA",
        DisplayConfigVideoOutputTechnology.DVI => "DVI",
        DisplayConfigVideoOutputTechnology.HDMI => "HDMI",
        DisplayConfigVideoOutputTechnology.LVDS => "LVDS",
        DisplayConfigVideoOutputTechnology.SDI => "SDI",
        DisplayConfigVideoOutputTechnology.DISPLAYPORT_EXTERNAL => "DisplayPort",
        DisplayConfigVideoOutputTechnology.DISPLAYPORT_EMBEDDED => "DisplayPort (embedded)",
        DisplayConfigVideoOutputTechnology.DISPLAYPORT_USB_TUNNEL => "DisplayPort (USB-C)",
        DisplayConfigVideoOutputTechnology.UDI_EXTERNAL => "UDI",
        DisplayConfigVideoOutputTechnology.UDI_EMBEDDED => "UDI (embedded)",
        DisplayConfigVideoOutputTechnology.MIRACAST => "Miracast",
        DisplayConfigVideoOutputTechnology.INDIRECT_WIRED => "Indirect (wired)",
        DisplayConfigVideoOutputTechnology.INDIRECT_VIRTUAL => "Indirect (virtual)",
        DisplayConfigVideoOutputTechnology.INTERNAL => "Internal panel",
        DisplayConfigVideoOutputTechnology.OTHER => "Other",
        _ => technology.ToString(),
    };

    private static string DescribeRotation(DisplayConfigRotation rotation) => rotation switch
    {
        DisplayConfigRotation.IDENTITY => "0°",
        DisplayConfigRotation.ROTATE90 => "90°",
        DisplayConfigRotation.ROTATE180 => "180°",
        DisplayConfigRotation.ROTATE270 => "270°",
        _ => rotation.ToString(),
    };
}
