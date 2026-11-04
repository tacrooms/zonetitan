// -----------------------------------------------------------------------------
// Portions of this file are ported and adapted from DisplayProfileManager
// (https://github.com/zac15987/DisplayProfileManager), file
// src/Helpers/DisplayConfigHelper.cs.
//
//     MIT License
//     Copyright (c) 2025 zac15987
//
// The full MIT license text is reproduced in THIRD-PARTY-LICENSES.md at the
// repository root.
//
// Modified for ZoneTitan:
//   - Structs made blittable (Win32 BOOL marshalled as uint) so path/mode arrays
//     marshal as fast, layout-exact blocks.
//   - Added GET_TARGET_NAME capture of monitorDevicePath, GET_ADAPTER_NAME,
//     GET_TARGET_PREFERRED_MODE, and the Windows 11 24H2 advanced-colour
//     structs (GET_ADVANCED_COLOR_INFO_2 / SET_HDR_STATE).
//   - Split pure interop from policy; all enumeration logic lives elsewhere.
// -----------------------------------------------------------------------------

using System.Runtime.InteropServices;

namespace ZoneTitan.Core.Native;

/// <summary>
/// Raw P/Invoke surface for the Windows Connected/Configuration Display (CCD) API.
/// This type is deliberately policy-free: it marshals, it does not decide.
/// </summary>
internal static class CcdNative
{
    #region Error codes

    public const int ERROR_SUCCESS = 0;
    public const int ERROR_INVALID_PARAMETER = 87;
    public const int ERROR_NOT_SUPPORTED = 50;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_GEN_FAILURE = 31;
    public const int ERROR_INSUFFICIENT_BUFFER = 122;

    /// <summary>Sentinel meaning "no mode index"; let Windows pick a mode.</summary>
    public const uint DISPLAYCONFIG_PATH_MODE_IDX_INVALID = 0xFFFFFFFF;

    #endregion

    #region Entry points

    [DllImport("user32.dll")]
    public static extern int GetDisplayConfigBufferSizes(
        QueryDisplayConfigFlags flags,
        out uint numPathArrayElements,
        out uint numModeInfoArrayElements);

    [DllImport("user32.dll")]
    public static extern int QueryDisplayConfig(
        QueryDisplayConfigFlags flags,
        ref uint numPathArrayElements,
        [Out] DISPLAYCONFIG_PATH_INFO[] pathArray,
        ref uint numModeInfoArrayElements,
        [Out] DISPLAYCONFIG_MODE_INFO[] modeInfoArray,
        nint currentTopologyId);

    [DllImport("user32.dll")]
    public static extern int SetDisplayConfig(
        uint numPathArrayElements,
        [In] DISPLAYCONFIG_PATH_INFO[]? pathArray,
        uint numModeInfoArrayElements,
        [In] DISPLAYCONFIG_MODE_INFO[]? modeInfoArray,
        SetDisplayConfigFlags flags);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_DEVICE_NAME request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_ADAPTER_NAME request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_TARGET_PREFERRED_MODE request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2 request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE request);

    [DllImport("user32.dll")]
    public static extern int DisplayConfigSetDeviceInfo(ref DISPLAYCONFIG_SET_HDR_STATE request);

    #endregion

    #region Enums

    [Flags]
    public enum QueryDisplayConfigFlags : uint
    {
        QDC_ALL_PATHS = 0x00000001,
        QDC_ONLY_ACTIVE_PATHS = 0x00000002,
        QDC_DATABASE_CURRENT = 0x00000004,
        QDC_VIRTUAL_MODE_AWARE = 0x00000010,
        QDC_INCLUDE_HMD = 0x00000020,
    }

    [Flags]
    public enum SetDisplayConfigFlags : uint
    {
        SDC_TOPOLOGY_INTERNAL = 0x00000001,
        SDC_TOPOLOGY_CLONE = 0x00000002,
        SDC_TOPOLOGY_EXTEND = 0x00000004,
        SDC_TOPOLOGY_EXTERNAL = 0x00000008,
        SDC_USE_DATABASE_CURRENT = 0x00000010,
        SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x00000020,
        SDC_VALIDATE = 0x00000040,
        SDC_APPLY = 0x00000080,
        SDC_NO_OPTIMIZATION = 0x00000100,
        SDC_SAVE_TO_DATABASE = 0x00000200,
        SDC_ALLOW_CHANGES = 0x00000400,
        SDC_PATH_PERSIST_IF_REQUIRED = 0x00000800,
        SDC_FORCE_MODE_ENUMERATION = 0x00001000,
        SDC_ALLOW_PATH_ORDER_CHANGES = 0x00002000,
        SDC_VIRTUAL_MODE_AWARE = 0x00008000,
    }

    [Flags]
    public enum DisplayConfigPathFlags : uint
    {
        DISPLAYCONFIG_PATH_ACTIVE = 0x00000001,
        DISPLAYCONFIG_PATH_PREFERRED_UNSCALED = 0x00000004,
        DISPLAYCONFIG_PATH_SUPPORT_VIRTUAL_MODE = 0x00000008,
    }

    public enum DisplayConfigDeviceInfoType : uint
    {
        GET_SOURCE_NAME = 1,
        GET_TARGET_NAME = 2,
        GET_TARGET_PREFERRED_MODE = 3,
        GET_ADAPTER_NAME = 4,
        SET_TARGET_PERSISTENCE = 5,
        GET_TARGET_BASE_TYPE = 6,
        GET_SUPPORT_VIRTUAL_RESOLUTION = 7,
        SET_SUPPORT_VIRTUAL_RESOLUTION = 8,
        GET_ADVANCED_COLOR_INFO = 9,
        SET_ADVANCED_COLOR_STATE = 10,
        GET_SDR_WHITE_LEVEL = 11,

        // wingdi.h stops assigning explicit values after GET_SDR_WHITE_LEVEL = 11, so the
        // remainder are implicit. Getting these wrong is quiet but nasty: 13 is
        // SET_MONITOR_SPECIALIZATION and 14 is SET_RESERVED1, so a mis-numbered
        // SET_HDR_STATE would write to a reserved device-info channel.
        GET_MONITOR_SPECIALIZATION = 12,
        SET_MONITOR_SPECIALIZATION = 13,
        SET_RESERVED1 = 14,
        GET_ADVANCED_COLOR_INFO_2 = 15,
        SET_HDR_STATE = 16,
        SET_WCG_STATE = 17,
    }

    public enum DisplayConfigModeInfoType : uint
    {
        SOURCE = 1,
        TARGET = 2,
        DESKTOP_IMAGE = 3,
    }

    public enum DisplayConfigVideoOutputTechnology : uint
    {
        HD15 = 0,
        SVIDEO = 1,
        COMPOSITE_VIDEO = 2,
        COMPONENT_VIDEO = 3,
        DVI = 4,
        HDMI = 5,
        LVDS = 6,
        D_JPN = 8,
        SDI = 9,
        DISPLAYPORT_EXTERNAL = 10,
        DISPLAYPORT_EMBEDDED = 11,
        UDI_EXTERNAL = 12,
        UDI_EMBEDDED = 13,
        SDTVDONGLE = 14,
        MIRACAST = 15,
        INDIRECT_WIRED = 16,
        INDIRECT_VIRTUAL = 17,
        DISPLAYPORT_USB_TUNNEL = 18,
        INTERNAL = 0x80000000,
        OTHER = 0xFFFFFFFF,
    }

    public enum DisplayConfigRotation : uint
    {
        IDENTITY = 1,
        ROTATE90 = 2,
        ROTATE180 = 3,
        ROTATE270 = 4,
    }

    public enum DisplayConfigScaling : uint
    {
        IDENTITY = 1,
        CENTERED = 2,
        STRETCHED = 3,
        ASPECTRATIOCENTEREDMAX = 4,
        CUSTOM = 5,
        PREFERRED = 128,
    }

    public enum DisplayConfigScanLineOrdering : uint
    {
        UNSPECIFIED = 0,
        PROGRESSIVE = 1,
        INTERLACED = 2,
        INTERLACED_UPPERFIELDFIRST = 2,
        INTERLACED_LOWERFIELDFIRST = 3,
    }

    public enum DisplayConfigPixelFormat : uint
    {
        PIXELFORMAT_8BPP = 1,
        PIXELFORMAT_16BPP = 2,
        PIXELFORMAT_24BPP = 3,
        PIXELFORMAT_32BPP = 4,
        PIXELFORMAT_NONGDI = 5,
    }

    public enum DisplayConfigColorEncoding : uint
    {
        RGB = 0,
        YCBCR444 = 1,
        YCBCR422 = 2,
        YCBCR420 = 3,
        INTENSITY = 4,
    }

    /// <summary>Windows 11 24H2+ advanced colour mode (GET_ADVANCED_COLOR_INFO_2).</summary>
    public enum DisplayConfigAdvancedColorMode : uint
    {
        SDR = 0,
        WCG = 1,
        HDR = 2,
    }

    [Flags]
    public enum AdvancedColorInfoFlags : uint
    {
        AdvancedColorSupported = 0x1,
        AdvancedColorEnabled = 0x2,
        WideColorEnforced = 0x4,
        AdvancedColorForceDisabled = 0x8,
    }

    #endregion

    #region Structures

    [StructLayout(LayoutKind.Sequential)]
    public struct LUID : IEquatable<LUID>
    {
        public uint LowPart;
        public int HighPart;

        public bool Equals(LUID other) => LowPart == other.LowPart && HighPart == other.HighPart;
        public override bool Equals(object? obj) => obj is LUID other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(LowPart, HighPart);
        public override string ToString() => $"{HighPart:X8}:{LowPart:X8}";
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_RATIONAL
    {
        public uint Numerator;
        public uint Denominator;

        public double ToHz() => Denominator == 0 ? 0d : (double)Numerator / Denominator;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECTL
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_2DREGION
    {
        public uint cx;
        public uint cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_SOURCE_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId;
        public uint id;
        public uint modeInfoIdx;
        public DisplayConfigVideoOutputTechnology outputTechnology;
        public DisplayConfigRotation rotation;
        public DisplayConfigScaling scaling;
        public DISPLAYCONFIG_RATIONAL refreshRate;
        public DisplayConfigScanLineOrdering scanLineOrdering;

        /// <summary>
        /// Win32 BOOL (4 bytes). Held as uint so the struct stays blittable — a managed
        /// <c>bool</c> here would make the whole path array non-blittable and force a
        /// slow element-by-element marshal.
        /// </summary>
        public uint targetAvailable;

        public uint statusFlags;

        public readonly bool IsTargetAvailable => targetAvailable != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_PATH_INFO
    {
        public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
        public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
        public uint flags;

        public readonly bool IsActive =>
            (flags & (uint)DisplayConfigPathFlags.DISPLAYCONFIG_PATH_ACTIVE) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_VIDEO_SIGNAL_INFO
    {
        public ulong pixelRate;
        public DISPLAYCONFIG_RATIONAL hSyncFreq;
        public DISPLAYCONFIG_RATIONAL vSyncFreq;
        public DISPLAYCONFIG_2DREGION activeSize;
        public DISPLAYCONFIG_2DREGION totalSize;
        public uint videoStandard;
        public DisplayConfigScanLineOrdering scanLineOrdering;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SOURCE_MODE
    {
        public uint width;
        public uint height;
        public DisplayConfigPixelFormat pixelFormat;
        public POINTL position;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_MODE
    {
        public DISPLAYCONFIG_VIDEO_SIGNAL_INFO targetVideoSignalInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DESKTOP_IMAGE_INFO
    {
        public POINTL PathSourceSize;
        public RECTL DesktopImageRegion;
        public RECTL DesktopImageClip;
    }

    [StructLayout(LayoutKind.Explicit)]
    public struct DISPLAYCONFIG_MODE_INFO_UNION
    {
        [FieldOffset(0)] public DISPLAYCONFIG_TARGET_MODE targetMode;
        [FieldOffset(0)] public DISPLAYCONFIG_SOURCE_MODE sourceMode;
        [FieldOffset(0)] public DISPLAYCONFIG_DESKTOP_IMAGE_INFO desktopImageInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_MODE_INFO
    {
        public DisplayConfigModeInfoType infoType;
        public uint id;
        public LUID adapterId;
        public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_DEVICE_INFO_HEADER
    {
        public DisplayConfigDeviceInfoType type;
        public uint size;
        public LUID adapterId;
        public uint id;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        /// <summary>GDI name such as <c>\\.\DISPLAY1</c>. Unstable — never use as identity.</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string viewGdiDeviceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_TARGET_DEVICE_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint flags;
        public DisplayConfigVideoOutputTechnology outputTechnology;
        public ushort edidManufactureId;
        public ushort edidProductCodeId;
        public uint connectorInstance;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string monitorFriendlyDeviceName;

        /// <summary>
        /// Stable per-monitor device path, e.g. <c>\\?\DISPLAY#DELA1FB#5&amp;...</c>.
        /// This is ZoneTitan's monitor identity (spec §4) — never the adapter LUID.
        /// </summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string monitorDevicePath;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct DISPLAYCONFIG_ADAPTER_NAME
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string adapterDevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_TARGET_PREFERRED_MODE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public uint width;
        public uint height;
        public DISPLAYCONFIG_TARGET_MODE targetMode;
    }

    /// <summary>Legacy advanced-colour query (pre-24H2).</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
        public AdvancedColorInfoFlags values;
        public DisplayConfigColorEncoding colorEncoding;
        public int bitsPerColorChannel;
    }

    /// <summary>Windows 11 24H2+ advanced-colour query. Preferred; falls back to the legacy struct.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        /// <summary>
        /// Bitfield: bit0 advancedColorSupported, bit1 advancedColorActive, bit2 reserved,
        /// bit3 advancedColorLimitedByPolicy, bit4 highDynamicRangeSupported,
        /// bit5 highDynamicRangeUserEnabled, bit6 wideColorSupported, bit7 wideColorUserEnabled.
        /// </summary>
        public uint value;

        public DisplayConfigColorEncoding colorEncoding;
        public uint bitsPerColorChannel;
        public DisplayConfigAdvancedColorMode activeColorMode;

        public readonly bool AdvancedColorSupported => (value & 0x1) != 0;
        public readonly bool AdvancedColorActive => (value & 0x2) != 0;
        public readonly bool LimitedByPolicy => (value & 0x8) != 0;
        public readonly bool HdrSupported => (value & 0x10) != 0;
        public readonly bool HdrUserEnabled => (value & 0x20) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        /// <summary>Bit 0 = enableAdvancedColor.</summary>
        public uint value;
    }

    /// <summary>Windows 11 24H2+ HDR toggle.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct DISPLAYCONFIG_SET_HDR_STATE
    {
        public DISPLAYCONFIG_DEVICE_INFO_HEADER header;

        /// <summary>Bit 0 = enableHdr.</summary>
        public uint value;
    }

    #endregion

    /// <summary>Maps a Win32 error code from the CCD entry points to a readable string.</summary>
    public static string DescribeError(int code) => code switch
    {
        ERROR_SUCCESS => "ERROR_SUCCESS",
        ERROR_ACCESS_DENIED => "ERROR_ACCESS_DENIED (5)",
        ERROR_GEN_FAILURE => "ERROR_GEN_FAILURE (31) — device not functioning",
        ERROR_NOT_SUPPORTED => "ERROR_NOT_SUPPORTED (50)",
        ERROR_INVALID_PARAMETER => "ERROR_INVALID_PARAMETER (87)",
        ERROR_INSUFFICIENT_BUFFER => "ERROR_INSUFFICIENT_BUFFER (122)",
        _ => $"Win32 error {code}",
    };
}
