using System.Runtime.InteropServices;

namespace ZoneTitan.Core.Display;

/// <summary>
/// Lists the display modes Windows will offer for an attached monitor.
/// <para>
/// Uses the legacy <c>EnumDisplaySettingsEx</c> because CCD has no mode-list query — it
/// reports the current and preferred mode only. This is diagnostic code: it answers
/// "can this panel actually do 240 Hz on this cable", which CCD cannot.
/// </para>
/// </summary>
public static class ModeEnumerator
{
    private const int ENUM_CURRENT_SETTINGS = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettingsEx(
        string? lpszDeviceName, int iModeNum, ref DEVMODE lpDevMode, uint dwFlags);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    public sealed record AvailableMode(uint Width, uint Height, uint RefreshHz, uint BitsPerPixel);

    /// <summary>All distinct modes offered for a GDI device name such as <c>\\.\DISPLAY1</c>.</summary>
    public static IReadOnlyList<AvailableMode> Enumerate(string gdiDeviceName)
    {
        var modes = new HashSet<AvailableMode>();
        var devMode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

        for (var i = 0; EnumDisplaySettingsEx(gdiDeviceName, i, ref devMode, 0); i++)
        {
            modes.Add(new AvailableMode(
                devMode.dmPelsWidth, devMode.dmPelsHeight, devMode.dmDisplayFrequency, devMode.dmBitsPerPel));

            devMode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };
        }

        return modes
            .OrderByDescending(m => (long)m.Width * m.Height)
            .ThenByDescending(m => m.RefreshHz)
            .ToList();
    }

    /// <summary>The mode currently in force, per GDI.</summary>
    public static AvailableMode? Current(string gdiDeviceName)
    {
        var devMode = new DEVMODE { dmSize = (ushort)Marshal.SizeOf<DEVMODE>() };

        return EnumDisplaySettingsEx(gdiDeviceName, ENUM_CURRENT_SETTINGS, ref devMode, 0)
            ? new AvailableMode(devMode.dmPelsWidth, devMode.dmPelsHeight,
                devMode.dmDisplayFrequency, devMode.dmBitsPerPel)
            : null;
    }
}
