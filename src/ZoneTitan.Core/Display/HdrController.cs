using System.Runtime.InteropServices;
using ZoneTitan.Core.Diagnostics;
using static ZoneTitan.Core.Native.CcdNative;

namespace ZoneTitan.Core.Display;

/// <summary>
/// Sets per-monitor HDR. Topology changes routinely reset advanced colour, so the zone
/// engine re-asserts desired HDR after every successful apply (spec §5).
/// </summary>
internal static class HdrController
{
    /// <summary>
    /// Applies the desired HDR state to a target, preferring the Windows 11 24H2 path and
    /// falling back to the legacy advanced-colour toggle. Returns false when neither works.
    /// </summary>
    public static bool TrySetHdr(LUID adapterId, uint targetId, bool enable, out string detail)
    {
        var modern = new DISPLAYCONFIG_SET_HDR_STATE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.SET_HDR_STATE,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_HDR_STATE>(),
                adapterId = adapterId,
                id = targetId,
            },
            value = enable ? 1u : 0u,
        };

        var modernResult = DisplayConfigSetDeviceInfo(ref modern);
        if (modernResult == ERROR_SUCCESS)
        {
            detail = "SET_HDR_STATE";
            return true;
        }

        var legacy = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.SET_ADVANCED_COLOR_STATE,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                adapterId = adapterId,
                id = targetId,
            },
            value = enable ? 1u : 0u,
        };

        var legacyResult = DisplayConfigSetDeviceInfo(ref legacy);
        if (legacyResult == ERROR_SUCCESS)
        {
            detail = "SET_ADVANCED_COLOR_STATE (legacy)";
            return true;
        }

        detail = $"SET_HDR_STATE {DescribeError(modernResult)}; " +
                 $"SET_ADVANCED_COLOR_STATE {DescribeError(legacyResult)}";
        Log.Warn($"HDR set failed for target {targetId}: {detail}");
        return false;
    }
}
